using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class ReleaseDataSafetyAcceptanceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-release-data-safety", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { try { Directory.Delete(_root, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public async Task DS01_empty_first_run_database_reaches_target_schema_with_integrity_and_no_sample_state()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "first-run.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        Assert.Equal(SchemaCompatibility.CURRENT, await schema.MigrateAsync());
        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(DatabaseIntegrityState.INTEGRITY_OK, (await new SqliteIntegrityService().CheckAsync(store.DatabasePath)).State);
        Assert.Equal(0, await ScalarLongAsync(store.DatabasePath, "SELECT COUNT(*) FROM state_records;"));
        Assert.Equal(0, await ScalarLongAsync(store.DatabasePath, "SELECT COUNT(*) FROM checkpoints;"));
    }

    [Fact]
    public void DS01_governed_default_database_path_is_per_user_product_state()
    {
        var path = Path.GetFullPath(SqliteStateStore.DefaultDatabasePath);
        var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.True(path.StartsWith(local, StringComparison.OrdinalIgnoreCase));
        Assert.True(path.Contains($"{Path.DirectorySeparatorChar}PCC Executive{Path.DirectorySeparatorChar}state{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        Assert.False(path.Contains("browser-profiles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DS02_clean_restart_reconstructs_representative_state()
    {
        var path = Path.Combine(_root, "clean-restart.db");
        SeededState seeded;
        await using (var store = await DurabilityTestFixture.NewStoreAsync(_root, "clean-restart.db"))
        {
            seeded = await SeedRepresentativeAsync(store);
            var orchestration = new CrashConsistentOrchestrationStore(store);
            var recovery = new DurableStartupRecoveryService(store, orchestration);
            var shutdown = new SafeShutdownCoordinator(new FakeNewSendGate(), new RecoveryCheckpointService(store), recovery, orchestration, store);
            await shutdown.ShutdownAsync(seeded.Snapshot, "0.1.0");
        }

        await using var reopened = new SqliteStateStore(path);
        await reopened.InitializeAsync();
        var durable = new CrashConsistentOrchestrationStore(reopened);
        var startup = new DurableStartupRecoveryService(reopened, durable);
        Assert.Equal(RecoveryStartupKind.CLEAN_SHUTDOWN, await startup.BeginStartupAsync(seeded.Snapshot.ProjectRun.Id));

        var full = await new FullDurabilityRecoveryService(reopened, durable).ReconstructAsync(seeded.Snapshot.ProjectRun.Id);
        Assert.NotNull(full);
        Assert.Equal(seeded.Snapshot.ProjectRun.Id, full!.Orchestration.ProjectRun.Id);
        Assert.Equal(6, full.LogicalSessions.Count);
        Assert.Equal(6, full.Conversations.Count);
        Assert.Equal(seeded.Attention.Id, (await reopened.LoadAttentionAsync(seeded.Attention.Id))!.Id);
        Assert.Equal("BrowserChat", (await reopened.LoadSettingsAsync()).Provider);
        Assert.NotNull(await new RecoveryCheckpointService(reopened).LoadAsync(seeded.Checkpoint.CheckpointId));
        Assert.Equal(1, await ScalarLongAsync(path, "SELECT COUNT(*) FROM state_records WHERE kind='decision-journal';"));
    }

    [Fact]
    public async Task DS03_unclean_restart_is_detected_and_state_remains_recoverable()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "unclean.db");
        var seeded = await SeedRepresentativeAsync(store);
        var uncertain = seeded.Snapshot.Dispatches[2];
        await store.ReserveAsync(uncertain.Id.ToString(), uncertain.ContentHash);
        await store.UpdateAsync(uncertain.Id.ToString(), PCCExecutive.Browser.DispatchState.SubmittedUnknown, "uncertain-send");

        var orchestration = new CrashConsistentOrchestrationStore(store);
        var startup = new DurableStartupRecoveryService(store, orchestration);
        Assert.Equal(RecoveryStartupKind.INTERRUPTED_DISPATCH, await startup.BeginStartupAsync(seeded.Snapshot.ProjectRun.Id));
        var restored = await startup.ReconstructAsync(seeded.Snapshot.ProjectRun.Id);
        Assert.NotNull(restored);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, restored!.Dispatches[2].State);
        Assert.Equal(DispatchReservationStatus.DuplicateBlocked, (await store.ReserveAsync(uncertain.Id.ToString(), uncertain.ContentHash)).Status);

        var journal = new RecoveryJournalService(store.DatabasePath);
        await journal.RecordAsync(RecoveryJournalKind.UNCLEAN_SHUTDOWN, "release-acceptance", seeded.Snapshot.ProjectRun.Id);
        await journal.RecordAsync(RecoveryJournalKind.UNCERTAIN_DISPATCH_RECOVERED, uncertain.Id.ToString(), seeded.Snapshot.ProjectRun.Id);
        var entries = await journal.ListAsync();
        Assert.Contains(RecoveryJournalKind.UNCLEAN_SHUTDOWN, entries);
        Assert.Contains(RecoveryJournalKind.UNCERTAIN_DISPATCH_RECOVERED, entries);
    }

    [Fact]
    public async Task DS04_five_worker_restart_preserves_mixed_worker_states_and_identity()
    {
        var path = Path.Combine(_root, "five-worker.db");
        SeededState seeded;
        await using (var store = await DurabilityTestFixture.NewStoreAsync(_root, "five-worker.db"))
            seeded = await SeedRepresentativeAsync(store);

        await using var reopened = new SqliteStateStore(path);
        await reopened.InitializeAsync();
        var full = await new FullDurabilityRecoveryService(reopened, new CrashConsistentOrchestrationStore(reopened))
            .ReconstructAsync(seeded.Snapshot.ProjectRun.Id);

        Assert.NotNull(full);
        Assert.Equal(TaskState.Completed, full!.Orchestration.Tasks[0].State);
        Assert.Equal(TaskState.Running, full.Orchestration.Tasks[1].State);
        Assert.Equal(TaskState.Dispatched, full.Orchestration.Tasks[2].State);
        Assert.Equal(TaskState.Blocked, full.Orchestration.Tasks[3].State);
        Assert.Equal(TaskState.Assigned, full.Orchestration.Tasks[4].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.COMPLETED, full.Orchestration.Dispatches[0].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.GENERATING, full.Orchestration.Dispatches[1].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, full.Orchestration.Dispatches[2].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.FAILED, full.Orchestration.Dispatches[3].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.PREPARED, full.Orchestration.Dispatches[4].State);
        for (var i = 0; i < 5; i++) Assert.Equal(i + 1, full.Orchestration.Assignments[full.Orchestration.Tasks[i].Id].Value);
    }

    [Fact]
    public async Task DS05_submitted_unknown_identity_hash_and_conversation_survive_reopen()
    {
        var path = Path.Combine(_root, "submitted-unknown.db");
        SeededState seeded;
        await using (var store = await DurabilityTestFixture.NewStoreAsync(_root, "submitted-unknown.db"))
            seeded = await SeedRepresentativeAsync(store);

        await using var reopened = new SqliteStateStore(path);
        await reopened.InitializeAsync();
        var restored = await new CrashConsistentOrchestrationStore(reopened).LoadAsync(seeded.Snapshot.ProjectRun.Id);
        Assert.NotNull(restored);
        var expected = seeded.Snapshot.Dispatches[2];
        var actual = restored!.Dispatches.Single(x => x.Id == expected.Id);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.ConversationId, actual.ConversationId);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, actual.State);
    }

    [Fact]
    public async Task DS06_reinstall_same_data_root_preserves_database_id_and_settings()
    {
        var path = Path.Combine(_root, "reinstall.db");
        string id;
        await using (var store = await DurabilityTestFixture.NewStoreAsync(_root, "reinstall.db"))
        {
            var schema = new DurabilitySchemaManager(store.DatabasePath);
            await schema.MigrateAsync();
            id = await schema.GetDatabaseIdAsync();
            await store.SaveSettingsAsync(new PccExecutiveSettings(BaseDispatchIntervalSeconds: 17, AutoResume: false));
        }

        await using var reinstalled = new SqliteStateStore(path);
        await reinstalled.InitializeAsync();
        var reinstalledSchema = new DurabilitySchemaManager(path);
        Assert.Equal(SchemaCompatibility.CURRENT, await reinstalledSchema.ClassifyAsync());
        Assert.Equal(id, await reinstalledSchema.GetDatabaseIdAsync());
        var settings = await reinstalled.LoadSettingsAsync();
        Assert.Equal(17, settings.BaseDispatchIntervalSeconds);
        Assert.False(settings.AutoResume);
    }

    [Fact]
    public async Task DS07_v1_to_v2_migration_preserves_preexisting_canonical_state()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "v1-v2.db");
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerPlanning, DateTimeOffset.UtcNow, new ManagerEstimate(33), new VerifiedCompletion(12), ProjectCompletionMode.Active);
        var attention = new AttentionRequest(AttentionRequestId.New(), run.Id, AttentionState.Open, "LOGIN", "login", "sign in", "runtime", false, DateTimeOffset.UtcNow);
        await store.SaveProjectRunAsync(run);
        await store.SaveAttentionAsync(attention);
        await store.SaveSettingsAsync(new PccExecutiveSettings(BaseDispatchIntervalSeconds: 12));
        await InsertStateRecordAsync(store.DatabasePath, "decision-journal", "decision-1", run.Id.ToString(), "{\"decision\":\"continue\"}");

        Assert.Equal(1, await store.GetSchemaVersionAsync());
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        Assert.Equal(SchemaCompatibility.CURRENT, await schema.MigrateAsync());
        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(run, await store.LoadProjectRunAsync(run.Id));
        Assert.Equal(attention, await store.LoadAttentionAsync(attention.Id));
        Assert.Equal(12, (await store.LoadSettingsAsync()).BaseDispatchIntervalSeconds);
        Assert.Equal(1, await ScalarLongAsync(store.DatabasePath, "SELECT COUNT(*) FROM state_records WHERE kind='decision-journal';"));
        Assert.Equal(MigrationRunStatus.APPLIED, (await schema.GetMigrationAsync("durability-v2"))!.Status);
    }

    [Fact]
    public async Task DS08_interrupted_migration_keeps_v1_source_and_verified_backup()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "migration-interrupted.db");
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerPlanning, DateTimeOffset.UtcNow, new ManagerEstimate(10), new VerifiedCompletion(5), ProjectCompletionMode.Active);
        await store.SaveProjectRunAsync(run);
        var backupDir = Path.Combine(_root, "migration-interrupted-backups");
        var backup = await new VerifiedBackupService(store).CreateAsync(backupDir, "0.1.0", "PRE_MIGRATION");
        Assert.True(backup.IsVerified);
        Assert.Equal(1, await store.GetSchemaVersionAsync());

        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await Assert.ThrowsAsync<InjectedCrashException>(() => schema.MigrateAsync(new DeterministicCrashFaultInjector(CrashFaultPoint.BEFORE_COMMIT)));
        Assert.Equal(1, await store.GetSchemaVersionAsync());
        Assert.Equal(MigrationRunStatus.ROLLED_BACK, (await schema.GetMigrationAsync("durability-v2"))!.Status);
        Assert.True((await new VerifiedBackupService(store).VerifyAsync(backup.ManifestPath)).IsVerified);
        Assert.Equal(run, await store.LoadProjectRunAsync(run.Id));
    }

    [Fact]
    public async Task DS09_pre_update_backup_is_verified_and_contains_canonical_state()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "pre-update.db");
        var seeded = await SeedRepresentativeAsync(store);
        var backup = await new VerifiedBackupService(store).CreateAsync(Path.Combine(_root, "pre-update-backups"), "0.1.0", "PRE_UPDATE", "source-sha");
        Assert.True(backup.IsVerified);
        Assert.Equal(await new DurabilitySchemaManager(store.DatabasePath).GetDatabaseIdAsync(), backup.Manifest.SourceDatabaseId);
        Assert.Equal(1, await ScalarLongAsync(backup.Manifest.FilePath, $"SELECT COUNT(*) FROM state_records WHERE kind='project-run' AND id='{seeded.Snapshot.ProjectRun.Id}';"));
    }

    [Fact]
    public async Task DS10_failed_update_restore_returns_previous_state_and_preserves_failed_database_copy()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "rollback.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await schema.MigrateAsync();
        await store.SaveSettingsAsync(new PccExecutiveSettings(BaseDispatchIntervalSeconds: 10));
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "rollback-backups"), "0.1.0", "PRE_UPDATE");
        await store.SaveSettingsAsync(new PccExecutiveSettings(BaseDispatchIntervalSeconds: 99));

        var preserved = await service.RestoreAsync(backup, false);
        Assert.True(File.Exists(preserved));
        Assert.Equal(10, (await store.LoadSettingsAsync()).BaseDispatchIntervalSeconds);
        Assert.Equal(DatabaseIntegrityState.INTEGRITY_OK, (await new SqliteIntegrityService().CheckAsync(store.DatabasePath)).State);
    }

    [Fact]
    public async Task DS11_verified_compatible_backup_restores_same_database_identity()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "restore-ok.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await schema.MigrateAsync();
        var id = await schema.GetDatabaseIdAsync();
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "restore-ok-backups"), "0.1.0", "TEST");
        await store.SaveSettingsAsync(new PccExecutiveSettings(BaseDispatchIntervalSeconds: 44));
        await service.RestoreAsync(backup, false);
        Assert.Equal(id, await schema.GetDatabaseIdAsync());
        Assert.Equal(10, (await store.LoadSettingsAsync()).BaseDispatchIntervalSeconds);
    }

    [Fact]
    public async Task DS12_corrupted_sqlite_backup_is_rejected()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "corrupted-backup.db");
        await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "corrupted-backups"), "0.1.0", "TEST");
        await File.WriteAllBytesAsync(backup.Manifest.FilePath, Enumerable.Repeat((byte)0x5a, 2048).ToArray());
        var invalid = await service.VerifyAsync(backup.ManifestPath);
        Assert.False(invalid.IsVerified);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(invalid, false));
    }

    [Fact]
    public async Task DS13_backup_hash_mismatch_is_rejected()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "hash-mismatch.db");
        await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "hash-mismatch-backups"), "0.1.0", "TEST");
        await File.AppendAllTextAsync(backup.Manifest.FilePath, "tamper");
        var invalid = await service.VerifyAsync(backup.ManifestPath);
        Assert.False(invalid.IsVerified);
        Assert.Contains("backup-hash:mismatch", invalid.Findings);
    }

    [Fact]
    public async Task DS14_newer_database_than_application_is_not_downgraded_or_restored()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "newer-source.db");
        await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "newer-source-backups"), "0.1.0", "TEST");

        await using (var connection = new SqliteConnection(SqliteDurabilityConnection.ConnectionString(backup.Manifest.FilePath)))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES(99,$at);";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await RewriteManifestHashAndSchemaAsync(backup.ManifestPath, backup.Manifest.FilePath, 99);
        var newer = await service.VerifyAsync(backup.ManifestPath);
        Assert.False(newer.IsVerified);
        Assert.Contains("schema-version:newer-than-application", newer.Findings);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(newer, false));
        Assert.Equal(2, await store.GetSchemaVersionAsync());
    }

    [Fact]
    public void DS15_uninstall_policy_preserves_user_data_by_default_and_requires_explicit_full_cleanup()
    {
        var repo = FindRepoRoot();
        var installer = File.ReadAllText(Path.Combine(repo, "installer", "PCCExecutive.iss"));
        Assert.True(installer.Contains("Preserving durable PCC Executive user data.", StringComparison.Ordinal));
        Assert.True(installer.Contains("/FULLCLEANUP=1", StringComparison.Ordinal));
        Assert.True(installer.Contains("FULL CLEANUP explicitly selected", StringComparison.Ordinal));
        Assert.True(installer.Contains("Choose Yes to KEEP DATA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DS16_reinstall_rediscovers_preserved_project_history()
    {
        var path = Path.Combine(_root, "reinstall-history.db");
        ProjectRun run;
        AttentionRequest attention;
        await using (var store = await DurabilityTestFixture.NewStoreAsync(_root, "reinstall-history.db"))
        {
            await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();
            run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerReview, DateTimeOffset.UtcNow, new ManagerEstimate(90), new VerifiedCompletion(80), ProjectCompletionMode.Active);
            attention = new AttentionRequest(AttentionRequestId.New(), run.Id, AttentionState.Open, "EXTERNAL_BLOCKER", "blocked", "resolve", null, false, DateTimeOffset.UtcNow);
            await store.SaveProjectRunAsync(run);
            await store.SaveAttentionAsync(attention);
        }

        await using var reinstalled = new SqliteStateStore(path);
        await reinstalled.InitializeAsync();
        Assert.Equal(run, await reinstalled.LoadProjectRunAsync(run.Id));
        Assert.Equal(attention, await reinstalled.LoadAttentionAsync(attention.Id));
    }

    [Fact]
    public void DS17_package_contamination_gate_covers_database_wal_shm_and_browser_profile_material()
    {
        var repo = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(repo, "tests", "data-safety", "Write-DataSafetyGate.ps1"));
        Assert.True(script.Contains("EndsWith('.db')", StringComparison.Ordinal));
        Assert.True(script.Contains("EndsWith('-wal')", StringComparison.Ordinal));
        Assert.True(script.Contains("EndsWith('-shm')", StringComparison.Ordinal));
        Assert.True(script.Contains("browser-profiles", StringComparison.Ordinal));
        Assert.True(script.Contains("Backups", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DS18_online_backup_preserves_committed_data_while_wal_is_present()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "wal-backup.db");
        await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();

        await using var writer = await SqliteDurabilityConnection.OpenAsync(store.DatabasePath, new SqliteDurabilityPolicy(WalAutoCheckpointPages: 0));
        await SqliteDurabilityConnection.ExecuteAsync(writer, "PRAGMA wal_autocheckpoint=0;");
        await using (var command = writer.CreateCommand())
        {
            command.CommandText = "INSERT INTO state_records(kind,id,project_run_id,payload,updated_at) VALUES('wal-proof','proof',NULL,'committed-in-wal',$at);";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(File.Exists(store.DatabasePath + "-wal"));
        var backup = await new VerifiedBackupService(store).CreateAsync(Path.Combine(_root, "wal-backups"), "0.1.0", "WAL_PROOF");
        Assert.True(backup.IsVerified);
        Assert.Equal(1, await ScalarLongAsync(backup.Manifest.FilePath, "SELECT COUNT(*) FROM state_records WHERE kind='wal-proof' AND payload='committed-in-wal';"));
    }

    [Fact]
    public async Task DS19_active_database_online_backup_is_safe_and_restore_remains_blocked_while_leased()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "active-backup.db");
        await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();
        await using var active = await SqliteDurabilityConnection.OpenAsync(store.DatabasePath, new SqliteDurabilityPolicy());
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "active-backups"), "0.1.0", "ACTIVE_DB");
        Assert.True(backup.IsVerified);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backup, true));
    }

    [Fact]
    public async Task DS20_conversation_lineage_remains_archived_predecessor_active_successor()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "lineage.db");
        var runId = ProjectRunId.New();
        var agentId = LogicalAgentId.New();
        var oldId = ConversationId.New();
        var activeId = ConversationId.New();
        var checkpoint = CheckpointId.New();

        var old = new PCCExecutive.Domain.Conversation(oldId, agentId, 1, AgentProviderKind.BrowserChat, "old", "old-url", ConversationState.Archived, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-1), null, activeId, 1, 10, checkpoint, "rollover");
        var active = new PCCExecutive.Domain.Conversation(activeId, agentId, 2, AgentProviderKind.BrowserChat, "new", "new-url", ConversationState.Active, DateTimeOffset.UtcNow, null, oldId, null, 1, 1, checkpoint, "rollover");
        await store.SaveConversationAsync(old, runId);
        await store.SaveConversationAsync(active, runId);
        await store.SaveLogicalAgentAsync(new LogicalAgentSession(agentId, runId, AgentRole.Manager, null, null, activeId, LogicalSessionState.Active));
        await new CrashConsistentOrchestrationStore(store).SaveAsync(new OrchestrationRecoverySnapshot(
            new ProjectRun(runId, ProjectId.New(), ProjectRunState.WaveRunning, DateTimeOffset.UtcNow, new ManagerEstimate(50), new VerifiedCompletion(40), ProjectCompletionMode.Active),
            null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.WaveRunning, DateTimeOffset.UtcNow));

        var full = await new FullDurabilityRecoveryService(store, new CrashConsistentOrchestrationStore(store)).ReconstructAsync(runId);
        Assert.NotNull(full);
        Assert.Equal(ConversationState.Archived, full!.Conversations.Single(x => x.Id == oldId).State);
        Assert.Equal(activeId, full.Conversations.Single(x => x.Id == oldId).SuccessorId);
        Assert.Equal(ConversationState.Active, full.Conversations.Single(x => x.Id == activeId).State);
        Assert.Equal(oldId, full.Conversations.Single(x => x.Id == activeId).PredecessorId);
    }

    [Fact]
    public async Task DS21_exactly_one_active_conversation_per_logical_agent()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "one-active.db");
        var runId = ProjectRunId.New();
        var agentId = LogicalAgentId.New();
        var activeId = ConversationId.New();
        await store.SaveConversationAsync(new PCCExecutive.Domain.Conversation(activeId, agentId, 1, AgentProviderKind.BrowserChat, "active", "url", ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1, 1, null, null), runId);
        await store.SaveLogicalAgentAsync(new LogicalAgentSession(agentId, runId, AgentRole.Worker, new WorkerSlotId(1), null, activeId, LogicalSessionState.Active));
        var orchestration = new CrashConsistentOrchestrationStore(store);
        await orchestration.SaveAsync(new OrchestrationRecoverySnapshot(
            new ProjectRun(runId, ProjectId.New(), ProjectRunState.WaveRunning, DateTimeOffset.UtcNow, new ManagerEstimate(10), new VerifiedCompletion(5), ProjectCompletionMode.Active),
            null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.WaveRunning, DateTimeOffset.UtcNow));
        Assert.True(await new ConversationInvariantService(new FullDurabilityRecoveryService(store, orchestration)).ExactlyOneActiveAsync(runId, agentId));
    }

    [Fact]
    public async Task DS22_persisted_operational_payloads_pass_privacy_guard_without_full_transcripts()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "privacy.db");
        var seeded = await SeedRepresentativeAsync(store);
        var payloads = await ReadPayloadsAsync(store.DatabasePath, seeded.Snapshot.ProjectRun.Id);
        var guard = new OperationalStatePrivacyGuard();
        foreach (var payload in payloads) guard.Validate(payload);

        var sensitive = string.Concat("Author", "ization", ":", " Bearer ", "secret-value");
        Assert.Throws<InvalidDataException>(() => guard.Validate(sensitive));
    }

    [Fact]
    public async Task DS23_database_identity_survives_reopen_backup_and_restore()
    {
        var path = Path.Combine(_root, "database-id.db");
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "database-id.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await schema.MigrateAsync();
        var id = await schema.GetDatabaseIdAsync();
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "database-id-backups"), "0.1.0", "TEST");
        Assert.Equal(id, backup.Manifest.SourceDatabaseId);
        await service.RestoreAsync(backup, false);
        Assert.Equal(id, await schema.GetDatabaseIdAsync());

        await using var reopened = new SqliteStateStore(path);
        await reopened.InitializeAsync();
        Assert.Equal(id, await new DurabilitySchemaManager(path).GetDatabaseIdAsync());
    }

    [Fact]
    public async Task DS23_wrong_database_identity_backup_is_rejected_before_target_mutation()
    {
        await using var first = await DurabilityTestFixture.NewStoreAsync(_root, "identity-first.db");
        await new DurabilitySchemaManager(first.DatabasePath).MigrateAsync();
        var firstService = new VerifiedBackupService(first);
        var backup = await firstService.CreateAsync(Path.Combine(_root, "identity-backups"), "0.1.0", "TEST");

        await using var second = await DurabilityTestFixture.NewStoreAsync(_root, "identity-second.db");
        var secondSchema = new DurabilitySchemaManager(second.DatabasePath);
        await secondSchema.MigrateAsync();
        var secondId = await secondSchema.GetDatabaseIdAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedBackupService(second).RestoreAsync(backup, false));
        Assert.Equal(secondId, await secondSchema.GetDatabaseIdAsync());
        Assert.Equal(DatabaseIntegrityState.INTEGRITY_OK, (await new SqliteIntegrityService().CheckAsync(second.DatabasePath)).State);
    }

    [Fact]
    public async Task DS24_recovery_journal_persists_semantic_evidence()
    {
        var path = Path.Combine(_root, "journal-evidence.db");
        await using (var store = await DurabilityTestFixture.NewStoreAsync(_root, "journal-evidence.db"))
        {
            await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();
            var journal = new RecoveryJournalService(store.DatabasePath);
            await journal.RecordAsync(RecoveryJournalKind.UNCLEAN_SHUTDOWN, "forced-stop");
            await journal.RecordAsync(RecoveryJournalKind.DB_INTEGRITY_CHECK, DatabaseIntegrityState.INTEGRITY_OK.ToString());
            await journal.RecordAsync(RecoveryJournalKind.BACKUP_VERIFIED, "backup");
            await journal.RecordAsync(RecoveryJournalKind.RECOVERY_COMPLETE, "complete");
        }

        var reopenedJournal = new RecoveryJournalService(path);
        var events = await reopenedJournal.ListAsync();
        Assert.Contains(RecoveryJournalKind.UNCLEAN_SHUTDOWN, events);
        Assert.Contains(RecoveryJournalKind.DB_INTEGRITY_CHECK, events);
        Assert.Contains(RecoveryJournalKind.BACKUP_VERIFIED, events);
        Assert.Contains(RecoveryJournalKind.RECOVERY_COMPLETE, events);
    }

    [Fact]
    public void DS25_machine_readable_data_safety_gate_has_exact_25_case_contract_and_required_states()
    {
        var repo = FindRepoRoot();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "tests", "data-safety", "data-safety-matrix.json")));
        var root = json.RootElement;
        Assert.Equal("DATA_SAFETY", root.GetProperty("gate").GetString());
        var states = root.GetProperty("allowedStates").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var state in new[] { "PASS", "FAIL", "BLOCKED_PACKAGE", "BLOCKED_CI", "NOT_EXECUTED" }) Assert.Contains(state, states);
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(25, cases.Length);
        Assert.Equal(25, cases.Select(x => x.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());

        var gateScript = File.ReadAllText(Path.Combine(repo, "tests", "data-safety", "Write-DataSafetyGate.ps1"));
        Assert.True(gateScript.Contains("Gate='DATA_SAFETY'", StringComparison.Ordinal));
        Assert.True(gateScript.Contains("ReleaseGateAlias='DATA_PRESERVATION'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_manifest_with_missing_file_is_rejected_without_touching_target()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "missing-backup.db");
        await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "missing-backups"), "0.1.0", "TEST");
        File.Delete(backup.Manifest.FilePath);
        var invalid = await service.VerifyAsync(backup.ManifestPath);
        Assert.False(invalid.IsVerified);
        Assert.Contains("backup-file:missing", invalid.Findings);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(invalid, false));
        Assert.Equal(DatabaseIntegrityState.INTEGRITY_OK, (await new SqliteIntegrityService().CheckAsync(store.DatabasePath)).State);
    }

    [Fact]
    public void Worker5_update_and_installer_contracts_are_data_preservation_oriented()
    {
        var repo = FindRepoRoot();
        var updater = File.ReadAllText(Path.Combine(repo, "updater", "Invoke-Upgrade.ps1"));
        var installer = File.ReadAllText(Path.Combine(repo, "installer", "PCCExecutive.iss"));
        Assert.True(updater.Contains("checkpoint.json", StringComparison.Ordinal));
        Assert.True(updater.Contains("HEALTH_FAILED_ROLLBACK_REQUIRED", StringComparison.Ordinal));
        Assert.True(updater.Contains("restore-update-checkpoint", StringComparison.Ordinal));
        Assert.True(installer.Contains("prepare-installer-upgrade", StringComparison.Ordinal));
        Assert.True(installer.Contains("post-install-verify", StringComparison.Ordinal));
    }

    private async Task<SeededState> SeedRepresentativeAsync(SqliteStateStore store)
    {
        var snapshot = DurabilityTestFixture.Wave(5);
        var tasks = snapshot.Tasks.ToArray();
        tasks[0] = tasks[0] with { State = TaskState.Completed };
        tasks[1] = tasks[1] with { State = TaskState.Running };
        tasks[2] = tasks[2] with { State = TaskState.Dispatched };
        tasks[3] = tasks[3] with { State = TaskState.Blocked };
        tasks[4] = tasks[4] with { State = TaskState.Assigned };
        var dispatches = snapshot.Dispatches.ToArray();
        dispatches[0] = dispatches[0] with { State = PCCExecutive.Domain.DispatchState.COMPLETED };
        dispatches[1] = dispatches[1] with { State = PCCExecutive.Domain.DispatchState.GENERATING };
        dispatches[2] = dispatches[2] with { State = PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN };
        dispatches[3] = dispatches[3] with { State = PCCExecutive.Domain.DispatchState.FAILED };
        dispatches[4] = dispatches[4] with { State = PCCExecutive.Domain.DispatchState.PREPARED };
        snapshot = snapshot with { Tasks = tasks, Dispatches = dispatches, Phase = OrchestrationPhase.WaveRunning, SavedAt = DateTimeOffset.UtcNow };

        var durable = new CrashConsistentOrchestrationStore(store);
        await durable.CreateWaveAsync(snapshot);
        await store.SaveProjectRunAsync(snapshot.ProjectRun);
        await store.SaveWaveAsync(snapshot.CurrentWave!);
        for (var i = 0; i < 5; i++)
        {
            await store.SaveTaskAsync(tasks[i], snapshot.ProjectRun.Id);
            await store.SaveDispatchAsync(dispatches[i]);
            var conversation = new PCCExecutive.Domain.Conversation(
                dispatches[i].ConversationId, dispatches[i].LogicalAgentId, 1, AgentProviderKind.BrowserChat,
                $"worker-provider-{i + 1}", $"worker-url-{i + 1}", ConversationState.Active,
                DateTimeOffset.UtcNow, null, null, null, 1, 1, null, null);
            await store.SaveConversationAsync(conversation, snapshot.ProjectRun.Id);
            await store.SaveLogicalAgentAsync(new LogicalAgentSession(
                dispatches[i].LogicalAgentId, snapshot.ProjectRun.Id, AgentRole.Worker,
                new WorkerSlotId(i + 1), tasks[i].Id, conversation.Id, LogicalSessionState.Active));
        }

        var managerId = LogicalAgentId.New();
        var managerConversationId = ConversationId.New();
        await store.SaveConversationAsync(new PCCExecutive.Domain.Conversation(
            managerConversationId, managerId, 1, AgentProviderKind.BrowserChat, "manager-provider", "manager-url",
            ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1, 1, null, null), snapshot.ProjectRun.Id);
        await store.SaveLogicalAgentAsync(new LogicalAgentSession(
            managerId, snapshot.ProjectRun.Id, AgentRole.Manager, null, null, managerConversationId, LogicalSessionState.Active));

        var attention = new AttentionRequest(
            AttentionRequestId.New(), snapshot.ProjectRun.Id, AttentionState.Open, "LOGIN",
            "Login required", "Sign in", "manager-runtime", false, DateTimeOffset.UtcNow);
        await store.SaveAttentionAsync(attention);
        await store.SaveSettingsAsync(new PccExecutiveSettings(BaseDispatchIntervalSeconds: 10, AutoResume: true));
        await InsertStateRecordAsync(store.DatabasePath, "decision-journal", "manager-decision-1", snapshot.ProjectRun.Id.ToString(), "{\"decision\":\"continue\",\"evidence\":\"head\"}");
        var checkpoint = await new RecoveryCheckpointService(store).CreateAsync(
            snapshot.ProjectRun.Id, managerId, null, null, snapshot.CurrentWave!.Id, managerConversationId, null,
            "task/pcc-executive-t0001-v1", "head", "#21", "WAVE_RUNNING",
            ["foundation-complete"], ["none"], ["preserve-uncertain-dispatch"], "continue safely",
            "0.1.0", "RELEASE_DATA_SAFETY");
        await new RecoveryJournalService(store.DatabasePath).RecordAsync(RecoveryJournalKind.RECOVERY_COMPLETE, "seeded", snapshot.ProjectRun.Id);
        return new(snapshot, managerId, managerConversationId, attention, checkpoint);
    }

    private static async Task<long> ScalarLongAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(SqliteDurabilityConnection.ConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertStateRecordAsync(string databasePath, string kind, string id, string? projectRunId, string payload)
    {
        await using var connection = new SqliteConnection(SqliteDurabilityConnection.ConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO state_records(kind,id,project_run_id,payload,updated_at) VALUES($kind,$id,$run,$payload,$at) ON CONFLICT(kind,id) DO UPDATE SET payload=excluded.payload,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$run", (object?)projectRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadPayloadsAsync(string databasePath, ProjectRunId projectRunId)
    {
        var result = new List<string>();
        await using var connection = new SqliteConnection(SqliteDurabilityConnection.ConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM state_records WHERE project_run_id=$run OR project_run_id IS NULL;";
        command.Parameters.AddWithValue("$run", projectRunId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task RewriteManifestHashAndSchemaAsync(string manifestPath, string databasePath, int schemaVersion)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = document.RootElement;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(databasePath))).ToLowerInvariant();
        var integrityElement = root.GetProperty("integrityStatus");
        var integrityStatus = integrityElement.ValueKind == JsonValueKind.String
            ? Enum.Parse<DatabaseIntegrityState>(integrityElement.GetString()!, true)
            : (DatabaseIntegrityState)integrityElement.GetInt32();
        var manifest = new BackupManifest(
            root.GetProperty("backupId").GetString()!,
            root.GetProperty("sourceDatabaseId").GetString()!,
            schemaVersion,
            root.GetProperty("applicationVersion").GetString()!,
            root.TryGetProperty("sourceSha", out var sourceSha) && sourceSha.ValueKind != JsonValueKind.Null ? sourceSha.GetString() : null,
            root.GetProperty("createdAt").GetDateTimeOffset(),
            root.GetProperty("reason").GetString()!,
            root.GetProperty("filePath").GetString()!,
            hash,
            integrityStatus);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "VERSION")) &&
                    File.Exists(Path.Combine(directory.FullName, "installer", "PCCExecutive.iss")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found for release data-safety acceptance.");
    }

    private sealed record SeededState(
        OrchestrationRecoverySnapshot Snapshot,
        LogicalAgentId ManagerId,
        ConversationId ManagerConversationId,
        AttentionRequest Attention,
        RecoveryCheckpointDocument Checkpoint);
}
