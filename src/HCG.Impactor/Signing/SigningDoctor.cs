using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Signing;
using HCG.Impactor.Devices;
using HCG.Impactor.Model;

namespace HCG.Impactor.Signing;

/// <summary>What a signing check looks at, in the order the Signing Doctor reports them.</summary>
public enum ProfileCheckKind
{
    Platform,
    ProfileSignature,
    BundleIdentifier,
    Team,
    Certificate,
    CertificateExpiry,
    ProfileExpiry,
    ProfileType,
    Device,
    Entitlements,
}

public sealed record ProfileCheck(ProfileCheckKind Kind, bool Passed, string Detail)
{
    public string Title => Kind switch
    {
        ProfileCheckKind.Platform => "Platform",
        ProfileCheckKind.ProfileSignature => "Profile signature",
        ProfileCheckKind.BundleIdentifier => "Bundle identifier",
        ProfileCheckKind.Team => "Apple Developer team",
        ProfileCheckKind.Certificate => "Certificate in profile",
        ProfileCheckKind.CertificateExpiry => "Certificate expiration",
        ProfileCheckKind.ProfileExpiry => "Profile expiration",
        ProfileCheckKind.ProfileType => "Development / distribution type",
        ProfileCheckKind.Device => "Registered device",
        _ => "Entitlements",
    };
}

/// <summary>One installed profile examined for the requested app.</summary>
public sealed record ProfileVerdict(ProfileEntry Profile, IReadOnlyList<ProfileCheck> Checks, AppleCertificateInfo? Identity)
{
    public bool Usable => Checks.All(c => c.Passed);
    public IEnumerable<ProfileCheck> Failures => Checks.Where(c => !c.Passed);
    public string Name => Profile.Info.Name;
}

public sealed record SigningDoctorRequest
{
    public required string BundleIdentifier { get; init; }
    /// <summary>The device the app must install on (for the device-list check). Null skips that check.</summary>
    public AppleDevice? Device { get; init; }
    /// <summary>Entitlements the app needs (from its .entitlements file or current signature). Empty: only the basics.</summary>
    public IReadOnlyDictionary<string, object> Entitlements { get; init; } = new Dictionary<string, object>();
    /// <summary>The distribution wanted; null = anything installable on a device (Development, Ad Hoc, Enterprise).</summary>
    public DistributionMethod? Method { get; init; }
    /// <summary>Restrict to one Apple Developer team.</summary>
    public string? TeamId { get; init; }
}

public sealed record SigningDoctorReport
{
    public required SigningDoctorRequest Request { get; init; }
    public IReadOnlyList<AppleCertificateInfo> Identities { get; init; } = [];
    public IReadOnlyList<ProfileVerdict> Profiles { get; init; } = [];
    /// <summary>One line, e.g. "None of the 8 profiles can sign com.example for iPhone …".</summary>
    public required string Summary { get; init; }
    /// <summary>For each check, how many profiles failed it (the most common blockers first).</summary>
    public IReadOnlyList<(ProfileCheckKind Kind, int Count)> FailureCounts { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    /// <summary>What to do, most effective first.</summary>
    public IReadOnlyList<string> NextSteps { get; init; } = [];
    /// <summary>The team HCG would create a profile in (from the usable identities), when there is exactly one.</summary>
    public string? SuggestedTeam { get; init; }
    public bool HasUsableProfile => Profiles.Any(p => p.Usable);
    public ProfileVerdict? Closest => Profiles.OrderBy(p => p.Failures.Count()).FirstOrDefault();
}

/// <summary>
/// HCG Signing Doctor: explains exactly why each installed provisioning profile can or cannot sign an app — bundle
/// identifier (App ID), team, certificate, certificate and profile expiry, registered devices, development vs
/// distribution, entitlements, platform and the profile's own signature — and what to do about it. Read-only and
/// portable; it never changes a profile, a certificate or the Apple Developer account.
/// </summary>
public static class SigningDoctor
{
    public static SigningDoctorReport Diagnose(SigningInventorySnapshot inventory, SigningDoctorRequest request, DateTimeOffset now)
    {
        var bundleId = request.BundleIdentifier.Trim();
        var identities = inventory.Certificates
            .Where(c => c.Kind is AppleCertificateKind.Development or AppleCertificateKind.AppleDistribution)
            .Where(c => request.TeamId is null || c.TeamId == request.TeamId)
            .ToList();
        var withKey = identities.Where(c => c.HasPrivateKey == true).ToList();
        var verdicts = inventory.Profiles.Select(p => Examine(p, bundleId, withKey, identities, request, now)).ToList();
        verdicts = verdicts.OrderByDescending(v => v.Usable).ThenBy(v => v.Failures.Count()).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var findings = new List<Finding>();
        if (!BundleIds.IsValid(bundleId))
            findings.Add(Finding.Block("doctor.bundle-id-invalid", "Invalid bundle identifier", $"\"{bundleId}\" is not a valid bundle identifier."));
        if (identities.Count == 0)
            findings.Add(Finding.Block("doctor.no-identity", "No iOS signing certificate on this Mac",
                "The keychain has no Apple Development or Apple Distribution certificate" + (request.TeamId is null ? "." : $" for team {request.TeamId}."),
                "Xcode › Settings › Accounts › your team › Manage Certificates › + › Apple Development."));
        else if (withKey.Count == 0)
            findings.Add(Finding.Block("doctor.no-private-key", "Certificates without their private key",
                $"{identities.Count} certificate(s) are on this Mac, but none has its private key, so none can sign.",
                "Import the .p12 exported from the Mac that created the certificate, or create a new Apple Development certificate in Xcode (this does not revoke the old one)."));
        foreach (var id in withKey.Where(c => c.NotAfter <= now))
            findings.Add(Finding.Block("doctor.identity-expired", "Certificate expired", $"{id.CommonName} expired on {id.NotAfter:yyyy-MM-dd}.",
                "Create a new certificate in Xcode › Settings › Accounts › Manage Certificates. HCG never revokes certificates."));

        var counts = verdicts.SelectMany(v => v.Failures.Select(f => f.Kind)).GroupBy(k => k).Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2).ToList();
        var usable = verdicts.Where(v => v.Usable).ToList();
        var target = request.Device is null ? "" : $" for {request.Device.Name}";
        var summary = usable.Count > 0
            ? $"{usable.Count} of {verdicts.Count} profile(s) can sign {bundleId}{target}: \"{usable[0].Name}\"" + (usable.Count > 1 ? " and others" : "") + "."
            : verdicts.Count == 0
                ? $"No provisioning profiles are installed on this Mac, so nothing can sign {bundleId}{target}."
                : $"None of the {verdicts.Count} profile(s) can sign {bundleId}{target}. " +
                  string.Join("; ", counts.Take(4).Select(c => $"{c.Item2} fail {Title(c.Key).ToLowerInvariant()}")) + ".";

        var teams = withKey.Where(c => c.NotAfter > now).Select(c => c.TeamId).OfType<string>().Distinct().ToList();
        return new SigningDoctorReport
        {
            Request = request with { BundleIdentifier = bundleId },
            Identities = identities,
            Profiles = verdicts,
            Summary = summary,
            FailureCounts = counts,
            Findings = findings,
            NextSteps = usable.Count > 0 ? [$"Sign with \"{usable[0].Name}\" (Signing Studio picks it automatically)."] : NextSteps(verdicts, bundleId, request, teams),
            SuggestedTeam = request.TeamId ?? (teams.Count == 1 ? teams[0] : null),
        };
    }

    private static ProfileVerdict Examine(ProfileEntry p, string bundleId, List<AppleCertificateInfo> withKey, List<AppleCertificateInfo> all, SigningDoctorRequest r, DateTimeOffset now)
    {
        var checks = new List<ProfileCheck>();
        var info = p.Info;
        var team = p.Team;

        checks.Add(new(ProfileCheckKind.Platform, p.IsIos, p.IsIos ? "iOS profile." : $"For {string.Join(", ", info.Platforms)}, not iOS."));
        checks.Add(new(ProfileCheckKind.ProfileSignature, info.Signature != ProfileSignatureStatus.Invalid,
            info.Signature switch
            {
                ProfileSignatureStatus.VerifiedApple => "Signed by Apple and intact.",
                ProfileSignatureStatus.IntactUntrustedSigner => "Intact, but its signer does not chain to an Apple root trusted on this Mac (devices only accept Apple-issued profiles).",
                _ => "Damaged: its signature does not match its contents. Download it again.",
            }));

        var covers = SigningPlanner.AppIdCovers(info.ApplicationIdentifier, team, bundleId);
        checks.Add(new(ProfileCheckKind.BundleIdentifier, covers, covers
            ? $"App ID {info.ApplicationIdentifier} covers {bundleId}."
            : $"App ID {info.ApplicationIdentifier ?? "(none)"} does not cover {bundleId}" + (info.ApplicationIdentifier is { } a && a.EndsWith(".*", StringComparison.Ordinal) ? " (the wildcard is for another prefix)." : " — it is for a different app.")));

        var teamOk = team is not null && (r.TeamId is null || team == r.TeamId) && all.Any(c => c.TeamId == team);
        checks.Add(new(ProfileCheckKind.Team, teamOk, team is null ? "The profile names no team."
            : r.TeamId is not null && team != r.TeamId ? $"Team {team}; you asked for {r.TeamId}."
            : all.Any(c => c.TeamId == team) ? $"Team {team} ({info.TeamName}) — you have a certificate for it."
            : $"Team {team} ({info.TeamName}); none of your certificates is from this team (yours: {YourTeams(all)})."));

        var inProfile = withKey.Where(c => info.CertificateSha1s.Contains(c.Sha1, StringComparer.OrdinalIgnoreCase)).ToList();
        var inProfileNoKey = all.Where(c => c.HasPrivateKey != true && info.CertificateSha1s.Contains(c.Sha1, StringComparer.OrdinalIgnoreCase)).ToList();
        var identity = inProfile.OrderByDescending(c => c.NotAfter).FirstOrDefault();
        checks.Add(new(ProfileCheckKind.Certificate, identity is not null, identity is not null
            ? $"Includes your certificate {identity.CommonName} ({identity.ShortSha1})."
            : inProfileNoKey.Count > 0
                ? $"Includes {inProfileNoKey[0].CommonName}, but its private key is not on this Mac."
                : $"Includes {info.CertificateSha1s.Count} certificate(s) ({string.Join(", ", info.CertificateSha1s.Take(3).Select(s => s.Length > 8 ? s[..8] : s))}…), none of them yours" +
                  (withKey.Count > 0 ? $" (yours: {string.Join(", ", withKey.Select(c => c.ShortSha1))})." : ".")));

        var certOk = identity is not null && identity.NotAfter > now;
        checks.Add(new(ProfileCheckKind.CertificateExpiry, certOk, identity is null ? "No matching certificate to check."
            : certOk ? $"{identity.CommonName} is valid until {identity.NotAfter:yyyy-MM-dd}."
            : $"{identity.CommonName} expired on {identity.NotAfter:yyyy-MM-dd}."));

        var profOk = info.Expiration is { } exp && exp > now;
        checks.Add(new(ProfileCheckKind.ProfileExpiry, profOk, info.Expiration is null ? "No expiration date." : profOk
            ? $"Valid until {info.Expiration:yyyy-MM-dd}." : $"Expired on {info.Expiration:yyyy-MM-dd}."));

        var method = p.Method;
        var typeOk = method is not null && (r.Method is null ? method != DistributionMethod.AppStore : method == r.Method)
                     && (identity is null || (identity.Kind == AppleCertificateKind.Development) == (method == DistributionMethod.Development));
        checks.Add(new(ProfileCheckKind.ProfileType, typeOk, method is null ? $"Unknown profile type ({info.Kind})."
            : r.Method is null && method == DistributionMethod.AppStore ? "App Store profile: for upload to App Store Connect only, never installable on a device."
            : r.Method is not null && method != r.Method ? $"{method} profile; {r.Method} was requested."
            : identity is not null && (identity.Kind == AppleCertificateKind.Development) != (method == DistributionMethod.Development)
                ? $"{method} profile, but the certificate is {(identity.Kind == AppleCertificateKind.Development ? "Apple Development" : "Apple Distribution")}."
            : $"{method} profile{(method == DistributionMethod.Development ? " (Apple Development certificate, debugging allowed)" : "")}."));

        if (r.Device is { } d)
        {
            bool devOk;
            string detail;
            if (method == DistributionMethod.Enterprise || info.ProvisionsAllDevices) { devOk = true; detail = "Provisions all devices (In-House)."; }
            else if (method == DistributionMethod.AppStore) { devOk = false; detail = "App Store profiles have no device list."; }
            else if (d.Udid is null) { devOk = false; detail = $"{d.Name} did not report its UDID; registration cannot be checked."; }
            else
            {
                devOk = info.ProvisionedDevices.Contains(d.Udid, StringComparer.OrdinalIgnoreCase);
                detail = devOk ? $"{d.Name} ({AppleDeviceContract.Redact(d.Udid)}) is in the device list."
                    : $"{d.Name} ({AppleDeviceContract.Redact(d.Udid)}) is not among its {info.ProvisionedDevices.Count} device(s).";
            }
            checks.Add(new(ProfileCheckKind.Device, devOk, detail));
        }

        var missing = new List<string>();
        foreach (var (key, value) in r.Entitlements)
        {
            if (key is "application-identifier" or "com.apple.developer.team-identifier" or "get-task-allow" or "keychain-access-groups") continue;
            if (!p.RawEntitlements.TryGetValue(key, out var granted)) { missing.Add($"{key} (not enabled for the App ID)"); continue; }
            var req = AppleCertificates.RenderValue(EntitlementComposer.Rewrite(value, null, team ?? "", null, null));
            var gr = AppleCertificates.RenderValue(granted);
            var ok = value is bool b ? granted is bool gb && (gb || !b) : SigningValidator.ValueAllowed(req, gr);
            if (!ok) missing.Add($"{key} = {req} (profile allows {gr})");
        }
        checks.Add(new(ProfileCheckKind.Entitlements, missing.Count == 0, r.Entitlements.Count == 0
            ? "No special entitlements requested."
            : missing.Count == 0 ? $"All {r.Entitlements.Count} requested entitlement(s) are granted." : "Missing: " + string.Join("; ", missing) + "."));

        return new ProfileVerdict(p, checks, identity);
    }

    private static IReadOnlyList<string> NextSteps(List<ProfileVerdict> verdicts, string bundleId, SigningDoctorRequest r, List<string> teams)
    {
        var steps = new List<string>();
        var team = r.TeamId ?? (teams.Count == 1 ? teams[0] : null);
        bool AnyPassing(ProfileCheckKind k) => verdicts.Any(v => v.Checks.Any(c => c.Kind == k && c.Passed));
        if (teams.Count == 0)
        {
            steps.Add("Create an Apple Development certificate: Xcode › Settings › Accounts › your team › Manage Certificates › + › Apple Development.");
            return steps;
        }
        steps.Add($"Let HCG create the profile with your Xcode account (recommended): Signing Doctor › \"Create profile with Xcode\" — " +
                  $"HCG builds a throwaway stub app with bundle ID {bundleId}{(team is null ? "" : $" for team {team}")} using xcodebuild -allowProvisioningUpdates" +
                  (r.Device is null ? "" : " -allowProvisioningDeviceRegistration (registers the connected device)") +
                  ". Xcode registers the App ID, enables the entitlements' capabilities, and downloads a Development profile. Nothing is revoked or installed.");
        if (!AnyPassing(ProfileCheckKind.BundleIdentifier))
            steps.Add($"Or manually: developer.apple.com › Certificates, Identifiers & Profiles › Identifiers › + › App IDs › App › Explicit Bundle ID {bundleId}" +
                      (r.Entitlements.Count > 0 ? $", with the capabilities for: {string.Join(", ", r.Entitlements.Keys.Take(6))}" : "") + ".");
        if (r.Device is { Udid: { } udid } d && verdicts.All(v => v.Checks.Any(c => c.Kind == ProfileCheckKind.Device && !c.Passed)))
            steps.Add($"Register {d.Name} (UDID {udid}) at developer.apple.com › Devices › + (or connect it while Xcode creates the profile).");
        steps.Add($"Then: Profiles › + › iOS App Development › App ID {bundleId} › your Apple Development certificate › your devices › Generate › Download, and add it in Provisioning (Add profile…).");
        return steps;
    }

    private static string YourTeams(List<AppleCertificateInfo> all)
    {
        var teams = string.Join(", ", all.Select(c => c.TeamId).OfType<string>().Distinct());
        return teams.Length > 0 ? teams : "no certificates";
    }

    private static string Title(ProfileCheckKind k) => new ProfileCheck(k, false, "").Title;
}
