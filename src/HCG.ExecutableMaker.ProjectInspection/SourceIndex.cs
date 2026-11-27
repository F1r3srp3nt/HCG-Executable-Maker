namespace HCG.ExecutableMaker.ProjectInspection;

/// <summary>
/// A read-only listing of a source tree, built once and shared by all inspectors. Skips build outputs and VCS folders
/// and never follows links.
/// </summary>
public sealed class SourceIndex
{
    public const int MaxFiles = 200_000;
    public const int MaxDepth = 16;

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".hg", ".svn", ".vs", ".idea", "node_modules", "TestResults", "__MACOSX",
    };

    /// <summary>
    /// NuGet's legacy restore folder (packages/ with *.nupkg files or repositories.config) is skipped. A folder merely
    /// named Packages is not: Swift projects keep their local packages there (Packages/Core/Package.swift).
    /// </summary>
    internal static bool IsNuGetPackagesFolder(DirectoryInfo dir)
    {
        if (!dir.Name.Equals("packages", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (File.Exists(Path.Combine(dir.FullName, "repositories.config"))) return true;
            if (dir.EnumerateFiles("*.nupkg", SearchOption.TopDirectoryOnly).Any()) return true;
            foreach (var sub in dir.EnumerateDirectories().Take(50))
                if (sub.EnumerateFiles("*.nupkg", SearchOption.TopDirectoryOnly).Any()) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private SourceIndex(string root, IReadOnlyList<string> files, bool truncated)
    {
        Root = root;
        Files = files;
        Truncated = truncated;
    }

    public string Root { get; }
    /// <summary>Relative paths with '/' separators.</summary>
    public IReadOnlyList<string> Files { get; }
    public bool Truncated { get; }

    public static SourceIndex Build(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var files = new List<string>();
        var truncated = false;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (e.LinkTarget is not null || e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (e is DirectoryInfo)
                {
                    if (depth < MaxDepth && !Skipped.Contains(e.Name) && !IsNuGetPackagesFolder((DirectoryInfo)e)) stack.Push((e.FullName, depth + 1));
                }
                else
                {
                    files.Add(Path.GetRelativePath(root, e.FullName).Replace('\\', '/'));
                    if (files.Count >= MaxFiles) { truncated = true; break; }
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        return new SourceIndex(root, files, truncated);
    }

    /// <summary>
    /// Folder names that hold test fixtures: sample projects a repository's own tests build. They are never offered as
    /// the repository's app (HCG Executable Maker's tests/Fixtures holds iOS, Android, Theos and Electron samples).
    /// </summary>
    public static readonly IReadOnlySet<string> FixtureDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Fixtures", "__fixtures__", "TestFixtures", "test-fixtures", "testdata", "test_data",
    };

    /// <summary>True when a file lies inside a test-fixture folder of this tree (the imported root itself never counts).</summary>
    public static bool IsTestFixture(string relative) => relative.Split('/').SkipLast(1).Any(FixtureDirectories.Contains);

    private SourceIndex? _withoutFixtures;

    /// <summary>This listing without files inside test-fixture folders (the same instance when there are none).</summary>
    public SourceIndex WithoutTestFixtures()
    {
        if (_withoutFixtures is not null) return _withoutFixtures;
        var kept = Files.Where(f => !IsTestFixture(f)).ToList();
        return _withoutFixtures = kept.Count == Files.Count ? this : new SourceIndex(Root, kept, Truncated);
    }

    public IEnumerable<string> WithExtension(params string[] extensions) =>
        Files.Where(f => extensions.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)));

    public IEnumerable<string> Named(params string[] names) =>
        Files.Where(f => names.Any(n => string.Equals(FileName(f), n, StringComparison.OrdinalIgnoreCase)));

    public string FullPath(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    public static string FileName(string relative) => relative[(relative.LastIndexOf('/') + 1)..];

    public static int Depth(string relative) => relative.Count(c => c == '/');
}
