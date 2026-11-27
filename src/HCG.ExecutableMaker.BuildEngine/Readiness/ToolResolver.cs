using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.BuildEngine.Readiness;

/// <summary>A location the person chose (per project profile or for all projects), tried before automatic detection.</summary>
public sealed record PreferredTool(string Path, string Source);

/// <summary>What resolving one tool found: the installation to use (or why none fits) and every installation seen.</summary>
public sealed record ToolResolution(ReadinessState State, ToolInstallation? Selected, IReadOnlyList<ToolInstallation> Found, string Detail);

/// <summary>The host facts resolution depends on; replaceable in tests.</summary>
public sealed record ToolHost
{
    public TargetPlatform Platform { get; init; } = HostInfo.CurrentPlatform;
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public Func<string, string?> GetEnvironment { get; init; } = Environment.GetEnvironmentVariable;
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    public Func<string, IEnumerable<string>> ListDirectory { get; init; } = SafeList;
    public Func<string, string?> LinkTarget { get; init; } = FinalTarget;
    public Func<string, bool> IsExecutable { get; init; } = Executable;
    /// <summary>macOS: Xcode or the Command Line Tools are installed, so /usr/bin shims can run without an install dialog.</summary>
    public Func<bool> AppleDeveloperToolsPresent { get; init; } = DeveloperToolsPresent;
    /// <summary>HCG runs as root (Homebrew refuses root, and HCG never runs installers that way).</summary>
    public Func<bool> IsRoot { get; init; } = () => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    public static ToolHost Current { get; } = new();

    private static IEnumerable<string> SafeList(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? FinalTarget(string path)
    {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool Executable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try { return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// xcode-select -p names a developer directory that exists. Checked without running the /usr/bin shims, which would
    /// open Apple's "install the command line developer tools" dialog on a Mac without them.
    /// </summary>
    private static bool DeveloperToolsPresent()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (Environment.GetEnvironmentVariable("DEVELOPER_DIR") is { Length: > 0 } dev && Directory.Exists(dev)) return true;
        return Directory.Exists("/Library/Developer/CommandLineTools/usr/bin") || Directory.Exists("/Applications/Xcode.app/Contents/Developer")
            || File.Exists("/var/db/xcode_select_link") && Directory.Exists(new FileInfo("/var/db/xcode_select_link").LinkTarget ?? "/nonexistent");
    }
}

/// <summary>
/// Finds a tool the way a build will use it: a location chosen for the project, then one chosen for all projects, then
/// PATH, then the places installers put it (apps opened from Finder do not inherit the shell PATH, so Homebrew on Apple
/// Silicon and Intel, the official Go installer, rustup, nvm and the like are searched explicitly). Each candidate's
/// version is read with a harmless probe outside the project (for Go with GOTOOLCHAIN=local, so the probe never
/// downloads a toolchain). Among several installations the first one that meets the project's minimum is used, so an
/// older installation that another project relies on is never replaced or upgraded.
/// </summary>
public sealed class ToolResolver(IProcessRunner runner, ToolHost? host = null)
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private const int MaxProbes = 10;
    private readonly ToolHost _host = host ?? ToolHost.Current;

    public ToolHost Host => _host;

    public async Task<ToolResolution> ResolveAsync(ToolDefinition tool, string? minimumVersion, IReadOnlyList<PreferredTool> preferred, CancellationToken ct)
    {
        // A chosen location is used as chosen: if it is wrong the build must not silently fall back to something else.
        foreach (var p in preferred)
        {
            var problem = CheckChosen(tool, p.Path);
            if (problem is not null)
                return new ToolResolution(ReadinessState.InvalidPath, null, [], $"{tool.DisplayName} location {p.Path} ({p.Source}): {problem}");
            var (version, error) = await ProbeAsync(tool, p.Path, ct).ConfigureAwait(false);
            var inst = new ToolInstallation(p.Path, version, p.Source);
            if (error is not null)
                return new ToolResolution(ReadinessState.InvalidPath, null, [inst], $"{tool.DisplayName} at {p.Path} ({p.Source}) did not run: {error}");
            if (!Meets(version, minimumVersion, tool))
                return new ToolResolution(ReadinessState.IncompatibleVersion, null, [inst],
                    $"{tool.DisplayName} {version ?? "(version unknown)"} at {p.Path} ({p.Source}) is older than the {minimumVersion} this build needs.");
            return new ToolResolution(ReadinessState.Ready, inst, [inst], Ready(tool, inst));
        }

        var found = new List<ToolInstallation>();
        var shimBlocked = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Candidates(tool))
        {
            var real = _host.LinkTarget(path) ?? path;
            if (!seen.Add(real)) continue;
            if (tool.AppleShim && _host.Platform == TargetPlatform.MacOS && path.StartsWith("/usr/bin/", StringComparison.Ordinal) && !_host.AppleDeveloperToolsPresent())
            {
                shimBlocked = true; // running it would open Apple's install dialog
                continue;
            }
            if (found.Count >= MaxProbes) break;
            var (version, error) = await ProbeAsync(tool, path, ct).ConfigureAwait(false);
            if (error is not null) continue; // broken or a non-working shim: not an installation
            found.Add(new ToolInstallation(path, version, Source(path, real)));
        }

        if (found.Count == 0)
        {
            var detail = shimBlocked
                ? $"{tool.DisplayName} is provided by Apple's developer tools, which are not installed on this Mac."
                : $"{tool.DisplayName} was not found on PATH or in {Where(tool)}.";
            return new ToolResolution(shimBlocked ? ReadinessState.SetupRequired : ReadinessState.NotFound, null, [], detail);
        }
        var selected = found.FirstOrDefault(f => Meets(f.Version, minimumVersion, tool));
        if (selected is null)
            return new ToolResolution(ReadinessState.IncompatibleVersion, null, found,
                $"This build needs {tool.DisplayName} {minimumVersion} or newer; found " +
                string.Join(", ", found.Select(f => $"{f.Version ?? "unknown version"} at {f.Path}")) + ".");
        return new ToolResolution(ReadinessState.Ready, selected, found, Ready(tool, selected));
    }

    /// <summary>Checks a location the person picked before it is saved: right tool, runs, reports a version.</summary>
    public async Task<ToolResolution> VerifyChosenAsync(ToolDefinition tool, string path, string? minimumVersion, CancellationToken ct) =>
        await ResolveAsync(tool, minimumVersion, [new PreferredTool(NormalizeChosen(tool, path), "Chosen in HCG")], ct).ConfigureAwait(false);

    /// <summary>A folder (the bin folder or the install root) or the executable itself → the executable.</summary>
    public string NormalizeChosen(ToolDefinition tool, string path)
    {
        var p = path.Trim().TrimEnd('/', '\\');
        foreach (var exe in tool.Executables.SelectMany(ExecutableNames))
        {
            if (_host.FileExists(Path.Combine(p, exe))) return Path.Combine(p, exe);
            if (_host.FileExists(Path.Combine(p, "bin", exe))) return Path.Combine(p, "bin", exe);
        }
        return p;
    }

    private string? CheckChosen(ToolDefinition tool, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return "not an absolute path.";
        if (!_host.FileExists(path)) return "the file does not exist.";
        var name = Path.GetFileName(path);
        if (!tool.Executables.SelectMany(ExecutableNames).Any(n => string.Equals(n, name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            return $"this is {name}, not {string.Join(" or ", tool.Executables)}.";
        if (!_host.IsExecutable(path)) return "the file is not executable.";
        return null;
    }

    private IEnumerable<string> ExecutableNames(string exe) =>
        _host.Platform == TargetPlatform.Windows && !Path.HasExtension(exe) ? [exe + ".exe", exe + ".cmd"] : [exe];

    /// <summary>PATH first (in order), then the catalogue's install locations, expanded ("~", "*").</summary>
    public IEnumerable<string> Candidates(ToolDefinition tool)
    {
        var sep = _host.Platform == TargetPlatform.Windows ? ';' : ':';
        foreach (var dir in (_host.GetEnvironment("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
            foreach (var exe in tool.Executables.SelectMany(ExecutableNames))
            {
                string full;
                try { full = Path.Combine(dir.Trim('"'), exe); }
                catch (ArgumentException) { continue; }
                if (Path.IsPathFullyQualified(full) && _host.FileExists(full)) yield return full;
            }
        foreach (var pattern in tool.Locations(_host.Platform))
            foreach (var p in Expand(pattern))
                if (_host.FileExists(p)) yield return p;
    }

    /// <summary>"~" → home; a "*" segment matches entries of that folder (newest-looking names first).</summary>
    public IEnumerable<string> Expand(string pattern)
    {
        var p = pattern.StartsWith('~') ? _host.Home + pattern[1..] : pattern;
        if (!p.Contains('*')) return [p];
        var sepChars = new[] { '/', '\\' };
        var star = p.IndexOf('*');
        var dirEnd = p.LastIndexOfAny(sepChars, star);
        var nextSep = p.IndexOfAny(sepChars, star);
        var dir = p[..dirEnd];
        var segment = nextSep < 0 ? p[(dirEnd + 1)..] : p[(dirEnd + 1)..nextSep];
        var rest = nextSep < 0 ? "" : p[nextSep..];
        var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(segment).Replace(@"\*", ".*") + "$");
        return _host.ListDirectory(dir)
            .Where(e => regex.IsMatch(Path.GetFileName(e)))
            .OrderByDescending(e => Path.GetFileName(e), Comparer<string>.Create((a, b) => SwiftCapabilities.Compare(Digits(a), Digits(b))))
            .SelectMany(e => Expand(e + rest));

        static string Digits(string s) => new(s.SkipWhile(c => !char.IsDigit(c)).ToArray());
    }

    private async Task<(string? Version, string? Error)> ProbeAsync(ToolDefinition tool, string path, CancellationToken ct)
    {
        if (tool.VersionArguments.Count == 0)
            return _host.IsExecutable(path) ? (null, null) : (null, "not executable");
        string? lastError = null;
        foreach (var args in tool.VersionArguments)
        {
            // The build environment without secrets (the same scrubbing builds get), with the tool's folder first on PATH.
            var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var kv in Security.BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment) env[kv.Key] = kv.Value;
            env["PATH"] = string.Join(_host.Platform == TargetPlatform.Windows ? ';' : ':', new[] { Path.GetDirectoryName(path), _host.GetEnvironment("PATH") }.Where(s => !string.IsNullOrEmpty(s)));
            env["LANG"] = "C";
            if (_host.Platform != TargetPlatform.Windows) env["HOME"] = _host.Home;
            foreach (var kv in tool.ProbeEnvironment) env[kv.Key] = kv.Value;
            try
            {
                var (result, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec
                {
                    Executable = path, Arguments = args, Timeout = ProbeTimeout, Environment = env,
                    // Never inside a project: `go version` in a module that names a newer toolchain would fetch it.
                    WorkingDirectory = Path.GetTempPath(),
                }, ct).ConfigureAwait(false);
                var text = stdout + "\n" + stderr;
                if (result.Succeeded && Version(tool, text) is { } v) return (v, null);
                if (result.Succeeded && tool.Generic) return (null, null);
                lastError = result.TimedOut ? "the version check timed out" : FirstLine(stderr) ?? FirstLine(stdout) ?? $"exit code {result.ExitCode}";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                lastError = ex.Message;
            }
        }
        return (null, lastError ?? "did not report a version");
    }

    /// <summary>The version a probe printed (the highest one for tools that list several).</summary>
    public static string? Version(ToolDefinition tool, string output)
    {
        var versions = tool.VersionPattern.Matches(output)
            .Select(m => m.Groups.Values.Skip(1).FirstOrDefault(g => g.Success && g.Value.Length > 0)?.Value)
            .OfType<string>().ToList();
        if (versions.Count == 0) return null;
        return tool.HighestVersion ? versions.OrderByDescending(v => v, Comparer<string>.Create(SwiftCapabilities.Compare)).First() : versions[0];
    }

    public static bool Meets(string? version, string? minimum, ToolDefinition tool) =>
        minimum is null || version is not null && SwiftCapabilities.Compare(version, minimum) >= 0;

    private string Source(string path, string real)
    {
        var s = ToolCatalog.SourceOf(path, real == path ? null : real, _host.Home);
        var onPath = (_host.GetEnvironment("PATH") ?? "").Split(_host.Platform == TargetPlatform.Windows ? ';' : ':')
            .Any(d => d.Length > 0 && string.Equals(Path.GetDirectoryName(path), d.TrimEnd('/', '\\'), StringComparison.Ordinal));
        return s.Length == 0 ? (onPath ? "PATH" : "install location") : onPath ? s + " · on PATH" : s;
    }

    private string Where(ToolDefinition tool)
    {
        var places = tool.Locations(_host.Platform).Select(l => Path.GetDirectoryName(l) ?? l).Distinct().Take(6).ToList();
        return places.Count == 0 ? "the usual install locations" : string.Join(", ", places);
    }

    private static string Ready(ToolDefinition tool, ToolInstallation i) =>
        $"{tool.DisplayName} {i.Version ?? ""}".TrimEnd() + $" at {i.Path}" + (i.Source.Length > 0 ? $" ({i.Source})" : "");

    private static string? FirstLine(string? s) => s?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}
