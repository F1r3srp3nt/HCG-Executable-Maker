using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

public sealed record MobileBuildRequest
{
    public required ProjectRecord Project { get; init; }
    /// <summary>Settings with <see cref="BuildSettings.Mobile"/> set.</summary>
    public required BuildSettings Settings { get; init; }
    /// <summary>The user explicitly confirmed they trust the project (Gradle wrapper, build scripts, Xcode run phases).</summary>
    public required bool TrustAcknowledged { get; init; }
    public bool AllowOverwriteOutput { get; init; }
}

/// <summary>
/// Mobile Build Studio's engine: validate → (debug keystore) → toolchain build steps → locate products →
/// (archive inspection → export | bundletool) → copy to output → verify → checksum → persist.
/// A build succeeds only when every command exits 0, at least one fresh product is found, and no verification fails.
/// </summary>
public sealed class MobileBuildService(
    IBuildWorker worker,
    IToolchainService toolchains,
    IBuildRepository builds,
    MobileArtifactVerifier verifier,
    IAppPaths paths,
    TimeProvider time,
    ILogger<MobileBuildService>? logger = null,
    HCG.ExecutableMaker.BuildEngine.Signing.SigningPrebuildCheck? signingCheck = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<MobileBuildService>.Instance;

    /// <summary>Folder suffix per output, e.g. <c>Hello-1.2.0-android-debug</c>.</summary>
    public static string Suffix(MobileOutput o) => o switch
    {
        MobileOutput.AndroidDebugApk => "android-debug",
        MobileOutput.AndroidReleaseApk => "android-release",
        MobileOutput.AndroidAppBundle => "android-aab",
        MobileOutput.AndroidAabToApk => "android-apks",
        MobileOutput.IosSimulatorApp => "ios-simulator",
        MobileOutput.IosArchive => "ios-archive",
        _ => "ios-ipa",
    };

    public static string OutputRootFor(BuildSettings s) =>
        Path.Combine(Path.GetFullPath(s.OutputDirectory), $"{PathGuard.SanitizeFileName(s.Identity.ApplicationName, "App")}-{PathGuard.SanitizeFileName(s.Identity.Version, "0")}-{Suffix(s.Mobile!.Output)}");

    /// <summary>
    /// A mobile build (IPA, Xcode archive, APK/AAB…) never packages a Theos .deb, even when the folder also holds a
    /// Theos project: that is the “Jailbreak package (.deb, Theos)” route of New Build.
    /// </summary>
    public static void ValidateRoute(BuildSettings settings)
    {
        if (settings.Native is not null || settings.PackagingFormats.Contains(PackagingFormat.JailbreakDeb))
            throw new BuildValidationException("Mobile Build Studio builds the app project (IPA, archive, APK or AAB); it never packages a .deb. Build the Theos package on the “Jailbreak package (.deb, Theos)” route in New Build.");
    }

    public async Task<BuildRecord> RunAsync(MobileBuildRequest request, IBuildObserver observer, CancellationToken cancellationToken)
    {
        if (!request.TrustAcknowledged)
            throw new InvalidOperationException("Build refused: the user has not acknowledged that this project is trusted. " + TrustPolicy.TrustBoundaryExplanation);
        var settings = request.Settings;
        var options = settings.Mobile ?? throw new ArgumentException("Mobile build settings are required.", nameof(request));
        ValidateRoute(settings);

        var build = new BuildRecord
        {
            Id = Guid.NewGuid(), ProjectId = request.Project.Id, ProjectName = request.Project.Name, Settings = settings,
            Status = BuildStatus.Running, StartedAt = time.GetUtcNow(), TrustAcknowledged = true,
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

        string? outputRoot = null;
        var cleanup = new List<string>();
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(settings.Timeout);
        var stopwatch = Stopwatch.StartNew();
        var parser = new MobileOutputParser();

        async Task RunStepsAsync(MobilePlan plan)
        {
            foreach (var note in plan.Notes) Log(BuildLogKind.Warning, note);
            foreach (var step in plan.Steps)
            {
                overall.Token.ThrowIfCancellationRequested();
                observer.OnStage(step.Stage, step.Title);
                Log(BuildLogKind.Command, step.DisplayCommand);
                int errorsBefore;
                lock (build.Messages) errorsBefore = build.Messages.Count;
                var result = await worker.RunAsync(step.Process, (stream, text) =>
                {
                    var message = parser.Parse(text);
                    Log(message?.Severity switch
                    {
                        DiagnosticSeverity.Error => BuildLogKind.Error,
                        DiagnosticSeverity.Warning => BuildLogKind.Warning,
                        _ => BuildLogKind.Output,
                    }, text);
                    if (message is not null) AddMessage(message);
                }, overall.Token).ConfigureAwait(false);
                build.ExitCode = result.ExitCode;
                if (result.Cancelled || result.TimedOut || overall.IsCancellationRequested) overall.Token.ThrowIfCancellationRequested();
                if (result.ExitCode != 0)
                {
                    // The first actionable error of this step, in full (xcodebuild's exit code 65 alone says only "failed").
                    BuildMessage? first;
                    lock (build.Messages) first = build.Messages.Skip(errorsBefore).FirstOrDefault(m => m.Severity == DiagnosticSeverity.Error && !m.Text.Contains("** ", StringComparison.Ordinal) && !m.Text.StartsWith("FAILURE:", StringComparison.Ordinal))
                                                  ?? build.Messages.Skip(errorsBefore).FirstOrDefault(m => m.Severity == DiagnosticSeverity.Error);
                    var bundle = step.Process.Arguments.SkipWhile(a => a != "-resultBundlePath").Skip(1).FirstOrDefault();
                    throw new StepFailedException($"{step.Title} failed with exit code {result.ExitCode}." +
                        (first is null ? " No error line was recognized; see the complete log." : $"\nFirst error: {(first.File is null ? "" : first.File + (first.Line is { } ln ? $":{ln}" : "") + ": ")}{parser.Describe(first)}") +
                        $"\nComplete log: {build.LogPath}" + (bundle is not null && Directory.Exists(bundle) ? $"\nResult bundle: {bundle}" : ""));
                }
                Log(BuildLogKind.Success, $"{step.Title} completed in {result.Duration.TotalSeconds:F1}s");
            }
        }

        try
        {
            observer.OnStage(BuildStage.Preparing, "Validating configuration and toolchains");
            Log(BuildLogKind.Info, $"HCG Executable Maker {HostInfo.ProductVersion} — Mobile Build Studio — build {build.Id}");
            Log(BuildLogKind.Info, $"Project: {request.Project.Name} · {options.Framework.DisplayName()} · {options.Output.DisplayName()}");
            Log(BuildLogKind.Warning, $"Isolation: {worker.Description}");

            var report = await toolchains.GetAsync(cancellationToken: overall.Token).ConfigureAwait(false);
            build.ToolchainVersions["host"] = $"{report.Host.DisplayName} ({report.Host.OsDescription})";
            foreach (var id in RelevantToolchains(options))
                if (report.Find(id) is { IsAvailable: true, Version: { } v }) build.ToolchainVersions[id] = v;

            var analysis = request.Project.Analysis?.Mobile;
            var artifactInput = options.InputArtifact is not null;
            if (!artifactInput)
            {
                if (analysis is null) throw new BuildValidationException("This project was not recognized as a mobile project. Re-inspect it or import a supported Android/iOS/Flutter/.NET MAUI project.");
                var option = MobileCapabilities.Evaluate(analysis, report).FirstOrDefault(x => x.Output == options.Output)
                             ?? throw new BuildValidationException($"{options.Output.DisplayName()} does not apply to this project.");
                if (!option.IsAvailable) throw new BuildValidationException($"{options.Output.DisplayName()} is not available: {option.Reason}");
                // Apple signing: validate what this Mac can check before xcodebuild starts (keychain, verified profiles,
                // team, bundle IDs, entitlements, devices). Checks that need Apple's services are logged as not verified.
                if (signingCheck is not null
                    && await signingCheck.CheckIosAsync(request.Project, options, analysis, PathGuard.GetRealPath(request.Project.SourceDirectory), report, overall.Token).ConfigureAwait(false) is { } signing)
                {
                    foreach (var (kind, text) in HCG.ExecutableMaker.BuildEngine.Signing.SigningPrebuildCheck.Describe(signing)) Log(kind, text);
                    if (signing.HasFailures)
                        throw new BuildValidationException("Signing is not ready: " + string.Join(" ", signing.Failures.Select(f =>
                            $"{f.Title}{(f.Target is { } t ? $" ({t})" : "")}: {f.Detail}{(f.Remedy is { } r ? " Fix: " + r : "")}")));
                }
            }
            else if (options.Output != MobileOutput.AndroidAabToApk)
                throw new BuildValidationException("Only AAB → APK can start from an existing artifact.");

            var scrub = worker.CreateEnvironment();
            if (scrub.RemovedNames.Count > 0)
                Log(BuildLogKind.Info, $"Removed {scrub.RemovedNames.Count} sensitive environment variable(s) from the build environment: {string.Join(", ", scrub.RemovedNames)}");
            var workDir = Path.Combine(PathGuard.GetRealPath(buildDir), "work");
            Directory.CreateDirectory(workDir);
            var sourceDir = PathGuard.GetRealPath(request.Project.SourceDirectory);
            var toolHome = Path.Combine(paths.Root, "toolhomes");
            Directory.CreateDirectory(toolHome);

            DebugKeystore? keystore = null;
            if (options.Signing == MobileSigningMode.DebugKeystore || options.Output == MobileOutput.AndroidAabToApk)
                keystore = await EnsureDebugKeystoreAsync(report, scrub.Environment, Log, overall.Token).ConfigureAwait(false);

            var input = new MobilePlanInput
            {
                Options = options, Analysis = analysis ?? new MobileAnalysis { Framework = options.Framework }, SourceDirectory = sourceDir,
                WorkDirectory = workDir, Toolchains = report, Environment = scrub.Environment, ToolHomeDirectory = toolHome, Keystore = keystore,
            };
            var startedUtc = time.GetUtcNow().UtcDateTime.AddSeconds(-2);
            var products = new List<(PackagingFormat Format, string Path)>();
            string? inputAab = null;

            if (artifactInput)
            {
                // The imported bundle is the input, not a product of this build: it is not copied or re-reported.
                inputAab = MobileArgs.Inside(sourceDir, options.InputArtifact!);
                if (!File.Exists(inputAab) || MobileArtifactInspector.KindOf(inputAab) != MobileArtifactKind.Aab) throw new BuildValidationException("The input artifact is not an .aab file.");
                var inputReport = MobileArtifactInspector.Inspect(inputAab, overall.Token);
                if (inputReport.Status == VerificationStatus.Failed)
                    throw new BuildValidationException("The input App Bundle is invalid: " + string.Join("; ", inputReport.Checks.Where(c => c.Status == VerificationStatus.Failed).Select(c => c.Detail)));
                Log(BuildLogKind.Info, $"Input bundle: {inputReport.PackageId} {inputReport.VersionName} ({inputReport.VersionCode}), modules {string.Join(", ", inputReport.Modules)}");
            }
            else
            {
                if (options.Framework is MobileFramework.NativeAndroid or MobileFramework.Capacitor && analysis!.Android is { } android &&
                    GradlePlanner.ReadWrapper(MobileArgs.Inside(sourceDir, android.Root)) is { } wrapper)
                {
                    Log(BuildLogKind.Info, $"Gradle wrapper: {wrapper.DistributionUrl ?? "(no distributionUrl)"} · wrapper jar SHA-256 {wrapper.JarSha256}");
                    foreach (var w in GradlePlanner.AssessWrapper(wrapper)) AddMessage(new BuildMessage(DiagnosticSeverity.Warning, w, "HCG-GRADLE"));
                }
                var plan = options.Framework switch
                {
                    MobileFramework.NativeAndroid => GradlePlanner.Plan(input),
                    MobileFramework.NativeIos => XcodePlanner.Plan(input),
                    MobileFramework.Flutter => FlutterPlanner.Plan(input),
                    MobileFramework.Capacitor => CapacitorPlanner.Plan(input),
                    MobileFramework.DotNetIos => DotNetIosPlanner.Plan(input),
                    _ => MauiPlanner.Plan(input),
                };
                await RunStepsAsync(plan).ConfigureAwait(false);

                if (options.Framework is MobileFramework.NativeIos or MobileFramework.DotNetIos && options.Output == MobileOutput.IosIpa)
                {
                    var archive = options.Framework == MobileFramework.NativeIos
                        ? XcodePlanner.ArchivePath(input, XcodePlanner.ResolveContainer(input).Scheme)
                        : Directory.Exists(DotNetIosPlanner.ArchiveDirectory(input))
                            ? Directory.EnumerateDirectories(DotNetIosPlanner.ArchiveDirectory(input), "*.xcarchive", SearchOption.AllDirectories).FirstOrDefault()
                              ?? throw new StepFailedException("dotnet publish succeeded but created no .xcarchive, so there is nothing to export.")
                            : throw new StepFailedException("dotnet publish succeeded but created no archive folder, so there is nothing to export.");
                    Log(BuildLogKind.Info, $"Archive: {archive}");
                    observer.OnStage(BuildStage.VerifyingOutput, "Inspecting the archive's signature");
                    var archiveReport = MobileArtifactInspector.Inspect(archive);
                    if (archiveReport.Status == VerificationStatus.Failed)
                        throw new StepFailedException("The archive is invalid: " + string.Join("; ", archiveReport.Checks.Where(c => c.Status == VerificationStatus.Failed).Select(c => c.Detail)));
                    if (archiveReport.Signing is MobileSigningState.Unsigned or MobileSigningState.AdHocSigned && archiveReport.Signer is null)
                        throw new StepFailedException("The archive is not signed with an Apple development or distribution certificate, so Xcode cannot export an IPA. " +
                                                      "Configure signing (Apple Developer team, certificate and provisioning profile) and archive again. HCG does not bypass code signing.");
                    Log(BuildLogKind.Success, $"Archive signed: {archiveReport.Signing} ({archiveReport.Signer})");
                    var export = XcodePlanner.Export(input, archive, archiveReport.PackageId);
                    cleanup.Add(Path.Combine(workDir, "ExportOptions.plist"));
                    await RunStepsAsync(export).ConfigureAwait(false);
                    products.AddRange(Locate(export.Outputs, startedUtc));
                }
                else products.AddRange(Locate(plan.Outputs, startedUtc));
            }

            if (options.Output == MobileOutput.AndroidAabToApk)
            {
                var aab = inputAab ?? products.Where(p => p.Format == PackagingFormat.AndroidAab).Select(p => p.Path).FirstOrDefault()
                          ?? throw new StepFailedException("No .aab was produced, so bundletool has nothing to convert.");
                var apks = Path.Combine(workDir, Path.GetFileNameWithoutExtension(aab) + ".apks");
                await RunStepsAsync(new MobilePlan([BundletoolPlanner.BuildApks(input, aab, apks)], [])
                {
                    Notes = ["bundletool generates APKs FROM the App Bundle (signed with HCG's local debug key, for testing). This is not a store-signed release."],
                }).ConfigureAwait(false);
                if (!File.Exists(apks)) throw new StepFailedException("bundletool reported success but produced no .apks file.");
                products.Add((PackagingFormat.AndroidApkSet, apks));
                if (options.ApkSetMode == ApkSetMode.Universal)
                    products.Add((PackagingFormat.AndroidApk, ExtractUniversalApk(apks, workDir)));
            }

            if (products.Count == 0)
                throw new StepFailedException("The build reported success, but no new artifact was found in the expected output location.");

            observer.OnStage(BuildStage.Packaging, "Collecting artifacts");
            outputRoot = BuildService.PrepareOutputRoot(Path.GetDirectoryName(OutputRootFor(settings))!, Path.GetFileName(OutputRootFor(settings)), request.AllowOverwriteOutput);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, BuildService.OutputMarkerFile), HcgJson.Serialize(new { buildId = build.Id, complete = false }), overall.Token).ConfigureAwait(false);
            Log(BuildLogKind.Info, $"Output folder: {outputRoot}");

            observer.OnStage(BuildStage.Checksumming, "Verifying artifacts and computing SHA-256 checksums");
            var sums = new StringBuilder();
            foreach (var (format, source) in products)
            {
                var destination = Path.Combine(outputRoot, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
                var isDir = Directory.Exists(source);
                if (isDir) CopyDirectory(source, destination); else File.Copy(source, destination, overwrite: true);
                var (artifactReport, checks) = await verifier.VerifyAsync(destination, report, overall.Token).ConfigureAwait(false);
                string sha;
                long size;
                if (isDir)
                {
                    var (digest, manifest, total) = await Checksums.Sha256DirectoryAsync(destination, overall.Token).ConfigureAwait(false);
                    await File.WriteAllTextAsync(destination + ".manifest.sha256.txt", manifest, overall.Token).ConfigureAwait(false);
                    (sha, size) = (digest, total);
                }
                else
                {
                    sha = await Checksums.Sha256FileAsync(destination, overall.Token).ConfigureAwait(false);
                    size = new FileInfo(destination).Length;
                    sums.Append(sha).Append("  ").Append(Path.GetFileName(destination)).Append('\n');
                }
                var status = Packaging.Verification.Checks.Combine(checks);
                build.Artifacts.Add(new ArtifactRecord
                {
                    Id = Guid.NewGuid(), BuildId = build.Id, Format = format, Kind = isDir ? ArtifactKind.Directory : ArtifactKind.File,
                    Path = destination, SizeBytes = size, Sha256 = sha, Verification = status, Checks = checks, CreatedAt = time.GetUtcNow(),
                });
                Log(status == VerificationStatus.Failed ? BuildLogKind.Error : BuildLogKind.Success,
                    $"{Path.GetFileName(destination)} — {size:N0} bytes — SHA-256 {sha} — verification {status}");
                foreach (var c in checks)
                    Log(c.Status switch { VerificationStatus.Failed => BuildLogKind.Error, VerificationStatus.Passed => BuildLogKind.Info, _ => BuildLogKind.Warning }, $"  {c.Name}: {c.Detail}");
                if (!artifactReport.IsDistributable)
                    Log(BuildLogKind.Warning, $"  {Path.GetFileName(destination)} is not a store-distributable artifact ({artifactReport.Signing}).");
            }
            if (sums.Length > 0) await File.WriteAllTextAsync(Path.Combine(outputRoot, "SHA256SUMS.txt"), sums.ToString(), overall.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, BuildService.OutputMarkerFile), HcgJson.Serialize(new
            {
                buildId = build.Id, project = request.Project.Name, settings.Identity.ApplicationName, settings.Identity.Version,
                framework = options.Framework.ToString(), output = options.Output.ToString(), signing = options.Signing.ToString(),
                createdAt = time.GetUtcNow(), complete = true, uploaded = false,
            }), overall.Token).ConfigureAwait(false);

            if (build.Artifacts.Any(a => a.Verification == VerificationStatus.Failed))
            {
                Fail(BuildStatus.Failed, "One or more artifacts failed verification.");
                return build;
            }
            build.Status = BuildStatus.Succeeded;
            Log(BuildLogKind.Success, $"Build succeeded: {build.Artifacts.Count} artifact(s), {build.WarningCount} warning(s). Nothing was uploaded or published.");
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
        catch (Exception ex) when (ex is BuildValidationException or StepFailedException or InvalidDataException)
        {
            Fail(BuildStatus.Failed, ex.Message);
            return build;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mobile build {BuildId} failed unexpectedly", build.Id);
            Fail(BuildStatus.Failed, $"Unexpected error: {ex.Message}");
            return build;
        }
        finally
        {
            stopwatch.Stop();
            foreach (var file in cleanup)
                try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (build.Status != BuildStatus.Succeeded && build.Artifacts.Count == 0 && outputRoot is not null &&
                File.Exists(Path.Combine(outputRoot, BuildService.OutputMarkerFile)))
                try { Directory.Delete(outputRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            build.EndedAt = time.GetUtcNow();
            Log(BuildLogKind.Info, $"Finished with status {build.Status} in {stopwatch.Elapsed:hh\\:mm\\:ss}.");
            await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
            observer.OnStage(BuildStage.Completed, build.Status.ToString());
        }
    }

    /// <summary>The "First error: …" line of a failure reason (or its first line), shown in full above the log.</summary>
    public static string? FirstErrorOf(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var line = reason.Split('\n').FirstOrDefault(l => l.StartsWith("First error: ", StringComparison.Ordinal));
        return line is null ? reason.Split('\n')[0] : line["First error: ".Length..];
    }

    private static IEnumerable<string> RelevantToolchains(MobileBuildOptions o) => o.Output.Platform() == TargetPlatform.Android
        ? [ToolchainIds.Jdk, ToolchainIds.AndroidSdk, ToolchainIds.AndroidBuildTools, ToolchainIds.Bundletool, ToolchainIds.Flutter, ToolchainIds.MauiWorkloads, ToolchainIds.DotNetSdk]
        : [ToolchainIds.Xcode, ToolchainIds.IosSdk, ToolchainIds.Flutter, ToolchainIds.MauiWorkloads, ToolchainIds.DotNetSdk];

    /// <summary>Products matching the plan's rules that were written during this build (stale files in the import are ignored).</summary>
    internal static List<(PackagingFormat, string)> Locate(IEnumerable<OutputRule> rules, DateTime sinceUtc)
    {
        var found = new List<(PackagingFormat, string)>();
        foreach (var rule in rules)
        {
            if (!Directory.Exists(rule.SearchRoot)) continue;
            var option = rule.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var candidates = rule.IsDirectory
                ? Directory.EnumerateDirectories(rule.SearchRoot, rule.Pattern, option).Where(d => Freshness(d, isDir: true) >= sinceUtc)
                : Directory.EnumerateFiles(rule.SearchRoot, rule.Pattern, option).Where(f => File.GetLastWriteTimeUtc(f) >= sinceUtc);
            found.AddRange(candidates.Order(StringComparer.Ordinal).Select(c => (rule.Format, c)));
        }
        return found;
    }

    private static DateTime Freshness(string dir, bool isDir)
    {
        var info = Path.Combine(dir, "Info.plist");
        return File.Exists(info) ? File.GetLastWriteTimeUtc(info) : Directory.GetLastWriteTimeUtc(dir);
    }

    /// <summary>Extracts universal.apk from an APK set (bounded read; the name is fixed, never taken from the archive).</summary>
    internal static string ExtractUniversalApk(string apks, string workDir)
    {
        using var zip = ZipFile.OpenRead(apks);
        var bytes = ZipIntegrity.ReadEntry(zip, "universal.apk", 2_000_000_000) ?? throw new StepFailedException("The APK set contains no universal.apk.");
        var target = Path.Combine(workDir, Path.GetFileNameWithoutExtension(apks) + "-universal.apk");
        File.WriteAllBytes(target, bytes);
        return target;
    }

    /// <summary>Copies a bundle directory, recreating symbolic links instead of following them.</summary>
    public static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo) Directory.CreateSymbolicLink(target, link); else File.CreateSymbolicLink(target, link);
            }
            else if (entry is DirectoryInfo d) CopyDirectory(d.FullName, target);
            else
            {
                File.Copy(entry.FullName, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName));
            }
        }
    }

    /// <summary>
    /// Creates HCG's local Android debug keystore once (JDK keytool, conventional debug alias/password). It lives in
    /// HCG's app data, never in a project or repository, and is never used for production signing.
    /// </summary>
    private async Task<DebugKeystore> EnsureDebugKeystoreAsync(ToolchainReport report, IReadOnlyDictionary<string, string> env, Action<BuildLogKind, string> log, CancellationToken ct)
    {
        var dir = Path.Combine(paths.Root, "signing");
        var path = Path.Combine(dir, "hcg-debug.keystore");
        if (File.Exists(path)) return new DebugKeystore(path);
        var java = MobileArgs.InspectedJdk(report)?.JavaExecutable ?? throw new BuildValidationException("A JDK is required to create the debug keystore. Choose one in Toolchains › Java.");
        var keytool = Path.Combine(Path.GetDirectoryName(PathGuard.GetRealPath(java))!, OperatingSystem.IsWindows() ? "keytool.exe" : "keytool");
        if (!File.Exists(keytool)) throw new BuildValidationException("keytool was not found next to java; a full JDK is required.");
        Directory.CreateDirectory(dir);
        var spec = new ProcessSpec
        {
            Executable = keytool, Environment = env, Timeout = TimeSpan.FromMinutes(2),
            Arguments = ["-genkeypair", "-keystore", path, "-storetype", "PKCS12", "-storepass", DebugKeystore.Password, "-keypass", DebugKeystore.Password,
                "-alias", DebugKeystore.Alias, "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000", "-dname", DebugKeystore.DistinguishedName, "-noprompt"],
        };
        log(BuildLogKind.Info, "Creating HCG's local Android debug keystore (test signing only): " + path);
        var result = await worker.RunAsync(spec, (_, line) => log(BuildLogKind.Output, line), ct).ConfigureAwait(false);
        if (!result.Succeeded || !File.Exists(path)) throw new StepFailedException($"keytool could not create the debug keystore (exit code {result.ExitCode}).");
        return new DebugKeystore(path);
    }

    private sealed class StepFailedException(string message) : Exception(message);
}
