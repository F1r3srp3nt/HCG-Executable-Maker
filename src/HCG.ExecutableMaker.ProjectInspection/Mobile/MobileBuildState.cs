using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>One line of current build state: a short tag, its tone, and the explanation.</summary>
public sealed record MobileBuildStateResult(string Tag, string Tone, string Detail);

/// <summary>
/// The precise state of the CURRENT selection (project + scheme + output + signing), derived only from its readiness —
/// never from a previous build's result, which is reported separately. Each blocker gets the most specific name:
/// a deployment target outside the selected Xcode's range is not a missing toolchain, and an installed Xcode that cannot
/// be used is not a missing one.
/// </summary>
public static class MobileBuildState
{
    public const string Ready = "Ready to build";
    public const string DeploymentBlocked = "Blocked — deployment target incompatible";
    public const string SchemeNotInSnapshot = "Blocked — scheme not in imported copy";
    public const string OtherHost = "Requires a Mac with Xcode";
    public const string ToolchainMissing = "Required toolchain missing";
    public const string ToolchainIncompatible = "Toolchain installed but incompatible";
    public const string ToolchainUnusable = "Toolchain installed but unusable";
    public const string ToolchainSetup = "Toolchain installed — setup required";
    public const string SigningRequired = "Signing required";
    public const string ConfigurationRequired = "Configuration required";

    private static readonly string[] ToolchainNames =
        ["Xcode", "iOS SDK", "iOS Simulator SDK", "Mac with Xcode", "JDK", "Android SDK", "SDK Platforms", "SDK Build-Tools", "bundletool",
         "Flutter SDK", ".NET MAUI workload", ".NET iOS workload", ".NET SDK", "Toolchains", "Gradle wrapper"];

    public static bool IsSigning(ReadinessItem i) =>
        i.Tool is "Provisioning profile" or "Code-signing identity" or "Code signing" || i.Tool.Contains("certificate", StringComparison.OrdinalIgnoreCase);

    public static bool IsToolchain(ReadinessItem i) => ToolchainNames.Contains(i.Tool) || i.Tool.StartsWith("SDK Platform", StringComparison.Ordinal);

    /// <summary>Category of one blocker, most specific first; the lower the rank, the more it explains the state.</summary>
    private static (int Rank, string Tag, string Tone) Category(ReadinessItem b) => b switch
    {
        { Tool: MobileReadiness.SchemeTool } => (0, SchemeNotInSnapshot, "error"),
        { Tool: MobileReadiness.DeploymentTool, State: ReadinessState.IncompatibleVersion } => (1, DeploymentBlocked, "error"),
        { State: ReadinessState.OtherHost } => (2, OtherHost, "warning"),
        _ when IsSigning(b) => (7, SigningRequired, "warning"),
        { State: ReadinessState.NotFound } when IsToolchain(b) => (3, ToolchainMissing, "warning"),
        { State: ReadinessState.IncompatibleVersion } when IsToolchain(b) => (4, ToolchainIncompatible, "warning"),
        { State: ReadinessState.InvalidPath } when IsToolchain(b) => (5, ToolchainUnusable, "warning"),
        { State: ReadinessState.SetupRequired } when IsToolchain(b) => (6, ToolchainSetup, "warning"),
        _ => (8, ConfigurationRequired, "warning"),
    };

    /// <summary>State of a selection whose readiness was evaluated. <paramref name="what"/> names the output (e.g. "iOS Simulator app").</summary>
    public static MobileBuildStateResult Classify(BuildReadiness readiness, string what)
    {
        var blockers = readiness.Blockers.ToList();
        if (blockers.Count == 0) return new(Ready, "success", $"{what} can be built on this computer.");
        var ranked = blockers.Select(b => (Item: b, Cat: Category(b))).OrderBy(x => x.Cat.Rank).ToList();
        var (_, tag, tone) = ranked[0].Cat;
        var primary = ranked.Where(x => x.Cat.Tag == tag).Select(x => Line(x.Item));
        var rest = ranked.Where(x => x.Cat.Tag != tag).Select(x => $"{x.Item.Tool} ({x.Cat.Tag.ToLowerInvariant()})").ToList();
        return new(tag, tone, string.Join(" ", primary) + (rest.Count == 0 ? "" : " Also required: " + string.Join("; ", rest) + "."));
    }

    private static string Line(ReadinessItem b) => $"{b.Tool}: {b.Detail}" + (b.Fix is null ? "" : $" → {b.Fix}");

    /// <summary>
    /// Everything still needed before Start is enabled, in order: a scheme/output choice, each readiness blocker with its
    /// fix, and the per-build trust confirmation. Empty when the build can start.
    /// </summary>
    public static IReadOnlyList<string> Prerequisites(BuildReadiness? readiness, bool outputSelected, bool trustAcknowledged)
    {
        var list = new List<string>();
        if (!outputSelected) list.Add("Choose an available output in section 3. Outputs are judged for the scheme selected above them.");
        if (readiness is not null)
            foreach (var b in readiness.Blockers.OrderBy(b => Category(b).Rank))
                list.Add($"{b.Tool} — {b.Fix ?? b.Detail}");
        if (!trustAcknowledged)
            list.Add("Tick “I trust this project and want to run its build scripts on this computer” (section 5). It is confirmed per build and clears after each build.");
        return list;
    }
}
