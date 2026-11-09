using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCG.ExecutableMaker.Account;

/// <summary>
/// How Executable Maker reaches HCG: the identity server (OAuth 2.1 authorization code + PKCE, public client, no
/// secret), the HCG website API (ERP App Access, support requests) and the HCG AI gateway.
/// <para>
/// Built-in defaults contain only values that were verified on the live HCG systems or in the identity server's
/// source (see docs/HCG-ACCOUNT.md). The OAuth client ID is empty until Executable Maker's client is registered;
/// until then the account features report "Not configured" and contact nothing. A local file
/// (<c>hcg-connect.json</c> in the app data folder) can supply the registered client ID; it may never contain a secret.
/// </para>
/// </summary>
public sealed record HcgConnectConfiguration
{
    public const string ContractVersion = "hcg.executable-maker.connect/1";
    public const string FileName = "hcg-connect.json";

    [JsonPropertyName("contractVersion")] public string Contract { get; init; } = ContractVersion;
    public Uri Issuer { get; init; } = new("https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1");
    public Uri AuthorizationEndpoint { get; init; } = new("https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/authorize");
    public Uri TokenEndpoint { get; init; } = new("https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/token");
    public Uri UserinfoEndpoint { get; init; } = new("https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/userinfo");
    public Uri JwksEndpoint { get; init; } = new("https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/.well-known/jwks.json");
    /// <summary>Public OAuth client registered for Executable Maker (no secret exists for it). Empty = not registered.</summary>
    public string? ClientId { get; init; }
    /// <summary>
    /// Loopback redirect URIs, each registered exactly (the server matches redirect URIs as exact strings, so the port is
    /// fixed). The first one whose port is free is used.
    /// </summary>
    public IReadOnlyList<Uri> RedirectUris { get; init; } =
    [
        new("http://127.0.0.1:47820/oauth/callback"),
        new("http://127.0.0.1:47821/oauth/callback"),
        new("http://127.0.0.1:47822/oauth/callback"),
    ];
    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "email", "profile", "offline_access"];
    /// <summary>HCG website (ERP, App Access, support). Executable Maker's routes live under <see cref="ApiPath"/>.</summary>
    public Uri ApiBaseUrl { get; init; } = new("https://beta.hakmicapitalgroup.com");
    public string ApiPath { get; init; } = "/api/public/executable-maker/v1";
    /// <summary>ERP App Access product id for Executable Maker (PROPOSED until registered in ERP ▸ App Access).</summary>
    public string AppIdentifier { get; init; } = "executable-maker";
    /// <summary>
    /// HCG AI gateway. Null until its contract (capability discovery, request schema, auth) is published and verified:
    /// AI assistance then shows "Not connected".
    /// </summary>
    public Uri? AiGatewayBaseUrl { get; init; }
    public string? AiCapabilitiesPath { get; init; }

    public static HcgConnectConfiguration Default { get; } = new();

    /// <summary>What is missing before sign-in can start (empty = ready).</summary>
    public IReadOnlyList<string> MissingForSignIn()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ClientId)) missing.Add("Executable Maker's OAuth client is not registered with HCG yet (client ID)");
        if (RedirectUris.Count == 0) missing.Add("Redirect URI");
        foreach (var uri in RedirectUris)
            if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Fragment))
                missing.Add($"Redirect URI {uri} must be http://127.0.0.1:<port>/<path> (loopback, explicit port, no fragment)");
        if (Issuer.Scheme != "https" || TokenEndpoint.Scheme != "https" || AuthorizationEndpoint.Scheme != "https")
            missing.Add("Identity endpoints must use HTTPS");
        return missing;
    }

    public bool AiGatewayConfigured => AiGatewayBaseUrl is not null && !string.IsNullOrWhiteSpace(AiCapabilitiesPath);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Forbidden key fragments: a desktop app is a public client and must never hold a secret.</summary>
    private static readonly string[] ForbiddenKeyFragments = ["secret", "password", "service_role", "servicerole", "private", "apikey", "api_key"];

    public sealed record LoadResult(HcgConnectConfiguration Configuration, string? Problem);

    /// <summary>
    /// Loads <c>hcg-connect.json</c> from the app data folder over the built-in defaults. A file with a secret-like key,
    /// another contract version or invalid JSON is refused (the defaults are used and the problem is reported).
    /// </summary>
    public static LoadResult Load(string appDataRoot)
    {
        var path = Path.Combine(appDataRoot, FileName);
        if (!File.Exists(path)) return new LoadResult(Default, null);
        try
        {
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            foreach (var key in Keys(doc.RootElement))
                if (ForbiddenKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    return new LoadResult(Default, $"{FileName} contains \"{key}\". It must not contain secrets: Executable Maker is a public client and needs none. The file was ignored.");
            var loaded = JsonSerializer.Deserialize<HcgConnectConfiguration>(text, Json) ?? Default;
            if (loaded.Contract != ContractVersion)
                return new LoadResult(Default, $"{FileName} is for contract \"{loaded.Contract}\"; this version needs \"{ContractVersion}\". The file was ignored.");
            return new LoadResult(loaded, null);
        }
        catch (JsonException ex)
        {
            return new LoadResult(Default, $"{FileName} is not valid JSON ({ex.Message}). The file was ignored.");
        }
    }

    private static IEnumerable<string> Keys(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                yield return p.Name;
                foreach (var k in Keys(p.Value)) yield return k;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray())
                foreach (var k in Keys(i)) yield return k;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
