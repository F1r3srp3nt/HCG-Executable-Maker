using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.Infrastructure.Persistence;

public sealed class SqliteSettingsStore(SqliteDatabase db) : ISettingsStore
{
    private const string Key = "app-settings";

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", Key);
        var json = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        try { return HcgJson.Deserialize<AppSettings>(json) ?? new AppSettings(); }
        catch (System.Text.Json.JsonException) { return new AppSettings(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", Key);
        cmd.Parameters.AddWithValue("$v", HcgJson.Serialize(settings));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
