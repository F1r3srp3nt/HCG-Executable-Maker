using System.Text.Json;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.MacXcode;

/// <summary>
/// Plans the xcodebuild invocation for a native macOS app. The imported source is copied into HCG's per-build folder
/// first (ditto), and xcodebuild runs on that copy with its derived data, result bundle and Swift package checkouts in
/// the same folder — the imported source and the project's settings are never written to. Settings HCG passes on the
/// command line are listed by <see cref="Overrides"/> and logged before the build. Scripts that ship with the project
/// (INSTALL.command and the like) are never run.
/// </summary>
public static class MacXcodeBuildPlanner
{
    public sealed record Layout(string WorkRoot, string SourceCopy, string Container, bool IsWorkspace, string DerivedData, string ResultBundle, string SourcePackages)
    {
        public string TestResultBundle => ResultBundle[..^".xcresult".Length] + "-Tests.xcresult";
        public string ArchivePath => ResultBundle[..^".xcresult".Length] + ".xcarchive";
    }

    public const string ListKey = "xcode-list", SettingsKey = "xcode-settings";

    /// <summary>What xcodebuild -list -json reports for a project or a workspace.</summary>
    public sealed record ListInfo(string Name, IReadOnlyList<string> Schemes, IReadOnlyList<string> Targets, IReadOnlyList<string> Configurations);

    /// <summary>Parses <c>xcodebuild -list -json</c> ({"project": {...}} or {"workspace": {...}}); null with the reason when unreadable.</summary>
    public static (ListInfo? Info, string? Problem) ParseList(IEnumerable<string> captured)
    {
        var text = string.Join('\n', captured);
        var start = text.IndexOf('{');
        if (start < 0) return (null, "xcodebuild -list did not print JSON.");
        try
        {
            using var doc = JsonDocument.Parse(text[start..(text.LastIndexOf('}') + 1)]);
            var root = doc.RootElement;
            var node = root.TryGetProperty("project", out var p) ? p : root.TryGetProperty("workspace", out var w) ? w : default;
            if (node.ValueKind != JsonValueKind.Object) return (null, "xcodebuild -list printed neither a project nor a workspace.");
            IReadOnlyList<string> Arr(string key) => node.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];
            return (new ListInfo(node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", Arr("schemes"), Arr("targets"), Arr("configurations")), null);
        }
        catch (JsonException ex) { return (null, "xcodebuild -list printed JSON that could not be read: " + ex.Message); }
    }

    /// <summary>The scheme and configuration must be ones xcodebuild itself lists for the container.</summary>
    public static string? CheckList(IReadOnlyList<string> captured, MacXcodeBuildOptions o)
    {
        var (info, problem) = ParseList(captured);
        if (info is null) return problem;
        if (!info.Schemes.Contains(o.Scheme))
            return $"xcodebuild does not list the scheme {o.Scheme} for {info.Name} (it lists: {string.Join(", ", info.Schemes.DefaultIfEmpty("none"))}). " +
                   "Share the scheme in Xcode (Product › Scheme › Manage Schemes › Shared) and commit it, or choose a listed scheme.";
        if (info.Configurations.Count > 0 && !info.Configurations.Contains(o.Configuration))
            return $"The project has no configuration {o.Configuration} (it has: {string.Join(", ", info.Configurations)}).";
        return null;
    }

    /// <summary>A setting HCG passes to xcodebuild, why, and what the project itself says.</summary>
    public sealed record Override(string Setting, string Value, string Reason);

    /// <summary>The app xcodebuild reports for the scheme (from -showBuildSettings -json), and where it lands.</summary>
    public sealed record Resolved(string Target, string BuiltProductsDir, string FullProductName, string ExecutablePath,
        string? BundleIdentifier, string? DeploymentTarget, string? MarketingVersion, string? BuildVersion, string? Archs, string? SdkVersion, string? XcodeBuild, string? CodeSignIdentity)
    {
        /// <summary>A macOS path as xcodebuild reports it ('/'-separated on every host that reads it).</summary>
        public string AppPath => BuiltProductsDir.TrimEnd('/') + "/" + FullProductName;
    }

    public static void Validate(BuildSettings s, HostEnvironment host, string sourceDir)
    {
        if (host.Platform != TargetPlatform.MacOS)
            throw new BuildValidationException($"Native macOS apps are built with Xcode on a Mac. This computer runs {host.Platform.DisplayName()}; nothing was built.");
        var o = s.MacXcode ?? throw new BuildValidationException("No Xcode project was selected.");
        if (s.Platform != TargetPlatform.MacOS) throw new BuildValidationException("An Xcode macOS app can only target macOS.");
        MobileArgs.RequireToken(o.Scheme, "scheme", out _);
        MobileArgs.RequireToken(o.Configuration, "build configuration", out _);
        MobileArgs.RequireToken(o.AppTarget, "app target", out _);
        if (!o.ProjectPath.EndsWith(".xcodeproj", StringComparison.Ordinal)) throw new BuildValidationException($"'{o.ProjectPath}' is not an .xcodeproj.");
        if (!Directory.Exists(MobileArgs.Inside(sourceDir, o.ProjectPath))) throw new BuildValidationException($"The project {o.ProjectPath} is no longer in the imported source. Re-inspect the project.");
        if (o.WorkspacePath is { } ws && (!ws.EndsWith(".xcworkspace", StringComparison.Ordinal) || !Directory.Exists(MobileArgs.Inside(sourceDir, ws))))
            throw new BuildValidationException($"The workspace {ws} is not in the imported source.");
    }

    /// <summary>
    /// What HCG adds on xcodebuild's command line. Everything else — identity, version, deployment target, Info.plist,
    /// entitlements, signing — comes from the project unchanged.
    /// </summary>
    public static IReadOnlyList<Override> Overrides(MacXcodeBuildOptions o, string? projectArchs)
    {
        var list = new List<Override>
        {
            new("ARCHS", o.Architecture.Archs(), $"{o.Architecture.Label()} as chosen in HCG (the project sets {(projectArchs is null ? "no ARCHS: Xcode's default for Release is arm64 + x86_64" : $"ARCHS = {projectArchs}")})."),
            new("ONLY_ACTIVE_ARCH", "NO", "Build every architecture in ARCHS, not only this Mac's."),
            new("COMPILER_INDEX_STORE_ENABLE", "NO", "Skips Xcode's code index (an editor feature); no effect on the app."),
        };
        if (o.SignLocallyAdHoc)
        {
            list.Add(new("CODE_SIGN_IDENTITY", "-", "Sign ad hoc to run on this Mac (chosen in HCG): the project's signing identity is not used."));
            list.Add(new("CODE_SIGN_STYLE", "Manual", "Needed for the ad-hoc identity."));
            list.Add(new("DEVELOPMENT_TEAM", "", "No team for an ad-hoc signature."));
            list.Add(new("PROVISIONING_PROFILE_SPECIFIER", "", "No provisioning profile for an ad-hoc signature."));
        }
        return list;
    }

    public static Layout PrepareLayout(BuildSettings s, string buildDir, string sourceDir)
    {
        var o = s.MacXcode!;
        var work = Path.Combine(buildDir, "xcode");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var copy = Path.Combine(work, "source");
        var container = o.WorkspacePath ?? o.ProjectPath;
        return new Layout(work, copy, MobileArgs.Inside(copy, container), o.WorkspacePath is not null,
            Path.Combine(work, "DerivedData"), Path.Combine(work, PathGuard.SanitizeFileName(o.Scheme, "Build") + ".xcresult"), Path.Combine(work, "SourcePackages"));
    }

    /// <param name="resolvePackages">The project references Swift packages: resolve them in their own step (clear dependency errors).</param>
    /// <param name="hostArch">This Mac's architecture (arm64 or x86_64): tests run on it.</param>
    public static IReadOnlyList<BuildStep> Plan(BuildSettings s, Layout layout, string sourceDir, string xcodebuild, string? projectArchs, IReadOnlyDictionary<string, string> environment,
        bool resolvePackages = false, string hostArch = "arm64")
    {
        var o = s.MacXcode!;
        // xcodebuild buffers its output when it is not a terminal unless told otherwise; live logs need it unbuffered.
        var env = MobileArgs.With(environment, ("NSUnbufferedIO", "YES"));
        List<string> common =
        [
            layout.IsWorkspace ? "-workspace" : "-project", layout.Container,
            "-scheme", o.Scheme,
            "-configuration", o.Configuration,
            "-destination", "generic/platform=macOS",
            "-derivedDataPath", layout.DerivedData,
            "-clonedSourcePackagesDirPath", layout.SourcePackages,
        ];
        var overrides = Overrides(o, projectArchs).Select(x => $"{x.Setting}={x.Value}").ToList();
        var workDir = Path.GetDirectoryName(layout.Container)!;
        List<string> container = [layout.IsWorkspace ? "-workspace" : "-project", layout.Container];
        var steps = new List<BuildStep>
        {
            new(BuildStage.Preparing, "Copy the imported source into the build folder (the original is not built in or written to)",
                new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = [sourceDir, layout.SourceCopy], Environment = env, Timeout = TimeSpan.FromMinutes(10) }),
            new(BuildStage.Preparing, "Discover schemes, targets and configurations (xcodebuild -list)",
                new ProcessSpec { Executable = xcodebuild, Arguments = [.. container, "-list", "-json"], WorkingDirectory = workDir, Environment = env, Timeout = TimeSpan.FromMinutes(5) },
                CaptureStdout: true) { CaptureKey = ListKey, Check = captured => CheckList(captured, o) },
        };
        if (resolvePackages)
            steps.Add(new(BuildStage.Restoring, "Resolve Swift package dependencies (xcodebuild -resolvePackageDependencies)",
                new ProcessSpec { Executable = xcodebuild, Arguments = [.. container, "-scheme", o.Scheme, "-derivedDataPath", layout.DerivedData, "-clonedSourcePackagesDirPath", layout.SourcePackages, "-resolvePackageDependencies"],
                    WorkingDirectory = workDir, Environment = env, Timeout = TimeSpan.FromMinutes(20) }));
        steps.Add(new(BuildStage.Restoring, "Resolve build settings (xcodebuild -showBuildSettings)",
            new ProcessSpec { Executable = xcodebuild, Arguments = [.. common, .. overrides, "-showBuildSettings", "-json"], WorkingDirectory = workDir, Environment = env, Timeout = TimeSpan.FromMinutes(10) },
            CaptureStdout: true) { CaptureKey = SettingsKey });
        steps.Add(new(BuildStage.Compiling, $"Build {o.Scheme} ({o.Configuration}, {o.Architecture.Label()}) with xcodebuild",
            new ProcessSpec { Executable = xcodebuild, Arguments = [.. common, "-resultBundlePath", layout.ResultBundle, .. overrides, "build"], WorkingDirectory = workDir, Environment = env }));
        if (o.RunTests)
        {
            // The scheme's Test action, with its own configuration (usually Debug), on this Mac's architecture.
            var testOverrides = Overrides(o, projectArchs).Where(x => x.Setting is not ("ARCHS" or "ONLY_ACTIVE_ARCH")).Select(x => $"{x.Setting}={x.Value}");
            steps.Add(new(BuildStage.Compiling, $"Run the tests of {o.Scheme} (xcodebuild test)",
                new ProcessSpec { Executable = xcodebuild, Arguments = [.. container, "-scheme", o.Scheme, "-destination", $"platform=macOS,arch={hostArch}",
                    "-derivedDataPath", layout.DerivedData, "-clonedSourcePackagesDirPath", layout.SourcePackages, "-resultBundlePath", layout.TestResultBundle, .. testOverrides, "test"],
                    WorkingDirectory = workDir, Environment = env }));
        }
        if (o.CreateArchive || s.PackagingFormats.Contains(PackagingFormat.MacXcodeArchive))
            steps.Add(new(BuildStage.Packaging, $"Archive {o.Scheme} ({o.Configuration}) into an .xcarchive (xcodebuild archive)",
                new ProcessSpec { Executable = xcodebuild, Arguments = [.. common, "-archivePath", layout.ArchivePath, .. overrides, "archive"], WorkingDirectory = workDir, Environment = env }));
        return steps;
    }

    /// <summary>
    /// The app target's built product from xcodebuild's own settings (-showBuildSettings -json, captured stdout). Null
    /// with the reason when the scheme builds no app.
    /// </summary>
    public static (Resolved? App, string? Problem) Resolve(IEnumerable<string> captured, MacXcodeBuildOptions o)
    {
        var text = string.Join('\n', captured);
        var start = text.IndexOf('[');
        if (start < 0) return (null, "xcodebuild -showBuildSettings did not print its settings as JSON.");
        try
        {
            using var doc = JsonDocument.Parse(text[start..(text.LastIndexOf(']') + 1)]);
            var entries = doc.RootElement.EnumerateArray()
                .Select(e => (Target: e.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "",
                              Settings: e.TryGetProperty("buildSettings", out var b) ? b : default))
                .Where(e => e.Settings.ValueKind == JsonValueKind.Object).ToList();
            string? Get(JsonElement b, string key) => b.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } sv ? sv : null;
            var pick = entries.FirstOrDefault(e => e.Target == o.AppTarget);
            if (pick.Settings.ValueKind != JsonValueKind.Object)
                pick = entries.FirstOrDefault(e => Get(e.Settings, "WRAPPER_EXTENSION") == "app" && Get(e.Settings, "PRODUCT_TYPE") == "com.apple.product-type.application");
            if (pick.Settings.ValueKind != JsonValueKind.Object)
                return (null, $"The scheme {o.Scheme} builds no macOS application (targets: {string.Join(", ", entries.Select(e => e.Target))}).");
            var b = pick.Settings;
            var dir = Get(b, "TARGET_BUILD_DIR") ?? Get(b, "BUILT_PRODUCTS_DIR");
            var name = Get(b, "FULL_PRODUCT_NAME");
            var exe = Get(b, "EXECUTABLE_PATH");
            if (dir is null || name is null || exe is null || !name.EndsWith(".app", StringComparison.Ordinal))
                return (null, $"xcodebuild did not report where {pick.Target} builds its app (TARGET_BUILD_DIR / FULL_PRODUCT_NAME).");
            return (new Resolved(pick.Target, dir, name, exe, Get(b, "PRODUCT_BUNDLE_IDENTIFIER"), Get(b, "MACOSX_DEPLOYMENT_TARGET"),
                Get(b, "MARKETING_VERSION"), Get(b, "CURRENT_PROJECT_VERSION"), Get(b, "ARCHS"), Get(b, "SDK_VERSION"), Get(b, "XCODE_PRODUCT_BUILD_VERSION"), Get(b, "CODE_SIGN_IDENTITY")), null);
        }
        catch (JsonException ex)
        {
            return (null, "xcodebuild -showBuildSettings printed settings that could not be read: " + ex.Message);
        }
    }

    /// <summary>Checks the app exists and was written by this build (not a stale product from an earlier build).</summary>
    public static string? CheckFresh(Resolved app, DateTimeOffset buildStart)
    {
        var plist = Path.Combine(app.AppPath, "Contents", "Info.plist");
        if (!Directory.Exists(app.AppPath)) return $"xcodebuild reported success, but {app.FullProductName} is not at {app.BuiltProductsDir}.";
        if (!File.Exists(plist)) return $"{app.FullProductName} has no Contents/Info.plist.";
        if (File.GetLastWriteTimeUtc(plist) < buildStart.UtcDateTime.AddSeconds(-5)) return $"{app.FullProductName} predates this build: xcodebuild did not produce it.";
        return null;
    }
}

/// <summary>xcodebuild's diagnostics (compiler errors with file and line, project-level errors, ** BUILD FAILED **).</summary>
public sealed class XcodeBuildOutputParser : IBuildOutputParser
{
    private readonly MobileOutputParser _inner = new();
    public BuildMessage? Parse(string line) => _inner.Parse(line);
    public string Describe(BuildMessage m) => _inner.Describe(m);
    public IBuildOutputParser Clone() => new XcodeBuildOutputParser();
}
