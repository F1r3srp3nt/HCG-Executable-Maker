using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.Tests;

public class ProjectServiceTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly AppPaths _paths;
    private readonly SqliteProjectRepository _projects;
    private readonly ProjectService _service;

    public ProjectServiceTests()
    {
        _paths = new AppPaths(_t.Combine("app"));
        var db = new SqliteDatabase(_paths);
        _projects = new SqliteProjectRepository(db);
        _service = new ProjectService(new ProjectImporter(_paths, TimeProvider.System),
            new ProjectInspectionService([new DotNetProjectInspector(TimeProvider.System)], TimeProvider.System),
            _projects, new SqliteSettingsStore(db), new StaticToolchains(), _paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
    }

    public void Dispose() => _t.Dispose();

    private sealed class StaticToolchains : IToolchainService
    {
        public ToolchainReport? Latest => Fixtures.Toolchains("10.0.114");
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(Latest!);
    }

    [Fact]
    public async Task Import_inspects_and_persists_then_removal_keeps_original_source()
    {
        var src = _t.Combine("MyTool");
        _t.Write("MyTool/MyTool.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _t.Write("MyTool/Program.cs", "System.Console.WriteLine(1);");

        var result = await _service.ImportDirectoryAsync(src, null, CancellationToken.None);
        var stored = await _projects.GetAsync(result.Project.Id);
        Assert.Equal(SupportLevel.Supported, stored!.Analysis!.Support);
        Assert.Equal("MyTool", stored.Name);

        await _service.RemoveProjectAsync(stored);
        Assert.False(Directory.Exists(stored.WorkspacePath));
        Assert.Null(await _projects.GetAsync(stored.Id));
        Assert.True(File.Exists(Path.Combine(src, "MyTool.csproj")), "original source must survive workspace removal");
    }

    [Fact]
    public async Task Refuses_to_delete_anything_outside_the_workspace_directory()
    {
        var precious = _t.Combine("precious");
        _t.Write("precious/data.txt", "keep me");
        var rogue = new ProjectRecord
        {
            Id = Guid.NewGuid(), Name = "Rogue", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/elsewhere",
            WorkspacePath = precious, ImportedAt = DateTimeOffset.UtcNow,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveProjectAsync(rogue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveProjectAsync(new ProjectRecord
        {
            Id = Guid.NewGuid(), Name = "Root", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/elsewhere",
            WorkspacePath = _paths.WorkspacesDirectory, ImportedAt = DateTimeOffset.UtcNow,
        }));
        Assert.True(File.Exists(Path.Combine(precious, "data.txt")));
    }
}
