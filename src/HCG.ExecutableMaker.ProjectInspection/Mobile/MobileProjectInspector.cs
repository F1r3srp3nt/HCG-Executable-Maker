using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Detects Android (Gradle), iOS (Xcode), Flutter and .NET MAUI app projects by their real project structure (not file
/// extensions alone) and reports what can be built here. Purely static.
/// </summary>
public sealed class MobileProjectInspector(TimeProvider time) : IProjectInspector
{
    public string Id => "mobile";
    public string DisplayName => "Mobile (Android / iOS)";

    /// <summary>
    /// 105–110 when a mobile app project is the top-level project. If a desktop .NET project sits higher in the tree
    /// (e.g. a repository that merely contains mobile test fixtures), the score drops below the .NET inspector's 100.
    /// </summary>
    public int Match(SourceIndex index)
    {
        var mauiProjects = MauiAnalyzer.Find(index);
        var markers = new List<(string Path, int Score)>();
        markers.AddRange(FlutterAnalyzer.FindPubspecs(index).Select(p => (p, 110)));
        markers.AddRange(mauiProjects.Select(m => (m.ProjectPath, 110)));
        markers.AddRange(AndroidGradleAnalyzer.FindRoots(index).Select(r => (AndroidGradleAnalyzer.Join(r, "build.gradle"), 105)));
        markers.AddRange(XcodeProjectAnalyzer.FindMobileProjects(index).Select(p => (p, 105)));
        markers.AddRange(XcodeProjectAnalyzer.FindAppPackages(index).Select(p => (p, 105)));
        if (markers.Count == 0) return 0;
        var mobileDepth = markers.Min(m => SourceIndex.Depth(m.Path));
        var mauiPaths = mauiProjects.Select(m => m.ProjectPath).ToHashSet(StringComparer.Ordinal);
        var desktopDepth = index.WithExtension(".csproj", ".fsproj", ".vbproj").Where(p => !mauiPaths.Contains(p)).Select(SourceIndex.Depth).DefaultIfEmpty(int.MaxValue).Min();
        return desktopDepth < mobileDepth ? 90 : markers.Max(m => m.Score);
    }

    public Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(index, toolchains), cancellationToken);

    public ProjectAnalysis Inspect(SourceIndex index, ToolchainReport? toolchains)
    {
        var diagnostics = new List<AnalysisDiagnostic>();
        var mobile = Analyze(index, diagnostics);
        var options = MobileCapabilities.Evaluate(mobile, toolchains);
        var available = options.Where(o => o.IsAvailable).ToList();
        var hostBlocked = options.Where(o => !o.IsAvailable && o.Reason?.Contains("require a Mac", StringComparison.Ordinal) == true).ToList();

        var hasBuildable = mobile.Framework switch
        {
            MobileFramework.NativeAndroid => mobile.Android?.AppModules.Count > 0 || mobile.Ios?.AppTargets.Any() == true,
            MobileFramework.NativeIos => mobile.Ios?.AppTargets.Any() == true,
            _ => mobile.Platforms.Count > 0,
        };
        var (support, summary) =
            !hasBuildable ? (SupportLevel.RequiresConfiguration, "A mobile project structure was found, but no buildable application target or module.")
            : available.Count > 0 ? (SupportLevel.Supported, $"Buildable here: {string.Join(", ", available.Select(o => o.DisplayName))}.")
            : hostBlocked.Count == options.Count ? (SupportLevel.RequiresDifferentHost, "iOS builds require a Mac with Xcode.")
            : (SupportLevel.MissingToolchain, "Mobile project recognized, but required toolchains are missing on this computer.");

        var name = mobile.Capacitor?.AppName ?? mobile.Flutter?.Name ?? mobile.Maui?.ApplicationTitle ?? Path.GetFileNameWithoutExtension(mobile.Maui?.ProjectPath)
                   ?? mobile.Android?.AppModules.FirstOrDefault()?.ApplicationId?.Split('.').Last()
                   ?? mobile.Ios?.AppTargets.FirstOrDefault()?.Name
                   ?? new DirectoryInfo(index.Root).Name;
        var language = mobile.Framework switch
        {
            MobileFramework.Flutter => "Dart",
            MobileFramework.DotNetMaui => "C#",
            MobileFramework.Capacitor => "TypeScript / JavaScript",
            MobileFramework.NativeIos => "Swift / Objective-C",
            _ => mobile.Android?.UsesKotlin == true ? "Kotlin" : "Java",
        };
        var framework = mobile.Framework switch
        {
            MobileFramework.NativeAndroid => "Android Gradle" + (mobile.Android?.AgpVersion is { } agp ? $" · AGP {agp}" : ""),
            MobileFramework.NativeIos => "Xcode" + (mobile.Ios?.UsesSwiftPackages == true ? " · Swift packages" : "") + (mobile.Ios?.UsesCocoaPods == true ? " · CocoaPods" : ""),
            MobileFramework.Flutter => "Flutter" + (mobile.Flutter?.SdkConstraint is { } c ? $" (Dart SDK {c})" : ""),
            MobileFramework.Capacitor => "Capacitor" + (mobile.Capacitor?.CapacitorVersion is { } cv ? $" {cv}" : "") + " · web app",
            _ => ".NET MAUI · " + string.Join(", ", mobile.Maui?.TargetFrameworks ?? []),
        };
        var recommended = new List<PackagingFormat>();
        if (mobile.Platforms.Contains(TargetPlatform.Android)) recommended.AddRange([PackagingFormat.AndroidApk, PackagingFormat.AndroidAab]);
        if (mobile.Platforms.Contains(TargetPlatform.IOS)) recommended.AddRange([PackagingFormat.IosArchive, PackagingFormat.IosIpa]);

        var requirements = new List<ToolchainRequirement>();
        void Req(string id, string display) => requirements.Add(new ToolchainRequirement(id, display, null, toolchains?.IsAvailable(id) == true, toolchains?.Find(id)?.Version));
        if (mobile.Platforms.Contains(TargetPlatform.Android)) { Req(ToolchainIds.Jdk, "JDK 17+"); Req(ToolchainIds.AndroidSdk, "Android SDK"); }
        if (mobile.Platforms.Contains(TargetPlatform.IOS)) { Req(ToolchainIds.Xcode, "Xcode (macOS)"); Req(ToolchainIds.IosSdk, "iOS SDK"); }
        if (mobile.Framework == MobileFramework.Flutter) Req(ToolchainIds.Flutter, "Flutter SDK");
        if (mobile.Framework == MobileFramework.Capacitor) Req(ToolchainIds.Node, "Node.js 22+ and npm");
        if (mobile.Framework == MobileFramework.DotNetMaui) { Req(ToolchainIds.DotNetSdk, ".NET SDK"); Req(ToolchainIds.MauiWorkloads, ".NET MAUI workloads"); }

        if (mobile.Android is { } and && and.AgpVersion is not null)
        {
            var jdk = toolchains?.Find(ToolchainIds.Jdk)?.Version is { } jv && int.TryParse(jv.Split('.')[0], out var j) ? j : (int?)null;
            var compat = AgpCompatibility.Check(and.AgpVersion, and.WrapperGradleVersion, jdk);
            diagnostics.Add(new(compat.GradleOk == false || compat.JdkOk == false ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info, compat.Message, and.Root));
        }

        return new ProjectAnalysis
        {
            InspectorId = Id,
            ProjectName = name,
            Language = language,
            Framework = framework,
            Runtime = string.Join(" + ", mobile.Platforms.Select(p => p.DisplayName())),
            ApplicationType = mobile.Framework switch
            {
                MobileFramework.Flutter => "Flutter application",
                MobileFramework.DotNetMaui => ".NET MAUI application",
                MobileFramework.Capacitor => "Capacitor application (web app in a native shell)",
                MobileFramework.NativeIos => "iOS application",
                _ => mobile.Platforms.Contains(TargetPlatform.IOS) ? "Android + iOS applications" : "Android application",
            },
            Support = support,
            SupportSummary = summary + " Mobile projects are built in Mobile Build Studio.",
            RequiredToolchains = requirements,
            MissingDependencies = options.Where(o => !o.IsAvailable && o.Reason is not null).Select(o => o.Reason!).Distinct().Take(6).ToList(),
            RecommendedFormats = recommended,
            Diagnostics = diagnostics,
            Mobile = mobile,
            AnalyzedAt = time.GetUtcNow(),
        };
    }

    public static MobileAnalysis Analyze(SourceIndex index, List<AnalysisDiagnostic> diagnostics)
    {
        // Capacitor first: its android/ and ios/ folders are native projects that only build after npm installs @capacitor/*.
        if (CapacitorAnalyzer.FindRoots(index).FirstOrDefault() is { } capRoot)
        {
            var cap = CapacitorAnalyzer.Analyze(index, capRoot);
            var platforms = new List<TargetPlatform>();
            AndroidProjectInfo? capAndroid = null;
            IosProjectInfo? capIos = null;
            if (cap.HasAndroid) { platforms.Add(TargetPlatform.Android); capAndroid = AndroidGradleAnalyzer.Analyze(index, CapacitorAnalyzer.Join(capRoot, "android"), out _); }
            if (cap.HasIos && XcodeProjectAnalyzer.FindMobileProjects(index).FirstOrDefault(p => p.StartsWith(CapacitorAnalyzer.Join(capRoot, "ios/"), StringComparison.Ordinal)) is { } xp)
            {
                platforms.Add(TargetPlatform.IOS);
                capIos = XcodeProjectAnalyzer.Analyze(index, xp, out _);
            }
            if (platforms.Count == 0) diagnostics.Add(new(DiagnosticSeverity.Warning, "Capacitor app without android/ or ios/ projects (add one with npx cap add android|ios).", cap.ConfigFile));
            if (cap.BuildScript is null) diagnostics.Add(new(DiagnosticSeverity.Warning, "package.json has no build script, so the web app cannot be built before syncing.", CapacitorAnalyzer.Join(capRoot, "package.json")));
            return new MobileAnalysis
            {
                Framework = MobileFramework.Capacitor, Platforms = platforms, Capacitor = cap, Android = capAndroid, Ios = capIos,
                ApplicationIds = cap.AppId is { } id ? [id] : [],
            };
        }

        var pubspec = FlutterAnalyzer.FindPubspecs(index).FirstOrDefault();
        if (pubspec is not null)
        {
            var f = FlutterAnalyzer.Analyze(index, pubspec);
            var platforms = new List<TargetPlatform>();
            if (f.HasAndroid) platforms.Add(TargetPlatform.Android);
            if (f.HasIos) platforms.Add(TargetPlatform.IOS);
            if (platforms.Count == 0) diagnostics.Add(new(DiagnosticSeverity.Warning, "Flutter project has neither android/ nor ios/ platform folders (run `flutter create .` to add them).", pubspec));
            AndroidProjectInfo? and = null;
            if (f.HasAndroid) and = AndroidGradleAnalyzer.Analyze(index, AndroidGradleAnalyzer.Join(f.Root, "android"), out _);
            return new MobileAnalysis
            {
                Framework = MobileFramework.Flutter, Platforms = platforms, Flutter = f, Android = and,
                ApplicationIds = and?.AppModules.Select(m => m.ApplicationId).OfType<string>().ToList() ?? [],
            };
        }

        var maui = MauiAnalyzer.Find(index).FirstOrDefault();
        if (maui is not null)
        {
            var platforms = new List<TargetPlatform>();
            if (maui.AndroidTargetFramework is not null) platforms.Add(TargetPlatform.Android);
            if (maui.IosTargetFramework is not null) platforms.Add(TargetPlatform.IOS);
            return new MobileAnalysis
            {
                Framework = MobileFramework.DotNetMaui, Platforms = platforms, Maui = maui,
                ApplicationIds = maui.ApplicationId is { } id ? [id] : [],
            };
        }

        AndroidProjectInfo? android = null;
        if (AndroidGradleAnalyzer.FindRoots(index).FirstOrDefault() is { } root)
        {
            android = AndroidGradleAnalyzer.Analyze(index, root, out var d);
            diagnostics.AddRange(d);
        }
        IosProjectInfo? ios = null;
        var xcodeProjects = XcodeProjectAnalyzer.FindMobileProjects(index);
        if (xcodeProjects.Count > 0)
        {
            // Prefer the project with an app target.
            foreach (var p in xcodeProjects)
            {
                var info = XcodeProjectAnalyzer.Analyze(index, p, out var d);
                if (ios is null || (!ios.AppTargets.Any() && info.AppTargets.Any())) { ios = info; diagnostics.RemoveAll(x => x.Path?.StartsWith(p, StringComparison.Ordinal) == true); diagnostics.AddRange(d); }
            }
        }
        else if (XcodeProjectAnalyzer.FindAppPackages(index).FirstOrDefault() is { } pkg)
        {
            diagnostics.Add(new(DiagnosticSeverity.Warning, "Swift Playgrounds-style app package (.iOSApplication) found without an Xcode project. Open it in Xcode and save an .xcodeproj to build it with HCG.", pkg));
            ios = new IosProjectInfo { ProjectPath = null };
        }

        var list = new List<TargetPlatform>();
        if (android is not null) list.Add(TargetPlatform.Android);
        if (ios is not null) list.Add(TargetPlatform.IOS);
        return new MobileAnalysis
        {
            Framework = android is not null ? MobileFramework.NativeAndroid : MobileFramework.NativeIos,
            Platforms = list,
            Android = android,
            Ios = ios,
            ApplicationIds = [.. (android?.AppModules.Select(m => m.ApplicationId) ?? []).Concat(ios?.AppTargets.Select(t => t.BundleIdentifier) ?? []).OfType<string>().Distinct()],
        };
    }
}
