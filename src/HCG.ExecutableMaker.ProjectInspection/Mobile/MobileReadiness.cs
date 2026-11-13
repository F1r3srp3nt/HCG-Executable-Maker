using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Readiness of ONE project + output + signing choice on this machine. Each tool is either required for this build
/// (and blocks it when not ready) or optional / not needed — so a missing Flutter SDK never blocks a native Android
/// build, Android tools never matter for iOS, Windows tools are never listed, and a missing provisioning profile never
/// blocks a Simulator build. Development certificates are never treated as distribution readiness.
/// </summary>
public static class MobileReadiness
{
    private const string JavaUrl = "https://developer.android.com/build/jdks";
    private const string SdkUrl = "https://developer.android.com/studio/intro/update#sdk-manager";
    private const string XcodeUrl = "https://apps.apple.com/app/xcode/id497799835";
    private const string CertUrl = "https://developer.apple.com/help/account/certificates/create-developer-id-certificates";
    private const string ProfileUrl = "https://developer.apple.com/help/account/provisioning-profiles/create-a-development-provisioning-profile";

    public static BuildReadiness Evaluate(MobileAnalysis mobile, MobileOutput output, MobileBuildOptions? options, ToolchainReport? report)
    {
        var target = $"{mobile.Framework.DisplayName()} → {output.DisplayName()}";
        if (report is null) return new BuildReadiness(target, [new("Toolchains", ReadinessState.SetupRequired, true, "Toolchains have not been detected yet.", "Press Refresh on the Toolchains page.")]);
        var items = new List<ReadinessItem>();
        var android = output.Platform() == TargetPlatform.Android;
        if (android) AddAndroid(items, mobile, output, report);
        else AddIos(items, mobile, output, options, report);
        AddFramework(items, mobile, android, report);
        return new BuildReadiness(target, items);
    }

    // ───────────── Android ─────────────

    private static void AddAndroid(List<ReadinessItem> items, MobileAnalysis m, MobileOutput output, ToolchainReport r)
    {
        var artifactOnly = m.ArtifactPath is not null;
        items.Add(JavaItem(r, artifactOnly ? null : m.Android?.WrapperGradleVersion, artifactOnly ? null : m.Android?.AgpVersion,
            m.Framework == MobileFramework.DotNetMaui ? 17 : null));

        if (artifactOnly)
        {
            items.Add(BundletoolItem(r, required: true));
            return;
        }
        var sdk = r.AndroidSdk;
        var sdkRow = r.Find(ToolchainIds.AndroidSdk);
        if (sdk is { Problem: { } problem })
            items.Add(new("Android SDK", ReadinessState.InvalidPath, true, problem, "Choose the SDK folder in Toolchains › Android SDK (Browse…), or Use automatic detection.", sdk.Root, SetupUrl: SdkUrl));
        else if (sdk?.Root is null && sdkRow?.IsAvailable != true)
            items.Add(new("Android SDK", ReadinessState.NotFound, true, "The Android SDK was not found.", "Install Android Studio (it installs the SDK), or choose an existing SDK with Browse… in Toolchains › Android SDK.", SetupUrl: SdkUrl));
        else
        {
            var root = sdk?.Root ?? sdkRow!.ExecutablePath;
            items.Add(new("Android SDK", ReadinessState.Ready, true, $"Found at {root} ({sdk?.Source ?? sdkRow?.Source ?? "detected"}).", Path: root));
            AddSdkComponents(items, m, sdk);
        }

        if (m.Framework == MobileFramework.NativeAndroid)
        {
            items.Add(m.Android is not { AppModules.Count: > 0 }
                ? new("Android app module", ReadinessState.SetupRequired, true, "No Android application module was found in the project.")
                : m.Android.HasWrapper
                    ? new("Gradle wrapper", ReadinessState.Ready, true, $"Gradle {m.Android.WrapperGradleVersion ?? "(version not declared)"} via the project's wrapper.", Version: m.Android.WrapperGradleVersion)
                    : new("Gradle wrapper", ReadinessState.SetupRequired, true, "The project has no Gradle wrapper (gradlew + gradle/wrapper/gradle-wrapper.jar).", "Generate one in the project with `gradle wrapper`, commit it, then re-import."));
        }
        items.Add(BundletoolItem(r, required: output == MobileOutput.AndroidAabToApk));
        foreach (var (id, name) in new[] { (ToolchainIds.Xcode, "Xcode"), (ToolchainIds.IosSdk, "iOS SDK") })
            items.Add(new(name, ReadinessState.NotNeeded, false, "Not used for Android builds."));
    }

    private static void AddSdkComponents(List<ReadinessItem> items, MobileAnalysis m, AndroidSdkDiscoveryResult? sdk)
    {
        if (sdk is null) return;
        var compile = m.Android?.AppModules.Select(a => a.CompileSdk).FirstOrDefault(c => c is not null);
        var licences = sdk.LicensesAccepted;
        if (compile is { } api && m.Framework == MobileFramework.NativeAndroid)
        {
            var installed = sdk.Platforms.Contains(api.ToString());
            items.Add(installed
                ? new($"SDK Platform {api}", ReadinessState.Ready, true, $"android-{api} is installed (compileSdk {api}).")
                : licences
                    ? new($"SDK Platform {api}", ReadinessState.NotVerifiable, true, $"android-{api} (compileSdk {api}) is not installed; Gradle downloads it during the build because the SDK licences are accepted.",
                        "Or install it beforehand in SDK Manager › SDK Platforms.", SetupUrl: SdkUrl)
                    : new($"SDK Platform {api}", ReadinessState.SetupRequired, true, $"android-{api} (compileSdk {api}) is not installed, and Gradle cannot download it because the SDK licences have not been accepted.",
                        $"Install Android {api} in Android Studio › SDK Manager › SDK Platforms (HCG never accepts licences).", SetupUrl: SdkUrl));
        }
        else
            items.Add(sdk.Platforms.Count > 0
                ? new("SDK Platforms", ReadinessState.Ready, true, "Installed: " + string.Join(", ", sdk.Platforms.Take(4).Select(p => "android-" + p)))
                : new("SDK Platforms", licences ? ReadinessState.NotVerifiable : ReadinessState.SetupRequired, true,
                    "No SDK platform is installed." + (licences ? " Gradle downloads the one the project needs (licences accepted)." : ""),
                    "Install one in Android Studio › SDK Manager › SDK Platforms.", SetupUrl: SdkUrl));

        var bt = sdk.SelectedBuildTools;
        items.Add(bt is not null
            ? new("SDK Build-Tools", ReadinessState.Ready, true, $"Build-tools {bt.Version} (apksigner, zipalign, aapt2 present).", Path: bt.Directory, Version: bt.Version)
            : new("SDK Build-Tools", licences ? ReadinessState.NotVerifiable : ReadinessState.SetupRequired, true,
                (sdk.BuildTools.Count == 0 ? "No build-tools are installed." : "Installed build-tools are incomplete.") + (licences ? " The Android Gradle plugin downloads the version it needs." : ""),
                "Install SDK Build-Tools in Android Studio › SDK Manager › SDK Tools.", SetupUrl: SdkUrl));
        items.Add(bt?.Apksigner is { } apksigner
            ? new("apksigner", ReadinessState.Ready, false, "Used after the build to verify APK signatures.", Path: apksigner, Version: bt.Version)
            : new("apksigner", ReadinessState.NotFound, false, "Optional: without it HCG cannot re-verify APK signatures after the build (the build itself works).", "Install SDK Build-Tools.", SetupUrl: SdkUrl));
        items.Add(bt?.Zipalign is { } za ? new("zipalign", ReadinessState.Ready, false, "Part of build-tools " + bt.Version, Path: za)
            : new("zipalign", ReadinessState.NotFound, false, "Optional for HCG (Gradle uses the build-tools it needs).", "Install SDK Build-Tools.", SetupUrl: SdkUrl));
        items.Add(bt?.Aapt2 is { } aapt2 ? new("aapt2", ReadinessState.Ready, false, "Part of build-tools " + bt.Version, Path: aapt2)
            : new("aapt2", ReadinessState.NotFound, false, "Optional for HCG (Gradle downloads its own aapt2).", "Install SDK Build-Tools.", SetupUrl: SdkUrl));
    }

    /// <summary>The JDK item: present, a real JDK, and compatible with the project's Gradle / AGP.</summary>
    public static ReadinessItem JavaItem(ToolchainReport r, string? gradleVersion, string? agpVersion, int? minimum = null)
    {
        const string fixPick = "Choose a JDK 17 or 21 in Toolchains › Java (Android Studio's bundled JDK works), then press Verify.";
        var row = r.Find(ToolchainIds.Jdk);
        var sel = r.Java?.Selected;
        if (r.Java is null && row is { IsAvailable: true })
            sel = new JdkCandidate(Path.GetDirectoryName(Path.GetDirectoryName(row.ExecutablePath ?? "")) ?? "", row.Source ?? "detected", row.Version, ParseMajor(row.Version), true, null);
        if (sel is null)
            return new("JDK", ReadinessState.NotFound, true, row?.Detail ?? "No working JDK was found.", fixPick, SetupUrl: JavaUrl);
        if (!sel.IsUsable)
            return new("JDK", File.Exists(sel.JavaExecutable) ? ReadinessState.SetupRequired : ReadinessState.InvalidPath, true, sel.Problem!, fixPick, sel.Home, SetupUrl: JavaUrl);
        if (minimum is { } min && sel.Major < min)
            return new("JDK", ReadinessState.IncompatibleVersion, true, $"JDK {sel.Major} is too old: {min}+ is required.", fixPick, sel.Home, sel.Version, JavaUrl);
        var fit = AgpCompatibility.CheckJava(sel.Major, gradleVersion, agpVersion);
        var where = $"{sel.Home} ({sel.Source})";
        return fit.Ok switch
        {
            false => new("JDK", ReadinessState.IncompatibleVersion, true, fit.Message + $" Selected: {where}.", fixPick, sel.Home, sel.Version, JavaUrl),
            null when gradleVersion is null && agpVersion is null && sel.Major >= 17 => new("JDK", ReadinessState.Ready, true, $"JDK {sel.Major}: {where}.", null, sel.Home, sel.Version),
            null => new("JDK", ReadinessState.NotVerifiable, true, fit.Message + $" Selected: JDK {sel.Major}, {where}.", null, sel.Home, sel.Version, JavaUrl),
            _ => new("JDK", ReadinessState.Ready, true, fit.Message + $" {where}.", null, sel.Home, sel.Version),
        };
    }

    private static int ParseMajor(string? v) => int.TryParse((v ?? "").Split('.', '_', '-')[0], out var m) ? m : 0;

    private static ReadinessItem BundletoolItem(ToolchainReport r, bool required)
    {
        if (!required) return new("bundletool", ReadinessState.NotNeeded, false, "Only used to turn an App Bundle into APKs.");
        return r.Find(ToolchainIds.Bundletool) is { IsAvailable: true } b
            ? new("bundletool", ReadinessState.Ready, true, "bundletool " + b.Version, Path: b.ExecutablePath, Version: b.Version)
            : new("bundletool", ReadinessState.NotFound, true, "bundletool is needed to turn the App Bundle into APKs.",
                "Download bundletool-all-<version>.jar from github.com/google/bundletool/releases and set BUNDLETOOL_JAR, or put it in HCG's tools folder.",
                SetupUrl: "https://developer.android.com/tools/bundletool");
    }

    // ───────────── iOS ─────────────

    private static void AddIos(List<ReadinessItem> items, MobileAnalysis m, MobileOutput output, MobileBuildOptions? o, ToolchainReport r)
    {
        if (r.Host.Platform != TargetPlatform.MacOS)
        {
            items.Add(new("Mac with Xcode", ReadinessState.OtherHost, true,
                "iOS builds require a Mac with Xcode (Apple's toolchain only runs on macOS). Nothing on this computer needs fixing.",
                "Open the project in HCG Executable Maker on a Mac with Xcode, or use the macOS CI runner."));
            return;
        }
        var x = r.Xcode;
        if (x is null)
        {
            // Reports without the Xcode diagnosis: fall back to the validated rows.
            var xr = r.Find(ToolchainIds.Xcode);
            var sr = r.Find(ToolchainIds.IosSdk);
            items.Add(xr is { IsAvailable: true }
                ? new("Xcode", ReadinessState.Ready, true, $"Xcode {xr.Version}", Path: xr.ExecutablePath, Version: xr.Version)
                : new("Xcode", ReadinessState.NotFound, true, xr?.Detail ?? "Xcode is not installed or not selected.", xr?.InstallHint ?? "Install Xcode and select it in Toolchains › Xcode.", SetupUrl: XcodeUrl));
            if (xr is { IsAvailable: true })
                items.Add(sr is { IsAvailable: true }
                    ? new("iOS SDK", ReadinessState.Ready, true, $"iOS SDK {sr.Version}")
                    : new("iOS SDK", ReadinessState.SetupRequired, true, "No iOS SDK is installed in the selected Xcode.", "Install it in Xcode › Settings › Components."));
        }
        else items.Add(x switch
        {
            { State: XcodeState.Ready or XcodeState.SdkMissing } => new("Xcode", ReadinessState.Ready, true, $"Xcode {x.VerifiedVersion} ({x.VerifiedBuild}) at {x.ActiveDeveloperDirectory}.", Path: x.ActiveDeveloperDirectory, Version: x.VerifiedVersion),
            { State: XcodeState.NotInstalled } => new("Xcode", ReadinessState.NotFound, true, x.Diagnosis, x.CorrectiveAction, SetupUrl: XcodeUrl),
            { State: XcodeState.ValidationFailed } => new("Xcode", ReadinessState.InvalidPath, true, x.Diagnosis + (x.RawError is null ? "" : " " + x.RawError), x.CorrectiveAction),
            _ => new("Xcode", ReadinessState.SetupRequired, true, x.Diagnosis, x.CorrectiveAction),
        });

        var simulator = output == MobileOutput.IosSimulatorApp;
        var sdkName = simulator ? "iphonesimulator" : "iphoneos";
        if (m.Framework == MobileFramework.NativeIos)
        {
            if (m.Ios is not { } ios || !ios.AppTargets.Any())
                items.Add(new("iOS app target", ReadinessState.SetupRequired, true, "No iOS application target was found in the Xcode project."));
            else if (ios.UsesCocoaPods && ios.WorkspacePath is null)
                items.Add(new("CocoaPods workspace", ReadinessState.SetupRequired, true, "The project uses CocoaPods but has no .xcworkspace.", "Run `pod install` in the project, then re-import."));
        }
        if (m.DotNetIos is { } dn)
        {
            items.Add(dn.OutsideWorkspace.Count > 0
                ? new("Project references", ReadinessState.SetupRequired, true, $"{dn.ProjectPath} uses files outside the imported folder: {string.Join(", ", dn.OutsideWorkspace.Take(3))}.",
                    "Import the repository root that contains them (HCG builds the whole repository, never a partial copy).")
                : dn.MissingReferences.Count > 0
                    ? new("Project references", ReadinessState.SetupRequired, true, "Missing: " + string.Join("; ", dn.MissingReferences.Take(3)))
                    : new("Project references", ReadinessState.Ready, true, dn.ProjectReferences.Count == 0 ? "None." : $"{dn.ProjectReferences.Count} project(s), resolved from the repository: {string.Join(", ", dn.ProjectReferences.Select(Path.GetFileNameWithoutExtension))}."));
        }
        if (m.Framework == MobileFramework.NativeIos && x is { State: XcodeState.Ready or XcodeState.SdkMissing } && m.Ios is { } xi)
            AddDeploymentRange(items, xi, o, x, simulator);
        if (x is { State: XcodeState.Ready or XcodeState.SdkMissing })
            items.Add(x.Sdks.Any(s => s.StartsWith(sdkName + " ", StringComparison.Ordinal))
                ? new(simulator ? "iOS Simulator SDK" : "iOS SDK", ReadinessState.Ready, true, x.Sdks.First(s => s.StartsWith(sdkName + " ", StringComparison.Ordinal)))
                : new(simulator ? "iOS Simulator SDK" : "iOS SDK", ReadinessState.SetupRequired, true, $"The selected Xcode has no {sdkName} SDK.",
                    "Install the iOS platform in Xcode › Settings › Components, then press Refresh.", SetupUrl: "https://developer.apple.com/documentation/xcode/downloading-and-installing-additional-xcode-components"));

        var signing = o?.Signing ?? MobileSigningMode.ProjectDefault;
        var unsigned = simulator || signing == MobileSigningMode.Unsigned;
        if (m.Framework == MobileFramework.DotNetIos && !unsigned && signing == MobileSigningMode.AppleManual)
        {
            var identity = o?.SigningIdentity;
            items.Add(identity is null
                ? new("Code-signing identity", ReadinessState.SetupRequired, true, "Name the certificate to sign with (CodesignKey), e.g. \"Apple Distribution: Company (TEAMID)\".")
                : r.AppleIdentities.Count == 0 || r.AppleIdentities.Any(i => i.Name == identity || i.Sha1.Equals(identity, StringComparison.OrdinalIgnoreCase))
                    ? new("Code-signing identity", r.AppleIdentities.Count == 0 ? ReadinessState.NotVerifiable : ReadinessState.Ready, true, $"\"{identity}\"" + (r.AppleIdentities.Count == 0 ? " (the keychain was not read; the build confirms)." : " is in the keychain."))
                    : new("Code-signing identity", ReadinessState.NotFound, true, $"\"{identity}\" is not in this Mac's keychain. Installed: {string.Join("; ", r.AppleIdentities.Select(i => i.Name).Take(3))}.",
                        "Install the certificate (Xcode › Settings › Accounts › Manage Certificates), then press Refresh.", SetupUrl: CertUrl));
        }
        if (m.Framework == MobileFramework.DotNetIos && !simulator && signing == MobileSigningMode.Unsigned)
            items.Add(new("Code signing", ReadinessState.SetupRequired, true, DotNetIosArchiveNeedsSigning,
                "Choose a certificate and provisioning profile (CodesignKey / CodesignProvision), or build for the iOS Simulator."));
        else if (unsigned)
        {
            var why = simulator ? "Simulator builds are not code-signed for devices." : "Unsigned archive (inspection only).";
            items.Add(new("Signing certificate", ReadinessState.NotNeeded, false, why));
            items.Add(new("Provisioning profile", ReadinessState.NotNeeded, false, why));
        }
        else AddIosSigning(items, m, output, o, signing, r);
        foreach (var name in new[] { "JDK", "Android SDK" })
            items.Add(new(name, ReadinessState.NotNeeded, false, "Not used for iOS builds."));
    }

    /// <summary>
    /// Compares the deployment target of every app target the chosen scheme archives with the range the selected Xcode's
    /// iPhoneOS SDK supports. Out of range blocks the build before xcodebuild runs and names the routes that work: another
    /// Xcode (selected explicitly in Toolchains) or another scheme the project offers. HCG never changes the target.
    /// </summary>
    internal static void AddDeploymentRange(List<ReadinessItem> items, IosProjectInfo ios, MobileBuildOptions? o, XcodeStatus x, bool simulator)
    {
        var scheme = ios.Schemes.FirstOrDefault(sc => sc.Name == o?.Scheme) ?? ios.Schemes.FirstOrDefault(sc => ios.AppTargets.Any(t => t.Name == sc.Name)) ?? ios.Schemes.FirstOrDefault();
        var targetNames = scheme?.ArchiveTargets.Count > 0 ? scheme.ArchiveTargets : [scheme?.Name ?? ios.AppTargets.FirstOrDefault()?.Name ?? ""];
        var targets = ios.AppTargets.Where(t => targetNames.Contains(t.Name)).ToList();
        if (targets.Count == 0) return;
        var range = x.IosDeploymentRange;
        var xcode = $"Xcode {x.VerifiedVersion} ({x.ActiveDeveloperDirectory})";
        if (range is null)
        {
            items.Add(new("Deployment target", ReadinessState.NotVerifiable, false,
                $"{string.Join(", ", targets.Select(t => $"{t.Name}: iOS {t.DeploymentTarget ?? "(not set)"}"))}. The supported range of {xcode} could not be read (no iPhoneOS SDKSettings.json); xcodebuild will check it."));
            return;
        }
        var outside = targets.Where(t => range.Contains(t.DeploymentTarget) == false).ToList();
        if (outside.Count == 0)
        {
            items.Add(new("Deployment target", ReadinessState.Ready, true,
                $"{string.Join(", ", targets.Select(t => $"{t.Name}: iOS {t.DeploymentTarget}"))} — within {xcode}'s supported range iOS {range.Minimum}–{range.Maximum} (SDK {range.SdkVersion})."));
            return;
        }
        var other = ios.Schemes.Where(sc => sc.Name != scheme?.Name)
            .Select(sc => (Scheme: sc, Targets: ios.AppTargets.Where(t => (sc.ArchiveTargets.Count > 0 ? sc.ArchiveTargets : [sc.Name]).Contains(t.Name)).ToList()))
            .Where(c => c.Targets.Count > 0 && c.Targets.All(t => range.Contains(t.DeploymentTarget) == true)).ToList();
        var routes = new List<string> { $"Select an Xcode that supports iOS {outside[0].DeploymentTarget} in Toolchains › Xcode (HCG uses it for its own builds only; the system setting is unchanged)" };
        routes.AddRange(other.Select(c => $"or choose scheme {c.Scheme.Name} (iOS {string.Join(", ", c.Targets.Select(t => t.DeploymentTarget).Distinct())}; it does not install on older iOS versions)"));
        var below = IosDeploymentRange.Parse(outside[0].DeploymentTarget) < IosDeploymentRange.Parse(range.Minimum);
        items.Add(new("Deployment target", ReadinessState.IncompatibleVersion, true,
            $"{string.Join(", ", outside.Select(t => $"{t.Name} (scheme {scheme?.Name}) needs iOS {t.DeploymentTarget}"))}, but {xcode} with the iPhoneOS {range.SdkVersion} SDK supports iOS {range.Minimum}–{range.Maximum}" +
            $" ({(below ? "it is below the minimum" : "it is above the maximum")}). xcodebuild would stop with “The iOS deployment target 'IPHONEOS_DEPLOYMENT_TARGET' is set to {outside[0].DeploymentTarget}, but the range of supported deployment target versions is {range.Minimum} to {range.Maximum}”." +
            " HCG does not change the project's deployment target.",
            string.Join("; ", routes) + "."));
    }

    /// <summary>The .NET iOS SDK refuses an unsigned archive (Xamarin.Shared.targets, CI run 78).</summary>
    public const string DotNetIosArchiveNeedsSigning =
        ".NET for iOS creates an Xcode archive (and an IPA) only with code signing: the iOS SDK stops with \"Code signing must be enabled to create an Xcode archive\". An unsigned device archive is not possible for this project type.";

    private static void AddIosSigning(List<ReadinessItem> items, MobileAnalysis m, MobileOutput output, MobileBuildOptions? o, MobileSigningMode signing, ToolchainReport r)
    {
        // Which certificate type the destination needs: development for debugging, distribution for everything else.
        var export = output == MobileOutput.IosIpa ? o?.ExportMethod ?? IosExportMethod.Debugging : (IosExportMethod?)null;
        var needsDistribution = export is IosExportMethod.AppStoreConnect or IosExportMethod.ReleaseTesting or IosExportMethod.Enterprise;
        var identityRow = r.Find(ToolchainIds.AppleSigningIdentities);
        if (r.AppleIdentities.Count == 0 && identityRow is { IsAvailable: true })
        {
            // Identities exist but their types are unknown (older report): never assume distribution readiness.
            items.Add(new("Signing certificate", ReadinessState.NotVerifiable, true, "Code-signing identities are present, but their certificate types are unknown; the build will confirm."));
            items.Add(new("Provisioning profile", ReadinessState.NotVerifiable, false, "Checked by Xcode during the build."));
            return;
        }
        var dev = r.AppleIdentities.Where(i => i.Kind == AppleCertificateKind.Development).ToList();
        var dist = r.AppleIdentities.Where(i => i.Kind == AppleCertificateKind.AppleDistribution).ToList();
        var automaticCanCreate = signing == MobileSigningMode.AppleAutomatic && o?.AllowProvisioningUpdates == true;
        var certName = needsDistribution ? "Apple Distribution certificate" : "Apple Development certificate";
        var have = needsDistribution ? dist : dev;
        if (have.Count > 0)
            items.Add(new(certName, ReadinessState.Ready, true, string.Join("; ", have.Select(i => i.Name).Take(2))));
        else if (automaticCanCreate)
            items.Add(new(certName, ReadinessState.NotVerifiable, true, $"No {certName} in the keychain; with automatic signing and provisioning updates, Xcode may create it with the account signed in to Xcode."));
        else
            items.Add(new(certName, ReadinessState.SetupRequired, true,
                needsDistribution && dev.Count > 0
                    ? $"Only development certificates are in the keychain ({dev.Count}). {export switch { IosExportMethod.AppStoreConnect => "App Store Connect", IosExportMethod.ReleaseTesting => "Ad Hoc / release testing", _ => "Enterprise" }} export needs an Apple Distribution certificate; a development certificate is not enough."
                    : $"No {certName} in the keychain (no code-signing identities were found for this destination).",
                "Create it in Xcode › Settings › Accounts › Manage Certificates (or at developer.apple.com › Certificates), then press Refresh.", SetupUrl: CertUrl));

        var wantedKind = export switch
        {
            IosExportMethod.AppStoreConnect => IosProfileKind.AppStore,
            IosExportMethod.ReleaseTesting => IosProfileKind.AdHoc,
            IosExportMethod.Enterprise => IosProfileKind.Enterprise,
            _ => IosProfileKind.Development,
        };
        var bundleIds = m.Ios?.AppTargets.Select(t => t.BundleIdentifier).OfType<string>().ToList()
                        ?? (m.DotNetIos?.ApplicationId is { } dotnetId ? [dotnetId] : []);
        var now = DateTimeOffset.UtcNow;
        var candidates = r.IosProfiles.Where(p => !p.IsExpired(now) && p.Kind == wantedKind && (bundleIds.Count == 0 || bundleIds.Any(p.Matches))).ToList();
        switch (signing)
        {
            case MobileSigningMode.AppleManual:
                var named = o?.ProvisioningProfile;
                var match = r.IosProfiles.FirstOrDefault(p => named is not null && (p.Name == named || p.Uuid == named));
                items.Add(match is null
                    ? new("Provisioning profile", ReadinessState.NotFound, true, $"Manual signing names the profile \"{named}\", which is not installed on this Mac.",
                        "Download it in Xcode › Settings › Accounts › Download Manual Profiles, or at developer.apple.com › Profiles.", SetupUrl: ProfileUrl)
                    : match.IsExpired(now)
                        ? new("Provisioning profile", ReadinessState.SetupRequired, true, $"\"{named}\" expired on {match.Expiration:yyyy-MM-dd}.", "Regenerate it at developer.apple.com › Profiles and download it again.", SetupUrl: ProfileUrl)
                        : match.Kind != wantedKind
                            ? new("Provisioning profile", ReadinessState.IncompatibleVersion, true, $"\"{named}\" is a {match.Kind} profile; this destination needs a {wantedKind} profile.", "Choose or create a profile of the right type.", SetupUrl: ProfileUrl)
                            : new("Provisioning profile", ReadinessState.Ready, true, $"\"{named}\" ({match.Kind}, {match.ApplicationIdentifier}, expires {match.Expiration:yyyy-MM-dd})."));
                break;
            case MobileSigningMode.AppleAutomatic:
                items.Add(candidates.Count > 0
                    ? new("Provisioning profile", ReadinessState.Ready, false, $"{candidates.Count} matching {wantedKind} profile(s) installed; Xcode picks one.")
                    : automaticCanCreate
                        ? new("Provisioning profile", ReadinessState.NotVerifiable, false, $"No {wantedKind} profile installed; Xcode creates one during the build (provisioning updates allowed).")
                        : new("Provisioning profile", ReadinessState.SetupRequired, true, $"No matching {wantedKind} profile is installed, and provisioning updates are off.",
                            "Enable 'Allow provisioning updates' (uses the account signed in to Xcode), or download the profile in Xcode › Settings › Accounts.", SetupUrl: ProfileUrl));
                break;
            default: // ProjectDefault: the project's own signing settings decide
                items.Add(new("Provisioning profile", candidates.Count > 0 ? ReadinessState.Ready : ReadinessState.NotVerifiable, false,
                    candidates.Count > 0 ? $"{candidates.Count} matching {wantedKind} profile(s) installed." : $"The project's own signing settings decide; no matching {wantedKind} profile was found locally (the build will tell)."));
                break;
        }
    }

    // ───────────── Frameworks ─────────────

    private static void AddFramework(List<ReadinessItem> items, MobileAnalysis m, bool android, ToolchainReport r)
    {
        switch (m.Framework)
        {
            case MobileFramework.Flutter:
                items.Add(r.Find(ToolchainIds.Flutter) is { IsAvailable: true } f
                    ? new("Flutter SDK", ReadinessState.Ready, true, "Flutter " + f.Version, Path: f.ExecutablePath, Version: f.Version)
                    : new("Flutter SDK", ReadinessState.NotFound, true, "This is a Flutter project and the Flutter SDK was not found.",
                        "Install Flutter, then add flutter/bin to PATH or set FLUTTER_ROOT, and press Refresh.", SetupUrl: "https://docs.flutter.dev/get-started/install"));
                if (r.Host.Platform == TargetPlatform.Windows && android)
                    items.Add(new("Host", ReadinessState.OtherHost, true, "Flutter builds from HCG run on macOS and Linux hosts in this version."));
                break;
            case MobileFramework.DotNetMaui:
                var workloads = r.Find(ToolchainIds.MauiWorkloads)?.AdditionalVersions ?? [];
                var ok = android ? workloads.Any(w => w.StartsWith("maui", StringComparison.Ordinal) || w == "android") : workloads.Any(w => w is "maui" or "maui-ios" or "ios");
                items.Add(ok
                    ? new(".NET MAUI workload", ReadinessState.Ready, true, string.Join(", ", workloads))
                    : new(".NET MAUI workload", ReadinessState.NotFound, true, $"The .NET MAUI {(android ? "Android" : "iOS")} workload is not installed.",
                        $"Run: dotnet workload install {(android ? "maui-android" : "maui-ios")} (HCG never installs workloads).", SetupUrl: "https://learn.microsoft.com/dotnet/maui/get-started/installation"));
                items.Add(new("Flutter SDK", ReadinessState.NotNeeded, false, "Not a Flutter project."));
                break;
            case MobileFramework.DotNetIos:
                items.Add(r.Find(ToolchainIds.DotNetSdk) is { IsAvailable: true } sdkRow
                    ? new(".NET SDK", ReadinessState.Ready, true, $".NET SDK {sdkRow.Version}" + (m.DotNetIos?.GlobalJsonSdk is { } gj ? $" (global.json asks for {gj})" : ""), Path: sdkRow.ExecutablePath, Version: sdkRow.Version)
                    : new(".NET SDK", ReadinessState.NotFound, true, "The .NET SDK was not found.", "Install the .NET SDK the project's global.json names, then press Refresh.", SetupUrl: "https://dotnet.microsoft.com/download"));
                var ios = r.Find(ToolchainIds.DotNetIosWorkload);
                var pin = m.DotNetIos?.WorkloadPin;
                items.Add(ios is not { IsAvailable: true }
                    ? new(".NET iOS workload", r.Host.Platform == TargetPlatform.MacOS ? ReadinessState.NotFound : ReadinessState.OtherHost, true,
                        ios?.Detail ?? "The .NET iOS workload is not installed.",
                        pin is null ? "Run: dotnet workload install ios (HCG never installs workloads)." : $"Run: dotnet workload install ios --from-rollback-file \"{m.DotNetIos!.WorkloadPinFile}\" — the version the project pins ({pin}). HCG never installs workloads.",
                        SetupUrl: "https://learn.microsoft.com/dotnet/core/tools/dotnet-workload-install")
                    : pin is not null && ios.Version is { } installed && !pin.StartsWith(installed.Split('/')[0], StringComparison.Ordinal)
                        ? new(".NET iOS workload", ReadinessState.IncompatibleVersion, true, $"Installed {installed}; the project pins {pin} ({m.DotNetIos!.WorkloadPinFile}). Each workload version requires a specific Xcode.",
                            $"Install the pinned version: dotnet workload install ios --from-rollback-file \"{m.DotNetIos.WorkloadPinFile}\", or update the project's pin deliberately.", Version: installed)
                        : new(".NET iOS workload", ReadinessState.Ready, true, ios.Detail ?? "Installed" + (pin is null ? "" : $"; matches the project's pin {pin}"), Version: ios.Version));
                items.Add(new("Flutter SDK", ReadinessState.NotNeeded, false, "Not a Flutter project."));
                break;
            default:
                items.Add(new("Flutter SDK", ReadinessState.NotNeeded, false, "Not a Flutter project."));
                items.Add(new(".NET MAUI workload", ReadinessState.NotNeeded, false, "Not a .NET MAUI project."));
                break;
        }
    }
}
