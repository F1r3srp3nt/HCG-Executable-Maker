using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Porting.Android;
using HCG.ExecutableMaker.Porting.Apple;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Porting;

/// <summary>
/// Source-based porting: assess → generate a separate output copy with a reviewable plan and diff → compile with
/// Mobile Build Studio's engine → launch in a simulator/emulator. Each level is reported on its own; none is implied by
/// another. Publishing and store submission are never performed.
/// </summary>
public sealed class PortingService(IToolchainService toolchains, ProjectService projects, MobileBuildService mobile, DeviceLauncher launcher, TimeProvider time)
{
    public const string ReportFolder = "HCG-PORT";
    public IReadOnlyList<IPorter> Porters { get; } = [new MacToIosPorter(), new LinuxToAndroidPorter()];

    public IPorter Porter(PortingPath path) => Porters.First(p => p.Path == path);

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
            $"{PathGuard.SanitizeFileName(project.Name, "project")}-{(assessment.Path == PortingPath.MacOSToIOS ? "ios" : "android")}-port-{time.GetLocalNow():yyyyMMdd-HHmmss}");

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
            var output = new PortOutput(project.SourceDirectory, outputRoot);
            try
            {
                var (steps, buildRoot) = porter.Generate(index, fresh, options, output);
                var plan = new MigrationPlan { Steps = steps, Changes = output.Changes, IsDraft = options.Draft };
                var port = new PortReport
                {
                    Id = Guid.NewGuid(), SourceProject = project.Name, SourceDirectory = project.SourceLocation, OutputDirectory = outputRoot,
                    BuildRoot = buildRoot, Assessment = fresh, Plan = plan, Options = options, CreatedAt = time.GetUtcNow(),
                    Verification =
                    [
                        new VerificationEntry(VerificationLevel.Generated, LevelStatus.Passed,
                            $"{plan.Added} file(s) added, {plan.Modified} modified, {plan.Unchanged} copied unchanged{(plan.IsDraft ? " — DRAFT with blocker markers (will not compile)" : "")}.", time.GetUtcNow(), outputRoot),
                        NotYet(VerificationLevel.Compiled), NotYet(VerificationLevel.LaunchedInSimulatorOrEmulator), NotYet(VerificationLevel.TestedOnDevice),
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
        VerificationLevel.Compiled => "Not compiled yet.",
        VerificationLevel.LaunchedInSimulatorOrEmulator => "Not launched yet.",
        _ => "Not tested on a physical device. HCG never claims device testing it did not perform.",
    });

    // ───────────── Compile ─────────────

    public sealed record CompileOutcome(PortReport Report, BuildRecord? Build, string? Artifact);

    public async Task<CompileOutcome> CompileAsync(PortReport port, bool trustAcknowledged, IBuildObserver observer, CancellationToken cancellationToken)
    {
        if (!trustAcknowledged) throw new InvalidOperationException("Compiling runs the generated build and the project's code. Confirm trust first.");
        if (port.Plan.IsDraft)
            return new(Record(port, new VerificationEntry(VerificationLevel.Compiled, LevelStatus.NotPerformed,
                "This is a draft with blocker markers: it cannot compile until the blockers are ported by hand.", time.GetUtcNow())), null, null);
        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var ios = port.Assessment.Path == PortingPath.MacOSToIOS;
        if (ios && report.Host.Platform != TargetPlatform.MacOS)
            return new(Record(port, new VerificationEntry(VerificationLevel.Compiled, LevelStatus.NotPossibleHere,
                $"iOS apps compile only on a Mac with Xcode. This computer runs {report.Host.Platform.DisplayName()}: copy the output folder to a Mac and compile it there.", time.GetUtcNow())), null, null);
        if (!ios)
            foreach (var (id, name) in new[] { (ToolchainIds.AndroidNdk, "Android NDK"), (ToolchainIds.AndroidCmake, "CMake (Android SDK)"), (ToolchainIds.AndroidSdk, "Android SDK"), (ToolchainIds.Jdk, "JDK") })
                if (!report.IsAvailable(id))
                    return new(Record(port, new VerificationEntry(VerificationLevel.Compiled, LevelStatus.NotPossibleHere,
                        $"{name} is not installed, so the native build cannot run here. {report.Find(id)?.InstallHint} (HCG does not install SDK components, and the generated project pins versions so Gradle does not download them either.)", time.GetUtcNow())), null, null);

        // The port is imported like any project, then built by Mobile Build Studio's engine (logging, history, verification).
        var import = await projects.ImportDirectoryAsync(port.OutputDirectory, null, cancellationToken).ConfigureAwait(false);
        var project = import.Project;
        var index = SourceIndex.Build(project.SourceDirectory, cancellationToken);
        MobileAnalysis mobileAnalysis;
        MobileBuildOptions options;
        string relative;
        if (ios)
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

    public async Task<PortReport> LaunchAsync(PortReport port, bool trustAcknowledged, CancellationToken cancellationToken)
    {
        if (!trustAcknowledged) throw new InvalidOperationException("Launching runs the ported app. Confirm trust first.");
        var compiled = port.Level(VerificationLevel.Compiled);
        if (compiled.Status != LevelStatus.Passed || compiled.Evidence is null)
            return Record(port, new VerificationEntry(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.NotPerformed, "Compile the port successfully first.", time.GetUtcNow()));
        var shot = Path.Combine(port.OutputDirectory, ReportFolder, $"launch-{time.GetUtcNow():yyyyMMdd-HHmmss}.png");
        if (port.Assessment.Path == PortingPath.MacOSToIOS)
        {
            var r = await launcher.LaunchIosSimulatorAsync(compiled.Evidence, port.Options.AppId, shot, cancellationToken).ConfigureAwait(false);
            SaveLog(port, r.Log);
            return Record(port, new VerificationEntry(r.Level, r.Status, r.Detail, time.GetUtcNow(), r.ScreenshotPath));
        }
        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (report.Find(ToolchainIds.Adb) is not { IsAvailable: true, ExecutablePath: { } adb })
            return Record(port, new VerificationEntry(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.NotPossibleHere, "adb (Android SDK Platform-Tools) is not installed.", time.GetUtcNow()));
        var results = await launcher.LaunchAndroidAsync(adb, compiled.Evidence, port.Options.AppId, "hcg.port.console.MainActivity", shot, cancellationToken).ConfigureAwait(false);
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
    }

    private static void SaveLog(PortReport port, IReadOnlyList<string> log)
    {
        var dir = Path.Combine(port.OutputDirectory, ReportFolder);
        Directory.CreateDirectory(dir);
        File.AppendAllLines(Path.Combine(dir, "launch.log"), log.Prepend($"--- {DateTimeOffset.Now:u}"));
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
        VerificationLevel.Compiled => "Compiled",
        VerificationLevel.LaunchedInSimulatorOrEmulator => "Launched in simulator/emulator",
        _ => "Tested on a physical device",
    };

    public static string Markdown(PortReport p)
    {
        var a = p.Assessment;
        var sb = new StringBuilder();
        sb.AppendLine($"# Port: {p.SourceProject} → {(a.Path == PortingPath.MacOSToIOS ? "iOS" : "Android")}");
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
