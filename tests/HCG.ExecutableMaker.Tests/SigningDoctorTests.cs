using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.Impactor.Diagnostics;
using HCG.Impactor.Inspection;
using HCG.Impactor.MacOS;
using HCG.Impactor.Signing;
using Xunit;
using F = HCG.ExecutableMaker.Tests.ImpactorFixtures;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Signing Doctor and Xcode-account provisioning — SYNTHETIC profiles and certificates. The scenario mirrors HCG Mail:
/// one signing identity and eight installed profiles, none usable for com.hakmicapitalgroup.hcgmail.
/// </summary>
public sealed class SigningDoctorTests
{
    private const string Mail = "com.hakmicapitalgroup.hcgmail";
    private static readonly DateTimeOffset Now = SigningFixtures.Now;

    private static ProfileEntry Entry(TempDir dir, SigningFixtures.ProfileSpec spec)
    {
        var path = dir.Combine(spec.Uuid + ".mobileprovision");
        File.WriteAllBytes(path, SigningFixtures.Profile(spec));
        var read = IpaInspector.ReadProfile(path, SigningFixtures.TrustedRoots);
        return new ProfileEntry(read.Profile!, read.RawEntitlements, path);
    }

    private static SigningFixtures.ProfileSpec Spec(string name, string appId, System.Security.Cryptography.X509Certificates.X509Certificate2 cert, string team = SigningFixtures.Team,
        DateTimeOffset? expires = null, IReadOnlyList<string>? devices = null, bool dev = true, Dictionary<string, object>? ent = null) => new()
    {
        Name = name, Team = team, AppId = team + "." + appId, Certificates = [cert], Expires = expires ?? Now.AddMonths(6),
        Devices = devices ?? [F.DeviceUdid], GetTaskAllow = dev, Entitlements = ent ?? new Dictionary<string, object>(),
    };

    [Fact]
    public void Eight_profiles_none_usable_each_reason_is_named()
    {
        using var dir = new TempDir();
        var mine = SigningFixtures.Development();
        var someoneElses = SigningFixtures.Development("Other Person");
        var dist = SigningFixtures.Distribution();
        var otherTeamCert = SigningFixtures.Development("Teammate", SigningFixtures.OtherTeam);
        var profiles = new[]
        {
            Entry(dir, Spec("HCG Terminal Dev", "com.hakmicapitalgroup.hcgterminal", mine)),                                     // wrong App ID
            Entry(dir, Spec("HCG Wildcard Other Prefix", "com.example.*", mine)),                                                // wildcard for another prefix
            Entry(dir, Spec("Other Team Mail", Mail, otherTeamCert, team: SigningFixtures.OtherTeam)),                           // other team
            Entry(dir, Spec("Mail Without My Cert", Mail, someoneElses)),                                                        // certificate not included
            Entry(dir, Spec("Mail Expired", Mail, mine, expires: Now.AddDays(-3))),                                              // profile expired
            Entry(dir, Spec("Mail Other Devices", Mail, mine, devices: ["00008030-000000000000FFFF"])),                         // device not registered
            Entry(dir, Spec("Mail App Store", Mail, dist, dev: false, devices: []) with { Devices = null }),                     // distribution / App Store
            Entry(dir, Spec("Mail No Push", Mail, mine)),                                                                        // entitlement missing
        };
        var inv = new SigningInventorySnapshot([SigningFixtures.Info(mine)], profiles);
        var request = new SigningDoctorRequest
        {
            BundleIdentifier = Mail, Device = F.Device(),
            Entitlements = new Dictionary<string, object> { ["aps-environment"] = "development", ["keychain-access-groups"] = new List<object> { SigningFixtures.Team + "." + Mail } },
        };

        var r = SigningDoctor.Diagnose(inv, request, Now);

        Assert.False(r.HasUsableProfile);
        Assert.Equal(8, r.Profiles.Count);
        Assert.StartsWith("None of the 8 profile(s) can sign com.hakmicapitalgroup.hcgmail for Synthetic iPhone.", r.Summary);
        ProfileCheck Failed(string profile, ProfileCheckKind kind) => r.Profiles.Single(p => p.Name == profile).Checks.Single(c => c.Kind == kind && !c.Passed);

        Assert.Contains("does not cover com.hakmicapitalgroup.hcgmail", Failed("HCG Terminal Dev", ProfileCheckKind.BundleIdentifier).Detail);
        Assert.Contains("wildcard is for another prefix", Failed("HCG Wildcard Other Prefix", ProfileCheckKind.BundleIdentifier).Detail);
        Assert.Contains($"Team {SigningFixtures.OtherTeam}", Failed("Other Team Mail", ProfileCheckKind.Team).Detail);
        Assert.Contains("none of them yours", Failed("Mail Without My Cert", ProfileCheckKind.Certificate).Detail);
        Assert.Contains("Expired on", Failed("Mail Expired", ProfileCheckKind.ProfileExpiry).Detail);
        Assert.Contains("is not among its 1 device(s)", Failed("Mail Other Devices", ProfileCheckKind.Device).Detail);
        Assert.Contains("App Store profile", Failed("Mail App Store", ProfileCheckKind.ProfileType).Detail);
        Assert.Contains("aps-environment", Failed("Mail No Push", ProfileCheckKind.Entitlements).Detail);
        // A profile that only lacks the push entitlement is the closest one; every other check passes for it.
        Assert.Equal("Mail No Push", r.Closest!.Name);
        Assert.Single(r.Closest.Failures);
        // The way forward: Xcode's automatic provisioning with the team of the only identity.
        Assert.Equal(SigningFixtures.Team, r.SuggestedTeam);
        Assert.Contains("allowProvisioningUpdates", r.NextSteps[0]);
        Assert.Contains(r.FailureCounts, c => c.Kind == ProfileCheckKind.BundleIdentifier && c.Count >= 2);
    }

    [Fact]
    public void A_matching_profile_is_usable_and_expired_certificates_are_named()
    {
        using var dir = new TempDir();
        var mine = SigningFixtures.Development();
        var good = Entry(dir, Spec("Mail Dev", Mail, mine, ent: new() { ["aps-environment"] = "development" }));
        var ok = SigningDoctor.Diagnose(new([SigningFixtures.Info(mine)], [good]),
            new SigningDoctorRequest { BundleIdentifier = Mail, Device = F.Device(), Entitlements = new Dictionary<string, object> { ["aps-environment"] = "development" } }, Now);
        Assert.True(ok.HasUsableProfile);
        Assert.Contains("\"Mail Dev\"", ok.Summary);

        var expiredCert = SigningFixtures.Development(notAfter: Now.AddDays(-1));
        var withExpired = Entry(dir, Spec("Mail Dev 2", Mail, expiredCert));
        var bad = SigningDoctor.Diagnose(new([SigningFixtures.Info(expiredCert)], [withExpired]), new SigningDoctorRequest { BundleIdentifier = Mail }, Now);
        Assert.Contains(bad.Profiles[0].Failures, c => c.Kind == ProfileCheckKind.CertificateExpiry && c.Detail.Contains("expired on"));
        Assert.Contains(bad.Findings, f => f.Code == "doctor.identity-expired");

        var noKey = SigningDoctor.Diagnose(new([SigningFixtures.Info(mine, hasKey: false)], [good]), new SigningDoctorRequest { BundleIdentifier = Mail }, Now);
        Assert.Contains(noKey.Findings, f => f.Code == "doctor.no-private-key");
        Assert.Contains("private key is not on this Mac", noKey.Profiles[0].Checks.Single(c => c.Kind == ProfileCheckKind.Certificate).Detail);
    }

    [Fact]
    public void Provisioning_stub_project_command_and_entitlements()
    {
        using var dir = new TempDir();
        var req = new ProvisioningRequest
        {
            BundleIdentifier = Mail, TeamId = SigningFixtures.Team, MinimumIos = "17.0",
            Entitlements = new Dictionary<string, object> { ["aps-environment"] = "development", ["application-identifier"] = "X.y", ["keychain-access-groups"] = new List<object> { "$(AppIdentifierPrefix)" + Mail } },
        };
        var folder = XcodeProvisioningAssistant.WriteStubProject(dir.Combine("stub"), req);
        var pbx = File.ReadAllText(Path.Combine(folder, "HCGProvisioningStub.xcodeproj", "project.pbxproj"));
        Assert.Contains($"PRODUCT_BUNDLE_IDENTIFIER = {Mail};", pbx);
        Assert.Contains("IPHONEOS_DEPLOYMENT_TARGET = 17.0;", pbx);
        Assert.Contains("CODE_SIGN_STYLE = Automatic;", pbx);
        Assert.True(File.Exists(Path.Combine(folder, "HCGProvisioningStub.xcodeproj", "xcshareddata", "xcschemes", "HCGProvisioningStub.xcscheme")));
        Assert.True(File.Exists(Path.Combine(folder, "Sources", "StubApp.swift")));
        var ent = PlistReader.ParseDictionary(File.ReadAllBytes(Path.Combine(folder, "HCGProvisioningStub.entitlements")));
        Assert.Equal("development", ent["aps-environment"]);
        Assert.False(ent.ContainsKey("application-identifier")); // Xcode fills identity entitlements itself
        Assert.Equal([$"{SigningFixtures.Team}.{Mail}"], ((List<object>)ent["keychain-access-groups"]).Cast<string>());

        var args = XcodeProvisioningAssistant.Arguments(folder, req with { RegisterConnectedDevices = true });
        Assert.Contains("-allowProvisioningUpdates", args);
        Assert.Contains("-allowProvisioningDeviceRegistration", args);
        Assert.DoesNotContain("-allowProvisioningDeviceRegistration", XcodeProvisioningAssistant.Arguments(folder, req)); // opt-in only
        Assert.Contains($"DEVELOPMENT_TEAM={SigningFixtures.Team}", args);
        Assert.Contains("CODE_SIGN_STYLE=Automatic", args);
        Assert.DoesNotContain(args, a => a.Contains("revoke", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("-allowProvisioningDeviceRegistration", XcodeProvisioningAssistant.Arguments(folder, req with { RegisterConnectedDevices = false }));
        var withKey = XcodeProvisioningAssistant.Arguments(folder, req with { ApiKey = ("/k/AuthKey.p8", "KEY123", "issuer-uuid") });
        Assert.Contains("-authenticationKeyPath", withKey);
    }

    [Theory]
    [InlineData("error: No Accounts: Add a new account in Accounts settings. (in target 'HCGProvisioningStub')", "Xcode is not signed in to this team")]
    [InlineData("error: The app identifier \"com.hakmicapitalgroup.hcgmail\" cannot be registered to your development team because it is not available.", "Bundle identifier belongs to another team")]
    [InlineData("error: Your team has no devices from which to generate a provisioning profile.", "No registered device")]
    [InlineData("error: Signing for \"HCGProvisioningStub\" requires a development team.", "No team selected")]
    [InlineData("error: Personal development teams, including \"Jane\", do not support the Push Notifications capability.", "The team cannot provide this capability")]
    [InlineData("error: No signing certificate \"iOS Development\" found", "No usable signing certificate")]
    [InlineData("something unexpected", "Profile creation failed")]
    public void Provisioning_failures_are_explained(string output, string title)
    {
        var d = ProvisioningDiagnostics.Diagnose(output);
        Assert.Equal(title, d.Title);
        Assert.Equal(DiagnosisKind.ProvisioningFailed, d.Kind);
        Assert.False(string.IsNullOrWhiteSpace(d.Remedy));
    }
}
