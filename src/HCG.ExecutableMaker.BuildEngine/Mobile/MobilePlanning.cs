using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>One external tool invocation in a mobile build. Arguments are structured; nothing goes through a shell.</summary>
public sealed record MobileStep(BuildStage Stage, string Title, ProcessSpec Process)
{
    /// <summary>Command line for logs, with password-like arguments masked.</summary>
    public string DisplayCommand => MobileArgs.Mask(Process).DisplayCommand;
}

/// <summary>Where a step's product is expected. Files are matched by pattern and must be newer than the build start.</summary>
public sealed record OutputRule(PackagingFormat Format, string SearchRoot, string Pattern, bool IsDirectory, bool Recursive = true);

public sealed record MobilePlan(IReadOnlyList<MobileStep> Steps, IReadOnlyList<OutputRule> Outputs)
{
    public List<string> Notes { get; init; } = [];
}

/// <summary>Everything a planner needs. Paths are absolute and symlink-free.</summary>
public sealed record MobilePlanInput
{
    public required MobileBuildOptions Options { get; init; }
    public required MobileAnalysis Analysis { get; init; }
    /// <summary>Workspace source directory (an isolated copy of the imported project).</summary>
    public required string SourceDirectory { get; init; }
    /// <summary>HCG's per-build working directory (derived data, archives, export options).</summary>
    public required string WorkDirectory { get; init; }
    public required ToolchainReport Toolchains { get; init; }
    /// <summary>Scrubbed environment for untrusted build processes.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    /// <summary>HCG-managed tool homes (GRADLE_USER_HOME, ANDROID_USER_HOME) so personal credentials are never read.</summary>
    public required string ToolHomeDirectory { get; init; }
    /// <summary>HCG's local debug keystore, when the chosen signing mode needs it. Never a production key.</summary>
    public DebugKeystore? Keystore { get; init; }
}

/// <summary>HCG's generated Android debug keystore. The password is the public Android debug convention, not a secret.</summary>
public sealed record DebugKeystore(string Path)
{
    public const string Alias = "androiddebugkey";
    public const string Password = "android";
    public const string DistinguishedName = "CN=Android Debug,O=Android,C=US";
}

public static partial class MobileArgs
{
    /// <summary>Returns a copy whose arguments have password values replaced with ***, for display only.</summary>
    public static ProcessSpec Mask(ProcessSpec spec) => spec with
    {
        Arguments = spec.Arguments.Select(a =>
            PasswordArg().IsMatch(a) ? PasswordArg().Replace(a, "$1***") : a).ToList(),
    };

    [GeneratedRegex(@"^((?:-P)?[\w.\-]*(?:password|pass)[\w.\-]*=|--(?:ks|key)-pass=pass:|pass:)(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordArg();

    /// <summary>Gradle task suffix for a variant: "freeRelease" → "FreeRelease".</summary>
    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Values that become a single argv entry still have to look like what they claim to be.</summary>
    public static void RequireToken(string? value, string what, [System.Diagnostics.CodeAnalysis.NotNull] out string result, string pattern = @"^[A-Za-z0-9][A-Za-z0-9 _.\-]{0,127}$")
    {
        if (value is null || !Regex.IsMatch(value, pattern))
            throw new BuildValidationException($"Invalid {what}: '{value}'.");
        result = value;
    }

    /// <summary>Resolves a '/'-relative path inside a root and refuses escapes.</summary>
    public static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsWithin(root, full)) throw new BuildValidationException($"'{relative}' is outside the project.");
        return full;
    }

    /// <summary>Environment with the given additions (later wins).</summary>
    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> env, params (string Key, string? Value)[] extra)
    {
        var d = new Dictionary<string, string>(env, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (k, v) in extra) if (v is not null) d[k] = v;
        return d;
    }

    /// <summary>JAVA_HOME for a java executable (…/bin/java → …), following symlinks such as /usr/bin/java.</summary>
    public static string? JavaHome(string? java)
    {
        if (java is null) return null;
        var real = PathGuard.GetRealPath(java);
        var home = Path.GetDirectoryName(Path.GetDirectoryName(real));
        return home is not null && File.Exists(Path.Combine(home, "bin", Path.GetFileName(real))) ? home : null;
    }
}
