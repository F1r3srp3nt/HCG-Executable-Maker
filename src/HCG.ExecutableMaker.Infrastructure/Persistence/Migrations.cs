namespace HCG.ExecutableMaker.Infrastructure.Persistence;

public sealed record Migration(int Version, string Description, string Sql);

/// <summary>Append-only list of schema migrations. Never edit a released migration; add a new one.</summary>
public static class Migrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new(1, "Initial schema: projects, builds, messages, artifacts, settings", """
            CREATE TABLE projects (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                source_location TEXT NOT NULL,
                workspace_path TEXT NOT NULL,
                preserved_archive_path TEXT NULL,
                imported_at TEXT NOT NULL,
                last_opened_at TEXT NULL,
                analysis_json TEXT NULL
            );

            CREATE TABLE builds (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                project_name TEXT NOT NULL,
                status TEXT NOT NULL,
                settings_json TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                exit_code INTEGER NULL,
                failure_reason TEXT NULL,
                log_path TEXT NULL,
                warning_count INTEGER NOT NULL DEFAULT 0,
                error_count INTEGER NOT NULL DEFAULT 0,
                trust_acknowledged INTEGER NOT NULL DEFAULT 0,
                toolchain_versions_json TEXT NULL
            );

            CREATE TABLE build_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                build_id TEXT NOT NULL REFERENCES builds(id) ON DELETE CASCADE,
                severity TEXT NOT NULL,
                text TEXT NOT NULL,
                code TEXT NULL,
                file TEXT NULL,
                line INTEGER NULL
            );

            CREATE TABLE artifacts (
                id TEXT PRIMARY KEY,
                build_id TEXT NOT NULL REFERENCES builds(id) ON DELETE CASCADE,
                format TEXT NOT NULL,
                kind TEXT NOT NULL,
                path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                verification TEXT NOT NULL,
                checks_json TEXT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """),
        new(2, "Indexes for history queries", """
            CREATE INDEX ix_builds_project ON builds(project_id);
            CREATE INDEX ix_builds_started ON builds(started_at DESC);
            CREATE INDEX ix_messages_build ON build_messages(build_id);
            CREATE INDEX ix_artifacts_build ON artifacts(build_id);
            """),
    ];
}
