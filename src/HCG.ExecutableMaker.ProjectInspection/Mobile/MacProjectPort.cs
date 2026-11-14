namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>Retargets a private import copy of a single-target Mac application.</summary>
public static class MacProjectPort
{
    private sealed record Span(Dictionary<string, object> Owner, string? Key, int Start, int End);

    public sealed record Result(string ProjectFile, IReadOnlyList<string> ReviewFiles);

    public static Result PrepareCopy(string root, string relativeProject, string? expectedBundleId = null)
    {
        var index = SourceIndex.Build(root);
        var projects = XcodeProjectAnalyzer.FindProjects(index);
        if (projects.Count > 1)
            throw new InvalidDataException("Select a standalone project folder. This source tree contains multiple Xcode projects.");
        if (!projects.Contains(relativeProject, StringComparer.Ordinal))
            throw new InvalidDataException("The selected Xcode project is missing from the imported source.");
        var file = index.FullPath(relativeProject + "/project.pbxproj");
        if (new FileInfo(file).Length > 2_000_000)
            throw new InvalidDataException("The project file exceeds the 2 MB porting limit.");
        var spans = new List<Span>();
        string original = File.ReadAllText(file);
        var plist = (Dictionary<string, object>)OpenStepPlist.Parse(original,
            (owner, key, start, end) => spans.Add(new(owner, key, start, end)));
        var objects = (Dictionary<string, object>)plist["objects"];
        Dictionary<string, object> Resolve(object id) => (Dictionary<string, object>)objects[(string)id];
        var project = Resolve(plist["rootObject"]);
        var targets = ((List<object>)project["targets"]).Select(Resolve).ToList();
        if (targets.Count != 1 || targets[0].GetValueOrDefault("productType") as string != "com.apple.product-type.application")
            throw new InvalidDataException("Automatic porting currently supports one standalone app target. Projects with helpers, tests or extensions require target-by-target porting.");
        var target = targets[0];
        var overrides = new Dictionary<Dictionary<string, object>, Dictionary<string, string>>();
        var platformValues = new List<string>();
        var ids = new Dictionary<string, string>();
        foreach (var node in new[] { project, target })
        {
            var list = Resolve(node["buildConfigurationList"]);
            foreach (var config in ((List<object>)list["buildConfigurations"]).Select(Resolve))
            {
                if (config.ContainsKey("baseConfigurationReference") || config.ContainsKey("baseConfigurationReferenceAnchor"))
                    throw new InvalidDataException("This project uses external build configurations. Review its xcconfig settings before porting.");
                var settings = (Dictionary<string, object>)config["buildSettings"];
                foreach (string key in new[] { "SDKROOT", "SUPPORTED_PLATFORMS" })
                    if (settings.TryGetValue(key, out var value))
                        platformValues.AddRange(value is List<object> values ? values.Cast<string>() : ((string)value).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (settings.GetValueOrDefault("PRODUCT_BUNDLE_IDENTIFIER") is string id)
                    ids[(string)config["name"]] = id;
                var changes = new Dictionary<string, string>
                {
                    ["SDKROOT"] = "iphoneos", ["SUPPORTED_PLATFORMS"] = "iphoneos iphonesimulator",
                    ["IPHONEOS_DEPLOYMENT_TARGET"] = "16.0", ["TARGETED_DEVICE_FAMILY"] = "1,2",
                };
                if (ReferenceEquals(node, target))
                {
                    changes["LD_RUNPATH_SEARCH_PATHS"] = "$(inherited) @executable_path/Frameworks";
                    changes["INFOPLIST_KEY_UILaunchScreen_Generation"] = "YES";
                }
                overrides[settings] = changes;
            }
        }
        if (platformValues.Count == 0 || platformValues.Any(p => p != "macosx"))
            throw new InvalidDataException("Select a macOS-only Xcode project. For an existing iOS target, use Import Folder.");
        if (expectedBundleId is not null && !ids.Values.Contains(expectedBundleId, StringComparer.Ordinal))
            throw new InvalidDataException("The source bundle identifier could not be matched to the selected Mac app. Choose its matching source or port the source project directly.");
        var edits = new List<(int Start, int End, string Text)>();
        foreach (var span in spans)
        {
            if (!overrides.TryGetValue(span.Owner, out var changes)) continue;
            if (span.Key is null)
            {
                string insert = string.Concat(changes.Select(pair => $"\n\t\t\t\t{pair.Key} = \"{pair.Value}\";"));
                edits.Add((span.Start, span.End, insert));
                continue;
            }
            var key = span.Key.Split('[')[0];
            if (changes.ContainsKey(key) || key is "MACOSX_DEPLOYMENT_TARGET" or "ENABLE_HARDENED_RUNTIME" or "ENABLE_APP_SANDBOX")
                edits.Add((span.Start, span.End, ""));
        }
        var result = new System.Text.StringBuilder(original);
        // For compact plists a first entry can begin at the insertion offset.
        foreach (var edit in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
            result.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
        File.WriteAllText(file, result.ToString());
        var review = index.WithExtension(".entitlements", ".xib", ".storyboard").ToList();
        foreach (var source in index.WithExtension(".swift", ".m", ".mm", ".h"))
        {
            var text = AndroidGradleAnalyzer.Read(index, source);
            if (text.Contains("AppKit", StringComparison.Ordinal) || text.Contains("Cocoa", StringComparison.Ordinal)
                || text.Contains("NSApplication", StringComparison.Ordinal) || text.Contains("NSWindow", StringComparison.Ordinal))
                review.Add(source);
        }
        return new Result(file, review);
    }
}
