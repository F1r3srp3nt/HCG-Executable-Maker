using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>The state line Mobile Build Studio shows for the current selection.</summary>
public sealed record MobileStateView(string Tag, string Tone, string Detail);

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
            SourceRevision = SourceRevision.Of(project.SourceDirectory, project.ImportedAt),
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

    private static bool IsSigning(ReadinessItem b) =>
        b.Tool is "Provisioning profile" or "Code-signing identity" or "Code signing" || b.Tool.Contains("certificate", StringComparison.OrdinalIgnoreCase);

    private static MobileStateView Classify(IReadOnlyList<ReadinessItem> blockers, string? fallback)
    {
        var tools = blockers.Where(b => !IsSigning(b)).ToList();
        var missing = tools.Where(b => b.State is ReadinessState.NotFound or ReadinessState.OtherHost or ReadinessState.InvalidPath).ToList();
        var incompatible = tools.Where(b => b.State == ReadinessState.IncompatibleVersion).ToList();
        string Join(IEnumerable<ReadinessItem> items) => string.Join(" ", items.Select(b => $"{b.Tool}: {b.Detail}"));
        if (missing.Count > 0) return new("Required toolchain missing", "warning", Join(missing));
        if (incompatible.Count > 0) return new("Selected toolchain incompatible", "warning", Join(incompatible));
        return new("Configuration required", "warning", blockers.Count > 0 ? Join(blockers) : fallback ?? "");
    }
}

/// <summary>The revision of an imported source folder: its git commit when it is a checkout, otherwise the import time.</summary>
public static class SourceRevision
{
    public static string Of(string directory, DateTimeOffset importedAt)
    {
        try
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (File.Exists(git) && File.ReadAllText(git).Trim() is { } pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    git = Path.GetFullPath(Path.Combine(dir.FullName, pointer[7..].Trim()));
                if (!Directory.Exists(git)) { if (dir.FullName == Path.GetFullPath(directory)) continue; break; }
                var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
                if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return $"git {head[..Math.Min(12, head.Length)]} (detached)";
                var refName = head[5..];
                var refFile = Path.Combine(git, refName);
                var sha = File.Exists(refFile) ? File.ReadAllText(refFile).Trim()
                    : File.Exists(Path.Combine(git, "packed-refs")) ? File.ReadLines(Path.Combine(git, "packed-refs")).Select(l => l.Split(' ')).FirstOrDefault(p => p.Length == 2 && p[1] == refName)?[0] : null;
                return sha is null ? $"git {refName.Replace("refs/heads/", "")}" : $"git {sha[..Math.Min(12, sha.Length)]} ({refName.Replace("refs/heads/", "")})";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return $"import of {importedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC";
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
