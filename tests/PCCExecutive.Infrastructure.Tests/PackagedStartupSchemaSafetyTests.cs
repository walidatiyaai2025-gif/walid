using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class PackagedStartupSchemaSafetyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-packaged-startup-schema", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CleanPackagedStartupReachesSchemaV2()
    {
        var database = Path.Combine(_root, "clean", "pcc-executive.db");
        var backups = Path.Combine(_root, "clean-backups");

        await PackagedStartupSchemaSafety.EnsureCurrentAsync(database, backups, "0.1.0");

        await using var store = new SqliteStateStore(database);
        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(SchemaCompatibility.CURRENT, await new DurabilitySchemaManager(database).ClassifyAsync());
    }

    [Fact]
    public async Task ExistingV1IsBackedUpAndMigratedWithoutLosingSettings()
    {
        var database = Path.Combine(_root, "upgrade", "pcc-executive.db");
        var backups = Path.Combine(_root, "upgrade-backups");
        await using (var store = new SqliteStateStore(database))
        {
            await store.InitializeAsync();
            await store.SaveSettingsAsync(new PccExecutiveSettings(BaseDispatchIntervalSeconds: 23, AutoResume: false));
            Assert.Equal(1, await store.GetSchemaVersionAsync());
        }

        await PackagedStartupSchemaSafety.EnsureCurrentAsync(database, backups, "0.1.0");

        await using var reopened = new SqliteStateStore(database);
        Assert.Equal(2, await reopened.GetSchemaVersionAsync());
        var settings = await reopened.LoadSettingsAsync();
        Assert.Equal(23, settings.BaseDispatchIntervalSeconds);
        Assert.False(settings.AutoResume);
        Assert.NotEmpty(Directory.GetFiles(backups, "*.db"));
        Assert.NotEmpty(Directory.GetFiles(backups, "*.manifest.json"));
    }

    [Fact]
    public async Task NewerSchemaIsRejectedWithoutMutatingDatabase()
    {
        var database = Path.Combine(_root, "newer", "pcc-executive.db");
        var backups = Path.Combine(_root, "newer-backups");
        await using (var store = new SqliteStateStore(database))
        {
            await store.InitializeAsync();
            Assert.Equal(SchemaCompatibility.CURRENT, await new DurabilitySchemaManager(database).MigrateAsync());
        }

        await using (var connection = new SqliteConnection(SqliteDurabilityConnection.ConnectionString(database)))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations(version,applied_at) VALUES(99,$at);";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var before = await HashAsync(database);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PackagedStartupSchemaSafety.EnsureCurrentAsync(database, backups, "0.1.0"));
        var after = await HashAsync(database);

        Assert.Contains("NEWER_THAN_APP", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, after);
        Assert.False(Directory.Exists(backups));
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
