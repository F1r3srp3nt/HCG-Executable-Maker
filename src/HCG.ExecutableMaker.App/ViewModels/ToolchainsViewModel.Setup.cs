using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// Guided setup for Java and the Android SDK on the Toolchains page: the resolved path, version and source, the
/// actionable reason when something is wrong, Browse… / Verify / Use automatic detection, and the official setup page.
/// Choices are saved in HCG only after they verify, and readiness is recomputed immediately.
/// </summary>
public sealed partial class ToolchainsViewModel
{
    // ---------- Java ----------
    [ObservableProperty] private StatusTag _javaState = new("—", "neutral");
    [ObservableProperty] private string _javaPath = "";
    [ObservableProperty] private string _javaVersion = "—";
    [ObservableProperty] private string _javaSource = "";
    [ObservableProperty] private string _javaDetail = "";
    [ObservableProperty] private string _javaPathInput = "";
    [ObservableProperty] private string _javaMessage = "";
    [ObservableProperty] private bool _javaSaved;
    public ObservableCollection<string> JavaCandidates { get; } = [];

    // ---------- Android SDK ----------
    [ObservableProperty] private StatusTag _sdkState = new("—", "neutral");
    [ObservableProperty] private string _sdkPath = "";
    [ObservableProperty] private string _sdkSource = "";
    [ObservableProperty] private string _sdkDetail = "";
    [ObservableProperty] private string _sdkPlatforms = "—";
    [ObservableProperty] private string _sdkBuildTools = "—";
    [ObservableProperty] private string _sdkTools = "—";
    [ObservableProperty] private string _sdkLicences = "";
    [ObservableProperty] private string _sdkPathInput = "";
    [ObservableProperty] private string _sdkMessage = "";
    [ObservableProperty] private bool _sdkSaved;

    private void ApplyJava(ToolchainReport report)
    {
        var row = report.Find(ToolchainIds.Jdk);
        JavaState = row is null ? new StatusTag("—", "neutral") : new StatusTag(Format.Label(row.Status), Format.Tone(row.Status));
        var sel = report.Java?.Selected;
        JavaPath = sel?.Home ?? "Not found";
        JavaVersion = sel?.Version is { } v ? $"Java {v}{(sel.HasJavac ? " (JDK with javac)" : "")}" : "—";
        JavaSource = sel?.Source ?? "";
        JavaDetail = row?.Detail ?? "";
        JavaSaved = report.Java?.SavedHome is not null;
        if (string.IsNullOrEmpty(JavaPathInput)) JavaPathInput = report.Java?.SavedHome ?? "";
        JavaCandidates.Clear();
        foreach (var c in report.Java?.Candidates ?? [])
            JavaCandidates.Add(c.IsUsable ? $"✓ Java {c.Version} — {c.Home} ({c.Source})" : $"✗ {c.Home} ({c.Source}): {c.Problem}");
    }

    private void ApplyAndroid(ToolchainReport report)
    {
        var row = report.Find(ToolchainIds.AndroidSdk);
        var sdk = report.AndroidSdk;
        SdkState = row is null ? new StatusTag("—", "neutral") : new StatusTag(Format.Label(row.Status), Format.Tone(row.Status));
        SdkPath = sdk?.Root ?? "Not found";
        SdkSource = sdk?.Source ?? "";
        SdkDetail = sdk?.Problem ?? row?.Detail ?? "";
        SdkPlatforms = sdk is null || sdk.Platforms.Count == 0 ? "None installed" : string.Join(", ", sdk.Platforms.Select(p => "android-" + p));
        SdkBuildTools = sdk is null || sdk.BuildTools.Count == 0 ? "None installed"
            : string.Join(", ", sdk.BuildTools.Select(b => b.Version + (b.IsComplete ? "" : " (incomplete)"))) + (sdk.SelectedBuildTools is { } bt ? $" — using {bt.Version}" : " — no complete version");
        var sel = sdk?.SelectedBuildTools;
        SdkTools = sel is null ? "apksigner, zipalign, aapt2: not available (no complete build-tools)" : $"apksigner {sel.Apksigner}\nzipalign {sel.Zipalign}\naapt2 {sel.Aapt2}";
        SdkLicences = sdk?.Root is null ? "" : sdk.LicensesAccepted ? "SDK licences accepted." : "SDK licences not accepted: Gradle cannot download missing components until you run sdkmanager --licenses yourself (HCG never accepts licences).";
        SdkSaved = sdk?.SavedRoot is not null;
        if (string.IsNullOrEmpty(SdkPathInput)) SdkPathInput = sdk?.SavedRoot ?? "";
    }

    [RelayCommand]
    private async Task BrowseJavaAsync()
    {
        if (await shell.PickFolderAsync("Choose a JDK folder (JAVA_HOME, a .jdk bundle or Android Studio's jbr folder)") is not { } path) return;
        JavaPathInput = path;
        await VerifyJavaAsync();
    }

    /// <summary>Runs the JDK at the typed/browsed path; saves it only when it works, then refreshes everything.</summary>
    [RelayCommand]
    private async Task VerifyJavaAsync()
    {
        if (string.IsNullOrWhiteSpace(JavaPathInput)) { JavaMessage = "Enter or browse to a JDK folder first."; return; }
        JavaMessage = "Verifying…";
        var check = await toolPaths.VerifyAndSaveJavaAsync(JavaPathInput, save: true);
        JavaMessage = check.Saved
            ? $"Verified and saved for HCG: Java {check.Candidate.Version} at {check.Candidate.Home}. Your system JAVA_HOME was not changed."
            : $"Not saved — {check.Candidate.Problem}";
        if (check.Saved) await RefreshAsync();
    }

    [RelayCommand]
    private async Task UseAutomaticJavaAsync()
    {
        await toolPaths.ClearJavaAsync();
        JavaPathInput = "";
        JavaMessage = "HCG detects the JDK automatically again.";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task BrowseSdkAsync()
    {
        if (await shell.PickFolderAsync("Choose the Android SDK folder") is not { } path) return;
        SdkPathInput = path;
        await VerifySdkAsync();
    }

    [RelayCommand]
    private async Task VerifySdkAsync()
    {
        if (string.IsNullOrWhiteSpace(SdkPathInput)) { SdkMessage = "Enter or browse to the Android SDK folder first."; return; }
        var check = await toolPaths.VerifyAndSaveAndroidSdkAsync(SdkPathInput, save: true);
        SdkMessage = check.Saved
            ? $"Verified and saved for HCG: {check.Sdk.Root} ({check.Sdk.Platforms.Count} platform(s), {check.Sdk.BuildTools.Count} build-tools). ANDROID_HOME was not changed."
            : $"Not saved — {check.Sdk.Problem}";
        if (check.Saved) await RefreshAsync();
    }

    [RelayCommand]
    private async Task UseAutomaticSdkAsync()
    {
        await toolPaths.ClearAndroidSdkAsync();
        SdkPathInput = "";
        SdkMessage = "HCG detects the Android SDK automatically again.";
        await RefreshAsync();
    }

    [RelayCommand] private async Task OpenJavaGuideAsync() => await shell.OpenUrlAsync(JavaDiscovery.SetupUrl);
    [RelayCommand] private async Task OpenSdkGuideAsync() => await shell.OpenUrlAsync(AndroidSdkDiscovery.SetupUrl);
    [RelayCommand] private async Task OpenXcodeGuideAsync() => await shell.OpenUrlAsync(XcodeDiscovery.AppStoreUrl);
    [RelayCommand] private async Task OpenToolUrlAsync(ToolchainItem? item) { if (item?.SetupUrl is { } u) await shell.OpenUrlAsync(u); }
}

/// <summary>Groups toolchain rows by what they are for, so optional SDKs and other-host tools never look like a broken machine.</summary>
public static class ToolchainGrouping
{
    public static IEnumerable<ToolchainGroup> Group(ToolchainReport report)
    {
        var host = report.Host.Platform;
        static int Order(string cat) => cat switch
        {
            "Build" or "Source control" => 0, "macOS" or "Swift" or "Swift / macOS apps" => 1, "Android" => 2, "Apple mobile" => 3, "Flutter" => 4, ".NET MAUI" => 5, "Node.js / Electron" => 6, "C / C++" => 7, _ => 8,
        };
        // Groups that belong entirely to another host OS go last: they are information, not work to do.
        foreach (var g in report.Toolchains.GroupBy(t => t.Category)
                     .OrderBy(g => g.All(t => t.Status == ToolchainStatus.UnsupportedOnHost) || (g.Key == "Swift / macOS apps" && host != TargetPlatform.MacOS) ? 9 : Order(g.Key)))
        {
            var otherHost = g.All(t => t.Status == ToolchainStatus.UnsupportedOnHost);
            var (title, note, optional) = g.Key switch
            {
                "Android" => ("Android", "Needed only for Android projects (native Gradle, Flutter or .NET MAUI). Missing items do not affect macOS, Windows or iOS builds.", true),
                "Apple mobile" => ("Apple mobile (iOS)", "Needed only for iOS projects. Simulator builds need no certificates or profiles; signed device builds do.", true),
                "Flutter" => ("Flutter", "Needed only for Flutter projects.", true),
                ".NET MAUI" => (".NET MAUI", "Needed only for .NET MAUI projects.", true),
                "Node.js / Electron" => ("Node.js / Electron", "Needed only for Electron desktop apps. The Node.js version is checked against each project's engines field when you inspect it.", true),
                "C / C++" => ("C / C++", "Needed only for C/C++ projects. CMake projects need CMake and a compiler (Ninja is optional); Makefile projects need make and a compiler. HCG builds for this computer's platform only.", true),
                "Windows" when otherHost => ("Windows (other host)", "These tools exist only on Windows and are used when building Windows installers on a Windows PC. Nothing to set up on this computer.", true),
                "macOS" when otherHost => ("macOS (other host)", "These tools exist only on macOS. Nothing to set up on this computer.", true),
                "Swift / macOS apps" when host != TargetPlatform.MacOS => ("Swift / macOS apps (Mac only)", "Swift macOS apps are built on a Mac. Nothing to set up on this computer.", true),
                _ => (g.Key, "", false),
            };
            yield return new ToolchainGroup(title, g.Select(t => new ToolchainItem(t, optional)).ToList()) { Note = note };
        }
    }
}
