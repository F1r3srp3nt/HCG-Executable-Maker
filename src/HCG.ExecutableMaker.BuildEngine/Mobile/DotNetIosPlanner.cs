using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>
/// .NET for iOS applications that are not MAUI (HCG Mail's Avalonia.iOS head): built by the .NET iOS workload, which
/// drives Xcode's SDK, actool, ibtool and codesign. No .xcodeproj is involved. Stages stay separate and honest:
/// a Simulator build is a Simulator build; a device archive is created by the toolchain (ArchiveOnBuild), signed with the
/// chosen identity and profile or with the project's own signing settings. The .NET iOS SDK's <c>ArchiveDir</c> is the
/// <c>.xcarchive</c> itself, not a folder for it (Xamarin.MacDev.Tasks.Archive: <c>Directory.CreateDirectory(ArchiveDir)</c>),
/// so HCG passes the exact archive path, <see cref="ArchivePath"/>, and uses exactly that path afterwards. An IPA is
/// packaged by the SDK from the same build (<c>BuildIpa</c>, <c>IpaPackagePath</c>), at <see cref="IpaPath"/>; its
/// distribution type follows the provisioning profile. Project references are resolved from the repository root,
/// because the whole repository was imported.
/// </summary>
public static class DotNetIosPlanner
{
    public static string ArchiveDirectory(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "archives");

    /// <summary>The exact <c>.xcarchive</c> the SDK writes (<c>-p:ArchiveDir=</c>): &lt;work&gt;/archives/&lt;name&gt;.xcarchive.</summary>
    public static string ArchivePath(MobilePlanInput input) => Path.Combine(ArchiveDirectory(input), ProductName(input) + ".xcarchive");

    /// <summary>The exact IPA the SDK writes (<c>-p:IpaPackagePath=</c>): &lt;work&gt;/ipa/&lt;name&gt;.ipa.</summary>
    public static string IpaPath(MobilePlanInput input) => Path.Combine(input.WorkDirectory, "ipa", ProductName(input) + ".ipa");

    /// <summary>The assembly name (else the project file's name), reduced to characters that are safe in a path and an MSBuild property.</summary>
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
                var ipa = o.Output == MobileOutput.IosIpa;
                var args = new List<string> { "publish", project, "-c", configuration, "-r", "ios-arm64", "-p:ArchiveOnBuild=true", "-p:ArchiveDir=" + ArchivePath(input) };
                args.AddRange(ipa ? ["-p:BuildIpa=true", "-p:IpaPackagePath=" + IpaPath(input)] : ["-p:BuildIpa=false"]);
                args.AddRange(common);
                args.AddRange(SigningArguments(o, notes, p.CodesignKey));
                if (ipa)
                    notes.Add("The IPA is packaged by the .NET iOS SDK from this build; its distribution type (development, ad hoc, App Store) follows the provisioning profile.");
                // The archive and the IPA are taken from their exact paths and checked by the build service, never searched for.
                return new MobilePlan([Step(BuildStage.Compiling, $"dotnet publish → archive{(ipa ? " and IPA" : "")} (ios-arm64, {configuration}, signed)", args)], []) { Notes = notes };
            }
            default:
                throw new BuildValidationException($"{o.Output.DisplayName()} is not an iOS output.");
        }
    }

    /// <summary>
    /// Unsigned is refused: the .NET iOS SDK archives only with code signing. Manual: the named certificate and
    /// installed provisioning profile (CodesignKey / CodesignProvision). Project default: whatever the project sets.
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
                throw new BuildValidationException($"{o.Signing} signing does not apply to .NET for iOS builds (use a named identity and profile, or unsigned).");
        }
    }
}
