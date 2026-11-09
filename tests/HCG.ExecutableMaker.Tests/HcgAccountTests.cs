using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// MOCK identity server: a scripted HttpMessageHandler that answers like the HCG identity server (Supabase Auth OAuth 2.1
/// server: token, userinfo, JWKS with an ES256 key). The key is generated per test; nothing here contacts HCG.
/// </summary>
public sealed class MockIdentityServer : HttpMessageHandler
{
    public const string Issuer = "https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1";
    public const string ClientId = "11111111-2222-4333-8444-555555555555";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string Subject { get; set; } = "9b2d7c1e-0000-4000-8000-00000000c0de";
    public string? UserinfoSubject { get; set; }
    public string? NonceOverride { get; set; }
    public string? AudienceOverride { get; set; }
    public long? ExpOverride { get; set; }
    public bool SignWithOtherKey { get; set; }
    public string? NextNonce { get; set; }
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
    public List<(string Path, string? Form)> Calls { get; } = [];
    public int RefreshCount { get; private set; }
    /// <summary>Token responses carry no refresh_token (code exchange and/or refresh).</summary>
    public bool OmitRefreshTokenOnExchange { get; set; }
    public bool OmitRefreshTokenOnRefresh { get; set; }
    /// <summary>client_id claim of issued access tokens (default: the app's client ID).</summary>
    public string AccessTokenClientId { get; set; } = ClientId;

    public static HcgConnectConfiguration Config => HcgConnectConfiguration.Default with { ClientId = ClientId };

    public string Jwks()
    {
        var p = _key.ExportParameters(false);
        return JsonSerializer.Serialize(new { keys = new[] { new { kty = "EC", crv = "P-256", kid = "k1", use = "sig", alg = "ES256", x = Base64Url.Encode(p.Q.X), y = Base64Url.Encode(p.Q.Y) } } });
    }

    public string IdToken(string nonce)
    {
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("""{"alg":"ES256","kid":"k1","typ":"JWT"}"""));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = Base64Url.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = Issuer, ["aud"] = AudienceOverride ?? ClientId, ["sub"] = Subject, ["iat"] = now, ["exp"] = ExpOverride ?? now + 3600,
            ["nonce"] = NonceOverride ?? nonce, ["email"] = "founder@example.test", ["name"] = "Test Founder",
        })));
        using var signer = SignWithOtherKey ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var sig = (signer ?? _key).SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return header + "." + payload + "." + Base64Url.Encode(sig);
    }

    public static string AccessToken(string sub, string clientId = ClientId) =>
        "eyJhbGciOiJFUzI1NiJ9." + Base64Url.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub, aud = "authenticated", role = "authenticated", client_id = clientId }))) + ".c2ln";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var form = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.RequestUri!.AbsolutePath, form));
        if (Override?.Invoke(request) is { } custom) return custom;
        var path = request.RequestUri.AbsolutePath;
        if (path.EndsWith("/.well-known/jwks.json")) return Json(HttpStatusCode.OK, Jwks());
        if (path.EndsWith("/oauth/userinfo"))
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { sub = UserinfoSubject ?? Subject, email = "founder@example.test", name = "Test Founder" }));
        if (path.EndsWith("/oauth/token"))
        {
            var refreshing = form!.Contains("grant_type=refresh_token");
            if (refreshing) RefreshCount++;
            Assert.DoesNotContain("client_secret", form);
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["access_token"] = AccessToken(Subject, AccessTokenClientId),
                ["refresh_token"] = (refreshing ? OmitRefreshTokenOnRefresh : OmitRefreshTokenOnExchange) ? null : "rt-" + Guid.NewGuid().ToString("N"),
                ["expires_in"] = 3600, ["token_type"] = "bearer",
                ["id_token"] = refreshing ? null : IdToken(NextNonce ?? ""),
            }));
        }
        return Json(HttpStatusCode.NotFound, "{}");
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public class HcgAuthTests
{
    private static (HcgAuthClient Client, MockIdentityServer Server) Client(HcgConnectConfiguration? config = null)
    {
        var server = new MockIdentityServer();
        return (new HcgAuthClient(new HttpClient(server), config ?? MockIdentityServer.Config, TimeProvider.System), server);
    }

    private static readonly Uri Redirect = new("http://127.0.0.1:47820/oauth/callback");

    [Fact]
    public void Pkce_uses_S256_as_in_RFC_7636_appendix_B()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", PkcePair.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var p = PkcePair.Create();
        Assert.InRange(p.Verifier.Length, 43, 128);
        Assert.NotEqual(p.Verifier, PkcePair.Create().Verifier);
    }

    [Fact]
    public void Authorization_url_is_code_flow_with_pkce_state_and_nonce_and_no_secret()
    {
        var (client, _) = Client();
        var attempt = client.BeginSignIn(Redirect);
        var q = LoopbackCallbackListener.ParseQuery(attempt.AuthorizationUrl.Query.TrimStart('?'));
        Assert.Equal("https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/authorize", attempt.AuthorizationUrl.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", q["response_type"]);
        Assert.Equal(MockIdentityServer.ClientId, q["client_id"]);
        Assert.Equal(Redirect.ToString(), q["redirect_uri"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(PkcePair.ChallengeFor(attempt.Pkce.Verifier), q["code_challenge"]);
        Assert.Equal(attempt.State, q["state"]);
        Assert.Equal(attempt.Nonce, q["nonce"]);
        Assert.Equal("openid email profile", q["scope"]);
        Assert.DoesNotContain("secret", attempt.AuthorizationUrl.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_a_registered_client_sign_in_is_not_configured()
    {
        var (client, _) = Client(HcgConnectConfiguration.Default);
        Assert.Equal(HcgAuthFailure.NotConfigured, Assert.Throws<HcgAuthException>(() => client.BeginSignIn(Redirect)).Failure);
        Assert.Contains(HcgConnectConfiguration.Default.MissingForSignIn(), m => m.Contains("not registered"));
    }

    [Fact]
    public async Task Callback_state_error_and_code_are_validated_before_any_token_request()
    {
        var (client, server) = Client();
        var attempt = client.BeginSignIn(Redirect);
        var mismatch = await Assert.ThrowsAsync<HcgAuthException>(() => client.CompleteSignInAsync(attempt, new Dictionary<string, string> { ["state"] = "forged", ["code"] = "c" }, default));
        Assert.Equal(HcgAuthFailure.Invalid, mismatch.Failure);
        var denied = await Assert.ThrowsAsync<HcgAuthException>(() => client.CompleteSignInAsync(attempt, new Dictionary<string, string> { ["state"] = attempt.State, ["error"] = "access_denied" }, default));
        Assert.Equal(HcgAuthFailure.Denied, denied.Failure);
        var noCode = await Assert.ThrowsAsync<HcgAuthException>(() => client.CompleteSignInAsync(attempt, new Dictionary<string, string> { ["state"] = attempt.State }, default));
        Assert.Equal(HcgAuthFailure.Invalid, noCode.Failure);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task Successful_sign_in_validates_the_id_token_and_confirms_the_subject()
    {
        var (client, server) = Client();
        var attempt = client.BeginSignIn(Redirect);
        server.NextNonce = attempt.Nonce;
        var session = await client.CompleteSignInAsync(attempt, new Dictionary<string, string> { ["state"] = attempt.State, ["code"] = "auth-code" }, default);
        Assert.Equal(server.Subject, session.Identity.Subject);
        Assert.Equal("founder@example.test", session.Identity.Email);
        var tokenCall = server.Calls.Single(c => c.Path.EndsWith("/oauth/token"));
        Assert.Contains("code_verifier=" + attempt.Pkce.Verifier, tokenCall.Form);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(Redirect.ToString()), tokenCall.Form);
        Assert.DoesNotContain(session.AccessToken, session.ToString());
    }

    public static TheoryData<string, Action<MockIdentityServer>> BadTokens => new()
    {
        { "nonce", s => s.NonceOverride = "replayed" },
        { "application", s => s.AudienceOverride = "some-other-client" },
        { "expired", s => s.ExpOverride = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds() },
        { "signature", s => s.SignWithOtherKey = true },
        { "does not match", s => s.UserinfoSubject = "someone-else" },
    };

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Invalid_identity_tokens_are_rejected_and_nothing_is_stored(string expected, Action<MockIdentityServer> tamper)
    {
        var (client, server) = Client();
        var attempt = client.BeginSignIn(Redirect);
        server.NextNonce = attempt.Nonce;
        tamper(server);
        var ex = await Assert.ThrowsAsync<HcgAuthException>(() => client.CompleteSignInAsync(attempt, new Dictionary<string, string> { ["state"] = attempt.State, ["code"] = "c" }, default));
        Assert.Equal(HcgAuthFailure.Invalid, ex.Failure);
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("refresh_token_not_found", HcgAuthFailure.Expired)]
    [InlineData("refresh_token_already_used", HcgAuthFailure.Expired)]
    [InlineData("session_not_found", HcgAuthFailure.Expired)]
    [InlineData("invalid_grant", HcgAuthFailure.Expired)]
    public async Task Dead_refresh_tokens_mean_the_session_expired(string code, HcgAuthFailure expected)
    {
        var (client, server) = Client();
        server.Override = r => r.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
            ? MockIdentityServer.Json(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = "invalid_grant", error_code = code })) : null;
        var session = new HcgSession { AccessToken = "a", RefreshToken = "r", ExpiresAt = DateTimeOffset.UtcNow, Identity = new("s", null, null), ClientId = MockIdentityServer.ClientId };
        Assert.Equal(expected, (await Assert.ThrowsAsync<HcgAuthException>(() => client.RefreshAsync(session, default))).Failure);
    }

    [Fact]
    public async Task Server_errors_and_network_failures_are_unavailable_not_denied()
    {
        var (client, server) = Client();
        server.Override = _ => MockIdentityServer.Json(HttpStatusCode.ServiceUnavailable, "{}");
        var session = new HcgSession { AccessToken = "a", RefreshToken = "r", ExpiresAt = DateTimeOffset.UtcNow, Identity = new("s", null, null), ClientId = MockIdentityServer.ClientId };
        Assert.Equal(HcgAuthFailure.Unavailable, (await Assert.ThrowsAsync<HcgAuthException>(() => client.RefreshAsync(session, default))).Failure);
        server.Override = _ => throw new HttpRequestException("no route to host");
        Assert.Equal(HcgAuthFailure.Unavailable, (await Assert.ThrowsAsync<HcgAuthException>(() => client.RefreshAsync(session, default))).Failure);
    }

    [Fact]
    public async Task Loopback_listener_accepts_only_its_path_and_returns_the_query()
    {
        var config = MockIdentityServer.Config with { RedirectUris = [new Uri($"http://127.0.0.1:{FreePort()}/oauth/callback")] };
        using var listener = LoopbackCallbackListener.StartFirstAvailable(config.RedirectUris)!;
        var wait = listener.WaitForCallbackAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        using var http = new HttpClient();
        var notFound = await http.GetAsync(new Uri(listener.RedirectUri, "/favicon.ico"));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        var ok = await http.GetAsync(listener.RedirectUri + "?code=abc%2B1&state=xyz");
        var page = await ok.Content.ReadAsStringAsync();
        Assert.Contains("HAKMI CAPITAL GROUP", page);
        Assert.DoesNotContain("abc", page); // the code is never echoed
        var q = await wait;
        Assert.Equal("abc+1", q["code"]);
        Assert.Equal("xyz", q["state"]);
    }

    [Fact]
    public void A_busy_port_moves_to_the_next_registered_redirect_and_all_busy_is_reported()
    {
        var p1 = FreePort();
        var p2 = FreePort();
        var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, p1);
        blocker.Start();
        try
        {
            using var l = LoopbackCallbackListener.StartFirstAvailable([new Uri($"http://127.0.0.1:{p1}/cb"), new Uri($"http://127.0.0.1:{p2}/cb")]);
            Assert.Equal(p2, l!.RedirectUri.Port);
            Assert.Null(LoopbackCallbackListener.StartFirstAvailable([new Uri($"http://127.0.0.1:{p1}/cb")]));
        }
        finally { blocker.Stop(); }
    }

    internal static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void Configuration_files_with_secrets_or_another_contract_are_refused()
    {
        using var t = new TempDir();
        t.Write(HcgConnectConfiguration.FileName, """{"contractVersion":"hcg.executable-maker.connect/1","clientId":"abc","clientSecret":"nope"}""");
        var r = HcgConnectConfiguration.Load(t.Path);
        Assert.Null(r.Configuration.ClientId);
        Assert.Contains("must not contain secrets", r.Problem);
        t.Write(HcgConnectConfiguration.FileName, """{"contractVersion":"hcg.lifeos.connect/1","clientId":"abc"}""");
        Assert.Contains("contract", HcgConnectConfiguration.Load(t.Path).Problem);
        t.Write(HcgConnectConfiguration.FileName, """{"contractVersion":"hcg.executable-maker.connect/1","clientId":"abc"}""");
        Assert.Equal("abc", HcgConnectConfiguration.Load(t.Path).Configuration.ClientId);
        var bad = MockIdentityServer.Config with { RedirectUris = [new Uri("http://example.com:1234/cb"), new Uri("com.hcg.app://cb")] };
        Assert.Equal(2, bad.MissingForSignIn().Count(m => m.Contains("loopback")));
    }
}

public class HcgSessionTests
{
    private static (HcgAccountService Account, MockIdentityServer Server, SessionOnlyCredentialStore Store) Account(SessionOnlyCredentialStore? store = null)
    {
        var server = new MockIdentityServer();
        store ??= new SessionOnlyCredentialStore();
        var config = MockIdentityServer.Config;
        return (new HcgAccountService(config, new HcgAuthClient(new HttpClient(server), config, TimeProvider.System), store, TimeProvider.System), server, store);
    }

    private sealed class ScriptedBrowser(Func<Uri, Task> onOpen) : ISystemBrowser
    {
        public async Task<bool> OpenAsync(Uri url) { _ = Task.Run(() => onOpen(url)); await Task.Yield(); return true; }
    }

    /// <summary>MOCK browser: follows the authorization URL straight to the loopback callback (as if the user approved).</summary>
    private static ScriptedBrowser ApprovingBrowser(MockIdentityServer server, Func<IReadOnlyDictionary<string, string>, string>? callbackQuery = null) => new(async url =>
    {
        var q = LoopbackCallbackListener.ParseQuery(url.Query.TrimStart('?'));
        server.NextNonce = q["nonce"];
        using var http = new HttpClient();
        var query = callbackQuery?.Invoke(q) ?? $"code=the-code&state={Uri.EscapeDataString(q["state"])}";
        await http.GetAsync(q["redirect_uri"] + "?" + query);
    });

    private static HcgConnectConfiguration WithFreePorts(HcgConnectConfiguration c) =>
        c with { RedirectUris = [new Uri($"http://127.0.0.1:{HcgAuthTests.FreePort()}/oauth/callback")] };

    private static HcgAccountService AccountWith(HcgConnectConfiguration config, MockIdentityServer server, ISecureCredentialStore store) =>
        new(config, new HcgAuthClient(new HttpClient(server), config, TimeProvider.System), store, TimeProvider.System);

    [Fact]
    public async Task Sign_in_through_the_browser_stores_only_the_refresh_token_and_restores_on_next_start()
    {
        var server = new MockIdentityServer();
        var store = new SessionOnlyCredentialStore();
        var config = WithFreePorts(MockIdentityServer.Config);
        var account = AccountWith(config, server, store);
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.Equal(HcgAccountStatus.SignedIn, account.Status);
        Assert.Equal("founder@example.test", account.Identity!.Email);
        var stored = await store.ReadAsync("session");
        Assert.NotNull(stored);
        Assert.DoesNotContain("access", stored, StringComparison.OrdinalIgnoreCase); // access token stays in memory
        Assert.Contains("\"RefreshToken\"", stored);
        Assert.True(Encoding.UTF8.GetByteCount(stored!) < WindowsCredentialStore.MaxBlobBytes);

        // Next start: restore = one refresh, then signed in again.
        var restarted = AccountWith(config, server, store);
        await restarted.InitializeAsync();
        Assert.Equal(HcgAccountStatus.SignedIn, restarted.Status);
        Assert.Equal(1, server.RefreshCount);
        Assert.NotNull(await restarted.GetAccessTokenAsync());
    }

    [Fact]
    public async Task A_refresh_without_a_new_refresh_token_keeps_the_current_one_and_needs_no_new_id_token()
    {
        var server = new MockIdentityServer { OmitRefreshTokenOnRefresh = true };
        var store = new SessionOnlyCredentialStore();
        var config = WithFreePorts(MockIdentityServer.Config);
        var account = AccountWith(config, server, store);
        await account.SignInAsync(ApprovingBrowser(server));
        var issued = JsonDocument.Parse((await store.ReadAsync("session"))!).RootElement.GetProperty("RefreshToken").GetString();
        var restarted = AccountWith(config, server, store);
        await restarted.InitializeAsync(); // refresh: no id_token, no refresh_token in the response
        Assert.Equal(HcgAccountStatus.SignedIn, restarted.Status);
        Assert.Equal(issued, JsonDocument.Parse((await store.ReadAsync("session"))!).RootElement.GetProperty("RefreshToken").GetString());
    }

    [Fact]
    public async Task Without_a_refresh_token_the_session_lasts_this_run_only_and_is_not_stored()
    {
        var server = new MockIdentityServer { OmitRefreshTokenOnExchange = true };
        var store = new SessionOnlyCredentialStore();
        var account = AccountWith(WithFreePorts(MockIdentityServer.Config), server, store);
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.Equal(HcgAccountStatus.SignedIn, account.Status);
        Assert.Contains("for this run only", account.Message);
        Assert.Null(await store.ReadAsync("session"));
        Assert.True(await account.RecoverFromUnauthorizedAsync((await account.GetAccessTokenAsync())!) is false);
        Assert.Equal(HcgAccountStatus.Expired, account.Status);
        Assert.Equal(0, server.RefreshCount); // nothing to refresh with: no call is made
    }

    [Fact]
    public async Task An_access_token_issued_to_another_client_is_refused_and_nothing_is_stored()
    {
        var server = new MockIdentityServer { AccessTokenClientId = "99999999-0000-4000-8000-000000000000" };
        var store = new SessionOnlyCredentialStore();
        var account = AccountWith(WithFreePorts(MockIdentityServer.Config), server, store);
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
        Assert.Contains("issued to another app", account.Message);
        Assert.Null(await store.ReadAsync("session"));
    }

    [Fact]
    public async Task Forged_state_in_the_callback_is_rejected_and_nothing_is_stored()
    {
        var server = new MockIdentityServer();
        var store = new SessionOnlyCredentialStore();
        var account = AccountWith(WithFreePorts(MockIdentityServer.Config), server, store);
        await account.SignInAsync(ApprovingBrowser(server, _ => "code=x&state=attacker"));
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
        Assert.Contains("state mismatch", account.Message);
        Assert.Null(await store.ReadAsync("session"));
    }

    [Fact]
    public async Task Declined_consent_is_denied()
    {
        var server = new MockIdentityServer();
        var account = AccountWith(WithFreePorts(MockIdentityServer.Config), server, new SessionOnlyCredentialStore());
        await account.SignInAsync(ApprovingBrowser(server, q => $"error=access_denied&state={Uri.EscapeDataString(q["state"])}"));
        Assert.Equal(HcgAccountStatus.Denied, account.Status);
    }

    [Fact]
    public async Task Expired_refresh_clears_the_stored_session()
    {
        var (account, server, store) = Account();
        await store.WriteAsync("session", JsonSerializer.Serialize(new { Version = 1, ClientId = MockIdentityServer.ClientId, RefreshToken = "old", Subject = "s" }));
        server.Override = r => r.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
            ? MockIdentityServer.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_code":"session_expired"}""") : null;
        await account.InitializeAsync();
        Assert.Equal(HcgAccountStatus.Expired, account.Status);
        Assert.Null(await store.ReadAsync("session"));
        Assert.Null(await account.GetAccessTokenAsync());
    }

    [Fact]
    public async Task An_outage_during_restore_keeps_the_session_and_reports_unavailable()
    {
        var (account, server, store) = Account();
        await store.WriteAsync("session", JsonSerializer.Serialize(new { Version = 1, ClientId = MockIdentityServer.ClientId, RefreshToken = "keep", Subject = server.Subject, Email = "a@b.c" }));
        server.Override = _ => MockIdentityServer.Json(HttpStatusCode.BadGateway, "{}");
        await account.InitializeAsync();
        Assert.Equal(HcgAccountStatus.Unavailable, account.Status);
        Assert.Equal("a@b.c", account.Identity!.Email);
        Assert.NotNull(await store.ReadAsync("session"));
        server.Override = null;
        await account.RetryAsync();
        Assert.Equal(HcgAccountStatus.SignedIn, account.Status);
    }

    [Fact]
    public async Task A_stored_session_of_another_client_is_discarded()
    {
        var (account, server, store) = Account();
        await store.WriteAsync("session", JsonSerializer.Serialize(new { Version = 1, ClientId = "another-app", RefreshToken = "r", Subject = "s" }));
        await account.InitializeAsync();
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
        Assert.Null(await store.ReadAsync("session"));
        Assert.Equal(0, server.RefreshCount);
    }

    [Fact]
    public async Task Sign_out_and_revocation_remove_the_session()
    {
        var (account, server, store) = Account();
        await store.WriteAsync("session", JsonSerializer.Serialize(new { Version = 1, ClientId = MockIdentityServer.ClientId, RefreshToken = "r", Subject = server.Subject }));
        await account.InitializeAsync();
        Assert.True(account.IsSignedIn);
        await account.MarkRevokedAsync("HCG has withdrawn access.");
        Assert.Equal(HcgAccountStatus.Revoked, account.Status);
        Assert.Null(await store.ReadAsync("session"));
        await account.SignOutAsync();
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
    }

    [Fact]
    public async Task Not_configured_builds_contact_nothing()
    {
        var server = new MockIdentityServer();
        var config = HcgConnectConfiguration.Default; // no client registered
        var account = AccountWith(config, server, new SessionOnlyCredentialStore());
        Assert.Equal(HcgAccountStatus.NotConfigured, account.Status);
        await account.InitializeAsync();
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.Equal(HcgAccountStatus.NotConfigured, account.Status);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task The_platform_credential_store_round_trips_a_secret()
    {
        var (store, problem) = SecureCredentialStores.ForCurrentPlatform();
        if (!store.IsPersistent)
        {
            Assert.NotNull(problem); // explained: session-only on this host (e.g. Linux without a keyring)
            return;
        }
        var account = "test-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await store.WriteAsync(account, "value-1");
            await store.WriteAsync(account, "value-2"); // update in place
            Assert.Equal("value-2", await store.ReadAsync(account));
        }
        catch (Exception ex) when (OperatingSystem.IsLinux() && ex is InvalidOperationException)
        {
            return; // secret-tool present but no unlocked keyring (headless CI): reported to the user the same way
        }
        finally { await store.DeleteAsync(account); }
        Assert.Null(await store.ReadAsync(account));
    }
}
