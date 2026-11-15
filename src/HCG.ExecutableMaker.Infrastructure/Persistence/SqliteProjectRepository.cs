using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Data.Sqlite;

namespace HCG.ExecutableMaker.Infrastructure.Persistence;

public sealed class SqliteProjectRepository(SqliteDatabase db) : IProjectRepository
{
    public async Task UpsertAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, name, source_kind, source_location, workspace_path, preserved_archive_path, imported_at, last_opened_at, analysis_json,
                                  source_revision, source_fingerprint, refreshed_at)
            VALUES ($id, $name, $kind, $src, $ws, $arch, $imp, $open, $analysis, $rev, $fp, $refreshed)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, last_opened_at = excluded.last_opened_at, analysis_json = excluded.analysis_json,
                                          source_revision = excluded.source_revision, source_fingerprint = excluded.source_fingerprint, refreshed_at = excluded.refreshed_at
            """;
        cmd.Parameters.AddWithValue("$id", project.Id.ToString());
        cmd.Parameters.AddWithValue("$name", project.Name);
        cmd.Parameters.AddWithValue("$kind", project.SourceKind.ToString());
        cmd.Parameters.AddWithValue("$src", project.SourceLocation);
        cmd.Parameters.AddWithValue("$ws", project.WorkspacePath);
        cmd.Parameters.AddWithValue("$arch", (object?)project.PreservedArchivePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imp", project.ImportedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$open", (object?)project.LastOpenedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$analysis", project.Analysis is null ? DBNull.Value : HcgJson.Serialize(project.Analysis));
        cmd.Parameters.AddWithValue("$rev", (object?)project.SourceRevision ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fp", (object?)project.SourceFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$refreshed", (object?)project.RefreshedAt?.ToString("O") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var list = await QueryAsync("WHERE id = $id", cmd => cmd.Parameters.AddWithValue("$id", id.ToString()), cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    public Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        QueryAsync("ORDER BY COALESCE(last_opened_at, imported_at) DESC", null, cancellationToken);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM projects WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ProjectRecord>> QueryAsync(string tail, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, source_kind, source_location, workspace_path, preserved_archive_path, imported_at, last_opened_at, analysis_json, source_revision, source_fingerprint, refreshed_at FROM projects " + tail;
        bind?.Invoke(cmd);
        var result = new List<ProjectRecord>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ProjectAnalysis? analysis = null;
            if (!r.IsDBNull(8))
            {
                try { analysis = HcgJson.Deserialize<ProjectAnalysis>(r.GetString(8)); }
                catch (System.Text.Json.JsonException) { analysis = null; } // stale schema: re-inspect on open
            }
            result.Add(new ProjectRecord
            {
                Id = Guid.Parse(r.GetString(0)),
                Name = r.GetString(1),
                SourceKind = Enum.Parse<ProjectSourceKind>(r.GetString(2)),
                SourceLocation = r.GetString(3),
                WorkspacePath = r.GetString(4),
                PreservedArchivePath = r.IsDBNull(5) ? null : r.GetString(5),
                ImportedAt = DateTimeOffset.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                LastOpenedAt = r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
                Analysis = analysis,
                SourceRevision = r.IsDBNull(9) ? null : r.GetString(9),
                SourceFingerprint = r.IsDBNull(10) ? null : r.GetString(10),
                RefreshedAt = r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11), System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        return result;
    }
}
