#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
#:project ../../src/HCG.ExecutableMaker.Infrastructure/HCG.ExecutableMaker.Infrastructure.csproj
#:project ../../src/HCG.ExecutableMaker.ProjectInspection/HCG.ExecutableMaker.ProjectInspection.csproj
#:project ../../src/HCG.ExecutableMaker.BuildEngine/HCG.ExecutableMaker.BuildEngine.csproj
// Mobile Build Studio from the command line: the SAME engine the app uses (import → static inspection → toolchain
// build → verification → checksums → SQLite history). Used by CI to prove real Android/iOS builds, and handy locally.
//
// Usage:
//   dotnet run --no-cache tools/mobile-build/mobile-build.cs -- <project-dir|artifact> <output-dir> --output <kind>
//        [--signing project|unsigned|debug-key|automatic|manual] [--team ABCDE12345] [--profile NAME]
//        [--export app-store-connect|release-testing|enterprise|debugging] [--variant release] [--module :app]
//        [--apks universal|device --device-spec spec.json] [--timeout-minutes 30] [--trust] [--expect-failure "text"]
//   dotnet run --no-cache tools/mobile-build/mobile-build.cs -- <project-dir|artifact> <output-dir> --inspect-only
//   dotnet run --no-cache tools/mobile-build/mobile-build.cs -- <mac-project-dir> <output-dir> --port-mac App.xcodeproj
//        [--from-app App.app] [--output ios-simulator --trust]
//   --port-mac runs Mobile Build Studio's "Port a Mac app to iOS" path (import copy + MacProjectPort retargeting);
//   --from-app first reads the built Mac app's bundle identifier (like "From Mac app…") and requires the source to match.
//   kinds: android-debug-apk | android-release-apk | android-aab | aab-to-apk | ios-simulator | ios-archive | ios-ipa
//
// --trust is REQUIRED: building executes the project's build scripts (Gradle wrapper, Xcode run phases).
// --expect-failure turns a build that fails with the given message into exit code 0 (used by CI to prove that HCG
// refuses, e.g., IPA export of an unsigned archive). Exit code is non-zero otherwise when the build fails.
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using Microsoft.Extensions.Logging.Abstractions;

var portMac = Option("--port-mac");
var inspectOnly = args.Contains("--inspect-only") || (portMac is not null && Option("--output") is null);
if (args.Length < 2 || (Option("--output") is null && !inspectOnly))
{
    Console.Error.WriteLine("Usage: mobile-build.cs <project-dir|artifact> <output-dir> --output <kind> [--trust] [options]  (see header)");
    return 2;
}
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!args.Contains("--trust") && !inspectOnly)
{
    Console.Error.WriteLine("Refusing to build without --trust: building runs the project's own build scripts with your permissions.");
    return 2;
}
var kind = Option("--output") ?? "";
var expectFailure = Option("--expect-failure");

// A throw-away data folder unless HCG_EXECUTABLE_MAKER_HOME points at a real one (then imports appear in the app).
var paths = string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.RootOverrideVariable))
    ? new AppPaths(Path.Combine(Path.GetTempPath(), "hcg-mobile-" + Guid.NewGuid().ToString("N")[..8]))
    : AppPaths.CreateDefault();
paths.EnsureCreated();
var db = new SqliteDatabase(paths);
var runner = new ProcessRunner();
var toolchains = new ToolchainService(new ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System));
IProjectInspector[] inspectors =
[
    new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System), new MobileArtifactProjectInspector(TimeProvider.System),
    .. EcosystemSignatureInspector.CreateDefaults(TimeProvider.System),
];
var projects = new ProjectService(new ProjectImporter(paths, TimeProvider.System), new ProjectInspectionService(inspectors, TimeProvider.System),
    new SqliteProjectRepository(db), new SqliteSettingsStore(db), toolchains, paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
var buildRepo = new SqliteBuildRepository(db);
var service = new MobileBuildService(new LocalProcessBuildWorker(runner), toolchains, buildRepo, new MobileArtifactVerifier(runner), paths, TimeProvider.System);

var report = await toolchains.GetAsync();
Console.WriteLine($"Host: {report.Host.DisplayName}");
foreach (var t in report.Toolchains.Where(t => t.Category is "Android" or "Apple mobile" or "Flutter" or ".NET MAUI" || t.Id is ToolchainIds.Xcode))
    Console.WriteLine($"  {t.DisplayName,-28} {t.Status,-18} {t.Version} {t.ExecutablePath}");

ImportResult import;
if (portMac is not null)
{
    string? bundleId = null;
    if (Option("--from-app") is { } app)
    {
        var plist = Path.Combine(Path.GetFullPath(app), "Contents", "Info.plist");
        bundleId = HCG.ExecutableMaker.Packaging.Mobile.PlistReader.ParseDictionary(File.ReadAllBytes(plist)).GetValueOrDefault("CFBundleIdentifier") as string
                   ?? throw new InvalidDataException("The Mac app has no CFBundleIdentifier.");
        Console.WriteLine($"Mac app {Path.GetFileName(app)}: bundle identifier {bundleId} — source required, porting {portMac}");
    }
    MacProjectPort.Result? port = null;
    import = await projects.ImportDirectoryAsync(input, null, CancellationToken.None,
        copy => port = MacProjectPort.PrepareCopy(copy, portMac, bundleId));
    Console.WriteLine($"Ported project: {port!.ProjectFile}");
    Console.WriteLine($"Ported source: {import.Project.SourceDirectory}");
    Console.WriteLine(port.ReviewFiles.Count == 0 ? "Review: (no files flagged)" : "Review: " + string.Join(", ", port.ReviewFiles));
}
else
{
    import = File.Exists(input)
        ? await projects.ImportArtifactAsync(input, null, CancellationToken.None)
        : await projects.ImportDirectoryAsync(input, null, CancellationToken.None);
}
var analysis = import.Project.Analysis!;
Console.WriteLine($"Imported {import.FileCount} file(s): {analysis.ApplicationType} — {analysis.Support} — {analysis.SupportSummary}");
foreach (var d in analysis.Diagnostics) Console.WriteLine($"  [{d.Severity}] {d.Message}");
if (analysis.Mobile is not { } mobile)
{
    Console.Error.WriteLine("Not a mobile project.");
    return 1;
}
foreach (var o in MobileCapabilities.Evaluate(mobile, report))
    Console.WriteLine($"  {(o.IsAvailable ? "✓" : "✗")} {o.DisplayName}{(o.Reason is null ? "" : " — " + o.Reason)}");
if (inspectOnly) return 0;

var outputKind = kind switch
{
    "android-debug-apk" => MobileOutput.AndroidDebugApk,
    "android-release-apk" => MobileOutput.AndroidReleaseApk,
    "android-aab" => MobileOutput.AndroidAppBundle,
    "aab-to-apk" => MobileOutput.AndroidAabToApk,
    "ios-simulator" => MobileOutput.IosSimulatorApp,
    "ios-archive" => MobileOutput.IosArchive,
    "ios-ipa" => MobileOutput.IosIpa,
    _ => throw new ArgumentException("Unknown --output " + kind),
};
var options = new MobileBuildOptions
{
    Framework = mobile.Framework,
    Output = outputKind,
    Module = Option("--module"),
    Variant = Option("--variant"),
    Signing = Option("--signing") switch
    {
        "unsigned" => MobileSigningMode.Unsigned,
        "debug-key" => MobileSigningMode.DebugKeystore,
        "automatic" => MobileSigningMode.AppleAutomatic,
        "manual" => MobileSigningMode.AppleManual,
        _ => MobileSigningMode.ProjectDefault,
    },
    DevelopmentTeam = Option("--team"),
    ProvisioningProfile = Option("--profile"),
    ExportMethod = Option("--export") switch
    {
        "app-store-connect" => IosExportMethod.AppStoreConnect,
        "release-testing" => IosExportMethod.ReleaseTesting,
        "enterprise" => IosExportMethod.Enterprise,
        "debugging" => IosExportMethod.Debugging,
        null => outputKind == MobileOutput.IosIpa ? IosExportMethod.Debugging : null,
        var other => throw new ArgumentException("Unknown --export " + other),
    },
    ApkSetMode = Option("--apks") == "device" ? ApkSetMode.DeviceTargeted : ApkSetMode.Universal,
    DeviceSpecPath = Option("--device-spec") is { } spec ? Path.GetFullPath(spec) : null,
    InputArtifact = mobile.ArtifactPath,
};
var identityName = mobile.ApplicationIds.FirstOrDefault() ?? analysis.ProjectName;
var build = await service.RunAsync(new MobileBuildRequest
{
    Project = import.Project, TrustAcknowledged = true, AllowOverwriteOutput = true,
    Settings = new BuildSettings
    {
        ProjectRelativePath = mobile.Android?.Root ?? mobile.Ios?.ProjectPath ?? mobile.Flutter?.Root ?? mobile.Maui?.ProjectPath ?? mobile.ArtifactPath ?? "",
        Platform = outputKind.Platform(), Architecture = TargetArchitecture.Universal, OutputDirectory = output,
        Timeout = TimeSpan.FromMinutes(int.TryParse(Option("--timeout-minutes"), out var m) ? m : 45),
        Identity = new AppIdentity { ApplicationName = analysis.ProjectName, Version = "ci", ApplicationId = identityName },
        Mobile = options,
    },
}, new ConsoleObserver(), CancellationToken.None);

Console.WriteLine($"Result: {build.Status} {build.FailureReason} in {build.Duration}");
foreach (var a in build.Artifacts)
{
    Console.WriteLine($"  {a.Path}  {a.SizeBytes:N0} bytes  sha256={a.Sha256}  {a.Verification}");
    foreach (var c in a.Checks) Console.WriteLine($"     - {c.Name}: {c.Status} — {c.Detail}");
}
var saved = await buildRepo.GetAsync(build.Id);
Console.WriteLine($"History: build {build.Id} persisted as {saved?.Status} with {saved?.Artifacts.Count} artifact(s).");
if (expectFailure is not null)
{
    if (build.Status == BuildStatus.Failed && build.FailureReason?.Contains(expectFailure, StringComparison.Ordinal) == true)
    {
        Console.WriteLine($"Expected refusal confirmed: \"{expectFailure}\"");
        return 0;
    }
    Console.Error.WriteLine($"Expected a failure containing \"{expectFailure}\", got {build.Status}: {build.FailureReason}");
    return 1;
}
return build.Status == BuildStatus.Succeeded ? 0 : 1;

sealed class ConsoleObserver : IBuildObserver
{
    public void OnStage(BuildStage stage, string description) => Console.WriteLine($"   [{stage}] {description}");
    public void OnLog(BuildLogLine line) => Console.WriteLine($"   {(line.Kind is BuildLogKind.Output ? "" : line.Kind.ToString().ToUpperInvariant() + ": ")}{line.Text}");
}
