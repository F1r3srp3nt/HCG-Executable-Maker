using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace HCG.ExecutableMaker.Account;

public enum ConnectionCheckState { Passed, Warning, Failed, Skipped }

public sealed record ConnectionCheckStep(string Name, ConnectionCheckState State, string Detail);

public sealed record ConnectionCheckResult(IReadOnlyList<ConnectionCheckStep> Steps)
{
    /// <summary>
    /// Configuration, identity server and client registration all passed: sign-in can be attempted. This does not prove
    /// sign-in works; only a completed sign-in does.
    /// </summary>
    public bool ReadyToSignIn => Steps.Take(4).All(s => s.State == ConnectionCheckState.Passed);
}

/// <summary>
/// Checks an HCG Connect configuration against the real servers without signing in and without sending any credential:
/// the configuration itself, the identity server's discovery document (issuer, endpoints, PKCE S256, public clients),
/// its signing keys, whether the identity server knows the client ID and redirect URI (an authorization request that is
/// not followed, so no browser opens and no session starts), and whether HCG's central APIs are deployed. Every failure
/// names what failed: DNS, TLS, timeout, HTTP status, or the identity server's own error.
/// </summary>
/// <param name="http">An HttpClient that does NOT follow redirects (the authorization request must not be followed).</param>
public sealed class HcgConnectionCheck(HttpClient http)
{
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    public async Task<ConnectionCheckResult> RunAsync(HcgConnectConfiguration config, CancellationToken ct = default)
    {
        var steps = new List<ConnectionCheckStep>();
        var missing = config.MissingForSignIn();
        steps.Add(missing.Count > 0
            ? new("Configuration", ConnectionCheckState.Failed, string.Join("; ", missing) + ".")
            : new("Configuration", ConnectionCheckState.Passed,
                $"Public client {config.ClientId}, issuer {config.Issuer}, redirect URIs {string.Join(", ", config.RedirectUris)}, scopes {string.Join(" ", config.Scopes)}." +
                (config.UsesLocalIdentityServer ? " This is a local test identity server on this computer, not HCG." : "")));
        if (missing.Count > 0)
        {
            foreach (var name in new[] { "Identity server", "Signing keys", "Client registration", "HCG central API", "Executable Maker API" })
                steps.Add(new(name, ConnectionCheckState.Skipped, "Not checked: fix the configuration first."));
            return new ConnectionCheckResult(steps);
        }

        steps.Add(await DiscoveryAsync(config, ct).ConfigureAwait(false));
        steps.Add(await JwksAsync(config, ct).ConfigureAwait(false));
        steps.Add(steps[1].State == ConnectionCheckState.Failed
            ? new("Client registration", ConnectionCheckState.Skipped, "Not checked: the identity server could not be verified.")
            : await ClientAsync(config, ct).ConfigureAwait(false));
        steps.Add(await ApiAsync("HCG central API", new Uri(config.ApiBaseUrl, config.EcosystemApiPath.TrimEnd('/') + "/status"), config, ct).ConfigureAwait(false));
        steps.Add(await ApiAsync("Executable Maker API", new Uri(config.ApiBaseUrl, config.ApiPath.TrimEnd('/') + "/access"), config, ct).ConfigureAwait(false));
        return new ConnectionCheckResult(steps);
    }

    private async Task<ConnectionCheckStep> DiscoveryAsync(HcgConnectConfiguration c, CancellationToken ct)
    {
        const string name = "Identity server";
        var url = new Uri(c.Issuer.ToString().TrimEnd('/') + "/.well-known/openid-configuration");
        var (response, error) = await GetAsync(url, ct).ConfigureAwait(false);
        if (error is not null) return new(name, ConnectionCheckState.Failed, error);
        using (response)
        {
            var body = await response!.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(name, ConnectionCheckState.Failed, $"{url} answered HTTP {(int)response.StatusCode}: the issuer is wrong or the identity server has no OpenID discovery document.");
            JsonElement d;
            try { d = JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException) { return new(name, ConnectionCheckState.Failed, $"{url} did not return JSON: this is not an OpenID Connect identity server."); }
            var problems = new List<string>();
            void Same(string key, Uri expected)
            {
                var actual = d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (!string.Equals(actual?.TrimEnd('/'), expected.ToString().TrimEnd('/'), StringComparison.Ordinal))
                    problems.Add($"{key} is {actual ?? "(missing)"}, the configuration has {expected}");
            }
            Same("issuer", c.Issuer);
            Same("authorization_endpoint", c.AuthorizationEndpoint);
            Same("token_endpoint", c.TokenEndpoint);
            Same("userinfo_endpoint", c.UserinfoEndpoint);
            Same("jwks_uri", c.JwksEndpoint);
            if (!Has(d, "code_challenge_methods_supported", "S256")) problems.Add("the identity server does not list PKCE S256");
            if (!Has(d, "token_endpoint_auth_methods_supported", "none")) problems.Add("the identity server does not list public clients (token_endpoint_auth_method none)");
            foreach (var scope in c.Scopes)
                if (d.TryGetProperty("scopes_supported", out _) && !Has(d, "scopes_supported", scope)) problems.Add($"scope \"{scope}\" is not supported");
            return problems.Count == 0
                ? new(name, ConnectionCheckState.Passed, $"{c.Issuer.Host} publishes matching endpoints, PKCE S256 and public clients.")
                : new(name, ConnectionCheckState.Failed, "The configuration does not match the identity server: " + string.Join("; ", problems) + ".");
        }
    }

    private async Task<ConnectionCheckStep> JwksAsync(HcgConnectConfiguration c, CancellationToken ct)
    {
        const string name = "Signing keys";
        var (response, error) = await GetAsync(c.JwksEndpoint, ct).ConfigureAwait(false);
        if (error is not null) return new(name, ConnectionCheckState.Failed, error);
        using (response)
        {
            if (!response!.IsSuccessStatusCode) return new(name, ConnectionCheckState.Failed, $"{c.JwksEndpoint} answered HTTP {(int)response.StatusCode}.");
            try
            {
                var d = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;
                var algs = d.GetProperty("keys").EnumerateArray().Select(k => k.TryGetProperty("alg", out var a) ? a.GetString() : k.TryGetProperty("kty", out var t) ? t.GetString() : "?").ToList();
                return algs.Count == 0
                    ? new(name, ConnectionCheckState.Failed, "The identity server publishes no signing keys, so ID tokens cannot be verified.")
                    : new(name, ConnectionCheckState.Passed, $"{algs.Count} signing key(s): {string.Join(", ", algs)}.");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return new(name, ConnectionCheckState.Failed, $"{c.JwksEndpoint} is not a JSON Web Key Set.");
            }
        }
    }

    /// <summary>
    /// An authorization request for the configured client and first redirect URI, not followed: a redirect means the
    /// identity server accepted the client, redirect URI and PKCE parameters; an error names what it rejected.
    /// </summary>
    private async Task<ConnectionCheckStep> ClientAsync(HcgConnectConfiguration c, CancellationToken ct)
    {
        const string name = "Client registration";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = c.ClientId!, ["redirect_uri"] = c.RedirectUris[0].ToString(),
            ["scope"] = string.Join(' ', c.Scopes), ["state"] = Base64Url(RandomNumberGenerator.GetBytes(16)),
            ["nonce"] = Base64Url(RandomNumberGenerator.GetBytes(16)), ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
        }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var url = new UriBuilder(c.AuthorizationEndpoint) { Query = query }.Uri;
        var (response, error) = await GetAsync(url, ct).ConfigureAwait(false);
        if (error is not null) return new(name, ConnectionCheckState.Failed, error);
        using (response)
        {
            var status = (int)response!.StatusCode;
            if (status is >= 300 and < 400)
                return new(name, ConnectionCheckState.Passed, $"{c.Issuer.Host} accepted client {c.ClientId} with redirect URI {c.RedirectUris[0]} and PKCE S256 (the sign-in page was not opened).");
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (code, message) = OAuthError(body);
            var detail = code switch
            {
                "oauth_client_not_found" or "invalid_client" =>
                    $"{c.Issuer.Host} does not know client ID {c.ClientId}: the client is not registered with HCG (or was removed). HCG must register Executable Maker's public desktop client and issue its ID.",
                _ when (code + " " + message).Contains("redirect", StringComparison.OrdinalIgnoreCase) =>
                    $"{c.Issuer.Host} knows the client but rejected the redirect URI {c.RedirectUris[0]}: HCG must register all of {string.Join(", ", c.RedirectUris)} exactly.",
                _ when (code + " " + message).Contains("scope", StringComparison.OrdinalIgnoreCase) =>
                    $"{c.Issuer.Host} rejected the scopes \"{string.Join(' ', c.Scopes)}\": {message ?? code}.",
                _ => $"{c.Issuer.Host} rejected the authorization request (HTTP {status}{(code is null ? "" : ", " + code)}{(message is null ? "" : ": " + message)}).",
            };
            return new(name, ConnectionCheckState.Failed, detail);
        }
    }

    private async Task<ConnectionCheckStep> ApiAsync(string name, Uri url, HcgConnectConfiguration c, CancellationToken ct)
    {
        var (response, error) = await GetAsync(url, ct).ConfigureAwait(false);
        if (error is not null) return new(name, ConnectionCheckState.Warning, error + " Local builds are not affected.");
        using (response)
        {
            var isJson = response!.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            var status = (int)response.StatusCode;
            if (isJson && (response.IsSuccessStatusCode || status is 401 or 403))
                return new(name, ConnectionCheckState.Passed, $"{url.AbsolutePath} on {url.Host} is deployed (HTTP {status}{(status == 401 ? ", sign-in required" : "")}).");
            // 404, or 406 from the site's catch-all page ("Only HTML requests are supported here"): no such API route.
            return new(name, ConnectionCheckState.Warning, status is 404 or 406
                ? $"{url.AbsolutePath} is not deployed on {url.Host} (HTTP {status}: the site has no such API route). Features that use it stay unavailable until HCG deploys it; local builds are not affected."
                : $"{url} answered HTTP {status}{(isJson ? "" : " without JSON")}. Local builds are not affected.");
        }
    }

    private async Task<(HttpResponseMessage? Response, string? Error)> GetAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StepTimeout);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"No answer from {url.Host} within {StepTimeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return (null, Describe(ex, url));
        }
    }

    /// <summary>A specific, user-readable reason for a failed request (DNS, refused, TLS, proxy, …).</summary>
    public static string Describe(HttpRequestException ex, Uri url) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => $"DNS: {url.Host} could not be resolved. Check the address in the configuration and this computer's network connection.",
        HttpRequestError.SecureConnectionError => $"TLS: a secure connection to {url.Host} could not be established ({Security.SecretRedactor.Redact(ex.InnerException?.Message ?? ex.Message)}). Check the system date and any proxy or network filter that replaces certificates.",
        HttpRequestError.ProxyTunnelError => $"Proxy: the network proxy refused the connection to {url.Host}.",
        HttpRequestError.ConnectionError => $"{url.Host}:{url.Port} could not be reached ({Security.SecretRedactor.Redact(ex.InnerException?.Message ?? ex.Message)}).",
        _ => $"{url.Host}: {Security.SecretRedactor.Redact(ex.Message)}",
    };

    private static bool Has(JsonElement d, string key, string value) =>
        d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array && v.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == value);

    private static (string? Code, string? Message) OAuthError(string body)
    {
        try
        {
            var d = JsonDocument.Parse(body).RootElement;
            string? S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (S("error_code") ?? S("error"), S("msg") ?? S("error_description") ?? S("message"));
        }
        catch (JsonException) { return (null, null); }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
