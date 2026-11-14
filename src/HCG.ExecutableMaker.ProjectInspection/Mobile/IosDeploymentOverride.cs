using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

public enum IosOverrideChange
{
    /// <summary>The target's deployment version is below the new minimum and is raised to it.</summary>
    Raised,
    /// <summary>The target already targets exactly the new minimum.</summary>
    Unchanged,
    /// <summary>The target targets a newer iOS: a build-wide override would LOWER it (refused).</summary>
    Lowered,
    /// <summary>The target's deployment version could not be resolved statically.</summary>
    NotResolved,
}

/// <summary>What a build-only minimum iOS does to one target the scheme builds.</summary>
public sealed record IosOverrideEffect(string Target, string Kind, bool IsDependency, string? From, string To, IosOverrideChange Change);

/// <summary>
/// The inspected effects of raising the minimum iOS for one build: which targets change, what could break, what the
/// resulting app can no longer be installed on, and whether HCG can apply it at all (<see cref="Supported"/>).
/// </summary>
public sealed record IosDeploymentOverridePlan
{
    public required string Value { get; init; }
    public required string Scheme { get; init; }
    public IReadOnlyList<IosOverrideEffect> Effects { get; init; } = [];
    /// <summary>Why the override cannot be applied (empty = it can).</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];
    /// <summary>Effects HCG cannot rule out before building (packages, unresolved targets) and side effects to know about.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>The lowest deployment target among the targets that are raised (what the project supports today).</summary>
    public string? ProjectMinimum { get; init; }
    public bool Supported => Blockers.Count == 0;
    /// <summary>The single build setting passed to xcodebuild.</summary>
    public string BuildSetting => $"{IosDeploymentOverride.Setting}={Value}";

    /// <summary>What the user gives up, in one sentence.</summary>
    public string Consequence =>
        $"The app built with this override requires iOS {Value} or later: it will no longer install on iPhones or iPads running an iOS version earlier than {Value}" +
        (ProjectMinimum is { } min ? $" (the project itself supports iOS {min} and later)." : ".");

    /// <summary>Where the change lives: only in this build's xcodebuild command.</summary>
    public string Scope =>
        $"The project is not edited: HCG passes {BuildSetting} to xcodebuild for this build only. The imported copy, your original folder and the scheme keep their own deployment targets" +
        (ProjectMinimum is { } min ? $" (iOS {min})." : ".");

    /// <summary>The full explanation shown before the user accepts it.</summary>
    public string Describe()
    {
        var lines = new List<string> { Consequence, "", Scope, "", $"Targets {Scheme} builds:" };
        lines.AddRange(Effects.Select(e => e.Change switch
        {
            IosOverrideChange.Raised => $"• {e.Target} ({e.Kind}{(e.IsDependency ? ", dependency" : "")}): iOS {e.From} → iOS {e.To}",
            IosOverrideChange.Unchanged => $"• {e.Target} ({e.Kind}{(e.IsDependency ? ", dependency" : "")}): already iOS {e.To}",
            IosOverrideChange.Lowered => $"• {e.Target} ({e.Kind}{(e.IsDependency ? ", dependency" : "")}): iOS {e.From} — would be LOWERED to iOS {e.To}",
            _ => $"• {e.Target} ({e.Kind}{(e.IsDependency ? ", dependency" : "")}): not resolved statically → iOS {e.To}",
        }));
        if (Blockers.Count > 0) { lines.Add(""); lines.Add("Cannot be applied:"); lines.AddRange(Blockers.Select(b => "• " + b)); }
        if (Warnings.Count > 0) { lines.Add(""); lines.Add("Also note:"); lines.AddRange(Warnings.Select(w => "• " + w)); }
        return string.Join('\n', lines);
    }
}

/// <summary>
/// The third way out of a deployment-target incompatibility (after another Xcode and another scheme): raise the minimum iOS
/// for one build, explicitly. It is applied as a single xcodebuild build setting, so the project's files are never edited,
/// and it is only offered when HCG has confirmed that the selected Xcode accepts the new value for both SDKs and that no
/// target would be lowered by it.
/// </summary>
public static partial class IosDeploymentOverride
{
    public const string Setting = "IPHONEOS_DEPLOYMENT_TARGET";

    [GeneratedRegex(@"^\d{1,2}(\.\d{1,2}){0,2}$")]
    private static partial Regex VersionPattern();

    public static bool IsValidValue(string? value) => value is not null && VersionPattern().IsMatch(value);

    public static string KindOf(string? productType) => productType switch
    {
        "com.apple.product-type.application" => "app",
        "com.apple.product-type.app-extension" or "com.apple.product-type.app-extension.messages" => "app extension",
        "com.apple.product-type.extensionkit-extension" => "extension",
        "com.apple.product-type.application.on-demand-install-capable" => "App Clip",
        "com.apple.product-type.framework" => "framework",
        "com.apple.product-type.library.static" => "static library",
        "com.apple.product-type.library.dynamic" => "dynamic library",
        "com.apple.product-type.bundle" => "bundle",
        "com.apple.product-type.bundle.unit-test" => "unit tests",
        "com.apple.product-type.bundle.ui-testing" => "UI tests",
        null => "target",
        _ => productType.Split('.').Last(),
    };

    private static bool IsExtension(string kind) => kind is "app extension" or "extension" or "App Clip";

    /// <summary>
    /// The lowest minimum that makes the selected scheme buildable with the selected Xcode: the highest SDK minimum among the
    /// SDKs (device, Simulator) for which a target is below it. Null when nothing is below a minimum (a target above the
    /// SDK's maximum is not fixed by raising anything) or the ranges are not known.
    /// </summary>
    public static string? Suggest(IosProjectInfo ios, string? schemeName, string? configuration, XcodeStatus? x)
    {
        if (x is null) return null;
        var scheme = IosSchemeCompatibility.FindScheme(ios, schemeName);
        Version? best = null;
        string? bestText = null;
        foreach (var simulator in new[] { false, true })
        {
            if (x.RangeFor(IosSchemeCompatibility.SdkFor(simulator)) is not { } range || IosDeploymentRange.Parse(range.Minimum) is not { } min) continue;
            var config = configuration ?? IosSchemeCompatibility.DefaultConfiguration(ios, scheme, simulator);
            var below = IosSchemeCompatibility.TargetsOf(ios, scheme, schemeName, config, simulator)
                .Any(t => IosDeploymentRange.Parse(t.DeploymentTarget) is { } v && v < min);
            if (below && (best is null || min > best)) (best, bestText) = (min, range.Minimum);
        }
        return bestText;
    }

    /// <summary>Inspects raising every target of the scheme to <paramref name="value"/> for one build, for both SDKs.</summary>
    public static IosDeploymentOverridePlan Inspect(IosProjectInfo ios, string? schemeName, string? configuration, XcodeStatus? x, string value)
    {
        var scheme = IosSchemeCompatibility.FindScheme(ios, schemeName);
        var name = scheme?.Name ?? schemeName ?? ios.AppTargets.FirstOrDefault()?.Name ?? "?";
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (!IsValidValue(value))
            return new IosDeploymentOverridePlan { Value = value, Scheme = name, Blockers = [$"“{value}” is not an iOS version (expected e.g. 15.0)."] };
        var wanted = IosDeploymentRange.Parse(value)!;
        if (x is null || x.State is not (XcodeState.Ready or XcodeState.SdkMissing))
            blockers.Add("No usable Xcode is selected, so HCG cannot confirm that its SDKs accept iOS " + value + ".");

        // Per target: its deployment target for each SDK it is built for (device and Simulator configurations can differ).
        var byTarget = new Dictionary<string, (IosTargetDeployment First, List<string?> Froms)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var simulator in new[] { false, true })
        {
            var sdk = IosSchemeCompatibility.SdkFor(simulator);
            var sdkName = simulator ? "iPhoneSimulator" : "iPhoneOS";
            if (x is not null && x.State is XcodeState.Ready or XcodeState.SdkMissing)
            {
                var range = x.RangeFor(sdk);
                if (range is null)
                    blockers.Add($"The {sdkName} SDK range of the selected Xcode could not be read ({x.RangeDiagnostics.GetValueOrDefault(sdk) ?? "no SDKSettings.json"}). " +
                                 "HCG raises the minimum only to a version it has confirmed the SDK accepts.");
                else if (range.Contains(value) == false)
                    blockers.Add($"iOS {value} is outside the selected Xcode's {sdkName} {range.SdkVersion} SDK range (iOS {range.Minimum}–{range.Maximum}).");
            }
            var config = configuration ?? IosSchemeCompatibility.DefaultConfiguration(ios, scheme, simulator);
            foreach (var t in IosSchemeCompatibility.TargetsOf(ios, scheme, schemeName, config, simulator))
            {
                if (!byTarget.TryGetValue(t.Target, out var entry)) { entry = (t, []); byTarget[t.Target] = entry; order.Add(t.Target); }
                entry.Froms.Add(t.DeploymentTarget);
            }
        }

        var effects = new List<IosOverrideEffect>();
        foreach (var target in order)
        {
            var (t, froms) = byTarget[target];
            var kind = KindOf(ios.Targets.FirstOrDefault(x => x.Name == target)?.ProductType);
            var parsed = froms.Select(IosDeploymentRange.Parse).ToList();
            var change = parsed.Any(p => p is not null && p > wanted) ? IosOverrideChange.Lowered
                : parsed.Any(p => p is null) ? IosOverrideChange.NotResolved
                : parsed.Any(p => p < wanted) ? IosOverrideChange.Raised
                : IosOverrideChange.Unchanged;
            var from = string.Join(" / ", froms.Select(f => f ?? "not resolved").Distinct());
            effects.Add(new IosOverrideEffect(target, kind, t.IsDependency, from, value, change));
            if (change == IosOverrideChange.Lowered)
                blockers.Add($"{target} ({kind}) targets iOS {from}, which is newer than iOS {value}. One build-wide {Setting} would lower it, " +
                             "and code that relies on newer iOS APIs could then fail or misbehave. HCG never lowers a deployment target.");
            if (change == IosOverrideChange.NotResolved)
                warnings.Add($"{target}: the deployment target could not be resolved statically ({string.Join("; ", t.Unresolved.DefaultIfEmpty("not set"))}); the override sets it to iOS {value} as well.");
        }
        if (effects.Count == 0)
            blockers.Add($"Scheme {name} names no iOS target HCG can read, so the effect of the override cannot be inspected.");
        else if (blockers.Count == 0 && effects.All(e => e.Change == IosOverrideChange.Unchanged))
            blockers.Add($"Every target of {name} already targets iOS {value}; there is nothing to raise.");

        var extensions = effects.Where(e => IsExtension(e.Kind)).Select(e => e.Target).ToList();
        if (extensions.Count > 0)
            warnings.Add($"Embedded extensions ({string.Join(", ", extensions)}) are raised together with the app, so they keep matching its minimum iOS.");
        if (ios.UsesSwiftPackages)
            warnings.Add($"Swift package dependencies are built with {Setting}={value} too. HCG cannot read their declared platforms before building; " +
                         $"a package that requires a newer iOS than {value} would fail to compile, and xcodebuild names it.");
        if (ios.UsesCocoaPods)
            warnings.Add($"CocoaPods targets in the workspace are built with {Setting}={value} too; a pod that requires a newer iOS than {value} would fail to compile.");
        var untouched = ios.Targets.Where(t => t.Resolved.Count > 0 && !byTarget.ContainsKey(t.Name)).Select(t => t.Name).ToList();
        if (untouched.Count > 0)
            warnings.Add($"Not built by {name}, so not affected: {string.Join(", ", untouched)}.");

        var projectMinimum = effects.Where(e => e.Change == IosOverrideChange.Raised)
            .SelectMany(e => e.From!.Split(" / "))
            .Select(f => (Text: f, Version: IosDeploymentRange.Parse(f)))
            .Where(p => p.Version is not null && p.Version < wanted)
            .OrderBy(p => p.Version).Select(p => p.Text).FirstOrDefault();
        return new IosDeploymentOverridePlan
        {
            Value = value, Scheme = name, Effects = effects, Blockers = blockers.Distinct().ToList(), Warnings = warnings, ProjectMinimum = projectMinimum,
        };
    }
}
