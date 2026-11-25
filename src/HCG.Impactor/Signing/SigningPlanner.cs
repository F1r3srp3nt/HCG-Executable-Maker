using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Signing;
using HCG.Impactor.Devices;
using HCG.Impactor.Model;

namespace HCG.Impactor.Signing;

/// <summary>Apple distribution methods HCG Impactor signs for. Each is decided by the provisioning profile's type.</summary>
public enum DistributionMethod
{
    /// <summary>Apple Development certificate + Development profile: registered devices, debugging allowed.</summary>
    Development,
    /// <summary>Apple Distribution certificate + Ad Hoc profile: registered devices, release build.</summary>
    AdHoc,
    /// <summary>Apple Distribution of an Enterprise Program team + In-House profile: the organisation's own devices.</summary>
    Enterprise,
    /// <summary>Apple Distribution + App Store profile: for upload to App Store Connect/TestFlight only — not installable directly.</summary>
    AppStore,
}

/// <summary>A provisioning profile HCG can sign with: the verified summary plus its raw entitlement values and file.</summary>
public sealed record ProfileEntry(AppleProvisioningProfileInfo Info, IReadOnlyDictionary<string, object> RawEntitlements, string? Path)
{
    public string? Team => Info.TeamIds.FirstOrDefault();
    public DistributionMethod? Method => Info.Kind switch
    {
        AppleProfileKind.Development => DistributionMethod.Development,
        AppleProfileKind.AdHoc => DistributionMethod.AdHoc,
        AppleProfileKind.Enterprise => DistributionMethod.Enterprise,
        AppleProfileKind.AppStore => DistributionMethod.AppStore,
        _ => null,
    };
    public bool IsIos => Info.Platforms.Count == 0 || Info.Platforms.Contains(ApplePlatformNames.Ios);
}

/// <summary>Signing identities and profiles available on this computer (from the Keychain and the profile folders).</summary>
public sealed record SigningInventorySnapshot(IReadOnlyList<AppleCertificateInfo> Certificates, IReadOnlyList<ProfileEntry> Profiles)
{
    public IEnumerable<AppleCertificateInfo> IosSigningIdentities(DateTimeOffset now) =>
        Certificates.Where(c => c.HasPrivateKey == true && c.Kind is AppleCertificateKind.Development or AppleCertificateKind.AppleDistribution);
}

public sealed record SigningRequest
{
    /// <summary>SHA-1 of the identity to sign with; null = choose automatically.</summary>
    public string? IdentitySha1 { get; init; }
    /// <summary>Profile UUID for the main app; null = choose automatically.</summary>
    public string? ProfileUuid { get; init; }
    /// <summary>Profile UUIDs for nested bundles by relative path ("PlugIns/Share.appex").</summary>
    public IReadOnlyDictionary<string, string> ProfileOverrides { get; init; } = new Dictionary<string, string>();
    /// <summary>Restrict to one distribution method; null = any installable method (or what the chosen profile is).</summary>
    public DistributionMethod? Method { get; init; }
    /// <summary>New bundle identifier for the main app (extensions keep their suffix). Null keeps the original.</summary>
    public string? NewBundleIdentifier { get; init; }
    /// <summary>The device the app is meant for; enables the registration check.</summary>
    public AppleDevice? TargetDevice { get; init; }
    /// <summary>Remove entitlements the profile does not allow instead of stopping (each removal is reported).</summary>
    public bool DropUnsupportedEntitlements { get; init; }
    /// <summary>Days before expiry that count as "soon".</summary>
    public int ExpiryWarningDays { get; init; } = 14;
}

/// <summary>What HCG will do to one bundle.</summary>
public sealed record BundleAssignment
{
    public required BundleInfo Bundle { get; init; }
    public string? NewBundleIdentifier { get; init; }
    public ProfileEntry? Profile { get; init; }
    /// <summary>Entitlements to sign with (null for frameworks and dylibs, which are signed without entitlements).</summary>
    public IReadOnlyDictionary<string, object>? Entitlements { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string Label => Bundle.RelativePath.Length == 0 ? "The app" : Bundle.RelativePath;
    public string? FinalBundleIdentifier => NewBundleIdentifier ?? Bundle.BundleIdentifier;
}

public sealed record SigningPlan
{
    public AppleCertificateInfo? Identity { get; init; }
    public DistributionMethod? Method { get; init; }
    public IReadOnlyList<BundleAssignment> Assignments { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    /// <summary>When the signed app stops launching: the earliest of the certificate's and the profiles' expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsReady => Identity is not null && Assignments.Count > 0 && !Findings.HasBlockers();
    public bool Installable => Method is DistributionMethod.Development or DistributionMethod.AdHoc or DistributionMethod.Enterprise;
    public BundleAssignment? Main => Assignments.FirstOrDefault(a => a.Bundle.Kind == BundleKind.App);
}

/// <summary>
/// Decides how an inspected IPA will be signed: the identity, the provisioning profile of each bundle that carries
/// entitlements, and the entitlements themselves. Every rule is Apple's own (profile certificate list, App ID, team,
/// entitlement grants, device list, expiry); nothing is bypassed — when a rule is not met the plan says what to change.
/// Pure logic: portable and unit-tested without Apple tools.
/// </summary>
public static class SigningPlanner
{
    public static SigningPlan Plan(IpaReport ipa, SigningInventorySnapshot inventory, SigningRequest request, DateTimeOffset now)
    {
        var findings = new List<Finding>();
        if (ipa.App is null) return new SigningPlan { Findings = [Finding.Block("plan.no-app", "Nothing to sign", "Inspect a valid IPA first.")] };
        findings.AddRange(ipa.Findings.Where(f => f.Code is "binary.encrypted" or "binary.simulator" or "binary.not-ios" or "binary.32-bit" or "app.no-bundle-id"
            or "ipa.multiple-apps" or "bundle.no-executable" or "bundle.no-info-plist"));
        if (findings.HasBlockers()) return new SigningPlan { Findings = findings };

        var oldMain = ipa.App.BundleIdentifier!;
        var newMain = string.IsNullOrWhiteSpace(request.NewBundleIdentifier) ? null : request.NewBundleIdentifier!.Trim();
        if (newMain is not null && !BundleIds.IsValid(newMain))
            return new SigningPlan { Findings = [Finding.Block("plan.bundle-id-invalid", "Invalid bundle identifier", $"\"{newMain}\" is not a valid bundle identifier (letters, digits, '-' and '.', reverse-DNS).")] };
        string FinalId(BundleInfo b) => newMain is null || b.BundleIdentifier is null ? b.BundleIdentifier! : BundleIds.Rebase(b.BundleIdentifier, oldMain, newMain);

        var entitled = ipa.AllBundles.Where(b => b.TakesEntitlements && b.BundleIdentifier is not null).ToList();
        var identities = inventory.IosSigningIdentities(now).ToList();

        // ---- identity
        AppleCertificateInfo? identity;
        if (request.IdentitySha1 is { } sha)
        {
            identity = inventory.Certificates.FirstOrDefault(c => string.Equals(c.Sha1, sha, StringComparison.OrdinalIgnoreCase));
            if (identity is null)
                return new SigningPlan { Findings = [Finding.Block("identity.not-found", "Signing identity not found", $"No certificate with SHA-1 {sha} is in this Mac's keychain.", "Choose one of the identities Certificate Manager lists.")] };
        }
        else
        {
            identity = ChooseIdentity(identities, entitled, inventory.Profiles, request, now, FinalId);
            if (identity is null)
            {
                findings.Add(identities.Count == 0
                    ? Finding.Block("identity.none", "No iOS signing identity", "This Mac's keychain has no Apple Development or Apple Distribution certificate with its private key.",
                        "Sign in to your Apple Account in Xcode › Settings › Accounts and choose Manage Certificates › + (Apple Development), or import your .p12 in Signing & Security.")
                    : Finding.Block("identity.no-profile-match", "No identity matches an installed profile",
                        $"None of your {identities.Count} signing identit{(identities.Count == 1 ? "y is" : "ies are")} included in a provisioning profile for {FinalId(ipa.App)} that is valid now.",
                        "Download a profile for this App ID that includes your certificate (Xcode › Settings › Accounts › Download Manual Profiles, or developer.apple.com › Profiles)."));
                return new SigningPlan { Findings = findings };
            }
        }
        CheckIdentity(identity, request, now, findings);

        // ---- per-bundle profiles and entitlements
        var assignments = new List<BundleAssignment>();
        var expiry = identity.NotAfter;
        DistributionMethod? method = request.Method;
        foreach (var b in ipa.SigningOrder)
        {
            if (!b.TakesEntitlements || b.BundleIdentifier is null)
            {
                assignments.Add(new BundleAssignment { Bundle = b });
                continue;
            }
            var finalId = FinalId(b);
            var label = b.RelativePath.Length == 0 ? "The app" : b.RelativePath;
            var wanted = b.Kind == BundleKind.App ? request.ProfileUuid : request.ProfileOverrides.GetValueOrDefault(b.RelativePath);
            ProfileEntry? profile;
            if (wanted is not null)
            {
                profile = inventory.Profiles.FirstOrDefault(p => string.Equals(p.Info.Uuid, wanted, StringComparison.OrdinalIgnoreCase));
                if (profile is null) { findings.Add(Finding.Block("profile.not-found", "Profile not found", $"{label}: no installed profile has UUID {wanted}.") with { Subject = label }); continue; }
                foreach (var problem in ProfileProblems(profile, identity, finalId, method, request.TargetDevice, now))
                    findings.Add(problem with { Subject = label });
            }
            else
            {
                profile = Candidates(inventory.Profiles, identity, finalId, method, now)
                    .OrderByDescending(p => Score(p, finalId, request.TargetDevice))
                    .ThenByDescending(p => p.Info.Expiration)
                    .FirstOrDefault();
                if (profile is null)
                {
                    findings.Add(Finding.Block("profile.none", "No matching provisioning profile",
                        $"{label} ({finalId}) has no installed iOS profile that is valid now, covers this App ID and includes {identity.CommonName}.",
                        $"Create or download a profile for App ID {identity.TeamId}.{finalId} with this certificate (developer.apple.com › Profiles), then import it in Provisioning Manager.") with { Subject = label });
                    continue;
                }
                foreach (var problem in ProfileProblems(profile, identity, finalId, method, request.TargetDevice, now).Where(f => f.Severity != FindingSeverity.Blocker || f.Code == "device.unregistered"))
                    findings.Add(problem with { Subject = label });
            }
            method ??= profile.Method;
            if (profile.Method != method)
                findings.Add(Finding.Block("profile.method-mixed", "Mixed distribution types", $"{label} uses a {profile.Method} profile while the app is signed for {method}. All bundles must use the same type.") with { Subject = label });
            if (profile.Info.Expiration is { } pe && pe < expiry) expiry = pe;
            var (ent, notes, entFindings) = EntitlementComposer.Compose(b, profile, finalId, request.DropUnsupportedEntitlements);
            findings.AddRange(entFindings.Select(f => f with { Subject = label }));
            assignments.Add(new BundleAssignment { Bundle = b, NewBundleIdentifier = finalId != b.BundleIdentifier ? finalId : null, Profile = profile, Entitlements = ent, Notes = notes });
        }

        if (method == DistributionMethod.AppStore)
            findings.Add(Finding.Warn("method.app-store", "App Store signing is not installable",
                "An App Store profile produces an IPA for App Store Connect/TestFlight. iPhones and iPads refuse to install it directly, and HCG does not upload to App Store Connect.",
                "Use a Development or Ad Hoc profile to install on your devices."));
        if (identity.Kind == AppleCertificateKind.Development && method is DistributionMethod.AdHoc or DistributionMethod.AppStore or DistributionMethod.Enterprise)
            findings.Add(Finding.Block("identity.kind", "Development certificate with a distribution profile", $"{identity.CommonName} is a development certificate; {method} needs Apple Distribution."));
        if (identity.Kind == AppleCertificateKind.AppleDistribution && method == DistributionMethod.Development)
            findings.Add(Finding.Block("identity.kind", "Distribution certificate with a development profile", $"{identity.CommonName} is a distribution certificate; Development profiles need Apple Development."));

        if (newMain is not null && newMain != oldMain)
            findings.Add(Finding.Warn("bundle-id.changed", "Bundle identifier changes",
                $"{oldMain} → {newMain}. iOS treats this as a different app: it installs next to the original, and the original's data, keychain items and settings are not available to it.",
                "Keep the original identifier to update an existing installation."));
        var originalTeam = ipa.App.Signature.TeamId;
        if (originalTeam is not null && identity.TeamId is not null && originalTeam != identity.TeamId && newMain is null)
            findings.Add(Finding.Warn("team.changed", "Signing team changes",
                $"The IPA was signed by team {originalTeam}; it will be signed by {identity.TeamId}. If the original is installed on the device, iOS refuses the update (application-identifier mismatch) until it is removed — removing an app deletes its data, and HCG never removes apps.",
                "Back up the app's data first if it matters, or install under a new bundle identifier."));
        if (expiry - now < TimeSpan.FromDays(request.ExpiryWarningDays) && expiry > now)
            findings.Add(Finding.Warn("expiry.soon", "Signature expires soon", $"The signed app stops launching on {expiry:yyyy-MM-dd} (certificate or profile expiry).",
                "Signing Monitor reminds you; re-sign with renewed assets before then."));

        return new SigningPlan { Identity = identity, Method = method, Assignments = assignments, Findings = findings, ExpiresAt = expiry };
    }

    // ---------------------------------------------------------------- choices

    private static AppleCertificateInfo? ChooseIdentity(List<AppleCertificateInfo> identities, List<BundleInfo> entitled, IReadOnlyList<ProfileEntry> profiles,
        SigningRequest request, DateTimeOffset now, Func<BundleInfo, string> finalId)
    {
        // Ready identities first; ones macOS reports a trust problem for (e.g. a missing WWDR intermediate) stay possible
        // with a warning, because codesign decides whether the chain is acceptable.
        var usable = identities.Where(c => c.Readiness(now) is SigningReadiness.Ready or SigningReadiness.TrustProblem)
            .OrderBy(c => c.Readiness(now) == SigningReadiness.Ready ? 0 : 1).ToList();
        // An identity qualifies when every entitled bundle has a valid profile that includes it.
        var qualified = usable.Where(id => entitled.All(b => Candidates(profiles, id, finalId(b), request.Method, now).Any())).ToList();
        if (qualified.Count == 0) return null;
        // Prefer: profiles that include the target device, installable methods, Development when a device is the target, then the latest expiry.
        return qualified
            .OrderByDescending(id => request.TargetDevice?.Udid is { } udid && entitled.All(b => Candidates(profiles, id, finalId(b), request.Method, now).Any(p => p.Info.ProvisionedDevices.Contains(udid, StringComparer.OrdinalIgnoreCase) || p.Info.ProvisionsAllDevices)))
            .ThenByDescending(id => entitled.All(b => Candidates(profiles, id, finalId(b), request.Method, now).Any(p => p.Method != DistributionMethod.AppStore)))
            .ThenByDescending(id => id.Kind == AppleCertificateKind.Development)
            .ThenByDescending(id => id.NotAfter)
            .First();
    }

    /// <summary>Profiles that can sign <paramref name="bundleId"/> with <paramref name="identity"/> now.</summary>
    public static IEnumerable<ProfileEntry> Candidates(IReadOnlyList<ProfileEntry> profiles, AppleCertificateInfo identity, string bundleId, DistributionMethod? method, DateTimeOffset now) =>
        profiles.Where(p => p.IsIos && p.Info.Signature != ProfileSignatureStatus.Invalid
            && p.Info.Expiration > now
            && p.Info.CertificateSha1s.Contains(identity.Sha1, StringComparer.OrdinalIgnoreCase)
            && AppIdCovers(p.Info.ApplicationIdentifier, p.Team, bundleId)
            && p.Method is not null && (method is null || p.Method == method));

    private static int Score(ProfileEntry p, string bundleId, AppleDevice? device)
    {
        var score = 0;
        if (device?.Udid is { } udid && (p.Info.ProvisionedDevices.Contains(udid, StringComparer.OrdinalIgnoreCase) || p.Info.ProvisionsAllDevices)) score += 100;
        if (p.Method != DistributionMethod.AppStore) score += 50;
        if (p.Info.ApplicationIdentifier == $"{p.Team}.{bundleId}") score += 20; // explicit App ID beats wildcard
        if (p.Info.Signature == ProfileSignatureStatus.VerifiedApple) score += 5;
        return score;
    }

    /// <summary>Does the profile's application-identifier ("TEAM.com.x.app" or "TEAM.*" / "TEAM.com.x.*") cover the bundle id?</summary>
    public static bool AppIdCovers(string? appId, string? team, string bundleId)
    {
        if (appId is null || team is null || !appId.StartsWith(team + ".", StringComparison.Ordinal)) return false;
        var pattern = appId[(team.Length + 1)..];
        return pattern == bundleId || pattern == "*" || (pattern.EndsWith(".*", StringComparison.Ordinal) && bundleId.StartsWith(pattern[..^1], StringComparison.Ordinal));
    }

    private static void CheckIdentity(AppleCertificateInfo identity, SigningRequest request, DateTimeOffset now, List<Finding> findings)
    {
        switch (identity.Readiness(now))
        {
            case SigningReadiness.Ready: break;
            case SigningReadiness.Expired:
                findings.Add(Finding.Block("identity.expired", "Certificate expired", $"{identity.CommonName} expired on {identity.NotAfter:yyyy-MM-dd}.", "Create a new certificate in Xcode › Settings › Accounts › Manage Certificates. HCG never revokes or replaces certificates."));
                break;
            case SigningReadiness.PrivateKeyMissing:
                findings.Add(Finding.Block("identity.no-key", "Private key not on this Mac", $"{identity.CommonName} is in the keychain without its private key, so it cannot sign.", "Import the .p12 exported from the Mac that created it, or create a new certificate in Xcode."));
                break;
            case SigningReadiness.TrustProblem:
                findings.Add(Finding.Warn("identity.trust", "Certificate trust problem", $"macOS reports: {identity.TrustProblem}. codesign may refuse it.", "Install Apple's WWDR intermediate certificate (Xcode installs it), then check again."));
                break;
            case var other:
                findings.Add(Finding.Block("identity.not-ready", "Identity not usable", $"{identity.CommonName}: {other}."));
                break;
        }
        if (identity.Kind is not (AppleCertificateKind.Development or AppleCertificateKind.AppleDistribution))
            findings.Add(Finding.Block("identity.wrong-type", "Not an iOS signing certificate", $"{identity.CommonName} cannot sign iOS apps. Use Apple Development or Apple Distribution."));
        if (identity.NotAfter > now && identity.NotAfter - now < TimeSpan.FromDays(request.ExpiryWarningDays))
            findings.Add(Finding.Warn("identity.expires-soon", "Certificate expires soon", $"{identity.CommonName} expires on {identity.NotAfter:yyyy-MM-dd}."));
    }

    /// <summary>Everything wrong with using <paramref name="p"/> for <paramref name="bundleId"/> (used to validate a user's choice).</summary>
    public static IEnumerable<Finding> ProfileProblems(ProfileEntry p, AppleCertificateInfo identity, string bundleId, DistributionMethod? method, AppleDevice? device, DateTimeOffset now)
    {
        var name = $"\"{p.Info.Name}\"";
        if (p.Info.Signature == ProfileSignatureStatus.Invalid)
            yield return Finding.Block("profile.invalid", "Profile damaged", $"{name}'s signature does not match its contents.", "Download it again.");
        else if (p.Info.Signature == ProfileSignatureStatus.IntactUntrustedSigner)
            yield return Finding.Warn("profile.untrusted", "Profile not verified as Apple's", $"{name} is intact, but its signer does not chain to an Apple root trusted on this computer. Devices only accept Apple-issued profiles.");
        if (!p.IsIos)
            yield return Finding.Block("profile.platform", "Not an iOS profile", $"{name} is for {string.Join(", ", p.Info.Platforms)}.");
        if (p.Info.Expiration is { } exp && exp <= now)
            yield return Finding.Block("profile.expired", "Profile expired", $"{name} expired on {exp:yyyy-MM-dd}.", "Regenerate it at developer.apple.com › Profiles (or let Xcode refresh it) and import it.");
        else if (p.Info.Expiration is { } soon && soon - now < TimeSpan.FromDays(14))
            yield return Finding.Warn("profile.expires-soon", "Profile expires soon", $"{name} expires on {soon:yyyy-MM-dd}.");
        if (!p.Info.CertificateSha1s.Contains(identity.Sha1, StringComparer.OrdinalIgnoreCase))
            yield return Finding.Block("profile.certificate", "Certificate not in profile", $"{name} does not include {identity.CommonName} ({identity.ShortSha1}).",
                "Edit the profile at developer.apple.com to include this certificate, download it and import it — or choose the certificate the profile lists.");
        if (identity.TeamId is not null && p.Team is not null && identity.TeamId != p.Team)
            yield return Finding.Block("profile.team", "Team mismatch", $"{name} belongs to team {p.Team}; the certificate belongs to {identity.TeamId}.");
        if (!AppIdCovers(p.Info.ApplicationIdentifier, p.Team, bundleId))
            yield return Finding.Block("profile.app-id", "App ID does not match", $"{name} is for {p.Info.ApplicationIdentifier}, which does not cover {bundleId}.",
                $"Use a profile for {p.Team}.{bundleId} (or a wildcard that covers it).");
        if (method is not null && p.Method != method)
            yield return Finding.Block("profile.method", "Wrong distribution type", $"{name} is a {p.Method} profile; {method} was requested.");
        if (p.Method is DistributionMethod.Development or DistributionMethod.AdHoc && device is not null)
        {
            if (device.Udid is null)
                yield return Finding.Warn("device.registration-unknown", "Device registration not checked", $"{device.Name} did not report its UDID, so HCG cannot check {name}'s device list.");
            else if (!p.Info.ProvisionedDevices.Contains(device.Udid, StringComparer.OrdinalIgnoreCase))
                yield return Finding.Block("device.unregistered", "Device not registered in the profile",
                    $"{device.Name} ({AppleDeviceContract.Redact(device.Udid)}) is not in {name}'s device list ({p.Info.ProvisionedDevices.Count} devices). iOS refuses to install the app.",
                    "Register the device at developer.apple.com › Devices (or connect it with Xcode open, which registers it), regenerate the profile, download and import it.");
        }
    }
}

/// <summary>Bundle identifier helpers.</summary>
public static class BundleIds
{
    public static bool IsValid(string id) => id.Length is > 0 and <= 155 && !id.StartsWith('.') && !id.EndsWith('.') && !id.Contains("..")
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');

    /// <summary>Rebases an identifier under a renamed main app: "old.app.share" → "new.app.share".</summary>
    public static string Rebase(string id, string oldMain, string newMain) =>
        id == oldMain ? newMain : id.StartsWith(oldMain + ".", StringComparison.Ordinal) ? newMain + id[oldMain.Length..] : id;
}
