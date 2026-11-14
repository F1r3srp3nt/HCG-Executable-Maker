using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>One concise, shared statement of whether the selected scheme fits the selected Xcode, per SDK, and the ways out.</summary>
public sealed record IosCompatibilitySummary
{
    /// <summary>
    /// The one sentence shown when the scheme does not fit, e.g. "Project minimum: iOS 14.0. Selected toolchain minimum: iOS 15.0
    /// (Xcode 27.0 · device and Simulator SDKs)." Null when nothing is confirmed incompatible.
    /// </summary>
    public string? Problem { get; init; }
    public string? ProjectMinimum { get; init; }
    public string? ToolchainMinimum { get; init; }
    /// <summary>Short per-SDK lines: "Device · iPhoneOS 27.0 · Release" → "Incompatible — HCGNexus iOS 14.0 outside iOS 15.0–27.0.99."</summary>
    public IReadOnlyList<(string Label, string Text, IosToolchainFit Fit)> Lines { get; init; } = [];
    /// <summary>
    /// What could not be inspected (an SDK range or a deployment target HCG could not read), with the reader's own diagnostic.
    /// Shown as a warning; never presented as an incompatibility and never blocks by itself.
    /// </summary>
    public IReadOnlyList<string> InspectionNotices { get; init; } = [];
    /// <summary>Resolution 1 — keep the scheme: an installed Xcode verified to accept it, or what to install.</summary>
    public string? KeepRoute { get; init; }
    /// <summary>Resolution 2 — a scheme of this project verified compatible with the selected Xcode for the SDKs that fail.</summary>
    public string? Alternative { get; init; }
    public string? AlternativeMinimum { get; init; }
    /// <summary>Shown before switching: what the alternative's minimum iOS means, and that nothing else changes.</summary>
    public string? SwitchExplanation { get; init; }
    /// <summary>
    /// Resolution 3 — raise the minimum iOS for this build only. The inspected plan (effects on every target, blockers,
    /// warnings); offered only when the problem is a deployment target below the toolchain's minimum.
    /// </summary>
    public IosDeploymentOverridePlan? RaiseOffer { get; init; }
    /// <summary>The build-only minimum the user accepted and the current selection still matches (null = none).</summary>
    public IosDeploymentOverridePlan? ActiveOverride { get; init; }
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

    public static IosCompatibilitySummary Summarize(IosProjectInfo ios, string? scheme, string? configuration, XcodeStatus? x, string? deploymentOverride = null)
    {
        var sim = IosSchemeCompatibility.Evaluate(ios, scheme, Configuration(ios, scheme, configuration, true), true, x, deploymentOverride: deploymentOverride);
        var dev = IosSchemeCompatibility.Evaluate(ios, scheme, Configuration(ios, scheme, configuration, false), false, x, deploymentOverride: deploymentOverride);
        var active = deploymentOverride is null ? null : IosDeploymentOverride.Inspect(ios, scheme, configuration, x, deploymentOverride);
        string Line(IosSchemeCompatibilityResult r)
        {
            var needs = r.Outside.Count > 0 ? r.Outside : r.Targets;
            var lowest = needs.Select(t => t.DeploymentTarget).OfType<string>().Distinct().ToList();
            return r.Fit switch
            {
                IosToolchainFit.Compatible => $"Compatible — iOS {string.Join(", ", lowest)}{(r.DeploymentOverride is null ? "" : " (build-only minimum; the project keeps its own)")} is within iOS {r.Range?.Minimum}–{r.Range?.Maximum}.",
                IosToolchainFit.Incompatible when r.Outside.Count == 0 => "Incompatible — " + r.Detail,
                IosToolchainFit.Incompatible => $"Incompatible — {string.Join(", ", r.Outside.Select(t => $"{t.Target} iOS {t.DeploymentTarget}"))} outside iOS {r.Range?.Minimum}–{r.Range?.Maximum}.",
                IosToolchainFit.ToolchainMissing => "Toolchain missing — no Xcode is selected or installed.",
                _ => "Not verified (inspection failure, not an incompatibility) — " + r.Detail,
            };
        }
        string Label(IosSchemeCompatibilityResult r, string kind) => $"{kind} · {r.SdkDisplay}{(r.Range?.SdkVersion is { } v ? " " + v : "")} · {r.Configuration}";
        var lines = new List<(string, string, IosToolchainFit)> { (Label(dev, "Device"), Line(dev), dev.Fit), (Label(sim, "Simulator"), Line(sim), sim.Fit) };
        var notices = new[] { (dev, "Device"), (sim, "Simulator") }.Where(p => p.Item1.Fit == IosToolchainFit.NotVerifiable)
            .Select(p => $"{p.Item2} ({p.Item1.SdkDisplay} SDK): not verified before building — {p.Item1.Detail}").ToList();

        var failing = new[] { dev, sim }.Where(r => r.Fit == IosToolchainFit.Incompatible).ToList();
        if (failing.Count == 0)
            return new IosCompatibilitySummary
            {
                Lines = lines, InspectionNotices = notices, ActiveOverride = active is { Supported: true } ? active : null,
                DeviceFits = dev.Fit != IosToolchainFit.Incompatible, SimulatorFits = true,
            };

        var first = failing[0];
        var xcode = x?.VerifiedVersion is { } xv ? "Xcode " + xv : "the selected Xcode";
        var which = failing.Count == 2 ? "device and Simulator SDKs" : failing[0] == dev ? "device SDK" : "Simulator SDK";
        if (active is { Supported: false })
        {
            // The user's override no longer applies to what is selected: say so instead of silently dropping it.
            return new IosCompatibilitySummary
            {
                Problem = $"The build-only minimum iOS {active.Value} cannot be used for {active.Scheme}: {string.Join(" ", active.Blockers)}",
                Lines = lines, InspectionNotices = notices, ActiveOverride = active, KeepRoute = null,
                DeviceFits = dev.Fit != IosToolchainFit.Incompatible, SimulatorFits = sim.Fit != IosToolchainFit.Incompatible,
            };
        }
        var outside = failing.SelectMany(r => r.Outside).ToList();
        var targets = outside.Select(t => t.DeploymentTarget).OfType<string>().Distinct().ToList();
        var below = failing.Where(r => r.Range is not null && r.Outside.Any(t => IosDeploymentRange.Parse(t.DeploymentTarget) < IosDeploymentRange.Parse(r.Range.Minimum))).ToList();
        string? projectMin = null, toolMin = null, problem;
        if (below.Count > 0)
        {
            projectMin = outside.Select(t => t.DeploymentTarget).OfType<string>().OrderBy(IosDeploymentRange.Parse).First();
            toolMin = below.Select(r => r.Range!.Minimum).OrderByDescending(IosDeploymentRange.Parse).First();
            problem = $"Project minimum: iOS {projectMin}. Selected toolchain minimum: iOS {toolMin} ({xcode} · {which}).";
        }
        else
            problem = $"Project deployment target: iOS {string.Join(", ", targets)}. Selected toolchain maximum: iOS {first.Range?.Maximum} ({xcode} · {which}).";

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
            ? $"Keep {first.Scheme}: select {string.Join(" or ", first.CompatibleXcodes.Select(c => $"Xcode {c.Version}"))} (installed, its SDK accepts iOS {string.Join(", ", targets)}) in the Xcode list above."
            : $"Keep {first.Scheme}: no installed Xcode accepts iOS {string.Join(", ", targets)}. Install one whose SDK does, then select it in the Xcode list above.";
        var raise = below.Count > 0 && IosDeploymentOverride.Suggest(ios, scheme, configuration, x) is { } suggestion
            ? IosDeploymentOverride.Inspect(ios, scheme, configuration, x, suggestion)
            : null;
        return new IosCompatibilitySummary
        {
            Problem = problem, ProjectMinimum = projectMin, ToolchainMinimum = toolMin, Lines = lines, InspectionNotices = notices,
            KeepRoute = keep, Alternative = alternative, AlternativeMinimum = altMin, SwitchExplanation = explanation, RaiseOffer = raise,
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
