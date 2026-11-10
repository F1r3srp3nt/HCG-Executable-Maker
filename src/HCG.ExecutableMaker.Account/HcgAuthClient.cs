using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCG.ExecutableMaker.Account;

public sealed record HcgIdentity(string Subject, string? Email, string? DisplayName);

/// <summary>A signed-in HCG session. Stored only in the OS credential store; never logged.</summary>
public sealed record HcgSession
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required HcgIdentity Identity { get; init; }
    public required string ClientId { get; init; }
    /// <summary>The organisation the user chose (when the account has several); null = personal / default.</summary>
    public string? OrganisationId { get; init; }

    public bool ExpiresSoon(DateTimeOffset now) => ExpiresAt - now < TimeSpan.FromSeconds(60);
    public override string ToString() => $"HcgSession({Identity.Subject}, expires {ExpiresAt:O})"; // never the tokens
}

public enum HcgAuthFailure
{
    /// <summary>The sign-in has not been configured for this build (client not registered).</summary>
    NotConfigured,
    /// <summary>The user declined on the consent page, or HCG refused the account.</summary>
    Denied,
    /// <summary>The session can no longer be refreshed: sign in again.</summary>
    Expired,
    /// <summary>HCG could not be reached or answered with a server error. Nothing about access is implied.</summary>
    Unavailable,
    /// <summary>The response did not match this sign-in (state, nonce, ID token, subject): rejected, nothing stored.</summary>
    Invalid,
    Cancelled,
    /// <summary>Every registered loopback port is in use by another program.</summary>
    PortsBusy,
}

public sealed class HcgAuthException(HcgAuthFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public HcgAuthFailure Failure { get; } = failure;
}

/// <summary>One sign-in in progress: the values the callback and the ID token must match.</summary>
public sealed record HcgSignInAttempt(Uri AuthorizationUrl, string State, string Nonce, PkcePair Pkce, Uri RedirectUri);

/// <summary>
/// OAuth 2.1 authorization code + PKCE (S256) against the HCG identity server, as a public client: no client secret
/// exists or is sent. The browser step is the system browser; the callback arrives on a loopback listener.
/// </summary>
public sealed class HcgAuthClient(HttpClient http, HcgConnectConfiguration config, TimeProvider time)
{
    /// <summary>The HCG Connect configuration in use; replaced when a new configuration is imported (see HcgAccountService).</summary>
    public HcgConnectConfiguration Configuration { get; internal set; } = config;

    /// <summary>Error codes the identity server returns for a refresh token that can never work again (verified live by the LifeOS integration).</summary>
    public static readonly IReadOnlySet<string> DeadRefreshCodes = new HashSet<string>
    {
        "invalid_grant", "refresh_token_not_found", "refresh_token_already_used", "session_not_found", "session_expired",
    };

    private string? _jwks;
    private DateTimeOffset _jwksFetched;

    public HcgSignInAttempt BeginSignIn(Uri redirectUri)
    {
        if (Configuration.MissingForSignIn() is { Count: > 0 } missing)
            throw new HcgAuthException(HcgAuthFailure.NotConfigured, "HCG sign-in is not configured: " + string.Join("; ", missing) + ".");
        var pkce = PkcePair.Create();
        var state = Base64Url.RandomToken();
        var nonce = Base64Url.RandomToken();
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = Configuration.ClientId!,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = string.Join(' ', Configuration.Scopes),
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
        };
        var url = new UriBuilder(Configuration.AuthorizationEndpoint)
        {
            Query = string.Join('&', query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))),
        }.Uri;
        return new HcgSignInAttempt(url, state, nonce, pkce, redirectUri);
    }

    /// <summary>Validates the callback, exchanges the code, validates the ID token and confirms the subject with userinfo.</summary>
    public async Task<HcgSession> CompleteSignInAsync(HcgSignInAttempt attempt, IReadOnlyDictionary<string, string> callback, CancellationToken ct)
    {
        var state = callback.GetValueOrDefault("state") ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(attempt.State)))
            throw new HcgAuthException(HcgAuthFailure.Invalid, "The sign-in response did not match this request (state mismatch). Nothing was stored; try again.");
        if (callback.TryGetValue("error", out var error))
            throw error is "access_denied"
                ? new HcgAuthException(HcgAuthFailure.Denied, "Sign-in was declined, or HCG did not allow this account to connect Executable Maker.")
                : new HcgAuthException(HcgAuthFailure.Invalid, $"HCG returned an error ({Clean(error)}). Try again.");
        if (!callback.TryGetValue("code", out var code) || code.Length is 0 or > 2048)
            throw new HcgAuthException(HcgAuthFailure.Invalid, "The sign-in response had no authorization code.");

        var tokens = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = attempt.RedirectUri.ToString(),
            ["client_id"] = Configuration.ClientId!,
            ["code_verifier"] = attempt.Pkce.Verifier,
        }, refreshing: false, ct).ConfigureAwait(false);
        if (tokens.IdToken is null)
            throw new HcgAuthException(HcgAuthFailure.Invalid, "HCG did not return an ID token for this sign-in (the openid scope was requested).");
        IdTokenValidator.Claims claims;
        try
        {
            claims = IdTokenValidator.Validate(tokens.IdToken, await JwksAsync(ct).ConfigureAwait(false), Configuration.Issuer.ToString(), Configuration.ClientId!, attempt.Nonce, time.GetUtcNow());
        }
        catch (IdTokenValidator.ValidationException ex)
        {
            throw new HcgAuthException(HcgAuthFailure.Invalid, ex.Message + " Nothing was stored.");
        }
        CheckAccessTokenClient(tokens.AccessToken);
        var info = await UserInfoAsync(tokens.AccessToken!, ct).ConfigureAwait(false);
        if (info.Subject != claims.Subject)
            throw new HcgAuthException(HcgAuthFailure.Invalid, "The account HCG reported does not match the signed ID token. Nothing was stored.");
        return Session(tokens, new HcgIdentity(claims.Subject, info.Email ?? claims.Email, info.DisplayName ?? claims.Name), organisation: null);
    }

    public async Task<HcgSession> RefreshAsync(HcgSession session, CancellationToken ct)
    {
        var tokens = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = session.RefreshToken,
            ["client_id"] = session.ClientId,
        }, refreshing: true, ct).ConfigureAwait(false);
        // The refreshed token must still be for the same account and this app. A refresh may return no new ID token
        // (not required) and, if the provider does not rotate it, no new refresh token: the current one stays valid.
        var sub = tokens.AccessToken is { } at && IdTokenValidator.PeekClaims(at) is { } c ? IdTokenValidator.Str(c, "sub") : null;
        if (sub is not null && sub != session.Identity.Subject)
            throw new HcgAuthException(HcgAuthFailure.Invalid, "The refreshed session belongs to a different account. Sign in again.");
        CheckAccessTokenClient(tokens.AccessToken);
        return Session(tokens, session.Identity, session.OrganisationId, previousRefreshToken: session.RefreshToken);
    }

    /// <summary>
    /// HCG access tokens carry the issued client ID as a signed <c>client_id</c> claim (audience <c>authenticated</c>).
    /// The app does not validate access tokens (HCG's APIs do), but one issued to another client is never used.
    /// </summary>
    private void CheckAccessTokenClient(string? accessToken)
    {
        if (accessToken is { } at && IdTokenValidator.PeekClaims(at) is { } c && IdTokenValidator.Str(c, "client_id") is { } issuedTo && issuedTo != Configuration.ClientId)
            throw new HcgAuthException(HcgAuthFailure.Invalid, "HCG returned an access token issued to another app. Nothing was stored; sign in again.");
    }

    public async Task<HcgIdentity> UserInfoAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Configuration.UserinfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var (status, body) = await SendAsync(request, ct).ConfigureAwait(false);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HcgAuthException(HcgAuthFailure.Expired, "HCG no longer accepts this session. Sign in again.");
        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
            throw new HcgAuthException(HcgAuthFailure.Unavailable, "HCG sign-in is temporarily unavailable. Try again later.");
        if (status != HttpStatusCode.OK)
            throw new HcgAuthException(HcgAuthFailure.Invalid, $"HCG's account information could not be read (HTTP {(int)status}).");
        using var doc = Parse(body);
        var root = doc.RootElement;
        var sub = IdTokenValidator.Str(root, "sub") ?? throw new HcgAuthException(HcgAuthFailure.Invalid, "HCG's account information has no subject.");
        return new HcgIdentity(sub, IdTokenValidator.Str(root, "email"), IdTokenValidator.Str(root, "name") ?? IdTokenValidator.Str(root, "preferred_username"));
    }

    internal async Task<string> JwksAsync(CancellationToken ct)
    {
        if (_jwks is not null && time.GetUtcNow() - _jwksFetched < TimeSpan.FromHours(1)) return _jwks;
        using var request = new HttpRequestMessage(HttpMethod.Get, Configuration.JwksEndpoint);
        var (status, body) = await SendAsync(request, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
            throw new HcgAuthException(HcgAuthFailure.Unavailable, "HCG's signing keys could not be read, so the sign-in cannot be verified. Try again later.");
        _jwks = body;
        _jwksFetched = time.GetUtcNow();
        return body;
    }

    private HcgSession Session(TokenResponse tokens, HcgIdentity identity, string? organisation, string? previousRefreshToken = null) => new()
    {
        AccessToken = tokens.AccessToken ?? throw new HcgAuthException(HcgAuthFailure.Invalid, "HCG returned no access token."),
        // Empty = HCG issued no refresh token: the session lasts until the access token expires and is not stored.
        RefreshToken = tokens.RefreshToken ?? previousRefreshToken ?? "",
        ExpiresAt = time.GetUtcNow().AddSeconds(Math.Clamp(tokens.ExpiresIn ?? 3600, 60, 86_400)),
        Identity = identity,
        ClientId = Configuration.ClientId!,
        OrganisationId = organisation,
    };

    private async Task<TokenResponse> TokenRequestAsync(Dictionary<string, string> form, bool refreshing, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Configuration.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        var (status, body) = await SendAsync(request, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.OK)
        {
            try { return JsonSerializer.Deserialize<TokenResponse>(body) ?? throw new JsonException("empty"); }
            catch (JsonException) { throw new HcgAuthException(HcgAuthFailure.Unavailable, "HCG returned an unreadable token response."); }
        }
        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
            throw new HcgAuthException(HcgAuthFailure.Unavailable, "HCG sign-in is temporarily unavailable. Try again later.");
        string? code = null, description = null;
        try
        {
            using var doc = Parse(body);
            code = IdTokenValidator.Str(doc.RootElement, "error_code") ?? IdTokenValidator.Str(doc.RootElement, "error");
            description = IdTokenValidator.Str(doc.RootElement, "error_description") ?? IdTokenValidator.Str(doc.RootElement, "msg");
        }
        catch (HcgAuthException) { }
        if (code is not null && DeadRefreshCodes.Contains(code))
            throw refreshing
                ? new HcgAuthException(HcgAuthFailure.Expired, "Your HCG session has expired or was ended. Sign in again.")
                : new HcgAuthException(HcgAuthFailure.Invalid, "The sign-in code was not accepted (it may have expired or been used). Start sign-in again.");
        if (code is "unauthorized_client" or "invalid_client")
            throw new HcgAuthException(HcgAuthFailure.NotConfigured, "HCG does not recognise this app's sign-in client. Its registration may be missing or changed.");
        throw new HcgAuthException(refreshing ? HcgAuthFailure.Expired : HcgAuthFailure.Invalid,
            $"HCG refused the {(refreshing ? "session refresh" : "sign-in")} ({Clean(code ?? ((int)status).ToString())}{(description is null ? "" : ": " + Clean(description))}).");
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new HcgAuthException(HcgAuthFailure.Cancelled, "Sign-in was cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new HcgAuthException(HcgAuthFailure.Unavailable, "HCG could not be reached. Check your internet connection and try again.", ex);
        }
    }

    private static JsonDocument Parse(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new HcgAuthException(HcgAuthFailure.Unavailable, "HCG returned an unreadable response."); }
    }

    /// <summary>Server-provided text, shortened and stripped of anything token-like, for display.</summary>
    private static string Clean(string text) => HCG.ExecutableMaker.Security.SecretRedactor.Redact(text.Length > 200 ? text[..200] : text);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("expires_in")] public long? ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }
}
