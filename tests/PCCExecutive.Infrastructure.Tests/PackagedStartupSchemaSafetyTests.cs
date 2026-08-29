using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class PackagedStartupSchemaSafetyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-packaged-startup-schema", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { try { Directory.Delete(_root, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public async Task CleanPackagedStartupReachesSchemaV2()
    {
        var database = Path.Combine(_root, "clean", "pcc-executive.db");
        await PackagedStartupSchemaSafety.EnsureCurrentAsync(database, Path.Combine(_root, "clean-backups"), "0.1.0");
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
        }
        await PackagedStartupSchemaSafety.EnsureCurrentAsync(database, backups, "0.1.0");
        await using var reopened = new SqliteStateStore(database);
        Assert.Equal(2, await reopened.GetSchemaVersionAsync());
        Assert.Equal(23, (await reopened.LoadSettingsAsync()).BaseDispatchIntervalSeconds);
        Assert.NotEmpty(Directory.GetFiles(backups, "*.db"));
    }

    [Fact]
    public async Task NewerSchemaIsRejectedWithoutMutatingDatabase()
    {
        var database = Path.Combine(_root, "newer", "pcc-executive.db");
        await using (var store = new SqliteStateStore(database)) { await store.InitializeAsync(); await new DurabilitySchemaManager(database).MigrateAsync(); }
        await using (var connection = new SqliteConnection(SqliteDurabilityConnection.ConnectionString(database)))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations(version,applied_at) VALUES(99,$at);";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var before = await HashAsync(database);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PackagedStartupSchemaSafety.EnsureCurrentAsync(database, Path.Combine(_root, "newer-backups"), "0.1.0"));
        Assert.Contains("NEWER_THAN_APP", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await HashAsync(database));
    }

    private static async Task<string> HashAsync(string path) => Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(path)));
}
