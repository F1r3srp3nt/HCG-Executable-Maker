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
/// </summary>
public static partial class DotNetIosPlanner
{
    public static string SimulatorDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "simulator");
    public static string DeviceDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "device");
    public static string ArchiveRoot(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "archive");
    public static string IpaDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "ipa");

    /// <summary>The file-system name of the app's packages: its title (or assembly name), without characters a shell or URL trips over.</summary>
    public static string PackageName(DotNetIosProjectInfo p) =>
        UnsafeName().Replace((p.ApplicationTitle ?? p.AssemblyName ?? Path.GetFileNameWithoutExtension(p.ProjectPath)).Trim(), "-").Trim('-') is { Length: > 0 } n ? n : "App";

    /// <summary>The exact .xcarchive this build creates (ArchiveDir).</summary>
    public static string ArchivePath(MobilePlanInput input) => Path.Combine(ArchiveRoot(input), PackageName(input.Analysis.DotNetIos!) + ".xcarchive");

    /// <summary>The exact .ipa this build creates (IpaPackagePath).</summary>
    public static string IpaPath(MobilePlanInput input) => Path.Combine(IpaDirectory(input), PackageName(input.Analysis.DotNetIos!) + ".ipa");

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
                args.AddRange(SigningArguments(o, notes));
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
                    "-p:BuildIpa=" + (ipa ? "true" : "false"),
                };
                if (ipa) args.Add("-p:IpaPackagePath=" + IpaPath(input));
                args.AddRange(common);
                args.AddRange(SigningArguments(o, notes));
                var outputs = new List<OutputRule> { new(PackagingFormat.IosArchive, ArchiveRoot(input), "*.xcarchive", IsDirectory: true, Recursive: false) };
                if (ipa) outputs.Insert(0, new(PackagingFormat.IosIpa, IpaDirectory(input), "*.ipa", IsDirectory: false, Recursive: false));
                return new MobilePlan([Step(BuildStage.Compiling, ipa ? $"dotnet publish → .ipa and .xcarchive (ios-arm64, {configuration}, signed)" : $"dotnet publish → .xcarchive (ios-arm64, {configuration}, signed)", args)], outputs)
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
    internal static List<string> SigningArguments(MobileBuildOptions o, List<string> notes)
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
                notes.Add("Signing uses the project's own CodesignKey / CodesignProvision settings (if any) and this Mac's keychain.");
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
        var args = new List<string> { "msbuild", a[1], "-p:Configuration=" + configuration, "-p:RuntimeIdentifier=ios-arm64", "-f", a[a.ToList().IndexOf("-f") + 1] };
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

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")] private static partial Regex UnsafeName();
}
