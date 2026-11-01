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

        if (android)
        {
            var missing = AndroidRequirements(mobile, report);
            void AddAndroid(MobileOutput o, string desc, params string[] extra)
            {
                var reasons = missing.Concat(extra).ToList();
                list.Add(new MobileOutputOption(o, reasons.Count == 0, desc, reasons.Count == 0 ? null : string.Join(" ", reasons)));
            }
            AddAndroid(MobileOutput.AndroidDebugApk, "Debug-signed APK for testing on devices and emulators (debug key, not for distribution).");
            AddAndroid(MobileOutput.AndroidReleaseApk, "Release APK. Unsigned unless the project defines its own release signing configuration.");
            AddAndroid(MobileOutput.AndroidAppBundle, "Android App Bundle (.aab) for Google Play. Play requires upload-key signing before submission.");
            AddAndroid(MobileOutput.AndroidAabToApk, "Builds the App Bundle, then uses bundletool to generate a universal or device-targeted APK set.",
                report?.IsAvailable(ToolchainIds.Bundletool) == true ? [] : ["bundletool was not found (set BUNDLETOOL_JAR or install bundletool)."]);
        }

        if (ios)
        {
            var missing = IosRequirements(mobile, report);
            void AddIos(MobileOutput o, string desc, params string[] extra)
            {
                var reasons = missing.Concat(extra).ToList();
                list.Add(new MobileOutputOption(o, reasons.Count == 0, desc, reasons.Count == 0 ? null : string.Join(" ", reasons)));
            }
            AddIos(MobileOutput.IosSimulatorApp, "Builds for the iOS Simulator. Simulator builds are NOT distributable and cannot run on devices.");
            AddIos(MobileOutput.IosArchive, "Device archive (.xcarchive). Signed with your team, or unsigned for inspection only.",
                mobile.Framework == MobileFramework.DotNetMaui ? [".NET MAUI publishes an .ipa directly; a separate archive output is not offered."] : []);
            AddIos(MobileOutput.IosIpa, "Archives and exports a signed .ipa (App Store Connect, release testing, enterprise or development).",
                report?.IsAvailable(ToolchainIds.AppleSigningIdentities) == true || report?.Host.Platform != TargetPlatform.MacOS
                    ? []
                    : ["No code-signing identities were found in the keychain. Add your Apple Developer account in Xcode → Settings → Accounts."]);
        }
        return list;
    }

    private static List<string> AndroidRequirements(MobileAnalysis m, ToolchainReport? r)
    {
        var reasons = new List<string>();
        if (r is null) { reasons.Add("Toolchains have not been detected yet."); return reasons; }
        if (!r.IsAvailable(ToolchainIds.Jdk)) reasons.Add("A JDK (17 or newer) is required.");
        if (!r.IsAvailable(ToolchainIds.AndroidSdk)) reasons.Add("The Android SDK was not found (set ANDROID_HOME).");
        switch (m.Framework)
        {
            case MobileFramework.NativeAndroid:
                if (m.Android is not { AppModules.Count: > 0 }) reasons.Add("No Android application module was found.");
                else if (!m.Android.HasWrapper) reasons.Add("The project has no Gradle wrapper (gradlew + gradle/wrapper/gradle-wrapper.jar). Generate one with `gradle wrapper` in the project.");
                break;
            case MobileFramework.Flutter:
                if (!r.IsAvailable(ToolchainIds.Flutter)) reasons.Add("The Flutter SDK was not found.");
                if (r.Host.Platform == TargetPlatform.Windows) reasons.Add("Flutter builds from HCG are supported on macOS and Linux hosts in this milestone.");
                break;
            case MobileFramework.DotNetMaui:
                if (!r.IsAvailable(ToolchainIds.MauiWorkloads) || r.Find(ToolchainIds.MauiWorkloads)?.AdditionalVersions.Any(w => w.StartsWith("maui", StringComparison.Ordinal) || w == "android") != true)
                    reasons.Add("The .NET MAUI/Android workload is not installed (dotnet workload install maui-android).");
                break;
        }
        return reasons;
    }

    private static List<string> IosRequirements(MobileAnalysis m, ToolchainReport? r)
    {
        var reasons = new List<string>();
        if (r is null) { reasons.Add("Toolchains have not been detected yet."); return reasons; }
        if (r.Host.Platform != TargetPlatform.MacOS)
        {
            reasons.Add("iOS builds require a Mac with Xcode (Apple's toolchain only runs on macOS).");
            return reasons;
        }
        if (!r.IsAvailable(ToolchainIds.Xcode)) reasons.Add("Xcode is not installed or not selected (xcode-select).");
        else if (!r.IsAvailable(ToolchainIds.IosSdk)) reasons.Add("No iOS SDK is installed in the selected Xcode (Xcode → Settings → Components).");
        switch (m.Framework)
        {
            case MobileFramework.NativeIos:
                if (m.Ios is not { } ios || !ios.AppTargets.Any()) reasons.Add("No iOS application target was found.");
                else if (ios.UsesCocoaPods && ios.WorkspacePath is null) reasons.Add("The project uses CocoaPods but has no .xcworkspace; run `pod install` first.");
                break;
            case MobileFramework.Flutter:
                if (!r.IsAvailable(ToolchainIds.Flutter)) reasons.Add("The Flutter SDK was not found.");
                break;
            case MobileFramework.DotNetMaui:
                if (r.Find(ToolchainIds.MauiWorkloads)?.AdditionalVersions.Any(w => w is "maui" or "maui-ios" or "ios") != true)
                    reasons.Add("The .NET MAUI/iOS workload is not installed (dotnet workload install maui-ios).");
                break;
        }
        return reasons;
    }
}
