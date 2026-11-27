using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.ProjectInspection.MacXcode;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// Native macOS Xcode builds without the UI: the same import, inspection, xcodebuild pipeline, verification, packaging,
/// diagnostics, Signing Doctor and Release Passport as New Build.
/// <code>
/// HCGExecutableMaker --xcode-mac &lt;folder|zip&gt; [--inspect-only]
///     [--project &lt;path.xcodeproj&gt;] [--scheme &lt;name&gt;] [--configuration Release|Debug|…] [--arch arm64|x86_64|universal]
///     [--test] [--archive] [--formats app,zip,dmg,pkg,xcarchive] [--sign-ad-hoc] [--out &lt;dir&gt;] [--timeout &lt;minutes&gt;]
///     --trust      (required to build: you trust this project's code, build phases and package dependencies)
/// HCGExecutableMaker --mac-signing-doctor &lt;App.app&gt;   (read-only signing report)
/// </code>
/// Exit codes: 0 succeeded (or inspected), 2 usage, 4 not a buildable macOS Xcode app on this Mac, 6 the build failed.
/// A failed build is never reported as successful; its diagnostics report is printed and written next to build.log.
/// </summary>
internal static class XcodeMacCommand
{
    private sealed class ConsoleObserver : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) => Console.WriteLine($"== {description}");
        public void OnLog(BuildLogLine line) => Console.WriteLine(line.Kind is BuildLogKind.Output ? "   " + line.Text : $"[{line.Kind}] {line.Text}");
    }

    public static int SigningDoctor(string[] args)
    {
        var app = args.SkipWhile(a => a != "--mac-signing-doctor").Skip(1).FirstOrDefault();
        if (app is null || !Directory.Exists(app) || !app.TrimEnd('/').EndsWith(".app", StringComparison.Ordinal)) { Console.Error.WriteLine("Usage: --mac-signing-doctor <App.app>"); return 2; }
        if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("The Signing Doctor reads macOS code signatures with codesign, spctl and stapler on a Mac."); return 4; }
        var report = new MacSigningDoctor(new Infrastructure.Processes.ProcessRunner()).InspectAsync(Path.GetFullPath(app), CancellationToken.None).GetAwaiter().GetResult();
        Console.Write(MacSigningDoctor.ToText(report));
        return 0;
    }

    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var source = Arg("--xcode-mac");
        if (source is null || (!Directory.Exists(source) && !File.Exists(source))) { Console.Error.WriteLine("Usage: --xcode-mac <folder|zip> [--inspect-only] [--scheme S] [--configuration C] [--arch arm64|x86_64|universal] [--test] [--archive] [--formats app,zip,dmg,pkg,xcarchive] [--sign-ad-hoc] [--out DIR] --trust"); return 2; }
        Console.WriteLine(HostInfo.BuildIdentity);
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        var toolchains = services.GetRequiredService<IToolchainDetector>().DetectAsync(CancellationToken.None).GetAwaiter().GetResult();
        var projects = services.GetRequiredService<ProjectService>();
        var imported = (Directory.Exists(source)
            ? projects.ImportDirectoryAsync(Path.GetFullPath(source), null, CancellationToken.None)
            : projects.ImportZipAsync(Path.GetFullPath(source), null, CancellationToken.None)).GetAwaiter().GetResult();
        var project = imported.Project;
        var analysis = project.Analysis!;
        Console.WriteLine($"Project: {project.Name} — {analysis.ApplicationType} ({analysis.InspectorId}) — {analysis.Support}: {analysis.SupportSummary}");
        foreach (var d in analysis.Diagnostics) Console.WriteLine($"  {d.Severity}: {d.Message}{(d.Path is { } p ? $" [{p}]" : "")}");
        if (analysis.InspectorId != MacXcodeProjectInspector.InspectorId || analysis.MacXcode is not { Projects.Count: > 0 } mx)
        {
            Console.WriteLine("Not a native macOS Xcode application project: nothing to build with the Xcode provider.");
            return 4;
        }

        var xp = Arg("--project") is { } wanted ? mx.Projects.FirstOrDefault(p => p.ProjectPath == wanted || p.ProjectPath.EndsWith("/" + wanted, StringComparison.Ordinal)) : mx.Projects[0];
        if (xp is null) { Console.Error.WriteLine($"No macOS Xcode app project {Arg("--project")}; found: {string.Join(", ", mx.Projects.Select(p => p.ProjectPath))}"); return 2; }
        var target = xp.AppTargets[0];
        foreach (var p in mx.Projects)
        {
            Console.WriteLine($"Xcode project {p.ProjectPath}{(p.WorkspacePath is { } w ? $" (workspace {w})" : "")}, format {p.ObjectVersion?.ToString() ?? "?"}");
            Console.WriteLine($"  Schemes: {string.Join(", ", p.Schemes.Select(s => s.Name + (s.Shared ? " (shared)" : "") + (s.TestTargets.Count > 0 ? $" [tests: {string.Join(", ", s.TestTargets)}]" : "")))}");
            Console.WriteLine($"  Configurations: {string.Join(", ", p.Configurations)} · languages: {string.Join(", ", p.Languages)} · UI: {string.Join(" + ", p.UiFrameworks)}");
            Console.WriteLine($"  Swift packages: {(p.PackageReferences.Count == 0 ? "none" : string.Join(", ", p.PackageReferences.Select(r => $"{r.Location} ({r.Kind})")))}");
            Console.WriteLine($"  Asset catalogs: {string.Join(", ", p.AssetCatalogs.DefaultIfEmpty("none"))}; app icon: {p.AppIconSet ?? "none"} ({p.AppIconImages.Count} image(s))");
            foreach (var t in p.AppTargets)
            {
                Console.WriteLine($"  App target {t.Name} → {t.ProductName}.app · {t.BundleIdentifier} · {t.MarketingVersion} ({t.BuildVersion}) · macOS {t.DeploymentTarget}+ · {(t.GeneratedInfoPlist ? "generated Info.plist" : $"Info.plist {t.InfoPlistFile}")} · packages {string.Join(", ", t.PackageProducts.DefaultIfEmpty("none"))}");
                foreach (var c in t.ConfigurationFacts) Console.WriteLine("    " + MacXcodeProjectInspector.Describe(c));
            }
        }
        if (args.Contains("--inspect-only")) return 0;
        if (!args.Contains("--trust"))
        {
            Console.WriteLine("Not built: building runs this project's code, build phases and Swift package manifests on this Mac. Pass --trust to approve the build of this source.");
            return 2;
        }

        var archArg = Arg("--arch");
        var host = HostInfo.Detect();
        var arch = archArg switch
        {
            "universal" => MacXcodeArchitecture.Universal,
            "x86_64" or "intel" => MacXcodeArchitecture.Intel,
            "arm64" => MacXcodeArchitecture.AppleSilicon,
            null => host.Architecture == TargetArchitecture.X64 ? MacXcodeArchitecture.Intel : MacXcodeArchitecture.AppleSilicon,
            _ => (MacXcodeArchitecture?)null,
        };
        if (arch is null) { Console.Error.WriteLine("--arch is arm64, x86_64 or universal."); return 2; }
        var formats = new List<PackagingFormat>();
        foreach (var f in (Arg("--formats") ?? "app,zip,dmg").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            formats.Add(f switch
            {
                "app" => PackagingFormat.MacAppBundle, "zip" => PackagingFormat.PortableZip, "dmg" => PackagingFormat.MacDmg, "pkg" => PackagingFormat.MacPkg,
                "xcarchive" => PackagingFormat.MacXcodeArchive,
                _ => throw new ArgumentException($"Unknown format {f}: use app, zip, dmg, pkg, xcarchive."),
            });
        if (!formats.Contains(PackagingFormat.MacAppBundle)) formats.Insert(0, PackagingFormat.MacAppBundle);
        var options = new MacXcodeBuildOptions
        {
            ProjectPath = xp.ProjectPath, WorkspacePath = xp.WorkspacePath,
            Scheme = Arg("--scheme") ?? MacXcodeProjectAnalyzer.SchemeFor(xp, target)?.Name ?? target.Name,
            Configuration = Arg("--configuration") ?? (xp.Configurations.Contains("Release") ? "Release" : xp.Configurations.Last()),
            Architecture = arch.Value, AppTarget = target.Name, ProductName = target.ProductName, BundleIdentifier = target.BundleIdentifier,
            DisplayName = target.DisplayName, DeploymentTarget = target.DeploymentTarget, SignLocallyAdHoc = args.Contains("--sign-ad-hoc"),
            LicenseFiles = xp.LicenseFiles, RunTests = args.Contains("--test"), CreateArchive = args.Contains("--archive") || formats.Contains(PackagingFormat.MacXcodeArchive),
        };
        if (options.RunTests)
        {
            var (own, packageTests) = MacXcodeProjectAnalyzer.TestsOf(xp, xp.Schemes.FirstOrDefault(s => s.Name == options.Scheme));
            options = options with { ProjectTestTargets = own, PackageTests = packageTests };
            Console.WriteLine("Tests: " + (own.Count + packageTests.Count == 0 ? "the scheme's Test action (xcodebuild test)"
                : string.Join("; ", own.Select(t => $"{t} (xcodebuild test)").Concat(packageTests.Select(t => $"{t.TestTarget} (swift test in {t.PackageDirectory})")))));
        }
        var settings = new BuildSettings
        {
            ProjectRelativePath = xp.ProjectPath,
            Platform = TargetPlatform.MacOS,
            Architecture = arch.Value.ToTarget(),
            Configuration = BuildConfigurationKind.Release,
            OutputDirectory = Path.GetFullPath(Arg("--out") ?? Path.Combine(paths.DefaultOutputDirectory)),
            Timeout = TimeSpan.FromMinutes(int.TryParse(Arg("--timeout"), out var minutes) ? minutes : 60),
            PackagingFormats = formats,
            MacXcode = options,
            Identity = new AppIdentity
            {
                ApplicationName = target.ProductName ?? target.Name,
                Version = target.MarketingVersion is { } v && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+(\.\d+){0,3}$") ? (v.Contains('.') ? v : v + ".0") : "1.0.0",
                ApplicationId = target.BundleIdentifier is { } id && !id.Contains("$(", StringComparison.Ordinal) ? id : "local." + (target.ProductName ?? target.Name),
                Publisher = "", Description = "",
            },
        };
        Console.WriteLine($"Building {options.Scheme} ({options.Configuration}, {options.Architecture.Label()}){(options.RunTests ? " with tests" : "")} → {string.Join(", ", formats.Select(f => f.DisplayName()))}");
        var build = services.GetRequiredService<BuildService>().RunAsync(new BuildRequest { Project = project, Settings = settings, TrustAcknowledged = true, AllowOverwriteOutput = true },
            new ConsoleObserver(), CancellationToken.None).GetAwaiter().GetResult();

        var folder = Path.GetDirectoryName(build.LogPath!)!;
        Console.WriteLine();
        Console.WriteLine($"Result: {build.Status.ToString().ToUpperInvariant()}{(build.FailureReason is { } fr && build.Status != BuildStatus.Succeeded ? " — " + fr : "")}");
        Console.WriteLine($"Build log: {build.LogPath}");
        var md = Path.Combine(folder, XcodeDiagnostics.MarkdownFileName);
        if (File.Exists(md))
        {
            Console.WriteLine($"Diagnostics: {md} (JSON: {Path.Combine(folder, XcodeDiagnostics.JsonFileName)})");
            if (build.Status != BuildStatus.Succeeded) Console.WriteLine(File.ReadAllText(md));
        }
        foreach (var a in build.Artifacts) Console.WriteLine($"Artifact: {a.Path} · SHA-256 {a.Sha256} · {a.SizeBytes:N0} bytes · {a.Verification}");
        if (build.Artifacts.FirstOrDefault() is { } first)
            foreach (var passport in Directory.EnumerateFiles(Path.GetDirectoryName(first.Path)!, "*.release-passport.json")) Console.WriteLine("Release Passport: " + passport);
        return build.Status == BuildStatus.Succeeded ? 0 : 6;
    }
}
