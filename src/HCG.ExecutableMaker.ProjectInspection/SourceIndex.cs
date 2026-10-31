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
        "bin", "obj", ".git", ".hg", ".svn", ".vs", ".idea", "node_modules", "TestResults", "packages", "__MACOSX",
    };

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
                    if (depth < MaxDepth && !Skipped.Contains(e.Name)) stack.Push((e.FullName, depth + 1));
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

    public IEnumerable<string> WithExtension(params string[] extensions) =>
        Files.Where(f => extensions.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)));

    public IEnumerable<string> Named(params string[] names) =>
        Files.Where(f => names.Any(n => string.Equals(FileName(f), n, StringComparison.OrdinalIgnoreCase)));

    public string FullPath(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    public static string FileName(string relative) => relative[(relative.LastIndexOf('/') + 1)..];

    public static int Depth(string relative) => relative.Count(c => c == '/');
}
