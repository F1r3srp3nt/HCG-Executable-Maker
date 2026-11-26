using System.Security.Cryptography;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.Impactor;
using HCG.Impactor.Cli;
using HCG.Impactor.Devices;
using HCG.Impactor.Diagnostics;
using HCG.Impactor.Inspection;
using HCG.Impactor.MacOS;
using HCG.Impactor.Model;
using HCG.Impactor.Signing;
using HCG.Impactor.Storage;
using Xunit;
using F = HCG.ExecutableMaker.Tests.ImpactorFixtures;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// HCG Impactor — AUTOMATED tests on SYNTHETIC fixtures (no Apple credentials, no device). Real codesign/devicectl runs are
/// in the macOS CI job "HCG Impactor"; physical-device installation is validated by hand (docs/IMPACTOR.md).
/// </summary>
public sealed class ImpactorTests
{
    private static readonly DateTimeOffset Now = SigningFixtures.Now;
    private static IpaInspectionOptions Opts(bool trustSyntheticRoot = false) => new() { Time = new FixedTime(Now), TrustedRoots = trustSyntheticRoot ? SigningFixtures.TrustedRoots : null };

    private static (string Ipa, System.Security.Cryptography.X509Certificates.X509Certificate2 Cert, byte[] Profile) SignedIpa(TempDir dir, Func<F.IpaSpec, F.IpaSpec>? tweak = null)
    {
        var cert = SigningFixtures.Development();
        var profile = SigningFixtures.Profile(F.Profile("*", cert) with { AppId = SigningFixtures.Team + ".*" });
        var spec = new F.IpaSpec { Signer = cert, EmbeddedProfile = profile };
        return (F.Ipa(dir, tweak is null ? spec : tweak(spec)), cert, profile);
    }

    // ------------------------------------------------------------------ IPA Inspector

    [Fact]
    public void Inspector_reads_identity_versions_architectures_bundles_signatures_profiles_and_integrity()
    {
        using var dir = new TempDir();
        var (ipa, cert, _) = SignedIpa(dir);
        var before = SHA256.HashData(File.ReadAllBytes(ipa));

        var r = IpaInspector.Inspect(ipa, Opts());

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(ipa))); // the original is never modified
        Assert.Equal(Convert.ToHexString(before).ToLowerInvariant(), r.Sha256);
        Assert.Equal("Payload/Synthetic.app", r.AppPath);
        var app = r.App!;
        Assert.Equal(F.AppId, app.BundleIdentifier);
        Assert.Equal(("1.2.0", "42", "16.0"), (app.ShortVersion, app.BuildNumber, app.MinimumOSVersion));
        Assert.Equal(["arm64"], app.Architectures);
        Assert.Equal("iOS", app.Slices[0].Platform);
        Assert.False(app.IsEncrypted);
        Assert.Equal(DeviceFamilies.IPhone | DeviceFamilies.IPad, r.Families);
        Assert.Contains("arm64", r.RequiredCapabilities);

        // Code signature read from the binary: identifier, team, certificate, time, entitlements.
        Assert.True(app.Signature.Present);
        Assert.False(app.Signature.AdHoc);
        Assert.Equal(F.AppId, app.Signature.Identifier);
        Assert.Equal(SigningFixtures.Team, app.Signature.TeamId);
        Assert.Equal("SHA-256", app.Signature.HashType);
        Assert.Equal(cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false), app.Signature.Signer!.CommonName);
        Assert.Equal(Now, app.Signature.SigningTime);
        Assert.Equal($"{SigningFixtures.Team}.{F.AppId}", app.Signature.Entitlements["application-identifier"]);
        Assert.Equal("true", app.Signature.Entitlements["get-task-allow"]);

        // Embedded profile verified against the synthetic root only when asked; untrusted otherwise.
        Assert.Equal(ProfileSignatureStatus.IntactUntrustedSigner, app.EmbeddedProfile!.Profile!.Signature);
        Assert.Equal(ProfileSignatureStatus.VerifiedApple, IpaInspector.Inspect(ipa, Opts(trustSyntheticRoot: true)).App!.EmbeddedProfile!.Profile!.Signature);
        Assert.Contains(F.DeviceUdid, app.EmbeddedProfile.Profile.ProvisionedDevices);
        Assert.True(app.EmbeddedProfile.RawEntitlements.ContainsKey("keychain-access-groups"));

        // Nested code: extension (with extension point), framework, loose dylib.
        Assert.Collection(r.Nested.OrderBy(b => b.RelativePath, StringComparer.Ordinal),
            b => { Assert.Equal(BundleKind.Framework, b.Kind); Assert.Equal("Frameworks/Lib.framework", b.RelativePath); Assert.Empty(b.Signature.Entitlements); },
            b => { Assert.Equal(BundleKind.Dylib, b.Kind); Assert.Equal("Frameworks/libswiftSynthetic.dylib", b.RelativePath); },
            b => { Assert.Equal(BundleKind.AppExtension, b.Kind); Assert.Equal("com.example.app.share", b.BundleIdentifier); Assert.Equal("com.apple.share-services", b.ExtensionPoint); });
        Assert.Equal(r.App, r.SigningOrder.Last()); // the app is sealed last
        Assert.True(r.SigningOrder.ToList().IndexOf(r.Nested.Single(b => b.Kind == BundleKind.Dylib)) < r.SigningOrder.ToList().IndexOf(r.Nested.Single(b => b.Kind == BundleKind.Framework)));

        // Sealed resources verified file by file.
        Assert.True(app.Resources.Intact);
        Assert.Equal(app.Resources.Sealed - 2, app.Resources.Verified); // nested code is sealed by cdhash, not re-hashed
        Assert.DoesNotContain(r.Findings, f => f.Severity == FindingSeverity.Blocker);
        Assert.True(r.CanProceed);
    }

    [Fact]
    public void Inspector_reports_files_changed_after_signing()
    {
        using var dir = new TempDir();
        var (ipa, _, _) = SignedIpa(dir, s => s with { TamperResource = true });
        var r = IpaInspector.Inspect(ipa, Opts());
        Assert.Equal(["Assets.car"], r.App!.Resources.Modified);
        var f = Assert.Single(r.Findings, f => f.Code == "integrity.resources-changed");
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("added, modified, or deleted", f.Detail);
    }

    [Theory]
    [InlineData("encrypted", "binary.encrypted")]
    [InlineData("simulator", "binary.simulator")]
    [InlineData("32-bit", "binary.32-bit")]
    public void Inspector_blocks_binaries_that_cannot_run_on_a_device(string kind, string code)
    {
        using var dir = new TempDir();
        var main = kind switch
        {
            "encrypted" => new F.MachOSpec { Encrypted = true },
            "simulator" => new F.MachOSpec { Platform = F.PlatformSimulator },
            _ => new F.MachOSpec { CpuType = 12, CpuSubtype = 9 }, // armv7
        };
        var ipa = F.Ipa(dir, new F.IpaSpec { Main = main, WithExtension = false, WithFramework = false });
        var r = IpaInspector.Inspect(ipa, Opts());
        Assert.Contains(r.Findings, f => f.Code == code && f.Severity == FindingSeverity.Blocker);
        Assert.False(r.CanProceed);
    }

    [Fact]
    public void Inspector_rejects_unsafe_damaged_and_wrong_packages_without_extracting_outside()
    {
        using var dir = new TempDir();
        var unsafeIpa = F.Ipa(dir, new F.IpaSpec { RawEntries = new Dictionary<string, byte[]> { ["../escape.txt"] = [1, 2, 3] } }, "unsafe.ipa");
        Assert.Contains(IpaInspector.Inspect(unsafeIpa, Opts()).Findings, f => f.Code == "ipa.unsafe-path");
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.txt")));

        var notZip = dir.Write("notzip.ipa", "this is not a zip");
        Assert.Contains(IpaInspector.Inspect(notZip, Opts()).Findings, f => f.Code == "ipa.not-zip");

        var noPayload = F.Ipa(dir, new F.IpaSpec { OmitPayload = true }, "nopayload.ipa");
        var np = Assert.Single(IpaInspector.Inspect(noPayload, Opts()).Findings, f => f.Code == "ipa.no-payload");
        Assert.Contains("top level", np.Remedy);

        var deb = dir.Write("tweak.deb", "!<arch>");
        var d = Assert.Single(IpaInspector.Inspect(deb, Opts()).Findings);
        Assert.Equal("ipa.is-deb", d.Code);
        Assert.Contains("HCG Repo", d.Remedy);
    }

    [Fact]
    public void Inspector_explains_unsigned_and_ad_hoc_apps()
    {
        using var dir = new TempDir();
        var unsigned = IpaInspector.Inspect(F.Ipa(dir, new F.IpaSpec(), "u.ipa"), Opts());
        Assert.Contains(unsigned.Findings, f => f.Code == "signature.unsigned");
        Assert.False(unsigned.App!.Signature.Present);
        var adhocSig = F.Signature(F.AppId, null, null, null);
        var adhoc = IpaInspector.Inspect(F.Ipa(dir, new F.IpaSpec { Main = new F.MachOSpec { Signature = adhocSig }, WithExtension = false, WithFramework = false }, "a.ipa"), Opts());
        Assert.True(adhoc.App!.Signature.AdHoc);
        Assert.Contains(adhoc.Findings, f => f.Code == "signature.adhoc");
    }

    // ------------------------------------------------------------------ Signing planner

    private static (IpaReport Ipa, SigningInventorySnapshot Inv, ProfileEntry Exact, ProfileEntry Wild, HCG.ExecutableMaker.Domain.AppleCertificateInfo Dev) PlanSetup(TempDir dir, IReadOnlyList<string>? devices = null, Func<F.IpaSpec, F.IpaSpec>? tweak = null)
    {
        var (ipaPath, cert, _) = SignedIpa(dir, tweak);
        var ipa = IpaInspector.Inspect(ipaPath, Opts());
        var exact = Entry(dir, F.Profile(F.AppId, cert, devices) with { Name = "Exact" });
        var wild = Entry(dir, F.Profile("*", cert, devices) with { Name = "Wildcard", AppId = SigningFixtures.Team + ".*" });
        var dev = SigningFixtures.Info(cert);
        return (ipa, new SigningInventorySnapshot([dev], [exact, wild]), exact, wild, dev);
    }

    private static ProfileEntry Entry(TempDir dir, SigningFixtures.ProfileSpec spec)
    {
        var path = dir.Combine(spec.Uuid + ".mobileprovision");
        File.WriteAllBytes(path, SigningFixtures.Profile(spec));
        var read = IpaInspector.ReadProfile(path, SigningFixtures.TrustedRoots);
        return new ProfileEntry(read.Profile!, read.RawEntitlements, path);
    }

    [Fact]
    public void Planner_chooses_identity_and_the_best_profile_per_bundle_and_composes_entitlements()
    {
        using var dir = new TempDir();
        var (ipa, inv, exact, wild, dev) = PlanSetup(dir);
        var plan = SigningPlanner.Plan(ipa, inv, new SigningRequest { TargetDevice = F.Device() }, Now);

        Assert.True(plan.IsReady, string.Join("; ", plan.Findings.Select(f => f.Title)));
        Assert.Equal(dev.Sha1, plan.Identity!.Sha1);
        Assert.Equal(DistributionMethod.Development, plan.Method);
        Assert.True(plan.Installable);
        Assert.Equal(exact.Info.Uuid, plan.Main!.Profile!.Info.Uuid); // explicit App ID beats the wildcard
        var ext = plan.Assignments.Single(a => a.Bundle.Kind == BundleKind.AppExtension);
        Assert.Equal(wild.Info.Uuid, ext.Profile!.Info.Uuid);
        Assert.Null(plan.Assignments.Single(a => a.Bundle.Kind == BundleKind.Framework).Entitlements); // frameworks: no entitlements
        Assert.Equal(plan.Main, plan.Assignments.Last());

        var ent = plan.Main.Entitlements!;
        Assert.Equal($"{SigningFixtures.Team}.{F.AppId}", ent["application-identifier"]);
        Assert.Equal(SigningFixtures.Team, ent["com.apple.developer.team-identifier"]);
        Assert.Equal(true, ent["get-task-allow"]);
        Assert.Equal([$"{SigningFixtures.Team}.{F.AppId}"], ((List<object>)ent["keychain-access-groups"]).Cast<string>());
        Assert.Equal(exact.Info.Expiration, plan.ExpiresAt);
    }

    [Fact]
    public void Planner_blocks_unregistered_device_expired_profile_missing_key_and_ungranted_entitlement()
    {
        using var dir = new TempDir();
        // Device not in the profile.
        var (ipa, inv, exact, _, dev) = PlanSetup(dir, devices: ["00008030-000000000000AAAA"]);
        var plan = SigningPlanner.Plan(ipa, inv, new SigningRequest { TargetDevice = F.Device(), ProfileUuid = exact.Info.Uuid, IdentitySha1 = dev.Sha1 }, Now);
        Assert.Contains(plan.Findings, f => f.Code == "device.unregistered" && f.Severity == FindingSeverity.Blocker && f.Remedy!.Contains("developer.apple.com"));
        Assert.False(plan.IsReady);

        // No private key → no identity.
        var noKey = inv with { Certificates = [dev with { HasPrivateKey = false }] };
        Assert.Contains(SigningPlanner.Plan(ipa, noKey, new SigningRequest(), Now).Findings, f => f.Code == "identity.none");

        // Expired profiles are not candidates; choosing one explicitly explains why.
        using var dir2 = new TempDir();
        var (ipa2, cert2, _) = SignedIpa(dir2);
        var expired = Entry(dir2, F.Profile("*", cert2, expires: Now.AddDays(-1)) with { AppId = SigningFixtures.Team + ".*" });
        var inv2 = new SigningInventorySnapshot([SigningFixtures.Info(cert2)], [expired]);
        var ip2 = IpaInspector.Inspect(ipa2, Opts());
        Assert.Contains(SigningPlanner.Plan(ip2, inv2, new SigningRequest(), Now).Findings, f => f.Code == "identity.no-profile-match");
        Assert.Contains(SigningPlanner.Plan(ip2, inv2, new SigningRequest { ProfileUuid = expired.Info.Uuid, IdentitySha1 = SigningFixtures.Info(cert2).Sha1 }, Now).Findings, f => f.Code == "profile.expired");

        // An entitlement the profile does not grant blocks — or is removed (reported) when the user chooses so.
        using var dir3 = new TempDir();
        var (ipa3, inv3, _, _, _) = PlanSetup(dir3, tweak: s => s with { ExtraEntitlements = [("com.apple.developer.healthkit", "<true/>")] });
        var blocked = SigningPlanner.Plan(ipa3, inv3, new SigningRequest(), Now);
        Assert.Contains(blocked.Findings, f => f.Code == "entitlement.not-granted" && f.Detail.Contains("healthkit"));
        var dropped = SigningPlanner.Plan(ipa3, inv3, new SigningRequest { DropUnsupportedEntitlements = true }, Now);
        Assert.True(dropped.IsReady);
        Assert.False(dropped.Main!.Entitlements!.ContainsKey("com.apple.developer.healthkit"));
        Assert.Contains(dropped.Findings, f => f.Code == "entitlement.removed");
    }

    [Fact]
    public void Planner_warns_about_bundle_id_changes_and_rebases_extensions()
    {
        using var dir = new TempDir();
        var (ipa, inv, _, _, _) = PlanSetup(dir);
        var plan = SigningPlanner.Plan(ipa, inv, new SigningRequest { NewBundleIdentifier = "com.example.app2" }, Now);
        Assert.True(plan.IsReady, string.Join("; ", plan.Findings.Select(f => f.Detail)));
        Assert.Equal("com.example.app2", plan.Main!.FinalBundleIdentifier);
        Assert.Equal("com.example.app2.share", plan.Assignments.Single(a => a.Bundle.Kind == BundleKind.AppExtension).FinalBundleIdentifier);
        Assert.Equal($"{SigningFixtures.Team}.com.example.app2", plan.Main.Entitlements!["application-identifier"]);
        Assert.Contains(plan.Findings, f => f.Code == "bundle-id.changed" && f.Detail.Contains("data"));
        Assert.Contains(SigningPlanner.Plan(ipa, inv, new SigningRequest { NewBundleIdentifier = "bad id!" }, Now).Findings, f => f.Code == "plan.bundle-id-invalid");
    }

    [Fact]
    public void Planner_distribution_rules_app_store_and_certificate_type()
    {
        using var dir = new TempDir();
        var (ipaPath, devCert, _) = SignedIpa(dir);
        var ipa = IpaInspector.Inspect(ipaPath, Opts());
        var dist = SigningFixtures.Distribution();
        var appStore = Entry(dir, F.Profile("*", dist, devices: [], dev: false) with { AppId = SigningFixtures.Team + ".*", Devices = null, Name = "App Store" });
        var inv = new SigningInventorySnapshot([SigningFixtures.Info(dist)], [appStore]);
        var plan = SigningPlanner.Plan(ipa, inv, new SigningRequest(), Now);
        Assert.Equal(DistributionMethod.AppStore, plan.Method);
        Assert.False(plan.Installable);
        Assert.Contains(plan.Findings, f => f.Code == "method.app-store");
        // The team change from the original signature (same team here) is not reported.
        Assert.DoesNotContain(plan.Findings, f => f.Code == "team.changed");
        // A development certificate is not in a distribution profile.
        var mixed = new SigningInventorySnapshot([SigningFixtures.Info(devCert)], [appStore]);
        Assert.Contains(SigningPlanner.Plan(ipa, mixed, new SigningRequest { IdentitySha1 = SigningFixtures.Info(devCert).Sha1, ProfileUuid = appStore.Info.Uuid }, Now).Findings, f => f.Code == "profile.certificate");
    }

    [Theory]
    [InlineData("ABCDE12345.com.example.app", "com.example.app", true)]
    [InlineData("ABCDE12345.*", "com.example.app.share", true)]
    [InlineData("ABCDE12345.com.example.*", "com.example.app", true)]
    [InlineData("ABCDE12345.com.example.*", "com.other.app", false)]
    [InlineData("ZYXWV98765.*", "com.example.app", false)]
    public void App_id_patterns(string appId, string bundleId, bool covers) =>
        Assert.Equal(covers, SigningPlanner.AppIdCovers(appId, SigningFixtures.Team, bundleId));

    [Fact]
    public void Entitlements_move_to_the_new_team_and_plist_round_trips()
    {
        var moved = EntitlementComposer.Rewrite(new List<object> { "OLDTEAM001.com.example.app", "$(AppIdentifierPrefix)shared", "com.apple.token" }, "OLDTEAM001", "ABCDE12345", null, null);
        Assert.Equal(["ABCDE12345.com.example.app", "ABCDE12345.shared", "com.apple.token"], ((List<object>)moved).Cast<string>());
        var xml = PlistXmlWriter.Write(new Dictionary<string, object> { ["a"] = true, ["b"] = "x<y", ["c"] = new List<object> { "1", "2" }, ["d"] = 5L });
        var back = PlistReader.ParseDictionary(System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Equal(true, back["a"]);
        Assert.Equal("x<y", back["b"]);
        Assert.Equal(["1", "2"], ((List<object>)back["c"]).Cast<string>());
        Assert.Equal(5L, back["d"]);
    }

    // ------------------------------------------------------------------ Devices

    [Fact]
    public void Device_compatibility_explains_every_condition()
    {
        using var dir = new TempDir();
        var (ipaPath, _, _) = SignedIpa(dir, s => s with { MinOs = "19.0", Family = "<integer>2</integer>" });
        var ipa = IpaInspector.Inspect(ipaPath, Opts());
        var codes = DeviceCompatibility.Evaluate(ipa, F.Device(os: "18.2", devMode: DeveloperModeState.Disabled, pairing: PairingState.Unpaired)).Select(f => f.Code).ToList();
        Assert.Contains("device.os-too-old", codes);
        Assert.Contains("device.family", codes);
        Assert.Contains("device.developer-mode", codes);
        Assert.Contains("device.not-paired", codes);
        Assert.Contains(DeviceCompatibility.Evaluate(ipa, F.Device(connection: DeviceConnection.Unavailable)), f => f.Code == "device.unavailable");
        Assert.Contains(DeviceCompatibility.Evaluate(ipa, F.Device(connection: DeviceConnection.Network)), f => f.Code == "device.wireless" && f.Severity == FindingSeverity.Warning);
        Assert.True(DeviceCompatibility.Compare("17.10", "17.9") > 0);
    }

    [Fact]
    public void Install_readiness_checks_signature_profile_and_registration()
    {
        using var dir = new TempDir();
        var (ipaPath, _, _) = SignedIpa(dir);
        var ipa = IpaInspector.Inspect(ipaPath, Opts());
        Assert.DoesNotContain(InstallReadiness.Evaluate(ipa, F.Device(), Now), f => f.Severity == FindingSeverity.Blocker);
        Assert.Contains(InstallReadiness.Evaluate(ipa, F.Device(udid: "00008030-000000000000BBBB"), Now), f => f.Code == "device.unregistered");
        Assert.Contains(InstallReadiness.Evaluate(ipa, F.Device(), Now.AddYears(2)), f => f.Code is "install.profile-expired" or "install.certificate-expired");
        var unsigned = IpaInspector.Inspect(F.Ipa(dir, new F.IpaSpec(), "u.ipa"), Opts());
        Assert.Contains(InstallReadiness.Evaluate(unsigned, F.Device(), Now), f => f.Code == "install.not-signed");
    }

    private const string DevicectlList = """
        {"info":{"arguments":["devicectl","list","devices"],"outcome":"success","version":"397.24"},
         "result":{"devices":[
          {"capabilities":[{"featureIdentifier":"com.apple.coredevice.feature.installapp","name":"Install Application"},
                           {"featureIdentifier":"com.apple.coredevice.feature.launchapplication","name":"Launch Application"}],
           "connectionProperties":{"pairingState":"paired","transportType":"wired","tunnelState":"connected"},
           "deviceProperties":{"name":"Yasin's iPhone","osVersionNumber":"18.2","osBuildUpdate":"22C152","developerModeStatus":"enabled","ddiServicesAvailable":true,"bootState":"booted"},
           "hardwareProperties":{"udid":"00008140-001A2B3C4D5E001E","productType":"iPhone17,1","marketingName":"iPhone 16 Pro","deviceType":"iPhone","platform":"iOS","cpuType":{"name":"arm64e"},"reality":"physical","ecid":1234567890123},
           "identifier":"5E1F2A3B-0000-4000-8000-00000000A11D"},
          {"capabilities":[],"connectionProperties":{"pairingState":"paired","tunnelState":"unavailable"},
           "deviceProperties":{"name":"Old iPad","osVersionNumber":"17.5","developerModeStatus":"disabled"},
           "hardwareProperties":{"udid":"00008027-000A11B22C33D44E","productType":"iPad8,1","deviceType":"iPad","platform":"iOS"},
           "identifier":"7A7A7A7A-0000-4000-8000-00000000B22E"}]}}
        """;

    [Fact]
    public void Devicectl_json_is_parsed_into_the_shared_device_record()
    {
        var devices = DevicectlJson.ParseDevices(DevicectlList, "devicectl 397.24");
        Assert.Equal(2, devices.Count);
        var phone = devices[0];
        Assert.Equal(("Yasin's iPhone", "iPhone 16 Pro", "iPhone17,1", "18.2", "arm64e"), (phone.Name, phone.MarketingName, phone.ProductType, phone.OsVersion, phone.CpuArchitecture));
        Assert.Equal((DeviceConnection.Usb, PairingState.Paired, DeveloperModeState.Enabled), (phone.Connection, phone.Pairing, phone.DeveloperMode));
        Assert.True(phone.CanInstall && phone.CanLaunch && phone.IsPhysical);
        var ipad = devices[1];
        Assert.Equal((DeviceConnection.Unavailable, DeveloperModeState.Disabled), (ipad.Connection, ipad.DeveloperMode));

        var json = AppleDeviceContract.Serialize(devices, "test", Now);
        Assert.DoesNotContain("00008140-001A2B3C4D5E001E", json); // UDIDs redacted unless explicitly included
        Assert.Contains("\"schema\": \"hcg.apple-device/1\"", json);
        var back = AppleDeviceContract.Deserialize(json)!;
        Assert.Equal(phone.Identifier, back.Devices[0].Identifier);
        Assert.Equal(DeviceConnection.Usb, back.Devices[0].Connection);
        Assert.Contains("00008140-001A2B3C4D5E001E", AppleDeviceContract.Serialize(devices, "test", Now, includeUdid: true));

        var failed = DevicectlJson.ReadOutcome("""{"info":{"outcome":"failed"},"error":{"code":3002,"domain":"com.apple.dt.CoreDeviceError","userInfo":{"NSLocalizedDescription":{"string":"Failed to install the app on the device."},"NSUnderlyingError":{"error":{"code":-402620395,"domain":"com.apple.dt.MobileDeviceErrorDomain","userInfo":{"NSLocalizedDescription":{"string":"A valid provisioning profile for this executable was not found."}}}}}}}""");
        Assert.False(failed.Succeeded);
        Assert.Contains("A valid provisioning profile for this executable was not found.", failed.ErrorText);
        Assert.Equal(DiagnosisKind.MissingProvisioningProfile, InstallDiagnostics.Diagnose(failed.ErrorText).Kind);
        Assert.Equal(("com.example.app", "file:///private/var/containers/Bundle/Application/X/Synthetic.app/"),
            DevicectlJson.ParseInstall("""{"info":{"outcome":"success"},"result":{"installedApplications":[{"bundleID":"com.example.app","installationURL":"file:///private/var/containers/Bundle/Application/X/Synthetic.app/"}]}}"""));
        Assert.Equal("1.2.0", DevicectlJson.ParseApps("""{"result":{"apps":[{"bundleIdentifier":"com.example.app","name":"Synthetic","version":"1.2.0","bundleVersion":"42"}]}}""").Single().Version);
    }

    // ------------------------------------------------------------------ Diagnostics

    [Theory]
    [InlineData("ERROR: The executable was signed with invalid entitlements. (0xe8008016)", DiagnosisKind.EntitlementMismatch)]
    [InlineData("A valid provisioning profile for this executable was not found. (0xe8008015)", DiagnosisKind.MissingProvisioningProfile)]
    [InlineData("The identity used to sign the executable is no longer valid. 0xe8008018", DiagnosisKind.ExpiredCertificate)]
    [InlineData("A signed resource has been added, modified, or deleted. (0xe8008017)", DiagnosisKind.ResourcesModified)]
    [InlineData("The application does not have a valid signature.", DiagnosisKind.InvalidSignature)]
    [InlineData("Unable to install: the device is not registered in the provisioning profile", DiagnosisKind.UnregisteredDevice)]
    [InlineData("This app requires a newer version of iOS.", DiagnosisKind.IncompatibleOsVersion)]
    [InlineData("Failed: IncompatibleArchitecture", DiagnosisKind.UnsupportedArchitecture)]
    [InlineData("kAMDMismatchedApplicationIdentifierEntitlementError: upgrade's application-identifier entitlement string does not match", DiagnosisKind.BundleIdentifierMismatch)]
    [InlineData("The device is passcode protected. (0xe80000e2)", DiagnosisKind.DeviceLocked)]
    [InlineData("Developer Mode is disabled on this device.", DiagnosisKind.DeveloperModeRequired)]
    [InlineData("The device is not paired with this host.", DiagnosisKind.DeviceNotPaired)]
    [InlineData("This device's iOS version is not supported by this version of Xcode.", DiagnosisKind.XcodeCompatibility)]
    [InlineData("Installation failed: not enough storage on the device", DiagnosisKind.InsufficientStorage)]
    [InlineData("ERROR: No devices found matching the criteria", DiagnosisKind.DeviceNotFound)]
    [InlineData("Something nobody has seen before", DiagnosisKind.Unknown)]
    public void Installation_failures_are_translated(string output, DiagnosisKind kind)
    {
        var d = InstallDiagnostics.Diagnose(output);
        Assert.Equal(kind, d.Kind);
        Assert.False(string.IsNullOrWhiteSpace(d.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(d.Remedy));
        Assert.Equal(output, d.RawRedacted);
    }

    [Fact]
    public void Diagnostics_redact_device_identifiers_and_personal_data()
    {
        const string raw = "udid 00008140-001A2B3C4D5E001E legacy 0123456789abcdef0123456789abcdef01234567 \"ecid\": 1234567890123 \"serialNumber\": \"F2LXK0ABCD12\" by jane@example.com at /Users/jane/Library and Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456";
        var r = InstallDiagnostics.Redact(raw);
        foreach (var secret in new[] { "001A2B3C4D5E", "0123456789abcdef0123456789abcdef01234567", "1234567890123", "F2LXK0ABCD12", "jane@example.com", "/Users/jane", "abcdefghijklmnopqrstuvwxyz123456" })
            Assert.DoesNotContain(secret, r);
        Assert.Contains("0000…001E", r);
        Assert.Contains("/Users/[user]", r);
        Assert.Equal(DiagnosisKind.Cancelled, InstallDiagnostics.Diagnose("", cancelled: true).Kind);
        Assert.Equal(DiagnosisKind.TimedOut, InstallDiagnostics.Diagnose("…", timedOut: true).Kind);
        Assert.Equal(DiagnosisKind.ToolMissing, InstallDiagnostics.Diagnose("", toolMissing: true).Kind);
    }

    // ------------------------------------------------------------------ Workflow (fake device + signer)

    private sealed class FakeSigner(string appDir) : IAppSigner
    {
        public bool IsAvailable => true;
        public int Unpacks;
        public Task<SignResult> SignAsync(SignJob job, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(new SignResult { Succeeded = true, OutputIpa = job.SourceIpa, OutputSha256 = "ab", SignedAppDirectory = appDir, Reinspection = job.Inspection });
        public Task<SignResult> UnpackAsync(string ipa, CancellationToken ct) { Unpacks++; return Task.FromResult(new SignResult { Succeeded = true, SignedAppDirectory = appDir }); }
        public void Cleanup(SignResult result) { }
    }

    private sealed class FakeDevices(InstallResult result, InstalledApp? existing = null) : IDeviceService
    {
        public bool IsAvailable => true;
        public List<string> Installs { get; } = [];
        public int Launches;
        public Task<DeviceListing> ListAsync(CancellationToken ct) => Task.FromResult(new DeviceListing([F.Device()], null, "synthetic", ""));
        public Task<InstalledApp?> FindInstalledAsync(AppleDevice d, string id, CancellationToken ct) => Task.FromResult(existing);
        public Task<InstallResult> InstallAsync(AppleDevice d, string app, IProgress<InstallProgress>? p, CancellationToken ct)
        {
            Installs.Add(d.Name);
            p?.Report(new InstallProgress(50, "Copying"));
            return Task.FromResult(result);
        }
        public Task<LaunchResult> LaunchAsync(AppleDevice d, string id, CancellationToken ct) { Launches++; return Task.FromResult(new LaunchResult(true, null, "")); }
    }

    private sealed class FakeInventory(SigningInventorySnapshot inv) : ISigningInventorySource
    {
        public string? LastProblem => null;
        public Task<SigningInventorySnapshot> ReadAsync(IReadOnlyList<string>? extra, CancellationToken ct) => Task.FromResult(inv);
    }

    [Fact]
    public async Task Deployment_validates_installs_launches_and_records_history()
    {
        using var dir = new TempDir();
        var (ipa, _, _) = SignedIpa(dir);
        var store = new ImpactorStore(dir.Combine("data"));
        var devices = new FakeDevices(new InstallResult { Succeeded = true, InstalledBundleIdentifier = F.AppId, Duration = TimeSpan.FromSeconds(7) });
        var service = new ImpactorService(new FakeSigner(dir.Path), devices, new FakeInventory(new([], [])), store, new FixedTime(Now));

        var progress = new List<int?>();
        var r = await service.DeployAsync(new DeploymentItem(ipa, F.Device()) { LaunchAfterInstall = true, WarningsAcknowledged = true }, new SyncProgress<InstallProgress>(p => progress.Add(p.Percent)), CancellationToken.None);
        Assert.Equal(InstallOutcome.Installed, r.Outcome);
        Assert.Contains("launched", r.Summary);
        Assert.Equal(1, devices.Launches);
        Assert.Contains(50, progress);
        var h = Assert.Single(store.History());
        Assert.Equal((InstallOutcome.Installed, "Synthetic iPhone", "0000…001E"), (h.Outcome, h.DeviceName, h.DeviceUdidRedacted));
        Assert.True(h.Launched);
    }

    [Fact]
    public async Task Deployment_never_touches_the_device_when_blocked_and_waits_for_confirmed_warnings()
    {
        using var dir = new TempDir();
        var (ipa, _, _) = SignedIpa(dir);
        var store = new ImpactorStore(dir.Combine("data"));
        var devices = new FakeDevices(new InstallResult { Succeeded = true }, existing: new InstalledApp(F.AppId, "Synthetic", "1.0", "1"));
        var service = new ImpactorService(new FakeSigner(dir.Path), devices, new FakeInventory(new([], [])), store, new FixedTime(Now));

        var blocked = await service.DeployAsync(new DeploymentItem(ipa, F.Device(udid: "00008030-000000000000BBBB")), null, CancellationToken.None);
        Assert.Equal(InstallOutcome.Blocked, blocked.Outcome);
        Assert.Empty(devices.Installs);
        Assert.Equal(InstallOutcome.Blocked, store.History().Single().Outcome);

        var waiting = await service.DeployAsync(new DeploymentItem(ipa, F.Device()), null, CancellationToken.None);
        Assert.Contains(waiting.Checks, f => f.Code == "install.existing-app" && f.Detail.Contains("never removes"));
        Assert.Empty(devices.Installs); // warnings need confirmation
        var go = await service.DeployAsync(new DeploymentItem(ipa, F.Device()) { WarningsAcknowledged = true }, null, CancellationToken.None);
        Assert.Equal(InstallOutcome.Installed, go.Outcome);
    }

    [Fact]
    public async Task Batch_installation_continues_after_a_failure_and_explains_it()
    {
        using var dir = new TempDir();
        var (ipa, _, _) = SignedIpa(dir);
        var store = new ImpactorStore(dir.Combine("data"));
        var failure = new InstallResult { Succeeded = false, Failure = InstallDiagnostics.Diagnose("The device is passcode protected. (0xe80000e2)") };
        var service = new ImpactorService(new FakeSigner(dir.Path), new FakeDevices(failure), new FakeInventory(new([], [])), store, new FixedTime(Now));
        var results = await service.DeployAllAsync([new(ipa, F.Device()) { WarningsAcknowledged = true }, new(ipa, F.Device() with { Name = "Second iPhone" }) { WarningsAcknowledged = true }], null, CancellationToken.None);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(InstallOutcome.Failed, r.Outcome));
        Assert.All(store.History(), h => Assert.Equal(nameof(DiagnosisKind.DeviceLocked), h.DiagnosisKind));
        Assert.Contains("locked", results[0].Summary);
    }

    [Fact]
    public void Signing_monitor_reminds_before_expiry_and_store_survives_corruption()
    {
        using var dir = new TempDir();
        var store = new ImpactorStore(dir.Path);
        store.Add(new SignedAppRecord { AppName = "Soon", BundleIdentifier = "a.soon", SignedSha256 = "1", SignedAt = Now, ExpiresAt = Now.AddDays(5) });
        store.Add(new SignedAppRecord { AppName = "Gone", BundleIdentifier = "a.gone", SignedSha256 = "2", SignedAt = Now, ExpiresAt = Now.AddDays(-1) });
        store.Add(new SignedAppRecord { AppName = "Fine", BundleIdentifier = "a.fine", SignedSha256 = "3", SignedAt = Now, ExpiresAt = Now.AddDays(200) });
        var reminders = store.Reminders(Now);
        Assert.Equal(["Gone", "Soon"], reminders.Select(r => r.App.AppName));
        Assert.Equal(["expired", "soon"], reminders.Select(r => r.Level));
        File.WriteAllText(Path.Combine(store.Directory, "install-history.json"), "{ not json");
        Assert.Empty(store.History());
        Assert.Single(Directory.GetFiles(store.Directory, "install-history.json.corrupt-*"));
    }

    [Fact]
    public void Diagnostic_export_uses_the_shared_report_format_and_is_redacted()
    {
        using var dir = new TempDir();
        var (ipa, _, _) = SignedIpa(dir);
        var session = new ImpactorSession
        {
            Inspection = IpaInspector.Inspect(ipa, Opts()),
            Device = F.Device(),
            Install = new InstallResult { Succeeded = false, Failure = InstallDiagnostics.Diagnose("udid 00008140-001A2B3C4D5E001E: Developer Mode is disabled"), RawRedacted = InstallDiagnostics.Redact("udid 00008140-001A2B3C4D5E001E: Developer Mode is disabled") },
            Capabilities = ImpactorCapabilities.Detect(false, false),
        };
        var folder = ImpactorReport.Export(session, dir.Path, "0.5.0-test", "install-1", Now);
        var json = File.ReadAllText(Path.Combine(folder, "report.json"));
        var md = File.ReadAllText(Path.Combine(folder, "report.md"));
        Assert.Contains("\"schema\": \"hcg.diagnostic-report/1\"", json);
        Assert.Contains("\"policy\": \"hcg.redaction/1\"", json);
        Assert.Contains("\"status\": \"issues-found\"", json);
        Assert.Contains("Developer Mode required", json);
        Assert.DoesNotContain("001A2B3C4D5E", json + md);
        Assert.Contains("## Installation (original output, redacted)", md);
    }

    // ------------------------------------------------------------------ CLI

    [Fact]
    public async Task Cli_inspect_and_plan_print_reports_and_exit_codes()
    {
        using var dir = new TempDir();
        var (ipa, inv, _, _, _) = PlanSetup(dir);
        var service = new ImpactorService(new FakeSigner(dir.Path), new FakeDevices(new InstallResult { Succeeded = true }), new FakeInventory(inv), new ImpactorStore(dir.Combine("data")), new FixedTime(Now));
        var outText = new StringWriter();
        var cli = new ImpactorCli(service, outText, "0.5.0-test", "id");
        Assert.Equal(0, await cli.RunAsync(["inspect", ipa.SourcePath], CancellationToken.None));
        Assert.Contains("Version 1.2.0 (build 42) · minimum iOS 16.0", outText.ToString());
        Assert.Contains("AppExtension: PlugIns/Share.appex", outText.ToString());

        outText.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["plan", ipa.SourcePath, "--device", "usb", "--report", dir.Combine("reports")], CancellationToken.None));
        var text = outText.ToString();
        Assert.Contains("Ready to sign.", text);
        Assert.Contains("profile \"Exact\"", text);
        Assert.Contains("Diagnostics exported", text);
        Assert.Single(Directory.GetDirectories(dir.Combine("reports")));
        Assert.Equal(2, await new ImpactorCli(service, TextWriter.Null, "t", "i").RunAsync(["bogus"], CancellationToken.None));
    }

    [Fact]
    public void Capabilities_are_honest_about_platform_limits()
    {
        var c = ImpactorCapabilities.Detect(codesign: false, devicectl: false);
        Assert.True(c.CanInspect);
        Assert.False(c.CanSign);
        Assert.NotNull(c.Limitation);
        if (!OperatingSystem.IsMacOS()) Assert.Contains("has not been implemented or tested", c.Limitation);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
