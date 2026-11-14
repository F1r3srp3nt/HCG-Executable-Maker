using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Signing;

public enum ProfileRequirement
{
    Required,
    /// <summary>macOS outside the App Store: only targets that use restricted (profile-backed) entitlements need one.</summary>
    OnlyForRestrictedEntitlements,
}

/// <summary>What one signing route needs. Signing is separate from notarization, uploading and publishing.</summary>
public sealed record SigningRouteRequirements(
    SigningRoute Route,
    SigningPlatform Platform,
    string Title,
    IReadOnlyList<AppleCertificateKind> IdentityKinds,
    IReadOnlyList<AppleProfileKind> ProfileKinds,
    ProfileRequirement Profile,
    IReadOnlyList<AppleCertificateKind> InstallerKinds,
    bool InstallerRequired,
    bool DevicesMatter,
    string AfterSigning)
{
    public string IdentityDescription => string.Join(" or ", IdentityKinds.Select(SigningRouteCatalog.Display));
}

public static class SigningRouteCatalog
{
    public static readonly IReadOnlyList<SigningRouteRequirements> All =
    [
        new(SigningRoute.LocalDevelopment, SigningPlatform.Ios, "iOS development (your registered devices)",
            [AppleCertificateKind.Development], [AppleProfileKind.Development], ProfileRequirement.Required, [], false, true,
            "Install on a registered device from Xcode or Finder. Nothing is uploaded."),
        new(SigningRoute.IosAdHoc, SigningPlatform.Ios, "iOS Ad Hoc / release testing (registered devices)",
            [AppleCertificateKind.AppleDistribution], [AppleProfileKind.AdHoc], ProfileRequirement.Required, [], false, true,
            "Export the IPA (release-testing) and install it on the devices listed in the profile."),
        new(SigningRoute.IosAppStore, SigningPlatform.Ios, "iOS App Store / TestFlight",
            [AppleCertificateKind.AppleDistribution], [AppleProfileKind.AppStore], ProfileRequirement.Required, [], false, false,
            "Export the IPA (app-store-connect). Uploading to App Store Connect is a separate step that HCG does not perform automatically."),
        new(SigningRoute.IosEnterprise, SigningPlatform.Ios, "iOS in-house (Apple Developer Enterprise Program)",
            [AppleCertificateKind.AppleDistribution], [AppleProfileKind.Enterprise], ProfileRequirement.Required, [], false, false,
            "Export the IPA (enterprise) and distribute it inside your organisation."),
        new(SigningRoute.LocalDevelopment, SigningPlatform.MacOs, "macOS development (this Mac)",
            [AppleCertificateKind.Development], [AppleProfileKind.Development], ProfileRequirement.OnlyForRestrictedEntitlements, [], false, true,
            "Runs on this Mac (and registered Macs when a development profile is embedded)."),
        new(SigningRoute.MacDeveloperId, SigningPlatform.MacOs, "macOS Developer ID (direct distribution)",
            [AppleCertificateKind.DeveloperIdApplication], [AppleProfileKind.DeveloperId], ProfileRequirement.OnlyForRestrictedEntitlements,
            [AppleCertificateKind.DeveloperIdInstaller], false, false,
            "Notarize separately (notarytool) before distributing; staple the ticket. Publishing is a separate step."),
        new(SigningRoute.MacAppStore, SigningPlatform.MacOs, "Mac App Store",
            [AppleCertificateKind.AppleDistribution, AppleCertificateKind.MacAppDistribution], [AppleProfileKind.AppStore], ProfileRequirement.Required,
            [AppleCertificateKind.MacInstallerDistribution], true, false,
            "Build the signed .pkg; uploading to App Store Connect is a separate step that HCG does not perform automatically."),
    ];

    public static SigningRouteRequirements For(SigningPlatform platform, SigningRoute route) =>
        All.FirstOrDefault(r => r.Platform == platform && r.Route == route)
        ?? throw new ArgumentException($"{route} is not a {platform} signing route.");

    public static IEnumerable<SigningRouteRequirements> ForPlatform(SigningPlatform platform) => All.Where(r => r.Platform == platform);

    public static string Display(AppleCertificateKind k) => k switch
    {
        AppleCertificateKind.DeveloperIdApplication => "Developer ID Application",
        AppleCertificateKind.DeveloperIdInstaller => "Developer ID Installer",
        AppleCertificateKind.AppleDistribution => "Apple Distribution",
        AppleCertificateKind.MacAppDistribution => "3rd Party Mac Developer Application",
        AppleCertificateKind.MacInstallerDistribution => "Mac Installer Distribution",
        AppleCertificateKind.Development => "Apple Development",
        _ => "other certificate",
    };

    public static string ProfilePlatform(SigningPlatform p) => p == SigningPlatform.MacOs ? ApplePlatformNames.MacOs : ApplePlatformNames.Ios;

    /// <summary>
    /// Entitlements that must be authorised by a provisioning profile. On macOS, App Sandbox and hardened-runtime keys
    /// (com.apple.security.*) need no profile — except application groups, which macOS 15+ ties to the profile.
    /// </summary>
    public static bool IsRestricted(string key, SigningPlatform platform) => platform switch
    {
        SigningPlatform.MacOs => key.StartsWith("com.apple.developer.", StringComparison.Ordinal)
                                 || key is "keychain-access-groups" or "aps-environment" or "com.apple.application-identifier" or "com.apple.security.application-groups",
        _ => key is not ("get-task-allow" or "application-identifier" or "com.apple.developer.team-identifier"),
    };
}

/// <summary>A target as the project describes it (from the analyser, or entered by the user).</summary>
public sealed record SigningTargetFacts(string Name, string? BundleIdentifier, bool IsEmbedded, IReadOnlyDictionary<string, string> Entitlements);

/// <summary>Signing tools found on this host (checked separately from signing assets).</summary>
public sealed record SigningToolFacts(bool IsMac, bool Codesign, bool SecurityTool, bool Xcodebuild, string? XcodeVersion, bool Productbuild);

public sealed record SigningValidationInput
{
    public required ProjectSigningConfiguration Configuration { get; init; }
    public required SigningAssetInventory Inventory { get; init; }
    /// <summary>The main app first, then extensions and other embedded targets.</summary>
    public required IReadOnlyList<SigningTargetFacts> Targets { get; init; }
    public required SigningToolFacts Tools { get; init; }
    /// <summary>Whether the build produces a .pkg (installer identity needed).</summary>
    public bool ProducesInstallerPackage { get; init; }
    public DateTimeOffset Now { get; init; }
    public TimeSpan ExpiryWarning { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Pre-build signing validation. Checks only what can be checked on this Mac from verified data, labels everything
/// that needs Apple's services as not verified, and gives a specific remedy for each failure.
/// </summary>
public static class SigningValidator
{
    private const string CertificatesUrl = "https://developer.apple.com/account/resources/certificates/list";
    private const string ProfilesUrl = "https://developer.apple.com/account/resources/profiles/list";

    public static SigningValidationReport Validate(SigningValidationInput input)
    {
        var cfg = input.Configuration;
        var checks = new List<SigningCheck>();
        SigningRouteRequirements route;
        try { route = SigningRouteCatalog.For(cfg.Platform, cfg.Route); }
        catch (ArgumentException ex)
        {
            checks.Add(new("Signing route", SigningCheckCategory.Route, SigningCheckScope.Local, SigningCheckOutcome.Failed, ex.Message,
                "Choose a route for this platform."));
            return new SigningValidationReport { Configuration = cfg, Checks = checks, CheckedAt = input.Now };
        }
        checks.Add(new("Signing route", SigningCheckCategory.Route, SigningCheckScope.Local, SigningCheckOutcome.Passed,
            $"{route.Title}. After signing: {route.AfterSigning}"));

        Tools(input, route, checks);
        Team(cfg, checks);
        if (input.Targets.Count == 0)
            checks.Add(new("Targets", SigningCheckCategory.BundleIdentifier, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                "No app target is known, so bundle identifiers and entitlements cannot be checked.", "Inspect the project again, or add the targets to the signing configuration."));

        if (cfg.Mode == ProjectSigningMode.XcodeAutomatic) Automatic(input, route, checks);
        else Manual(input, route, checks);

        Embedded(input, checks);
        checks.Add(new("Apple-side status", SigningCheckCategory.Certificate, SigningCheckScope.RequiresApple, SigningCheckOutcome.NotVerified,
            "Certificate revocation, profile invalidation and the App ID's enabled capabilities are kept by Apple. HCG did not query Apple, so these are not verified; " +
            "codesign, Xcode or App Store Connect report them when they are used."));
        return new SigningValidationReport { Configuration = cfg, Checks = checks, CheckedAt = input.Now };
    }

    private static void Tools(SigningValidationInput input, SigningRouteRequirements route, List<SigningCheck> checks)
    {
        var t = input.Tools;
        if (!t.IsMac)
        {
            checks.Add(new("Signing tools", SigningCheckCategory.Tools, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                "Apple code signing runs on macOS (codesign and the keychain); this computer is not a Mac.", "Build and sign on a Mac."));
            return;
        }
        var missing = new List<string>();
        if (!t.Codesign) missing.Add("codesign");
        if (!t.SecurityTool) missing.Add("security");
        if (route.Platform == SigningPlatform.Ios && !t.Xcodebuild) missing.Add("xcodebuild (Xcode)");
        if (input.ProducesInstallerPackage && !t.Productbuild) missing.Add("productbuild");
        checks.Add(missing.Count == 0
            ? new("Signing tools", SigningCheckCategory.Tools, SigningCheckScope.Local, SigningCheckOutcome.Passed,
                "codesign and security are available" + (t.XcodeVersion is { } v ? $"; Xcode {v}" : "") + ".")
            : new("Signing tools", SigningCheckCategory.Tools, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                "Missing: " + string.Join(", ", missing) + ".", "Install Xcode (or the Command Line Tools) and select it in Toolchains › Xcode."));
    }

    private static void Team(ProjectSigningConfiguration cfg, List<SigningCheck> checks)
    {
        if (cfg.TeamId is not { } team || team.Length != 10 || !team.All(char.IsAsciiLetterOrDigit) || team.Any(char.IsLower))
            checks.Add(new("Team", SigningCheckCategory.Team, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                cfg.TeamId is null ? "No Apple Developer Team ID is selected." : $"\"{cfg.TeamId}\" is not a Team ID (10 upper-case letters or digits).",
                "Choose the team (Xcode › Settings › Accounts shows it; developer.apple.com › Membership details)."));
        else
            checks.Add(new("Team", SigningCheckCategory.Team, SigningCheckScope.Local, SigningCheckOutcome.Passed, $"Team {team}."));
    }

    private static void Automatic(SigningValidationInput input, SigningRouteRequirements route, List<SigningCheck> checks)
    {
        var cfg = input.Configuration;
        var local = input.Inventory.Certificates.Where(c => route.IdentityKinds.Contains(c.Kind) && (cfg.TeamId is null || c.TeamId == cfg.TeamId)).ToList();
        var ready = local.Where(c => c.Readiness(input.Now) == SigningReadiness.Ready).ToList();
        checks.Add(ready.Count > 0
            ? new("Signing certificate", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Passed,
                $"{ready.Count} {route.IdentityDescription} identit{(ready.Count == 1 ? "y" : "ies")} with a private key on this Mac for team {cfg.TeamId}; Xcode chooses one.")
            : new("Signing certificate", SigningCheckCategory.Certificate, SigningCheckScope.RequiresApple, SigningCheckOutcome.NotVerified,
                $"No usable {route.IdentityDescription} identity for team {cfg.TeamId} is on this Mac. With automatic signing, Xcode can create one using the Apple Account signed in to Xcode (Apple's services; not verified by HCG)."
                + (local.Count > 0 ? $" Present but not usable: {string.Join("; ", local.Select(c => $"{c.CommonName} ({c.Readiness(input.Now)})"))}." : ""),
                "Sign in to Xcode › Settings › Accounts with the team's Apple Account, or import a .p12 here."));
        checks.Add(new("Provisioning profiles", SigningCheckCategory.Profile, SigningCheckScope.RequiresApple, SigningCheckOutcome.NotVerified,
            "Xcode manages the profiles with automatic signing (and may create or renew them with Apple). HCG does not choose or check them in this mode."));
        foreach (var t in input.Targets)
            checks.Add(t.BundleIdentifier is null
                ? new("Bundle identifier", SigningCheckCategory.BundleIdentifier, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    "The target has no bundle identifier.", "Set PRODUCT_BUNDLE_IDENTIFIER for the target in Xcode.", t.Name)
                : new("Bundle identifier", SigningCheckCategory.BundleIdentifier, SigningCheckScope.Local, SigningCheckOutcome.Passed, t.BundleIdentifier, Target: t.Name));
    }

    private static void Manual(SigningValidationInput input, SigningRouteRequirements route, List<SigningCheck> checks)
    {
        var cfg = input.Configuration;
        var identity = Identity(input, route, checks, cfg.IdentitySha1, route.IdentityKinds, "Signing certificate", required: true);
        if (route.InstallerKinds.Count > 0 && (route.InstallerRequired && input.ProducesInstallerPackage || cfg.InstallerIdentitySha1 is not null))
            Identity(input, route, checks, cfg.InstallerIdentitySha1, route.InstallerKinds, "Installer certificate", required: route.InstallerRequired && input.ProducesInstallerPackage);

        foreach (var target in input.Targets)
        {
            var targetCfg = cfg.Targets.FirstOrDefault(t => t.TargetName == target.Name);
            var uuid = targetCfg?.ProfileUuid ?? (target.IsEmbedded ? null : cfg.ProfileUuid);
            var restricted = target.Entitlements.Keys.Where(k => SigningRouteCatalog.IsRestricted(k, route.Platform)).ToList();
            var needsProfile = route.Profile == ProfileRequirement.Required || restricted.Count > 0;
            if (target.BundleIdentifier is null)
                checks.Add(new("Bundle identifier", SigningCheckCategory.BundleIdentifier, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    "The target has no bundle identifier.", "Set PRODUCT_BUNDLE_IDENTIFIER for the target in Xcode.", target.Name));
            if (!needsProfile)
            {
                checks.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.Local, SigningCheckOutcome.NotApplicable,
                    route.Route == SigningRoute.MacDeveloperId
                        ? "Not needed: a Developer ID app without restricted entitlements is signed with the certificate alone."
                        : "Not needed: this macOS target uses no restricted entitlements.", Target: target.Name));
                if (uuid is not null) Profile(input, route, identity, target, uuid, restricted, checks);
                continue;
            }
            if (uuid is null)
            {
                checks.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    route.Profile == ProfileRequirement.Required
                        ? $"No provisioning profile is selected; {route.Title} needs a profile of type {string.Join(" or ", route.ProfileKinds)} for {target.BundleIdentifier ?? target.Name}."
                        : $"No provisioning profile is selected, but the target uses restricted entitlements ({string.Join(", ", restricted)}), which need one.",
                    "Import or download a matching profile (Xcode › Settings › Accounts › Download Manual Profiles, or developer.apple.com › Profiles) and select it.", target.Name));
                continue;
            }
            Profile(input, route, identity, target, uuid, restricted, checks);
        }
    }

    private static AppleCertificateInfo? Identity(SigningValidationInput input, SigningRouteRequirements route, List<SigningCheck> checks,
        string? sha1, IReadOnlyList<AppleCertificateKind> kinds, string title, bool required)
    {
        var cfg = input.Configuration;
        var wanted = string.Join(" or ", kinds.Select(SigningRouteCatalog.Display));
        if (sha1 is null)
        {
            checks.Add(new(title, SigningCheckCategory.Certificate, SigningCheckScope.Local, required ? SigningCheckOutcome.Failed : SigningCheckOutcome.NotApplicable,
                $"No {wanted} certificate is selected.", required ? $"Select an identity of type {wanted}, or import its .p12." : null));
            return null;
        }
        var c = input.Inventory.Certificate(sha1);
        if (c is null)
        {
            checks.Add(new(title, SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"The selected certificate (SHA-1 {sha1[..Math.Min(8, sha1.Length)]}…) is not in this Mac's keychain.", "Import its .p12 here, or select another identity.", null));
            return null;
        }
        if (!c.Location.HasFlag(SigningAssetLocation.ThisMac))
        {
            checks.Add(new(title, SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"{c.CommonName} is known only from your HCG account{(c.RecordedOnDevice is { } d ? $" (recorded on {d})" : "")}. An account record never makes an identity usable: the certificate and its private key must be in this Mac's keychain.",
                "Export it as a .p12 on the Mac that has it and import it here."));
            return null;
        }
        checks.Add(kinds.Contains(c.Kind)
            ? new(title, SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Passed, $"{c.CommonName} ({SigningRouteCatalog.Display(c.Kind)}).")
            : new(title, SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"{c.CommonName} is of type {SigningRouteCatalog.Display(c.Kind)}; {route.Title} needs {wanted}.",
                $"Select an identity of type {wanted} (create it in Xcode › Settings › Accounts › Manage Certificates if you have none)."));
        checks.Add(c.HasPrivateKey == true
            ? new($"{title} private key", SigningCheckCategory.PrivateKey, SigningCheckScope.Local, SigningCheckOutcome.Passed, "The matching private key is in this Mac's keychain.")
            : new($"{title} private key", SigningCheckCategory.PrivateKey, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"{c.CommonName} is in the keychain without its private key, so it cannot sign.",
                "On the Mac that created the certificate, export certificate + private key as a .p12 and import it here."));
        var left = c.NotAfter - input.Now;
        checks.Add(c.NotAfter <= input.Now
            ? new($"{title} validity", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"Expired on {c.NotAfter:yyyy-MM-dd}.", "Create a new certificate (Xcode › Settings › Accounts › Manage Certificates) and update the profiles that use it.")
            : c.NotBefore > input.Now
                ? new($"{title} validity", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed, $"Not valid until {c.NotBefore:yyyy-MM-dd}.", "Check this Mac's date and time.")
                : left < input.ExpiryWarning
                    ? new($"{title} validity", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Warning,
                        $"Expires on {c.NotAfter:yyyy-MM-dd} ({(int)left.TotalDays} days).", "Renew it soon; profiles that include it must be regenerated.")
                    : new($"{title} validity", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Passed, $"Valid until {c.NotAfter:yyyy-MM-dd}."));
        if (c.Revocation == RevocationStatus.ReportedRevoked)
            checks.Add(new($"{title} revocation", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"macOS reports {c.CommonName} as revoked ({c.TrustProblem}).", "Create a new certificate; a revoked certificate cannot be used again.", null));
        else if (c.TrustProblem is { } problem)
            checks.Add(new($"{title} trust", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"macOS does not trust this identity: {problem}.",
                problem.Contains("NOT_TRUSTED", StringComparison.Ordinal)
                    ? "Install Apple's intermediate certificate (Apple Worldwide Developer Relations / Developer ID) from apple.com/certificateauthority."
                    : "Open Keychain Access and inspect the certificate."));
        else
            checks.Add(new($"{title} revocation", SigningCheckCategory.Certificate, SigningCheckScope.RequiresApple, SigningCheckOutcome.NotVerified,
                "Revocation status was not checked (it is kept by Apple). HCG does not report this certificate as \"not revoked\"."));
        if (cfg.TeamId is { } team && c.TeamId is { } certTeam && certTeam != team)
            checks.Add(new($"{title} team", SigningCheckCategory.Team, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"{c.CommonName} belongs to team {certTeam}; the project is set to team {team}.", "Select an identity of the project's team, or change the team."));
        return c;
    }

    private static void Profile(SigningValidationInput input, SigningRouteRequirements route, AppleCertificateInfo? identity, SigningTargetFacts target,
        string uuid, IReadOnlyList<string> restricted, List<SigningCheck> checks)
    {
        var cfg = input.Configuration;
        void Add(string title, SigningCheckCategory cat, SigningCheckOutcome o, string detail, string? remedy = null) =>
            checks.Add(new(title, cat, SigningCheckScope.Local, o, detail, remedy, target.Name));

        var p = input.Inventory.Profile(uuid);
        if (p is null || !p.Location.HasFlag(SigningAssetLocation.ThisMac) || p.Path is null)
        {
            Add("Provisioning profile", SigningCheckCategory.Profile, SigningCheckOutcome.Failed,
                p is null ? $"The selected profile (UUID {uuid}) is not installed on this Mac."
                          : $"{p.Name} is known only from your HCG account{(p.RecordedOnDevice is { } d ? $" (recorded on {d})" : "")}; it is not installed on this Mac.",
                "Download it in Xcode › Settings › Accounts › Download Manual Profiles (or developer.apple.com › Profiles) and import it here.");
            return;
        }
        if (p.Signature != ProfileSignatureStatus.VerifiedApple)
        {
            Add("Provisioning profile signature", SigningCheckCategory.Profile, SigningCheckOutcome.Failed,
                $"{p.Name}: the profile's signature could not be verified as Apple's.", "Download the profile again from Apple.");
            return;
        }
        Add("Provisioning profile", SigningCheckCategory.Profile, SigningCheckOutcome.Passed, $"{p.Name} — {p.DistributionDescription} (UUID {p.Uuid}, signature verified).");

        var left = p.Expiration is { } e ? e - input.Now : TimeSpan.MaxValue;
        if (p.IsExpired(input.Now))
            Add("Profile validity", SigningCheckCategory.Profile, SigningCheckOutcome.Failed, $"{p.Name} expired on {p.Expiration:yyyy-MM-dd}.",
                "Regenerate the profile at developer.apple.com › Profiles (or let Xcode renew it) and import it.");
        else if (left < input.ExpiryWarning)
            Add("Profile validity", SigningCheckCategory.Profile, SigningCheckOutcome.Warning, $"Expires on {p.Expiration:yyyy-MM-dd} ({(int)left.TotalDays} days).", "Regenerate it soon.");
        else
            Add("Profile validity", SigningCheckCategory.Profile, SigningCheckOutcome.Passed, $"Valid until {p.Expiration:yyyy-MM-dd}.");

        var platform = SigningRouteCatalog.ProfilePlatform(route.Platform);
        if (!p.Platforms.Contains(platform))
            Add("Profile platform", SigningCheckCategory.Platform, SigningCheckOutcome.Failed,
                $"{p.Name} is for {string.Join(", ", p.Platforms.Select(ApplePlatformNames.Display))}; this build targets {ApplePlatformNames.Display(platform)}.",
                $"Use a profile for {ApplePlatformNames.Display(platform)}.");
        else
            Add("Profile platform", SigningCheckCategory.Platform, SigningCheckOutcome.Passed, ApplePlatformNames.Display(platform) + ".");

        if (!route.ProfileKinds.Contains(p.Kind))
            Add("Profile type", SigningCheckCategory.Profile, SigningCheckOutcome.Failed,
                $"{p.Name} is of type {p.Kind}; {route.Title} needs {string.Join(" or ", route.ProfileKinds)}.",
                "Select or create a profile of the right distribution type at developer.apple.com › Profiles.");
        else
            Add("Profile type", SigningCheckCategory.Profile, SigningCheckOutcome.Passed, p.DistributionDescription + ".");

        if (cfg.TeamId is { } team && !p.TeamIds.Contains(team))
            Add("Profile team", SigningCheckCategory.Team, SigningCheckOutcome.Failed, $"{p.Name} belongs to team {string.Join(", ", p.TeamIds)}; the project uses team {team}.",
                "Use a profile from the project's team.");

        if (identity is not null)
        {
            if (p.CertificateSha1s.Contains(identity.Sha1, StringComparer.OrdinalIgnoreCase))
                Add("Certificate in profile", SigningCheckCategory.Profile, SigningCheckOutcome.Passed, $"{p.Name} includes {identity.CommonName}.");
            else
                Add("Certificate in profile", SigningCheckCategory.Profile, SigningCheckOutcome.Failed,
                    $"{p.Name} does not include the selected certificate {identity.CommonName} (SHA-1 {identity.ShortSha1}…); it allows {p.CertificateSha1s.Count} other certificate(s).",
                    "Edit the profile at developer.apple.com › Profiles to include this certificate, download it and import it here — or select a certificate the profile includes.");
        }

        if (target.BundleIdentifier is { } bundle)
        {
            if (!p.Matches(bundle))
                Add("Bundle identifier", SigningCheckCategory.BundleIdentifier, SigningCheckOutcome.Failed,
                    $"{bundle} is not covered by the profile's App ID {p.ApplicationIdentifier}.",
                    "Use a profile whose App ID matches this bundle identifier (an explicit App ID, or a wildcard such as TEAMID.com.example.*).");
            else if (p.IsWildcard && restricted.Any(k => k is not ("keychain-access-groups" or "com.apple.developer.team-identifier")))
                Add("Bundle identifier", SigningCheckCategory.BundleIdentifier, SigningCheckOutcome.Failed,
                    $"{bundle} matches the wildcard App ID {p.ApplicationIdentifier}, but wildcard App IDs cannot carry the capabilities this target uses ({string.Join(", ", restricted)}).",
                    "Create an explicit App ID for this bundle identifier with these capabilities, and a profile for it.");
            else
                Add("Bundle identifier", SigningCheckCategory.BundleIdentifier, SigningCheckOutcome.Passed,
                    p.IsWildcard ? $"{bundle} matches the wildcard App ID {p.ApplicationIdentifier}." : $"{bundle} matches the profile's App ID.");
        }

        Entitlements(p, target, route, Add);
        Devices(input, route, p, Add);
    }

    private static void Entitlements(AppleProvisioningProfileInfo p, SigningTargetFacts target, SigningRouteRequirements route,
        Action<string, SigningCheckCategory, SigningCheckOutcome, string, string?> add)
    {
        var problems = new List<string>();
        foreach (var (key, value) in target.Entitlements)
        {
            if (key is "application-identifier" or "com.apple.application-identifier" or "com.apple.developer.team-identifier") continue;
            if (key is "get-task-allow" or "com.apple.security.get-task-allow")
            {
                if (value == "true" && !p.GetTaskAllow && route.Route != SigningRoute.LocalDevelopment)
                    problems.Add($"{key}=true (debugging) is not allowed by a profile of type {p.Kind}");
                continue;
            }
            if (!SigningRouteCatalog.IsRestricted(key, route.Platform)) continue;
            if (!p.Entitlements.TryGetValue(key, out var granted)) { problems.Add($"{key} is not in the profile"); continue; }
            if (!ValueAllowed(value, granted)) problems.Add($"{key} = {value} is not allowed (the profile allows {granted})");
        }
        if (problems.Count > 0)
            add("Entitlements", SigningCheckCategory.Entitlements, SigningCheckOutcome.Failed, string.Join("; ", problems) + ".",
                "Enable the capability for the App ID at developer.apple.com › Identifiers, regenerate and import the profile — or remove the entitlement from the target.");
        else
            add("Entitlements", SigningCheckCategory.Entitlements, SigningCheckOutcome.Passed,
                target.Entitlements.Count == 0 ? "The target requests no entitlements." : $"All {target.Entitlements.Count} requested entitlement(s) are allowed by the profile.", null);
    }

    /// <summary>Every requested item must be granted exactly, by "*", or by a granted prefix ending in "*" (e.g. "ABCDE12345.*").</summary>
    public static bool ValueAllowed(string requested, string granted)
    {
        var grantedItems = granted.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in requested.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!grantedItems.Any(g => g == item || g == "*" || (g.EndsWith('*') && item.StartsWith(g[..^1], StringComparison.Ordinal))))
                return false;
        return true;
    }

    private static void Devices(SigningValidationInput input, SigningRouteRequirements route, AppleProvisioningProfileInfo p,
        Action<string, SigningCheckCategory, SigningCheckOutcome, string, string?> add)
    {
        if (!route.DevicesMatter || p.Kind is not (AppleProfileKind.Development or AppleProfileKind.AdHoc))
        {
            add("Devices", SigningCheckCategory.Devices, SigningCheckOutcome.NotApplicable,
                p.ProvisionsAllDevices ? "The profile provisions all devices." : "This distribution type has no device list.", null);
            return;
        }
        var intended = input.Configuration.IntendedDevices;
        if (intended.Count == 0)
        {
            add("Devices", SigningCheckCategory.Devices, SigningCheckOutcome.NotVerified,
                $"No device was chosen for installation; the profile lists {p.ProvisionedDevices.Count} device(s). Only those devices can run this build.", null);
            return;
        }
        var missing = intended.Where(d => !p.ProvisionedDevices.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        add("Devices", SigningCheckCategory.Devices, missing.Count == 0 ? SigningCheckOutcome.Passed : SigningCheckOutcome.Failed,
            missing.Count == 0 ? $"All {intended.Count} chosen device(s) are in the profile." : $"Not in the profile: {string.Join(", ", missing)}.",
            missing.Count == 0 ? null : "Register the device at developer.apple.com › Devices, regenerate the profile and import it.");
    }

    /// <summary>Embedded targets (extensions, watch apps, helpers) must use identifiers under the main app's.</summary>
    private static void Embedded(SigningValidationInput input, List<SigningCheck> checks)
    {
        var main = input.Targets.FirstOrDefault(t => !t.IsEmbedded)?.BundleIdentifier;
        if (main is null) return;
        foreach (var t in input.Targets.Where(t => t.IsEmbedded && t.BundleIdentifier is not null))
            if (!t.BundleIdentifier!.StartsWith(main + ".", StringComparison.Ordinal))
                checks.Add(new("Embedded target identifier", SigningCheckCategory.BundleIdentifier, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    $"{t.BundleIdentifier} is embedded in {main} but does not start with \"{main}.\"; App Store and device installation reject this.",
                    $"Rename the target's bundle identifier to {main}.<name>.", t.Name));
    }
}
