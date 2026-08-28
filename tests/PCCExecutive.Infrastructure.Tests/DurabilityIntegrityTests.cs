using Microsoft.Data.Sqlite;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class DurabilityIntegrityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-durability-integrity", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { try { Directory.Delete(_root, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public void Sqlite_policy_is_wal_full_foreign_keys_and_bounded_busy_retry()
    {
        var policy = new SqliteDurabilityPolicy();
        Assert.Equal("WAL", policy.JournalMode);
        Assert.Equal("FULL", policy.Synchronous);
        Assert.True(policy.ForeignKeys);
        Assert.Equal(5000, policy.BusyTimeoutMilliseconds);
        Assert.InRange(policy.BusyRetryCount, 1, 10);
    }

    [Fact]
    public async Task Integrity_check_success_is_ok()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "integrity.db");
        var result = await new SqliteIntegrityService().CheckAsync(store.DatabasePath);
        Assert.Equal(DatabaseIntegrityState.INTEGRITY_OK, result.State);
    }

    [Fact]
    public async Task Read_only_probe_cannot_poison_an_overlapping_writable_metadata_connection()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "readonly-overlap.db");
        var policy = new SqliteDurabilityPolicy();
        await using var readOnly = await SqliteDurabilityConnection.OpenAsync(
            store.DatabasePath,
            policy,
            SqliteOpenMode.ReadOnly);

        var schema = new DurabilitySchemaManager(store.DatabasePath, policy);
        await schema.InitializeMetadataAsync();
        Assert.False(string.IsNullOrWhiteSpace(await schema.GetDatabaseIdAsync()));
    }

    [Fact]
    public async Task Corrupted_database_is_detected()
    {
        var path = Path.Combine(_root, "corrupt.db");
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)0x5a, 1024).ToArray());
        var result = await new SqliteIntegrityService().CheckAsync(path);
        Assert.Equal(DatabaseIntegrityState.INTEGRITY_FAILED, result.State);
    }

    [Fact]
    public async Task Migration_success_reaches_v2_and_applied_journal()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "migration.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        Assert.Equal(SchemaCompatibility.UPGRADE_REQUIRED, await schema.ClassifyAsync());
        Assert.Equal(SchemaCompatibility.CURRENT, await schema.MigrateAsync());
        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(MigrationRunStatus.APPLIED, (await schema.GetMigrationAsync("durability-v2"))!.Status);
    }

    [Fact]
    public async Task Migration_interruption_before_commit_rolls_back()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "migration-crash.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await Assert.ThrowsAsync<InjectedCrashException>(() => schema.MigrateAsync(new DeterministicCrashFaultInjector(CrashFaultPoint.BEFORE_COMMIT)));
        Assert.Equal(1, await store.GetSchemaVersionAsync());
        Assert.Equal(MigrationRunStatus.ROLLED_BACK, (await schema.GetMigrationAsync("durability-v2"))!.Status);
    }

    [Fact]
    public async Task Migration_after_commit_is_current_even_if_process_dies_after_commit()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "migration-after.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await Assert.ThrowsAsync<InjectedCrashException>(() => schema.MigrateAsync(new DeterministicCrashFaultInjector(CrashFaultPoint.AFTER_COMMIT)));
        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(SchemaCompatibility.CURRENT, await schema.ClassifyAsync());
    }

    [Fact]
    public async Task Newer_database_is_classified_and_not_downgraded()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "newer.db");
        await using (var connection = new SqliteConnection(SqliteDurabilityConnection.ConnectionString(store.DatabasePath)))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations(version,applied_at) VALUES(99,$at);";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        Assert.Equal(SchemaCompatibility.NEWER_THAN_APP, await schema.ClassifyAsync());
        Assert.Equal(SchemaCompatibility.NEWER_THAN_APP, await schema.MigrateAsync());
        Assert.Equal(99, await store.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task Verified_backup_has_hash_schema_identity_and_integrity()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "backup.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await schema.MigrateAsync();
        var backup = await new VerifiedBackupService(store).CreateAsync(Path.Combine(_root, "backups"), "0.1.0", "PRE_UPDATE", "source-sha");
        Assert.True(backup.IsVerified);
        Assert.NotEmpty(backup.Manifest.BackupId);
        Assert.NotEmpty(backup.Manifest.SourceDatabaseId);
        Assert.Equal(2, backup.Manifest.SchemaVersion);
        Assert.Equal("source-sha", backup.Manifest.SourceSha);
        Assert.Equal(DatabaseIntegrityState.INTEGRITY_OK, backup.Manifest.IntegrityStatus);
        Assert.True(File.Exists(backup.ManifestPath));
    }

    [Fact]
    public async Task Tampered_backup_is_unverified_and_restore_is_rejected()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "tamper.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await schema.MigrateAsync();
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "bad"), "0.1.0", "TEST");
        await File.AppendAllTextAsync(backup.Manifest.FilePath, "tamper");
        var invalid = await service.VerifyAsync(backup.ManifestPath);
        Assert.False(invalid.IsVerified);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(invalid, false));
    }

    [Fact]
    public async Task Restore_is_blocked_when_active_database_lease_is_declared()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "active.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await schema.MigrateAsync();
        var service = new VerifiedBackupService(store);
        var backup = await service.CreateAsync(Path.Combine(_root, "active-backup"), "0.1.0", "TEST");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backup, true));
    }

    [Fact]
    public async Task Safe_migration_creates_verified_backup_before_schema_change()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "safe-migration.db");
        var schema = new DurabilitySchemaManager(store.DatabasePath);
        await schema.InitializeMetadataAsync();
        await schema.MigrateAsync();
        var journal = new RecoveryJournalService(store.DatabasePath);
        var result = await new SafeMigrationRecoveryCoordinator(new VerifiedBackupService(store), schema, journal)
            .MigrateWithVerifiedBackupAsync(Path.Combine(_root, "pre-migration"), "0.1.0", "sha");
        Assert.True(result.Succeeded);
        Assert.True(result.Backup.IsVerified);
        Assert.Contains(RecoveryJournalKind.BACKUP_VERIFIED, await journal.ListAsync());
    }
}
