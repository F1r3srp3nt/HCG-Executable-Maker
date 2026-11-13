using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.Porting;

/// <summary>A conversion path implementation. Assessment is read-only; generation writes only into a <see cref="PortOutput"/>.</summary>
public interface IPorter
{
    PortingPath Path { get; }
    /// <summary>Plain-language list of what this path supports in this version.</summary>
    IReadOnlyList<string> Scope { get; }
    IReadOnlyList<PortSource> FindSources(SourceIndex index);
    PortingAssessment Assess(SourceIndex index, PortSource source, ToolchainReport? toolchains);
    /// <summary>Writes the port into <paramref name="output"/>; returns the plan steps and the build root (relative to the output).</summary>
    (IReadOnlyList<string> Steps, string BuildRoot) Generate(SourceIndex index, PortingAssessment assessment, PortOptions options, PortOutput output);
}

/// <summary>
/// The separate output copy of a port. Every write goes through here so the migration plan lists each added,
/// modified and unchanged file with its diff. Paths are confined to the output folder; the source is only read.
/// </summary>
public sealed class PortOutput
{
    private readonly Dictionary<string, PlannedFileChange> _changes = new(StringComparer.Ordinal);

    public string SourceRoot { get; }
    public string Root { get; }

    public PortOutput(string sourceRoot, string outputRoot)
    {
        SourceRoot = Path.GetFullPath(sourceRoot);
        Root = Path.GetFullPath(outputRoot);
        if (PathGuard.IsWithin(SourceRoot, Root) || PathGuard.IsWithin(Root, SourceRoot))
            throw new InvalidOperationException("The port output folder must be separate from the original project (neither may contain the other).");
        Directory.CreateDirectory(Root);
    }

    public IReadOnlyList<PlannedFileChange> Changes => _changes.Values.OrderBy(c => c.Kind).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();

    private string Target(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsWithin(Root, full)) throw new InvalidOperationException($"Refusing to write outside the port output: {relative}");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    /// <summary>Copies a source directory unchanged (build/cache folders skipped); files may later be modified.</summary>
    public void CopyTree(SourceIndex index, string sourceDir, string targetDir)
    {
        var prefix = sourceDir.Length == 0 ? "" : sourceDir.TrimEnd('/') + "/";
        foreach (var rel in index.Files.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var inner = rel[prefix.Length..];
            if (inner.Split('/').SkipLast(1).Any(SwiftPackageScanner.IgnoredDirectories.Contains)) continue;
            var dest = Join(targetDir, inner);
            File.Copy(index.FullPath(rel), Target(dest), overwrite: true);
            _changes[dest] = new PlannedFileChange(dest, FileChangeKind.CopiedUnchanged, "Copied from " + rel, "");
        }
    }

    /// <summary>Copies one source file unchanged (relative to the source root) to <paramref name="outputRelative"/>.</summary>
    public void CopyFile(string sourceRelative, string outputRelative)
    {
        File.Copy(Path.Combine(SourceRoot, sourceRelative.Replace('/', Path.DirectorySeparatorChar)), Target(outputRelative), overwrite: true);
        _changes[outputRelative] = new PlannedFileChange(outputRelative, FileChangeKind.CopiedUnchanged, "Copied from " + sourceRelative, "");
    }

    /// <summary>Replaces a copied file's content, recording the diff against the original source file.</summary>
    public void Modify(string outputRelative, string originalRelative, string newText, string reason)
    {
        var original = File.ReadAllText(Path.Combine(SourceRoot, originalRelative.Replace('/', Path.DirectorySeparatorChar)));
        File.WriteAllText(Target(outputRelative), newText);
        var diff = TextDiff.Unified(original, newText, originalRelative, outputRelative);
        _changes[outputRelative] = diff.Length == 0
            ? new PlannedFileChange(outputRelative, FileChangeKind.CopiedUnchanged, "Copied from " + originalRelative, "")
            : new PlannedFileChange(outputRelative, FileChangeKind.Modified, reason, diff);
    }

    public void Add(string outputRelative, string text, string reason)
    {
        File.WriteAllText(Target(outputRelative), text);
        _changes[outputRelative] = new PlannedFileChange(outputRelative, FileChangeKind.Added, reason, TextDiff.Unified(null, text, outputRelative, outputRelative));
    }

    public void AddBinary(string outputRelative, byte[] data, string reason, bool executable = false)
    {
        var path = Target(outputRelative);
        File.WriteAllBytes(path, data);
        if (executable && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        _changes[outputRelative] = new PlannedFileChange(outputRelative, FileChangeKind.Added, reason, $"Binary file ({data.Length:N0} bytes)\n");
    }

    public static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a.TrimEnd('/') + "/" + b;

    /// <summary>Relative path from directory <paramref name="fromDir"/> to <paramref name="to"/> (both '/'-separated, relative to the same root).</summary>
    public static string Relative(string fromDir, string to)
    {
        var a = fromDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var b = to.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common]) common++;
        var parts = Enumerable.Repeat("..", a.Length - common).Concat(b.Skip(common)).ToList();
        return parts.Count == 0 ? "." : string.Join('/', parts);
    }
}
