using System.Text.Json;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// HCG session isolation: production, development and TEST sessions never share a stored session; a stored session is
/// bound to its client and its issuer; signing out removes only this environment's session; switching account replaces
/// the session; and a session is sent only to a Store of the same kind (never production to a TEST Store or back).
/// </summary>
public partial class HcgSessionTests
{
    [Fact]
    public void Production_development_and_TEST_sessions_have_separate_stored_entries()
    {
        var production = HcgConnectConfiguration.Production;
        var development = HcgConnectConfiguration.Development;
        var test = production with { Issuer = new Uri("http://127.0.0.1:5123/auth/v1") };
        Assert.Equal("session", production.SessionStoreAccount);
        Assert.Equal("session-development", development.SessionStoreAccount);
        Assert.Equal("session-local-test", test.SessionStoreAccount);
        Assert.Equal(3, new[] { production, development, test }.Select(c => c.SessionStoreAccount).Distinct().Count());
    }

    [Fact]
    public async Task A_stored_session_from_another_issuer_is_never_refreshed_and_is_deleted()
    {
        var (account, server, store) = Account();
        await store.WriteAsync("session", JsonSerializer.Serialize(new
        {
            Version = 1, ClientId = MockIdentityServer.ClientId, RefreshToken = "r-from-elsewhere", Subject = server.Subject,
            Issuer = "http://127.0.0.1:9/auth/v1",
        }));
        await account.InitializeAsync();
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
        Assert.Equal(0, server.RefreshCount);            // the other issuer's refresh token was not sent here
        Assert.Null(await store.ReadAsync("session"));
    }

    [Fact]
    public async Task A_session_stored_before_issuers_were_recorded_restores_and_gains_its_issuer()
    {
        var (account, server, store) = Account();
        await store.WriteAsync("session", JsonSerializer.Serialize(new { Version = 1, ClientId = MockIdentityServer.ClientId, RefreshToken = "r", Subject = server.Subject }));
        await account.InitializeAsync();
        Assert.True(account.IsSignedIn);
        Assert.Contains($"\"Issuer\":\"{MockIdentityServer.Issuer}\"", await store.ReadAsync("session"));
    }

    [Fact]
    public async Task Signing_out_removes_only_this_environments_session()
    {
        var (account, server, store) = Account();
        await store.WriteAsync("session-local-test", "TEST session (another environment)");
        await store.WriteAsync("session-development", "development session (another environment)");
        await store.WriteAsync("session", JsonSerializer.Serialize(new { Version = 1, ClientId = MockIdentityServer.ClientId, RefreshToken = "r", Subject = server.Subject }));
        await account.InitializeAsync();
        Assert.True(account.IsSignedIn);
        await account.SignOutAsync();
        Assert.Equal(HcgAccountStatus.SignedOut, account.Status);
        Assert.Null(await store.ReadAsync("session"));
        Assert.Equal("TEST session (another environment)", await store.ReadAsync("session-local-test"));
        Assert.Equal("development session (another environment)", await store.ReadAsync("session-development"));
    }

    [Fact]
    public async Task Signing_in_as_another_account_replaces_the_previous_accounts_session()
    {
        var server = new MockIdentityServer();
        var store = new SessionOnlyCredentialStore();
        var config = WithFreePorts(MockIdentityServer.Config);
        var account = AccountWith(config, server, store);
        await account.SignInAsync(ApprovingBrowser(server));
        var first = account.Identity!.Subject;
        server.Subject = "22222222-0000-4000-8000-0000000000b2";
        await account.SignOutAsync();
        Assert.Null(await account.GetAccessTokenAsync());
        await account.SignInAsync(ApprovingBrowser(server));
        Assert.True(account.IsSignedIn);
        Assert.NotEqual(first, account.Identity!.Subject);
        Assert.Equal(server.Subject, account.Identity.Subject);
        var stored = await store.ReadAsync("session");
        Assert.Contains(server.Subject, stored);
        Assert.DoesNotContain(first, stored);
    }
}

public sealed class StoreSessionPolicyTests
{
    private static readonly HcgConnectConfiguration Production = HcgConnectConfiguration.Production;
    private static readonly HcgConnectConfiguration Test = HcgConnectConfiguration.Production with { Issuer = new Uri("http://127.0.0.1:5123/auth/v1") };

    [Theory]
    [InlineData("https://store.hakmicapitalgroup.com/", false, true)]
    [InlineData("http://127.0.0.1:41234/", false, false)]
    [InlineData("http://localhost:41234/", false, false)]
    [InlineData("http://127.0.0.1:41234/", true, true)]
    [InlineData("https://store.hakmicapitalgroup.com/", true, false)]
    public void A_session_goes_only_to_a_Store_of_the_same_kind(string origin, bool testSession, bool allowed)
    {
        var refusal = StoreSessionPolicy.Refusal(testSession ? Test : Production, new Uri(origin));
        Assert.Equal(allowed, refusal is null);
    }

    [Fact]
    public async Task Diagnostics_never_send_a_production_sign_in_to_a_TEST_Store()
    {
        var seen = new List<string?>();
        var handler = new Lambda(r =>
        {
            seen.Add(r.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"status":"ok","mode":"test","publishing":"enabled"}""", System.Text.Encoding.UTF8, "application/json") };
        });
        var client = new HcgStoreClient(new HttpClient(handler), new Uri("http://127.0.0.1:41234/"), _ => Task.FromResult<string?>("production-token"));
        var r = await StoreConnectionDiagnostics.RunAsync(client, new StoreDiagnosticsContext(true, "dev@example.test", "Signed in.", Production), _ => Task.FromResult<string?>("production-token"));
        Assert.All(seen, b => Assert.Null(b));
        Assert.Equal(StoreCheckState.Failed, r["auth"].State);
        Assert.Contains("never sent", r["auth"].Summary);
        Assert.False(r.CanUpload);
    }

    private sealed class Lambda(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }
}
