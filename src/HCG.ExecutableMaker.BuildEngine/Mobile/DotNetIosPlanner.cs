using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>
/// .NET for iOS applications that are not MAUI (HCG Mail's Avalonia.iOS head): built by the .NET iOS workload, which
/// drives Xcode's SDK, actool, ibtool and codesign. No .xcodeproj is involved. Stages stay separate and honest:
/// a Simulator build is a Simulator build; a device archive is created by the toolchain (ArchiveOnBuild, into HCG's
/// build folder via ArchiveDir) — unsigned (inspection only) or signed with the chosen identity and profile; an IPA is
/// exported from a signed archive by xcodebuild -exportArchive in its own stage. Project references are resolved from
/// the repository root, because the whole repository was imported.
/// </summary>
public static class DotNetIosPlanner
{
    public static string ArchiveDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "archives");

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
            throw new BuildValidationException("The .NET iOS workload is not installed." + (p.WorkloadPinFile is { } pin ? $" The project pins its version: dotnet workload install ios --from-rollback-file \"{pin}\"." : " Run: dotnet workload install ios.") + " HCG never installs workloads.");
        var project = MobileArgs.Inside(input.SourceDirectory, p.ProjectPath);
        var projectDir = Path.GetDirectoryName(project)!;
        MobileArgs.RequireToken(p.TargetFramework, "target framework", out var tfm, @"^net\d+\.\d+-ios[\d.]*$");
        var common = new List<string> { "-f", tfm, "-p:UseSharedCompilation=false", "-nodeReuse:false" };
        var env = MobileArgs.With(input.Environment, ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), ("DOTNET_NOLOGO", "1"));
        MobileStep Step(BuildStage stage, string title, List<string> args) =>
            new(stage, title, new ProcessSpec { Executable = dotnet, Arguments = args, WorkingDirectory = projectDir, Environment = env });

        switch (o.Output)
        {
            case MobileOutput.IosSimulatorApp:
            {
                var rid = input.Toolchains.Host.Architecture == TargetArchitecture.Arm64 ? "iossimulator-arm64" : "iossimulator-x64";
                var configuration = o.Variant ?? "Debug";
                MobileArgs.RequireToken(configuration, "configuration", out configuration);
                var outDir = Path.Combine(input.WorkDirectory, "simulator");
                var buildArgs = new List<string> { "build", project, "-c", configuration, "-r", rid, "-o", outDir };
                buildArgs.AddRange(common);
                return new MobilePlan([Step(BuildStage.Compiling, $"dotnet build ({rid}, {configuration})", buildArgs)],
                    [new OutputRule(PackagingFormat.IosSimulatorApp, outDir, "*.app", IsDirectory: true, Recursive: false)])
                {
                    Notes = [$"Simulator build ({rid}): runs only in the iOS Simulator; not installable on devices and not distributable."],
                };
            }
            case MobileOutput.IosArchive:
            case MobileOutput.IosIpa:
            {
                var configuration = o.Variant ?? "Release";
                MobileArgs.RequireToken(configuration, "configuration", out configuration);
                var notes = new List<string>();
                var args = new List<string> { "publish", project, "-c", configuration, "-r", "ios-arm64", "-p:ArchiveOnBuild=true", "-p:ArchiveDir=" + ArchiveDirectory(input), "-p:BuildIpa=false" };
                args.AddRange(common);
                args.AddRange(SigningArguments(o, notes));
                var outputs = o.Output == MobileOutput.IosArchive
                    ? new List<OutputRule> { new(PackagingFormat.IosArchive, ArchiveDirectory(input), "*.xcarchive", IsDirectory: true, Recursive: true) }
                    : [];
                return new MobilePlan([Step(BuildStage.Compiling, $"dotnet publish → archive (ios-arm64, {configuration}{(o.Signing == MobileSigningMode.Unsigned ? ", unsigned" : ", signed")})", args)], outputs) { Notes = notes };
            }
            default:
                throw new BuildValidationException($"{o.Output.DisplayName()} is not an iOS output.");
        }
    }

    /// <summary>
    /// Unsigned: EnableCodeSigning=false (an archive for inspection, never exportable). Manual: the named certificate and
    /// installed provisioning profile (CodesignKey / CodesignProvision). Project default: whatever the project sets.
    /// </summary>
    internal static List<string> SigningArguments(MobileBuildOptions o, List<string> notes)
    {
        switch (o.Signing)
        {
            case MobileSigningMode.Unsigned:
                notes.Add("UNSIGNED archive: suitable for inspection only. It cannot be installed on devices or exported to an IPA.");
                return ["-p:EnableCodeSigning=false"];
            case MobileSigningMode.AppleManual:
                MobileArgs.RequireToken(o.SigningIdentity, "code-signing identity", out var identity, @"^[A-Za-z0-9][A-Za-z0-9 _.:()\-]{0,127}$");
                MobileArgs.RequireToken(o.ProvisioningProfile, "provisioning profile name or UUID", out var profile, @"^[A-Za-z0-9][A-Za-z0-9 _.\-]{0,127}$");
                notes.Add($"Signed with \"{identity}\" and provisioning profile \"{profile}\" (from this Mac's keychain and installed profiles).");
                return ["-p:CodesignKey=" + identity, "-p:CodesignProvision=" + profile];
            case MobileSigningMode.ProjectDefault:
                notes.Add("Signing uses the project's own CodesignKey / CodesignProvision settings (if any) and this Mac's keychain.");
                return [];
            default:
                throw new BuildValidationException($"{o.Signing} signing does not apply to .NET for iOS builds (use a named identity and profile, or unsigned).");
        }
    }
}
