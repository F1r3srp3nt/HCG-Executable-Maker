using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Single source of truth for which mobile outputs are offered for a project on this host. Every unavailable output
/// carries the concrete reason (missing tool, wrong host, unsupported project shape).
/// </summary>
public static class MobileCapabilities
{
    /// <summary>
    /// The outputs this project offers on this host. <paramref name="selection"/> carries the user's scheme (and
    /// configuration): availability is judged for the scheme that will actually be built, never for a default one —
    /// otherwise a project whose first scheme is out of the selected Xcode's range (HCG Sentinel's iOS 14.7 legacy
    /// scheme on an Xcode that starts at iOS 15.0) shows every output unavailable even with its compatible scheme chosen.
    /// </summary>
    public static IReadOnlyList<MobileOutputOption> Evaluate(MobileAnalysis mobile, ToolchainReport? report, MobileBuildOptions? selection = null)
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
            var readiness = MobileReadiness.Evaluate(mobile, o, new MobileBuildOptions
            {
                Framework = mobile.Framework, Output = o, Signing = signing, Scheme = selection?.Scheme, Variant = selection?.Variant,
            }, report);
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
            Add(MobileOutput.IosArchive, "Device archive (.xcarchive). Signed with your team, or unsigned for inspection only.", MobileSigningMode.Unsigned,
                mobile.Framework == MobileFramework.DotNetMaui ? [".NET MAUI publishes an .ipa directly; a separate archive output is not offered."] : []);
            Add(MobileOutput.IosIpa, "Archives and exports a signed .ipa (App Store Connect, release testing, enterprise or development).");
        }
        return list;
    }

    /// <summary>Required-but-missing reasons for building this project for a platform (kept for callers that list them).</summary>
    public static IReadOnlyList<string> Blockers(MobileAnalysis mobile, MobileOutput output, ToolchainReport? report, string? scheme = null) =>
        MobileReadiness.Evaluate(mobile, output, scheme is null ? null : new MobileBuildOptions { Framework = mobile.Framework, Output = output, Scheme = scheme }, report)
            .Blockers.Select(b => b.Detail).ToList();
}
