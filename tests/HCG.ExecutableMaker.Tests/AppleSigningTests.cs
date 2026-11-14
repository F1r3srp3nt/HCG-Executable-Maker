using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Signing;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Packaging.Signing;
using HCG.ExecutableMaker.Security;
using static HCG.ExecutableMaker.Tests.SigningFixtures;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Keychain detection and import (synthetic identities, in-memory keychain).</summary>
public class SigningImportTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeKeychain _keychain = new();
    private AppleSigningAssetService Service() => new(_keychain, Store(_dir), new FixedTime(Now));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_p12_with_the_right_password_imports_certificate_and_private_key()
    {
        using var cert = Development();
        var result = await Service().ImportPkcs12Async(Pkcs12(cert, "correct horse"), "correct horse", CancellationToken.None);
        Assert.Equal(SigningImportStatus.Imported, result.Status);
        Assert.True(result.Certificate!.HasPrivateKey);
        Assert.Equal(Team, result.Certificate.TeamId);
        Assert.Equal(AppleCertificateKind.Development, result.Certificate.Kind);
        Assert.Contains("macOS asks", result.Message);
        var inventory = await Service().ReadAsync(null, null, CancellationToken.None);
        Assert.Equal(SigningReadiness.Ready, Assert.Single(inventory.Certificates).Readiness(Now));
    }

    [Fact]
    public async Task A_wrong_p12_password_changes_nothing()
    {
        using var cert = Development();
        var result = await Service().ImportPkcs12Async(Pkcs12(cert, "right"), "wrong", CancellationToken.None);
        Assert.Equal(SigningImportStatus.WrongPassword, result.Status);
        Assert.Empty(_keychain.Operations);
        Assert.DoesNotContain("wrong", result.Message.Replace("The password does not open", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_password_prompt_or_declined_keychain_prompt_imports_nothing()
    {
        using var cert = Development();
        var p12 = Pkcs12(cert, "pw");
        Assert.Equal(SigningImportStatus.Cancelled, (await Service().ImportPkcs12Async(p12, null, CancellationToken.None)).Status);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(SigningImportStatus.Cancelled, (await Service().ImportPkcs12Async(p12, "pw", cts.Token)).Status);
        Assert.Empty(_keychain.Operations);

        _keychain.NextAddResult = new KeychainWriteResult(false, "User canceled the operation.", UserDenied: true);
        var declined = await Service().ImportPkcs12Async(p12, "pw", CancellationToken.None);
        Assert.Equal(SigningImportStatus.Cancelled, declined.Status);
        Assert.Empty(_keychain.Items);
    }

    [Fact]
    public async Task A_cer_imports_without_a_private_key_and_says_what_is_missing()
    {
        using var cert = Distribution();
        var result = await Service().ImportCertificateAsync(cert.Export(X509ContentType.Cert), CancellationToken.None);
        Assert.Equal(SigningImportStatus.ImportedCertificateOnly, result.Status);
        Assert.False(result.Certificate!.HasPrivateKey);
        Assert.Contains("cannot sign", result.Message);
        Assert.Contains(".p12", result.Remedy);
        Assert.Contains("key=False", Assert.Single(_keychain.Operations));
        var c = Assert.Single((await Service().ReadAsync(null, null, CancellationToken.None)).Certificates);
        Assert.Equal(SigningReadiness.PrivateKeyMissing, c.Readiness(Now));
    }

    [Fact]
    public async Task Duplicates_are_detected_by_fingerprint_and_change_nothing()
    {
        using var cert = Development();
        _keychain.Add(cert, hasKey: true);
        var p12 = await Service().ImportPkcs12Async(Pkcs12(cert, "pw"), "pw", CancellationToken.None);
        Assert.Equal(SigningImportStatus.Duplicate, p12.Status);
        var cer = await Service().ImportCertificateAsync(cert.Export(X509ContentType.Cert), CancellationToken.None);
        Assert.Equal(SigningImportStatus.Duplicate, cer.Status);
        Assert.Empty(_keychain.Operations);
    }

    [Fact]
    public async Task A_p12_adds_the_missing_key_for_a_certificate_that_was_here_without_one()
    {
        using var cert = Development();
        _keychain.Add(cert, hasKey: false);
        var result = await Service().ImportPkcs12Async(Pkcs12(cert, "pw"), "pw", CancellationToken.None);
        Assert.Equal(SigningImportStatus.Imported, result.Status);
    }

    [Fact]
    public async Task Expired_invalid_and_non_Apple_certificates_are_refused()
    {
        using var expired = Development(notAfter: Now.AddDays(-1));
        Assert.Equal(SigningImportStatus.Expired, (await Service().ImportPkcs12Async(Pkcs12(expired, "pw"), "pw", CancellationToken.None)).Status);
        Assert.Equal(SigningImportStatus.Invalid, (await Service().ImportPkcs12Async(Encoding.UTF8.GetBytes("not a p12"), "pw", CancellationToken.None)).Status);
        Assert.Equal(SigningImportStatus.Invalid, (await Service().ImportCertificateAsync([1, 2, 3, 4], CancellationToken.None)).Status);
        using var other = Issue("Some Web Server", null, Now.AddDays(-1), Now.AddYears(1));
        Assert.Equal(SigningImportStatus.Unsupported, (await Service().ImportPkcs12Async(Pkcs12(other, "pw"), "pw", CancellationToken.None)).Status);
        Assert.Empty(_keychain.Operations);
    }

    [Fact]
    public async Task Deleting_locally_uses_delete_identity_only_when_the_key_is_here()
    {
        using var withKey = Development();
        using var certOnly = Distribution();
        _keychain.Add(withKey, true);
        _keychain.Add(certOnly, false);
        var svc = Service();
        var inv = await svc.ReadAsync(null, null, CancellationToken.None);
        await svc.DeleteLocalAsync(inv.Certificates.First(c => c.HasPrivateKey == true), CancellationToken.None);
        await svc.DeleteLocalAsync(inv.Certificates.First(c => c.HasPrivateKey == false), CancellationToken.None);
        Assert.StartsWith("delete-identity ", _keychain.Operations[0]);
        Assert.StartsWith("delete-certificate ", _keychain.Operations[1]);
        Assert.Contains("private key", SigningRemovalEffects.DeleteLocalIdentity);
        Assert.Contains("stays valid with Apple", SigningRemovalEffects.DeleteLocalIdentity);
        Assert.Contains("stay in this Mac's keychain", SigningRemovalEffects.RemoveAccountRecord);
        Assert.Contains("never revokes", SigningRemovalEffects.RevokeWithApple);
    }

    [Fact]
    public async Task Keychain_problems_reported_by_macOS_are_kept_and_revocation_is_only_ever_reported_not_assumed()
    {
        using var revoked = Development("Revoked");
        using var fine = Development("Fine");
        _keychain.Add(revoked, true, "CSSMERR_TP_CERT_REVOKED");
        _keychain.Add(fine, true);
        var inv = await Service().ReadAsync(null, null, CancellationToken.None);
        var r = inv.Certificates.Single(c => c.CommonName.Contains("Revoked"));
        Assert.Equal(RevocationStatus.ReportedRevoked, r.Revocation);
        Assert.Equal(SigningReadiness.ReportedRevoked, r.Readiness(Now));
        Assert.Equal(RevocationStatus.NotChecked, inv.Certificates.Single(c => c.CommonName.Contains("Fine")).Revocation);
    }

    [Fact]
    public async Task Account_records_never_make_an_asset_ready_and_mark_other_Macs()
    {
        using var here = Development("Here");
        using var elsewhere = Distribution();
        _keychain.Add(here, true);
        var hereInfo = Info(here);
        var account = new List<AccountSigningAssetRecord>
        {
            new() { Id = Guid.NewGuid().ToString(), AssetType = "certificate", Sha1 = hereInfo.Sha1, DeviceId = "this-mac" },
            new() { Id = Guid.NewGuid().ToString(), AssetType = "certificate", Sha1 = Info(elsewhere).Sha1, DisplayName = "Apple Distribution: Synthetic Test Team (ABCDE12345)",
                    CertificateKind = "AppleDistribution", TeamId = Team, ExpiresAt = Now.AddMonths(6), DeviceId = "other-mac", DeviceLabel = "Studio Mac" },
            new() { Id = Guid.NewGuid().ToString(), AssetType = "profile", ProfileUuid = Guid.NewGuid().ToString(), DisplayName = "Remote profile", TeamId = Team,
                    ProfileKind = "AppStore", BundleIdPattern = "com.example.app", Platforms = ["iOS"], ExpiresAt = Now.AddMonths(3), DeviceId = "other-mac" },
        };
        var inv = await Service().ReadAsync(account, "this-mac", CancellationToken.None);
        var local = inv.Certificate(hereInfo.Sha1)!;
        Assert.Equal(SigningAssetLocation.ThisMac | SigningAssetLocation.HcgAccountMetadata, local.Location);
        Assert.Equal(SigningReadiness.Ready, local.Readiness(Now));
        var remote = inv.Certificate(Info(elsewhere).Sha1)!;
        Assert.True(remote.Location.HasFlag(SigningAssetLocation.AnotherMac));
        Assert.False(remote.Location.HasFlag(SigningAssetLocation.ThisMac));
        Assert.Null(remote.HasPrivateKey);
        Assert.Equal(SigningReadiness.NotOnThisMac, remote.Readiness(Now));
        Assert.Equal("Studio Mac", remote.RecordedOnDevice);
        var profile = Assert.Single(inv.Profiles);
        Assert.Null(profile.Path);
        Assert.False(profile.Location.HasFlag(SigningAssetLocation.ThisMac));
    }
}

/// <summary>Provisioning profiles read from verified CMS contents; import with duplicate, expiry and malformed checks.</summary>
public class ProvisioningProfileTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string WriteProfile(byte[] data, string name = "downloaded.mobileprovision")
    {
        var path = _dir.Combine("downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void Fields_come_from_the_verified_plist_not_the_file_name()
    {
        using var dev = Development();
        var spec = new ProfileSpec
        {
            Name = "Example Development", Devices = ["00008110-000A1B2C3D4E5F60", "00008030-001122334455667E"], GetTaskAllow = true, Certificates = [dev],
            Entitlements = new Dictionary<string, object> { ["aps-environment"] = "development", ["keychain-access-groups"] = new[] { Team + ".*" } },
        };
        var r = ProvisioningProfileReader.Read(Profile(spec), "/tmp/AppStore-Distribution.mobileprovision", TrustedRoots);
        var p = Assert.IsType<AppleProvisioningProfileInfo>(r.Profile);
        Assert.Equal(ProfileSignatureStatus.VerifiedApple, p.Signature);
        Assert.Equal(AppleProfileKind.Development, p.Kind);           // the file name says "AppStore" — ignored
        Assert.Equal(spec.Uuid, p.Uuid);
        Assert.Equal("Example Development", p.Name);
        Assert.Equal([Team], p.TeamIds);
        Assert.Equal("com.example.app", p.BundleIdPattern);
        Assert.Equal(["iOS"], p.Platforms);
        Assert.Equal(2, p.ProvisionedDevices.Count);
        Assert.Equal("development", p.Entitlements["aps-environment"]);
        Assert.Equal(Team + ".*", p.Entitlements["keychain-access-groups"]);
        Assert.Equal([Info(dev).Sha1], p.CertificateSha1s);
        Assert.Equal(spec.Expires, p.Expiration);
    }

    [Theory]
    [InlineData("adhoc", AppleProfileKind.AdHoc)]
    [InlineData("appstore", AppleProfileKind.AppStore)]
    [InlineData("enterprise", AppleProfileKind.Enterprise)]
    [InlineData("developerid", AppleProfileKind.DeveloperId)]
    [InlineData("macappstore", AppleProfileKind.AppStore)]
    public void Profile_types_are_classified_from_contents(string type, AppleProfileKind expected)
    {
        using var dist = Distribution();
        using var devId = DeveloperIdApplication();
        var spec = type switch
        {
            "adhoc" => new ProfileSpec { Devices = ["00008110-000A1B2C3D4E5F60"], Certificates = [dist] },
            "appstore" => new ProfileSpec { Certificates = [dist] },
            "enterprise" => new ProfileSpec { AllDevices = true, Certificates = [dist] },
            "developerid" => new ProfileSpec { AllDevices = true, Platform = "OSX", Certificates = [devId] },
            _ => new ProfileSpec { Platform = "OSX", Certificates = [dist] },
        };
        Assert.Equal(expected, ProvisioningProfileReader.Read(Profile(spec), null, TrustedRoots).Profile!.Kind);
    }

    [Fact]
    public void Malformed_tampered_and_untrusted_profiles_are_rejected()
    {
        Assert.Contains("not a signed (CMS)", ProvisioningProfileReader.Read(Encoding.UTF8.GetBytes(ProfileXml(new ProfileSpec())), null, TrustedRoots).Problem);
        Assert.Contains("not a valid property list", ProvisioningProfileReader.Read(Sign(Encoding.UTF8.GetBytes("hello"), ProfileSigner), null, TrustedRoots).Problem);
        Assert.Contains("missing required fields", ProvisioningProfileReader.Read(Profile(new ProfileSpec { OmitUuid = true }), null, TrustedRoots).Problem);

        var good = Profile(new ProfileSpec { Name = "Tamper Target" });
        var tampered = (byte[])good.Clone();
        var at = Encoding.Latin1.GetString(tampered).IndexOf("Tamper Target", StringComparison.Ordinal);
        tampered[at] = (byte)'X';
        Assert.Contains("does not match its contents", ProvisioningProfileReader.Read(tampered, null, TrustedRoots).Problem);

        Assert.Equal(ProfileSignatureStatus.VerifiedApple, ProvisioningProfileReader.Read(good, null, TrustedRoots).Profile!.Signature);
        // Intact signature, but the signer does not chain to a trusted root.
        using var key = System.Security.Cryptography.RSA.Create(2048);
        using var selfSigned = new CertificateRequest("CN=Self Signed", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(Now.AddYears(-1), Now.AddYears(1));
        Assert.Equal(ProfileSignatureStatus.IntactUntrustedSigner, ProvisioningProfileReader.Read(Profile(new ProfileSpec(), selfSigned), null, TrustedRoots).Profile!.Signature);
    }

    [Fact]
    public void Import_installs_by_UUID_and_rejects_duplicates_expired_malformed_and_untrusted_profiles()
    {
        var store = Store(_dir);
        var spec = new ProfileSpec { Name = "Installed" };
        var first = store.Import(WriteProfile(Profile(spec)), Now);
        Assert.Equal(SigningImportStatus.Imported, first.Status);
        Assert.Equal(spec.Uuid + ".mobileprovision", Path.GetFileName(first.Profile!.Path));

        // Same UUID under another file name → duplicate, nothing written.
        var dup = store.Import(WriteProfile(Profile(spec), "copy-of-it.mobileprovision"), Now);
        Assert.Equal(SigningImportStatus.Duplicate, dup.Status);
        Assert.Single(Directory.GetFiles(store.InstallFolder));

        Assert.Equal(SigningImportStatus.Expired, store.Import(WriteProfile(Profile(new ProfileSpec { Expires = Now.AddDays(-2) }), "old.mobileprovision"), Now).Status);
        Assert.Equal(SigningImportStatus.Invalid, store.Import(WriteProfile([0x30, 0x03, 0x02, 0x01, 0x00], "junk.mobileprovision"), Now).Status);
        Assert.Equal(SigningImportStatus.Unsupported, store.Import(WriteProfile(Profile(new ProfileSpec { Platform = "PalmOS" }), "palm.mobileprovision"), Now).Status);
        using var key = System.Security.Cryptography.RSA.Create(2048);
        using var selfSigned = new CertificateRequest("CN=Self Signed", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(Now.AddYears(-1), Now.AddYears(1));
        Assert.Equal(SigningImportStatus.Invalid, store.Import(WriteProfile(Profile(new ProfileSpec(), selfSigned), "foreign.mobileprovision"), Now).Status);
        Assert.Single(Directory.GetFiles(store.InstallFolder));

        var mac = store.Import(WriteProfile(Profile(new ProfileSpec { Platform = "OSX", AppId = Team + ".com.example.mac" }), "mac.provisionprofile"), Now);
        Assert.EndsWith(".provisionprofile", mac.Profile!.Path);
        Assert.Equal(2, store.List().Profiles.Count);
        Assert.Equal(1, store.Remove(spec.Uuid));
        Assert.Single(store.List().Profiles);
    }

    [Fact]
    public void Unreadable_profiles_in_the_folder_are_listed_with_the_reason()
    {
        var store = Store(_dir);
        Directory.CreateDirectory(store.InstallFolder);
        File.WriteAllText(Path.Combine(store.InstallFolder, "broken.mobileprovision"), "not cms");
        var listing = store.List();
        Assert.Empty(listing.Profiles);
        Assert.Contains("not a signed", Assert.Single(listing.Unreadable).Problem);
    }

    [Theory]
    [InlineData("ABCDE12345.com.example.app", "com.example.app", true)]
    [InlineData("ABCDE12345.com.example.app", "com.example.app2", false)]
    [InlineData("ABCDE12345.com.example.app", "com.example.app.widget", false)]
    [InlineData("ABCDE12345.com.example.*", "com.example.app", true)]
    [InlineData("ABCDE12345.com.example.*", "com.example.app.widget", true)]
    [InlineData("ABCDE12345.com.example.*", "com.examplex.app", false)]
    [InlineData("ABCDE12345.com.example.*", "com.example.", false)]
    [InlineData("ABCDE12345.*", "org.anything", true)]
    [InlineData("ZYXWV98765.com.example.app", "com.example.app", false)]   // prefix is not the profile's team
    public void Bundle_identifier_wildcard_rules(string appId, string bundleId, bool matches)
    {
        var p = new AppleProvisioningProfileInfo { Uuid = Guid.NewGuid().ToString(), Name = "x", TeamIds = [Team], ApplicationIdentifier = appId };
        Assert.Equal(matches, p.Matches(bundleId));
    }
}

/// <summary>Pre-build validation: route, identity, key, profile, team, bundle ID, entitlements, devices, targets.</summary>
public class SigningValidationTests
{
    private static SigningValidationReport Validate(ProjectSigningConfiguration cfg, IEnumerable<AppleCertificateInfo> certs, IEnumerable<AppleProvisioningProfileInfo> profiles,
        IReadOnlyList<SigningTargetFacts>? targets = null, bool isMac = true, bool pkg = false) =>
        SigningValidator.Validate(new SigningValidationInput
        {
            Configuration = cfg,
            Inventory = new SigningAssetInventory { Certificates = certs.ToList(), Profiles = profiles.ToList() },
            Targets = targets ?? [App()],
            Tools = new SigningToolFacts(isMac, isMac, isMac, isMac, isMac ? "26.3" : null, isMac),
            ProducesInstallerPackage = pkg,
            Now = Now,
        });

    private static SigningTargetFacts App(string bundle = "com.example.app", Dictionary<string, string>? ent = null) => new("App", bundle, false, ent ?? []);

    private static AppleProvisioningProfileInfo Installed(ProfileSpec spec) =>
        ProvisioningProfileReader.Read(Profile(spec), "/Users/test/Library/Developer/Xcode/UserData/Provisioning Profiles/" + spec.Uuid + ".mobileprovision", TrustedRoots).Profile!;

    private static ProjectSigningConfiguration Manual(SigningRoute route, AppleCertificateInfo identity, AppleProvisioningProfileInfo? profile, SigningPlatform platform = SigningPlatform.Ios) => new()
    {
        ProjectId = "p", Platform = platform, Mode = ProjectSigningMode.Manual, Route = route, TeamId = Team, IdentitySha1 = identity.Sha1, ProfileUuid = profile?.Uuid,
    };

    private static void AssertFailed(SigningValidationReport r, SigningCheckCategory category, string fragment, string? target = null)
    {
        var f = r.Failures.Where(c => c.Category == category && (target is null || c.Target == target)).ToList();
        Assert.True(f.Any(c => c.Detail.Contains(fragment, StringComparison.Ordinal)),
            $"Expected a {category} failure containing \"{fragment}\". Failures: {string.Join(" | ", r.Failures.Select(x => $"{x.Category}: {x.Detail}"))}");
        Assert.All(f, c => Assert.False(string.IsNullOrEmpty(c.Remedy)));
    }

    [Fact]
    public void A_complete_iOS_App_Store_configuration_passes_and_Apple_side_checks_stay_not_verified()
    {
        using var dist = Distribution();
        var p = Installed(new ProfileSpec { Certificates = [dist], Entitlements = new Dictionary<string, object> { ["aps-environment"] = "production" } });
        var r = Validate(Manual(SigningRoute.IosAppStore, Info(dist), p), [Info(dist)], [p], [App(ent: new() { ["aps-environment"] = "production" })]);
        Assert.False(r.HasFailures, string.Join(" | ", r.Failures.Select(f => f.Detail)));
        Assert.Contains(r.Checks, c => c.Title == "Signing certificate revocation" && c.Outcome == SigningCheckOutcome.NotVerified && c.Scope == SigningCheckScope.RequiresApple);
        Assert.DoesNotContain(r.Checks, c => c.Outcome == SigningCheckOutcome.Passed && c.Detail.Contains("not revoked", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Checks, c => c.Title == "Apple-side status" && c.Outcome == SigningCheckOutcome.NotVerified);
    }

    [Fact]
    public void Missing_private_key_is_a_specific_signing_failure_not_a_toolchain_problem()
    {
        using var dist = Distribution();
        var p = Installed(new ProfileSpec { Certificates = [dist] });
        var r = Validate(Manual(SigningRoute.IosAppStore, Info(dist, hasKey: false), p), [Info(dist, hasKey: false)], [p]);
        AssertFailed(r, SigningCheckCategory.PrivateKey, "without its private key");
        Assert.DoesNotContain(r.Failures, f => f.Category == SigningCheckCategory.Tools);
        Assert.DoesNotContain(r.Checks, c => c.Detail.Contains("toolchain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Team_mismatches_between_project_certificate_and_profile_fail()
    {
        using var other = Distribution(team: OtherTeam);
        var p = Installed(new ProfileSpec { Team = OtherTeam, AppId = OtherTeam + ".com.example.app", Certificates = [other] });
        var r = Validate(Manual(SigningRoute.IosAppStore, Info(other), p), [Info(other)], [p]);
        AssertFailed(r, SigningCheckCategory.Team, $"belongs to team {OtherTeam}; the project is set to team {Team}");
        AssertFailed(r, SigningCheckCategory.Team, "the project uses team " + Team);
    }

    [Fact]
    public void Bundle_identifier_certificate_and_entitlement_mismatches_fail_with_remedies()
    {
        using var dist = Distribution();
        using var otherDist = Distribution();
        var p = Installed(new ProfileSpec { Certificates = [otherDist], AppId = Team + ".com.example.other",
            Entitlements = new Dictionary<string, object> { ["aps-environment"] = "development" } });
        var ent = new Dictionary<string, string> { ["aps-environment"] = "production", ["com.apple.developer.icloud-services"] = "CloudKit" };
        var r = Validate(Manual(SigningRoute.IosAppStore, Info(dist), p), [Info(dist), Info(otherDist)], [p], [App(ent: ent)]);
        AssertFailed(r, SigningCheckCategory.BundleIdentifier, "is not covered by the profile's App ID");
        AssertFailed(r, SigningCheckCategory.Profile, "does not include the selected certificate");
        AssertFailed(r, SigningCheckCategory.Entitlements, "aps-environment = production is not allowed");
        AssertFailed(r, SigningCheckCategory.Entitlements, "com.apple.developer.icloud-services is not in the profile");
    }

    [Fact]
    public void Wildcard_App_IDs_match_but_cannot_carry_capabilities()
    {
        using var dist = Distribution();
        var wildcard = Installed(new ProfileSpec { Certificates = [dist], AppId = Team + ".com.example.*" });
        var plain = Validate(Manual(SigningRoute.IosAppStore, Info(dist), wildcard), [Info(dist)], [wildcard]);
        Assert.False(plain.HasFailures);
        Assert.Contains(plain.Checks, c => c.Category == SigningCheckCategory.BundleIdentifier && c.Detail.Contains("wildcard"));
        var push = Validate(Manual(SigningRoute.IosAppStore, Info(dist), wildcard), [Info(dist)], [wildcard], [App(ent: new() { ["aps-environment"] = "production" })]);
        AssertFailed(push, SigningCheckCategory.BundleIdentifier, "wildcard App IDs cannot carry");
    }

    [Fact]
    public void Wrong_certificate_type_wrong_profile_type_and_wrong_platform_fail()
    {
        using var dev = Development();
        using var dist = Distribution();
        var devProfile = Installed(new ProfileSpec { Certificates = [dev, dist], Devices = ["d1"], GetTaskAllow = true });
        var r = Validate(Manual(SigningRoute.IosAppStore, Info(dev), devProfile), [Info(dev)], [devProfile]);
        AssertFailed(r, SigningCheckCategory.Certificate, "is of type Apple Development");
        AssertFailed(r, SigningCheckCategory.Profile, "is of type Development");

        var macProfile = Installed(new ProfileSpec { Certificates = [dist], Platform = "OSX" });
        AssertFailed(Validate(Manual(SigningRoute.IosAppStore, Info(dist), macProfile), [Info(dist)], [macProfile]), SigningCheckCategory.Platform, "this build targets iOS");
    }

    [Fact]
    public void Expired_certificates_and_profiles_fail_and_soon_expiring_ones_warn()
    {
        using var expired = Distribution(notAfter: Now.AddDays(-3));
        using var soon = Distribution(notAfter: Now.AddDays(10));
        var p = Installed(new ProfileSpec { Certificates = [expired, soon], Expires = Now.AddDays(-1) });
        var r = Validate(Manual(SigningRoute.IosAppStore, Info(expired), p), [Info(expired)], [p]);
        AssertFailed(r, SigningCheckCategory.Certificate, "Expired on");
        AssertFailed(r, SigningCheckCategory.Profile, "expired on");
        var w = Validate(Manual(SigningRoute.IosAppStore, Info(soon), Installed(new ProfileSpec { Certificates = [soon] })), [Info(soon)],
            [Installed(new ProfileSpec { Certificates = [soon] })]);
        Assert.Contains(w.Checks, c => c.Outcome == SigningCheckOutcome.Warning && c.Detail.Contains("(10 days)"));
    }

    [Fact]
    public void Device_eligibility_is_checked_against_the_profile_when_devices_are_chosen()
    {
        using var dev = Development();
        var p = Installed(new ProfileSpec { Certificates = [dev], Devices = ["00008110-000A1B2C3D4E5F60"], GetTaskAllow = true });
        var cfg = Manual(SigningRoute.LocalDevelopment, Info(dev), p);
        Assert.Contains(Validate(cfg, [Info(dev)], [p]).Checks, c => c.Category == SigningCheckCategory.Devices && c.Outcome == SigningCheckOutcome.NotVerified);
        Assert.False(Validate(cfg with { IntendedDevices = ["00008110-000A1B2C3D4E5F60"] }, [Info(dev)], [p]).HasFailures);
        AssertFailed(Validate(cfg with { IntendedDevices = ["00008030-FFFFFFFFFFFFFFFF"] }, [Info(dev)], [p]), SigningCheckCategory.Devices, "Not in the profile");
    }

    [Fact]
    public void Developer_ID_and_local_macOS_builds_without_restricted_entitlements_need_no_profile()
    {
        using var devId = DeveloperIdApplication();
        var sandboxOnly = new Dictionary<string, string> { ["com.apple.security.app-sandbox"] = "true", ["com.apple.security.cs.allow-jit"] = "true" };
        var cfg = Manual(SigningRoute.MacDeveloperId, Info(devId), null, SigningPlatform.MacOs);
        var r = Validate(cfg, [Info(devId)], [], [App(ent: sandboxOnly)]);
        Assert.False(r.HasFailures, string.Join(" | ", r.Failures.Select(f => f.Detail)));
        Assert.Contains(r.Checks, c => c.Category == SigningCheckCategory.Profile && c.Outcome == SigningCheckOutcome.NotApplicable && c.Detail.Contains("certificate alone"));

        using var macDev = Development();
        Assert.False(Validate(Manual(SigningRoute.LocalDevelopment, Info(macDev), null, SigningPlatform.MacOs), [Info(macDev)], []).HasFailures);

        // A restricted entitlement (iCloud) needs a Developer ID profile.
        var icloud = Validate(cfg, [Info(devId)], [], [App(ent: new() { ["com.apple.developer.icloud-services"] = "CloudKit" })]);
        AssertFailed(icloud, SigningCheckCategory.Profile, "restricted entitlements");
    }

    [Fact]
    public void Mac_App_Store_needs_a_profile_and_an_installer_identity_for_the_pkg()
    {
        using var dist = Distribution();
        var cfg = Manual(SigningRoute.MacAppStore, Info(dist), null, SigningPlatform.MacOs);
        var r = Validate(cfg, [Info(dist)], [], pkg: true);
        AssertFailed(r, SigningCheckCategory.Profile, "No provisioning profile is selected");
        AssertFailed(r, SigningCheckCategory.Certificate, "No Mac Installer Distribution certificate is selected");
        using var devIdInstaller = DeveloperIdInstaller();
        var wrongInstaller = Validate(cfg with { InstallerIdentitySha1 = Info(devIdInstaller).Sha1 }, [Info(dist), Info(devIdInstaller)], [], pkg: true);
        AssertFailed(wrongInstaller, SigningCheckCategory.Certificate, "is of type Developer ID Installer");
    }

    [Fact]
    public void Every_target_and_extension_is_checked_with_its_own_profile()
    {
        using var dist = Distribution();
        var appProfile = Installed(new ProfileSpec { Name = "App", Certificates = [dist] });
        var widgetProfile = Installed(new ProfileSpec { Name = "Widget", Certificates = [dist], AppId = Team + ".com.example.app.widget" });
        var cfg = Manual(SigningRoute.IosAppStore, Info(dist), appProfile) with
        {
            Targets = [new TargetSigningConfiguration { TargetName = "Widget", ProfileUuid = widgetProfile.Uuid, IsEmbedded = true }],
        };
        var targets = new List<SigningTargetFacts> { App(), new("Widget", "com.example.app.widget", true, new Dictionary<string, string>()) };
        Assert.False(Validate(cfg, [Info(dist)], [appProfile, widgetProfile], targets).HasFailures);

        // The extension given the app's profile fails on the extension only.
        var wrong = cfg with { Targets = [new TargetSigningConfiguration { TargetName = "Widget", ProfileUuid = appProfile.Uuid, IsEmbedded = true }] };
        var r = Validate(wrong, [Info(dist)], [appProfile, widgetProfile], targets);
        AssertFailed(r, SigningCheckCategory.BundleIdentifier, "com.example.app.widget is not covered", "Widget");
        Assert.DoesNotContain(r.Failures, f => f.Target == "App");

        // An extension with no profile of its own fails (it does not silently inherit the app's).
        var none = cfg with { Targets = [] };
        AssertFailed(Validate(none, [Info(dist)], [appProfile], targets), SigningCheckCategory.Profile, "No provisioning profile is selected", "Widget");

        // Embedded identifiers must be under the app's.
        var outside = new List<SigningTargetFacts> { App(), new("Widget", "com.other.widget", true, new Dictionary<string, string>()) };
        AssertFailed(Validate(cfg, [Info(dist)], [appProfile, widgetProfile], outside), SigningCheckCategory.BundleIdentifier, "does not start with");
    }

    [Fact]
    public void Account_only_assets_are_never_treated_as_usable()
    {
        using var dist = Distribution();
        var remote = Info(dist) with { Location = SigningAssetLocation.HcgAccountMetadata | SigningAssetLocation.AnotherMac, HasPrivateKey = null, RecordedOnDevice = "Studio Mac" };
        var p = Installed(new ProfileSpec { Certificates = [dist] });
        var r = Validate(Manual(SigningRoute.IosAppStore, remote, p), [remote], [p]);
        AssertFailed(r, SigningCheckCategory.Certificate, "known only from your HCG account (recorded on Studio Mac)");
    }

    [Fact]
    public void Automatic_signing_without_a_local_identity_is_not_verified_rather_than_failed()
    {
        var cfg = new ProjectSigningConfiguration { ProjectId = "p", Mode = ProjectSigningMode.XcodeAutomatic, Route = SigningRoute.LocalDevelopment, TeamId = Team };
        var r = Validate(cfg, [], []);
        Assert.False(r.HasFailures);
        Assert.Contains(r.Checks, c => c.Title == "Signing certificate" && c.Outcome == SigningCheckOutcome.NotVerified && c.Scope == SigningCheckScope.RequiresApple);
        Assert.Contains(r.Checks, c => c.Title == "Provisioning profiles" && c.Scope == SigningCheckScope.RequiresApple);
    }

    [Fact]
    public void Tools_are_a_separate_category_and_a_missing_team_is_reported_as_such()
    {
        var r = Validate(new ProjectSigningConfiguration { ProjectId = "p", Mode = ProjectSigningMode.XcodeAutomatic }, [], [], isMac: false);
        Assert.Contains(r.Failures, f => f.Category == SigningCheckCategory.Tools && f.Detail.Contains("not a Mac"));
        AssertFailed(r, SigningCheckCategory.Team, "No Apple Developer Team ID");
    }

    [Theory]
    [InlineData("production", "production", true)]
    [InlineData("production", "development", false)]
    [InlineData("ABCDE12345.com.example.shared", "ABCDE12345.*", true)]
    [InlineData("applinks:example.com, webcredentials:example.com", "*", true)]
    [InlineData("group.com.example.a, group.com.example.b", "group.com.example.a", false)]
    public void Entitlement_values_must_be_granted_exactly_or_by_wildcard(string requested, string granted, bool allowed) =>
        Assert.Equal(allowed, SigningValidator.ValueAllowed(requested, granted));
}

/// <summary>Secrets stay out of processes, logs, settings and account metadata.</summary>
public class SigningSecretHandlingTests : IDisposable
{
    private const string Password = "Sup3r-S3cret-P12-Passw0rd!";
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static RecordingProcessRunner Security(string identities = "", string pem = "") => new(spec => spec.Arguments.FirstOrDefault() switch
    {
        "find-identity" => (0, identities, ""),
        "find-certificate" => (0, pem, ""),
        _ => (0, "", ""),
    });

    [Fact]
    public async Task A_p12_import_never_puts_the_password_on_a_command_line_in_a_message_or_in_settings()
    {
        using var cert = Development();
        var runner = Security();
        var keychain = new FakeKeychain { ListFrom = new MacAppleKeychain(runner, supported: true) };
        var service = new AppleSigningAssetService(keychain, Store(_dir), new FixedTime(Now));
        var wrong = await service.ImportPkcs12Async(Pkcs12(cert, Password), Password + "x", CancellationToken.None);
        var ok = await service.ImportPkcs12Async(Pkcs12(cert, Password), Password, CancellationToken.None);
        Assert.Equal(SigningImportStatus.Imported, ok.Status);
        Assert.NotEmpty(runner.Calls);
        foreach (var call in runner.Calls)
        {
            Assert.Equal("/usr/bin/security", call.Executable);
            Assert.DoesNotContain(call.Arguments, a => a.Contains(Password, StringComparison.Ordinal));
            Assert.DoesNotContain(call.Arguments, a => a is "-P" or "-A" or "-T" or "import");
            Assert.Null(call.Environment);
        }
        foreach (var text in new[] { wrong.Message, wrong.Remedy ?? "", ok.Message, ok.Remedy ?? "", JsonSerializer.Serialize(ok.Certificate) })
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        var config = new ProjectSigningConfiguration { ProjectId = "p", IdentitySha1 = ok.Certificate!.Sha1, TeamId = Team };
        var json = HcgJson.Serialize(config);
        Assert.DoesNotContain(Password, json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE KEY", json);
    }

    [Fact]
    public void Diagnostic_text_with_a_p12_password_or_key_is_redacted()
    {
        var log = $"P12_PASSWORD={Password}\nimport ok\n-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----";
        var redacted = SecretRedactor.Redact(log);
        Assert.DoesNotContain(Password, redacted);
        Assert.DoesNotContain("MIIEvQ", redacted);
        Assert.Contains("import ok", redacted);
    }

    [Fact]
    public async Task Keychain_commands_take_fingerprints_only_and_refuse_anything_else()
    {
        var runner = Security();
        var kc = new MacAppleKeychain(runner, supported: true);
        var bad = await kc.DeleteIdentityAsync("-A; rm -rf ~", CancellationToken.None);
        Assert.False(bad.Succeeded);
        Assert.Empty(runner.Calls);
        var sha = new string('a', 40);
        Assert.True((await kc.DeleteIdentityAsync(sha, CancellationToken.None)).Succeeded);
        Assert.Equal(["delete-identity", "-Z", sha.ToUpperInvariant()], runner.Calls.Single().Arguments);
        await kc.ListAsync(CancellationToken.None);
        Assert.Equal(["find-identity"], runner.Calls[1].Arguments);
        Assert.Equal(["find-certificate", "-a", "-p"], runner.Calls[2].Arguments);
    }

    [Fact]
    public async Task Keychain_listing_reads_every_identity_with_its_problem_and_certificates_without_keys()
    {
        using var withKey = Development("With Key");
        using var noKey = Distribution();
        using var expired = Development("Expired", notAfter: Now.AddDays(-5));
        string Sha(X509Certificate2 c) => Info(c).Sha1;
        var identities =
            "Policy: X.509 Basic\n  Matching identities\n" +
            $"  1) {Sha(withKey)} \"Apple Development: With Key ({Team})\"\n" +
            $"  2) {Sha(expired)} \"Apple Development: Expired ({Team})\" (CSSMERR_TP_CERT_EXPIRED)\n" +
            "     2 identities found\n\n  Valid identities only\n" +
            $"  1) {Sha(withKey)} \"Apple Development: With Key ({Team})\"\n     1 valid identities found\n";
        var pem = string.Concat(new[] { withKey, noKey, expired }.Select(c => c.ExportCertificatePem() + "\n"));
        var kc = new MacAppleKeychain(Security(identities, pem), supported: true);
        var listing = await kc.ListAsync(CancellationToken.None);
        Assert.Equal(2, listing.Identities.Count);
        Assert.Equal("CSSMERR_TP_CERT_EXPIRED", listing.Identities.Single(i => i.Sha1 == Sha(expired)).Problem);
        var inv = await new AppleSigningAssetService(kc, Store(_dir), new FixedTime(Now)).ReadAsync(null, null, CancellationToken.None);
        Assert.Equal(3, inv.Certificates.Count);
        Assert.False(inv.Certificate(Sha(noKey))!.HasPrivateKey);
        Assert.Equal(SigningReadiness.Expired, inv.Certificate(Sha(expired))!.Readiness(Now));
        Assert.Equal(SigningReadiness.Ready, inv.Certificate(Sha(withKey))!.Readiness(Now));
    }

    [Fact]
    public void Account_metadata_is_an_allow_list_of_public_details()
    {
        using var cert = Development();
        var info = Info(cert);
        var record = new AccountSigningAssetRecord { Id = "", AssetType = "certificate", Sha1 = info.Sha1, Sha256 = info.Sha256, TeamId = Team, DisplayName = info.CommonName };
        SigningAccountService.Validate(record);
        Assert.Throws<HcgApiException>(() => SigningAccountService.Validate(record with { Sha1 = "-----BEGIN PRIVATE KEY-----" }));
        Assert.Throws<HcgApiException>(() => SigningAccountService.Validate(record with { AssetType = "private-key" }));
        Assert.Throws<HcgApiException>(() => SigningAccountService.Validate(record with { ProfileUuid = Guid.NewGuid().ToString() }));
        Assert.Throws<HcgApiException>(() => SigningAccountService.Validate(record with { TeamId = "not a team" }));

        var prefs = AccountProjectSigningPreferences.From(new ProjectSigningConfiguration
        {
            ProjectId = "p", TeamId = Team, IdentitySha1 = info.Sha1, ProfileUuid = Guid.NewGuid().ToString(), IntendedDevices = ["00008110-000A1B2C3D4E5F60"],
            Targets = [new TargetSigningConfiguration { TargetName = "App", BundleIdentifier = "com.example.app", Entitlements = new Dictionary<string, string> { ["aps-environment"] = "production" } }],
        });
        var json = JsonSerializer.Serialize(prefs, HcgApiClient.Json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["platform", "mode", "route", "teamId", "identitySha1", "profileUuid", "targets"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("00008110", json);   // device UDIDs stay on this Mac
        Assert.DoesNotContain("aps-environment", json);
    }

    [Fact]
    public void The_encrypted_vault_is_reported_unavailable_and_API_keys_are_separate_from_signing()
    {
        Assert.False(EncryptedSigningVault.Available);
        Assert.Contains("not available", EncryptedSigningVault.UnavailableReason);
        Assert.Empty(AppStoreConnectKeys.Validate(new AppStoreConnectApiKeyReference { KeyId = "2X9R4HXF34", IssuerId = Guid.NewGuid().ToString() }));
        Assert.Contains(AppStoreConnectKeys.Validate(new AppStoreConnectApiKeyReference { KeyId = "2X9R4HXF34" }), p => p.Contains("Issuer ID"));
        Assert.Contains(AppStoreConnectKeys.Validate(new AppStoreConnectApiKeyReference { KeyId = "2X9R4HXF34", Type = AppStoreConnectKeyType.Individual, IssuerId = Guid.NewGuid().ToString() }),
            p => p.Contains("do not use an Issuer ID"));
        Assert.Contains(AppStoreConnectKeys.Validate(new AppStoreConnectApiKeyReference { KeyId = "short" }), p => p.Contains("Key ID"));
        Assert.Contains("not an app-signing certificate", AppStoreConnectKeys.Explanation);
    }
}

public class ProjectSigningStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Configuration_round_trips_and_the_device_id_is_stable()
    {
        var db = new SqliteDatabase(_dir.Combine("data", "test.db"));
        await db.MigrateAsync();
        var store = new SqliteProjectSigningStore(db);
        Assert.Null(await store.LoadAsync("p1"));
        var cfg = new ProjectSigningConfiguration
        {
            ProjectId = "p1", Platform = SigningPlatform.MacOs, Mode = ProjectSigningMode.Manual, Route = SigningRoute.MacDeveloperId, TeamId = Team,
            IdentitySha1 = new string('A', 40), Targets = [new TargetSigningConfiguration { TargetName = "Helper", BundleIdentifier = "com.example.app.helper", IsEmbedded = true }],
            UpdatedAt = Now,
        };
        await store.SaveAsync(cfg);
        var loaded = await store.LoadAsync("p1");
        Assert.Equal(SigningRoute.MacDeveloperId, loaded!.Route);
        Assert.Equal("Helper", Assert.Single(loaded.Targets).TargetName);
        var id = await store.DeviceIdAsync();
        Assert.Equal(id, await store.DeviceIdAsync());
        await store.DeleteAsync("p1");
        Assert.Null(await store.LoadAsync("p1"));
    }
}

/// <summary>The pre-build check as the iOS build runs it: project targets, their .entitlements files and the build options.</summary>
public class SigningPrebuildCheckTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private sealed class MemoryStore : IProjectSigningStore
    {
        public Dictionary<string, ProjectSigningConfiguration> Items { get; } = [];
        public Task<ProjectSigningConfiguration?> LoadAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(projectId));
        public Task SaveAsync(ProjectSigningConfiguration configuration, CancellationToken cancellationToken = default) { Items[configuration.ProjectId] = configuration; return Task.CompletedTask; }
        public Task DeleteAsync(string projectId, CancellationToken cancellationToken = default) { Items.Remove(projectId); return Task.CompletedTask; }
        public Task<string> DeviceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult("device");
    }

    private (ProjectRecord Project, MobileAnalysis Analysis, HCG.ExecutableMaker.BuildEngine.Signing.SigningPrebuildCheck Check, MemoryStore Store, AppleProvisioningProfileInfo AppProfile, AppleProvisioningProfileInfo WidgetProfile)
        Setup(X509Certificate2 dist)
    {
        var project = new ProjectRecord { Id = Guid.NewGuid(), Name = "Demo", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/orig", WorkspacePath = _dir.Combine("ws"), ImportedAt = Now };
        _dir.Write("ws/source/App/App.entitlements",
            "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>aps-environment</key><string>production</string></dict></plist>");
        var analysis = new MobileAnalysis
        {
            Framework = MobileFramework.NativeIos,
            Ios = new IosProjectInfo
            {
                ProjectPath = "Demo.xcodeproj",
                Targets =
                [
                    new IosTargetInfo { Name = "App", ProductType = "com.apple.product-type.application", BundleIdentifier = "com.example.app" },
                    new IosTargetInfo { Name = "Widget", ProductType = "com.apple.product-type.app-extension", BundleIdentifier = "com.example.app.widget" },
                    new IosTargetInfo { Name = "AppTests", ProductType = "com.apple.product-type.bundle.unit-test", BundleIdentifier = "com.example.app.tests" },
                ],
            },
        };
        var keychain = new FakeKeychain();
        keychain.Add(dist, hasKey: true);
        var store = Store(_dir);
        var appSpec = new ProfileSpec { Name = "Demo App Store", Certificates = [dist], Entitlements = new Dictionary<string, object> { ["aps-environment"] = "production" } };
        var widgetSpec = new ProfileSpec { Name = "Demo Widget App Store", Certificates = [dist], AppId = Team + ".com.example.app.widget" };
        Directory.CreateDirectory(_dir.Combine("dl"));
        File.WriteAllBytes(_dir.Combine("dl", "a.mobileprovision"), Profile(appSpec));
        File.WriteAllBytes(_dir.Combine("dl", "w.mobileprovision"), Profile(widgetSpec));
        var appProfile = store.Import(_dir.Combine("dl", "a.mobileprovision"), Now).Profile!;
        var widgetProfile = store.Import(_dir.Combine("dl", "w.mobileprovision"), Now).Profile!;
        var signingStore = new MemoryStore();
        var check = new HCG.ExecutableMaker.BuildEngine.Signing.SigningPrebuildCheck(new AppleSigningAssetService(keychain, store, new FixedTime(Now)), signingStore, new FixedTime(Now));
        return (project, analysis, check, signingStore, appProfile, widgetProfile);
    }

    private static ToolchainReport Report() =>
        new(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"),
            [new ToolchainInfo { Id = ToolchainIds.Xcode, DisplayName = "Xcode", Category = "Apple", Status = ToolchainStatus.Installed, Version = "26.3", ExecutablePath = "/usr/bin/xcodebuild" }],
            null, null, Now);

    private static MobileBuildOptions Ipa(string profile) => new()
    {
        Framework = MobileFramework.NativeIos, Output = MobileOutput.IosIpa, Scheme = "App", Signing = MobileSigningMode.AppleManual,
        DevelopmentTeam = Team, ProvisioningProfile = profile, ExportMethod = IosExportMethod.AppStoreConnect,
    };

    [Fact]
    public async Task Each_target_is_checked_with_the_entitlements_from_its_file_and_the_extension_needs_its_own_profile()
    {
        using var dist = Distribution();
        var (project, analysis, check, store, appProfile, widgetProfile) = Setup(dist);
        var report = await check.CheckIosAsync(project, Ipa("Demo App Store"), analysis, project.SourceDirectory, Report(), CancellationToken.None);
        Assert.NotNull(report);
        Assert.Equal(SigningRoute.IosAppStore, report.Configuration.Route);
        Assert.Equal(Info(dist).Sha1, report.Configuration.IdentitySha1);          // picked from the profile, like Xcode
        Assert.Contains(report.Checks, c => c.Target == "App" && c.Category == SigningCheckCategory.Entitlements && c.Outcome == SigningCheckOutcome.Passed && c.Detail.Contains("1 requested"));
        Assert.Contains(report.Failures, c => c.Target == "Widget" && c.Category == SigningCheckCategory.Profile);
        Assert.DoesNotContain(report.Checks, c => c.Target == "AppTests");

        // With the widget's profile saved for the project, everything this Mac can check passes.
        await store.SaveAsync(new ProjectSigningConfiguration
        {
            ProjectId = project.Id.ToString(), Targets = [new TargetSigningConfiguration { TargetName = "Widget", ProfileUuid = widgetProfile.Uuid, IsEmbedded = true }],
        });
        var fixedReport = await check.CheckIosAsync(project, Ipa(appProfile.Uuid), analysis, project.SourceDirectory, Report(), CancellationToken.None);
        if (OperatingSystem.IsMacOS()) Assert.False(fixedReport!.HasFailures, string.Join(" | ", fixedReport.Failures.Select(f => f.Detail)));
        else Assert.Equal([SigningCheckCategory.Tools], fixedReport!.Failures.Select(f => f.Category).Distinct());
        Assert.Contains(HCG.ExecutableMaker.BuildEngine.Signing.SigningPrebuildCheck.Describe(fixedReport), l => l.Text.Contains("needs Apple — not checked by HCG"));
    }

    [Fact]
    public async Task Simulator_unsigned_and_project_default_builds_are_not_blocked()
    {
        using var dist = Distribution();
        var (project, analysis, check, _, _, _) = Setup(dist);
        Assert.Null(await check.CheckIosAsync(project, Ipa("x") with { Output = MobileOutput.IosSimulatorApp }, analysis, project.SourceDirectory, Report(), CancellationToken.None));
        Assert.Null(await check.CheckIosAsync(project, Ipa("x") with { Signing = MobileSigningMode.Unsigned }, analysis, project.SourceDirectory, Report(), CancellationToken.None));
        Assert.Null(await check.CheckIosAsync(project, Ipa("x") with { Signing = MobileSigningMode.ProjectDefault }, analysis, project.SourceDirectory, Report(), CancellationToken.None));
    }

    [Fact]
    public async Task A_profile_that_is_not_installed_is_named_in_the_failure()
    {
        using var dist = Distribution();
        var (project, analysis, check, _, _, _) = Setup(dist);
        var report = await check.CheckIosAsync(project, Ipa("Some Other Profile"), analysis, project.SourceDirectory, Report(), CancellationToken.None);
        Assert.Contains(report!.Failures, f => f.Detail.Contains("\"Some Other Profile\" is not installed"));
    }
}

/// <summary>Runs only on macOS with HCG_KEYCHAIN_TESTS=1: it writes a synthetic identity to the login keychain and deletes it again.</summary>
public sealed class RequiresKeychainFactAttribute : FactAttribute
{
    public RequiresKeychainFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "The macOS keychain exists only on macOS.";
        else if (Environment.GetEnvironmentVariable("HCG_KEYCHAIN_TESTS") != "1") Skip = "Set HCG_KEYCHAIN_TESTS=1 to import and delete a synthetic identity in this user's keychain.";
    }
}

public class SigningKeychainIntegrationTests
{
    [RequiresKeychainFact]
    public async Task A_synthetic_p12_is_imported_listed_with_its_key_and_deleted_from_the_real_keychain()
    {
        using var cert = Issue($"Apple Development: HCG Keychain Test {Guid.NewGuid():N} ({Team})", Team, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var runner = new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner();
        var keychain = new MacAppleKeychain(runner);
        using var dir = new TempDir();
        var service = new AppleSigningAssetService(keychain, Store(dir), TimeProvider.System);
        var sha1 = Info(cert).Sha1;
        try
        {
            var result = await service.ImportPkcs12Async(Pkcs12(cert, "keychain-test"), "keychain-test", CancellationToken.None);
            Assert.True(result.Status == SigningImportStatus.Imported, result.Message);
            var listing = await keychain.ListAsync(CancellationToken.None);
            var identity = Assert.Single(listing.Identities, i => i.Sha1 == sha1);
            Assert.NotNull(identity.Problem); // signed by a synthetic root: macOS reports it as not trusted, and HCG shows that
            var again = await service.ImportPkcs12Async(Pkcs12(cert, "keychain-test"), "keychain-test", CancellationToken.None);
            Assert.Equal(SigningImportStatus.Duplicate, again.Status);
        }
        finally
        {
            var deleted = await keychain.DeleteIdentityAsync(sha1, CancellationToken.None);
            Assert.True(deleted.Succeeded, deleted.Problem);
        }
        Assert.DoesNotContain((await keychain.ListAsync(CancellationToken.None)).Identities, i => i.Sha1 == sha1);
    }
}
