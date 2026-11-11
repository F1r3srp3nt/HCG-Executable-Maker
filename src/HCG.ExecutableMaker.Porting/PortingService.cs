using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Electron;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Porting.Android;
using HCG.ExecutableMaker.Porting.Apple;
using HCG.ExecutableMaker.Porting.Web;
using HCG.ExecutableMaker.ProjectInspection.Node;
using HCG.ExecutableMaker.Security;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Porting;

/// <summary>
/// Source-based porting: assess → generate a separate output copy with a reviewable plan and diff → compile with
/// Mobile Build Studio's engine → launch in a simulator/emulator. Each level is reported on its own; none is implied by
/// another. Publishing and store submission are never performed.
/// </summary>
public sealed class PortingService(IToolchainService toolchains, ProjectService projects, MobileBuildService mobile, DeviceLauncher launcher, IProcessRunner runner, TimeProvider time)
{
    public const string ReportFolder = "HCG-PORT";
    public IReadOnlyList<IPorter> Porters { get; } =
        [new MacToIosPorter(), new LinuxToAndroidPorter(), new WebToMobilePorter(MobileTarget.Android), new WebToMobilePorter(MobileTarget.Ios)];

    public IPorter Porter(PortingPath path) => Porters.First(p => p.Path == path);

    /// <summary>
    /// Read-only compatibility inspection of an imported project: the assessments of every implemented path, plus the
    /// compatibility report (all sources, classified features, per-target options incl. planned ones). Nothing runs.
    /// </summary>
    public async Task<(CompatibilityReport Report, IReadOnlyList<PortingAssessment> Assessments)> InspectAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        var assessments = await AssessAsync(project, cancellationToken).ConfigureAwait(false);
        var report = await Task.Run(() =>
        {
            var index = SourceIndex.Build(project.SourceDirectory, cancellationToken);
            return CompatibilityInspector.Build(index, assessments, SourceRevisionReader.Capture(OriginalFolder(project), index, time), time.GetUtcNow());
        }, cancellationToken).ConfigureAwait(false);
        return (report, assessments);
    }

    /// <summary>The user's original folder (null when the import was a ZIP or the folder is gone).</summary>
    private static string? OriginalFolder(ProjectRecord project) => Directory.Exists(project.SourceLocation) ? project.SourceLocation : null;

    /// <summary>Read-only: finds convertible sources on every path and assesses each. Nothing is executed.</summary>
    public async Task<IReadOnlyList<PortingAssessment>> AssessAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var index = SourceIndex.Build(project.SourceDirectory, cancellationToken);
            return Porters.SelectMany(p => p.FindSources(index).Select(s => p.Assess(index, s, report))).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The output folder for a new port: a fresh, timestamped folder in <paramref name="parent"/>.</summary>
    public string OutputFolderFor(ProjectRecord project, PortingAssessment assessment, string parent) =>
        Path.Combine(Path.GetFullPath(parent),
            $"{PathGuard.SanitizeFileName(SourceName(project), "project")}-{assessment.Path.FolderSuffix()}-{time.GetLocalNow():yyyyMMdd-HHmmss}");

    /// <summary>The user's folder/ZIP name (the analysed project name can be a generic workspace folder).</summary>
    private static string SourceName(ProjectRecord project)
    {
        var name = Path.GetFileNameWithoutExtension(project.SourceLocation.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? project.Name : name;
    }

    public async Task<PortReport> GenerateAsync(ProjectRecord project, PortingAssessment assessment, PortOptions options, string outputParent, CancellationToken cancellationToken)
    {
        var outputRoot = OutputFolderFor(project, assessment, outputParent);
        // Never write into (or around) the user's original project or HCG's workspace copy.
        foreach (var protectedDir in new[] { project.SourceLocation, project.WorkspacePath }.Where(Directory.Exists))
            if (PathGuard.IsWithin(protectedDir, outputRoot) || PathGuard.IsWithin(outputRoot, protectedDir))
                throw new InvalidOperationException($"Choose an output folder outside the original project ({protectedDir}). The port is always a separate copy.");
        if (Directory.Exists(outputRoot)) throw new InvalidOperationException($"{outputRoot} already exists.");

        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (assessment.Path == PortingPath.LinuxToAndroid)
            options = options with
            {
                NdkVersion = options.NdkVersion ?? (report.Find(ToolchainIds.AndroidNdk) is { IsAvailable: true } n ? n.Version : null),
                CmakeVersion = options.CmakeVersion ?? (report.Find(ToolchainIds.AndroidCmake) is { IsAvailable: true } c ? c.Version : null),
            };
        return await Task.Run(() =>
        {
            var index = SourceIndex.Build(project.SourceDirectory, cancellationToken);
            // Re-assess against the current files so the plan matches what is generated.
            var porter = Porter(assessment.Path);
            var fresh = porter.Assess(index, assessment.Source, report);
            var revision = SourceRevisionReader.Capture(OriginalFolder(project), index, time);
            var output = new PortOutput(project.SourceDirectory, outputRoot);
            try
            {
                var (steps, buildRoot) = porter.Generate(index, fresh, options, output);
                var plan = new MigrationPlan { Steps = steps, Changes = output.Changes, IsDraft = options.Draft };
                var port = new PortReport
                {
                    Id = Guid.NewGuid(), SourceProject = SourceName(project), SourceDirectory = project.SourceLocation, OutputDirectory = outputRoot,
                    BuildRoot = buildRoot, Assessment = fresh, Plan = plan, Options = options, CreatedAt = time.GetUtcNow(),
                    Source = revision, GeneratorVersion = HostInfo.ProductVersion,
                    Verification =
                    [
                        new VerificationEntry(VerificationLevel.Generated, LevelStatus.Passed,
                            $"{plan.Added} file(s) added, {plan.Modified} modified, {plan.Unchanged} copied unchanged{(plan.IsDraft ? " — DRAFT with blocker markers (will not compile)" : "")}.", time.GetUtcNow(), outputRoot),
                        NotYet(VerificationLevel.Validated), NotYet(VerificationLevel.Compiled), NotYet(VerificationLevel.LaunchedInSimulatorOrEmulator), NotYet(VerificationLevel.TestedOnDevice),
                    ],
                };
                Save(port);
                return port;
            }
            catch
            {
                try { Directory.Delete(outputRoot, recursive: true); } catch (IOException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static VerificationEntry NotYet(VerificationLevel level) => new(level, LevelStatus.NotPerformed, level switch
    {
        VerificationLevel.Validated => "Not validated yet.",
        VerificationLevel.Compiled => "Not compiled yet.",
        VerificationLevel.LaunchedInSimulatorOrEmulator => "Not launched yet.",
        _ => "Not tested on a physical device. HCG never claims device testing it did not perform.",
    });

    // ───────────── Validate ─────────────

    public static bool IsWebPackaging(PortingPath path) => path is PortingPath.WebToAndroid or PortingPath.WebToIOS;

    /// <summary>
    /// Checks a generated project without building it for a device: the original and HCG's imported copy are
    /// unchanged, every planned file exists, and the path's own checks pass. For web packaging (after trust is
    /// confirmed) it also installs the dependencies, runs the project's web build and has Capacitor's CLI create the
    /// native project from its template — that runs the project's and npm packages' code. A passed validation is not a
    /// mobile build.
    /// </summary>
    public async Task<PortReport> ValidateAsync(PortReport port, bool trustAcknowledged, Action<string>? progress, CancellationToken cancellationToken)
    {
        var checks = new List<ValidationCheck>();
        var log = new List<string>();
        void Log(string line) { log.Add(line); progress?.Invoke(line); }
        void Check(string name, bool passed, string detail) { checks.Add(new ValidationCheck(name, passed, detail)); Log($"{(passed ? "PASS" : "FAIL")} {name}: {detail}"); }

        await Task.Run(() =>
        {
            // Source preservation.
            if (port.Source is { } rev)
            {
                if (rev.OriginalDirectory is { } orig && rev.OriginalSha256 is { } origSha)
                    Check("Original project unchanged", Directory.Exists(orig) && SourceRevisionReader.Fingerprint(SourceIndex.Build(orig, cancellationToken), out _) == origSha,
                        Directory.Exists(orig) ? $"{orig}: content fingerprint compared with the one recorded at conversion." : $"{orig} no longer exists.");
                else Log("INFO The original was imported from a ZIP file (or moved), so only HCG's imported copy is compared.");
                if (rev.ConvertedDirectory is { } conv && Directory.Exists(conv))
                    Check("Converted source unchanged", SourceRevisionReader.Fingerprint(SourceIndex.Build(conv, cancellationToken), out _) == rev.ContentSha256,
                        $"HCG's imported copy matches sha256 {rev.ContentSha256[..12]}… recorded at conversion.");
            }
            var missing = port.Plan.Changes.Where(c => !File.Exists(Path.Combine(port.OutputDirectory, c.Path.Replace('/', Path.DirectorySeparatorChar)))).Select(c => c.Path).ToList();
            Check("Generated files present", missing.Count == 0, missing.Count == 0 ? $"All {port.Plan.Changes.Count} planned files exist." : "Missing: " + string.Join(", ", missing.Take(10)));
            Check("Not a draft", !port.Plan.IsDraft, port.Plan.IsDraft ? "This is a draft with blocker markers; it cannot be valid until they are ported by hand." : "No blocker markers.");
            if (IsWebPackaging(port.Assessment.Path)) WebStaticChecks(port, Check);
        }, cancellationToken).ConfigureAwait(false);

        var status = checks.All(c => c.Passed) ? LevelStatus.Passed : LevelStatus.Failed;
        var detail = status == LevelStatus.Passed ? $"{checks.Count} check(s) passed" : $"{checks.Count(c => !c.Passed)} of {checks.Count} check(s) failed: " + string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name));
        if (status == LevelStatus.Passed && IsWebPackaging(port.Assessment.Path))
        {
            if (!trustAcknowledged)
            {
                status = LevelStatus.NotPerformed;
                detail += ". The web build and the native project were not created: confirm trust first (this runs npm, the project's build script and Capacitor's CLI).";
            }
            else
            {
                var (ok, webDetail) = await WebNativeProjectAsync(port, Check, Log, cancellationToken).ConfigureAwait(false);
                status = ok switch { true => LevelStatus.Passed, false => LevelStatus.Failed, null => LevelStatus.NotPossibleHere };
                detail = webDetail;
            }
        }
        else if (status == LevelStatus.Passed) detail += " (source preserved, output complete). The next level is a real build.";
        SaveLog(port, log, "validate.log");
        var updated = port with { ValidationChecks = checks };
        return Record(updated, new VerificationEntry(VerificationLevel.Validated, status, detail, time.GetUtcNow(), Path.Combine(port.OutputDirectory, ReportFolder, "validate.log")));
    }

    private static void WebStaticChecks(PortReport port, Action<string, bool, string> check)
    {
        var root = port.OutputDirectory;
        var configPath = Path.Combine(root, WebToMobilePorter.ConfigFile);
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        check("Capacitor configuration", config.Contains($"\"appId\": \"{port.Options.AppId}\"", StringComparison.Ordinal) && !config.Contains("\"url\"", StringComparison.Ordinal),
            "capacitor.config.json has the app id and no development-server URL (the app uses its packaged files).");
        var pkg = File.Exists(Path.Combine(root, "package.json")) ? File.ReadAllText(Path.Combine(root, "package.json")) : "";
        check("Pinned runtime", pkg.Contains($"\"@capacitor/core\": \"{WebToMobilePorter.CapacitorVersion}\"", StringComparison.Ordinal),
            $"package.json pins Capacitor {WebToMobilePorter.CapacitorVersion} exactly (reproducible).");
        var envFiles = Directory.EnumerateFiles(root, ".env*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(Path.GetFileName).OfType<string>().Where(WebDesktopConversion.IsEnvFileWithValues).ToList();
        check("No environment files with values", envFiles.Count == 0, envFiles.Count == 0 ? "No .env files with values were copied." : "Found: " + string.Join(", ", envFiles));
    }

    /// <summary>Trusted part of web validation: npm install, the web build, and Capacitor's CLI adding the native project.</summary>
    private async Task<(bool? Ok, string Detail)> WebNativeProjectAsync(PortReport port, Action<string, bool, string> check, Action<string> log, CancellationToken ct)
    {
        var report = await toolchains.GetAsync(cancellationToken: ct).ConfigureAwait(false);
        var node = report.NodeInstallations.FirstOrDefault(n => int.TryParse(n.Version.TrimStart('v').Split('.')[0], out var major) && major >= 22);
        if (node?.NpmCli is null)
            return (null, $"Capacitor {WebToMobilePorter.CapacitorVersion} needs Node.js 22 or newer with npm" + (report.NodeInstallations.Count == 0 ? ", and Node.js was not found." : $"; found {string.Join(", ", report.NodeInstallations.Select(n => n.Version))}.") + " Install it (nodejs.org) and validate again.");
        var root = port.OutputDirectory;
        var platform = port.Assessment.Target == MobileTarget.Android ? "android" : "ios";
        var facts = WebDesktopConversion.Scan(root);
        var env = new Dictionary<string, string>(ElectronBuildPlanner.Environment(BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment, node)) { ["CI"] = "true", ["CAPACITOR_TELEMETRY"] = "false" };
        async Task<bool> Run(string title, string exe, IReadOnlyList<string> args, int minutes)
        {
            log($"== {title}");
            var r = await runner.RunAsync(new ProcessSpec { Executable = exe, Arguments = args, WorkingDirectory = root, Environment = env, Timeout = TimeSpan.FromMinutes(minutes) },
                (_, line) => { if (line.Length > 0) log("  " + line); }, ct).ConfigureAwait(false);
            check(title, r.Succeeded, r.Succeeded ? "Completed." : r.TimedOut ? $"Did not finish within {minutes} minutes." : $"Exit code {r.ExitCode}; see validate.log.");
            return r.Succeeded;
        }
        if (!await Run("Install dependencies (npm install)", node.NodePath, [node.NpmCli, "install", "--no-audit", "--no-fund"], 20).ConfigureAwait(false))
            return (false, "npm could not install the dependencies (network, or the project's package.json). Nothing was built.");
        if (!await Run($"Web build (npm run {facts.BuildScript})", node.NodePath, [node.NpmCli, "run", facts.BuildScript ?? "build"], 20).ConfigureAwait(false))
            return (false, $"The project's own web build failed (npm run {facts.BuildScript}). See validate.log.");
        var index = Path.Combine(root, facts.OutDir, "index.html");
        var html = File.Exists(index) ? File.ReadAllText(index) : "";
        check("Production build", html.Length > 0 && !html.Contains("/@vite/client", StringComparison.Ordinal) && !html.Contains(":5173", StringComparison.Ordinal),
            html.Length == 0 ? $"{facts.OutDir}/index.html was not produced." : $"{facts.OutDir}/index.html is a production build (no development-server references).");
        var cli = Path.Combine(root, "node_modules", "@capacitor", "cli", "bin", "capacitor");
        if (!await Run($"Create the native {platform} project (cap add {platform})", node.NodePath, [cli, "add", platform], 15).ConfigureAwait(false))
            return (false, $"Capacitor's CLI could not create the {platform}/ project. See validate.log.");
        var assets = platform == "android" ? Path.Combine(root, "android", "app", "src", "main", "assets", "public", "index.html") : Path.Combine(root, "ios", "App", "App", "public", "index.html");
        check("Web build inside the native project", File.Exists(assets), File.Exists(assets) ? $"{Path.GetRelativePath(root, assets)} exists." : $"{Path.GetRelativePath(root, assets)} is missing.");
        var nativeConfig = platform == "android" ? Path.Combine(root, "android", "app", "src", "main", "assets", "capacitor.config.json") : Path.Combine(root, "ios", "App", "App", "capacitor.config.json");
        check("Native configuration", File.Exists(nativeConfig) && !File.ReadAllText(nativeConfig).Contains("\"url\"", StringComparison.Ordinal),
            "The native project's copy of capacitor.config.json has no server URL.");
        var native = platform == "android" ? Path.Combine(root, "android", "gradlew") : Path.Combine(root, "ios", "App", "App.xcodeproj", "project.pbxproj");
        check(platform == "android" ? "Android Gradle project" : "Xcode project", File.Exists(native), File.Exists(native) ? $"{Path.GetRelativePath(root, native)} was created from Capacitor's template." : $"{Path.GetRelativePath(root, native)} is missing.");
        return ((bool?)true, $"Source preserved; web build OK; Capacitor created the native {platform}/ project and copied the build into it. Not yet built for a device or simulator.");
    }

    // ───────────── Compile ─────────────

    public sealed record CompileOutcome(PortReport Report, BuildRecord? Build, string? Artifact);

    public async Task<CompileOutcome> CompileAsync(PortReport port, bool trustAcknowledged, IBuildObserver observer, CancellationToken cancellationToken)
    {
        if (!trustAcknowledged) throw new InvalidOperationException("Compiling runs the generated build and the project's code. Confirm trust first.");
        if (port.Plan.IsDraft)
            return new(Record(port, new VerificationEntry(VerificationLevel.Compiled, LevelStatus.NotPerformed,
                "This is a draft with blocker markers: it cannot compile until the blockers are ported by hand.", time.GetUtcNow())), null, null);
        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var web = IsWebPackaging(port.Assessment.Path);
        var ios = port.Assessment.Target == MobileTarget.Ios;
        if (web && port.Level(VerificationLevel.Validated).Status != LevelStatus.Passed)
            return new(Record(port, new VerificationEntry(VerificationLevel.Compiled, LevelStatus.NotPerformed,
                "Validate the generated project first: validation runs the web build and creates the native project that is compiled here.", time.GetUtcNow())), null, null);
        if (ios && report.Host.Platform != TargetPlatform.MacOS)
            return new(Record(port, new VerificationEntry(VerificationLevel.Compiled, LevelStatus.NotPossibleHere,
                $"iOS apps compile only on a Mac with Xcode. This computer runs {report.Host.Platform.DisplayName()}: copy the output folder to a Mac and compile it there.", time.GetUtcNow())), null, null);
        if (!ios)
            foreach (var (id, name) in web
                         ? new[] { (ToolchainIds.AndroidSdk, "Android SDK"), (ToolchainIds.Jdk, "JDK 21") }
                         : new[] { (ToolchainIds.AndroidNdk, "Android NDK"), (ToolchainIds.AndroidCmake, "CMake (Android SDK)"), (ToolchainIds.AndroidSdk, "Android SDK"), (ToolchainIds.Jdk, "JDK") })
                if (!report.IsAvailable(id))
                    return new(Record(port, new VerificationEntry(VerificationLevel.Compiled, LevelStatus.NotPossibleHere,
                        $"{name} is not installed, so the native build cannot run here. {report.Find(id)?.InstallHint}" + (web ? " HCG does not install SDKs; the generated project is complete and can also be opened in Android Studio." : " The generated project pins the NDK/CMake versions found when it was generated, so Gradle does not download them either."), time.GetUtcNow())), null, null);

        // The port is imported like any project, then built by Mobile Build Studio's engine (logging, history, verification).
        var import = await projects.ImportDirectoryAsync(port.OutputDirectory, null, cancellationToken).ConfigureAwait(false);
        var project = import.Project;
        var index = SourceIndex.Build(project.SourceDirectory, cancellationToken);
        MobileAnalysis mobileAnalysis;
        MobileBuildOptions options;
        string relative;
        if (web)
        {
            var cap = CapacitorAnalyzer.Analyze(index, "");
            var and = ios ? null : AndroidGradleAnalyzer.Analyze(index, "android", out _);
            var xcodeProject = ios ? XcodeProjectAnalyzer.FindMobileProjects(index).FirstOrDefault(p => p.StartsWith("ios/", StringComparison.Ordinal)) : null;
            var iosInfo = xcodeProject is null ? null : XcodeProjectAnalyzer.Analyze(index, xcodeProject, out _);
            if (ios && iosInfo is null) throw new InvalidOperationException("The Capacitor iOS project (ios/App/App.xcodeproj) was not found in the imported project. Validate again.");
            mobileAnalysis = new MobileAnalysis
            {
                Framework = MobileFramework.Capacitor, Platforms = [ios ? TargetPlatform.IOS : TargetPlatform.Android], Capacitor = cap, Android = and, Ios = iosInfo,
                ApplicationIds = [port.Options.AppId],
            };
            options = ios
                ? new MobileBuildOptions { Framework = MobileFramework.Capacitor, Output = MobileOutput.IosSimulatorApp, Container = xcodeProject, Scheme = iosInfo!.Schemes.FirstOrDefault()?.Name ?? "App", Variant = "Debug" }
                : new MobileBuildOptions { Framework = MobileFramework.Capacitor, Output = MobileOutput.AndroidDebugApk, Module = ":app", Variant = "debug" };
            relative = ios ? xcodeProject! : "android";
        }
        else if (ios)
        {
            var proj = XcodeProjectAnalyzer.FindProjects(index).FirstOrDefault(p => PortOutput.Join(port.BuildRoot, "") is var root && (root.Length == 0 ? !p.Contains('/') : p.StartsWith(root + "/", StringComparison.Ordinal) && !p[(root.Length + 1)..].Contains('/')))
                       ?? throw new InvalidOperationException("The generated Xcode project was not found in the imported port.");
            var info = XcodeProjectAnalyzer.Analyze(index, proj, out _);
            mobileAnalysis = new MobileAnalysis { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = info, ApplicationIds = [port.Options.AppId] };
            options = new MobileBuildOptions
            {
                Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp, Container = proj,
                Scheme = info.Schemes.FirstOrDefault()?.Name, Variant = "Debug",
            };
            relative = proj;
        }
        else
        {
            var info = AndroidGradleAnalyzer.Analyze(index, port.BuildRoot, out _);
            mobileAnalysis = new MobileAnalysis { Framework = MobileFramework.NativeAndroid, Platforms = [TargetPlatform.Android], Android = info, ApplicationIds = [port.Options.AppId] };
            options = new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk, Module = ":app", Variant = "debug" };
            relative = port.BuildRoot;
        }
        var analysis = (project.Analysis ?? new ProjectAnalysis { InspectorId = "porting", ProjectName = project.Name, Language = "", Framework = "", ApplicationType = "", Support = SupportLevel.Supported, SupportSummary = "", AnalyzedAt = time.GetUtcNow() })
            with { Mobile = mobileAnalysis };
        await projects.UpdateAnalysisAsync(project, analysis, cancellationToken).ConfigureAwait(false);

        var build = await mobile.RunAsync(new MobileBuildRequest
        {
            Project = project, TrustAcknowledged = true, AllowOverwriteOutput = true,
            Settings = new BuildSettings
            {
                ProjectRelativePath = relative, Platform = options.Output.Platform(), Architecture = TargetArchitecture.Universal,
                OutputDirectory = port.OutputDirectory + ".builds", Timeout = TimeSpan.FromMinutes(45),
                Identity = new AppIdentity { ApplicationName = port.Options.AppName, Version = port.Options.Version, ApplicationId = port.Options.AppId },
                Mobile = options,
            },
        }, observer, cancellationToken).ConfigureAwait(false);
        var artifact = build.Artifacts.FirstOrDefault(a => a.Path.EndsWith(".app", StringComparison.Ordinal) || a.Path.EndsWith(".apk", StringComparison.Ordinal))?.Path;
        var entry = build.Status == BuildStatus.Succeeded && artifact is not null
            ? new VerificationEntry(VerificationLevel.Compiled, LevelStatus.Passed,
                $"{options.Output.DisplayName()} built by Mobile Build Studio ({build.Artifacts.Count} artifact(s), {build.WarningCount} warning(s), verification {build.Artifacts.First().Verification}). Build {build.Id:N}.", time.GetUtcNow(), artifact)
            : new VerificationEntry(VerificationLevel.Compiled, LevelStatus.Failed,
                $"{options.Output.DisplayName()} failed: {build.FailureReason ?? build.Status.ToString()} ({build.ErrorCount} error(s)). Full log: {build.LogPath}", time.GetUtcNow(), build.LogPath);
        return new(Record(port, entry), build, artifact);
    }

    // ───────────── Launch ─────────────

    /// <param name="expectText">For apps (not console programs): text the launched screen must show (read through accessibility).</param>
    public async Task<PortReport> LaunchAsync(PortReport port, bool trustAcknowledged, CancellationToken cancellationToken, string? expectText = null)
    {
        if (!trustAcknowledged) throw new InvalidOperationException("Launching runs the ported app. Confirm trust first.");
        var compiled = port.Level(VerificationLevel.Compiled);
        if (compiled.Status != LevelStatus.Passed || compiled.Evidence is null)
            return Record(port, new VerificationEntry(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.NotPerformed, "Compile the port successfully first.", time.GetUtcNow()));
        var shot = Path.Combine(port.OutputDirectory, ReportFolder, $"launch-{time.GetUtcNow():yyyyMMdd-HHmmss}.png");
        if (port.Assessment.Target == MobileTarget.Ios)
        {
            var r = await launcher.LaunchIosSimulatorAsync(compiled.Evidence, port.Options.AppId, shot, cancellationToken).ConfigureAwait(false);
            SaveLog(port, r.Log);
            return Record(port, new VerificationEntry(r.Level, r.Status, r.Detail, time.GetUtcNow(), r.ScreenshotPath));
        }
        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (report.Find(ToolchainIds.Adb) is not { IsAvailable: true, ExecutablePath: { } adb })
            return Record(port, new VerificationEntry(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.NotPossibleHere, "adb (Android SDK Platform-Tools) is not installed.", time.GetUtcNow()));
        var results = IsWebPackaging(port.Assessment.Path)
            ? await launcher.LaunchAndroidAppAsync(adb, compiled.Evidence, port.Options.AppId, port.Options.AppId + ".MainActivity", shot, expectText, cancellationToken).ConfigureAwait(false)
            : await launcher.LaunchAndroidAsync(adb, compiled.Evidence, port.Options.AppId, "hcg.port.console.MainActivity", shot, cancellationToken).ConfigureAwait(false);
        foreach (var r in results)
        {
            SaveLog(port, r.Log);
            port = Record(port, new VerificationEntry(r.Level, r.Status, r.Detail, time.GetUtcNow(), r.ScreenshotPath));
        }
        return port;
    }

    // ───────────── Report files ─────────────

    private PortReport Record(PortReport port, VerificationEntry entry)
    {
        var updated = port.With(entry);
        Save(updated);
        return updated;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static PortReport Load(string outputDirectory) =>
        JsonSerializer.Deserialize<PortReport>(File.ReadAllText(Path.Combine(outputDirectory, ReportFolder, "report.json")), Json)
        ?? throw new InvalidDataException("Unreadable port report.");

    public static void Save(PortReport port)
    {
        var dir = Path.Combine(port.OutputDirectory, ReportFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(port, Json));
        File.WriteAllText(Path.Combine(dir, "PLAN.md"), Markdown(port));
        File.WriteAllText(Path.Combine(dir, "changes.diff"), string.Concat(port.Plan.Changes.Where(c => c.Kind != FileChangeKind.CopiedUnchanged).Select(c => c.Diff)));
        File.WriteAllText(Path.Combine(dir, "MIGRATION.md"), MigrationReport(port));
        File.WriteAllText(Path.Combine(dir, "conversion.json"), JsonSerializer.Serialize(ConversionRecord(port), Json));
    }

    /// <summary>What reproduces this result: the source revision, the tool version, the path, mode, target, options and pinned versions.</summary>
    public static object ConversionRecord(PortReport p) => new
    {
        schema = "hcg.executable-maker.mobile-conversion/1",
        generator = $"HCG Executable Maker {p.GeneratorVersion ?? "(unknown)"}",
        createdAt = p.CreatedAt,
        source = new
        {
            project = p.SourceProject, location = p.SourceDirectory, key = p.Assessment.Source.Key, kind = p.Assessment.Source.Kind,
            vcs = p.Source?.Vcs, commit = p.Source?.Commit, branch = p.Source?.Branch,
            contentSha256 = p.Source?.ContentSha256, fileCount = p.Source?.FileCount, originalSha256 = p.Source?.OriginalSha256,
        },
        conversion = new
        {
            path = p.Assessment.Path.ToString(), mode = p.Assessment.Mode.ToString(), target = p.Assessment.Target.ToString(),
            appName = p.Options.AppName, appId = p.Options.AppId, version = p.Options.Version, buildNumber = p.Options.BuildNumber,
            draft = p.Options.Draft, ndk = p.Options.NdkVersion, cmake = p.Options.CmakeVersion, defaultArguments = p.Options.DefaultArguments,
            usageDescriptions = p.Options.UsageDescriptions,
        },
        pins = p.Assessment.Pins,
        output = new { directory = p.OutputDirectory, buildRoot = p.BuildRoot, added = p.Plan.Added, modified = p.Plan.Modified, unchanged = p.Plan.Unchanged },
        reproduce = "Import the same source revision, choose the same path and target, enter the options above, and generate: the plan and diff are recomputed from the source.",
    };

    /// <summary>
    /// The migration report: what was modified, what is unresolved (with the four feature classes), and the manual steps.
    /// </summary>
    public static string MigrationReport(PortReport p)
    {
        var a = p.Assessment;
        var sb = new StringBuilder();
        sb.AppendLine($"# Migration report: {p.SourceProject} → {a.Target.DisplayName()}");
        sb.AppendLine();
        sb.AppendLine($"- Conversion: **{a.Mode.DisplayName()}** ({a.Path.DisplayName()})");
        sb.AppendLine($"- Source: `{a.Source.DisplayName}` ({a.Source.Kind}) from `{p.SourceDirectory}`; {p.Source?.Describe() ?? "revision not recorded"}");
        sb.AppendLine($"- Generated {p.CreatedAt:u} by HCG Executable Maker {p.GeneratorVersion ?? "(unknown)"} into `{p.OutputDirectory}`; the original project was not modified.");
        if (a.Pins.Count > 0) sb.AppendLine($"- Pinned: {string.Join(", ", a.Pins.Select(kv => $"{kv.Key} {kv.Value}"))}");
        sb.AppendLine("- Reproduce with the values in `conversion.json`.");
        sb.AppendLine();
        sb.AppendLine("## Status (each level separately — a generated project is not a verified mobile build)");
        sb.AppendLine();
        sb.AppendLine("| Level | Result | Detail |");
        sb.AppendLine("|---|---|---|");
        foreach (var level in Enum.GetValues<VerificationLevel>())
        {
            var e = p.Level(level);
            sb.AppendLine($"| {LevelText(level)} | {StatusText(e.Status)} | {e.Detail.Replace("|", "\\|")} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Modifications");
        sb.AppendLine();
        foreach (var c in p.Plan.Changes.Where(c => c.Kind != FileChangeKind.CopiedUnchanged))
            sb.AppendLine($"- {c.Kind}: `{c.Path}` — {c.Reason}");
        sb.AppendLine($"- {p.Plan.Unchanged} file(s) copied unchanged. Full diff: `changes.diff`.");
        sb.AppendLine();
        sb.AppendLine("## Features by class");
        foreach (var cls in Enum.GetValues<FeatureClass>())
        {
            var items = a.Findings.Where(f => f.Class == cls).ToList();
            sb.AppendLine();
            sb.AppendLine($"### {cls.DisplayName()} ({items.Count})");
            if (cls == FeatureClass.Reusable)
                foreach (var u in a.Reusable) sb.AppendLine($"- {u.Name}: {u.Files} file(s), {u.Kind}");
            foreach (var f in items)
                sb.AppendLine($"- **{f.Title}**{(f.Location.Length == 0 ? "" : $" (`{f.Location}`)")}: {f.Detail}{(f.Guidance is null ? "" : $" — {f.Guidance}")}");
        }
        sb.AppendLine();
        sb.AppendLine("## Unresolved (not generated, not faked)");
        sb.AppendLine();
        var unresolved = a.Findings.Where(f => f.Disposition is FindingDisposition.Blocker or FindingDisposition.RequiresCompanion or FindingDisposition.NeedsReview).ToList();
        if (unresolved.Count == 0) sb.AppendLine("- Nothing: every finding is reusable or adapted automatically.");
        foreach (var f in unresolved)
            sb.AppendLine($"- [{f.Class.DisplayName()}] {f.Title}{(f.Location.Length == 0 ? "" : $" (`{f.Location}`)")}");
        sb.AppendLine();
        sb.AppendLine("## Manual steps");
        sb.AppendLine();
        foreach (var step in a.ManualSteps.Concat(a.Signing).Distinct()) sb.AppendLine($"1. {step}");
        sb.AppendLine("1. Publishing, TestFlight upload and store submission are never done by HCG.");
        return sb.ToString();
    }

    private static void SaveLog(PortReport port, IReadOnlyList<string> log, string file = "launch.log")
    {
        var dir = Path.Combine(port.OutputDirectory, ReportFolder);
        Directory.CreateDirectory(dir);
        File.AppendAllLines(Path.Combine(dir, file), log.Select(l => SecretRedactor.Redact(l)).Prepend($"--- {DateTimeOffset.Now:u}"));
    }

    public static string StatusText(LevelStatus s) => s switch
    {
        LevelStatus.Passed => "Yes",
        LevelStatus.Failed => "Failed",
        LevelStatus.NotPossibleHere => "Not possible on this computer",
        _ => "Not performed",
    };

    public static string LevelText(VerificationLevel l) => l switch
    {
        VerificationLevel.Generated => "Generated",
        VerificationLevel.Validated => "Validated",
        VerificationLevel.Compiled => "Compiled",
        VerificationLevel.LaunchedInSimulatorOrEmulator => "Launched in simulator/emulator",
        _ => "Tested on a physical device",
    };

    public static string Markdown(PortReport p)
    {
        var a = p.Assessment;
        var sb = new StringBuilder();
        sb.AppendLine($"# Port: {p.SourceProject} → {a.Target.DisplayName()} ({a.Mode.DisplayName()})");
        sb.AppendLine();
        sb.AppendLine($"Generated {p.CreatedAt:u} by HCG Executable Maker from `{a.Source.DisplayName}` ({a.Source.Kind}). The original project was not modified.");
        sb.AppendLine();
        sb.AppendLine("## Verification (each level reported separately)");
        sb.AppendLine();
        sb.AppendLine("| Level | Result | Detail |");
        sb.AppendLine("|---|---|---|");
        foreach (var level in Enum.GetValues<VerificationLevel>())
        {
            var e = p.Level(level);
            sb.AppendLine($"| {LevelText(level)} | {StatusText(e.Status)} | {e.Detail.Replace("|", "\\|")} |");
        }
        sb.AppendLine();
        sb.AppendLine($"## Assessment: {a.Capability}");
        sb.AppendLine();
        sb.AppendLine(a.Summary);
        sb.AppendLine();
        foreach (var group in a.Findings.GroupBy(f => f.Disposition))
        {
            sb.AppendLine($"### {group.Key} ({group.Count()})");
            foreach (var f in group)
                sb.AppendLine($"- **{f.Title}**{(f.Location.Length == 0 ? "" : $" — `{f.Location}`")}: {f.Detail}{(f.Guidance is null ? "" : $" _{f.Guidance}_")}");
            sb.AppendLine();
        }
        sb.AppendLine("## Migration plan");
        sb.AppendLine();
        foreach (var s in p.Plan.Steps) sb.AppendLine($"1. {s}");
        sb.AppendLine();
        sb.AppendLine($"Files: {p.Plan.Added} added, {p.Plan.Modified} modified, {p.Plan.Unchanged} copied unchanged. Full diff: `changes.diff`.");
        sb.AppendLine();
        foreach (var c in p.Plan.Changes.Where(c => c.Kind != FileChangeKind.CopiedUnchanged))
            sb.AppendLine($"- {c.Kind}: `{c.Path}` — {c.Reason}");
        sb.AppendLine();
        sb.AppendLine("## Signing and distribution");
        sb.AppendLine();
        foreach (var s in a.Signing) sb.AppendLine($"- {s}");
        sb.AppendLine();
        sb.AppendLine("## Scope of this conversion path");
        sb.AppendLine();
        foreach (var s in a.Scope) sb.AppendLine($"- {s}");
        return sb.ToString();
    }
}
