using System.Net;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Importing the issued configuration, applying it at runtime, and the registration it describes.</summary>
public partial class HcgSessionTests
{
    private const string OtherClientId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";

    private static string ConfigJson(string clientId, string? extra = null) =>
        $$"""{ "contractVersion": "hcg.executable-maker.connect/1", "clientId": "{{clientId}}"{{extra}} }""";

    [Fact]
    public void Importing_a_valid_configuration_installs_it_and_keeps_the_previous_one()
    {
        using var dir = new TempDir();
        var file = dir.Write("issued.json", ConfigJson(MockIdentityServer.ClientId));
        File.WriteAllText(Path.Combine(dir.Path, HcgConnectConfiguration.FileName), ConfigJson(OtherClientId));
        var result = HcgConnectImport.Import(file, dir.Path);
        Assert.Null(result.Error);
        Assert.Equal(MockIdentityServer.ClientId, result.Configuration!.ClientId);
        Assert.Equal(MockIdentityServer.ClientId, HcgConnectConfiguration.Load(dir.Path).Configuration.ClientId);
        Assert.Contains(OtherClientId, File.ReadAllText(Path.Combine(dir.Path, "hcg-connect.previous.json")));
    }

    [Theory]
    [InlineData("<ISSUED_PUBLIC_CLIENT_ID>", null, "not an issued HCG client ID")]
    [InlineData(MockIdentityServer.ClientId, ", \"clientSecret\": \"x\"", "must not contain secrets")]
    [InlineData(MockIdentityServer.ClientId, ", \"clientid\": \"x\"", "unknown setting \"clientid\"")]
    [InlineData(MockIdentityServer.ClientId, ", \"issuer\": \"http://hcg.example/auth/v1\"", "must use HTTPS")]
    [InlineData(MockIdentityServer.ClientId, ", \"redirectUris\": [\"http://localhost/cb\"]", "must be http://127.0.0.1:<port>")]
    public void An_unusable_configuration_is_refused_with_the_reason_and_nothing_changes(string clientId, string? extra, string reason)
    {
        using var dir = new TempDir();
        var file = dir.Write("issued.json", ConfigJson(clientId, extra));
        var result = HcgConnectImport.Import(file, dir.Path);
        Assert.Null(result.Configuration);
        Assert.Contains(reason, result.Error);
        Assert.EndsWith("Nothing was changed.", result.Error);
        Assert.False(File.Exists(Path.Combine(dir.Path, HcgConnectConfiguration.FileName)));
    }

    [Fact]
    public void Invalid_json_and_oversized_files_are_refused()
    {
        using var dir = new TempDir();
        Assert.Contains("not valid JSON", HcgConnectImport.Import(dir.Write("a.json", "{ nope"), dir.Path).Error);
        Assert.Contains("about 1 KB", HcgConnectImport.Import(dir.Write("b.json", new string(' ', HcgConnectConfiguration.MaxFileBytes + 1)), dir.Path).Error);
        Assert.Contains("does not exist", HcgConnectImport.Import(Path.Combine(dir.Path, "missing.json"), dir.Path).Error);
    }

    [Fact]
    public async Task Applying_a_configuration_for_another_client_removes_the_session_and_enables_sign_in()
    {
        var server = new MockIdentityServer();
        var store = new SessionOnlyCredentialStore();
        var config = WithFreePorts(MockIdentityServer.Config);
        var account = AccountWith(config, server, store);
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.True(account.IsSignedIn);

        await account.ApplyConfigurationAsync(config with { ClientId = OtherClientId });
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
        Assert.Null(await store.ReadAsync("session"));
        Assert.Equal(OtherClientId, account.Configuration.ClientId);
        Assert.Contains("new HCG Connect configuration", account.Message);
    }

    [Fact]
    public async Task Applying_the_same_client_keeps_the_session_and_an_unconfigured_file_switches_sign_in_off()
    {
        var server = new MockIdentityServer();
        var store = new SessionOnlyCredentialStore();
        var config = WithFreePorts(MockIdentityServer.Config);
        var account = AccountWith(config, server, store);
        await account.SignInAsync(ApprovingBrowser(server));
        await account.ApplyConfigurationAsync(config with { AccountPortalUrl = new Uri("https://hakmicapitalgroup.com/client-portal") });
        Assert.True(account.IsSignedIn);
        Assert.NotNull(await store.ReadAsync("session"));

        await account.ApplyConfigurationAsync(config with { ClientId = null });
        Assert.Equal(HcgAccountStatus.NotConfigured, account.Status);
        Assert.Null(await store.ReadAsync("session"));
    }

    [Fact]
    public async Task A_new_configuration_becomes_usable_for_sign_in_without_a_restart()
    {
        var server = new MockIdentityServer();
        var store = new SessionOnlyCredentialStore();
        var unconfigured = HcgConnectConfiguration.Default;
        var account = new HcgAccountService(unconfigured, new HcgAuthClient(new HttpClient(server), unconfigured, TimeProvider.System), store, TimeProvider.System);
        Assert.Equal(HcgAccountStatus.NotConfigured, account.Status);
        await account.ApplyConfigurationAsync(WithFreePorts(MockIdentityServer.Config));
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.Equal(HcgAccountStatus.SignedIn, account.Status);
    }

    [Fact]
    public void The_registration_request_describes_a_public_native_pkce_client_from_the_configuration()
    {
        var d = JsonDocument.Parse(HcgConnectConfiguration.Default.RegistrationRequestJson()).RootElement;
        Assert.Equal("HCG Executable Maker", d.GetProperty("client_name").GetString());
        Assert.Equal("none", d.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("native", d.GetProperty("application_type").GetString());
        Assert.Equal(["authorization_code", "refresh_token"], d.GetProperty("grant_types").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["code"], d.GetProperty("response_types").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HcgConnectConfiguration.Default.RedirectUris.Select(u => u.ToString()), d.GetProperty("redirect_uris").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("openid email profile", d.GetProperty("scope").GetString());
        Assert.DoesNotContain("secret", d.GetRawText().Replace("token_endpoint_auth_method", ""), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("hcg-connect-").FullName;
        public string Write(string name, string text) { var p = System.IO.Path.Combine(Path, name); File.WriteAllText(p, text); return p; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

/// <summary>The connection check against scripted identity/API responses (nothing here contacts HCG).</summary>
public class HcgConnectionCheckTests
{
    private static readonly HcgConnectConfiguration Config = MockIdentityServer.Config;

    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(HttpStatusCode code) => new(code) { Content = new StringContent("<html>404</html>", Encoding.UTF8, "text/html") };

    private static object Discovery(HcgConnectConfiguration c, string? issuer = null) => new
    {
        issuer = issuer ?? c.Issuer.ToString(), authorization_endpoint = c.AuthorizationEndpoint.ToString(), token_endpoint = c.TokenEndpoint.ToString(),
        userinfo_endpoint = c.UserinfoEndpoint.ToString(), jwks_uri = c.JwksEndpoint.ToString(), code_challenge_methods_supported = new[] { "S256", "plain" },
        token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "none" }, scopes_supported = new[] { "openid", "profile", "email" },
    };

    private static Scripted Hcg(Func<HttpRequestMessage, HttpResponseMessage?>? overrideAnswer = null, string? issuer = null) => new(r =>
    {
        if (overrideAnswer?.Invoke(r) is { } o) return o;
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, Discovery(Config, issuer));
        if (path.EndsWith("/.well-known/jwks.json", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, new { keys = new[] { new { kty = "EC", alg = "ES256" } } });
        if (path.EndsWith("/oauth/authorize", StringComparison.Ordinal))
            return Json(HttpStatusCode.BadRequest, new { code = 400, error_code = "oauth_client_not_found", msg = "invalid client_id" });
        if (path.EndsWith("/ecosystem/v1/status", StringComparison.Ordinal))
            return Json(HttpStatusCode.Unauthorized, new { error = new { code = "unauthorized", message = "Sign in" }, api_version = "v1" });
        return Html(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task An_unregistered_client_is_reported_as_such_and_sign_in_is_not_ready()
    {
        var handler = Hcg();
        var result = await new HcgConnectionCheck(new HttpClient(handler)).RunAsync(Config);
        Assert.Equal(["Configuration", "Identity server", "Signing keys", "Client registration", "HCG central API", "Executable Maker API"], result.Steps.Select(s => s.Name));
        Assert.Equal(ConnectionCheckState.Passed, result.Steps[1].State);
        Assert.Equal(ConnectionCheckState.Passed, result.Steps[2].State);
        Assert.Equal(ConnectionCheckState.Failed, result.Steps[3].State);
        Assert.Contains("does not know client ID", result.Steps[3].Detail);
        Assert.Equal(ConnectionCheckState.Passed, result.Steps[4].State);
        Assert.Equal(ConnectionCheckState.Warning, result.Steps[5].State);
        Assert.Contains("not deployed", result.Steps[5].Detail);
        Assert.False(result.ReadyToSignIn);
        // The authorization request carries PKCE S256 and no secret.
        var authorize = handler.Requests.Single(u => u.AbsolutePath.EndsWith("/oauth/authorize", StringComparison.Ordinal)).Query;
        Assert.Contains("code_challenge_method=S256", authorize);
        Assert.DoesNotContain("secret", authorize, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_accepted_client_redirect_means_sign_in_can_be_attempted()
    {
        var handler = Hcg(r => r.RequestUri!.AbsolutePath.EndsWith("/oauth/authorize", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://beta.hakmicapitalgroup.com/oauth/consent?authorization_id=x") } }
            : null);
        var result = await new HcgConnectionCheck(new HttpClient(handler)).RunAsync(Config);
        Assert.True(result.ReadyToSignIn, string.Join("\n", result.Steps));
        Assert.Contains("accepted client", result.Steps[3].Detail);
    }

    [Fact]
    public async Task A_rejected_redirect_uri_and_a_mismatched_issuer_are_named()
    {
        var redirect = await new HcgConnectionCheck(new HttpClient(Hcg(r => r.RequestUri!.AbsolutePath.EndsWith("/oauth/authorize", StringComparison.Ordinal)
            ? Json(HttpStatusCode.BadRequest, new { error_code = "validation_failed", msg = "redirect_uri is not registered for this client" }) : null))).RunAsync(Config);
        Assert.Contains("rejected the redirect URI", redirect.Steps[3].Detail);

        var issuer = await new HcgConnectionCheck(new HttpClient(Hcg(issuer: "https://other.example/auth/v1"))).RunAsync(Config);
        Assert.Equal(ConnectionCheckState.Failed, issuer.Steps[1].State);
        Assert.Contains("issuer is https://other.example/auth/v1", issuer.Steps[1].Detail);
        Assert.Equal(ConnectionCheckState.Skipped, issuer.Steps[3].State);
    }

    [Fact]
    public async Task Network_failures_say_what_failed()
    {
        var dns = new Scripted(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."));
        var result = await new HcgConnectionCheck(new HttpClient(dns)).RunAsync(Config);
        Assert.StartsWith("DNS: qggdyzoydveafkmjgkpq.supabase.co could not be resolved", result.Steps[1].Detail);

        var tls = new Scripted(_ => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "SSL failed", new System.Security.Authentication.AuthenticationException("certificate untrusted")));
        Assert.StartsWith("TLS:", (await new HcgConnectionCheck(new HttpClient(tls)).RunAsync(Config)).Steps[1].Detail);
    }

    [Fact]
    public async Task A_configuration_without_a_client_is_not_sent_anywhere()
    {
        var handler = Hcg();
        var result = await new HcgConnectionCheck(new HttpClient(handler)).RunAsync(HcgConnectConfiguration.Default);
        Assert.Equal(ConnectionCheckState.Failed, result.Steps[0].State);
        Assert.All(result.Steps.Skip(1), s => Assert.Equal(ConnectionCheckState.Skipped, s.State));
        Assert.Empty(handler.Requests);
    }
}
