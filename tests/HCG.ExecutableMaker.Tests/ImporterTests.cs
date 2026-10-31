using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;

namespace HCG.ExecutableMaker.Tests;

public class ImporterTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly AppPaths _paths;
    private readonly ProjectImporter _importer;

    public ImporterTests()
    {
        _paths = new AppPaths(_temp.Combine("app"));
        _importer = new ProjectImporter(_paths, TimeProvider.System);
    }

    public void Dispose() => _temp.Dispose();

    private string Zip(params (string, string, int?)[] entries) => Zips.Create(_temp.Combine($"in-{Guid.NewGuid():N}.zip"), entries);

    private void AssertNoWorkspaceLeft() =>
        Assert.Empty(Directory.Exists(_paths.WorkspacesDirectory) ? Directory.GetFileSystemEntries(_paths.WorkspacesDirectory) : []);

    [Fact]
    public async Task Extracts_valid_archive_and_preserves_original()
    {
        var zip = Zip(("MyApp/MyApp.csproj", "<Project />", null), ("MyApp/Program.cs", "class P{}", null), ("MyApp/sub/", "", null));
        var progress = new List<ImportProgress>();
        var result = await _importer.ImportZipAsync(zip, new ImportLimits(), new SyncProgress<ImportProgress>(progress.Add), CancellationToken.None);

        Assert.Equal(2, result.FileCount);
        Assert.Equal("MyApp", result.Project.Name);
        Assert.Equal(ProjectSourceKind.ZipArchive, result.Project.SourceKind);
        Assert.True(File.Exists(Path.Combine(result.Project.SourceDirectory, "MyApp", "Program.cs")));
        Assert.True(PathGuard.IsWithin(_paths.WorkspacesDirectory, result.Project.WorkspacePath));
        Assert.True(File.Exists(result.Project.PreservedArchivePath));
        Assert.Equal(File.ReadAllBytes(zip), File.ReadAllBytes(result.Project.PreservedArchivePath!));
        Assert.True(File.Exists(zip), "original archive must not be moved");
        Assert.Contains(progress, p => p.Stage == "Extracting" && p.FilesProcessed == 2 && p.TotalFiles == 2);
    }

    [Fact]
    public async Task Rejects_corrupted_archive()
    {
        var path = _temp.Write("bad.zip", "this is not a zip file at all");
        var ex = await Assert.ThrowsAsync<ImportRejectedException>(() => _importer.ImportZipAsync(path, new ImportLimits(), null, CancellationToken.None));
        Assert.Contains("not a valid ZIP", ex.Message);
        AssertNoWorkspaceLeft();
    }

    [Fact]
    public async Task Rejects_truncated_archive()
    {
        var good = Zip(("a.txt", new string('x', 10_000), null));
        var bytes = File.ReadAllBytes(good);
        var truncated = _temp.Combine("truncated.zip");
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        await Assert.ThrowsAsync<ImportRejectedException>(() => _importer.ImportZipAsync(truncated, new ImportLimits(), null, CancellationToken.None));
        AssertNoWorkspaceLeft();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("ok/../../escape.txt")]
    [InlineData("/abs/escape.txt")]
    [InlineData(@"..\escape.txt")]
    [InlineData("C:/escape.txt")]
    public async Task Rejects_path_traversal(string evilName)
    {
        var zip = Zip(("good.txt", "ok", null), (evilName, "pwned", null));
        var ex = await Assert.ThrowsAsync<ImportRejectedException>(() => _importer.ImportZipAsync(zip, new ImportLimits(), null, CancellationToken.None));
        Assert.Contains("unsafe", ex.Message);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_paths.Root, "escape.txt")));
        AssertNoWorkspaceLeft();
    }

    [Fact]
    public async Task Rejects_symlink_entries()
    {
        var zip = Zip(("link", "/etc/passwd", 0xA000 | 0x1FF), ("a.txt", "x", null));
        var ex = await Assert.ThrowsAsync<ImportRejectedException>(() => _importer.ImportZipAsync(zip, new ImportLimits(), null, CancellationToken.None));
        Assert.Contains("symbolic link", ex.Message);
        AssertNoWorkspaceLeft();
    }

    [Fact]
    public async Task Rejects_duplicate_entries_instead_of_overwriting()
    {
        var zip = Zip(("a/File.txt", "1", null), ("a/file.txt", "2", null));
        var ex = await Assert.ThrowsAsync<ImportRejectedException>(() => _importer.ImportZipAsync(zip, new ImportLimits(), null, CancellationToken.None));
        Assert.Contains("duplicate", ex.Message);
    }

    [Fact]
    public async Task Enforces_file_count_limit()
    {
        var zip = Zip(("1.txt", "a", null), ("2.txt", "b", null), ("3.txt", "c", null));
        var ex = await Assert.ThrowsAsync<ImportRejectedException>(() =>
            _importer.ImportZipAsync(zip, new ImportLimits { MaxFileCount = 2 }, null, CancellationToken.None));
        Assert.Contains("limit of 2", ex.Message);
        AssertNoWorkspaceLeft();
    }

    [Fact]
    public async Task Enforces_total_size_limit()
    {
        var zip = Zip(("a.txt", new string('a', 600), null), ("b.txt", new string('b', 600), null));
        await Assert.ThrowsAsync<ImportRejectedException>(() =>
            _importer.ImportZipAsync(zip, new ImportLimits { MaxTotalBytes = 1000 }, null, CancellationToken.None));
        AssertNoWorkspaceLeft();
    }

    [Fact]
    public async Task Enforces_per_file_limit()
    {
        var zip = Zip(("big.txt", new string('a', 5000), null));
        await Assert.ThrowsAsync<ImportRejectedException>(() =>
            _importer.ImportZipAsync(zip, new ImportLimits { MaxFileBytes = 1000 }, null, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_decompression_bomb_ratio()
    {
        var zip = Zip(("bomb.txt", new string('0', 5_000_000), null));
        var ex = await Assert.ThrowsAsync<ImportRejectedException>(() => _importer.ImportZipAsync(zip, new ImportLimits(), null, CancellationToken.None));
        Assert.Contains("compression ratio", ex.Message);
    }

    [Fact]
    public async Task Skips_macos_metadata()
    {
        var zip = Zip(("__MACOSX/._a.txt", "meta", null), ("a.txt", "x", null), (".DS_Store", "x", null));
        var result = await _importer.ImportZipAsync(zip, new ImportLimits(), null, CancellationToken.None);
        Assert.Equal(1, result.FileCount);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Cancellation_leaves_no_workspace()
    {
        var zip = Zip(("a.txt", "x", null));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _importer.ImportZipAsync(zip, new ImportLimits(), null, cts.Token));
        AssertNoWorkspaceLeft();
    }

    [Fact]
    public async Task Directory_import_copies_snapshot_and_excludes_build_output()
    {
        var src = _temp.Combine("proj");
        _temp.Write("proj/App.csproj", "<Project />");
        _temp.Write("proj/bin/Debug/App.dll", "binary");
        _temp.Write("proj/obj/project.assets.json", "{}");
        _temp.Write("proj/.git/HEAD", "ref");
        _temp.Write("proj/src/Main.cs", "class A{}");

        var result = await _importer.ImportDirectoryAsync(src, new ImportLimits(), null, CancellationToken.None);

        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(result.Project.SourceDirectory, "src", "Main.cs")));
        Assert.False(Directory.Exists(Path.Combine(result.Project.SourceDirectory, "bin")));
        Assert.False(Directory.Exists(Path.Combine(result.Project.SourceDirectory, ".git")));
        Assert.Contains(result.Warnings, w => w.Contains("Excluded"));
        Assert.True(File.Exists(Path.Combine(src, "bin", "Debug", "App.dll")), "original source must be untouched");
    }

    [UnixFact]
    public async Task Directory_import_rejects_symlink_escape()
    {
        var secret = _temp.Write("outside/secret.txt", "top secret");
        _temp.Write("proj/App.csproj", "<Project />");
        File.CreateSymbolicLink(_temp.Combine("proj", "leak.txt"), secret);
        _temp.Write("proj/inner.txt", "inner");
        File.CreateSymbolicLink(_temp.Combine("proj", "alias.txt"), _temp.Combine("proj", "inner.txt"));

        var result = await _importer.ImportDirectoryAsync(_temp.Combine("proj"), new ImportLimits(), null, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(result.Project.SourceDirectory, "leak.txt")));
        Assert.Contains(result.Warnings, w => w.Contains("outside the project") && w.Contains("leak.txt"));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(result.Project.SourceDirectory, "alias.txt")));
    }

    [Fact]
    public async Task Directory_import_refuses_app_data_directory()
    {
        _paths.EnsureCreated();
        File.WriteAllText(Path.Combine(_paths.Root, "x.txt"), "x");
        await Assert.ThrowsAsync<ImportRejectedException>(() => _importer.ImportDirectoryAsync(_paths.Root, new ImportLimits(), null, CancellationToken.None));
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
