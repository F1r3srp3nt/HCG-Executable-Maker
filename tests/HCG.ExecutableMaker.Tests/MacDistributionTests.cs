using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Swift;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>macOS distribution: parsers, entitlement planning, capability states, .pkg generation and inspection (no Mac needed).</summary>
public sealed class MacDistributionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private const string Team = "ABCDE12345";

    // ───────────── Signing inventory ─────────────

    [Fact]
    public void Identities_include_installer_certificates_and_are_classified()
    {
        const string output = """
              1) 1111111111111111111111111111111111111111 "Developer ID Application: Jane Doe (ABCDE12345)"
              2) 2222222222222222222222222222222222222222 "Developer ID Installer: Jane Doe (ABCDE12345)"
              3) 3333333333333333333333333333333333333333 "Apple Distribution: Jane Doe (ABCDE12345)"
              4) 4444444444444444444444444444444444444444 "3rd Party Mac Developer Installer: Jane Doe (ABCDE12345)"
              5) 5555555555555555555555555555555555555555 "Apple Development: jane@example.com (XYZ9876543)"
              6) 6666666666666666666666666666666666666666 "Some Internal CA Client"
                 6 valid identities found
            """;
        var ids = MacSigningInventoryReader.ParseIdentities(output);
        Assert.Equal(
            [AppleCertificateKind.DeveloperIdApplication, AppleCertificateKind.DeveloperIdInstaller, AppleCertificateKind.AppleDistribution,
             AppleCertificateKind.MacInstallerDistribution, AppleCertificateKind.Development, AppleCertificateKind.Other],
            ids.Select(i => i.Kind));
        Assert.Equal(Team, ids[0].TeamId);
        Assert.True(ids[1].IsInstaller && ids[3].IsInstaller);
        Assert.True(ids[2].SignsMacAppStoreApps);
        Assert.Equal("XYZ9876543", ids[4].TeamId);
        Assert.Null(ids[5].TeamId);
    }

    internal static byte[] FakeProfile(string name, string appId, bool allDevices = false, bool devices = false, string certName = "Apple Distribution: Jane Doe (ABCDE12345)",
        DateTime? expires = null, X509Certificate2? cert = null)
    {
        cert ??= SelfSigned(certName);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Name</key><string>{name}</string>
              <key>UUID</key><string>{Guid.NewGuid()}</string>
              <key>TeamIdentifier</key><array><string>{Team}</string></array>
              <key>TeamName</key><string>Jane Doe</string>
              <key>Platform</key><array><string>OSX</string></array>
              <key>ExpirationDate</key><date>{(expires ?? DateTime.UtcNow.AddYears(1)):yyyy-MM-ddTHH:mm:ssZ}</date>
              <key>DeveloperCertificates</key><array><data>{Convert.ToBase64String(cert.RawData)}</data></array>
              <key>Entitlements</key><dict>
                <key>com.apple.application-identifier</key><string>{Team}.{appId}</string>
                <key>com.apple.developer.team-identifier</key><string>{Team}</string>
                <key>keychain-access-groups</key><array><string>{Team}.*</string></array>
              </dict>
              {(allDevices ? "<key>ProvisionsAllDevices</key><true/>" : "")}
              {(devices ? "<key>ProvisionedDevices</key><array><string>00000000-0000-0000-0000-000000000000</string></array>" : "")}
            </dict></plist>
            """;
        // Real profiles are CMS-signed: the plist sits between binary DER bytes.
        return [0x30, 0x82, 0x01, 0x02, .. Encoding.UTF8.GetBytes(xml), 0xA0, 0x00, 0x31, 0x00];
    }

    internal static X509Certificate2 SelfSigned(string cn)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void Profiles_are_parsed_typed_and_matched_to_bundle_ids()
    {
        var cert = SelfSigned("Apple Distribution: Jane Doe (ABCDE12345)");
        var store = MacSigningInventoryReader.ParseProfile(FakeProfile("Distil MAS", "dev.tokenlab.prototype", cert: cert), "/p/a.provisionprofile")!;
        Assert.Equal(MacProfileKind.MacAppStore, store.Kind);
        Assert.Equal(Team, store.TeamId);
        Assert.Equal($"{Team}.dev.tokenlab.prototype", store.ApplicationIdentifier);
        Assert.True(store.Matches("dev.tokenlab.prototype"));
        Assert.False(store.Matches("dev.tokenlab.other"));
        Assert.Contains("keychain-access-groups", store.EntitlementKeys);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(cert.RawData)), store.CertificateSha1s.Single());
        Assert.Equal(["OSX"], store.Platforms);

        var wildcard = MacSigningInventoryReader.ParseProfile(FakeProfile("Wildcard", "dev.tokenlab.*"), "/p/b")!;
        Assert.True(wildcard.Matches("dev.tokenlab.prototype"));
        Assert.False(wildcard.Matches("com.other.app"));

        Assert.Equal(MacProfileKind.DeveloperId, MacSigningInventoryReader.ParseProfile(FakeProfile("DevID", "x.y", allDevices: true, certName: "Developer ID Application: Jane Doe (ABCDE12345)"), "/p/c")!.Kind);
        Assert.Equal(MacProfileKind.Development, MacSigningInventoryReader.ParseProfile(FakeProfile("Dev", "x.y", devices: true, certName: "Apple Development: jane (ABCDE12345)"), "/p/d")!.Kind);
        var expired = MacSigningInventoryReader.ParseProfile(FakeProfile("Old", "x.y", expires: DateTime.UtcNow.AddDays(-2)), "/p/e")!;
        Assert.True(expired.IsExpired(DateTimeOffset.UtcNow));
        Assert.Null(MacSigningInventoryReader.ParseProfile(Encoding.UTF8.GetBytes("not a profile"), "/p/f"));
    }

    [Fact]
    public void Profiles_are_read_from_folders_and_deduplicated()
    {
        var bytes = FakeProfile("Distil MAS", "dev.tokenlab.prototype");
        File.WriteAllBytes(_t.Combine("a.provisionprofile"), bytes);
        Directory.CreateDirectory(_t.Combine("other"));
        File.WriteAllBytes(_t.Combine("other", "same.provisionprofile"), bytes);   // same UUID
        File.WriteAllBytes(_t.Combine("ios.mobileprovision"), bytes);              // iOS profiles are ignored
        Assert.Single(MacSigningInventoryReader.ReadProfiles([_t.Path, _t.Combine("other"), _t.Combine("missing")]));
    }

    // ───────────── Entitlement evidence ─────────────

    private SwiftTargetSourceFacts Facts(string source)
    {
        _t.Write("pkg/Sources/App/App.swift", source);
        return SwiftPackageScanner.FactsFor(SourceIndex.Build(_t.Path), "pkg", "App", null);
    }

    [Fact]
    public void Sources_give_evidence_for_entitlements_reviews_and_blockers()
    {
        var facts = Facts("""
            import SwiftUI
            import AVFoundation
            @main struct RelayApp: App { var body: some Scene { WindowGroup { Text("x") } } }
            final class Recorder { let r = try? AVAudioRecorder(url: URL(fileURLWithPath: "/tmp/x"), settings: [:]) }
            func fetch() async { _ = try? await URLSession.shared.data(from: URL(string: "https://example.com")!) }
            func save() { let p = NSSavePanel(); _ = p.runModal() }
            func run() { let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/bin/git") }
            // AXIsProcessTrusted() in a comment is not evidence
            """);
        var ev = facts.EntitlementEvidence;
        Assert.Contains(ev, e => e.Capability == "Microphone" && e.SandboxEntitlement == "com.apple.security.device.microphone" && e.HardenedRuntimeEntitlement == "com.apple.security.device.audio-input");
        Assert.Contains(ev, e => e.SandboxEntitlement == "com.apple.security.network.client" && e.HardenedRuntimeEntitlement is null);
        Assert.Contains(ev, e => e.SandboxEntitlement == "com.apple.security.files.user-selected.read-write");
        Assert.Contains(ev, e => e.Capability == "Running other programs" && e.Kind == EntitlementEvidenceKind.Review);
        Assert.DoesNotContain(ev, e => e.Kind == EntitlementEvidenceKind.SandboxBlocker);
        Assert.All(ev, e => Assert.Contains("App.swift:", e.Evidence));

        var devId = Entitlements.ForDeveloperId(ev);
        Assert.Equal(["com.apple.security.device.audio-input"], devId.Proposed.Select(p => p.Key));   // network needs nothing outside the sandbox

        var store = Entitlements.ForAppStore(ev);
        Assert.Equal(["com.apple.security.device.microphone", "com.apple.security.files.user-selected.read-write", "com.apple.security.network.client"], store.Proposed.Select(p => p.Key));
        Assert.Single(store.Reviews);
        Assert.Empty(store.Blockers);

        Assert.Equal(["com.apple.security.device.microphone"], Entitlements.Disabled(store, ["com.apple.security.network.client", "com.apple.security.files.user-selected.read-write"]).Select(p => p.Key));
        Assert.Equal(["com.apple.security.cs.allow-jit"], Entitlements.Unexpected(store, ["com.apple.security.network.client", "com.apple.security.cs.allow-jit"]));
    }

    [Fact]
    public void Sandbox_blockers_are_found()
    {
        var ev = Facts("""
            import AppKit
            import Sparkle
            func check() -> Bool { AXIsProcessTrusted() }
            """).EntitlementEvidence;
        Assert.Equal(["Accessibility control of other apps", "Own update mechanism"],
            ev.Where(e => e.Kind == EntitlementEvidenceKind.SandboxBlocker).Select(e => e.Capability).Order());
    }

    [Fact]
    public void Entitlements_plist_has_booleans_and_strings_escaped_and_sorted()
    {
        var plist = Entitlements.ToPlist(["com.apple.security.network.client", Entitlements.AppSandbox, Entitlements.AppSandbox],
            [(Entitlements.ApplicationIdentifier, $"{Team}.dev.a&b")]);
        Assert.Equal(1, plist.Split(Entitlements.AppSandbox).Length - 1);
        Assert.Contains($"<string>{Team}.dev.a&amp;b</string>", plist);
        Assert.True(plist.IndexOf("com.apple.application-identifier", StringComparison.Ordinal) < plist.IndexOf(Entitlements.AppSandbox, StringComparison.Ordinal));
        var parsed = HCG.ExecutableMaker.Packaging.Mobile.PlistReader.ParseDictionary(Encoding.UTF8.GetBytes(plist));
        Assert.Equal(true, parsed[Entitlements.AppSandbox]);
    }

    // ───────────── codesign / notarytool output ─────────────

    [Fact]
    public void Codesign_description_is_parsed()
    {
        var info = MacCodeSigner.ParseSignatureInfo("""
            Executable=/x/Distil.app/Contents/MacOS/TokenLab
            Identifier=dev.tokenlab.prototype
            CodeDirectory v=20500 size=1234 flags=0x10000(runtime) hashes=30+7 location=embedded
            Authority=Developer ID Application: Jane Doe (ABCDE12345)
            Authority=Developer ID Certification Authority
            Authority=Apple Root CA
            Timestamp=8 Nov 2026 at 10:00:00
            TeamIdentifier=ABCDE12345
            """);
        Assert.Equal("Developer ID Application: Jane Doe (ABCDE12345)", info.Leaf);
        Assert.True(info.HardenedRuntime && info.SecureTimestamp && !info.AdHoc);
        Assert.Equal(Team, info.TeamId);

        var adhoc = MacCodeSigner.ParseSignatureInfo("Identifier=x\nCodeDirectory v=20400 size=1 flags=0x2(adhoc) hashes=1\nSignature=adhoc\nTeamIdentifier=not set\n");
        Assert.True(adhoc.AdHoc);
        Assert.Null(adhoc.TeamId);
        Assert.False(adhoc.HardenedRuntime || adhoc.SecureTimestamp);
    }

    [Fact]
    public void Notarytool_submission_json_is_parsed()
    {
        Assert.Equal(("2efe2717-52ef-43a5-96dc-0797e4ca1041", "Accepted", "Processing complete"),
            NotarizationService.ParseSubmission("Conducting pre-submission checks…\n{\"id\":\"2efe2717-52ef-43a5-96dc-0797e4ca1041\",\"status\":\"Accepted\",\"message\":\"Processing complete\"}"));
        Assert.Equal("Invalid", NotarizationService.ParseSubmission("{\"id\":\"1\",\"status\":\"Invalid\",\"message\":\"Processing complete\"}").Status);
        Assert.Null(NotarizationService.ParseSubmission("Error: HTTP status code: 401").Status);
    }

    [Fact]
    public void Store_credentials_command_never_contains_a_password()
    {
        var cmd = AppleCredentialsService.StoreCredentialsCommand("HCG-Notary", "jane@example.com", Team);
        Assert.Equal($"xcrun notarytool store-credentials \"HCG-Notary\" --apple-id \"jane@example.com\" --team-id \"{Team}\"", cmd);
        Assert.DoesNotContain("--password", cmd);
    }

    // ───────────── .pkg ─────────────

    [Fact]
    public void Component_plist_is_not_relocatable_and_distribution_has_no_scripts()
    {
        var component = HCG.ExecutableMaker.Packaging.Mobile.PlistReader.Parse(Encoding.UTF8.GetBytes(MacPkgPackager.ComponentPlist("Distil.app"))) as List<object>;
        var entry = (Dictionary<string, object>)component!.Single();
        Assert.Equal(false, entry["BundleIsRelocatable"]);
        Assert.Equal("Distil.app", entry["RootRelativeBundlePath"]);

        var xml = System.Xml.Linq.XDocument.Parse(MacPkgPackager.DistributionXml("Distil & Co", "dev.tokenlab.prototype", "0.4.0", "component.pkg", ["arm64", "x86_64"], "14.0")).Root!;
        Assert.Equal("Distil & Co", xml.Element("title")!.Value);
        Assert.Equal("false", xml.Element("options")!.Attribute("require-scripts")!.Value);
        Assert.Equal("arm64,x86_64", xml.Element("options")!.Attribute("hostArchitectures")!.Value);
        Assert.Equal("true", xml.Element("domains")!.Attribute("enable_currentUserHome")!.Value);
        Assert.Equal("false", xml.Element("domains")!.Attribute("enable_anywhere")!.Value);
        Assert.Equal("14.0", xml.Descendants("os-version").Single().Attribute("min")!.Value);
        Assert.Equal("component.pkg", xml.Elements("pkg-ref").Single(e => e.Value.Length > 0).Value);
    }

    private string FakeApp(string root, string name = "Distil.app")
    {
        var app = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), InfoPlist.Create([("CFBundleIdentifier", "dev.tokenlab.prototype"), ("CFBundleShortVersionString", "0.4.0"), ("CFBundleExecutable", "TokenLab")]));
        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "TokenLab"), [0xCF, 0xFA, 0xED, 0xFE, 1, 2, 3]);
        FileOps.MarkExecutable(Path.Combine(app, "Contents", "MacOS", "TokenLab"));
        Directory.CreateDirectory(Path.Combine(app, "Contents", "_CodeSignature"));
        File.WriteAllText(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources"), "<plist/>");
        return app;
    }

    private string FakeExpandedPkg(string builtApp, string? version = "0.4.0", bool scripts = false, Action<string>? tamper = null)
    {
        var x = _t.Combine("expanded");
        var component = Path.Combine(x, "component.pkg");
        Directory.CreateDirectory(Path.Combine(component, "Payload"));
        File.WriteAllText(Path.Combine(x, "Distribution"), "<installer-gui-script/>");
        File.WriteAllText(Path.Combine(component, "PackageInfo"),
            $"<pkg-info format-version=\"2\" identifier=\"dev.tokenlab.prototype\" version=\"{version}\" install-location=\"/Applications\" auth=\"root\"><payload numberOfFiles=\"5\"/><bundle id=\"dev.tokenlab.prototype\" path=\"./Distil.app\"/><relocate/></pkg-info>");
        if (scripts) { Directory.CreateDirectory(Path.Combine(component, "Scripts")); File.WriteAllText(Path.Combine(component, "Scripts", "postinstall"), "#!/bin/sh"); }
        FileOps.CopyDirectory(builtApp, Path.Combine(component, "Payload", Path.GetFileName(builtApp)), CancellationToken.None);
        tamper?.Invoke(Path.Combine(component, "Payload", Path.GetFileName(builtApp)));
        return x;
    }

    [UnixFact]
    public void Expanded_package_is_checked_against_the_built_app()
    {
        var app = FakeApp(_t.Combine("out"));
        var ok = PkgInspector.InspectExpanded(FakeExpandedPkg(app), app, "dev.tokenlab.prototype", "0.4.0");
        Assert.All(ok, c => Assert.Equal(VerificationStatus.Passed, c.Status));

        Directory.Delete(_t.Combine("expanded"), true);
        var bad = PkgInspector.InspectExpanded(FakeExpandedPkg(app, version: "0.3.0", scripts: true,
            tamper: payload => File.SetUnixFileMode(Path.Combine(payload, "Contents", "MacOS", "TokenLab"), UnixFileMode.UserRead | UnixFileMode.UserWrite)), app, "dev.tokenlab.prototype", "0.4.0");
        Assert.Equal(VerificationStatus.Failed, bad.Single(c => c.Name == "Installer scripts").Status);
        Assert.Equal(VerificationStatus.Failed, bad.Single(c => c.Name == "Package identity").Status);
        var payload = bad.Single(c => c.Name == "Package payload");
        Assert.Equal(VerificationStatus.Failed, payload.Status);
        Assert.Contains("Contents/MacOS/TokenLab", payload.Detail);   // lost executable bit is detected
    }

    [Fact]
    public void Pkg_signature_output_is_interpreted_per_expected_signer()
    {
        Assert.Equal(VerificationStatus.PassedWithWarnings, PkgInspector.SignatureCheck("Package \"x.pkg\":\n   Status: no signature\n", PkgInspector.ExpectedSigner.None).Status);
        const string devId = "Package \"x.pkg\":\n   Status: signed by a developer certificate issued by Apple for distribution\n   Certificate Chain:\n    1. Developer ID Installer: Jane Doe (ABCDE12345)\n       Expires: 2030-01-01\n    2. Developer ID Certification Authority\n";
        Assert.Equal(VerificationStatus.Passed, PkgInspector.SignatureCheck(devId, PkgInspector.ExpectedSigner.DeveloperIdInstaller).Status);
        Assert.Equal(VerificationStatus.Failed, PkgInspector.SignatureCheck(devId, PkgInspector.ExpectedSigner.MacInstallerDistribution).Status);
        Assert.Equal(VerificationStatus.Failed, PkgInspector.SignatureCheck("   Status: no signature", PkgInspector.ExpectedSigner.DeveloperIdInstaller).Status);
        Assert.Equal(VerificationStatus.Passed, PkgInspector.SignatureCheck("Status: signed by a certificate trusted by macOS\n    1. 3rd Party Mac Developer Installer: Jane Doe (ABCDE12345)\n", PkgInspector.ExpectedSigner.MacInstallerDistribution).Status);
    }

    [UnixFact]
    public void Bundle_comparison_detects_missing_changed_and_extra_files()
    {
        var a = FakeApp(_t.Combine("a"));
        var b = _t.Combine("b", "Distil.app");
        FileOps.CopyDirectory(a, b, CancellationToken.None);
        Assert.Equal(VerificationStatus.Passed, BundleComparison.Compare(a, b, "x", "copy").Status);
        File.AppendAllText(Path.Combine(b, "Contents", "_CodeSignature", "CodeResources"), " ");
        File.WriteAllText(Path.Combine(b, "Contents", "extra.txt"), "x");
        var r = BundleComparison.Compare(a, b, "x", "copy");
        Assert.Equal(VerificationStatus.Failed, r.Status);
        Assert.Contains("Changed: Contents/_CodeSignature/CodeResources", r.Detail);
        Assert.Contains("Extra: Contents/extra.txt", r.Detail);
        Assert.Equal(VerificationStatus.Failed, BundleComparison.Compare(a, _t.Combine("nope"), "x", "copy").Status);
    }

    // ───────────── Capability states ─────────────

    private static readonly HostEnvironment Mac = new(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "m");
    private static readonly HostEnvironment Linux = new(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "m");

    private static MacSigningInventory Inventory(bool devId = true, bool installer = true, bool distribution = true, bool masInstaller = true, MacProvisioningProfile? profile = null) => new()
    {
        Identities =
        [
            .. devId ? [new SigningIdentity("1111111111111111111111111111111111111111", "Developer ID Application: Jane Doe (ABCDE12345)", AppleCertificateKind.DeveloperIdApplication, Team)] : Array.Empty<SigningIdentity>(),
            .. installer ? [new SigningIdentity("2222222222222222222222222222222222222222", "Developer ID Installer: Jane Doe (ABCDE12345)", AppleCertificateKind.DeveloperIdInstaller, Team)] : Array.Empty<SigningIdentity>(),
            .. distribution ? [new SigningIdentity("3333333333333333333333333333333333333333", "Apple Distribution: Jane Doe (ABCDE12345)", AppleCertificateKind.AppleDistribution, Team)] : Array.Empty<SigningIdentity>(),
            .. masInstaller ? [new SigningIdentity("4444444444444444444444444444444444444444", "3rd Party Mac Developer Installer: Jane Doe (ABCDE12345)", AppleCertificateKind.MacInstallerDistribution, Team)] : Array.Empty<SigningIdentity>(),
        ],
        Profiles = profile is null ? [] : [profile],
    };

    private static MacProvisioningProfile StoreProfile(string appId = "dev.tokenlab.prototype") => new()
    {
        Path = "/p/mas.provisionprofile", Name = "Distil MAS", TeamId = Team, ApplicationIdentifier = $"{Team}.{appId}",
        Kind = MacProfileKind.MacAppStore, Expiration = DateTimeOffset.UtcNow.AddYears(1), CertificateSha1s = ["3333333333333333333333333333333333333333"],
    };

    private static PackagingContext Ctx(HostEnvironment host, MacSigningInventory? inv = null, MacDistributionOptions? d = null, bool rootBundles = false,
        IReadOnlyList<EntitlementEvidence>? evidence = null, SwiftAppKind kind = SwiftAppKind.GuiApp) =>
        new(TargetPlatform.MacOS, host, null, kind == SwiftAppKind.GuiApp, true, kind)
        {
            Signing = inv, Distribution = d, BundleId = "dev.tokenlab.prototype", HasRootResourceBundles = rootBundles, Evidence = evidence ?? [],
        };

    private static PackagingOption Option(PackagingContext ctx, PackagingFormat f) => PackagingCatalog.Evaluate(ctx).Single(o => o.Format == f);

    [Fact]
    public void No_milestone_placeholders_remain_for_macOS()
    {
        foreach (var ctx in new[] { Ctx(Mac, Inventory()), Ctx(Linux), Ctx(Mac, kind: SwiftAppKind.CommandLine) })
            Assert.DoesNotContain(PackagingCatalog.Evaluate(ctx), o => o.Availability == PackagingAvailability.NotImplemented);
        var dotNet = PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.MacOS, Mac, null, IsGuiApplication: true));
        Assert.DoesNotContain(dotNet, o => o.Availability == PackagingAvailability.NotImplemented || (o.Reason ?? "").Contains("M0."));
        Assert.Equal(PackagingAvailability.NotApplicable, dotNet.Single(o => o.Format == PackagingFormat.MacAppStore).Availability);
        Assert.Equal(PackagingAvailability.Available, dotNet.Single(o => o.Format == PackagingFormat.MacPkg).Availability);
    }

    [Fact]
    public void Pkg_states()
    {
        var unsigned = Option(Ctx(Mac, Inventory(installer: false)), PackagingFormat.MacPkg);
        Assert.Equal(PackagingAvailability.Available, unsigned.Availability);
        Assert.Contains("Unsigned: for local testing only", unsigned.Reason);
        Assert.Contains("none found", unsigned.Reason);

        var signedAdHoc = Option(Ctx(Mac, Inventory(), new MacDistributionOptions { PkgInstallerSha1 = "2222222222222222222222222222222222222222" }), PackagingFormat.MacPkg);
        Assert.Equal(PackagingAvailability.SetupRequired, signedAdHoc.Availability);

        var signed = Option(Ctx(Mac, Inventory(), new MacDistributionOptions { SigningMode = MacSigningMode.DeveloperId, PkgInstallerSha1 = "2222222222222222222222222222222222222222" }), PackagingFormat.MacPkg);
        Assert.Equal(PackagingAvailability.Available, signed.Availability);
        Assert.Contains("Developer ID Installer: Jane Doe", signed.Reason);

        Assert.Equal(PackagingAvailability.RequiresDifferentHost, Option(Ctx(Linux), PackagingFormat.MacPkg).Availability);
        Assert.Equal(PackagingAvailability.NotApplicable, Option(Ctx(Mac, kind: SwiftAppKind.CommandLine), PackagingFormat.MacPkg).Availability);
    }

    [Fact]
    public void App_Store_states_walk_from_setup_to_ready()
    {
        var none = Option(Ctx(Mac, Inventory(distribution: false, masInstaller: false)), PackagingFormat.MacAppStore);
        Assert.Equal(PackagingAvailability.SetupRequired, none.Availability);
        Assert.Contains("Apple Distribution certificate", none.Reason);
        Assert.Contains("Mac Installer Distribution", none.Reason);
        Assert.Contains("provisioning profile for dev.tokenlab.prototype", none.Reason);

        var wrongProfile = Option(Ctx(Mac, Inventory(profile: StoreProfile("com.other"))), PackagingFormat.MacAppStore);
        Assert.Equal(PackagingAvailability.SetupRequired, wrongProfile.Availability);

        var chosen = new MacDistributionOptions
        {
            AppStoreApplicationSha1 = "3333333333333333333333333333333333333333", AppStoreInstallerSha1 = "4444444444444444444444444444444444444444",
            AppStoreInstallerName = "3rd Party Mac Developer Installer: Jane Doe (ABCDE12345)", AppStoreProfilePath = "/p/mas.provisionprofile",
        };
        Assert.Contains("confirm the App Sandbox entitlements", Option(Ctx(Mac, Inventory(profile: StoreProfile()), chosen), PackagingFormat.MacAppStore).Reason);
        var ready = Option(Ctx(Mac, Inventory(profile: StoreProfile()), chosen with { EntitlementsConfirmed = true }), PackagingFormat.MacAppStore);
        Assert.Equal(PackagingAvailability.Available, ready.Availability);

        var blocker = new EntitlementEvidence("Own update mechanism", EntitlementEvidenceKind.SandboxBlocker, null, null, "App.swift: import Sparkle", "store updates only");
        var incompatible = Option(Ctx(Mac, Inventory(profile: StoreProfile()), chosen with { EntitlementsConfirmed = true }, evidence: [blocker]), PackagingFormat.MacAppStore);
        Assert.Equal(PackagingAvailability.NotApplicable, incompatible.Availability);
        Assert.Contains("import Sparkle", incompatible.Reason);
        Assert.Equal(PackagingAvailability.NotApplicable, Option(Ctx(Mac, Inventory(profile: StoreProfile()), rootBundles: true), PackagingFormat.MacAppStore).Availability);
        Assert.Equal(PackagingAvailability.Failed, Option(Ctx(Mac, new MacSigningInventory { Problem = "security: keychain locked" }), PackagingFormat.MacAppStore).Availability);
    }

    [Fact]
    public void Developer_ID_states()
    {
        Assert.Equal(PackagingAvailability.Available, MacDistributionCatalog.DeveloperId(Ctx(Mac, Inventory())).Item1);
        Assert.Equal(PackagingAvailability.SetupRequired, MacDistributionCatalog.DeveloperId(Ctx(Mac, Inventory(devId: false))).Item1);
        Assert.Equal(PackagingAvailability.NotApplicable, MacDistributionCatalog.DeveloperId(Ctx(Mac, Inventory(), rootBundles: true)).Item1);
        Assert.Equal(PackagingAvailability.NotApplicable, MacDistributionCatalog.DeveloperId(new PackagingContext(TargetPlatform.MacOS, Mac, null, true)).Item1);
    }

    [Fact]
    public void Installers_and_store_packages_are_produced_after_the_app_bundle()
    {
        var order = PackagingCatalog.WithDependencies([PackagingFormat.MacAppStore, PackagingFormat.MacPkg, PackagingFormat.MacDmg]);
        Assert.Equal([PackagingFormat.MacAppBundle, PackagingFormat.MacDmg, PackagingFormat.MacPkg, PackagingFormat.MacAppStore], order);
    }

    [Fact]
    public void App_Store_blockers_name_each_missing_piece()
    {
        var settings = BuildPlannerTests.Settings("/tmp/out") with
        {
            Swift = new SwiftBuildOptions { PackageDirectory = "", Product = "TokenLab", Kind = SwiftAppKind.GuiApp },
            Identity = new AppIdentity { ApplicationName = "Distil", Version = "0.4.0", ApplicationId = "dev.tokenlab.prototype" },
        };
        var all = MacAppStorePackager.Blockers(settings, null, DateTimeOffset.UtcNow, hasRootResourceBundles: true);
        Assert.Equal(5, all.Count);
        var expired = StoreProfile() with { Expiration = DateTimeOffset.UtcNow.AddDays(-1), CertificateSha1s = ["9999999999999999999999999999999999999999"] };
        var some = MacAppStorePackager.Blockers(settings with { MacDistribution = new MacDistributionOptions
        {
            AppStoreApplicationSha1 = "3333333333333333333333333333333333333333", AppStoreInstallerName = "x", EntitlementsConfirmed = true,
        } }, expired, DateTimeOffset.UtcNow, false);
        Assert.Equal(2, some.Count);
        Assert.Contains(some, b => b.Contains("expired"));
        Assert.Contains(some, b => b.Contains("does not include the chosen distribution certificate"));
    }

    [Fact]
    public async Task Developer_ID_signing_refuses_unsealable_resource_bundles_before_touching_anything()
    {
        Directory.CreateDirectory(_t.Combine("publish", "Distil_Kit.bundle"));
        File.WriteAllBytes(_t.Combine("publish", "TokenLab"), [0xCF, 0xFA, 0xED, 0xFE]);
        var settings = BuildPlannerTests.Settings(_t.Combine("out")) with
        {
            Swift = new SwiftBuildOptions { PackageDirectory = "", Product = "TokenLab", Kind = SwiftAppKind.GuiApp },
            MacDistribution = new MacDistributionOptions { SigningMode = MacSigningMode.DeveloperId, DeveloperIdApplicationSha1 = "1111111111111111111111111111111111111111", EntitlementsConfirmed = true },
        };
        var runner = new RecordingRunner();
        var request = new PackagingRequest
        {
            Settings = settings, PublishDirectory = _t.Combine("publish"), ExecutableFileName = "TokenLab", OutputRoot = _t.Combine("out"), ArtifactBaseName = "Distil-0.4.0",
            Host = Mac, Toolchains = new ToolchainReport(Mac, [new ToolchainInfo { Id = ToolchainIds.Codesign, DisplayName = "codesign", Category = "macOS", Status = ToolchainStatus.Installed, ExecutablePath = "/usr/bin/codesign" }], null, null, DateTimeOffset.UtcNow),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SwiftAppBundlePackager(runner).PackageAsync(request, new NullLog(), CancellationToken.None));
        Assert.Contains("incompatible with this project", ex.Message);
        Assert.Empty(runner.Calls);
        Assert.False(Directory.Exists(_t.Combine("out", "Distil.app")));
    }

    [Fact]
    public async Task A_signed_installer_around_an_ad_hoc_app_is_refused()
    {
        var settings = BuildPlannerTests.Settings(_t.Combine("out")) with
        {
            MacDistribution = new MacDistributionOptions { PkgInstallerSha1 = "2222222222222222222222222222222222222222", PkgInstallerName = "Developer ID Installer: Jane Doe (ABCDE12345)" },
        };
        var request = new PackagingRequest
        {
            Settings = settings, PublishDirectory = _t.Path, ExecutableFileName = "x", OutputRoot = _t.Path, ArtifactBaseName = "x", Host = Mac,
            Toolchains = new ToolchainReport(Mac, [], null, null, DateTimeOffset.UtcNow),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new MacPkgPackager(new RecordingRunner()).PackageAsync(request, new NullLog(), CancellationToken.None));
        Assert.Contains("cannot be notarized", ex.Message);
    }

    [Fact]
    public void Notarization_is_refused_for_ad_hoc_builds_and_without_credentials()
    {
        var build = new BuildRecord { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Settings = BuildPlannerTests.Settings("/tmp/out"), StartedAt = DateTimeOffset.UtcNow, Status = BuildStatus.Succeeded };
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains("ad-hoc signed", NotarizationService.Blocker(build, "HCG-Notary"));
            var devId = new BuildRecord { Id = build.Id, ProjectId = build.ProjectId, StartedAt = build.StartedAt, Status = BuildStatus.Succeeded,
                Settings = build.Settings with { MacDistribution = new MacDistributionOptions { SigningMode = MacSigningMode.DeveloperId } } };
            Assert.Contains("store-credentials", NotarizationService.Blocker(devId, null));
            Assert.Null(NotarizationService.Blocker(devId, "HCG-Notary"));
        }
        else
        {
            Assert.Contains("on a Mac", NotarizationService.Blocker(build, "HCG-Notary"));
        }
        Assert.Empty(NotarizationService.Eligible(build));
    }

    private sealed class NullLog : IPackagingLog
    {
        public void Info(string message) { }
        public void Warning(string message) { }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            return Task.FromResult(new ProcessResult(1, TimeSpan.Zero, false, false));
        }
    }
}
