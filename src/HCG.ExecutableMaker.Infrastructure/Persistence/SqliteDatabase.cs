using HCG.ExecutableMaker.Core;
using Microsoft.Data.Sqlite;

namespace HCG.ExecutableMaker.Infrastructure.Persistence;

/// <summary>Opens connections and applies schema migrations in order. Stores metadata only — never secrets.</summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private int _migrated;

    public SqliteDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public SqliteDatabase(IAppPaths paths) : this(paths.DatabasePath) { }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _migrated) == 0)
            await MigrateAsync(cancellationToken).ConfigureAwait(false);
        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Applies pending migrations. Returns the versions applied by this call.</summary>
    public async Task<IReadOnlyList<int>> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var applied = new List<int>();
        await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
        await using (var init = connection.CreateCommand())
        {
            init.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    description TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                """;
            await init.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var existing = new HashSet<int>();
        await using (var q = connection.CreateCommand())
        {
            q.CommandText = "SELECT version FROM schema_migrations";
            await using var reader = await q.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) existing.Add(reader.GetInt32(0));
        }

        foreach (var migration in Migrations.All.OrderBy(m => m.Version))
        {
            if (existing.Contains(migration.Version)) continue;
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var record = connection.CreateCommand())
            {
                record.Transaction = tx;
                record.CommandText = "INSERT INTO schema_migrations (version, description, applied_at) VALUES ($v, $d, $t)";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$d", migration.Description);
                record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            applied.Add(migration.Version);
        }
        Volatile.Write(ref _migrated, 1);
        return applied;
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
}
