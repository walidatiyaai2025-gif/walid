using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public sealed record BackupManifest(
    string BackupId,
    string SourceDatabaseId,
    int SchemaVersion,
    string ApplicationVersion,
    string? SourceSha,
    DateTimeOffset CreatedAt,
    string Reason,
    string FilePath,
    string FileHash,
    DatabaseIntegrityState IntegrityStatus);

public sealed record VerifiedBackup(BackupManifest Manifest, string ManifestPath, bool IsVerified, IReadOnlyList<string> Findings);

public sealed class VerifiedBackupService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SqliteStateStore _store;
    private readonly SqliteDurabilityPolicy _policy;
    private readonly SqliteIntegrityService _integrity;
    private readonly DurabilitySchemaManager _schema;

    public VerifiedBackupService(SqliteStateStore store, SqliteDurabilityPolicy? policy = null)
    {
        _store = store;
        _policy = policy ?? new();
        _integrity = new SqliteIntegrityService(_policy);
        _schema = new DurabilitySchemaManager(store.DatabasePath, _policy);
    }

    public async Task<VerifiedBackup> CreateAsync(string directory, string applicationVersion, string reason, string? sourceSha = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await _schema.InitializeMetadataAsync(cancellationToken).ConfigureAwait(false);
        var backupId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, $"pcc-state-{backupId}.db");
        await using (var source = await SqliteDurabilityConnection.OpenAsync(_store.DatabasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false))
        await using (var target = await SqliteDurabilityConnection.OpenAsync(path, _policy, cancellationToken: cancellationToken).ConfigureAwait(false))
            source.BackupDatabase(target);
        var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        var integrity = await _integrity.CheckAsync(path, cancellationToken).ConfigureAwait(false);
        var manifest = new BackupManifest(backupId, await _schema.GetDatabaseIdAsync(cancellationToken).ConfigureAwait(false), await _store.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false), applicationVersion, sourceSha, DateTimeOffset.UtcNow, reason, path, hash, integrity.State);
        var manifestPath = path + ".manifest.json";
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, Json), cancellationToken).ConfigureAwait(false);
        var verified = await VerifyAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (verified.IsVerified) await PersistManifestAsync(verified.Manifest, cancellationToken).ConfigureAwait(false);
        return verified;
    }

    public async Task<VerifiedBackup> VerifyAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();
        BackupManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), Json); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(new("INVALID", "INVALID", 0, "", null, DateTimeOffset.MinValue, "", "", "", DatabaseIntegrityState.INTEGRITY_FAILED), manifestPath, false, [$"manifest:{ex.GetType().Name}"]); }
        if (manifest is null) return new(new("INVALID", "INVALID", 0, "", null, DateTimeOffset.MinValue, "", "", "", DatabaseIntegrityState.INTEGRITY_FAILED), manifestPath, false, ["manifest:null"]);
        if (!File.Exists(manifest.FilePath)) findings.Add("backup-file:missing");
        else
        {
            var hash = await HashFileAsync(manifest.FilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, manifest.FileHash, StringComparison.OrdinalIgnoreCase)) findings.Add("backup-hash:mismatch");
            var integrity = await _integrity.CheckAsync(manifest.FilePath, cancellationToken).ConfigureAwait(false);
            if (integrity.State != DatabaseIntegrityState.INTEGRITY_OK) findings.AddRange(integrity.Findings.Select(x => $"integrity:{x}"));
            try
            {
                await using var connection = await SqliteDurabilityConnection.OpenAsync(manifest.FilePath, _policy, SqliteOpenMode.ReadOnly, cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_migrations;";
                var schemaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                if (schemaVersion != manifest.SchemaVersion) findings.Add("schema-version:mismatch");
            }
            catch (SqliteException ex) { findings.Add($"schema:{ex.SqliteErrorCode}"); }
        }
        return new(manifest, manifestPath, findings.Count == 0 && manifest.IntegrityStatus == DatabaseIntegrityState.INTEGRITY_OK, findings);
    }

    public async Task<string> RestoreAsync(VerifiedBackup backup, bool activeDatabaseLease, CancellationToken cancellationToken = default)
    {
        if (activeDatabaseLease) throw new InvalidOperationException("Cannot restore over an actively leased canonical database.");
        var reverified = await VerifyAsync(backup.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (!reverified.IsVerified) throw new InvalidDataException("Only a verified compatible backup may be restored.");
        var preserved = _store.DatabasePath + $".preserved-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.db";
        if (File.Exists(_store.DatabasePath)) File.Copy(_store.DatabasePath, preserved, overwrite: false);
        await using (var source = await SqliteDurabilityConnection.OpenAsync(reverified.Manifest.FilePath, _policy, SqliteOpenMode.ReadOnly, cancellationToken).ConfigureAwait(false))
        await using (var target = await SqliteDurabilityConnection.OpenAsync(_store.DatabasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false))
            source.BackupDatabase(target);
        var integrity = await _integrity.CheckAsync(_store.DatabasePath, cancellationToken).ConfigureAwait(false);
        if (integrity.State != DatabaseIntegrityState.INTEGRITY_OK) throw new InvalidDataException("Restored database failed integrity validation.");
        return preserved;
    }

    private async Task PersistManifestAsync(BackupManifest manifest, CancellationToken cancellationToken)
    {
        await _schema.InitializeMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (await _schema.ClassifyAsync(cancellationToken).ConfigureAwait(false) == SchemaCompatibility.UPGRADE_REQUIRED) await _schema.MigrateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_store.DatabasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO durability_backups(backup_id,source_database_id,schema_version,application_version,source_sha,created_at,reason,file_path,file_hash,integrity_status,manifest_json) VALUES($id,$source,$schema,$app,$sha,$at,$reason,$path,$hash,$integrity,$json);";
        command.Parameters.AddWithValue("$id", manifest.BackupId);
        command.Parameters.AddWithValue("$source", manifest.SourceDatabaseId);
        command.Parameters.AddWithValue("$schema", manifest.SchemaVersion);
        command.Parameters.AddWithValue("$app", manifest.ApplicationVersion);
        command.Parameters.AddWithValue("$sha", (object?)manifest.SourceSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", manifest.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$reason", manifest.Reason);
        command.Parameters.AddWithValue("$path", manifest.FilePath);
        command.Parameters.AddWithValue("$hash", manifest.FileHash);
        command.Parameters.AddWithValue("$integrity", manifest.IntegrityStatus.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(manifest, Json));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }
}
