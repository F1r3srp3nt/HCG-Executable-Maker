using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;

namespace HCG.ExecutableMaker.Tests;

public class PersistenceTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly SqliteDatabase _db;

    public PersistenceTests() => _db = new SqliteDatabase(_t.Combine("data", "test.db"));
    public void Dispose() => _t.Dispose();

    private static ProjectRecord Project(string name = "App") => new()
    {
        Id = Guid.NewGuid(), Name = name, SourceKind = ProjectSourceKind.ZipArchive, SourceLocation = "/src/app.zip",
        WorkspacePath = "/ws/x", PreservedArchivePath = "/ws/x/original/app.zip", ImportedAt = DateTimeOffset.UtcNow,
        Analysis = new ProjectAnalysis
        {
            InspectorId = "dotnet", ProjectName = name, Language = "C#", Framework = ".NET 10.0", ApplicationType = "Console application",
            Support = SupportLevel.Supported, SupportSummary = "ok", AnalyzedAt = DateTimeOffset.UtcNow,
            RecommendedFormats = [PackagingFormat.PortableZip],
        },
    };

    private static BuildRecord Build(Guid projectId, BuildStatus status) => new()
    {
        Id = Guid.NewGuid(), ProjectId = projectId, ProjectName = "App", Status = status, StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow.AddSeconds(42),
        Settings = BuildPlannerTests.Settings("/out"), WarningCount = 1, TrustAcknowledged = true, ExitCode = 0, LogPath = "/logs/b.log",
        ToolchainVersions = new Dictionary<string, string> { ["dotnet-sdk"] = "10.0.114" },
        Messages = [new BuildMessage(DiagnosticSeverity.Warning, "careful", "CS0168", "/a.cs", 3)],
        Artifacts =
        [
            new ArtifactRecord
            {
                Id = Guid.NewGuid(), BuildId = Guid.Empty, Format = PackagingFormat.PortableZip, Kind = ArtifactKind.File, Path = "/out/a.zip",
                SizeBytes = 1234, Sha256 = new string('a', 64), Verification = VerificationStatus.Passed, CreatedAt = DateTimeOffset.UtcNow,
                Checks = [new VerificationCheck("Archive integrity", VerificationStatus.Passed, "ok")],
            },
        ],
    };

    [Fact]
    public async Task Migrations_apply_once_in_order()
    {
        Assert.Equal([1, 2], await _db.MigrateAsync());
        Assert.Empty(await _db.MigrateAsync());
        Assert.Equal(2, await _db.GetSchemaVersionAsync());
        var reopened = new SqliteDatabase(_db.DatabasePath);
        Assert.Empty(await reopened.MigrateAsync());
    }

    [Fact]
    public async Task Projects_round_trip_including_analysis()
    {
        var repo = new SqliteProjectRepository(_db);
        var p = Project();
        await repo.UpsertAsync(p);
        var loaded = await repo.GetAsync(p.Id);
        Assert.NotNull(loaded);
        Assert.Equal(p.SourceLocation, loaded!.SourceLocation);
        Assert.Equal(p.PreservedArchivePath, loaded.PreservedArchivePath);
        Assert.Equal(SupportLevel.Supported, loaded.Analysis!.Support);
        Assert.Equal([PackagingFormat.PortableZip], loaded.Analysis.RecommendedFormats);

        p.Name = "Renamed";
        await repo.UpsertAsync(p);
        Assert.Equal("Renamed", (await repo.ListAsync()).Single().Name);
    }

    [Fact]
    public async Task Builds_round_trip_with_messages_artifacts_and_settings()
    {
        var projects = new SqliteProjectRepository(_db);
        var builds = new SqliteBuildRepository(_db);
        var p = Project();
        await projects.UpsertAsync(p);
        var b = Build(p.Id, BuildStatus.Succeeded);
        await builds.InsertAsync(b);

        var loaded = (await builds.GetAsync(b.Id))!;
        Assert.Equal(BuildStatus.Succeeded, loaded.Status);
        Assert.InRange(loaded.Duration!.Value.TotalSeconds, 41.99, 42.01);
        Assert.Equal("osx-arm64", loaded.Settings.RuntimeIdentifier);
        Assert.Equal("My App", loaded.Settings.Identity.ApplicationName);
        Assert.Equal("CS0168", loaded.Messages.Single().Code);
        Assert.Equal(new string('a', 64), loaded.Artifacts.Single().Sha256);
        Assert.Equal("Archive integrity", loaded.Artifacts.Single().Checks.Single().Name);
        Assert.Equal("10.0.114", loaded.ToolchainVersions["dotnet-sdk"]);
        Assert.True(loaded.TrustAcknowledged);

        loaded.Status = BuildStatus.Failed;
        loaded.Messages.Add(new BuildMessage(DiagnosticSeverity.Error, "boom"));
        loaded.Artifacts.Clear();
        await builds.UpdateAsync(loaded);
        var again = (await builds.GetAsync(b.Id))!;
        Assert.Equal(BuildStatus.Failed, again.Status);
        Assert.Equal(2, again.Messages.Count);
        Assert.Empty(again.Artifacts);
    }

    [Fact]
    public async Task Statistics_listing_and_deletion()
    {
        var projects = new SqliteProjectRepository(_db);
        var builds = new SqliteBuildRepository(_db);
        var p = Project();
        await projects.UpsertAsync(p);
        await builds.InsertAsync(Build(p.Id, BuildStatus.Succeeded));
        await builds.InsertAsync(Build(p.Id, BuildStatus.Failed));
        var cancelled = Build(p.Id, BuildStatus.Cancelled);
        await builds.InsertAsync(cancelled);

        var stats = await builds.GetStatisticsAsync();
        Assert.Equal(new BuildStatistics(1, 3, 1, 1, 1, 3), stats);
        Assert.Equal(2, (await builds.ListAsync(limit: 2)).Count);
        Assert.Equal(3, (await builds.ListArtifactsAsync()).Count);

        await builds.DeleteAsync(cancelled.Id);
        Assert.Equal(2, (await builds.ListAsync(p.Id)).Count);
        await builds.DeleteAllAsync();
        Assert.Empty(await builds.ListAsync());
        Assert.Empty(await builds.ListArtifactsAsync());
    }

    [Fact]
    public async Task Deleting_project_cascades_history()
    {
        var projects = new SqliteProjectRepository(_db);
        var builds = new SqliteBuildRepository(_db);
        var p = Project();
        await projects.UpsertAsync(p);
        await builds.InsertAsync(Build(p.Id, BuildStatus.Succeeded));
        await projects.DeleteAsync(p.Id);
        Assert.Empty(await builds.ListAsync());
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        var store = new SqliteSettingsStore(_db);
        Assert.Equal(30, (await store.LoadAsync()).BuildTimeoutMinutes);
        await store.SaveAsync(new AppSettings { BuildTimeoutMinutes = 45, ImportLimits = new ImportLimits { MaxFileCount = 10 }, LastPublisher = "HCG" });
        var s = await store.LoadAsync();
        Assert.Equal(45, s.BuildTimeoutMinutes);
        Assert.Equal(10, s.ImportLimits.MaxFileCount);
        Assert.Equal("HCG", s.LastPublisher);
    }

    [Fact]
    public async Task Schema_has_no_secret_columns()
    {
        await using var c = await _db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(sql, ' ') FROM sqlite_master WHERE type = 'table'";
        var ddl = ((string)(await cmd.ExecuteScalarAsync())!).ToLowerInvariant();
        foreach (var word in new[] { "password", "private_key", "secret", "certificate", "token" })
            Assert.DoesNotContain(word, ddl);
    }
}
