using System.Security;
using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>
/// Plans xcodebuild invocations. Signing is never bypassed: an unsigned archive is labelled as such and is never
/// exported; exports always use <c>destination=export</c> (nothing is uploaded to App Store Connect).
/// </summary>
public static class XcodePlanner
{
    public sealed record Container(string Flag, string Path, string Scheme);

    public static Container ResolveContainer(MobilePlanInput input)
    {
        var o = input.Options;
        var ios = input.Analysis.Ios ?? throw new BuildValidationException("This project has no Xcode project.");
        var rel = o.Container ?? ios.WorkspacePath ?? ios.ProjectPath ?? throw new BuildValidationException("No .xcodeproj or .xcworkspace was found.");
        if (rel != ios.WorkspacePath && rel != ios.ProjectPath) throw new BuildValidationException($"'{rel}' is not this project's Xcode project or workspace.");
        var flag = rel.EndsWith(".xcworkspace", StringComparison.OrdinalIgnoreCase) ? "-workspace" : "-project";
        var scheme = o.Scheme ?? ios.Schemes.FirstOrDefault(s => ios.AppTargets.Any(t => t.Name == s.Name))?.Name
                     ?? ios.Schemes.FirstOrDefault()?.Name ?? ios.AppTargets.FirstOrDefault()?.Name
                     ?? throw new BuildValidationException("No scheme or application target was found.");
        MobileArgs.RequireToken(scheme, "scheme", out scheme);
        return new Container(flag, MobileArgs.Inside(input.SourceDirectory, rel), scheme);
    }

    public static string Xcodebuild(MobilePlanInput input) =>
        input.Toolchains.Find(ToolchainIds.IosSdk)?.ExecutablePath ?? "/usr/bin/xcodebuild";

    public static string ArchivePath(MobilePlanInput input, string scheme) => Path.Combine(input.WorkDirectory, scheme + ".xcarchive");

    public static MobilePlan Plan(MobilePlanInput input)
    {
        if (input.Toolchains.Host.Platform != TargetPlatform.MacOS)
            throw new BuildValidationException("iOS builds require a Mac with Xcode (Apple's toolchain only runs on macOS).");
        var o = input.Options;
        var c = ResolveContainer(input);
        var derived = Path.Combine(input.WorkDirectory, "DerivedData");
        var configuration = o.Variant ?? (o.Output == MobileOutput.IosSimulatorApp ? "Debug" : "Release");
        MobileArgs.RequireToken(configuration, "configuration", out configuration);
        var common = new List<string> { c.Flag, c.Path, "-scheme", c.Scheme, "-configuration", configuration, "-derivedDataPath", derived };

        switch (o.Output)
        {
            case MobileOutput.IosSimulatorApp:
            {
                var args = new List<string>(common) { "-sdk", "iphonesimulator", "-destination", "generic/platform=iOS Simulator", "COMPILER_INDEX_STORE_ENABLE=NO", "build" };
                var products = Path.Combine(derived, "Build", "Products", configuration + "-iphonesimulator");
                return new MobilePlan([Step(input, BuildStage.Compiling, $"xcodebuild build ({c.Scheme}, Simulator)", args)],
                    [new OutputRule(PackagingFormat.IosSimulatorApp, products, "*.app", IsDirectory: true, Recursive: false)])
                {
                    Notes = ["Simulator builds run only in the iOS Simulator. They are not installable on devices and not distributable."],
                };
            }
            case MobileOutput.IosArchive:
            case MobileOutput.IosIpa:
            {
                var archive = ArchivePath(input, c.Scheme);
                var args = new List<string>(common) { "-destination", "generic/platform=iOS", "-archivePath", archive, "COMPILER_INDEX_STORE_ENABLE=NO" };
                var notes = new List<string>();
                args.AddRange(SigningArguments(o, notes));
                args.Add("archive");
                var outputs = o.Output == MobileOutput.IosArchive
                    ? new List<OutputRule> { new(PackagingFormat.IosArchive, input.WorkDirectory, c.Scheme + ".xcarchive", IsDirectory: true, Recursive: false) }
                    : [];
                return new MobilePlan([Step(input, BuildStage.Compiling, $"xcodebuild archive ({c.Scheme})", args)], outputs) { Notes = notes };
            }
            default:
                throw new BuildValidationException($"{o.Output.DisplayName()} is not an Xcode output.");
        }
    }

    /// <summary>Build-setting overrides for the chosen signing mode. Never invents identities or profiles.</summary>
    internal static List<string> SigningArguments(MobileBuildOptions o, List<string> notes)
    {
        var args = new List<string>();
        switch (o.Signing)
        {
            case MobileSigningMode.ProjectDefault:
                notes.Add("Signing uses the project's own Xcode settings and the certificates already in this Mac's keychain.");
                break;
            case MobileSigningMode.Unsigned:
                args.AddRange(["CODE_SIGNING_ALLOWED=NO", "CODE_SIGNING_REQUIRED=NO", "CODE_SIGN_IDENTITY="]);
                notes.Add("UNSIGNED archive: suitable for inspection only. It cannot be installed on devices or exported to an IPA.");
                break;
            case MobileSigningMode.AppleAutomatic:
                args.AddRange(["CODE_SIGN_STYLE=Automatic", "DEVELOPMENT_TEAM=" + Team(o)]);
                if (o.AllowProvisioningUpdates) args.Add("-allowProvisioningUpdates");
                notes.Add($"Automatic signing for team {o.DevelopmentTeam}" + (o.AllowProvisioningUpdates
                    ? " — Xcode may contact Apple with the account signed in to Xcode to create or update profiles (explicitly allowed)."
                    : " using profiles already installed on this Mac."));
                break;
            case MobileSigningMode.AppleManual:
                MobileArgs.RequireToken(o.ProvisioningProfile, "provisioning profile name", out var profile, @"^[A-Za-z0-9][A-Za-z0-9 _.\-]{0,127}$");
                args.AddRange(["CODE_SIGN_STYLE=Manual", "DEVELOPMENT_TEAM=" + Team(o), "PROVISIONING_PROFILE_SPECIFIER=" + profile]);
                notes.Add($"Manual signing with provisioning profile '{profile}' (team {o.DevelopmentTeam}).");
                break;
            default:
                throw new BuildValidationException($"Signing mode {o.Signing} does not apply to iOS builds.");
        }
        return args;
    }

    private static string Team(MobileBuildOptions o)
    {
        MobileArgs.RequireToken(o.DevelopmentTeam, "Apple Developer Team ID (10 letters/digits)", out var team, "^[A-Z0-9]{10}$");
        return team;
    }

    /// <summary>
    /// The export step. Must only be called for an archive that inspection found to be signed with an Apple
    /// certificate. Writes ExportOptions.plist (no secrets) into the work directory; the caller deletes it afterwards.
    /// </summary>
    public static MobilePlan Export(MobilePlanInput input, string archivePath, string? bundleId)
    {
        var o = input.Options;
        var method = o.ExportMethod ?? throw new BuildValidationException("Choose an export method (App Store Connect, release testing, enterprise or development).");
        if (o.Signing == MobileSigningMode.Unsigned) throw new BuildValidationException("An unsigned archive cannot be exported to an IPA.");
        var exportDir = Path.Combine(input.WorkDirectory, "export");
        var optionsPath = Path.Combine(input.WorkDirectory, "ExportOptions.plist");
        File.WriteAllText(optionsPath, ExportOptions(o, method, bundleId), new UTF8Encoding(false));
        var args = new List<string> { "-exportArchive", "-archivePath", archivePath, "-exportPath", exportDir, "-exportOptionsPlist", optionsPath };
        if (o.Signing == MobileSigningMode.AppleAutomatic && o.AllowProvisioningUpdates) args.Add("-allowProvisioningUpdates");
        return new MobilePlan([Step(input, BuildStage.Packaging, $"xcodebuild -exportArchive ({method.XcodeValue()})", args)],
            [new OutputRule(PackagingFormat.IosIpa, exportDir, "*.ipa", IsDirectory: false, Recursive: false)])
        {
            Notes = ["The IPA is exported locally (destination=export). HCG never uploads to TestFlight or App Store Connect."],
        };
    }

    internal static string ExportOptions(MobileBuildOptions o, IosExportMethod method, string? bundleId)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n");
        void Key(string k, string v) => sb.Append("  <key>").Append(k).Append("</key><string>").Append(SecurityElement.Escape(v)).Append("</string>\n");
        Key("method", method.XcodeValue());
        Key("destination", "export");
        sb.Append("  <key>manageAppVersionAndBuildNumber</key><false/>\n");
        sb.Append("  <key>uploadSymbols</key><false/>\n");
        if (o.DevelopmentTeam is not null) Key("teamID", Team(o));
        switch (o.Signing)
        {
            case MobileSigningMode.AppleAutomatic: Key("signingStyle", "automatic"); break;
            case MobileSigningMode.AppleManual:
                Key("signingStyle", "manual");
                if (bundleId is null) throw new BuildValidationException("The archive's bundle identifier is unknown; manual export needs it to map the provisioning profile.");
                sb.Append("  <key>provisioningProfiles</key><dict><key>").Append(SecurityElement.Escape(bundleId)).Append("</key><string>")
                  .Append(SecurityElement.Escape(o.ProvisioningProfile)).Append("</string></dict>\n");
                break;
        }
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    private static MobileStep Step(MobilePlanInput input, BuildStage stage, string title, List<string> args) =>
        new(stage, title, new ProcessSpec
        {
            Executable = Xcodebuild(input), Arguments = args, WorkingDirectory = input.SourceDirectory,
            Environment = MobileArgs.With(input.Environment, ("NSUnbufferedIO", "YES")),
        });
}
