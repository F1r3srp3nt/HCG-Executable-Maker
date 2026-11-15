using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>The state line Mobile Build Studio shows for the current selection.</summary>
public sealed record MobileStateView(string Tag, string Tone, string Detail);

/// <summary>One condition "Start Mobile Build" waits for.</summary>
public sealed record BuildPrerequisite(string Title, bool Met, string Detail);

/// <summary>A finished build seen from the current selection: current, or a previous build made with other inputs.</summary>
public sealed record BuildResultView(bool IsCurrent, string Tag, string Tone, string Detail, IReadOnlyList<string> Differences);

public static class MobileBuildState
{
    /// <summary>The provenance of building this project with these settings now (also recorded at build start).</summary>
    public static BuildProvenance ProvenanceFor(ProjectRecord project, BuildSettings settings, ToolchainReport? report)
    {
        var o = settings.Mobile;
        var ios = o?.Output.Platform() == TargetPlatform.IOS;
        var simulator = o?.Output == MobileOutput.IosSimulatorApp;
        var mobile = project.Analysis?.Mobile;
        string? scheme = null, configuration = o?.Variant;
        if (ios && mobile?.Ios is { } info && o is not null && mobile.Framework is MobileFramework.NativeIos or MobileFramework.Capacitor)
        {
            var s = IosSchemeCompatibility.FindScheme(info, o.Scheme);
            scheme = s?.Name ?? o.Scheme;
            configuration ??= IosSchemeCompatibility.DefaultConfiguration(info, s, simulator);
        }
        configuration ??= o?.Output is MobileOutput.AndroidDebugApk or MobileOutput.IosSimulatorApp ? "Debug" : o is null ? null : "Release";
        var x = report?.Xcode;
        return new BuildProvenance
        {
            SourceRevision = SourceRevision.Of(project),
            Output = o?.Output.DisplayName() ?? settings.Platform.DisplayName(),
            ProjectPath = settings.ProjectRelativePath,
            Scheme = scheme,
            Configuration = configuration,
            Sdk = ios ? IosSchemeCompatibility.SdkFor(simulator) : null,
            Destination = ios ? simulator ? "generic/platform=iOS Simulator" : "generic/platform=iOS" : null,
            Signing = o?.Signing.ToString(),
            ExportMethod = o?.ExportMethod?.ToString(),
            Toolchain = ios && x is { VerifiedVersion: { } v } ? $"Xcode {v} ({x.ActiveDeveloperDirectory})" : null,
            DeploymentTargetOverride = ios ? o?.DeploymentTargetOverride : null,
        };
    }

    /// <summary>
    /// How a finished build relates to the current selection. A result is current only when every provenance field matches;
    /// otherwise it is a "Previous build", shown with what produced it — its artifacts stay available.
    /// </summary>
    public static BuildResultView DescribeResult(BuildRecord result, BuildProvenance? current)
    {
        var differences = result.Provenance is { } p && current is not null ? p.DifferencesFrom(current) : [];
        var when = (result.EndedAt ?? result.StartedAt).UtcDateTime;
        // What produced it, first: scheme, configuration and when — so a previous result is never read as the current state.
        var produced = $"{(result.Provenance?.Scheme is { } sc ? "scheme " + sc + ", " : "")}{(result.Provenance?.Configuration is { } cf ? cf + ", " : "")}" +
                       $"finished {when:yyyy-MM-dd HH:mm} UTC" + (result.Provenance?.Describe() is { Length: > 0 } inputs ? $" ({inputs})" : "");
        var summary = result.Status == BuildStatus.Succeeded
            ? $"{result.Status}: {result.Artifacts.Count} artifact(s) in {(result.Duration is { } d ? $"{d.TotalSeconds:F0}s" : "—")}"
            : $"{result.Status}: {MobileBuildService.FirstErrorOf(result.FailureReason) ?? result.FailureReason}";
        if (result.Provenance is null || current is not null && differences.Count == 0)
            return new BuildResultView(true, result.Status.ToString(), result.Status == BuildStatus.Succeeded ? "success" : "error", summary, []);
        if (current is null)
            return new BuildResultView(false, "Previous build", "neutral",
                $"{summary} — built with {produced}. Nothing can be built with the current settings, so this result does not describe them.", ["no buildable output is selected"]);
        return new BuildResultView(false, "Previous build", "neutral",
            $"{summary} — built with {produced}. The current settings differ, so this result does not describe them: {string.Join("; ", differences)}.", differences);
    }

    /// <summary>
    /// The precise state of the selection: Artifact verified / Build failed (for a result built with the current inputs only),
    /// Project implementation incomplete, Build adapter unavailable, Required toolchain missing, Selected toolchain
    /// incompatible, Configuration required, Recognized project, Ready to build. Missing signing assets are never a toolchain problem.
    /// </summary>
    public static MobileStateView Describe(ProjectAnalysis a, MobileAnalysis m, IReadOnlyList<MobileOutputOption> forPlatform, MobileOutputOption? selected,
        BuildReadiness? readiness, BuildRecord? last, BuildProvenance? current)
    {
        if (last is not null && DescribeResult(last, current).IsCurrent)
        {
            if (last.Status == BuildStatus.Succeeded && last.Artifacts.Count > 0 && last.Artifacts.All(x => x.Verification != VerificationStatus.Failed))
                return new("Artifact verified", "success", $"{last.Artifacts.Count} artifact(s) built and verified: {string.Join(", ", last.Artifacts.Select(x => x.Format.DisplayName()))}.");
            if (last.Status is BuildStatus.Failed or BuildStatus.TimedOut or BuildStatus.Cancelled)
                return new("Build failed", "error", MobileBuildService.FirstErrorOf(last.FailureReason) ?? last.FailureReason ?? "");
        }
        var noTarget = m.Framework == MobileFramework.NativeIos && m.Ios is { } ios && !ios.AppTargets.Any() || m.Framework == MobileFramework.NativeAndroid && m.Android is { AppModules.Count: 0 };
        if (noTarget) return new("Project implementation incomplete", "error", "The project has no application target or module to build.");
        if (selected is null)
        {
            var what = m.DotNetIos is { } dn ? $"{dn.ApplicationTitle ?? dn.AssemblyName} — {m.Framework.DisplayName()} app ({dn.ProjectPath})" : $"{a.ApplicationType} ({m.Framework.DisplayName()})";
            if (forPlatform.Count == 0) return new("Build adapter unavailable", "error", $"{what}: HCG has no build route for this project's outputs.");
            if (forPlatform.Any(o => o.IsAvailable)) return new("Recognized project", "info", $"{what}. Choose an output.");
            if (a.Support == SupportLevel.RequiresConfiguration) return new("Configuration required", "warning", $"{what}: {a.SupportSummary}");
            return Classify(forPlatform.SelectMany(o => o.Blockers).DistinctBy(b => (b.Tool, b.Detail)).ToList(), what + ": " + forPlatform[0].Reason);
        }
        if (readiness is null) return new("—", "neutral", "");
        var blockers = readiness.Blockers.ToList();
        return blockers.Count == 0 ? new("Ready to build", "success", $"{selected.DisplayName} can be built on this computer.") : Classify(blockers, null);
    }

    public const string DeploymentBlockedTag = "Blocked — deployment target incompatible";

    /// <summary>
    /// Everything "Start Mobile Build" waits for, in order, each met or not with what to do: a buildable output, every
    /// required readiness item (toolchain, deployment target, signing) and the per-build project-trust confirmation.
    /// </summary>
    public static IReadOnlyList<BuildPrerequisite> Prerequisites(MobileOutputOption? selected, IReadOnlyList<MobileOutputOption> forPlatform,
        BuildReadiness? readiness, bool trustAcknowledged, bool isBuilding)
    {
        var list = new List<BuildPrerequisite>();
        if (selected is null)
            list.Add(new("Output", false, forPlatform.Any(o => o.IsAvailable)
                ? "Choose an output under Platform, scheme and output."
                : "No output can be built with the current selection; the blockers are listed under Scheme compatibility and STATE."));
        else if (!selected.IsAvailable)
            list.Add(new("Output", false, $"{selected.DisplayName} is not available: {selected.Reason}"));
        else
            list.Add(new("Output", true, selected.DisplayName));
        // With no output chosen, what blocks every output (toolchain, deployment target); an output's own requirements, such
        // as the IPA's signing certificate, are listed once that output is chosen.
        var blockers = (readiness?.Blockers ?? (selected is null ? forPlatform.SelectMany(o => o.Blockers).Where(b => !IsSigning(b)) : [])).DistinctBy(b => (b.Tool, b.Detail)).ToList();
        foreach (var b in blockers)
            list.Add(new(b.Tool, false, b.Detail + (b.Fix is { Length: > 0 } f ? " " + f : "")));
        if (readiness is not null && blockers.Count == 0)
            list.Add(new("Toolchain, deployment target and signing", true, "Every required item in Readiness for this build is met."));
        list.Add(trustAcknowledged
            ? new("Project trust", true, "Confirmed for the next build.")
            : new("Project trust", false, "Tick “I trust this project and want to run its build scripts on this computer.” xcodebuild runs the project's build phases, scripts and package plugins; HCG asks before every build and clears the tick afterwards."));
        if (isBuilding) list.Add(new("No build running", false, "A build is in progress."));
        return list;
    }

    private static bool IsSigning(ReadinessItem b) =>
        b.Tool is "Provisioning profile" or "Code-signing identity" or "Code signing" || b.Tool.Contains("certificate", StringComparison.OrdinalIgnoreCase);

    private static MobileStateView Classify(IReadOnlyList<ReadinessItem> blockers, string? fallback)
    {
        var tools = blockers.Where(b => !IsSigning(b)).ToList();
        var missing = tools.Where(b => b.State is ReadinessState.NotFound or ReadinessState.OtherHost or ReadinessState.InvalidPath).ToList();
        // The selected Xcode is installed and works, but its SDK does not accept the selection's deployment target:
        // a property of the selection (scheme, configuration, Xcode), not a missing tool.
        var deployment = tools.Where(b => b.State == ReadinessState.IncompatibleVersion && b.Tool == IosBuildSelection.DeploymentTargetItem).ToList();
        var incompatible = tools.Where(b => b.State == ReadinessState.IncompatibleVersion && b.Tool != IosBuildSelection.DeploymentTargetItem).ToList();
        string Join(IEnumerable<ReadinessItem> items) => string.Join(" ", items.Select(b => $"{b.Tool}: {b.Detail}"));
        if (missing.Count > 0) return new("Required toolchain missing", "warning", Join(missing));
        if (deployment.Count > 0)
            return new(DeploymentBlockedTag, "error",
                "The selected Xcode is installed, but its SDK does not accept this selection's minimum iOS. " + Join(deployment.DistinctBy(b => b.Detail)) +
                " Resolve it under Scheme compatibility: a compatible scheme, another installed Xcode, or a project change.");
        if (incompatible.Count > 0) return new("Selected toolchain incompatible", "warning", "Installed, but not usable for this build. " + Join(incompatible));
        return new("Configuration required", "warning", blockers.Count > 0 ? Join(blockers) : fallback ?? "");
    }
}

/// <summary>
/// The revision a build used: the original's git commit (or ZIP hash) recorded when HCG copied it, the copy's
/// fingerprint and when it was copied. HCG's copy has no .git, so the revision is never read from the copy itself.
/// </summary>
public static class SourceRevision
{
    public static string Of(ProjectRecord project)
    {
        var copied = (project.RefreshedAt ?? project.ImportedAt).UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var what = project.SourceRevision ?? Core.GitRevision.Read(project.SourceDirectory);
        return what is null
            ? $"copy of {copied} UTC (revision not recorded: imported before HCG recorded it; use Refresh from source)"
            : $"{what}, copied {copied} UTC" + (project.SourceFingerprint is { } fp ? $" ({fp})" : "");
    }
}

/// <summary>
/// Compares a built iOS artifact with what the selection was meant to produce: bundle identifier, version, minimum iOS,
/// platform and architectures, from the artifact itself (Info.plist and Mach-O). These are artifact checks only; HCG does
/// not install or launch the app, so installation and runtime are reported as not verified.
/// </summary>
public static class IosArtifactExpectations
{
    public static IReadOnlyList<VerificationCheck> Check(MobileArtifactReport report, IosResolvedSettings? expected, bool simulator, TargetArchitecture host, string? deploymentOverride = null)
    {
        var checks = new List<VerificationCheck>();
        void Compare(string name, string? actual, string? wanted, string what)
        {
            if (wanted is null) checks.Add(new(name, VerificationStatus.NotVerifiable, $"{what}: {actual ?? "—"} (the project's value could not be resolved statically, so it was not compared)."));
            else if (actual is null) checks.Add(new(name, VerificationStatus.Failed, $"{what}: missing from the artifact (expected {wanted})."));
            else checks.Add(new(name, string.Equals(actual, wanted, StringComparison.Ordinal) ? VerificationStatus.Passed : VerificationStatus.Failed,
                string.Equals(actual, wanted, StringComparison.Ordinal) ? $"{what}: {actual}, as the selected scheme and configuration define." : $"{what}: {actual}, but the selected scheme and configuration define {wanted}."));
        }
        Compare("Bundle identifier (matches build)", report.PackageId, expected?.BundleIdentifier, "CFBundleIdentifier");
        Compare("Version (matches build)", report.VersionName, expected?.MarketingVersion, "CFBundleShortVersionString");
        if (expected?.BuildVersion is not null) Compare("Build number (matches build)", report.VersionCode, expected.BuildVersion, "CFBundleVersion");
        var minOk = expected?.DeploymentTarget is { } dt && report.MinimumOs is { } mo && IosDeploymentRange.Parse(mo) == IosDeploymentRange.Parse(dt);
        if (expected?.DeploymentTarget is null) checks.Add(new("Minimum iOS (matches build)", VerificationStatus.NotVerifiable, $"MinimumOSVersion {report.MinimumOs ?? "—"}; the deployment target was not resolved statically."));
        else checks.Add(new("Minimum iOS (matches build)", minOk ? VerificationStatus.Passed : VerificationStatus.Failed,
            minOk ? $"MinimumOSVersion {report.MinimumOs} = the {(deploymentOverride is null ? "deployment target" : "build-only minimum iOS")} {expected.DeploymentTarget}."
                  : $"MinimumOSVersion {report.MinimumOs ?? "missing"}, but the {(deploymentOverride is null ? "deployment target" : "build-only minimum iOS")} is {expected.DeploymentTarget}."));
        // The executable's own load command (LC_BUILD_VERSION minos) is what the device's loader enforces.
        if (expected?.DeploymentTarget is { } wantedMin)
        {
            if (report.BinaryMinimumOs.Count == 0)
                checks.Add(new("Minimum iOS in executable", VerificationStatus.NotVerifiable, "The executable's LC_BUILD_VERSION was not read" + (report.Kind == MobileArtifactKind.Ipa ? " (not read inside an .ipa)." : ".")));
            else
            {
                var all = report.BinaryMinimumOs.All(m => IosDeploymentRange.Parse(m) == IosDeploymentRange.Parse(wantedMin));
                checks.Add(new("Minimum iOS in executable", all ? VerificationStatus.Passed : VerificationStatus.Failed,
                    $"LC_BUILD_VERSION minos {string.Join(", ", report.BinaryMinimumOs.Distinct())}{(all ? " = " : ", expected ")}{wantedMin}."));
            }
        }
        var wantedPlatform = simulator ? "iphonesimulator" : "iphoneos";
        checks.Add(new("Platform (matches build)", report.Platform == wantedPlatform ? VerificationStatus.Passed : report.Platform is null ? VerificationStatus.NotVerifiable : VerificationStatus.Failed,
            $"Built for {report.Platform ?? "an unknown platform"}; the output requires {wantedPlatform}."));
        var arch = simulator ? (host == TargetArchitecture.Arm64 ? "arm64" : "x86_64") : "arm64";
        checks.Add(new("Architectures (matches build)", report.Architectures.Contains(arch) ? VerificationStatus.Passed : report.Architectures.Count == 0 ? VerificationStatus.NotVerifiable : VerificationStatus.Failed,
            $"{(report.Architectures.Count == 0 ? "No architectures read" : string.Join(", ", report.Architectures))}; {(simulator ? "this Mac's Simulator" : "iOS devices")} need {arch}."));
        checks.Add(new("Installation and runtime", VerificationStatus.NotVerifiable,
            simulator ? "Not tested: HCG checked the built app; it did not install it in a Simulator or launch it."
                      : "Not tested: HCG checked the artifact; it did not install it on a device or launch it."));
        return checks;
    }
}
