using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>One concise, shared statement of whether the selected scheme fits the selected Xcode, per SDK.</summary>
public sealed record IosCompatibilitySummary
{
    /// <summary>One sentence, e.g. "HCGSentinel targets iOS 14.7; Xcode 27.0 builds iOS 15.0 and later (device and Simulator)." Null when everything fits.</summary>
    public string? Problem { get; init; }
    /// <summary>Short per-SDK lines: "Device (iPhoneOS 27.0, Release): needs iOS 14.7 — outside iOS 15.0–27.0.99".</summary>
    public IReadOnlyList<(string Label, string Text, IosToolchainFit Fit)> Lines { get; init; } = [];
    /// <summary>How to keep the selected scheme (a compatible Xcode), when it does not fit.</summary>
    public string? KeepRoute { get; init; }
    /// <summary>A scheme of this project verified compatible with the selected Xcode for the SDKs that fail.</summary>
    public string? Alternative { get; init; }
    public string? AlternativeMinimum { get; init; }
    /// <summary>Shown before switching: what the alternative's minimum iOS means, and that nothing else changes.</summary>
    public string? SwitchExplanation { get; init; }
    public bool DeviceFits { get; init; }
    public bool SimulatorFits { get; init; }
}

/// <summary>
/// Mobile Build Studio's iOS selection rules, shared by the page and the tests: restoring a project's saved selection,
/// the effective configuration per SDK, the single shared deployment-target message, and short output reasons that
/// refer to it instead of repeating it.
/// </summary>
public static class IosBuildSelection
{
    public const string DeploymentTargetItem = "Deployment target";

    /// <summary>The saved selection, if it still matches the project; otherwise the project's defaults. Never invents a scheme.</summary>
    public static MobileSelection Restore(string projectId, IosProjectInfo ios, MobileSelection? saved)
    {
        var containers = Containers(ios);
        var schemes = Schemes(ios);
        return new MobileSelection
        {
            ProjectId = projectId,
            Container = saved?.Container is { } c && containers.Contains(c) ? c : containers.FirstOrDefault(),
            Scheme = saved?.Scheme is { } s && schemes.Contains(s) ? s : IosSchemeCompatibility.FindScheme(ios, null)?.Name ?? schemes.FirstOrDefault(),
            Configuration = saved?.Configuration is { } cfg && ios.Configurations.Contains(cfg) ? cfg : null,
            SavedAt = saved?.SavedAt ?? default,
        };
    }

    /// <summary>The workspace first (CocoaPods etc. need it), then the project.</summary>
    public static IReadOnlyList<string> Containers(IosProjectInfo ios) => new[] { ios.WorkspacePath, ios.ProjectPath }.OfType<string>().ToList();

    public static IReadOnlyList<string> Schemes(IosProjectInfo ios) =>
        ios.Schemes.Count > 0 ? ios.Schemes.Select(s => s.Name).ToList() : ios.AppTargets.Select(t => t.Name).Take(1).ToList();

    /// <summary>The configuration a build of this SDK uses: the explicit choice, else the scheme's own.</summary>
    public static string Configuration(IosProjectInfo ios, string? scheme, string? chosen, bool simulator) =>
        chosen ?? IosSchemeCompatibility.DefaultConfiguration(ios, IosSchemeCompatibility.FindScheme(ios, scheme), simulator);

    public static IosCompatibilitySummary Summarize(IosProjectInfo ios, string? scheme, string? configuration, XcodeStatus? x)
    {
        var sim = IosSchemeCompatibility.Evaluate(ios, scheme, Configuration(ios, scheme, configuration, true), true, x);
        var dev = IosSchemeCompatibility.Evaluate(ios, scheme, Configuration(ios, scheme, configuration, false), false, x);
        string Line(IosSchemeCompatibilityResult r)
        {
            var needs = r.Outside.Count > 0 ? r.Outside : r.Targets;
            var lowest = needs.Select(t => t.DeploymentTarget).OfType<string>().Distinct().ToList();
            return r.Fit switch
            {
                IosToolchainFit.Compatible => $"Compatible — iOS {string.Join(", ", lowest)} is within iOS {r.Range?.Minimum}–{r.Range?.Maximum}.",
                IosToolchainFit.Incompatible => $"Incompatible — {string.Join(", ", r.Outside.Select(t => $"{t.Target} iOS {t.DeploymentTarget}"))} outside iOS {r.Range?.Minimum}–{r.Range?.Maximum}.",
                IosToolchainFit.ToolchainMissing => "Toolchain missing — no Xcode is selected or installed.",
                _ => "Not verifiable before building — " + r.Detail,
            };
        }
        string Label(IosSchemeCompatibilityResult r, string kind) => $"{kind} · {r.SdkDisplay}{(r.Range?.SdkVersion is { } v ? " " + v : "")} · {r.Configuration}";
        var lines = new List<(string, string, IosToolchainFit)> { (Label(dev, "Device"), Line(dev), dev.Fit), (Label(sim, "Simulator"), Line(sim), sim.Fit) };

        var failing = new[] { dev, sim }.Where(r => r.Fit == IosToolchainFit.Incompatible).ToList();
        if (failing.Count == 0)
            return new IosCompatibilitySummary { Lines = lines, DeviceFits = dev.Fit != IosToolchainFit.Incompatible, SimulatorFits = true };

        var first = failing[0];
        var targets = failing.SelectMany(r => r.Outside).Select(t => t.DeploymentTarget).OfType<string>().Distinct().ToList();
        var which = failing.Count == 2 ? "device and Simulator" : failing[0] == dev ? "device" : "Simulator";
        var minimum = first.Range?.Minimum;
        var xcode = x?.VerifiedVersion is { } xv ? "Xcode " + xv : "The selected Xcode";
        var problem = $"{first.Scheme} targets iOS {string.Join(", ", targets)}; {xcode} builds iOS {minimum} and later ({which}).";

        // An alternative must be verified compatible for every SDK that fails, so switching really unblocks those builds.
        var alternative = failing.Select(r => r.CompatibleSchemes).Aggregate((a, b) => a.Intersect(b).ToList()).FirstOrDefault();
        string? altMin = null, explanation = null;
        if (alternative is not null)
        {
            var altTargets = IosSchemeCompatibility.TargetsOf(ios, IosSchemeCompatibility.FindScheme(ios, alternative), alternative,
                Configuration(ios, alternative, configuration, false), false);
            altMin = altTargets.Select(t => t.DeploymentTarget).OfType<string>().OrderByDescending(v => Version.TryParse(v, out var pv) ? pv : new Version(0, 0)).FirstOrDefault();
            explanation = $"{alternative} targets iOS {altMin}: what it builds installs and runs only on iOS {altMin} or later. " +
                          $"Switching changes only the scheme HCG builds. {first.Scheme} and its iOS {string.Join(", ", targets)} deployment target stay in the project unchanged; " +
                          $"to build {first.Scheme}, select an Xcode whose SDK accepts iOS {string.Join(", ", targets)}.";
        }
        var keep = first.CompatibleXcodes.Count > 0
            ? $"To keep {first.Scheme}: select {string.Join(" or ", first.CompatibleXcodes.Select(c => $"Xcode {c.Version}"))} (installed) below."
            : $"To keep {first.Scheme}: install an Xcode whose SDK accepts iOS {string.Join(", ", targets)} and select it below.";
        return new IosCompatibilitySummary
        {
            Problem = problem, Lines = lines, KeepRoute = keep, Alternative = alternative, AlternativeMinimum = altMin, SwitchExplanation = explanation,
            DeviceFits = dev.Fit != IosToolchainFit.Incompatible, SimulatorFits = sim.Fit != IosToolchainFit.Incompatible,
        };
    }

    /// <summary>
    /// An output's reason, shortened when the shared deployment-target message already explains it: the deployment target
    /// is named once at the top, and each output only says it is blocked by it (plus any blocker of its own).
    /// </summary>
    public static string? ShortReason(MobileOutputOption option)
    {
        if (option.IsAvailable) return null;
        if (!option.Blockers.Any(b => b.Tool == DeploymentTargetItem)) return option.Reason;
        var others = option.Blockers.Where(b => b.Tool != DeploymentTargetItem).Select(b => b.Detail + (b.Fix is null ? "" : " " + b.Fix)).ToList();
        var sdk = option.Output == MobileOutput.IosSimulatorApp ? "Simulator SDK" : "iPhoneOS SDK";
        return $"Blocked by the deployment target ({sdk}) — see Scheme compatibility above." + (others.Count > 0 ? " Also: " + string.Join(" ", others) : "");
    }
}
