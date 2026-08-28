namespace PCCExecutive.Infrastructure;

public static class PackagedStartupSchemaSafety
{
    public static Task EnsureDefaultCurrentAsync(string applicationVersion, CancellationToken cancellationToken = default)
    {
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCC Executive");
        return EnsureCurrentAsync(SqliteStateStore.DefaultDatabasePath, Path.Combine(dataRoot, "Backups", "startup-schema"), applicationVersion, cancellationToken);
    }

    public static async Task EnsureCurrentAsync(string databasePath, string backupRoot, string applicationVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        if (string.IsNullOrWhiteSpace(backupRoot)) throw new ArgumentException("Backup root is required.", nameof(backupRoot));
        if (string.IsNullOrWhiteSpace(applicationVersion)) throw new ArgumentException("Application version is required.", nameof(applicationVersion));

        var normalizedDatabasePath = Path.GetFullPath(databasePath);
        var existedBeforeStartup = File.Exists(normalizedDatabasePath);
        await using var store = new SqliteStateStore(normalizedDatabasePath);
        if (!existedBeforeStartup)
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent(await new DurabilitySchemaManager(normalizedDatabasePath).MigrateAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
            return;
        }

        var schema = new DurabilitySchemaManager(normalizedDatabasePath);
        var compatibility = await schema.ClassifyAsync(cancellationToken).ConfigureAwait(false);
        if (compatibility == SchemaCompatibility.CURRENT) return;
        if (compatibility != SchemaCompatibility.UPGRADE_REQUIRED)
            throw new InvalidOperationException($"Existing PCC Executive database is {compatibility}; startup refused without replacing or downgrading the database.");

        var backup = await new VerifiedBackupService(store).CreateAsync(backupRoot, applicationVersion, "PRE_STARTUP_SCHEMA_MIGRATION", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!backup.IsVerified) throw new InvalidOperationException("Verified pre-migration backup is required before startup schema migration.");
        RequireCurrent(await schema.MigrateAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    private static void RequireCurrent(SchemaCompatibility compatibility)
    {
        if (compatibility != SchemaCompatibility.CURRENT)
            throw new InvalidOperationException($"PCC Executive requires schema v{DurabilitySchemaManager.TargetSchemaVersion}; observed {compatibility}.");
    }
}
