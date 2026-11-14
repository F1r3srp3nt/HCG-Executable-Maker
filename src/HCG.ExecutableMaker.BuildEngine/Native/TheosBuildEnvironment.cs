using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Native;

/// <summary>
/// The one toolchain configuration a Theos build uses, resolved from the same toolchain report the inspection showed:
/// absolute paths of Theos, GNU make, ldid and perl, and the environment passed to <c>gmake</c> and inherited by every
/// recipe shell it starts.
/// </summary>
/// <remarks>
/// Why this exists: an app started from Finder or the Dock gets launchd's PATH (<c>/usr/bin:/bin:/usr/sbin:/sbin</c>).
/// Detection finds ldid in /opt/homebrew/bin by looking there explicitly, but Theos' signing recipe runs a bare
/// <c>ldid -S …</c> in /bin/bash, which searches only PATH — so the build failed with "ldid: command not found" (127)
/// after a successful compile and link. Now:
/// <list type="bullet">
/// <item>PATH keeps the sanitized parent PATH first (what the project would see anyway), then the system directories,
/// then the directory of each detected tool that is not reachable otherwise — appended, so no tool the parent PATH
/// already resolves is shadowed by a different copy.</item>
/// <item><c>TARGET_CODESIGN</c> is set in the environment to the detected ldid. Theos (makefiles/targets/_common/
/// darwin_head.mk) only looks ldid up on PATH when TARGET_CODESIGN is empty, and common.mk exports it; an environment
/// value — unlike a command-line one — still lets a project's own Makefile assignment win.</item>
/// <item>Nothing else changes: the sanitization that removed secret-like variables already happened.</item>
/// </list>
/// </remarks>
public sealed record TheosBuildEnvironment(
    string Theos,
    string Make,
    string Ldid,
    string Perl,
    IReadOnlyDictionary<string, string> Environment,
    string? Lzma = null)
{
    /// <summary>
    /// Where tools installed with Homebrew live. Detection already looks there for gmake and ldid; the .deb compressor is
    /// looked up the same way, because an app started from Finder does not have these directories on PATH.
    /// </summary>
    public static readonly IReadOnlyList<string> PackageManagerDirectories = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <summary>
    /// Theos packs the .deb with dm.pl, which runs <c>lzma -c</c> by name (Theos' default compression,
    /// THEOS_PLATFORM_DEB_COMPRESSION_TYPE=lzma). macOS has no lzma command; Homebrew's xz provides one.
    /// </summary>
    public IReadOnlyList<string> Warnings => Lzma is null
        ? ["Theos packs the .deb with \"lzma -c\" (its default compression), and no lzma command was found on PATH or in " +
           string.Join(", ", PackageManagerDirectories) + ". Unless the project sets THEOS_PLATFORM_DEB_COMPRESSION_TYPE to another compressor, " +
           "the packaging step will fail with \"lzma: command not found\". On a Mac, the xz package (brew install xz) provides lzma."]
        : [];

    /// <summary>Directories every Unix build shell needs (bash, rsync, cp, xcrun, codesign_allocate's caller …).</summary>
    public static readonly IReadOnlyList<string> SystemDirectories = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    /// <summary>The commands Theos' recipes start by name, with the detected executable each must resolve to.</summary>
    public IReadOnlyList<(string Command, string Expected)> RecipeCommands =>
        Lzma is null ? [("ldid", Ldid), ("perl", Perl)] : [("ldid", Ldid), ("perl", Perl), ("lzma", Lzma)];

    public string Path => Environment.GetValueOrDefault("PATH") ?? "";

    /// <summary>Null when a required tool was not detected (the readiness check reports which one).</summary>
    public static TheosBuildEnvironment? Resolve(ToolchainReport report, IReadOnlyDictionary<string, string> sanitized, IReadOnlyList<string>? packageManagerDirectories = null)
    {
        string? Exe(string id) => report.Find(id) is { IsAvailable: true, ExecutablePath: { } p } ? p : null;
        if (Exe(ToolchainIds.Theos) is not { } theos || Exe(ToolchainIds.TheosMake) is not { } make || Exe(ToolchainIds.Ldid) is not { } ldid
            || Exe(ToolchainIds.Perl) is not { } perl)
            return null;
        var env = new Dictionary<string, string>(sanitized, StringComparer.Ordinal)
        {
            ["THEOS"] = theos,
            ["TARGET_CODESIGN"] = ldid,
        };
        // The compressor dm.pl runs: whatever the parent PATH or the system directories give, else a package manager's copy.
        var basePath = BuildPath(sanitized.GetValueOrDefault("PATH"), []);
        var lzma = Which("lzma", basePath) ?? Which("lzma", string.Join(':', packageManagerDirectories ?? PackageManagerDirectories));
        env["PATH"] = BuildPath(sanitized.GetValueOrDefault("PATH"), lzma is null ? [ldid, perl, make] : [ldid, perl, make, lzma]);
        return new TheosBuildEnvironment(theos, make, ldid, perl, env, lzma);
    }

    /// <summary>Parent PATH (order kept), then the system directories, then each tool's own directory when it is not already reachable by name.</summary>
    public static string BuildPath(string? parentPath, IEnumerable<string> tools)
    {
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d) || !System.IO.Path.IsPathRooted(d)) return;   // "" or "." would mean the build folder
            var clean = d.TrimEnd('/');
            if (clean.Length == 0) clean = "/";
            if (!dirs.Contains(clean, StringComparer.Ordinal)) dirs.Add(clean);
        }
        foreach (var d in (parentPath ?? "").Split(':')) Add(d);
        foreach (var d in SystemDirectories) Add(d);
        foreach (var tool in tools)
        {
            var name = System.IO.Path.GetFileName(tool);
            var resolved = Which(name, string.Join(':', dirs));
            if (resolved is null || !SameFile(resolved, tool)) Add(System.IO.Path.GetDirectoryName(tool));
        }
        return string.Join(':', dirs);
    }

    /// <summary>What a POSIX shell would run for <paramref name="command"/> with this PATH (first executable file).</summary>
    public static string? Which(string command, string path)
    {
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir, command);
            if (File.Exists(candidate) && IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsExecutable(string file) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    public static bool SameFile(string a, string b)
    {
        static string Real(string p)
        {
            try { return new FileInfo(p).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? System.IO.Path.GetFullPath(p); }
            catch (IOException) { return System.IO.Path.GetFullPath(p); }
        }
        return string.Equals(Real(a), Real(b), StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks, in a shell started with exactly this environment (as gmake's recipes are), that every command the recipes
    /// start by name resolves to the detected executable. Returns the problems found (empty = ready).
    /// </summary>
    public async Task<IReadOnlyList<string>> VerifyInChildShellAsync(IBuildWorker worker, CancellationToken ct)
    {
        var shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        var script = string.Join("; ", RecipeCommands.Select(c => $"printf '%s=%s\\n' {c.Command} \"$(command -v {c.Command} || true)\""));
        var lines = new List<string>();
        var result = await worker.RunAsync(new ProcessSpec
        {
            Executable = shell, Arguments = ["-c", script], Environment = Environment, Timeout = TimeSpan.FromSeconds(20),
        }, (stream, text) => { if (stream == OutputStream.StdOut) lock (lines) lines.Add(text); }, ct).ConfigureAwait(false);
        var problems = new List<string>();
        if (!result.Succeeded) problems.Add($"The build's shell ({shell}) could not be started to check the tools (exit {result.ExitCode}).");
        foreach (var (command, expected) in RecipeCommands)
        {
            var found = lines.FirstOrDefault(l => l.StartsWith(command + "=", StringComparison.Ordinal))?[(command.Length + 1)..].Trim();
            if (string.IsNullOrEmpty(found))
                problems.Add($"{command} was detected at {expected}, but the build's shell cannot find it on PATH ({Path}).");
            else if (!SameFile(found, expected))
                problems.Add($"{command} in the build's shell is {found}, but HCG detected and checked {expected}. The build would use a different {command} than the one shown.");
        }
        return problems;
    }
}
