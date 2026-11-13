using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

public enum IosToolchainFit
{
    /// <summary>Every target the scheme builds is inside the selected Xcode's range for this SDK.</summary>
    Compatible,
    /// <summary>Xcode is present, but its SDK does not accept a target's deployment version ("Selected toolchain incompatible").</summary>
    Incompatible,
    /// <summary>A deployment target or the SDK's range could not be read statically; xcodebuild decides.</summary>
    NotVerifiable,
    /// <summary>No usable Xcode is selected ("Toolchain missing").</summary>
    ToolchainMissing,
}

/// <summary>One target the scheme builds, with its effective deployment target for the evaluated configuration and SDK.</summary>
public sealed record IosTargetDeployment(string Target, string? DeploymentTarget, string? Source, bool IsDependency, IReadOnlyList<string> Unresolved);

public sealed record IosSchemeCompatibilityResult
{
    public required IosToolchainFit Fit { get; init; }
    public required string Scheme { get; init; }
    public required string Configuration { get; init; }
    /// <summary>"iphoneos" (device) or "iphonesimulator".</summary>
    public required string Sdk { get; init; }
    public IReadOnlyList<IosTargetDeployment> Targets { get; init; } = [];
    public IosDeploymentRange? Range { get; init; }
    /// <summary>The selected Xcode, e.g. "Xcode 26.6 (/Applications/Xcode.app/Contents/Developer)".</summary>
    public string? Xcode { get; init; }
    /// <summary>Other schemes of the project verified compatible with the selected Xcode for this SDK.</summary>
    public IReadOnlyList<string> CompatibleSchemes { get; init; } = [];
    /// <summary>Installed Xcodes whose own SDK range (read from disk) accepts every target of this scheme.</summary>
    public IReadOnlyList<XcodeInstallation> CompatibleXcodes { get; init; } = [];
    public required string Detail { get; init; }
    public string? Fix { get; init; }
    public string SdkDisplay => Sdk == "iphonesimulator" ? "iPhoneSimulator" : "iPhoneOS";
    public IReadOnlyList<IosTargetDeployment> Outside =>
        Range is null ? [] : Targets.Where(t => Range.Contains(t.DeploymentTarget) == false).ToList();
}

/// <summary>
/// Is the selected Xcode able to build this scheme, for this configuration and SDK? Each target the scheme builds — the
/// archive (device) or run (Simulator) entries and every target they depend on, such as embedded extensions — is checked
/// with its own effective deployment target against the range of the SDK actually used. Device and Simulator are judged
/// separately; a device-SDK result is never reused for a Simulator build. HCG never changes a deployment target.
/// </summary>
public static class IosSchemeCompatibility
{
    public static string SdkFor(bool simulator) => simulator ? "iphonesimulator" : "iphoneos";

    public static IosSchemeInfo? FindScheme(IosProjectInfo ios, string? name) =>
        ios.Schemes.FirstOrDefault(s => s.Name == name) ?? (name is null ? ios.Schemes.FirstOrDefault(s => ios.AppTargets.Any(t => t.Name == s.Name)) ?? ios.Schemes.FirstOrDefault() : null);

    public static string DefaultConfiguration(IosProjectInfo ios, IosSchemeInfo? scheme, bool simulator)
    {
        var wanted = simulator ? scheme?.LaunchConfiguration ?? "Debug" : scheme?.ArchiveConfiguration ?? "Release";
        return ios.Configurations.Contains(wanted) ? wanted : ios.Configurations.FirstOrDefault() ?? wanted;
    }

    /// <summary>The targets a scheme builds for this destination, followed by their dependencies (transitively).</summary>
    public static IReadOnlyList<IosTargetDeployment> TargetsOf(IosProjectInfo ios, IosSchemeInfo? scheme, string? schemeName, string configuration, bool simulator)
    {
        var sdk = SdkFor(simulator);
        var entries = (simulator ? scheme?.RunTargets : scheme?.ArchiveTargets) is { Count: > 0 } e ? e
            : (simulator ? scheme?.ArchiveTargets : scheme?.RunTargets) is { Count: > 0 } other ? other
            : [scheme?.Name ?? schemeName ?? ios.AppTargets.FirstOrDefault()?.Name ?? ""];
        var byName = ios.Targets.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var result = new List<IosTargetDeployment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string name, bool dependency)
        {
            if (!seen.Add(name) || !byName.TryGetValue(name, out var t) || t.Resolved.Count == 0) return; // non-iOS targets have no iOS settings
            var s = t.SettingsFor(configuration, sdk);
            result.Add(new IosTargetDeployment(t.Name, s?.DeploymentTarget, s?.DeploymentTargetSource, dependency, s?.Unresolved ?? [$"no {configuration} configuration"]));
            foreach (var d in t.Dependencies) Visit(d, true);
        }
        foreach (var n in entries) Visit(n, false);
        return result;
    }

    public static IosSchemeCompatibilityResult Evaluate(IosProjectInfo ios, string? schemeName, string? configuration, bool simulator, XcodeStatus? x, bool includeAlternatives = true)
    {
        var scheme = FindScheme(ios, schemeName);
        var name = scheme?.Name ?? schemeName ?? ios.AppTargets.FirstOrDefault()?.Name ?? "?";
        var config = configuration is { Length: > 0 } c && ios.Configurations.Contains(c) ? c : DefaultConfiguration(ios, scheme, simulator);
        var sdk = SdkFor(simulator);
        var sdkName = simulator ? "iPhoneSimulator" : "iPhoneOS";
        var targets = TargetsOf(ios, scheme, schemeName, config, simulator);
        var listed = string.Join(", ", targets.Select(t => $"{t.Target}{(t.IsDependency ? " (dependency)" : "")}: iOS {t.DeploymentTarget ?? "not resolved"}"));
        IosSchemeCompatibilityResult Result(IosToolchainFit fit, string detail, string? fix = null, IosDeploymentRange? range = null, string? xcode = null) => new()
        {
            Fit = fit, Scheme = name, Configuration = config, Sdk = sdk, Targets = targets, Range = range, Xcode = xcode, Detail = detail, Fix = fix,
        };

        if (x is null || x.State is not (XcodeState.Ready or XcodeState.SdkMissing))
            return Result(IosToolchainFit.ToolchainMissing, x?.Diagnosis ?? "Xcode has not been detected yet.", x?.CorrectiveAction);
        var xcode = $"Xcode {x.VerifiedVersion} ({x.ActiveDeveloperDirectory})";
        if (targets.Count == 0)
            return Result(IosToolchainFit.NotVerifiable, $"Scheme {name} names no iOS target HCG can read; xcodebuild will check the deployment targets.", xcode: xcode);
        var range = x.RangeFor(sdk);
        if (range is null)
            return Result(IosToolchainFit.NotVerifiable,
                $"{listed}. The {sdkName} SDK range of {xcode} could not be read (no SDKSettings.json); xcodebuild will check it.", xcode: xcode);

        var outside = targets.Where(t => range.Contains(t.DeploymentTarget) == false).ToList();
        var unknown = targets.Where(t => range.Contains(t.DeploymentTarget) is null).ToList();
        var compatibleXcodes = x.Installed.Where(i => i.DeveloperDirectory != x.ActiveDeveloperDirectory
                && (simulator ? i.SimulatorRange : i.IosRange) is { } r && targets.All(t => r.Contains(t.DeploymentTarget) == true))
            .GroupBy(i => i.Version).Select(g => g.First()).ToList();
        var alternatives = includeAlternatives
            ? ios.Schemes.Where(s => s.Name != name)
                .Where(s => Evaluate(ios, s.Name, configuration is { Length: > 0 } ? config : null, simulator, x, includeAlternatives: false).Fit == IosToolchainFit.Compatible)
                .Select(s => s.Name).ToList()
            : [];

        if (outside.Count == 0 && unknown.Count == 0)
            return Result(IosToolchainFit.Compatible, $"{listed} — within {xcode}'s {sdkName} {range.SdkVersion} SDK range iOS {range.Minimum}–{range.Maximum}.", range: range, xcode: xcode)
                with { CompatibleSchemes = alternatives, CompatibleXcodes = compatibleXcodes };
        if (outside.Count == 0)
            return Result(IosToolchainFit.NotVerifiable,
                $"{string.Join(", ", unknown.Select(t => $"{t.Target}: deployment target not resolved statically ({string.Join("; ", t.Unresolved.DefaultIfEmpty("not set — Xcode uses the SDK default"))})"))}. xcodebuild will check it against iOS {range.Minimum}–{range.Maximum}.",
                range: range, xcode: xcode) with { CompatibleSchemes = alternatives, CompatibleXcodes = compatibleXcodes };

        var first = outside[0];
        var below = IosDeploymentRange.Parse(first.DeploymentTarget) < IosDeploymentRange.Parse(range.Minimum);
        var detail = $"{string.Join(", ", outside.Select(t => $"{t.Target}{(t.IsDependency ? " (dependency)" : "")} needs iOS {t.DeploymentTarget}{(t.Source is null ? "" : $" (from {t.Source})")}"))} in scheme {name} ({config}), " +
                     $"but {xcode} with the {sdkName} {range.SdkVersion} SDK accepts iOS {range.Minimum}–{range.Maximum} ({(below ? "it is below the minimum" : "it is above the maximum")}). " +
                     $"xcodebuild would stop with “The iOS deployment target 'IPHONEOS_DEPLOYMENT_TARGET' is set to {first.DeploymentTarget}, but the range of supported deployment target versions is {range.Minimum} to {range.Maximum}”. " +
                     "HCG does not change the project's deployment target.";
        var routes = new List<string>();
        var otherInstalled = x.Installed.Where(i => i.DeveloperDirectory != x.ActiveDeveloperDirectory).ToList();
        if (compatibleXcodes.Count > 0)
            routes.Add($"To keep scheme {name} (iOS {first.DeploymentTarget}): select {string.Join(" or ", compatibleXcodes.Select(i => $"Xcode {i.Version} ({i.AppPath})"))} in Toolchains › Xcode — its {sdkName} SDK accepts iOS {(simulator ? compatibleXcodes[0].SimulatorRange : compatibleXcodes[0].IosRange)!.Minimum} and later (read from that Xcode); HCG uses it for its own builds only");
        else
            routes.Add($"To keep scheme {name} (iOS {first.DeploymentTarget}): an Xcode whose {sdkName} SDK accepts iOS {first.DeploymentTarget} is required. " +
                       (otherInstalled.Count == 0 ? "No other Xcode is installed on this Mac."
                           : "None of the installed Xcodes does: " + string.Join("; ", otherInstalled.Select(i => $"Xcode {i.Version}: {((simulator ? i.SimulatorRange : i.IosRange) is { } r ? $"iOS {r.Minimum}–{r.Maximum}" : "range not readable")}")) + ".") +
                       " Install one whose SDK minimum is that low, then select it in Toolchains › Xcode");
        routes.AddRange(alternatives.Select(a => $"or use scheme {a}, which is verified compatible with this Xcode ({string.Join(", ", TargetsOf(ios, FindScheme(ios, a), a, config, simulator).Select(t => $"iOS {t.DeploymentTarget}").Distinct())}); it does not install on older iOS versions"));
        return Result(IosToolchainFit.Incompatible, detail, string.Join("; ", routes) + ".", range, xcode) with { CompatibleSchemes = alternatives, CompatibleXcodes = compatibleXcodes };
    }
}
