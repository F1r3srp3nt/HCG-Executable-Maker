using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// Finds JDKs without relying on the login shell (so it works when HCG is started from Finder, where PATH and
/// JAVA_HOME are not set): a JDK saved in HCG, JAVA_HOME, Android Studio's bundled JDK, macOS's JVM registry,
/// Homebrew, SDKMAN and the usual system folders — and only then <c>java</c> on PATH. Every candidate is validated by
/// running it (<c>java -XshowSettings:properties -version</c>, stdout and stderr both read), so a macOS
/// <c>/usr/bin/java</c> stub without a JDK behind it, a JRE without javac or a broken shim is never reported as ready.
/// </summary>
public sealed partial class JavaDiscovery(IProcessRunner runner)
{
    public const string SetupUrl = "https://developer.android.com/build/jdks";
    public const int RecommendedMajor = 17;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Everything discovery depends on, so tests can describe a machine (including a Finder launch with no shell variables).</summary>
    public sealed record Context
    {
        public string? SavedHome { get; init; }
        public Func<string, string?> GetEnv { get; init; } = Environment.GetEnvironmentVariable;
        public string UserHome { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public TargetPlatform Platform { get; init; } = HostInfo.CurrentPlatform;
        /// <summary>Folder roots that stand in for "/" (tests); empty = the real file system.</summary>
        public string RootPrefix { get; init; } = "";
        /// <summary>`java` found on PATH, if any.</summary>
        public string? PathJava { get; init; }
        /// <summary>JDK homes reported by <c>/usr/libexec/java_home -V</c> (macOS).</summary>
        public IReadOnlyList<string> RegisteredHomes { get; init; } = [];
    }

    /// <summary>Candidate JAVA_HOMEs in priority order, with where each came from. Nothing is executed here.</summary>
    public static IReadOnlyList<(string Home, string Source)> Candidates(Context c)
    {
        var list = new List<(string, string)>();
        void Add(string? home, string source)
        {
            if (string.IsNullOrWhiteSpace(home)) return;
            home = home.TrimEnd('/', '\\');
            if (list.All(x => !string.Equals(x.Item1, home, StringComparison.Ordinal))) list.Add((home, source));
        }
        string R(string path) => c.RootPrefix.Length == 0 ? path : c.RootPrefix.TrimEnd('/') + path;
        IEnumerable<string> Dirs(string dir, string pattern = "*")
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir, pattern).Order(StringComparer.Ordinal).ToList() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }

        if (c.SavedHome is { Length: > 0 } saved) Add(saved, "Saved in HCG");
        Add(c.GetEnv("JAVA_HOME"), "JAVA_HOME");
        switch (c.Platform)
        {
            case TargetPlatform.MacOS:
                foreach (var apps in new[] { R("/Applications"), Path.Combine(c.UserHome, "Applications") })
                    foreach (var studio in Dirs(apps, "Android Studio*.app"))
                        foreach (var rel in new[] { "Contents/jbr/Contents/Home", "Contents/jre/Contents/Home", "Contents/jre/jdk/Contents/Home" })
                            if (Directory.Exists(Path.Combine(studio, rel))) Add(Path.Combine(studio, rel), "Android Studio (bundled JDK)");
                foreach (var h in c.RegisteredHomes) Add(h, "/usr/libexec/java_home");
                foreach (var vms in new[] { R("/Library/Java/JavaVirtualMachines"), Path.Combine(c.UserHome, "Library", "Java", "JavaVirtualMachines") })
                    foreach (var jdk in Dirs(vms)) Add(Path.Combine(jdk, "Contents", "Home"), "JavaVirtualMachines");
                foreach (var brew in new[] { R("/opt/homebrew/opt"), R("/usr/local/opt") })
                    foreach (var jdk in Dirs(brew, "openjdk*")) Add(Path.Combine(jdk, "libexec", "openjdk.jdk", "Contents", "Home"), "Homebrew");
                break;
            case TargetPlatform.Windows:
                var pf = c.GetEnv("ProgramFiles") ?? @"C:\Program Files";
                foreach (var rel in new[] { @"Android\Android Studio\jbr", @"Android\Android Studio\jre" }) Add(Path.Combine(pf, rel), "Android Studio (bundled JDK)");
                foreach (var vendor in new[] { "Eclipse Adoptium", "Java", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft" })
                    foreach (var jdk in Dirs(Path.Combine(pf, vendor))) Add(jdk, vendor);
                break;
            default:
                foreach (var studio in new[] { R("/opt/android-studio"), Path.Combine(c.UserHome, "android-studio"), R("/snap/android-studio/current/android-studio") })
                    Add(Directory.Exists(Path.Combine(studio, "jbr")) ? Path.Combine(studio, "jbr") : null, "Android Studio (bundled JDK)");
                foreach (var jdk in Dirs(R("/usr/lib/jvm"))) Add(jdk, "/usr/lib/jvm");
                break;
        }
        foreach (var jdk in Dirs(Path.Combine(c.UserHome, ".sdkman", "candidates", "java")).Where(d => Path.GetFileName(d) != "current"))
            Add(jdk, "SDKMAN");
        if (c.PathJava is { } pathJava) Add(HomeOfExecutable(pathJava), "java on PATH");
        return list;
    }

    /// <summary>bin/java → its JAVA_HOME (the folder above bin), following symbolic links.</summary>
    public static string HomeOfExecutable(string java)
    {
        var real = PathGuard.GetRealPath(java);
        return Path.GetDirectoryName(Path.GetDirectoryName(real)) ?? real;
    }

    /// <summary>Runs every candidate (in parallel) and selects one: a saved JDK always wins (even when broken: it is then reported, never silently replaced); otherwise JAVA_HOME, then the first JDK 17+, then any working JDK.</summary>
    public async Task<JavaDiscoveryResult> DiscoverAsync(Context c, CancellationToken ct)
    {
        var candidates = Candidates(c);
        var probed = await Task.WhenAll(candidates.Select(x => ProbeAsync(x.Home, x.Source, c.Platform, ct))).ConfigureAwait(false);
        // The same JDK reached by two routes (JAVA_HOME and Android Studio, or a symlink) is listed once.
        var unique = probed.GroupBy(p => SafeRealPath(p.Home), StringComparer.Ordinal).Select(g => g.First()).ToList();
        JdkCandidate? selected;
        if (c.SavedHome is { Length: > 0 })
            selected = unique.FirstOrDefault(x => x.Source == "Saved in HCG");
        else
            selected = unique.FirstOrDefault(x => x.Source == "JAVA_HOME" && x.IsUsable)
                       ?? unique.FirstOrDefault(x => x.IsUsable && x.Major >= RecommendedMajor)
                       ?? unique.FirstOrDefault(x => x.IsUsable);
        return new JavaDiscoveryResult(selected, unique, c.SavedHome);
    }

    private static string SafeRealPath(string p)
    {
        try { return PathGuard.GetRealPath(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return p; }
    }

    /// <summary>Validates one JAVA_HOME by running its java. Never throws for a bad candidate; the problem is returned.</summary>
    public async Task<JdkCandidate> ProbeAsync(string home, string source, TargetPlatform platform, CancellationToken ct)
    {
        var exeName = platform == TargetPlatform.Windows ? "java.exe" : "java";
        var java = Path.Combine(home, "bin", exeName);
        if (!Directory.Exists(home))
            return new JdkCandidate(home, source, null, 0, false, $"{home} does not exist.");
        if (!File.Exists(java))
            return new JdkCandidate(home, source, null, 0, false, $"No bin/{exeName} in {home}: this folder is not a JDK. Choose the JDK's home folder (the one that contains bin/{exeName}).");
        string output;
        int exit;
        try
        {
            var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec
            {
                Executable = java, Arguments = ["-XshowSettings:properties", "-version"], Timeout = ProbeTimeout,
                Environment = ProbeEnvironment(home),
            }, ct).ConfigureAwait(false);
            // java prints its version and settings to stderr; shims and wrappers sometimes use stdout. Read both.
            output = stderr + "\n" + stdout;
            exit = r.TimedOut ? -1 : r.ExitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JdkCandidate(home, source, null, 0, false, $"{java} could not be started: {ex.Message}");
        }
        var (version, realHome, problem) = Interpret(output, exit);
        if (problem is not null) return new JdkCandidate(home, source, version, Major(version), false, problem);
        var effectiveHome = realHome ?? home;
        var hasJavac = File.Exists(Path.Combine(effectiveHome, "bin", platform == TargetPlatform.Windows ? "javac.exe" : "javac"))
                       || File.Exists(Path.Combine(home, "bin", platform == TargetPlatform.Windows ? "javac.exe" : "javac"));
        return hasJavac
            ? new JdkCandidate(home, source, version, Major(version), true, null)
            : new JdkCandidate(home, source, version, Major(version), false, $"Java {version} at {home} is a runtime only (no javac): Android builds need a full JDK.");
    }

    /// <summary>Reads <c>java -XshowSettings:properties -version</c> output: version, java.home, or why it is not a working Java.</summary>
    public static (string? Version, string? JavaHome, string? Problem) Interpret(string output, int exitCode)
    {
        var text = output.Trim();
        if (StubRegex().IsMatch(text))
            return (null, null, "This is macOS's java stub, and no JDK is installed behind it (\"Unable to locate a Java Runtime\"). Install a JDK or use Android Studio's bundled JDK.");
        var version = PropertyRegex("java.version").Match(text) is { Success: true } pv ? Normalize(pv.Groups[1].Value)
                      : ToolchainDetector.ParseJavaVersion(text);
        var javaHome = PropertyRegex("java.home").Match(text) is { Success: true } ph ? ph.Groups[1].Value.Trim() : null;
        if (exitCode != 0)
            return (version, javaHome, $"java -version failed (exit code {exitCode}): {FirstMeaningfulLine(text)}");
        if (version is null)
            return (null, javaHome, $"java ran but reported no version: {FirstMeaningfulLine(text)}");
        return (version, javaHome, null);
    }

    private static string Normalize(string v) => v.StartsWith("1.", StringComparison.Ordinal) ? v[2..] : v;

    public static int Major(string? version) =>
        version is not null && int.TryParse(version.Split('.', '_', '-', '+')[0], out var m) ? m : 0;

    private static string FirstMeaningfulLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("Property settings", StringComparison.Ordinal) && !l.Contains(" = ", StringComparison.Ordinal));
        return string.IsNullOrEmpty(line) ? "(no output)" : line.Length > 300 ? line[..300] + "…" : line;
    }

    private static IReadOnlyDictionary<string, string> ProbeEnvironment(string home)
    {
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        // Wrappers such as /usr/bin/java and asdf/jenv shims consult JAVA_HOME: point it at the candidate being tested.
        env["JAVA_HOME"] = home;
        env.Remove("JAVA_TOOL_OPTIONS");
        env.Remove("_JAVA_OPTIONS");
        return env;
    }

    /// <summary>JDK homes from <c>/usr/libexec/java_home -V</c> (macOS; the list is printed to stderr).</summary>
    public async Task<IReadOnlyList<string>> RegisteredHomesAsync(CancellationToken ct)
    {
        const string tool = "/usr/libexec/java_home";
        if (!File.Exists(tool)) return [];
        try
        {
            var (_, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = tool, Arguments = ["-V"], Timeout = ProbeTimeout }, ct).ConfigureAwait(false);
            return ParseJavaHomeList(stderr + "\n" + stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }

    /// <summary>Lines such as <c>    21.0.2 (arm64) "Eclipse Adoptium" - "OpenJDK 21.0.2" /Library/Java/…/Contents/Home</c>.</summary>
    public static IReadOnlyList<string> ParseJavaHomeList(string output) =>
        JavaHomeLineRegex().Matches(output).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

    [GeneratedRegex(@"Unable to locate a Java Runtime|No Java runtime present|requesting install", RegexOptions.IgnoreCase)]
    private static partial Regex StubRegex();

    private static Regex PropertyRegex(string name) => new($@"^\s*{Regex.Escape(name)}\s*=\s*(.+?)\s*$", RegexOptions.Multiline);

    [GeneratedRegex(@"^\s+\d[^\n]*?""\s+(/[^\n]+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex JavaHomeLineRegex();
}
