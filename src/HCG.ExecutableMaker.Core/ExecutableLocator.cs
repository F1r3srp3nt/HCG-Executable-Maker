using System.Runtime.InteropServices;

namespace HCG.ExecutableMaker.Core;

/// <summary>
/// Locates executables on PATH and in well-known install locations. GUI apps on macOS do not inherit the shell PATH,
/// so well-known locations are always searched as well.
/// </summary>
public interface IExecutableLocator
{
    string? Find(string name, IEnumerable<string>? extraCandidates = null);
}

public sealed class ExecutableLocator : IExecutableLocator
{
    private readonly Func<string, string?> _getEnv;
    private readonly Func<string, bool> _fileExists;

    public ExecutableLocator() : this(Environment.GetEnvironmentVariable, File.Exists) { }

    public ExecutableLocator(Func<string, string?> getEnv, Func<string, bool> fileExists)
    {
        _getEnv = getEnv;
        _fileExists = fileExists;
    }

    public string? Find(string name, IEnumerable<string>? extraCandidates = null)
    {
        foreach (var candidate in extraCandidates ?? [])
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate) && _fileExists(candidate))
                return candidate;
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var names = isWindows && !Path.HasExtension(name) ? new[] { name + ".exe", name + ".cmd", name + ".bat" } : new[] { name };
        var dirs = (_getEnv("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!isWindows)
            dirs.AddRange(["/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]);

        foreach (var dir in dirs.Distinct())
        {
            foreach (var n in names)
            {
                string full;
                try { full = Path.Combine(dir.Trim('"'), n); }
                catch (ArgumentException) { continue; }
                if (Path.IsPathFullyQualified(full) && _fileExists(full))
                    return full;
            }
        }
        return null;
    }

    /// <summary>Well-known .NET SDK host locations per OS.</summary>
    public static IEnumerable<string> DotNetCandidates(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var root = getEnv("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, exe);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, ".dotnet", exe);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf, "dotnet", exe);
        }
        else
        {
            yield return "/usr/local/share/dotnet/dotnet";
            yield return "/opt/homebrew/bin/dotnet";
            yield return "/usr/local/bin/dotnet";
            yield return "/usr/share/dotnet/dotnet";
            yield return "/usr/lib/dotnet/dotnet";
            yield return "/usr/bin/dotnet";
        }
    }
}
