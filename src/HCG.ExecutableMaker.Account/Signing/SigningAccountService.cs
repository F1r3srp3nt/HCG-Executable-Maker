using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Account.Signing;

public enum SigningAccountState
{
    /// <summary>Nobody is signed in with HCG: account backup is off; local signing works.</summary>
    NotSignedIn,
    /// <summary>HCG's signing-metadata routes answered: metadata can be recorded.</summary>
    Available,
    /// <summary>HCG does not provide the signing-metadata routes yet (the website package is not deployed).</summary>
    NotDeployed,
    /// <summary>This account may not use Executable Maker's account features (ERP App Access).</summary>
    Forbidden,
    /// <summary>HCG could not be reached. Nothing was changed.</summary>
    Unavailable,
}

/// <summary>Non-secret project preferences as sent to the account. Fields are an allow-list; nothing else is ever sent.</summary>
public sealed record AccountProjectSigningPreferences
{
    public string? Platform { get; init; }
    public string? Mode { get; init; }
    public string? Route { get; init; }
    public string? TeamId { get; init; }
    public string? IdentitySha1 { get; init; }
    public string? InstallerIdentitySha1 { get; init; }
    public string? ProfileUuid { get; init; }
    public IReadOnlyList<AccountTargetPreference> Targets { get; init; } = [];

    public static AccountProjectSigningPreferences From(ProjectSigningConfiguration c) => new()
    {
        Platform = c.Platform.ToString(), Mode = c.Mode.ToString(), Route = c.Route.ToString(), TeamId = c.TeamId,
        IdentitySha1 = c.IdentitySha1, InstallerIdentitySha1 = c.InstallerIdentitySha1, ProfileUuid = c.ProfileUuid,
        Targets = c.Targets.Select(t => new AccountTargetPreference(t.TargetName, t.BundleIdentifier, t.ProfileUuid)).ToList(),
    };
}

public sealed record AccountTargetPreference(string TargetName, string? BundleIdentifier, string? ProfileUuid);

/// <summary>
/// The encrypted private-key vault is a separate design (docs/SIGNING-VAULT.md). No backend for it exists, so it is
/// reported as unavailable — HCG never pretends to upload or back up a private key.
/// </summary>
public static class EncryptedSigningVault
{
    public const bool Available = false;

    public const string UnavailableReason =
        "Encrypted backup of private keys is not available yet. It needs HCG's encrypted vault service (client-side encryption, device approval and a " +
        "separate recovery key), which does not exist yet. Private keys stay only in this Mac's keychain; keep your own .p12 backup in a safe place.";
}

/// <summary>
/// Optional account integration: records NON-SECRET signing metadata (certificate fingerprints, team, type, expiry;
/// profile UUID, name, App ID, platforms, expiry) and project preferences in the user's HCG account, so another Mac can
/// show what exists and where. Used only after the user explicitly chooses "Back up to my HCG account".
/// The server authorises every request from the caller's own token (ERP App Access) and isolates each user's records.
/// </summary>
public sealed partial class SigningAccountService(HcgApiClient api)
{
    public const string AssetsPath = "signing/assets";
    public const string ProjectsPath = "signing/projects";

    public SigningAccountState State { get; private set; } = SigningAccountState.NotSignedIn;
    public string StatusText { get; private set; } = "Not signed in with HCG. Signing assets stay on this Mac.";
    public IReadOnlyList<AccountSigningAssetRecord> Records { get; private set; } = [];

    public sealed record AssetList(IReadOnlyList<AccountSigningAssetRecord> Assets);
    public sealed record AssetResponse(AccountSigningAssetRecord Asset);
    public sealed record RemovedResponse(bool Removed);
    public sealed record PreferencesResponse(AccountProjectSigningPreferences? Preferences);
    private sealed record PreferencesBody(AccountProjectSigningPreferences Preferences);

    /// <summary>Reads the account's records. Never throws: the state says what happened.</summary>
    public async Task<SigningAccountState> RefreshAsync(CancellationToken ct)
    {
        if (!api.Account.IsSignedIn) return Set(SigningAccountState.NotSignedIn, "Not signed in with HCG. Signing assets stay on this Mac.", []);
        try
        {
            var list = await api.GetAsync<AssetList>(AssetsPath, ct).ConfigureAwait(false);
            return Set(SigningAccountState.Available, $"HCG account metadata available ({list.Assets.Count} record(s)). Private keys are never uploaded.", list.Assets);
        }
        catch (HcgApiException ex) { return Fail(ex); }
    }

    /// <summary>Records a certificate's public metadata. The certificate itself, its private key and any password are not sent.</summary>
    public Task<AccountSigningAssetRecord> RecordCertificateAsync(AppleCertificateInfo c, string deviceId, string deviceLabel, CancellationToken ct) =>
        RecordAsync(new AccountSigningAssetRecord
        {
            Id = "", AssetType = "certificate", Sha1 = c.Sha1, Sha256 = c.Sha256, DisplayName = c.CommonName, TeamId = c.TeamId,
            CertificateKind = c.Kind.ToString(), ExpiresAt = c.NotAfter, DeviceId = deviceId, DeviceLabel = deviceLabel,
        }, ct);

    /// <summary>Records a profile's metadata (UUID, name, team, App ID pattern, type, platforms, expiry). Device lists and the file are not sent.</summary>
    public Task<AccountSigningAssetRecord> RecordProfileAsync(AppleProvisioningProfileInfo p, string deviceId, string deviceLabel, CancellationToken ct) =>
        RecordAsync(new AccountSigningAssetRecord
        {
            Id = "", AssetType = "profile", ProfileUuid = p.Uuid, DisplayName = p.Name, TeamId = p.TeamId, ProfileKind = p.Kind.ToString(),
            BundleIdPattern = p.BundleIdPattern, Platforms = p.Platforms, ExpiresAt = p.Expiration, DeviceId = deviceId, DeviceLabel = deviceLabel,
        }, ct);

    private async Task<AccountSigningAssetRecord> RecordAsync(AccountSigningAssetRecord record, CancellationToken ct)
    {
        Validate(record);
        var body = record with { Id = null! };
        var r = await api.PostAsync<AssetResponse>(AssetsPath, body, ct).ConfigureAwait(false);
        return r.Asset;
    }

    /// <summary>Removes HCG's record only. The keychain, other Macs and Apple are unaffected.</summary>
    public async Task ForgetAsync(string recordId, CancellationToken ct)
    {
        if (!Guid.TryParse(recordId, out _)) throw new HcgApiException(HcgApiFailure.Invalid, "Not an account record.");
        await api.SendAsync<RemovedResponse>(HttpMethod.Delete, $"{AssetsPath}/{recordId}", null, ct).ConfigureAwait(false);
    }

    public async Task<AccountProjectSigningPreferences?> GetProjectAsync(string projectKey, CancellationToken ct) =>
        (await api.GetAsync<PreferencesResponse>($"{ProjectsPath}/{Key(projectKey)}", ct).ConfigureAwait(false)).Preferences;

    public async Task<AccountProjectSigningPreferences?> PutProjectAsync(string projectKey, ProjectSigningConfiguration configuration, CancellationToken ct) =>
        (await api.SendAsync<PreferencesResponse>(HttpMethod.Put, $"{ProjectsPath}/{Key(projectKey)}",
            new PreferencesBody(AccountProjectSigningPreferences.From(configuration)), ct).ConfigureAwait(false)).Preferences;

    /// <summary>A project key that is the same on every Mac: the main bundle identifier when known.</summary>
    public static string Key(string projectKey) =>
        KeyRegex().IsMatch(projectKey) ? projectKey : throw new HcgApiException(HcgApiFailure.Invalid, "The project key must be a bundle identifier or project ID.");

    /// <summary>Client-side guard (the server checks the same): fingerprints, UUIDs and team IDs only.</summary>
    public static void Validate(AccountSigningAssetRecord r)
    {
        static bool Hex(string? s, int n) => s is null || (s.Length == n && s.All(Uri.IsHexDigit));
        var ok = r.AssetType switch
        {
            "certificate" => r.Sha1 is not null && Hex(r.Sha1, 40) && Hex(r.Sha256, 64) && r.ProfileUuid is null,
            "profile" => r.ProfileUuid is not null && Guid.TryParse(r.ProfileUuid, out _) && r.Sha1 is null && r.Sha256 is null,
            _ => false,
        };
        if (!ok || (r.TeamId is not null && !TeamRegex().IsMatch(r.TeamId)) || (r.DisplayName?.Length ?? 0) > 200)
            throw new HcgApiException(HcgApiFailure.Invalid, "Only certificate fingerprints, profile UUIDs and their public details can be recorded.");
    }

    private SigningAccountState Fail(HcgApiException ex) => ex.Failure switch
    {
        HcgApiFailure.NotDeployed or HcgApiFailure.NotFound => Set(SigningAccountState.NotDeployed,
            "HCG account backup is unavailable: HCG's website does not provide the signing-metadata service yet. Assets are imported to this Mac only.", []),
        HcgApiFailure.Forbidden or HcgApiFailure.Revoked => Set(SigningAccountState.Forbidden, ex.Message, []),
        HcgApiFailure.NotSignedIn or HcgApiFailure.Unauthorized => Set(SigningAccountState.NotSignedIn, ex.Message, []),
        _ => Set(SigningAccountState.Unavailable, ex.Message, Records),
    };

    private SigningAccountState Set(SigningAccountState state, string text, IReadOnlyList<AccountSigningAssetRecord> records)
    {
        State = state;
        StatusText = text;
        Records = records;
        return state;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex TeamRegex();
}
