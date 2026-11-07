using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Packagers;

public static class FileOps
{
    public static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, dest, overwrite: false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dest, File.GetUnixFileMode(file));
        }
    }

    public static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
            | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    /// <summary>Files that must be executable in Unix distributions (the app host and .NET's crash dump helper).</summary>
    public static bool IsUnixExecutable(string relativePath, string executableFileName, TargetPlatform target)
    {
        if (target == TargetPlatform.Windows) return false;
        var name = Path.GetFileName(relativePath);
        return string.Equals(name, executableFileName, StringComparison.Ordinal) || name == "createdump";
    }
}
