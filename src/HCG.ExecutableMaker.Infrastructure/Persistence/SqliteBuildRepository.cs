using System.Globalization;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Data.Sqlite;

namespace HCG.ExecutableMaker.Infrastructure.Persistence;

public sealed class SqliteBuildRepository(SqliteDatabase db) : IBuildRepository
{
    private const string BuildColumns =
        "id, project_id, project_name, status, settings_json, started_at, ended_at, exit_code, failure_reason, log_path, warning_count, error_count, trust_acknowledged, toolchain_versions_json";

    public async Task InsertAsync(BuildRecord build, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO builds ({BuildColumns}) VALUES ($id, $pid, $pname, $status, $settings, $start, $end, $exit, $fail, $log, $warn, $err, $trust, $tools)";
            BindBuild(cmd, build);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteChildrenAsync(c, tx, build, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(BuildRecord build, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE builds SET project_name = $pname, status = $status, settings_json = $settings, ended_at = $end, exit_code = $exit,
                    failure_reason = $fail, log_path = $log, warning_count = $warn, error_count = $err, trust_acknowledged = $trust,
                    toolchain_versions_json = $tools
                WHERE id = $id
                """;
            BindBuild(cmd, build);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM build_messages WHERE build_id = $id; DELETE FROM artifacts WHERE build_id = $id;";
            del.Parameters.AddWithValue("$id", build.Id.ToString());
            await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteChildrenAsync(c, tx, build, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindBuild(SqliteCommand cmd, BuildRecord b)
    {
        cmd.Parameters.AddWithValue("$id", b.Id.ToString());
        cmd.Parameters.AddWithValue("$pid", b.ProjectId.ToString());
        cmd.Parameters.AddWithValue("$pname", b.ProjectName);
        cmd.Parameters.AddWithValue("$status", b.Status.ToString());
        cmd.Parameters.AddWithValue("$settings", HcgJson.Serialize(b.Settings));
        cmd.Parameters.AddWithValue("$start", b.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$end", (object?)b.EndedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exit", (object?)b.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fail", (object?)b.FailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$log", (object?)b.LogPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$warn", b.WarningCount);
        cmd.Parameters.AddWithValue("$err", b.ErrorCount);
        cmd.Parameters.AddWithValue("$trust", b.TrustAcknowledged ? 1 : 0);
        cmd.Parameters.AddWithValue("$tools", HcgJson.Serialize(b.ToolchainVersions));
    }

    private static async Task WriteChildrenAsync(SqliteConnection c, SqliteTransaction tx, BuildRecord build, CancellationToken ct)
    {
        foreach (var m in build.Messages)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO build_messages (build_id, severity, text, code, file, line) VALUES ($b, $s, $t, $c, $f, $l)";
            cmd.Parameters.AddWithValue("$b", build.Id.ToString());
            cmd.Parameters.AddWithValue("$s", m.Severity.ToString());
            cmd.Parameters.AddWithValue("$t", m.Text);
            cmd.Parameters.AddWithValue("$c", (object?)m.Code ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", (object?)m.File ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$l", (object?)m.Line ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var a in build.Artifacts)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO artifacts (id, build_id, format, kind, path, size_bytes, sha256, verification, checks_json, created_at)
                VALUES ($id, $b, $fmt, $kind, $path, $size, $sha, $ver, $checks, $created)
                """;
            cmd.Parameters.AddWithValue("$id", a.Id.ToString());
            cmd.Parameters.AddWithValue("$b", build.Id.ToString());
            cmd.Parameters.AddWithValue("$fmt", a.Format.ToString());
            cmd.Parameters.AddWithValue("$kind", a.Kind.ToString());
            cmd.Parameters.AddWithValue("$path", a.Path);
            cmd.Parameters.AddWithValue("$size", a.SizeBytes);
            cmd.Parameters.AddWithValue("$sha", a.Sha256);
            cmd.Parameters.AddWithValue("$ver", a.Verification.ToString());
            cmd.Parameters.AddWithValue("$checks", HcgJson.Serialize(a.Checks));
            cmd.Parameters.AddWithValue("$created", a.CreatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<BuildRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var list = await QueryBuildsAsync("WHERE id = $id", null, cmd => cmd.Parameters.AddWithValue("$id", id.ToString()), cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    public Task<IReadOnlyList<BuildRecord>> ListAsync(Guid? projectId = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var where = projectId is null ? "" : "WHERE project_id = $pid";
        return QueryBuildsAsync(where + " ORDER BY started_at DESC" + (limit is { } l ? $" LIMIT {l}" : ""), null, cmd =>
        {
            if (projectId is { } p) cmd.Parameters.AddWithValue("$pid", p.ToString());
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<BuildRecord>> QueryBuildsAsync(string tail, object? _, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var builds = new List<BuildRecord>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT {BuildColumns} FROM builds {tail}";
            bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var settings = HcgJson.Deserialize<BuildSettings>(r.GetString(4))!;
                builds.Add(new BuildRecord
                {
                    Id = Guid.Parse(r.GetString(0)),
                    ProjectId = Guid.Parse(r.GetString(1)),
                    ProjectName = r.GetString(2),
                    Status = Enum.Parse<BuildStatus>(r.GetString(3)),
                    Settings = settings,
                    StartedAt = ParseDate(r.GetString(5)),
                    EndedAt = r.IsDBNull(6) ? null : ParseDate(r.GetString(6)),
                    ExitCode = r.IsDBNull(7) ? null : r.GetInt32(7),
                    FailureReason = r.IsDBNull(8) ? null : r.GetString(8),
                    LogPath = r.IsDBNull(9) ? null : r.GetString(9),
                    WarningCount = r.GetInt32(10),
                    ErrorCount = r.GetInt32(11),
                    TrustAcknowledged = r.GetInt32(12) != 0,
                    ToolchainVersions = r.IsDBNull(13) ? new Dictionary<string, string>() : HcgJson.Deserialize<Dictionary<string, string>>(r.GetString(13)) ?? [],
                });
            }
        }
        foreach (var b in builds)
        {
            b.Messages.AddRange(await ReadMessagesAsync(c, b.Id, ct).ConfigureAwait(false));
            b.Artifacts.AddRange(await ReadArtifactsAsync(c, "WHERE build_id = $b", cmd => cmd.Parameters.AddWithValue("$b", b.Id.ToString()), ct).ConfigureAwait(false));
        }
        return builds;
    }

    private static async Task<List<BuildMessage>> ReadMessagesAsync(SqliteConnection c, Guid buildId, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT severity, text, code, file, line FROM build_messages WHERE build_id = $b ORDER BY id";
        cmd.Parameters.AddWithValue("$b", buildId.ToString());
        var list = new List<BuildMessage>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new BuildMessage(Enum.Parse<DiagnosticSeverity>(r.GetString(0)), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4)));
        }
        return list;
    }

    private static async Task<List<ArtifactRecord>> ReadArtifactsAsync(SqliteConnection c, string tail, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, build_id, format, kind, path, size_bytes, sha256, verification, checks_json, created_at FROM artifacts " + tail;
        bind(cmd);
        var list = new List<ArtifactRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ArtifactRecord
            {
                Id = Guid.Parse(r.GetString(0)),
                BuildId = Guid.Parse(r.GetString(1)),
                Format = Enum.Parse<PackagingFormat>(r.GetString(2)),
                Kind = Enum.Parse<ArtifactKind>(r.GetString(3)),
                Path = r.GetString(4),
                SizeBytes = r.GetInt64(5),
                Sha256 = r.GetString(6),
                Verification = Enum.Parse<VerificationStatus>(r.GetString(7)),
                Checks = r.IsDBNull(8) ? [] : HcgJson.Deserialize<List<VerificationCheck>>(r.GetString(8)) ?? [],
                CreatedAt = ParseDate(r.GetString(9)),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<ArtifactRecord>> ListArtifactsAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadArtifactsAsync(c, "ORDER BY created_at DESC", _ => { }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM builds WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM builds";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BuildStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM projects),
              (SELECT COUNT(*) FROM builds),
              (SELECT COUNT(*) FROM builds WHERE status = 'Succeeded'),
              (SELECT COUNT(*) FROM builds WHERE status IN ('Failed', 'TimedOut')),
              (SELECT COUNT(*) FROM builds WHERE status = 'Cancelled'),
              (SELECT COUNT(*) FROM artifacts)
            """;
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await r.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new BuildStatistics(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5));
    }

    private static DateTimeOffset ParseDate(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
