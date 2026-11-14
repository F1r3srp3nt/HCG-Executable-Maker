using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Persistence;

/// <summary>Stores <see cref="MobileSelection"/> as JSON in the settings table (key "mobile-selection:&lt;project id&gt;").</summary>
public sealed class SqliteMobileSelectionStore(SqliteDatabase db) : IMobileSelectionStore
{
    private const string Prefix = "mobile-selection:";

    public async Task<MobileSelection?> LoadAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", Prefix + projectId);
        var json = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        try { return HcgJson.Deserialize<MobileSelection>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public async Task SaveAsync(MobileSelection selection, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", Prefix + selection.ProjectId);
        cmd.Parameters.AddWithValue("$v", HcgJson.Serialize(selection));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
