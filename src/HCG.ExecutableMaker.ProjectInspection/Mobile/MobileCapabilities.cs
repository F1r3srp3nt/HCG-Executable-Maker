using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Single source of truth for which mobile outputs are offered for a project on this host. Every unavailable output
/// carries the concrete reason (missing tool, wrong host, unsupported project shape).
/// </summary>
public static class MobileCapabilities
{
    public static IReadOnlyList<MobileOutputOption> Evaluate(MobileAnalysis mobile, ToolchainReport? report)
    {
        var list = new List<MobileOutputOption>();
        if (mobile.ArtifactPath is { } artifact)
        {
            // An imported artifact is never recompiled. The only transformation offered is Google's own AAB → APK set.
            if (artifact.EndsWith(".aab", StringComparison.OrdinalIgnoreCase))
            {
                var reasons = new List<string>();
                if (report is null) reasons.Add("Toolchains have not been detected yet.");
                else
                {
                    if (!report.IsAvailable(ToolchainIds.Jdk)) reasons.Add("A JDK (17 or newer) is required to run bundletool.");
                    if (!report.IsAvailable(ToolchainIds.Bundletool)) reasons.Add("bundletool was not found (set BUNDLETOOL_JAR or install bundletool).");
                }
                list.Add(new MobileOutputOption(MobileOutput.AndroidAabToApk, reasons.Count == 0,
                    "Generates a universal or device-targeted APK set from this App Bundle with bundletool (signed with HCG's local debug key, for testing).",
                    reasons.Count == 0 ? null : string.Join(" ", reasons)));
            }
            return list;
        }
        var android = mobile.Platforms.Contains(TargetPlatform.Android);
        var ios = mobile.Platforms.Contains(TargetPlatform.IOS);
        // Availability comes from the same per-build readiness the Studio shows (required items only). Archives are
        // judged with "unsigned" allowed, since that option needs no certificate; the chosen signing is checked later.
        void Add(MobileOutput o, string desc, MobileSigningMode signing = MobileSigningMode.ProjectDefault, params string[] extra)
        {
            var readiness = MobileReadiness.Evaluate(mobile, o, new MobileBuildOptions { Framework = mobile.Framework, Output = o, Signing = signing }, report);
            var reasons = readiness.Blockers.Select(b => b.Detail + (b.Fix is null ? "" : " " + b.Fix)).Concat(extra).ToList();
            list.Add(new MobileOutputOption(o, reasons.Count == 0, desc, reasons.Count == 0 ? null : string.Join(" ", reasons)));
        }
        if (android)
        {
            Add(MobileOutput.AndroidDebugApk, "Debug-signed APK for testing on devices and emulators (debug key, not for distribution).");
            Add(MobileOutput.AndroidReleaseApk, "Release APK. Unsigned unless the project defines its own release signing configuration.");
            Add(MobileOutput.AndroidAppBundle, "Android App Bundle (.aab) for Google Play. Play requires upload-key signing before submission.");
            Add(MobileOutput.AndroidAabToApk, "Builds the App Bundle, then uses bundletool to generate a universal or device-targeted APK set.");
        }
        if (ios)
        {
            Add(MobileOutput.IosSimulatorApp, "Builds for the iOS Simulator. Simulator builds are NOT distributable and cannot run on devices.");
            if (mobile.Framework == MobileFramework.NativeIos)
                Add(MobileOutput.IosDeviceApp, "Builds for a physical iPhone or iPad (Debug by default, so development-only features stay available), signed with your Apple Development identity. Not for distribution.");
            if (mobile.Framework == MobileFramework.DotNetIos)
            {
                // .NET for iOS: each output is its own route (DotNetIosPlanner). Device outputs are always signed, so they are judged
                // with the keychain's certificates and profiles (never as "unsigned", which the SDK refuses).
                Add(MobileOutput.IosDeviceApp, "Signed ios-arm64 app for registered iPhones and iPads (dotnet build; Debug or Release), with your Apple Development certificate and a development profile. No archive or IPA; not for distribution.");
                Add(MobileOutput.IosArchive, "Signed device archive (.xcarchive) made by the .NET iOS SDK (ArchiveOnBuild). Opens in Xcode's Organizer.");
                Add(MobileOutput.IosIpa, "Signed .ipa made by the .NET iOS SDK itself (BuildIpa), with the archive. Development, ad hoc or App Store, decided by the certificate and profile.");
                return list;
            }
            Add(MobileOutput.IosArchive, "Device archive (.xcarchive). Signed with your team, or unsigned for inspection only.", MobileSigningMode.Unsigned,
                mobile.Framework == MobileFramework.DotNetMaui ? [".NET MAUI publishes an .ipa directly; a separate archive output is not offered."] : []);
            Add(MobileOutput.IosIpa, "Archives and exports a signed .ipa (App Store Connect, release testing, enterprise or development).");
        }
        return list;
    }

    /// <summary>Required-but-missing reasons for building this project for a platform (kept for callers that list them).</summary>
    public static IReadOnlyList<string> Blockers(MobileAnalysis mobile, MobileOutput output, ToolchainReport? report) =>
        MobileReadiness.Evaluate(mobile, output, null, report).Blockers.Select(b => b.Detail).ToList();
}
