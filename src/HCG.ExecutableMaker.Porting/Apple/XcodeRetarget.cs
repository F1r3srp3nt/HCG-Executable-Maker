using System.Text;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Porting.Apple;

/// <summary>
/// Retargets a copy of a single-app-target macOS Xcode project to iPhone/iPad by editing build settings in place.
/// Every other byte of project.pbxproj (comments, ordering, unrelated settings) is preserved, so the diff shows only
/// the platform changes.
/// </summary>
internal static class XcodeRetarget
{
    private sealed record Span(Dictionary<string, object> Owner, string? Key, int Start, int End);

    /// <summary>macOS-only settings dropped from the iOS copy.</summary>
    private static readonly string[] Removed =
    [
        "MACOSX_DEPLOYMENT_TARGET", "ENABLE_HARDENED_RUNTIME", "ENABLE_APP_SANDBOX", "COMBINE_HIDPI_IMAGES",
        "INFOPLIST_KEY_LSApplicationCategoryType", "INFOPLIST_KEY_NSMainStoryboardFile", "INFOPLIST_KEY_NSPrincipalClass",
        "ENABLE_USER_SELECTED_FILES", "ENABLE_INCOMING_NETWORK_CONNECTIONS", "ENABLE_OUTGOING_NETWORK_CONNECTIONS",
        "CODE_SIGN_ENTITLEMENTS", "REGISTER_APP_GROUPS",
    ];

    private sealed record Model(Dictionary<string, object> Project, List<Dictionary<string, object>> Targets,
        List<(Dictionary<string, object> Settings, bool IsTarget, string Name)> Configs, List<Span> Spans, List<string> Problems);

    private static Model Read(string text)
    {
        var spans = new List<Span>();
        var root = (Dictionary<string, object>)OpenStepPlist.Parse(text, (owner, key, start, end) => spans.Add(new Span(owner, key, start, end)));
        var objects = (Dictionary<string, object>)root["objects"];
        Dictionary<string, object> Resolve(object id) => (Dictionary<string, object>)objects[(string)id];
        var project = Resolve(root["rootObject"]);
        var targets = ((List<object>)project["targets"]).Select(Resolve).ToList();
        var problems = new List<string>();
        var configs = new List<(Dictionary<string, object>, bool, string)>();
        foreach (var (node, isTarget) in targets.Select(t => (t, true)).Prepend((project, false)))
        {
            var list = Resolve(node["buildConfigurationList"]);
            foreach (var config in ((List<object>)list["buildConfigurations"]).Select(Resolve))
            {
                if (config.ContainsKey("baseConfigurationReference") || config.ContainsKey("baseConfigurationReferenceAnchor"))
                    problems.Add("xcconfig");
                configs.Add(((Dictionary<string, object>)config["buildSettings"], isTarget, (string)config["name"]));
            }
        }
        return new Model(project, targets, configs, spans, problems);
    }

    /// <summary>Existing display name and bundle identifier of the app target (to prefill the port options).</summary>
    public static (string? Name, string? BundleId) Identity(string text)
    {
        try
        {
            var model = Read(text);
            var settings = model.Configs.Where(c => c.IsTarget).Select(c => c.Settings).ToList();
            string? Get(string key) => settings.Select(s => s.GetValueOrDefault(key) as string).FirstOrDefault(v => !string.IsNullOrEmpty(v) && !v.Contains("$(", StringComparison.Ordinal));
            return (Get("INFOPLIST_KEY_CFBundleDisplayName") ?? model.Targets.FirstOrDefault()?.GetValueOrDefault("name") as string, Get("PRODUCT_BUNDLE_IDENTIFIER"));
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidCastException) { return (null, null); }
    }

    public static IReadOnlyList<PortingFinding> Inspect(string text, string file)
    {
        var findings = new List<PortingFinding>();
        PortingFinding F(FindingDisposition d, string title, string detail, string? guidance = null) =>
            new() { Category = FindingCategory.BuildSystem, Disposition = d, Title = title, Detail = detail, File = file, Guidance = guidance };
        Model model;
        try { model = Read(text); }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidCastException)
        {
            return [F(FindingDisposition.Blocker, "project.pbxproj could not be read", ex.Message)];
        }
        var apps = model.Targets.Where(t => t.GetValueOrDefault("productType") as string == "com.apple.product-type.application").ToList();
        if (model.Targets.Count != 1 || apps.Count != 1)
            findings.Add(F(FindingDisposition.Blocker, $"{model.Targets.Count} targets ({apps.Count} app)",
                "Automatic retargeting supports exactly one app target (no extensions, helpers or test targets) in this version.",
                "Port the app target alone (e.g. move its sources into a Swift package or a single-target project)."));
        if (model.Problems.Contains("xcconfig"))
            findings.Add(F(FindingDisposition.Blocker, "Build settings come from .xcconfig files",
                "HCG edits settings in project.pbxproj only; xcconfig files may re-apply macOS settings.", "Move the settings into the project or port the xcconfig files by hand."));
        if (model.Configs.Any(c => c.Settings.ContainsKey("CODE_SIGN_ENTITLEMENTS")))
            findings.Add(F(FindingDisposition.NeedsReview, "macOS entitlements file removed from the iOS target",
                "macOS sandbox entitlements do not apply on iOS; capabilities must be added for iOS in Xcode.", "Re-add needed capabilities (e.g. iCloud, push) for iOS."));
        if (model.Project.ContainsKey("packageReferences"))
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.Dependency, Disposition = FindingDisposition.NeedsReview, File = file,
                Title = "Swift package dependencies", Detail = "Their iOS support is only known when Xcode resolves and builds them.",
            });
        findings.Add(F(FindingDisposition.AutoAdapted, $"App target retargeted to iOS {MacToIosPorter.IosMajor}.0+ (iPhone and iPad)",
            "SDKROOT=iphoneos, SUPPORTED_PLATFORMS=iphoneos iphonesimulator, TARGETED_DEVICE_FAMILY=1,2, generated launch screen and scene manifest; macOS-only settings (deployment target, hardened runtime, sandbox, category) removed."));
        return findings;
    }

    public static string Apply(string text, PortOptions options, IReadOnlyList<string> requiredTexts, int iosMajor)
    {
        var model = Read(text);
        var overrides = new Dictionary<Dictionary<string, object>, Dictionary<string, string>>(ReferenceEqualityComparer.Instance);
        foreach (var (settings, isTarget, _) in model.Configs)
        {
            var changes = new Dictionary<string, string>
            {
                ["SDKROOT"] = "iphoneos",
                ["SUPPORTED_PLATFORMS"] = "iphoneos iphonesimulator",
                ["IPHONEOS_DEPLOYMENT_TARGET"] = $"{iosMajor}.0",
                ["TARGETED_DEVICE_FAMILY"] = "1,2",
            };
            if (isTarget)
            {
                changes["LD_RUNPATH_SEARCH_PATHS"] = "$(inherited) @executable_path/Frameworks";
                changes["INFOPLIST_KEY_UILaunchScreen_Generation"] = "YES";
                changes["INFOPLIST_KEY_UIApplicationSceneManifest_Generation"] = "YES";
                changes["PRODUCT_BUNDLE_IDENTIFIER"] = options.AppId;
                changes["INFOPLIST_KEY_CFBundleDisplayName"] = options.AppName;
                changes["MARKETING_VERSION"] = options.Version;
                changes["CURRENT_PROJECT_VERSION"] = options.BuildNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (var key in requiredTexts)
                    if (options.UsageDescriptions.GetValueOrDefault(key) is { } v) changes["INFOPLIST_KEY_" + key] = v.Trim();
            }
            overrides[settings] = changes;
        }
        var edits = new List<(int Start, int End, string Text)>();
        foreach (var span in model.Spans)
        {
            if (!overrides.TryGetValue(span.Owner, out var changes)) continue;
            if (span.Key is null)
            {
                edits.Add((span.Start, span.End, string.Concat(changes.Select(p => $"\n\t\t\t\t{p.Key} = {Quote(p.Value)};"))));
                continue;
            }
            var key = span.Key.Split('[')[0];
            // Ad-hoc identity "-" is a macOS convention; iOS uses Xcode's default identity with the chosen team.
            var adHocIdentity = key == "CODE_SIGN_IDENTITY" && span.Owner.GetValueOrDefault(span.Key) as string == "-";
            if (changes.ContainsKey(key) || Removed.Contains(key) || adHocIdentity)
            {
                // Remove the whole line (indentation and preceding newline), not only the entry.
                var start = span.Start;
                while (start > 0 && text[start - 1] is ' ' or '\t') start--;
                if (start > 0 && text[start - 1] == '\n') start--;
                edits.Add((start, span.End, ""));
            }
        }
        var result = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
            result.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
        return result.ToString();
    }

    private static string Quote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
