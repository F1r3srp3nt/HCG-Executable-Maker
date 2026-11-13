using System.Net;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// The Account connection: configuration missing locally vs. rejected by HCG (only with HCG's own answer), import and
/// persistence across restarts, Retry connection, exact field errors, the registration export, browser cancellation.
/// All against MOCK servers; nothing here contacts HCG.
/// </summary>
public partial class HcgSessionTests
{
    private sealed class RecordingBrowser(Func<Uri, Task>? onOpen = null, bool opens = true) : ISystemBrowser
    {
        public List<Uri> Opened { get; } = [];
        public async Task<bool> OpenAsync(Uri url)
        {
            Opened.Add(url);
            if (onOpen is not null) _ = Task.Run(() => onOpen(url));
            await Task.Yield();
            return opens;
        }
    }

    /// <summary>A connection check whose authorization probe gets the given answer (or a network failure).</summary>
    private static HcgConnectionCheck Probe(HttpStatusCode? status, string? body = null) => new(new HttpClient(new ProbeHandler(status, body)));

    private sealed class ProbeHandler(HttpStatusCode? status, string? body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (status is null) throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.");
            var response = new HttpResponseMessage(status.Value);
            if (status is HttpStatusCode.Found) response.Headers.Location = new Uri("https://beta.hakmicapitalgroup.com/oauth/consent");
            else response.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private static HcgAccountService AccountWithProbe(HcgConnectConfiguration config, MockIdentityServer server, ISecureCredentialStore store, HcgConnectionCheck check) =>
        new(config, new HcgAuthClient(new HttpClient(server), config, TimeProvider.System), store, TimeProvider.System, check: check);

    // ───────────── Configuration missing locally ─────────────

    [Fact]
    public void Missing_local_configuration_names_the_fields_and_never_claims_HCG_rejected_the_app()
    {
        var account = new HcgAccountService(HcgConnectConfiguration.Default, new HcgAuthClient(new HttpClient(new MockIdentityServer()), HcgConnectConfiguration.Default, TimeProvider.System),
            new SessionOnlyCredentialStore(), TimeProvider.System);
        Assert.Equal(HcgAccountStatus.NotConfigured, account.Status);
        Assert.Contains("Missing or invalid: clientId", account.Message, StringComparison.Ordinal);
        Assert.StartsWith("missing: no client ID is configured on this computer", Assert.Single(account.Configuration.Issues()).Problem, StringComparison.Ordinal);
        Assert.Contains("it does not mean HCG rejected the app", account.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not registered", account.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(", \"issuer\": \"http://auth.example/auth/v1\"", "issuer", "must use HTTPS")]
    [InlineData(", \"redirectUris\": [\"http://localhost:47820/oauth/callback\"]", "redirectUris", "http://127.0.0.1:<port>")]
    [InlineData(", \"scopes\": [\"email\"]", "scopes", "must include openid")]
    [InlineData(", \"tokenEndpoint\": \"https://elsewhere.example/oauth/token\"", "authorizationEndpoint/tokenEndpoint/jwksEndpoint", "issuer's host")]
    public void Each_invalid_field_is_reported_by_name(string extra, string field, string reason)
    {
        var (config, problem) = HcgConnectConfiguration.Parse(ConfigJson(MockIdentityServer.ClientId, extra));
        Assert.Null(problem);
        Assert.Contains(config.Issues(), i => i.Field == field && i.Problem.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrong_value_type_names_the_field()
    {
        var (_, problem) = HcgConnectConfiguration.Parse("""{ "contractVersion": "hcg.executable-maker.connect/1", "clientId": "11111111-2222-4333-8444-555555555555", "scopes": "openid" }""");
        Assert.Contains("(field scopes)", problem, StringComparison.Ordinal);
    }

    // ───────────── Import, persistence, retry ─────────────

    [Fact]
    public async Task An_imported_configuration_survives_a_restart_and_retry_rereads_a_replaced_file()
    {
        using var dir = new TempDir();
        var issued = dir.Write("issued.json", ConfigJson(MockIdentityServer.ClientId));
        Assert.Null(HcgConnectImport.Import(issued, dir.Path).Error);

        // "Restart": a new process loads the data folder exactly as Composition does.
        var (loaded, problem) = HcgConnectConfiguration.Load(dir.Path);
        Assert.Null(problem);
        Assert.Equal(MockIdentityServer.ClientId, loaded.ClientId);
        var restarted = new HcgAccountService(loaded, new HcgAuthClient(new HttpClient(new MockIdentityServer()), loaded, TimeProvider.System), new SessionOnlyCredentialStore(), TimeProvider.System);
        Assert.Equal(HcgAccountStatus.SignedOut, restarted.Status);

        // Someone replaces the file by hand with a broken one; Retry connection reports it with the reason.
        File.WriteAllText(Path.Combine(dir.Path, HcgConnectConfiguration.FileName), "{ \"contractVersion\": \"hcg.executable-maker.connect/1\", \"clientid\": \"x\" }");
        await restarted.ReloadConfigurationAsync(dir.Path);
        Assert.Equal(HcgAccountStatus.NotConfigured, restarted.Status);
        Assert.Contains("unknown setting \"clientid\"", restarted.Message, StringComparison.Ordinal);

        // ...and fixes it: Retry connection applies it without a restart.
        File.WriteAllText(Path.Combine(dir.Path, HcgConnectConfiguration.FileName), ConfigJson(OtherClientId));
        await restarted.ReloadConfigurationAsync(dir.Path);
        Assert.Equal((HcgAccountStatus.SignedOut, OtherClientId), (restarted.Status, restarted.Configuration.ClientId));
    }

    // ───────────── Rejected by HCG: only with HCG's own answer ─────────────

    [Fact]
    public async Task A_client_HCG_does_not_know_is_reported_from_its_answer_and_no_browser_opens()
    {
        var browser = new RecordingBrowser();
        var account = AccountWithProbe(WithFreePorts(MockIdentityServer.Config), new MockIdentityServer(), new SessionOnlyCredentialStore(),
            Probe(HttpStatusCode.BadRequest, """{"code":400,"error_code":"oauth_client_not_found","msg":"invalid client_id"}"""));
        await account.SignInAsync(browser);
        Assert.Equal(HcgAccountStatus.ClientRejected, account.Status);
        Assert.Contains("answered oauth_client_not_found", account.Message, StringComparison.Ordinal);
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task An_unreachable_identity_service_is_unavailable_not_rejected()
    {
        var browser = new RecordingBrowser();
        var account = AccountWithProbe(WithFreePorts(MockIdentityServer.Config), new MockIdentityServer(), new SessionOnlyCredentialStore(), Probe(null));
        await account.SignInAsync(browser);
        Assert.Equal(HcgAccountStatus.Unavailable, account.Status);
        Assert.StartsWith("DNS:", account.Message, StringComparison.Ordinal);
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task An_accepted_client_signs_in_after_the_preflight()
    {
        var server = new MockIdentityServer();
        var account = AccountWithProbe(WithFreePorts(MockIdentityServer.Config), server, new SessionOnlyCredentialStore(), Probe(HttpStatusCode.Found));
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.Equal(HcgAccountStatus.SignedIn, account.Status);
    }

    [Fact]
    public async Task A_token_endpoint_invalid_client_is_a_rejection_with_HCGs_code()
    {
        var server = new MockIdentityServer
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal)
                ? MockIdentityServer.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"Client authentication failed"}""") : null,
        };
        var account = AccountWith(WithFreePorts(MockIdentityServer.Config), server, new SessionOnlyCredentialStore());
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.Equal(HcgAccountStatus.ClientRejected, account.Status);
        Assert.Contains("(invalid_client: Client authentication failed)", account.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejection_clears_when_HCG_accepts_the_client_again()
    {
        var account = AccountWith(MockIdentityServer.Config, new MockIdentityServer(), new SessionOnlyCredentialStore());
        account.MarkClientRejected("HCG's identity service answered oauth_client_not_found.");
        Assert.Equal(HcgAccountStatus.ClientRejected, account.Status);
        account.ClearClientRejection();
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
    }

    // ───────────── Browser cancellation ─────────────

    [Fact]
    public async Task Cancelling_while_the_browser_is_open_ends_the_attempt_and_stores_nothing()
    {
        var store = new SessionOnlyCredentialStore();
        var account = AccountWith(WithFreePorts(MockIdentityServer.Config), new MockIdentityServer(), store);
        var browser = new RecordingBrowser(); // the user closes the tab: no callback ever arrives
        var signIn = account.SignInAsync(browser);
        await WaitFor(() => account.Status == HcgAccountStatus.SigningIn);
        account.CancelSignIn();
        await signIn;
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
        Assert.Equal("Sign-in was cancelled.", account.Message);
        Assert.Single(browser.Opened);
        Assert.Null(await store.ReadAsync("session"));
    }

    [Fact]
    public async Task A_browser_that_cannot_open_is_reported()
    {
        var account = AccountWith(WithFreePorts(MockIdentityServer.Config), new MockIdentityServer(), new SessionOnlyCredentialStore());
        await account.SignInAsync(new RecordingBrowser(opens: false));
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
        Assert.Contains("browser could not be opened", account.Message, StringComparison.Ordinal);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    // ───────────── Registration handoff ─────────────

    [Fact]
    public void The_exported_template_is_accepted_by_the_loader_and_only_lacks_the_issued_client_id()
    {
        var template = HcgConnectConfiguration.TemplateJson();
        var (config, problem) = HcgConnectConfiguration.Parse(template);
        Assert.Null(problem);
        var issue = Assert.Single(config.Issues());
        Assert.Equal("clientId", issue.Field);
        Assert.Contains("template placeholder", issue.Problem, StringComparison.Ordinal);
        var keys = JsonDocument.Parse(template).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(keys, k => Assert.Contains(k, HcgConnectConfiguration.KnownKeys));
        Assert.DoesNotContain("secret", template, StringComparison.OrdinalIgnoreCase);
        // With the issued ID filled in, sign-in is ready.
        var (filled, _) = HcgConnectConfiguration.Parse(template.Replace(HcgConnectConfiguration.ClientIdPlaceholder, MockIdentityServer.ClientId, StringComparison.Ordinal));
        Assert.Empty(filled.Issues());
    }

    [Fact]
    public void The_registration_export_writes_the_request_template_and_steps_from_the_implemented_flow()
    {
        using var dir = new TempDir();
        var files = HcgRegistrationExport.Write(dir.Path, HcgConnectConfiguration.Default);
        Assert.Equal(3, files.Count);
        var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.Path, HcgRegistrationExport.RequestFile))).RootElement;
        Assert.Equal("none", request.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal(HcgConnectConfiguration.Default.RedirectUris.Select(u => u.ToString()), request.GetProperty("redirect_uris").EnumerateArray().Select(e => e.GetString()));
        var readme = File.ReadAllText(Path.Combine(dir.Path, HcgRegistrationExport.ReadmeFile));
        Assert.Contains("http://127.0.0.1:47820/oauth/callback", readme, StringComparison.Ordinal);
        Assert.Contains("Do not reuse another app's client", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", string.Concat(files.Select(File.ReadAllText)), StringComparison.Ordinal);
    }
}
