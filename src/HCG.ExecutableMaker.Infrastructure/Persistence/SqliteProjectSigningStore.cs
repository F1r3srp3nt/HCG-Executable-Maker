using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Persistence;

/// <summary>Stores <see cref="ProjectSigningConfiguration"/> as JSON in the settings table (key "signing-project:&lt;id&gt;").</summary>
public sealed class SqliteProjectSigningStore(SqliteDatabase db) : IProjectSigningStore
{
    private const string Prefix = "signing-project:";
    private const string DeviceKey = "signing-device-id";

    public async Task<ProjectSigningConfiguration?> LoadAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var json = await ReadAsync(Prefix + projectId, cancellationToken).ConfigureAwait(false);
        try { return HcgJson.Deserialize<ProjectSigningConfiguration>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public Task SaveAsync(ProjectSigningConfiguration configuration, CancellationToken cancellationToken = default) =>
        WriteAsync(Prefix + configuration.ProjectId, HcgJson.Serialize(configuration), cancellationToken);

    public async Task DeleteAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", Prefix + projectId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DeviceIdAsync(CancellationToken cancellationToken = default)
    {
        var existing = await ReadAsync(DeviceKey, cancellationToken).ConfigureAwait(false);
        if (existing is { Length: > 0 } && Guid.TryParse(existing.Trim('"'), out var g)) return g.ToString();
        var id = Guid.NewGuid().ToString();
        await WriteAsync(DeviceKey, "\"" + id + "\"", cancellationToken).ConfigureAwait(false);
        return id;
    }

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private async Task WriteAsync(string key, string value, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
