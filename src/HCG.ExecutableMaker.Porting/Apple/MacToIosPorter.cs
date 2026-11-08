using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.Porting.Apple;

/// <summary>
/// macOS → iOS for SwiftUI sources:
/// <list type="bullet">
/// <item>Swift package with a SwiftUI executable target → new iOS Xcode project that compiles that target's sources and
/// links the package's library products (the package itself gains an iOS platform entry).</item>
/// <item>macOS Xcode project with one SwiftUI app target → the copy is retargeted to iPhone/iPad.</item>
/// </list>
/// Source edits are platform guards or UIKit equivalents next to the macOS code, so the copy still builds for macOS.
/// </summary>
public sealed class MacToIosPorter : IPorter
{
    public const int IosMajor = 17;
    public PortingPath Path => PortingPath.MacOSToIOS;

    public IReadOnlyList<string> Scope { get; } =
    [
        "Supported: SwiftUI apps written in Swift — a Swift package executable target, or a macOS Xcode project with a single app target (no extensions, helpers or test targets).",
        "Automatic adaptations: AppKit/Cocoa imports, plain-string NSPasteboard copy → UIPasteboard, macOS-only scenes (Settings, Window, MenuBarExtra) and window modifiers kept for macOS, SwiftPM activation app delegates.",
        "Not converted (reported as blockers): other AppKit APIs (panels, alerts, NSView/NSWindow, NSWorkspace…), Process, macOS-only frameworks, Objective-C/AppKit storyboards and XIBs.",
        "Not supported in this version: Objective-C or Mac Catalyst projects, multi-target Xcode projects, xcconfig-based projects, Electron/.NET/Qt apps.",
    ];

    public IReadOnlyList<PortSource> FindSources(SourceIndex index)
    {
        var sources = new List<PortSource>();
        foreach (var manifest in SwiftPackageScanner.FindManifests(index).Where(m => !XcodeProjectAnalyzer.IsIosAppPackage(index, m)))
        {
            var pkg = SwiftPackageScanner.Scan(index, manifest);
            foreach (var facts in pkg.SourceFacts.Where(f => f.SuggestedKind == SwiftAppKind.GuiApp))
                sources.Add(new PortSource
                {
                    Path = Path, Key = $"swiftpm:{pkg.Directory}:{facts.Target}", Kind = "Swift package · SwiftUI app target",
                    DisplayName = $"{facts.Target} — {pkg.DisplayDirectory}", Directory = pkg.Directory, Target = facts.Target,
                });
        }
        foreach (var project in XcodeProjectAnalyzer.FindProjects(index))
        {
            var dir = project.Contains('/') ? project[..project.LastIndexOf('/')] : "";
            var macOnly = XcodeProjectAnalyzer.IsMacOnlyProject(index, project);
            sources.Add(new PortSource
            {
                Path = Path, Key = (macOnly ? "xcode:" : "existing-ios:") + project,
                Kind = macOnly ? "macOS Xcode project" : "Existing iOS / multiplatform Xcode project",
                DisplayName = SourceIndex.FileName(project), Directory = dir, Target = project,
            });
        }
        return sources;
    }

    public PortingAssessment Assess(SourceIndex index, PortSource source, ToolchainReport? toolchains)
    {
        if (source.Key.StartsWith("existing-ios:", StringComparison.Ordinal))
            return new PortingAssessment
            {
                Path = Path, Source = source, Capability = PortingCapability.BuildExistingTarget, Scope = Scope, AssessedAt = DateTimeOffset.UtcNow,
                Summary = $"{source.DisplayName} already builds for iOS. No conversion is needed: build it in Mobile Build Studio (Simulator build, archive, IPA).",
                Toolchains = Toolchains(toolchains), Signing = Signing,
            };
        return source.Key.StartsWith("swiftpm:", StringComparison.Ordinal)
            ? AssessPackage(index, source, toolchains)
            : AssessXcode(index, source, toolchains);
    }

    // ───────────── Swift package app target ─────────────

    private sealed record PackageModel(SwiftManifestText Manifest, SwiftManifestText.Target App, string AppDir,
        IReadOnlyList<string> AppFiles, IReadOnlyList<(SwiftManifestText.Target Target, string Dir)> Libraries,
        IReadOnlyList<string> LinkProducts, List<PortingFinding> Findings);

    private static PackageModel ReadPackage(SourceIndex index, PortSource source)
    {
        var manifestPath = PortOutput.Join(source.Directory, "Package.swift");
        var manifest = new SwiftManifestText(File.ReadAllText(index.FullPath(manifestPath)));
        var findings = new List<PortingFinding>();
        var app = manifest.FindTarget(source.Target) ?? new SwiftManifestText.Target(source.Target, "executableTarget", null, [], false);
        var appDir = PortOutput.Join(source.Directory, app.Path ?? "Sources/" + app.Name);
        var appFiles = SwiftFiles(index, appDir);

        // Local library targets reachable from the app (their sources compile for iOS too).
        var libraries = new List<(SwiftManifestText.Target, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<SwiftManifestText.Dependency>(app.Dependencies);
        var link = new List<string>();
        while (queue.Count > 0)
        {
            var dep = queue.Dequeue();
            var local = dep.Package is null ? manifest.FindTarget(dep.Name) : null;
            var direct = app.Dependencies.Contains(dep);
            if (local is null)
            {
                findings.Add(new PortingFinding
                {
                    Category = FindingCategory.Dependency, File = manifestPath,
                    Disposition = direct ? FindingDisposition.Blocker : FindingDisposition.NeedsReview,
                    Title = $"Package product '{dep.Name}'{(dep.Package is null ? "" : $" from '{dep.Package}'")}",
                    Detail = direct
                        ? "The app target uses a product of another package directly. The generated Xcode project links products of this package only."
                        : "A library depends on another package; SwiftPM resolves it when Xcode builds. Whether it supports iOS is only known when it builds.",
                    Guidance = direct ? "Add the package to the generated Xcode project (File › Add Package Dependencies) or depend on it through a library target." : "Check the dependency's supported platforms.",
                });
                continue;
            }
            if (!seen.Add(local.Name)) continue;
            libraries.Add((local, PortOutput.Join(source.Directory, local.Path ?? "Sources/" + local.Name)));
            if (direct)
            {
                if (manifest.LibraryFor(local.Name) is { } product) { if (!link.Contains(product.Name)) link.Add(product.Name); }
                else findings.Add(new PortingFinding
                {
                    Category = FindingCategory.Dependency, Disposition = FindingDisposition.Blocker, File = manifestPath,
                    Title = $"Target '{local.Name}' is not exported as a library product",
                    Detail = "An Xcode app target can only link library products of a package.",
                    Guidance = $"Add .library(name: \"{local.Name}\", targets: [\"{local.Name}\"]) to the package's products.",
                });
            }
            foreach (var d in local.Dependencies) queue.Enqueue(d);
        }
        return new PackageModel(manifest, app, appDir, appFiles, libraries, link, findings);
    }

    private PortingAssessment AssessPackage(SourceIndex index, PortSource source, ToolchainReport? toolchains)
    {
        var model = ReadPackage(index, source);
        var findings = new List<PortingFinding>(model.Findings);
        var manifestPath = PortOutput.Join(source.Directory, "Package.swift");
        var units = new List<ReusableUnit>();

        var appPlans = model.AppFiles.Select(f => MacToIosRules.Analyze(new SwiftSource(f, File.ReadAllText(index.FullPath(f))), isAppTarget: true)).ToList();
        units.Add(Unit($"{model.App.Name} (app target)", "SwiftUI app sources → iOS app target", appPlans));
        findings.AddRange(appPlans.SelectMany(p => p.Findings));
        foreach (var (lib, dir) in model.Libraries)
        {
            var plans = SwiftFiles(index, dir).Select(f => MacToIosRules.Analyze(new SwiftSource(f, File.ReadAllText(index.FullPath(f))), isAppTarget: false)).ToList();
            units.Add(Unit($"{lib.Name} (library)", "Linked unchanged through the local package", plans));
            findings.AddRange(plans.SelectMany(p => p.Findings));
        }
        if (model.AppFiles.Count == 0)
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.BuildSystem, Disposition = FindingDisposition.Blocker, File = manifestPath,
                Title = "No Swift sources found for the app target", Detail = $"Expected sources in {model.AppDir}/.",
            });
        if (model.App.DeclaresResources)
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.BuildSystem, Disposition = FindingDisposition.NeedsReview, File = manifestPath,
                Title = "The app target declares SwiftPM resources",
                Detail = "Resources of the executable target are not copied into the generated Xcode app target.",
                Guidance = "Add the resources to the Xcode project (asset catalog or Copy Bundle Resources) and load them from Bundle.main.",
            });

        if (model.Manifest.WithIOSPlatform(IosMajor, out var present) is null)
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.BuildSystem, Disposition = FindingDisposition.Blocker, File = manifestPath,
                Title = "Cannot add an iOS platform to Package.swift automatically",
                Detail = "The manifest's platforms/name declaration is not in a form HCG edits.",
                Guidance = $"Add .iOS(.v{IosMajor}) to the package's platforms.",
            });
        else if (!present)
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.BuildSystem, Disposition = FindingDisposition.AutoAdapted, File = manifestPath,
                Title = $"Package.swift: add .iOS(.v{IosMajor}) to platforms",
                Detail = "Lets the package's libraries build for iOS. Package.swift stays a macOS package as before (the macOS-only app product is unchanged).",
            });
        if (model.Manifest.HasRemoteDependencies)
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.Dependency, Disposition = FindingDisposition.NeedsReview, File = manifestPath,
                Title = "Remote package dependencies", Detail = "Their iOS support is only known when Xcode resolves and builds them.",
            });
        findings.Add(new PortingFinding
        {
            Category = FindingCategory.BuildSystem, Disposition = FindingDisposition.AutoAdapted, File = PortOutput.Join(source.Directory, $"iOS/{source.Target}.xcodeproj"),
            Title = "New iOS Xcode project",
            Detail = $"An iPhone/iPad app target (iOS {IosMajor}.0+) compiling {model.App.Name}'s sources and linking {(model.LinkProducts.Count == 0 ? "no package products" : string.Join(", ", model.LinkProducts))} from the local package.",
        });
        var facts = SwiftPackageScanner.FactsFor(index, source.Directory, source.Target, model.App.Path);
        AddPermissions(findings, facts.PrivacyUsages);

        return Finish(source, toolchains, findings, units, facts.PrivacyUsages.Select(u => u.InfoPlistKey).Distinct().ToList(),
            $"{model.App.Name}: SwiftUI app in Swift package {(source.Directory.Length == 0 ? "(root)" : source.Directory + "/")}. ") with
        {
            SuggestedAppName = model.App.Name.EndsWith("App", StringComparison.Ordinal) && model.App.Name.Length > 3 ? model.App.Name[..^3] : model.App.Name,
            SuggestedAppId = "com.example." + TargetName(model.App.Name).ToLowerInvariant(),
        };
    }

    // ───────────── macOS Xcode project ─────────────

    private PortingAssessment AssessXcode(SourceIndex index, PortSource source, ToolchainReport? toolchains)
    {
        var findings = new List<PortingFinding>();
        var retarget = XcodeRetarget.Inspect(File.ReadAllText(index.FullPath(source.Target + "/project.pbxproj")), source.Target + "/project.pbxproj");
        findings.AddRange(retarget);
        var files = SwiftFiles(index, source.Directory);
        var plans = files.Select(f => MacToIosRules.Analyze(new SwiftSource(f, File.ReadAllText(index.FullPath(f))), isAppTarget: false)).ToList();
        findings.AddRange(plans.SelectMany(p => p.Findings));
        foreach (var ui in index.WithExtension(".xib", ".storyboard").Where(f => f.StartsWith(source.Directory, StringComparison.Ordinal)))
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.UiAdaptation, Disposition = FindingDisposition.Blocker, File = ui,
                Title = "AppKit interface file", Detail = "macOS XIBs/storyboards cannot be converted to UIKit automatically.",
                Guidance = "Rebuild this interface in SwiftUI (or a UIKit storyboard).",
            });
        foreach (var objc in index.WithExtension(".m", ".mm").Where(f => f.StartsWith(source.Directory, StringComparison.Ordinal)).Take(1))
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.PlatformApi, Disposition = FindingDisposition.Blocker, File = objc,
                Title = "Objective-C sources", Detail = "Objective-C/AppKit code is outside this version's supported scope (SwiftUI in Swift).",
            });
        var facts = SwiftPackageScanner.FactsFor(index, "", SourceIndex.FileName(source.Target), source.Directory);
        AddPermissions(findings, facts.PrivacyUsages);
        var units = new List<ReusableUnit> { Unit(SourceIndex.FileName(source.Target), "App target sources (retargeted to iOS)", plans) };
        var (name, id) = XcodeRetarget.Identity(File.ReadAllText(index.FullPath(source.Target + "/project.pbxproj")));
        return Finish(source, toolchains, findings, units, facts.PrivacyUsages.Select(u => u.InfoPlistKey).Distinct().ToList(),
            $"{source.DisplayName}: macOS Xcode project. ") with { SuggestedAppName = name, SuggestedAppId = id };
    }

    // ───────────── Shared ─────────────

    private PortingAssessment Finish(PortSource source, ToolchainReport? toolchains, List<PortingFinding> findings, List<ReusableUnit> units,
        IReadOnlyList<string> requiredTexts, string intro)
    {
        var blockers = findings.Count(f => f.Disposition == FindingDisposition.Blocker);
        var adapted = findings.Count(f => f.Disposition == FindingDisposition.AutoAdapted);
        var review = findings.Count(f => f.Disposition == FindingDisposition.NeedsReview);
        var files = units.Sum(u => u.Files);
        var unchanged = units.Sum(u => u.FilesUnchanged);
        var capability = blockers > 0 ? PortingCapability.BlockedNeedsPorting : PortingCapability.GenerateTarget;
        var summary = intro + (capability == PortingCapability.GenerateTarget
            ? $"HCG can generate an iOS target: {unchanged} of {files} Swift files carry over unchanged, {adapted} supported adaptation(s), {review} item(s) to review."
            : $"{blockers} blocker(s) need manual porting before an iOS target can work ({unchanged} of {files} Swift files carry over unchanged; {adapted} supported adaptation(s) available).");
        return new PortingAssessment
        {
            Path = Path, Source = source, Capability = capability, Summary = summary, Reusable = units,
            Findings = findings.OrderBy(f => f.Disposition switch { FindingDisposition.Blocker => 0, FindingDisposition.NeedsReview => 1, FindingDisposition.AutoAdapted => 2, _ => 3 })
                .ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList(),
            Toolchains = Toolchains(toolchains), Signing = Signing, Scope = Scope, RequiredTexts = requiredTexts, AssessedAt = DateTimeOffset.UtcNow,
        };
    }

    private static void AddPermissions(List<PortingFinding> findings, IReadOnlyList<SwiftPrivacyUsage> usages)
    {
        foreach (var u in usages.DistinctBy(u => u.InfoPlistKey))
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.Permission, Disposition = FindingDisposition.NeedsReview,
                Title = $"{u.InfoPlistKey} ({u.Purpose})", Detail = $"iOS shows this text in the permission prompt and terminates the app without it. Evidence: {u.Evidence}.",
                Guidance = "Enter the text before generating; it is written to the iOS target's Info.plist.",
            });
    }

    private static readonly string[] Signing =
    [
        "Simulator builds need no signing.",
        "Running on an iPhone/iPad requires an Apple Developer team and a provisioning profile (set DEVELOPMENT_TEAM in Xcode or Mobile Build Studio's signing options). HCG never creates certificates.",
        "App Store / TestFlight distribution and submission stay manual (archive + export in Mobile Build Studio, upload yourself).",
    ];

    private static IReadOnlyList<ToolchainRequirement> Toolchains(ToolchainReport? report)
    {
        var mac = report?.Host.Platform == TargetPlatform.MacOS;
        var xcode = report?.Find(ToolchainIds.Xcode);
        var sdk = report?.Find(ToolchainIds.IosSdk);
        return
        [
            new ToolchainRequirement("macos-host", "macOS build host (generate anywhere; compile on a Mac)", null, mac, report is null ? null : report.Host.DisplayName),
            new ToolchainRequirement(ToolchainIds.Xcode, "Xcode (full, not only Command Line Tools)", "15.0", xcode?.IsAvailable == true, xcode?.Version is { } v ? "Xcode " + v : xcode?.InstallHint ?? "Install Xcode from the Mac App Store"),
            new ToolchainRequirement(ToolchainIds.IosSdk, "iOS SDK + Simulator runtime", $"{IosMajor}.0", sdk?.IsAvailable == true, sdk?.Version is { } s ? "iOS SDK " + s : "Xcode › Settings › Components › iOS"),
        ];
    }

    private static ReusableUnit Unit(string name, string kind, List<SwiftFilePlan> plans) =>
        new(name, kind, plans.Count, plans.Sum(p => p.Source.Lines.Count), plans.Count(p => p.HasBlockers), plans.Count(p => !p.HasBlockers && p.IsAdapted));

    internal static IReadOnlyList<string> SwiftFiles(SourceIndex index, string dir)
    {
        var prefix = dir.Length == 0 ? "" : dir.TrimEnd('/') + "/";
        return index.WithExtension(".swift")
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
            .Where(f => !f[prefix.Length..].Split('/').SkipLast(1).Any(SwiftPackageScanner.IgnoredDirectories.Contains))
            .Where(f => !f.EndsWith("/Package.swift", StringComparison.Ordinal) && f != "Package.swift")
            .Order(StringComparer.Ordinal).ToList();
    }

    // ───────────── Generation ─────────────

    public (IReadOnlyList<string> Steps, string BuildRoot) Generate(SourceIndex index, PortingAssessment assessment, PortOptions options, PortOutput output)
    {
        var source = assessment.Source;
        if (assessment.Capability is PortingCapability.BuildExistingTarget or PortingCapability.NotSupported)
            throw new InvalidOperationException("Nothing to generate for this source: " + assessment.Summary);
        if (assessment.Capability == PortingCapability.BlockedNeedsPorting && !options.Draft)
            throw new InvalidOperationException("This source has blockers. Resolve them, or generate an explicit draft (blocker sites are marked with compile errors).");
        foreach (var key in assessment.RequiredTexts)
            if (string.IsNullOrWhiteSpace(options.UsageDescriptions.GetValueOrDefault(key)))
                throw new InvalidOperationException($"{key} needs a description before generating (iOS terminates apps that request this permission without one).");

        var steps = new List<string> { "Copied the workspace into the output folder (the original project is not modified)." };
        output.CopyTree(index, "", "");
        var adaptedFiles = 0;
        void AdaptFiles(IEnumerable<string> files, bool app)
        {
            foreach (var f in files)
            {
                var plan = MacToIosRules.Analyze(new SwiftSource(f, File.ReadAllText(index.FullPath(f))), app);
                if (plan.Edits.Count == 0 && !(options.Draft && plan.HasBlockers)) continue;
                output.Modify(f, f, plan.Apply(options.Draft), string.Join("; ", plan.Findings.Where(x => x.Disposition != FindingDisposition.Compatible).Select(x => x.Title).Distinct()));
                adaptedFiles++;
            }
        }

        string buildRoot;
        if (source.Key.StartsWith("swiftpm:", StringComparison.Ordinal))
        {
            var model = ReadPackage(index, source);
            AdaptFiles(model.AppFiles, true);
            foreach (var (_, dir) in model.Libraries) AdaptFiles(SwiftFiles(index, dir), false);
            steps.Add($"Adapted {adaptedFiles} Swift file(s) with platform guards / UIKit equivalents (macOS code unchanged).");
            var manifestPath = PortOutput.Join(source.Directory, "Package.swift");
            var withIos = model.Manifest.WithIOSPlatform(IosMajor, out var present) ?? throw new InvalidOperationException("Package.swift could not be adapted.");
            if (!present)
            {
                output.Modify(manifestPath, manifestPath, withIos, $"Add .iOS(.v{IosMajor}) to platforms");
                steps.Add($"Added .iOS(.v{IosMajor}) to Package.swift's platforms.");
            }
            var targetName = TargetName(options.AppName);
            buildRoot = PortOutput.Join(source.Directory, "iOS");
            var spec = new XcodeProjectWriter.Spec
            {
                TargetName = targetName, DisplayName = options.AppName, BundleId = options.AppId, Version = options.Version,
                BuildNumber = options.BuildNumber, DeploymentTarget = $"{IosMajor}.0",
                SourceFolder = PortOutput.Relative(buildRoot, model.AppDir),
                SwiftFiles = model.AppFiles.Select(f => f[(model.AppDir.Length + 1)..]).ToList(),
                LocalPackagePath = model.LinkProducts.Count > 0 ? PortOutput.Relative(buildRoot, source.Directory) : null,
                PackageProducts = model.LinkProducts,
                InfoPlistKeys = options.UsageDescriptions.Where(k => assessment.RequiredTexts.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value.Trim()),
            };
            var proj = PortOutput.Join(buildRoot, targetName + ".xcodeproj");
            output.Add(proj + "/project.pbxproj", XcodeProjectWriter.Pbxproj(spec), "Generated iOS app project");
            output.Add(proj + $"/xcshareddata/xcschemes/{targetName}.xcscheme", XcodeProjectWriter.Scheme(spec), "Shared scheme for command-line and HCG builds");
            steps.Add($"Generated {proj}: iPhone/iPad app target '{targetName}' ({options.AppId}, iOS {IosMajor}.0+) compiling {model.AppFiles.Count} Swift file(s) from {model.AppDir}/ and linking {(model.LinkProducts.Count == 0 ? "no package products" : string.Join(", ", model.LinkProducts))}.");
        }
        else
        {
            var pbxproj = source.Target + "/project.pbxproj";
            var text = File.ReadAllText(index.FullPath(pbxproj));
            var retargeted = XcodeRetarget.Apply(text, options, assessment.RequiredTexts, IosMajor);
            output.Modify(pbxproj, pbxproj, retargeted, "Retargeted the app target to iPhone/iPad");
            steps.Add($"Retargeted {source.Target} to iOS {IosMajor}.0+ (iPhone and iPad), bundle id {options.AppId}; macOS-only settings removed.");
            AdaptFiles(SwiftFiles(index, source.Directory), false);
            steps.Add($"Adapted {adaptedFiles} Swift file(s) with platform guards / UIKit equivalents.");
            buildRoot = source.Directory;
        }
        var review = assessment.Findings.Count(f => f.Disposition == FindingDisposition.NeedsReview);
        if (review > 0) steps.Add($"{review} item(s) need your review (see the assessment): behaviour on iOS differs or a decision is needed.");
        if (options.Draft)
            steps.Add($"DRAFT: {assessment.Blockers.Count()} blocker site(s) carry #error markers for iOS. The project will not compile until they are ported by hand.");
        steps.Add("Next: compile for the iOS Simulator (Compile step), then launch it in a simulator. Device testing and App Store submission stay manual.");
        return (steps, buildRoot);
    }

    internal static string TargetName(string appName)
    {
        var cleaned = new string(appName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (cleaned.Length == 0) cleaned = "App";
        return char.IsAsciiDigit(cleaned[0]) ? "App" + cleaned : cleaned;
    }
}
