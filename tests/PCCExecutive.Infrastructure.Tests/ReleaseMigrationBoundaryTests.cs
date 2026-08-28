using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class ReleaseMigrationBoundaryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-release-migration-boundary", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { try { Directory.Delete(_root, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public async Task Pre_migration_backup_does_not_mutate_schema_before_safe_coordinator_migrates()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "safe-v1-v2.db");
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerPlanning, DateTimeOffset.UtcNow, new ManagerEstimate(20), new VerifiedCompletion(10), ProjectCompletionMode.Active);
        await store.SaveProjectRunAsync(run);
        Assert.Equal(1, await store.GetSchemaVersionAsync());

        var backups = new VerifiedBackupService(store);
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        var journal = new RecoveryJournalService(store.DatabasePath);
        var result = await new SafeMigrationRecoveryCoordinator(backups, schema, journal)
            .MigrateWithVerifiedBackupAsync(Path.Combine(_root, "safe-backups"), "0.1.0", "source-sha", projectRunId: run.Id);

        Assert.True(result.Succeeded);
        Assert.True(result.Backup.IsVerified);
        Assert.Equal(1, result.Backup.Manifest.SchemaVersion);
        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(run, await store.LoadProjectRunAsync(run.Id));
        Assert.Equal(MigrationRunStatus.APPLIED, (await schema.GetMigrationAsync("durability-v2"))!.Status);
        var events = await journal.ListAsync();
        Assert.Contains(RecoveryJournalKind.BACKUP_VERIFIED, events);
        Assert.Contains(RecoveryJournalKind.MIGRATION_STARTED, events);
        Assert.Contains(RecoveryJournalKind.MIGRATION_COMPLETED, events);
    }

    [Fact]
    public async Task Interrupted_safe_migration_keeps_v1_source_verified_backup_and_failure_journal()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "interrupted-safe-v1-v2.db");
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerPlanning, DateTimeOffset.UtcNow, new ManagerEstimate(20), new VerifiedCompletion(10), ProjectCompletionMode.Active);
        await store.SaveProjectRunAsync(run);
        var backupDirectory = Path.Combine(_root, "interrupted-safe-backups");
        var backups = new VerifiedBackupService(store);
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        var journal = new RecoveryJournalService(store.DatabasePath);

        await Assert.ThrowsAsync<InjectedCrashException>(() =>
            new SafeMigrationRecoveryCoordinator(backups, schema, journal).MigrateWithVerifiedBackupAsync(
                backupDirectory,
                "0.1.0",
                "source-sha",
                new DeterministicCrashFaultInjector(CrashFaultPoint.BEFORE_COMMIT),
                run.Id));

        Assert.Equal(1, await store.GetSchemaVersionAsync());
        Assert.Equal(MigrationRunStatus.ROLLED_BACK, (await schema.GetMigrationAsync("durability-v2"))!.Status);
        Assert.Equal(run, await store.LoadProjectRunAsync(run.Id));
        var manifest = Directory.GetFiles(backupDirectory, "*.manifest.json", SearchOption.TopDirectoryOnly).Single();
        var verified = await backups.VerifyAsync(manifest);
        Assert.True(verified.IsVerified);
        Assert.Equal(1, verified.Manifest.SchemaVersion);
        var events = await journal.ListAsync();
        Assert.Contains(RecoveryJournalKind.BACKUP_VERIFIED, events);
        Assert.Contains(RecoveryJournalKind.MIGRATION_STARTED, events);
        Assert.Contains(RecoveryJournalKind.MIGRATION_FAILED, events);
    }
}
