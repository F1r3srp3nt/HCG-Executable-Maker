using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>
/// .NET for iOS applications that are not MAUI (HCG Mail's Avalonia.iOS head), built by the .NET iOS workload, which
/// drives Xcode's SDK, actool, ibtool and codesign. No .xcodeproj and no xcodebuild archive/export is involved. Each
/// output is its own route, with its own per-build folder under HCG's work directory, so only files this invocation
/// made can be found:
/// <list type="bullet">
/// <item>Simulator (Debug or Release): <c>dotnet build -r iossimulator-*</c> → work/simulator/*.app.</item>
/// <item>Signed device app (Debug or Release): <c>dotnet build -r ios-arm64</c> with CodesignKey/CodesignProvision → work/device/*.app.</item>
/// <item>XCArchive: <c>dotnet publish -r ios-arm64 -p:ArchiveOnBuild=true -p:ArchiveDir=work/archive/&lt;App&gt;.xcarchive</c>.
/// The SDK's Archive task uses ArchiveDir verbatim as the archive's root (Xamarin.MacDev.Tasks Archive.CreateArchiveDirectory),
/// so ArchiveDir must name the .xcarchive itself.</item>
/// <item>IPA: the SDK's own packaging, <c>-p:BuildIpa=true -p:IpaPackagePath=work/ipa/&lt;App&gt;.ipa</c> (CreateIpa: the signed
/// Payload/&lt;App&gt;.app zipped), together with the archive. The signing identity and profile decide whether it is a
/// development, ad hoc or App Store IPA; nothing re-signs or re-exports it.</item>
/// </list>
/// Device builds also set OutputPath to work/device/ (as <c>dotnet build -o</c> does), so the signed .app is kept even when
/// packaging fails afterwards.
/// The archive and the IPA are taken from those exact paths and checked (<see cref="InspectArchive"/>, <see cref="InspectIpa"/>),
/// never searched for. iOS workloads before 27.x ignore ArchiveDir and write to Xcode's Archives folder; the one archive
/// this build made there is found by bundle ID and time (<see cref="FindArchiveInXcodeFolder"/>) and copied to the exact path.
/// Project references are resolved from the repository root, because the whole repository was imported.
/// </summary>
public static partial class DotNetIosPlanner
{
    public static string SimulatorDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "simulator");
    public static string DeviceDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "device");
    public static string ArchiveDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "archives");
    public static string IpaDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "ipa");

    /// <summary>The configuration a route builds unless one is chosen: Debug for the Simulator and device apps, Release for archives and IPAs.</summary>
    public static string DefaultConfiguration(MobileOutput output) => output is MobileOutput.IosSimulatorApp or MobileOutput.IosDeviceApp ? "Debug" : "Release";

    /// <summary>What the chosen output needs and produces, in words (shown before the build).</summary>
    public static string Explain(MobileOutput output, IosExportMethod? method) => output switch
    {
        MobileOutput.IosSimulatorApp => "A Simulator .app: no certificate or profile; runs only in the iOS Simulator; not installable on a device, not distributable.",
        MobileOutput.IosDeviceApp => "A signed ios-arm64 .app for registered devices: an Apple Development certificate and a development profile that matches the bundle ID (a wildcard profile is fine when it allows the app's entitlements) and includes the device. No archive, no IPA.",
        MobileOutput.IosArchive => "An .xcarchive made by the .NET iOS SDK (ArchiveOnBuild): code signing is required (the SDK refuses an unsigned archive). It can be opened in Xcode's Organizer.",
        _ => method switch
        {
            IosExportMethod.AppStoreConnect => "An App Store IPA made by the .NET iOS SDK (BuildIpa): an Apple Distribution certificate and an App Store profile for exactly this bundle ID (not a wildcard). HCG never uploads it.",
            IosExportMethod.ReleaseTesting => "An ad hoc IPA made by the .NET iOS SDK (BuildIpa): an Apple Distribution certificate and an ad hoc profile for this bundle ID that lists the test devices.",
            IosExportMethod.Enterprise => "An in-house IPA made by the .NET iOS SDK (BuildIpa): an enterprise distribution certificate and an in-house profile.",
            _ => "A development IPA made by the .NET iOS SDK (BuildIpa): an Apple Development certificate and a development profile (a compatible wildcard profile is fine) that includes the devices. The archive is made too.",
        },
    };

    /// <summary>The exact <c>.xcarchive</c> the SDK writes (<c>-p:ArchiveDir=</c>): &lt;work&gt;/archives/&lt;name&gt;.xcarchive.</summary>
    public static string ArchivePath(MobilePlanInput input) => Path.Combine(ArchiveDirectory(input), ProductName(input) + ".xcarchive");

    /// <summary>The exact IPA the SDK writes (<c>-p:IpaPackagePath=</c>): &lt;work&gt;/ipa/&lt;name&gt;.ipa.</summary>
    public static string IpaPath(MobilePlanInput input) => Path.Combine(IpaDirectory(input), ProductName(input) + ".ipa");

    /// <summary>
    /// The product name the .NET iOS SDK gives the app: its AssemblyName (else the project file's name), e.g. HCGMail.iOS
    /// for HCGMail.iOS.app, so the archive and IPA are HCGMail.iOS.xcarchive and HCGMail.iOS.ipa, as HCG Mail's own
    /// export-ipa.sh names them. Reduced to characters that are safe in a path and an MSBuild property.
    /// </summary>
    public static string ProductName(MobilePlanInput input)
    {
        var p = input.Analysis.DotNetIos ?? throw new BuildValidationException("This is not a .NET for iOS project.");
        var raw = string.IsNullOrWhiteSpace(p.AssemblyName) || p.AssemblyName.Contains("$(", StringComparison.Ordinal)
            ? Path.GetFileNameWithoutExtension(p.ProjectPath) : p.AssemblyName;
        var safe = new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray()).Trim('.');
        return safe.Length == 0 ? "App" : safe;
    }

    /// <summary>
    /// Checks the archive the SDK wrote: the folder exists, has its Info.plist and exactly the app under
    /// Products/Applications. Returns the app's path, or the precise problem.
    /// </summary>
    public static (string? App, string? Problem) InspectArchive(string archive)
    {
        if (!Directory.Exists(archive))
        {
            var parent = Path.GetDirectoryName(archive);
            var misplaced = parent is not null && File.Exists(Path.Combine(parent, "Info.plist"));
            return (null, $"dotnet publish succeeded but did not create the archive {archive}." + (misplaced
                ? $" An archive was written into {parent} itself: the .NET iOS SDK uses ArchiveDir as the .xcarchive."
                : " No new archive for this app was found in Xcode's Archives folder either (where iOS workloads before 27.x write it). Check the publish log for the Archive task."));
        }
        if (!File.Exists(Path.Combine(archive, "Info.plist")))
            return (null, $"{Path.GetFileName(archive)} has no Info.plist, so it is not a complete Xcode archive.");
        var applications = Path.Combine(archive, "Products", "Applications");
        var apps = Directory.Exists(applications) ? Directory.GetDirectories(applications, "*.app") : [];
        return apps.Length switch
        {
            0 => (null, $"{Path.GetFileName(archive)} has no app in Products/Applications."),
            1 when !File.Exists(Path.Combine(apps[0], "Info.plist")) => (null, $"{Path.GetFileName(archive)}/Products/Applications/{Path.GetFileName(apps[0])} has no Info.plist."),
            1 => (apps[0], null),
            _ => (null, $"{Path.GetFileName(archive)} has {apps.Length} apps in Products/Applications ({string.Join(", ", apps.Select(Path.GetFileName))}); expected one."),
        };
    }

    /// <summary>Xcode's default archive folder (~/Library/Developer/Xcode/Archives) for the build's HOME.</summary>
    public static string XcodeArchivesFolder(string home) => Path.Combine(home, "Library", "Developer", "Xcode", "Archives");

    /// <summary>
    /// iOS workloads before 27.x do not pass <c>ArchiveDir</c> to their Archive task (Xamarin.Shared.targets 26.2: the
    /// task only outputs it), so the archive goes to Xcode's Archives folder as
    /// <c>&lt;yyyy-MM-dd&gt;/&lt;Project&gt; &lt;date&gt;.xcarchive</c>. Finds the one this build created: written at or after
    /// <paramref name="sinceUtc"/> and naming <paramref name="bundleId"/> in its Info.plist. Never guesses between several.
    /// </summary>
    public static (string? Archive, string? Problem) FindArchiveInXcodeFolder(string archivesFolder, string? bundleId, DateTime sinceUtc)
    {
        if (!Directory.Exists(archivesFolder)) return (null, null);
        var candidates = new List<string>();
        foreach (var day in Directory.EnumerateDirectories(archivesFolder))
            foreach (var archive in Directory.EnumerateDirectories(day, "*.xcarchive"))
            {
                var info = Path.Combine(archive, "Info.plist");
                if (!File.Exists(info) || File.GetLastWriteTimeUtc(info) < sinceUtc) continue;
                string? id = null;
                try
                {
                    id = (HCG.ExecutableMaker.Packaging.Mobile.PlistReader.ParseDictionary(File.ReadAllBytes(info)).GetValueOrDefault("ApplicationProperties") as Dictionary<string, object>)
                        ?.GetValueOrDefault("CFBundleIdentifier") as string;
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException) { continue; }
                if (bundleId is null || id == bundleId) candidates.Add(archive);
            }
        return candidates.Count switch
        {
            0 => (null, null),
            1 => (candidates[0], null),
            _ => (null, $"{candidates.Count} new archives for {bundleId ?? "this app"} are in {archivesFolder} ({string.Join(", ", candidates.Select(Path.GetFileName))}); HCG does not guess which one this build created."),
        };
    }

    /// <summary>Checks the IPA the SDK wrote: it exists and holds Payload/&lt;app&gt;.app with its Info.plist and executable.</summary>
    public static string? InspectIpa(string ipa, string appName, string? executable)
    {
        if (!File.Exists(ipa)) return $"dotnet publish succeeded but did not create the IPA {ipa}.";
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(ipa);
            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
            var root = $"Payload/{appName}/";
            if (!names.Contains(root + "Info.plist")) return $"{Path.GetFileName(ipa)} has no {root}Info.plist.";
            if (executable is not null && !names.Contains(root + executable)) return $"{Path.GetFileName(ipa)} has no {root}{executable}.";
            return null;
        }
        catch (InvalidDataException ex) { return $"{Path.GetFileName(ipa)} is not a valid IPA (ZIP): {ex.Message}"; }
    }

    public static MobilePlan Plan(MobilePlanInput input)
    {
        var o = input.Options;
        var p = input.Analysis.DotNetIos ?? throw new BuildValidationException("This is not a .NET for iOS project.");
        if (input.Toolchains.Host.Platform != TargetPlatform.MacOS)
            throw new BuildValidationException(".NET for iOS apps are built on a Mac: the iOS workload needs Xcode's SDK, linker and codesign.");
        if (p.OutsideWorkspace.Count > 0)
            throw new BuildValidationException($"{p.ProjectPath} uses files outside the imported folder ({string.Join(", ", p.OutsideWorkspace.Take(3))}). Import the repository root that contains them.");
        var dotnet = input.Toolchains.Find(ToolchainIds.DotNetSdk) is { IsAvailable: true, ExecutablePath: { } d } ? d : throw new BuildValidationException("The .NET SDK was not found.");
        if (input.Toolchains.Find(ToolchainIds.DotNetIosWorkload) is not { IsAvailable: true })
            throw new BuildValidationException("The .NET iOS workload is not installed." + (p.WorkloadPin is { } pin ? $" The project pins {pin} ({p.WorkloadPinFile}): {p.WorkloadInstallCommand}." : " Run: dotnet workload install ios.") + " HCG never installs workloads.");
        var project = MobileArgs.Inside(input.SourceDirectory, p.ProjectPath);
        var projectDir = Path.GetDirectoryName(project)!;
        MobileArgs.RequireToken(p.TargetFramework, "target framework", out var tfm, @"^net\d+\.\d+-ios[\d.]*$");
        var common = new List<string> { "-f", tfm, "-p:UseSharedCompilation=false", "-nodeReuse:false" };
        var env = MobileArgs.With(input.Environment, ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), ("DOTNET_NOLOGO", "1"));
        MobileStep Step(BuildStage stage, string title, List<string> args) =>
            new(stage, title, new ProcessSpec { Executable = dotnet, Arguments = args, WorkingDirectory = projectDir, Environment = env });
        var configuration = o.Variant ?? DefaultConfiguration(o.Output);
        MobileArgs.RequireToken(configuration, "configuration", out configuration);
        var notes = new List<string> { Explain(o.Output, o.ExportMethod) };
        if (o.Output == MobileOutput.IosIpa && o.ExportMethod is IosExportMethod.AppStoreConnect or IosExportMethod.ReleaseTesting or IosExportMethod.Enterprise && configuration != "Release")
            throw new BuildValidationException($"A distribution IPA ({o.ExportMethod}) must be a Release build; {configuration} can contain development-only code. Choose Release, or a development IPA.");

        switch (o.Output)
        {
            case MobileOutput.IosSimulatorApp:
            {
                var rid = input.Toolchains.Host.Architecture == TargetArchitecture.Arm64 ? "iossimulator-arm64" : "iossimulator-x64";
                var outDir = SimulatorDirectory(input);
                var args = new List<string> { "build", project, "-c", configuration, "-r", rid, "-o", outDir };
                args.AddRange(common);
                return new MobilePlan([Step(BuildStage.Compiling, $"dotnet build ({rid}, {configuration})", args)],
                    [new OutputRule(PackagingFormat.IosSimulatorApp, outDir, "*.app", IsDirectory: true, Recursive: false)]) { Notes = notes };
            }
            case MobileOutput.IosDeviceApp:
            {
                var args = new List<string> { "build", project, "-c", configuration, "-r", "ios-arm64", "-o", DeviceDirectory(input) };
                args.AddRange(common);
                args.AddRange(SigningArguments(o, notes, p.CodesignKey));
                return new MobilePlan([Step(BuildStage.Compiling, $"dotnet build (ios-arm64, {configuration}, signed)", args)],
                    [new OutputRule(PackagingFormat.IosDeviceApp, DeviceDirectory(input), "*.app", IsDirectory: true, Recursive: false)]) { Notes = notes };
            }
            case MobileOutput.IosArchive:
            case MobileOutput.IosIpa:
            {
                var ipa = o.Output == MobileOutput.IosIpa;
                var args = new List<string>
                {
                    "publish", project, "-c", configuration, "-r", "ios-arm64",
                    // As `dotnet build -o`: the signed .app lands in a folder of this build only, and is kept if packaging fails.
                    "-p:OutputPath=" + DeviceDirectory(input) + Path.DirectorySeparatorChar, "-p:_CommandLineDefinedOutputPath=true",
                    "-p:ArchiveOnBuild=true", "-p:ArchiveDir=" + ArchivePath(input),
                };
                args.AddRange(ipa ? ["-p:BuildIpa=true", "-p:IpaPackagePath=" + IpaPath(input)] : ["-p:BuildIpa=false"]);
                args.AddRange(common);
                args.AddRange(SigningArguments(o, notes, p.CodesignKey));
                // The archive and the IPA are taken from their exact paths and checked by the build service, never searched for.
                return new MobilePlan([Step(BuildStage.Compiling, ipa ? $"dotnet publish → .ipa and .xcarchive (ios-arm64, {configuration}, signed)" : $"dotnet publish → .xcarchive (ios-arm64, {configuration}, signed)", args)], [])
                {
                    Notes = notes,
                };
            }
            default:
                throw new BuildValidationException($"{o.Output.DisplayName()} is not an iOS output.");
        }
    }

    /// <summary>
    /// Unsigned is refused for device outputs: the .NET iOS SDK archives and installs only signed apps. Manual: the named
    /// certificate and installed provisioning profile (CodesignKey / CodesignProvision). Project default: what the project sets.
    /// </summary>
    internal static List<string> SigningArguments(MobileBuildOptions o, List<string> notes, string? projectCodesignKey = null)
    {
        switch (o.Signing)
        {
            case MobileSigningMode.Unsigned:
                throw new BuildValidationException(HCG.ExecutableMaker.ProjectInspection.Mobile.MobileReadiness.DotNetIosArchiveNeedsSigning);
            case MobileSigningMode.AppleManual:
                MobileArgs.RequireToken(o.SigningIdentity, "code-signing identity", out var identity, @"^[A-Za-z0-9][A-Za-z0-9 _.:()\-]{0,127}$");
                MobileArgs.RequireToken(o.ProvisioningProfile, "provisioning profile name or UUID", out var profile, @"^[A-Za-z0-9][A-Za-z0-9 _.\-]{0,127}$");
                notes.Add($"Signed with \"{identity}\" and provisioning profile \"{profile}\" (from this Mac's keychain and installed profiles).");
                return ["-p:CodesignKey=" + identity, "-p:CodesignProvision=" + profile];
            case MobileSigningMode.ProjectDefault:
                notes.Add(projectCodesignKey == "-"
                    ? HCG.ExecutableMaker.ProjectInspection.Mobile.MobileReadiness.DotNetIosAdHocProjectSigning
                    : "Signing uses the project's own CodesignKey / CodesignProvision settings (if any) and this Mac's keychain.");
                return [];
            default:
                throw new BuildValidationException($"{o.Signing} signing does not apply to .NET for iOS builds (use a named identity and profile).");
        }
    }

    // ───────────── Effective MSBuild properties (before building) ─────────────

    /// <summary>The properties read before a device build, as the build will see them (same configuration, RID and global properties).</summary>
    public static readonly string[] ProbedProperties =
    [
        "OutputType", "ApplicationId", "ApplicationTitle", "ApplicationDisplayVersion", "ApplicationVersion", "SupportedOSPlatformVersion",
        "RuntimeIdentifier", "RuntimeIdentifiers", "ArchiveOnBuild", "ArchiveDir", "BuildIpa", "IpaPackagePath", "OutputPath",
        "CodesignKey", "CodesignProvision", "CodesignEntitlements", "EnableCodeSigning", "MtouchLink", "UseInterpreter",
    ];

    /// <summary>
    /// <c>dotnet msbuild &lt;project&gt; -getProperty:…</c> with the build's own configuration, RID and global properties:
    /// evaluation only (no target runs, nothing is written).
    /// </summary>
    public static MobileStep? PropertyProbe(MobilePlanInput input, MobilePlan plan)
    {
        if (input.Options.Output is MobileOutput.IosSimulatorApp) return null;
        var build = plan.Steps.FirstOrDefault()?.Process;
        if (build is null) return null;
        var a = build.Arguments;
        var configuration = a[a.ToList().IndexOf("-c") + 1];
        // dotnet msbuild has no -f (that is dotnet build/publish's option): MSBuild rejects it as an unknown switch
        // (MSB1001), so the framework is passed as the property it sets. Found on the real HCG Mail (CI run 46112257158).
        var args = new List<string> { "msbuild", a[1], "-p:Configuration=" + configuration, "-p:RuntimeIdentifier=ios-arm64" };
        if (a.ToList().IndexOf("-f") is var fi and >= 0 && fi + 1 < a.Count) args.Add("-p:TargetFramework=" + a[fi + 1]);
        args.AddRange(a.Where(x => x.StartsWith("-p:", StringComparison.Ordinal) && !x.StartsWith("-p:UseSharedCompilation", StringComparison.Ordinal)));
        if (a.Contains("-o")) args.Add("-p:OutputPath=" + a[a.ToList().IndexOf("-o") + 1] + Path.DirectorySeparatorChar);
        args.AddRange(ProbedProperties.Select(n => "-getProperty:" + n));
        return new MobileStep(BuildStage.Preparing, "Effective MSBuild properties (evaluation only)", build with { Arguments = args });
    }

    /// <summary>Parses <c>-getProperty</c> output ({"Properties": {…}}); empty when it is not that JSON.</summary>
    public static IReadOnlyDictionary<string, string> ParseProperties(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            if (!doc.RootElement.TryGetProperty("Properties", out var props) || props.ValueKind != JsonValueKind.Object) return new Dictionary<string, string>();
            return props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString(), StringComparer.Ordinal);
        }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    /// <summary>What the effective properties mean for this output, before anything compiles: blockers and plain notes.</summary>
    public static IReadOnlyList<(DiagnosticSeverity Severity, string Text)> CheckProperties(IReadOnlyDictionary<string, string> p, MobileBuildOptions o, DotNetIosProjectInfo project)
    {
        var findings = new List<(DiagnosticSeverity, string)>();
        string V(string k) => p.TryGetValue(k, out var v) ? v : "";
        if (p.Count == 0) return [(DiagnosticSeverity.Warning, "The effective MSBuild properties could not be read; the build continues with the values HCG passes.")];
        if (!string.Equals(V("OutputType"), "Exe", StringComparison.OrdinalIgnoreCase))
            findings.Add((DiagnosticSeverity.Error, $"OutputType is \"{V("OutputType")}\", not Exe: the SDK creates no app bundle, archive or IPA for a library."));
        if (project.ApplicationId is { Length: > 0 } expected && V("ApplicationId") is { Length: > 0 } effective && effective != expected)
            findings.Add((DiagnosticSeverity.Warning, $"The effective ApplicationId is {effective}, not {expected} as the project file says (a property or variant changes it); signing is checked against {effective}."));
        if (V("RuntimeIdentifiers") is { Length: > 0 } rids && rids.Contains("iossimulator", StringComparison.Ordinal))
            findings.Add((DiagnosticSeverity.Info, $"The project lists RuntimeIdentifiers {rids}; this build is ios-arm64 only (-r ios-arm64)."));
        if (o.Output is MobileOutput.IosArchive or MobileOutput.IosIpa)
        {
            if (!string.Equals(V("ArchiveOnBuild"), "true", StringComparison.OrdinalIgnoreCase))
                findings.Add((DiagnosticSeverity.Error, $"ArchiveOnBuild evaluates to \"{V("ArchiveOnBuild")}\": no archive would be made."));
            if (!V("ArchiveDir").EndsWith(".xcarchive", StringComparison.Ordinal))
                findings.Add((DiagnosticSeverity.Error, $"ArchiveDir is \"{V("ArchiveDir")}\": the SDK writes the archive into exactly that folder, which must be the .xcarchive."));
        }
        if (o.Output == MobileOutput.IosIpa && !string.Equals(V("BuildIpa"), "true", StringComparison.OrdinalIgnoreCase))
            findings.Add((DiagnosticSeverity.Error, $"BuildIpa evaluates to \"{V("BuildIpa")}\": the SDK would make no IPA."));
        if (string.Equals(V("EnableCodeSigning"), "false", StringComparison.OrdinalIgnoreCase))
            findings.Add((DiagnosticSeverity.Error, "EnableCodeSigning is false: a device app, archive or IPA must be signed."));
        if (o.Signing == MobileSigningMode.ProjectDefault && V("CodesignKey").Length == 0)
            findings.Add((DiagnosticSeverity.Error, "Project-default signing, but the project sets no CodesignKey for ios-arm64. Choose a certificate and profile."));
        if (o.Signing == MobileSigningMode.AppleManual && V("CodesignProvision") != o.ProvisioningProfile)
            findings.Add((DiagnosticSeverity.Warning, $"CodesignProvision evaluates to \"{V("CodesignProvision")}\", not the chosen \"{o.ProvisioningProfile}\"."));
        return findings;
    }

}
