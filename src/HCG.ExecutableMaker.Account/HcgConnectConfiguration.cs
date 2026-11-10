using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCG.ExecutableMaker.Account;

/// <summary>
/// How Executable Maker reaches HCG: the identity server (OAuth 2.1 authorization code + PKCE, public client, no
/// secret), the HCG website API (ERP App Access, support requests) and the HCG AI gateway.
/// <para>
/// Built-in defaults contain only values that were verified on the live HCG systems or in the identity server's
/// source (see docs/hcg-connect/HANDOFF.md). The OAuth client ID is empty until HCG issues Executable Maker's public
/// client; until then the account features report "Not configured" and contact nothing. A local file
/// (<c>hcg-connect.json</c> in the app data folder, schema docs/hcg-connect/hcg-connect.schema.json) can supply the
/// issued client ID; it may never contain a secret, and unknown keys are refused so a typo cannot go unnoticed.
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
    /// <summary>
    /// Public OAuth client HCG issued for Executable Maker (a UUID; no secret exists for it). Empty or not a UUID (e.g. the
    /// handoff's placeholder) = not registered: sign-in stays off and nothing is contacted.
    /// </summary>
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
    /// <summary>
    /// Identity scopes only (they grant no Executable Maker permission). HCG's central guidance (9 Nov 2026) is to request
    /// the minimal identity scopes; the provider issues refresh tokens on code exchange without <c>offline_access</c>.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "email", "profile"];
    /// <summary>HCG website (ERP, App Access, support). Executable Maker's routes live under <see cref="ApiPath"/>.</summary>
    public Uri ApiBaseUrl { get; init; } = new("https://beta.hakmicapitalgroup.com");
    public string ApiPath { get; init; } = "/api/public/executable-maker/v1";
    /// <summary>
    /// App Access directory entry used by Executable Maker's own support routes (website package; PROPOSED until those
    /// routes are deployed and the product is registered in ERP ▸ App Access).
    /// </summary>
    public string AppIdentifier { get; init; } = "executable-maker";
    /// <summary>
    /// Central product key of Executable Maker in HCG's product registry, used with the Ecosystem API
    /// (<c>product=</c> / <c>product_id</c>). <c>hcg.executable.maker</c> is the key HCG's live product catalog uses.
    /// </summary>
    public string ProductId { get; init; } = "hcg.executable.maker";
    /// <summary>
    /// Central HCG Ecosystem API (account, organisation memberships, features, access checks). Live on HCG; answers 401
    /// without a token.
    /// </summary>
    public string EcosystemApiPath { get; init; } = "/api/public/ecosystem/v1";
    /// <summary>HCG's account page for the signed-in user (<c>/client-portal</c>), opened in the system browser.</summary>
    public Uri AccountPortalUrl { get; init; } = new("https://beta.hakmicapitalgroup.com/client-portal");
    /// <summary>
    /// Hosts whose HTTPS links (account, plan management, checkout) the app opens when HCG provides them. Any other link
    /// from a response is not offered.
    /// </summary>
    public IReadOnlyList<string> TrustedLinkHosts { get; init; } = ["beta.hakmicapitalgroup.com", "hakmicapitalgroup.com"];
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
        else if (!Guid.TryParseExact(ClientId, "D", out var id) || id == Guid.Empty)
            missing.Add($"The client ID \"{ClientId}\" is not an issued HCG client ID (HCG issues UUIDs); replace the placeholder with the issued public client ID");
        if (RedirectUris.Count == 0) missing.Add("Redirect URI");
        if (!Scopes.Contains("openid")) missing.Add("The scopes must include openid (the ID token proves who signed in)");
        foreach (var uri in RedirectUris)
            if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Fragment))
                missing.Add($"Redirect URI {uri} must be http://127.0.0.1:<port>/<path> (loopback, explicit port, no fragment)");
        Uri[] identity = [Issuer, AuthorizationEndpoint, TokenEndpoint, UserinfoEndpoint, JwksEndpoint];
        if (identity.Any(u => u.Scheme != "https" && !IsLoopbackHttp(u)))
            missing.Add("Identity endpoints must use HTTPS");
        return missing;
    }

    /// <summary>
    /// True when the identity endpoints point at a server on this computer (http://127.0.0.1 or localhost): a local test
    /// identity server, never HCG. The Account page says so.
    /// </summary>
    public bool UsesLocalIdentityServer => IsLoopbackHttp(Issuer);

    private static bool IsLoopbackHttp(Uri u) => u.Scheme == "http" && (u.Host == "127.0.0.1" || u.Host == "localhost");

    /// <summary>The URL when it is an HTTPS link on one of <see cref="TrustedLinkHosts"/>; otherwise null.</summary>
    public Uri? TrustedLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo)
        && TrustedLinkHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) ? u : null;

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
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadResult(Default, $"{FileName} could not be read ({ex.Message}). The file was ignored.");
        }
        return Parse(text);
    }

    /// <summary>Largest configuration file accepted (the real one is about 1 KB).</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Validates configuration text with the same rules as <see cref="Load"/> (nothing is written).</summary>
    public static LoadResult Parse(string text)
    {
        if (text.Length > MaxFileBytes) return new LoadResult(Default, $"{FileName} is larger than {MaxFileBytes / 1024} KB; it is not an HCG Connect configuration. The file was ignored.");
        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var key in Keys(doc.RootElement))
                if (ForbiddenKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    return new LoadResult(Default, $"{FileName} contains \"{key}\". It must not contain secrets: Executable Maker is a public client and needs none. The file was ignored.");
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new LoadResult(Default, $"{FileName} must be a JSON object. The file was ignored.");
            foreach (var p in doc.RootElement.EnumerateObject())
                if (!KnownKeys.Contains(p.Name))
                    return new LoadResult(Default, $"{FileName} has an unknown setting \"{p.Name}\" (check its spelling against docs/hcg-connect/hcg-connect.schema.json). The file was ignored.");
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

    /// <summary>The settings <c>hcg-connect.json</c> may contain (camelCase, exactly as in the schema).</summary>
    public static IReadOnlySet<string> KnownKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "$schema", "contractVersion", "issuer", "authorizationEndpoint", "tokenEndpoint", "userinfoEndpoint", "jwksEndpoint",
        "clientId", "redirectUris", "scopes", "apiBaseUrl", "apiPath", "appIdentifier", "productId", "ecosystemApiPath",
        "accountPortalUrl", "trustedLinkHosts", "aiGatewayBaseUrl", "aiCapabilitiesPath",
    };

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

    public const string ClientName = "HCG Executable Maker";

    /// <summary>
    /// The client metadata HCG registers for this desktop app (RFC 7591 field names), produced from this configuration:
    /// a public native client (no secret: token_endpoint_auth_method "none"), authorization code + refresh token,
    /// PKCE S256, loopback redirect URIs and the scopes the app requests. For HCG's registration; the app never calls a
    /// registration endpoint itself.
    /// </summary>
    public string RegistrationRequestJson() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["client_name"] = ClientName,
        ["application_type"] = "native",
        ["client_type"] = "public",
        ["token_endpoint_auth_method"] = "none",
        ["grant_types"] = new[] { "authorization_code", "refresh_token" },
        ["response_types"] = new[] { "code" },
        ["redirect_uris"] = RedirectUris.Select(u => u.ToString()).ToArray(),
        ["scope"] = string.Join(' ', Scopes),
        ["code_challenge_method"] = "S256",
    }, Json);
}
