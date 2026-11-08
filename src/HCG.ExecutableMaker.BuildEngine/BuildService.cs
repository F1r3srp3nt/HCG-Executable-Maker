using System.Diagnostics;
using System.Text;
using HCG.ExecutableMaker.BuildEngine.Electron;
using HCG.ExecutableMaker.BuildEngine.Swift;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Icons;
using HCG.ExecutableMaker.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.BuildEngine;

public sealed record BuildRequest
{
    public required ProjectRecord Project { get; init; }
    public required BuildSettings Settings { get; init; }
    /// <summary>The user explicitly confirmed they trust the project. Builds are refused without it.</summary>
    public required bool TrustAcknowledged { get; init; }
    /// <summary>Allows replacing a previous HCG output folder for the same app/version/RID.</summary>
    public bool AllowOverwriteOutput { get; init; }
    /// <summary>Assembly name of the project being built (from inspection), used to locate the app host.</summary>
    public string? AssemblyName { get; init; }
}

/// <summary>Receives live build events. Called from background threads.</summary>
public interface IBuildObserver
{
    void OnStarted(BuildRecord build) { }
    void OnStage(BuildStage stage, string description) { }
    void OnLog(BuildLogLine line) { }
    void OnMessage(BuildMessage message) { }
}

/// <summary>
/// Runs a complete build: validate → restore → build → publish → verify output → package → checksum → persist.
/// A build is marked <see cref="BuildStatus.Succeeded"/> only when every command exits 0, the expected executable
/// exists in the publish output, and no artifact verification fails.
/// </summary>
public sealed class BuildService(
    IBuildWorker worker,
    IToolchainService toolchains,
    IBuildRepository builds,
    PackagingRegistry packagers,
    IAppPaths paths,
    TimeProvider time,
    ILogger<BuildService>? logger = null)
{
    public const string OutputMarkerFile = ".hcg-build.json";
    private readonly ILogger _logger = logger ?? NullLogger<BuildService>.Instance;

    public IBuildWorker Worker => worker;

    public async Task<BuildRecord> RunAsync(BuildRequest request, IBuildObserver observer, CancellationToken cancellationToken)
    {
        if (!request.TrustAcknowledged)
            throw new InvalidOperationException("Build refused: the user has not acknowledged that this project is trusted. " + TrustPolicy.TrustBoundaryExplanation);

        var settings = request.Settings;
        var build = new BuildRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = request.Project.Id,
            ProjectName = request.Project.Name,
            Settings = settings,
            Status = BuildStatus.Running,
            StartedAt = time.GetUtcNow(),
            TrustAcknowledged = true,
        };
        var buildDir = Path.Combine(paths.BuildsDirectory, build.Id.ToString("N"));
        Directory.CreateDirectory(buildDir);
        build.LogPath = Path.Combine(buildDir, "build.log");
        build.ToolchainVersions["hcg-executable-maker"] = HostInfo.ProductVersion;

        await using var logWriter = new StreamWriter(build.LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var logGate = new object();
        void Log(BuildLogKind kind, string text)
        {
            var line = new BuildLogLine(time.GetUtcNow(), kind, text);
            lock (logGate) logWriter.WriteLine($"[{line.Timestamp:HH:mm:ss}] {(kind is BuildLogKind.Output ? "  " : kind.ToString().ToUpperInvariant() + " ")}{text}");
            observer.OnLog(line);
        }
        void AddMessage(BuildMessage m)
        {
            lock (build.Messages)
            {
                build.Messages.Add(m);
                if (m.Severity == DiagnosticSeverity.Warning) build.WarningCount++;
                if (m.Severity == DiagnosticSeverity.Error) build.ErrorCount++;
            }
            observer.OnMessage(m);
        }
        void Fail(BuildStatus status, string reason)
        {
            build.Status = status;
            build.FailureReason = reason;
            Log(BuildLogKind.Error, reason);
        }

        await builds.InsertAsync(build, CancellationToken.None).ConfigureAwait(false);
        observer.OnStarted(build);

        string? createdOutputRoot = null;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(settings.Timeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            observer.OnStage(BuildStage.Preparing, "Validating configuration and toolchains");
            Log(BuildLogKind.Info, $"HCG Executable Maker {HostInfo.ProductVersion} — build {build.Id}");
            Log(BuildLogKind.Info, $"Project: {request.Project.Name} ({settings.ProjectRelativePath})");
            Log(BuildLogKind.Info, settings.Electron is { } el
                ? $"Target: {settings.Platform.DisplayName()} {settings.Architecture.DisplayName()} · Electron app {(el.ProjectDirectory.Length == 0 ? "(root)" : el.ProjectDirectory)} · electron-builder --{el.Platform} {string.Join(' ', el.Targets)}{(el.PackagingScript is null ? "" : $" (following script \"{el.PackagingScript}\")")}"
                : settings.Swift is { } sw
                ? $"Target: {settings.Platform.DisplayName()} {settings.Architecture.DisplayName()}, {settings.Configuration} · Swift package {(sw.PackageDirectory.Length == 0 ? "(root)" : sw.PackageDirectory)} · product {sw.Product} ({(sw.Kind == SwiftAppKind.GuiApp ? "macOS app" : "command-line tool")})"
                : $"Target: {settings.Platform.DisplayName()} {settings.Architecture.DisplayName()} ({settings.RuntimeIdentifier}), {settings.Configuration}, {(settings.SelfContained ? "self-contained" : "framework-dependent")}{(settings.SingleFile ? ", single-file" : "")}");
            Log(BuildLogKind.Warning, $"Isolation: {worker.Description}");

            var report = await toolchains.GetAsync(cancellationToken: overall.Token).ConfigureAwait(false);
            var host = report.Host;
            build.ToolchainVersions["host"] = $"{host.DisplayName} ({host.OsDescription})";
            if (host.Platform == TargetPlatform.MacOS)
            {
                // Every tool this build starts inherits HCG's environment, so this is the Xcode/CLT the whole build uses.
                var developerDir = Environment.GetEnvironmentVariable("DEVELOPER_DIR");
                build.ToolchainVersions["developer-dir"] = developerDir ?? "xcode-select";
                Log(BuildLogKind.Info, developerDir is { Length: > 0 }
                    ? $"Developer directory: {developerDir} (DEVELOPER_DIR, applies to every tool this build runs; the system setting is unchanged)"
                    : "Developer directory: the system setting (xcode-select -p)");
                if (report.Xcode is { } x)
                    Log(BuildLogKind.Info, $"Xcode: {x.Diagnosis}");
            }
            var isSwift = settings.Swift is not null;
            var isElectron = settings.Electron is not null;
            ToolchainInfo? sdk = null, swiftTool = null;
            NodeInstallation? node = null;
            string? electronDir = null;
            ElectronBuildPlanner.Manifest? nodeManifest = null;
            if (isElectron)
            {
                var source = PathGuard.GetRealPath(request.Project.SourceDirectory);
                ElectronBuildPlanner.Validate(settings, host, source);
                electronDir = ElectronBuildPlanner.ProjectDirectory(settings, source);
                nodeManifest = ElectronBuildPlanner.ReadManifest(electronDir);
                string detail;
                (node, detail) = HCG.ExecutableMaker.ProjectInspection.Node.NodeCapabilities.SelectNode(report, nodeManifest.EnginesNode);
                if (node is null)
                {
                    Fail(BuildStatus.Failed, detail);
                    return build;
                }
                if (node.NpmCli is null)
                {
                    Fail(BuildStatus.Failed, $"npm was not found next to Node.js {node.Version} ({node.NodePath}). Reinstall Node.js, which includes npm.");
                    return build;
                }
                if (nodeManifest.EnginesNpm is { } wantNpm && node.NpmVersion is { } haveNpm
                    && HCG.ExecutableMaker.ProjectInspection.Node.NodeVersionRange.Check(haveNpm, wantNpm) == HCG.ExecutableMaker.ProjectInspection.Node.NodeVersionRange.Result.NotSatisfied)
                {
                    Fail(BuildStatus.Failed, $"The project requires npm {wantNpm}; Node.js {node.Version} ships npm {haveNpm}.");
                    return build;
                }
                build.ToolchainVersions["node"] = $"{node.Version} ({node.NodePath})";
                build.ToolchainVersions["npm"] = node.NpmVersion ?? "unknown";
                Log(BuildLogKind.Info, $"Node.js: {detail}{(nodeManifest.EnginesNode is null ? "" : $"; package.json requires {nodeManifest.EnginesNode}")}");
                Log(BuildLogKind.Info, $"npm: {node.NpmVersion ?? "(version unknown)"} ({node.NpmCli})");
            }
            else if (isSwift)
            {
                if (host.Platform != TargetPlatform.MacOS)
                {
                    Fail(BuildStatus.Failed, $"Swift macOS apps can only be built on a Mac. This computer runs {host.Platform.DisplayName()}.");
                    return build;
                }
                swiftTool = report.Find(ToolchainIds.Swift);
                if (swiftTool is not { IsAvailable: true, ExecutablePath: not null })
                {
                    Fail(BuildStatus.Failed, "The Swift compiler was not found. " + HCG.ExecutableMaker.ProjectInspection.Swift.SwiftCapabilities.DeveloperDirHint);
                    return build;
                }
                build.ToolchainVersions["swift"] = swiftTool.Version ?? "unknown";
                if (report.Find(ToolchainIds.MacOSSdk) is { IsAvailable: true, Version: { } sdkVersion }) build.ToolchainVersions["macos-sdk"] = sdkVersion;
                if (report.Find(ToolchainIds.MacOSVersion) is { IsAvailable: true, Version: { } macVersion }) build.ToolchainVersions["macos"] = macVersion;
                if (report.Find(ToolchainIds.XcodeCommandLineTools) is { IsAvailable: true, ExecutablePath: { } devDir }) build.ToolchainVersions["developer-dir"] = devDir;
                SwiftBuildPlanner.Validate(settings, host.Platform);
            }
            else
            {
                sdk = report.Find(ToolchainIds.DotNetSdk);
                if (sdk is not { IsAvailable: true, ExecutablePath: not null })
                {
                    Fail(BuildStatus.Failed, "The .NET SDK was not found. Install the .NET SDK and refresh the Toolchains page.");
                    return build;
                }
                build.ToolchainVersions["dotnet-sdk"] = sdk.Version ?? "unknown";
                DotNetBuildPlanner.Validate(settings);
            }
            if (report.Find(ToolchainIds.Git) is { IsAvailable: true, Version: { } gitVersion }) build.ToolchainVersions["git"] = gitVersion;
            var scrub = worker.CreateEnvironment();
            if (scrub.RemovedNames.Count > 0)
                Log(BuildLogKind.Info, $"Removed {scrub.RemovedNames.Count} sensitive environment variable(s) from the build environment: {string.Join(", ", scrub.RemovedNames)}");

            // Icons are generated before compilation so the Windows icon can be embedded.
            GeneratedIcons? icons = null;
            if (isElectron && settings.Identity.IconSourcePath is not null)
                Log(BuildLogKind.Warning, "The icon chosen in HCG is not used for Electron apps: electron-builder takes the icon from its configuration (mac.icon / buildResources).");
            else if (settings.Identity.IconSourcePath is { } iconSource)
            {
                Log(BuildLogKind.Info, $"Processing icon {Path.GetFileName(iconSource)}");
                icons = IconGenerator.Generate(iconSource, Path.Combine(buildDir, "icons"), "AppIcon",
                    ico: settings.Platform == TargetPlatform.Windows, icns: settings.Platform == TargetPlatform.MacOS);
                foreach (var w in icons.Warnings) AddMessage(new BuildMessage(DiagnosticSeverity.Warning, w, "HCG-ICON"));
                if (settings.Platform == TargetPlatform.Windows && host.Platform != TargetPlatform.Windows)
                    AddMessage(new BuildMessage(DiagnosticSeverity.Warning,
                        "The .exe file icon is embedded only when building on a Windows host. The generated .ico is included in the build folder.", "HCG-ICON"));
            }

            // Real (symlink-free) paths only: see PathGuard.GetRealPath.
            var publishDir = Path.Combine(PathGuard.GetRealPath(buildDir), "publish");
            var sourceDir = PathGuard.GetRealPath(request.Project.SourceDirectory);
            // SwiftPM scratch space lives in HCG's per-build folder, never inside the imported source.
            var swiftScratch = Path.Combine(PathGuard.GetRealPath(buildDir), "swiftpm");
            // electron-builder writes into HCG's per-build folder (never into the imported source's release/dist folder).
            var electronOut = Path.Combine(PathGuard.GetRealPath(buildDir), "electron-out");
            var steps = isElectron
                ? ElectronBuildPlanner.Plan(new ElectronBuildPlanner.PlanInput(settings, sourceDir, node!, electronOut, ElectronBuildPlanner.Environment(scrub.Environment, node!)))
                : isSwift
                ? SwiftBuildPlanner.Plan(new SwiftBuildPlanner.PlanInput(settings, sourceDir, swiftTool!.ExecutablePath!, swiftScratch, scrub.Environment))
                : DotNetBuildPlanner.Plan(new DotNetBuildPlanner.PlanInput(
                    settings, sourceDir, sdk!.ExecutablePath!, publishDir, host.Platform,
                    icons?.IcoPath, scrub.Environment));

            // Shared across steps: MSBuild repeats diagnostics in build and publish; swift build prints them per step.
            IBuildOutputParser parser = isElectron ? new ElectronOutputParser() : isSwift ? new SwiftOutputParser() : new MsBuildOutputParser();
            var captured = new List<string>();
            foreach (var step in steps)
            {
                overall.Token.ThrowIfCancellationRequested();
                observer.OnStage(step.Stage, step.Title);
                var missingInput = step.RequiredFiles.FirstOrDefault(r => !File.Exists(r.Path));
                if (missingInput.Path is not null)
                {
                    Fail(BuildStatus.Failed, missingInput.Message);
                    return build;
                }
                ProcessResult result = null!;
                for (var attempt = 1; attempt <= 1 + MaxTransientRetries; attempt++)
                {
                    var attemptParser = parser.Clone();
                    var transientLock = false;
                    int messagesBefore, warningsBefore, errorsBefore;
                    lock (build.Messages) (messagesBefore, warningsBefore, errorsBefore) = (build.Messages.Count, build.WarningCount, build.ErrorCount);
                    Log(BuildLogKind.Command, step.Process.DisplayCommand);
                    result = await worker.RunAsync(step.Process, (stream, text) =>
                    {
                        if (step.CaptureStdout && stream == OutputStream.StdOut) lock (captured) captured.Add(text);
                        if (IsTransientFileLock(text)) transientLock = true;
                        var message = attemptParser.Parse(text);
                        Log(message?.Severity switch
                        {
                            DiagnosticSeverity.Error => BuildLogKind.Error,
                            DiagnosticSeverity.Warning => BuildLogKind.Warning,
                            _ => stream == OutputStream.StdErr && !step.StdErrIsProgress ? BuildLogKind.Error : BuildLogKind.Output,
                        }, text);
                        if (message is not null) AddMessage(message);
                    }, overall.Token).ConfigureAwait(false);

                    var retryable = result.ExitCode != 0 && transientLock && !result.Cancelled && !result.TimedOut && !overall.IsCancellationRequested;
                    if (!retryable || attempt > MaxTransientRetries)
                    {
                        parser = attemptParser; // keep this attempt's de-duplication state
                        break;
                    }
                    // Discard diagnostics from the failed attempt so a successful retry is not reported with phantom errors.
                    lock (build.Messages)
                    {
                        build.Messages.RemoveRange(messagesBefore, build.Messages.Count - messagesBefore);
                        (build.WarningCount, build.ErrorCount) = (warningsBefore, errorsBefore);
                    }
                    Log(BuildLogKind.Warning, $"{step.Title} failed because a build output file was briefly locked by another process. Retrying once.");
                    await Task.Delay(TransientRetryDelay, overall.Token).ConfigureAwait(false);
                }

                build.ExitCode = result.ExitCode;
                if (result.Cancelled || result.TimedOut || overall.IsCancellationRequested)
                    overall.Token.ThrowIfCancellationRequested();
                if (result.ExitCode != 0)
                {
                    Fail(BuildStatus.Failed, $"{step.Title} failed with exit code {result.ExitCode}.");
                    return build;
                }
                Log(BuildLogKind.Success, $"{step.Title} completed in {result.Duration.TotalSeconds:F1}s");
            }

            observer.OnStage(BuildStage.VerifyingOutput, "Verifying publish output");
            string? exeName;
            var packagingInput = publishDir;
            if (isElectron)
            {
                var (relative, problem) = ElectronBuildPlanner.Discover(electronOut, settings, settings.Electron!.ProductName is { Length: > 0 } pn ? pn : nodeManifest!.ProductName ?? settings.Identity.ApplicationName);
                if (relative is null)
                {
                    Fail(BuildStatus.Failed, problem!);
                    return build;
                }
                exeName = relative;
                packagingInput = electronOut;
                foreach (var pkg in new[] { "electron", "electron-builder" })
                    if (ElectronBuildPlanner.InstalledVersion(electronDir!, pkg) is { } v) build.ToolchainVersions[pkg] = v;
                Log(BuildLogKind.Info, $"electron-builder output: {electronOut}");
                foreach (var entry in Directory.EnumerateFileSystemEntries(electronOut).Order(StringComparer.Ordinal))
                    Log(BuildLogKind.Info, $"  {Path.GetFileName(entry)}{(Directory.Exists(entry) ? "/" : "")}");
            }
            else if (isSwift)
            {
                var binPath = captured.Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('/'));
                if (binPath is null || !Directory.Exists(binPath))
                {
                    Fail(BuildStatus.Failed, "swift build reported success, but did not report an existing bin path (--show-bin-path).");
                    return build;
                }
                Log(BuildLogKind.Info, $"SwiftPM products: {binPath}");
                var (stagedExe, bundles) = SwiftBuildPlanner.Stage(binPath, settings.Swift!.Product, publishDir);
                exeName = stagedExe;
                if (bundles.Count > 0) Log(BuildLogKind.Info, $"SwiftPM resource bundles: {string.Join(", ", bundles)}");
                Log(BuildLogKind.Info, "Tests were not run: HCG builds the selected product only (test targets are neither compiled nor run).");
                try { Directory.Delete(swiftScratch, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(BuildLogKind.Warning, $"Could not remove SwiftPM scratch space {swiftScratch}: {ex.Message}"); }
            }
            else
            {
                exeName = LocateExecutable(publishDir, request.AssemblyName, settings.Platform);
            }
            if (exeName is null)
            {
                Fail(BuildStatus.Failed, $"The build reported success, but no {settings.Platform.DisplayName()} executable was found in the publish output ({publishDir}).");
                return build;
            }
            Log(BuildLogKind.Success, $"Found application executable: {exeName}");

            observer.OnStage(BuildStage.Packaging, "Packaging artifacts");
            var baseName = ArtifactBaseName(settings);
            var outputRoot = PrepareOutputRoot(settings.OutputDirectory, baseName, request.AllowOverwriteOutput);
            createdOutputRoot = outputRoot;
            // Claim the folder immediately so an interrupted build can be cleaned up or replaced later.
            await File.WriteAllTextAsync(Path.Combine(outputRoot, OutputMarkerFile), HcgJson.Serialize(new { buildId = build.Id, complete = false }), overall.Token).ConfigureAwait(false);
            Log(BuildLogKind.Info, $"Output folder: {outputRoot}");

            var packagingRequest = new PackagingRequest
            {
                Settings = settings,
                PublishDirectory = packagingInput,
                ExecutableFileName = exeName,
                OutputRoot = outputRoot,
                ArtifactBaseName = baseName,
                Icons = icons,
                Host = host,
                Toolchains = report,
            };
            var packagingLog = new PackagingLog(Log);
            var outputs = new List<PackagedOutput>();
            foreach (var format in PackagingCatalog.WithDependencies(settings.PackagingFormats, settings))
            {
                var packager = packagers.Get(format, settings) ?? throw new InvalidOperationException($"{format.DisplayName()} is not implemented in this version.");
                Log(BuildLogKind.Info, $"Packaging: {format.DisplayName()}");
                outputs.AddRange(await packager.PackageAsync(packagingRequest, packagingLog, overall.Token).ConfigureAwait(false));
            }

            observer.OnStage(BuildStage.Checksumming, "Computing SHA-256 checksums");
            var sums = new StringBuilder();
            foreach (var output in outputs)
            {
                string sha;
                long size;
                if (output.Kind == ArtifactKind.Directory)
                {
                    var (digest, manifest, total) = await Checksums.Sha256DirectoryAsync(output.Path, overall.Token).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(outputRoot, Path.GetFileName(output.Path) + ".manifest.sha256.txt"), manifest, overall.Token).ConfigureAwait(false);
                    (sha, size) = (digest, total);
                }
                else
                {
                    sha = await Checksums.Sha256FileAsync(output.Path, overall.Token).ConfigureAwait(false);
                    size = new FileInfo(output.Path).Length;
                    sums.Append(sha).Append("  ").Append(Path.GetFileName(output.Path)).Append('\n');
                }
                var status = Packaging.Verification.Checks.Combine(output.Checks);
                build.Artifacts.Add(new ArtifactRecord
                {
                    Id = Guid.NewGuid(),
                    BuildId = build.Id,
                    Format = output.Format,
                    Kind = output.Kind,
                    Path = output.Path,
                    SizeBytes = size,
                    Sha256 = sha,
                    Verification = status,
                    Checks = output.Checks,
                    CreatedAt = time.GetUtcNow(),
                });
                Log(status == VerificationStatus.Failed ? BuildLogKind.Error : BuildLogKind.Success,
                    $"{Path.GetFileName(output.Path)} — {size:N0} bytes — SHA-256 {sha} — verification {status}");
                foreach (var check in output.Checks.Where(c => c.Status != VerificationStatus.Passed))
                    Log(check.Status == VerificationStatus.Failed ? BuildLogKind.Error : BuildLogKind.Warning, $"  {check.Name}: {check.Detail}");
            }
            if (isElectron)
            {
                // The verified copies are in the output folder; electron-builder's staging copy is not kept.
                try { Directory.Delete(electronOut, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(BuildLogKind.Warning, $"Could not remove {electronOut}: {ex.Message}"); }
            }
            if (sums.Length > 0)
                await File.WriteAllTextAsync(Path.Combine(outputRoot, "SHA256SUMS.txt"), sums.ToString(), overall.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, OutputMarkerFile), HcgJson.Serialize(new
            {
                buildId = build.Id,
                project = request.Project.Name,
                settings.Identity.ApplicationName,
                settings.Identity.Version,
                runtimeIdentifier = settings.RuntimeIdentifier,
                createdAt = time.GetUtcNow(),
                complete = true,
                signing = settings.Platform == TargetPlatform.MacOS ? settings.MacDistribution.SigningMode.ToString() : "none",
                notarized = false,
            }), overall.Token).ConfigureAwait(false);

            if (build.Artifacts.Any(a => a.Verification == VerificationStatus.Failed))
            {
                // Never present output that failed verification: the reasons are in the log above; the files are removed.
                var failed = build.Artifacts.Where(a => a.Verification == VerificationStatus.Failed).Select(a => Path.GetFileName(a.Path)).ToList();
                foreach (var a in build.Artifacts.Where(a => a.Verification == VerificationStatus.Failed))
                    foreach (var c in a.Checks.Where(c => c.Status == VerificationStatus.Failed))
                        AddMessage(new BuildMessage(DiagnosticSeverity.Error, $"{Path.GetFileName(a.Path)} — {c.Name}: {c.Detail}", "HCG-VERIFY"));
                build.Artifacts.Clear();
                Fail(BuildStatus.Failed, $"Verification failed for {string.Join(", ", failed)} (see the checks in the log). The output was removed so no unverified artifact is offered.");
                return build;
            }
            build.Status = BuildStatus.Succeeded;
            Log(BuildLogKind.Success, $"Build succeeded: {build.Artifacts.Count} artifact(s), {build.WarningCount} warning(s).");
            return build;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Fail(BuildStatus.Cancelled, "Build cancelled by user. Running processes were terminated.");
            return build;
        }
        catch (OperationCanceledException) when (overall.IsCancellationRequested)
        {
            Fail(BuildStatus.TimedOut, $"Build exceeded the time limit of {settings.Timeout.TotalMinutes:F0} minutes and was terminated.");
            return build;
        }
        catch (BuildValidationException ex)
        {
            Fail(BuildStatus.Failed, ex.Message);
            return build;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build {BuildId} failed unexpectedly", build.Id);
            Fail(BuildStatus.Failed, $"Unexpected error: {ex.Message}");
            return build;
        }
        finally
        {
            stopwatch.Stop();
            if (build.Status != BuildStatus.Succeeded && build.Artifacts.Count == 0 && createdOutputRoot is not null)
                RemoveIncompleteOutput(createdOutputRoot, Log);
            build.EndedAt = time.GetUtcNow();
            Log(BuildLogKind.Info, $"Finished with status {build.Status} in {stopwatch.Elapsed:hh\\:mm\\:ss}.");
            await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
            observer.OnStage(BuildStage.Completed, build.Status.ToString());
        }
    }

    /// <summary>Retries for a build step that failed only because an output file was transiently locked.</summary>
    internal const int MaxTransientRetries = 1;
    internal static TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Recognizes the IOException MSBuild tasks report when a just-written output file is momentarily locked
    /// (observed on macOS with Avalonia's XAML compiler). Genuine compile errors never match.
    /// </summary>
    internal static bool IsTransientFileLock(string line) =>
        line.Contains("because it is being used by another process", StringComparison.Ordinal) &&
        (line.Contains("error MSB4018", StringComparison.Ordinal) || line.Contains("IOException", StringComparison.Ordinal));

    /// <summary>Base name shared by the per-build output folder and its artifacts, e.g. <c>MyApp-1.2.0-osx-arm64</c>.</summary>
    public static string ArtifactBaseName(BuildSettings s) =>
        $"{PathGuard.SanitizeFileName(s.Identity.ApplicationName, "App")}-{PathGuard.SanitizeFileName(s.Identity.Version, "0")}-{s.RuntimeIdentifier ?? (s.Platform == TargetPlatform.MacOS ? "osx" : s.Platform.ToString().ToLowerInvariant()) + "-" + s.Architecture.ToString().ToLowerInvariant()}";

    /// <summary>The per-build folder created inside the chosen output directory.</summary>
    public static string OutputRootFor(BuildSettings s) => Path.Combine(Path.GetFullPath(s.OutputDirectory), ArtifactBaseName(s));

    /// <summary>Finds the native app host in publish output: assembly name first, then the runtimeconfig.json stem.</summary>
    internal static string? LocateExecutable(string publishDir, string? assemblyName, TargetPlatform platform)
    {
        if (!Directory.Exists(publishDir)) return null;
        var ext = platform == TargetPlatform.Windows ? ".exe" : "";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(assemblyName)) candidates.Add(assemblyName + ext);
        candidates.AddRange(Directory.EnumerateFiles(publishDir, "*.runtimeconfig.json")
            .Select(f => Path.GetFileName(f)[..^".runtimeconfig.json".Length] + ext));
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(publishDir, c)));
    }

    /// <summary>
    /// Creates the per-build output folder. An existing folder is replaced only when allowed AND it carries HCG's
    /// marker file — folders HCG did not create are never deleted.
    /// </summary>
    internal static string PrepareOutputRoot(string outputDirectory, string baseName, bool allowOverwrite)
    {
        var root = Path.Combine(Path.GetFullPath(outputDirectory), baseName);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (!allowOverwrite)
                throw new BuildValidationException($"The output folder '{root}' already exists. Confirm replacement or choose another output directory.");
            if (!File.Exists(Path.Combine(root, OutputMarkerFile)))
                throw new BuildValidationException($"'{root}' exists and was not created by HCG Executable Maker, so it will not be replaced. Choose another output directory.");
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Deletes an output folder this build created but never completed (cancelled, timed out, or crashed).</summary>
    private void RemoveIncompleteOutput(string outputRoot, Action<BuildLogKind, string> log)
    {
        try
        {
            if (Directory.Exists(outputRoot) && File.Exists(Path.Combine(outputRoot, OutputMarkerFile)))
            {
                Directory.Delete(outputRoot, recursive: true);
                log(BuildLogKind.Info, $"Removed incomplete output folder {outputRoot}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove incomplete output {Path}", outputRoot);
            log(BuildLogKind.Warning, $"Incomplete output could not be removed: {outputRoot} ({ex.Message})");
        }
    }

    private sealed class PackagingLog(Action<BuildLogKind, string> log) : IPackagingLog
    {
        public void Info(string message) => log(BuildLogKind.Info, message);
        public void Warning(string message) => log(BuildLogKind.Warning, message);
    }
}
