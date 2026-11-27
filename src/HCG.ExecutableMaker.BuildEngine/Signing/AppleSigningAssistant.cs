using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Signing;

/// <summary>One target's effective build settings, as <c>xcodebuild -showBuildSettings -json</c> reports them (xcconfig files, Config/Local.xcconfig included, already applied).</summary>
public sealed record XcodeTargetSettings(string Target, IReadOnlyDictionary<string, string> Settings)
{
    public string? Get(string key) => Settings.TryGetValue(key, out var v) && v.Trim().Length > 0 ? v.Trim() : null;
    public string? Team => Get("DEVELOPMENT_TEAM");
    public string? Style => Get("CODE_SIGN_STYLE");
    public string? BundleIdentifier => Get("PRODUCT_BUNDLE_IDENTIFIER");
    public string? EntitlementsFile => Get("CODE_SIGN_ENTITLEMENTS");
    public string? DeviceFamily => Get("TARGETED_DEVICE_FAMILY");
    public string? CompilationConditions => Get("SWIFT_ACTIVE_COMPILATION_CONDITIONS");
    public bool IsApplication => Get("PRODUCT_TYPE") == "com.apple.product-type.application" || Get("WRAPPER_EXTENSION") == "app";
}

/// <summary>Reads <c>xcodebuild -showBuildSettings -json</c>.</summary>
public static class XcodeBuildSettings
{
    public static IReadOnlyList<XcodeTargetSettings> Parse(string output)
    {
        var start = output.IndexOf('[');
        if (start < 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(output[start..], new JsonDocumentOptions { AllowTrailingCommas = true });
            var list = new List<XcodeTargetSettings>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("buildSettings", out var bs) || bs.ValueKind != JsonValueKind.Object) continue;
                var target = e.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
                var settings = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in bs.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) settings[p.Name] = p.Value.GetString()!;
                list.Add(new XcodeTargetSettings(target, settings));
            }
            return list;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The scheme's application target (the first one that builds an .app).</summary>
    public static XcodeTargetSettings? App(IReadOnlyList<XcodeTargetSettings> targets) => targets.FirstOrDefault(t => t.IsApplication) ?? targets.FirstOrDefault();

    /// <summary>
    /// A build-setting name the project's bundle identifier is made of, e.g. UNIMATCH_BUNDLE_ID for
    /// <c>PRODUCT_BUNDLE_IDENTIFIER = "$(UNIMATCH_BUNDLE_ID)"</c>. Overriding that variable keeps every target's
    /// derived identifier (".uitests" and the like) consistent; null when the project writes the identifier directly.
    /// </summary>
    public static string? BundleIdentifierVariable(string pbxprojText)
    {
        var names = BundleVarRegex().Matches(pbxprojText).Select(m => m.Groups[1].Value).Distinct().ToList();
        return names.Count == 1 ? names[0] : null;
    }

    private static readonly Regex BundleVarPattern = new(@"PRODUCT_BUNDLE_IDENTIFIER\s*=\s*""?\$[({]([A-Z][A-Z0-9_]*)[)}]""?\s*;", RegexOptions.Compiled);
    private static Regex BundleVarRegex() => BundleVarPattern;
}

/// <summary>Everything the assistant decides from: the build, the project's effective settings and this Mac's signing assets.</summary>
public sealed record AppleSigningInput
{
    public required MobileBuildOptions Options { get; init; }
    public required string Configuration { get; init; }
    public XcodeTargetSettings? App { get; init; }
    public ProjectSigningConfiguration? Saved { get; init; }
    public IReadOnlyList<AppleCertificateInfo> Certificates { get; init; } = [];
    public IReadOnlyList<AppleProvisioningProfileInfo> Profiles { get; init; } = [];
    /// <summary>Entitlement keys the app target's .entitlements file requests.</summary>
    public IReadOnlyCollection<string> Entitlements { get; init; } = [];
    /// <summary>The project's bundle-identifier variable (see <see cref="XcodeBuildSettings.BundleIdentifierVariable"/>).</summary>
    public string? BundleIdentifierVariable { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>What will be passed to xcodebuild and why, or exactly what must be resolved first.</summary>
public sealed record AppleSigningPlan
{
    public required AppleBuildDestination Destination { get; init; }
    public required string Configuration { get; init; }
    public required IReadOnlyList<SigningCheck> Checks { get; init; }
    /// <summary>Build-setting overrides and flags for xcodebuild. The project's files are never changed.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string? Team { get; init; }
    public string? TeamSource { get; init; }
    public string? BundleIdentifier { get; init; }
    /// <summary>Teams of usable identities on this Mac (offered in Resolve Signing when no team is set).</summary>
    public IReadOnlyList<string> SuggestedTeams { get; init; } = [];
    public bool Ready => !Checks.Any(c => c.Outcome == SigningCheckOutcome.Failed);
    public IEnumerable<SigningCheck> Failures => Checks.Where(c => c.Outcome == SigningCheckOutcome.Failed);
    public string Summary => Ready
        ? $"{Destination.Label()} · {Configuration}: signing ready" + (Team is null ? " (no team needed)" : $" (team {Team}, {TeamSource})")
        : $"{Destination.Label()} · {Configuration}: " + string.Join(" ", Failures.Select(f => f.Detail + (f.Remedy is null ? "" : " Fix: " + f.Remedy)));
}

/// <summary>
/// Apple Signing Assistant: decides, before compilation, how an iOS build is signed for its destination.
/// <list type="bullet">
/// <item>Simulator builds need no team or profile; when the project has no team, the build is signed to run locally
/// (ad hoc, entitlements kept) instead of Xcode's automatic signing, which would ask for a team.</item>
/// <item>Physical iPhone/iPad builds need Apple Development signing: a team, an identity with its private key on this
/// Mac, and a development profile for the bundle identifier (or Xcode-managed provisioning the person authorized).</item>
/// <item>Distribution archives, TestFlight and App Store are Release builds; a Debug distribution build is refused, so
/// development-only code a project keeps to Debug (Unimatch's Development Run) cannot ship.</item>
/// </list>
/// The team, style, profile and bundle identifier are passed as xcodebuild build settings for this build only: the
/// imported project, its xcconfig files and Config/Local.xcconfig are never modified.
/// </summary>
public static partial class AppleSigningAssistant
{
    public const string ResolveCode = "HCG-SIGNING-RESOLVE";

    public static AppleSigningPlan Plan(AppleSigningInput input)
    {
        var o = input.Options;
        var destination = AppleDestinations.Of(o);
        var config = input.Configuration;
        var isDebugConfig = config.Contains("debug", StringComparison.OrdinalIgnoreCase);
        var checks = new List<SigningCheck>();
        var args = new List<string>();
        var notes = new List<string>();
        var target = input.App?.Target;
        var effectiveTeam = input.App?.Team;

        // ---- configuration safeguards ----
        if (destination.IsDistribution())
        {
            if (isDebugConfig)
                checks.Add(new("Build configuration", SigningCheckCategory.Route, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    $"{destination.Label()} must be a Release build, but “{config}” was chosen. Debug builds can contain development-only code (for example a Development Run limited to Debug).",
                    "Choose Release for distribution. Use “Physical iPhone/iPad (Debug)” to test development functionality on a device."));
            else if (input.App?.CompilationConditions is { } cond && Regex.IsMatch(cond, @"(^|\s)DEBUG(\s|$)"))
                checks.Add(new("Build configuration", SigningCheckCategory.Route, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    $"The “{config}” configuration compiles with DEBUG (SWIFT_ACTIVE_COMPILATION_CONDITIONS = {cond}), so development-only code would be included.",
                    "Archive with the project's Release configuration."));
            else
                checks.Add(new("Build configuration", SigningCheckCategory.Route, SigningCheckScope.Local, SigningCheckOutcome.Passed,
                    $"{config}: development-only code the project limits to Debug is excluded."));
        }
        else if (isDebugConfig)
            notes.Add($"{config} build: development-only functionality the project enables in Debug (for example a Development Run) is included. Never distribute this build.");
        else
            notes.Add($"{config} build: development-only functionality the project limits to Debug is not included.");

        // ---- bundle identifier (this build only) ----
        var bundleId = input.App?.BundleIdentifier;
        if (o.BundleIdentifier is { Length: > 0 } wanted)
        {
            if (!BundleIdRegex().IsMatch(wanted))
                checks.Add(new("Bundle identifier", SigningCheckCategory.BundleIdentifier, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    $"“{wanted}” is not a valid bundle identifier.", "Use reverse-DNS letters, digits, dots and hyphens, e.g. com.example.app."));
            else
            {
                args.Add(input.BundleIdentifierVariable is { } v ? $"{v}={wanted}" : $"PRODUCT_BUNDLE_IDENTIFIER={wanted}");
                notes.Add(input.BundleIdentifierVariable is { } v2
                    ? $"Bundle identifier {wanted} for this build ({v2}={wanted}; targets derived from it follow). The project is not changed."
                    : $"Bundle identifier {wanted} for this build (PRODUCT_BUNDLE_IDENTIFIER). The project is not changed.");
                bundleId = wanted;
            }
        }

        // ---- simulator: no team, certificate or profile ----
        if (destination == AppleBuildDestination.IosSimulatorDebug)
        {
            if (effectiveTeam is null && o.DevelopmentTeam is null)
            {
                // "Sign to Run Locally": ad hoc, no team or profile, entitlements still embedded (the Simulator's keychain
                // needs them). Automatic signing without a team is what makes Xcode ask for a development team.
                args.AddRange(["CODE_SIGN_STYLE=Manual", "CODE_SIGN_IDENTITY=-", "DEVELOPMENT_TEAM=", "PROVISIONING_PROFILE_SPECIFIER="]);
                notes.Add("Simulator build: no development team or provisioning profile is needed. The project has no team, so this build is signed to run locally (ad hoc, entitlements kept); the project is not changed.");
            }
            else notes.Add("Simulator build: signed to run locally; no provisioning profile is needed.");
            checks.Add(new("Simulator signing", SigningCheckCategory.Route, SigningCheckScope.Local, SigningCheckOutcome.Passed, "Not needed for the iOS Simulator."));
            return new AppleSigningPlan { Destination = destination, Configuration = config, Checks = checks, Arguments = args, Notes = notes, BundleIdentifier = bundleId };
        }

        // ---- device and distribution: Apple signing ----
        if (o.Signing == MobileSigningMode.Unsigned)
        {
            if (destination is AppleBuildDestination.IPhoneDebug or AppleBuildDestination.IPadDebug)
                checks.Add(new("Signing mode", SigningCheckCategory.Route, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    "A physical iPhone or iPad only runs apps signed with an Apple Development certificate.",
                    "Choose Automatic (Xcode-managed) or Manual signing in Resolve Signing."));
            else
                notes.Add("Unsigned archive: for inspection only; it cannot be installed or exported.");
            return new AppleSigningPlan { Destination = destination, Configuration = config, Checks = checks, Arguments = args, Notes = notes, BundleIdentifier = bundleId };
        }

        var (team, teamSource) = o.DevelopmentTeam is { Length: > 0 } t1 ? (t1, "chosen for this build")
            : input.Saved?.TeamId is { Length: > 0 } t2 ? (t2, "the project's signing profile in Signing Vault")
            : effectiveTeam is { } t3 ? (t3, "the project's own settings (xcconfig / Config/Local.xcconfig)")
            : ((string?)null, (string?)null);
        var development = destination is AppleBuildDestination.IPhoneDebug or AppleBuildDestination.IPadDebug;
        var wantedKind = development ? AppleCertificateKind.Development : AppleCertificateKind.AppleDistribution;
        var usable = input.Certificates.Where(c => c.Kind == wantedKind && c.Readiness(input.Now) == SigningReadiness.Ready && c.TeamId is not null).ToList();
        var suggested = usable.Select(c => c.TeamId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        if (team is null)
        {
            checks.Add(new("Development team", SigningCheckCategory.Team, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"Signing for “{target ?? "the app"}” requires a development team, and none is set: the project's effective DEVELOPMENT_TEAM is empty and no team is chosen in Signing Vault.",
                suggested.Count > 0
                    ? $"Choose a team in Resolve Signing ({string.Join(", ", suggested)} {(suggested.Count == 1 ? "has" : "have")} a usable {Kind(wantedKind)} identity on this Mac). HCG passes it as DEVELOPMENT_TEAM for this build; the project and Config/Local.xcconfig are not changed."
                    : $"Add your Apple Account in Xcode › Settings › Accounts (or import a {Kind(wantedKind)} .p12 in Signing & Security), then choose its team in Resolve Signing."));
            return new AppleSigningPlan { Destination = destination, Configuration = config, Checks = checks, Arguments = args, Notes = notes, BundleIdentifier = bundleId, SuggestedTeams = suggested };
        }
        if (!TeamRegex().IsMatch(team))
        {
            checks.Add(new("Development team", SigningCheckCategory.Team, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"“{team}” ({teamSource}) is not an Apple Team ID (10 capital letters and digits).", "Copy the Team ID from developer.apple.com › Membership details, or Xcode › Settings › Accounts."));
            return new AppleSigningPlan { Destination = destination, Configuration = config, Checks = checks, Arguments = args, Notes = notes, BundleIdentifier = bundleId, SuggestedTeams = suggested, Team = team, TeamSource = teamSource };
        }
        checks.Add(new("Development team", SigningCheckCategory.Team, SigningCheckScope.Local, SigningCheckOutcome.Passed, $"{team} ({teamSource})."));
        if (team != effectiveTeam) args.Add("DEVELOPMENT_TEAM=" + team);

        // Identity: a certificate of the right kind for the team, with its private key on this Mac.
        var identities = usable.Where(c => c.TeamId == team).ToList();
        var authorized = o.Signing == MobileSigningMode.AppleAutomatic && o.AllowProvisioningUpdates;
        if (identities.Count > 0)
            checks.Add(new($"{Kind(wantedKind)} identity", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Passed,
                $"{identities[0].CommonName} (SHA-1 {identities[0].Sha1[..8]}…, private key on this Mac)."));
        else
        {
            var present = input.Certificates.Where(c => c.Kind == wantedKind && c.TeamId == team).ToList();
            var detail = present.Count == 0
                ? $"No {Kind(wantedKind)} certificate for team {team} is in this Mac's keychain."
                : $"The {Kind(wantedKind)} certificate for team {team} cannot sign: {string.Join("; ", present.Select(c => $"{c.CommonName}: {c.Readiness(input.Now)}"))}.";
            checks.Add(new($"{Kind(wantedKind)} identity", present.Any(c => c.HasPrivateKey == false) ? SigningCheckCategory.PrivateKey : SigningCheckCategory.Certificate,
                SigningCheckScope.Local, authorized && development ? SigningCheckOutcome.NotVerified : SigningCheckOutcome.Failed,
                authorized && development ? detail + " Xcode can create a development certificate with your Apple Account (Xcode-managed provisioning is authorized)." : detail,
                $"Xcode › Settings › Accounts › your team › Manage Certificates › + {(development ? "Apple Development" : "Apple Distribution")}; or import the .p12 (with its private key) in Signing & Security."));
        }

        // Provisioning profile.
        var profileKinds = destination switch
        {
            AppleBuildDestination.IPhoneDebug or AppleBuildDestination.IPadDebug => new[] { AppleProfileKind.Development },
            AppleBuildDestination.TestFlight or AppleBuildDestination.AppStore => [AppleProfileKind.AppStore],
            _ => o.ExportMethod switch
            {
                IosExportMethod.ReleaseTesting => [AppleProfileKind.AdHoc],
                IosExportMethod.Enterprise => [AppleProfileKind.Enterprise],
                IosExportMethod.Debugging => [AppleProfileKind.Development],
                _ => [AppleProfileKind.AppStore, AppleProfileKind.AdHoc],
            },
        };
        var candidates = input.Profiles.Where(p => p.Signature == ProfileSignatureStatus.VerifiedApple && (p.Expiration is null || p.Expiration > input.Now)
                                                   && p.TeamIds.Contains(team) && profileKinds.Contains(p.Kind) && Matches(p, team, bundleId)).ToList();
        AppleProvisioningProfileInfo? profile = null;
        if (o.Signing == MobileSigningMode.AppleManual)
        {
            profile = o.ProvisioningProfile is { } named ? input.Profiles.FirstOrDefault(p => string.Equals(p.Uuid, named, StringComparison.OrdinalIgnoreCase) || p.Name == named) : null;
            if (profile is null)
                checks.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    o.ProvisioningProfile is null ? "Manual signing needs a provisioning profile." : $"“{o.ProvisioningProfile}” is not installed on this Mac.",
                    candidates.Count > 0 ? $"Use {string.Join(" or ", candidates.Select(c => $"“{c.Name}”"))}, or switch to Automatic signing." : "Import the profile in Signing & Security, or download it in Xcode › Settings › Accounts › Download Manual Profiles."));
            else if (!candidates.Contains(profile))
                checks.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    $"“{profile.Name}” does not fit this build: {Mismatch(profile, team, bundleId, profileKinds, input.Now)}.",
                    "Choose a profile for this team, bundle identifier and destination, or switch to Automatic signing."));
            else
            {
                checks.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.Local, SigningCheckOutcome.Passed, $"“{profile.Name}” ({profile.Kind}, {profile.ApplicationIdentifier})."));
                if (!profile.CertificateSha1s.Any(s => identities.Any(i => string.Equals(i.Sha1, s, StringComparison.OrdinalIgnoreCase))))
                    checks.Add(new("Profile certificate", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                        $"None of the certificates “{profile.Name}” allows is in this Mac's keychain with its private key.",
                        "Import the .p12 of a certificate the profile includes, or regenerate the profile with your certificate."));
            }
            args.AddRange(["CODE_SIGN_STYLE=Manual", "PROVISIONING_PROFILE_SPECIFIER=" + (profile?.Name ?? o.ProvisioningProfile ?? ""), "CODE_SIGN_IDENTITY=" + (development ? "Apple Development" : "Apple Distribution")]);
        }
        else
        {
            profile = candidates.OrderByDescending(p => p.Expiration).FirstOrDefault();
            if (profile is not null)
                checks.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.Local, SigningCheckOutcome.Passed,
                    $"Xcode can use the installed “{profile.Name}” ({profile.Kind}, {profile.ApplicationIdentifier})."));
            else if (authorized)
                checks.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.RequiresApple, SigningCheckOutcome.NotVerified,
                    $"No installed {string.Join("/", profileKinds)} profile matches {bundleId ?? "the app"} for team {team}. Xcode will create or download one with the Apple Account signed in to Xcode (authorized for this build)."));
            else
                checks.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    $"No installed {string.Join("/", profileKinds)} provisioning profile matches {bundleId ?? "the app"} for team {team}, and Xcode-managed provisioning is not authorized for this build.",
                    "In Resolve Signing, allow Xcode-managed provisioning (uses the Apple Account in Xcode › Settings › Accounts; Xcode creates the profile and registers connected devices), or import a profile in Signing & Security."));
            if (o.Signing == MobileSigningMode.AppleAutomatic || input.App?.Style == "Automatic") args.Add("CODE_SIGN_STYLE=Automatic");
            if (authorized)
            {
                args.Add("-allowProvisioningUpdates");
                if (development) args.Add("-allowProvisioningDeviceRegistration");
                notes.Add("Xcode-managed provisioning is authorized: Xcode may contact Apple with the account signed in to Xcode to create or renew profiles" + (development ? " and register connected devices." : "."));
            }
        }

        // Entitlements the app requests must be in the profile.
        if (profile is not null && input.Entitlements.Count > 0)
        {
            var missing = input.Entitlements.Where(k => !IsIgnorableEntitlement(k) && !profile.Entitlements.ContainsKey(k)).ToList();
            checks.Add(missing.Count == 0
                ? new("Entitlements", SigningCheckCategory.Entitlements, SigningCheckScope.Local, SigningCheckOutcome.Passed, $"“{profile.Name}” allows the {input.Entitlements.Count} entitlement(s) the app requests.")
                : new("Entitlements", SigningCheckCategory.Entitlements, SigningCheckScope.Local, authorized ? SigningCheckOutcome.NotVerified : SigningCheckOutcome.Failed,
                    $"“{profile.Name}” does not allow {string.Join(", ", missing)}, which {input.App?.EntitlementsFile ?? "the app's entitlements"} requests." + (authorized ? " Xcode-managed provisioning can add the capability." : ""),
                    "Enable the capability for the App ID on developer.apple.com (or let Xcode manage provisioning), then refresh the profile."));
        }

        // Physical devices: registration in the development profile, and the app's device family.
        if (development)
        {
            if (profile is { ProvisionsAllDevices: false } && (input.Saved?.IntendedDevices ?? []) is { Count: > 0 } intended)
            {
                var notRegistered = intended.Where(d => !profile.ProvisionedDevices.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
                checks.Add(notRegistered.Count == 0
                    ? new("Device registration", SigningCheckCategory.Devices, SigningCheckScope.Local, SigningCheckOutcome.Passed, $"{intended.Count} device(s) are registered in “{profile.Name}”.")
                    : new("Device registration", SigningCheckCategory.Devices, SigningCheckScope.Local, authorized ? SigningCheckOutcome.NotVerified : SigningCheckOutcome.Warning,
                        $"{string.Join(", ", notRegistered)} {(notRegistered.Count == 1 ? "is" : "are")} not in “{profile.Name}”" + (authorized ? "; Xcode registers connected devices (authorized)." : "."),
                        "Connect the device and allow Xcode-managed provisioning, or register it on developer.apple.com and refresh the profile."));
            }
            else
                checks.Add(new("Device registration", SigningCheckCategory.Devices, SigningCheckScope.RequiresApple, SigningCheckOutcome.NotVerified,
                    profile is null ? "Checked once a development profile exists." : $"“{profile.Name}” lists {profile.ProvisionedDevices.Count} device(s). Add your device's UDID in Signing Vault to check it here."));
            if (destination == AppleBuildDestination.IPadDebug && input.App?.DeviceFamily is { } fam && !fam.Split(',').Select(x => x.Trim()).Contains("2"))
                checks.Add(new("Device family", SigningCheckCategory.Platform, SigningCheckScope.Local, SigningCheckOutcome.Warning,
                    $"The app targets iPhone only (TARGETED_DEVICE_FAMILY = {fam}); on an iPad it runs in iPhone compatibility mode."));
        }

        return new AppleSigningPlan
        {
            Destination = destination, Configuration = config, Checks = checks, Arguments = args, Notes = notes,
            Team = team, TeamSource = teamSource, BundleIdentifier = bundleId, SuggestedTeams = suggested,
        };
    }

    private static string Kind(AppleCertificateKind k) => k == AppleCertificateKind.Development ? "Apple Development" : "Apple Distribution";

    /// <summary>The profile's App ID (TEAMID.bundle, TEAMID.prefix.* or TEAMID.*) covers the bundle identifier.</summary>
    public static bool Matches(AppleProvisioningProfileInfo p, string team, string? bundleId)
    {
        if (p.ApplicationIdentifier is not { } appId || !appId.StartsWith(team + ".", StringComparison.Ordinal)) return false;
        var pattern = appId[(team.Length + 1)..];
        if (bundleId is null) return pattern == "*";
        if (pattern == "*") return true;
        return pattern.EndsWith(".*", StringComparison.Ordinal) ? bundleId.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == bundleId;
    }

    private static string Mismatch(AppleProvisioningProfileInfo p, string team, string? bundleId, AppleProfileKind[] kinds, DateTimeOffset now)
    {
        var why = new List<string>();
        if (!p.TeamIds.Contains(team)) why.Add($"it belongs to team {string.Join(", ", p.TeamIds)}, not {team}");
        if (!kinds.Contains(p.Kind)) why.Add($"it is a {p.Kind} profile, this destination needs {string.Join(" or ", kinds)}");
        if (!Matches(p, team, bundleId)) why.Add($"its App ID {p.ApplicationIdentifier} does not cover {bundleId}");
        if (p.Expiration is { } e && e <= now) why.Add($"it expired on {e:yyyy-MM-dd}");
        if (p.Signature != ProfileSignatureStatus.VerifiedApple) why.Add("its Apple signature could not be verified");
        return why.Count == 0 ? "unknown reason" : string.Join("; ", why);
    }

    private static bool IsIgnorableEntitlement(string key) =>
        key is "application-identifier" or "com.apple.developer.team-identifier" or "get-task-allow" or "keychain-access-groups";

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex TeamRegex();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,154}$")]
    private static partial Regex BundleIdRegex();
}

/// <summary>Recognizes xcodebuild's signing failures, so a failed build offers Resolve Signing instead of a raw log line.</summary>
public static class AppleSigningFailures
{
    public sealed record Failure(SigningCheckCategory Category, string Explanation, string Remedy);

    private static readonly (Regex Pattern, SigningCheckCategory Category, string Explanation, string Remedy)[] Rules =
    [
        (new Regex(@"requires a development team", RegexOptions.IgnoreCase), SigningCheckCategory.Team,
            "The project has no development team for this build.",
            "Resolve Signing: choose your Apple Developer team (HCG passes it as DEVELOPMENT_TEAM for this build), then retry."),
        (new Regex(@"No (Accounts|account for team)", RegexOptions.IgnoreCase), SigningCheckCategory.Team,
            "Xcode has no Apple Account for this team.",
            "Add the account in Xcode › Settings › Accounts, then retry; or use manual signing with an installed profile."),
        (new Regex(@"No (signing certificate|certificate for team)|doesn't include signing certificate|No .*(Development|Distribution) signing certificate", RegexOptions.IgnoreCase), SigningCheckCategory.Certificate,
            "No usable signing certificate (with its private key) for the team is on this Mac, or the profile does not include it.",
            "Resolve Signing: import the .p12 in Signing & Security or create the certificate in Xcode › Settings › Accounts › Manage Certificates."),
        (new Regex(@"No profiles? for '[^']+' (were|was) found|requires a provisioning profile|doesn't match the entitlements|Provisioning profile .* doesn't (support|include)", RegexOptions.IgnoreCase), SigningCheckCategory.Profile,
            "No provisioning profile fits the bundle identifier, team and capabilities.",
            "Resolve Signing: allow Xcode-managed provisioning for this build, or import a matching profile."),
        (new Regex(@"(device|Device) .* (isn't|is not) (registered|included)|Your team has no devices", RegexOptions.IgnoreCase), SigningCheckCategory.Devices,
            "The device is not registered in the development profile.",
            "Resolve Signing: connect the device and allow Xcode-managed provisioning (registers it), or register its UDID on developer.apple.com."),
    ];

    public static IReadOnlyList<Failure> Classify(IEnumerable<string> lines)
    {
        var found = new List<Failure>();
        foreach (var line in lines)
            foreach (var (pattern, category, explanation, remedy) in Rules)
                if (pattern.IsMatch(line) && found.All(f => f.Category != category))
                    found.Add(new Failure(category, explanation + $" (Xcode: “{line.Trim()}”)", remedy));
        return found;
    }
}
