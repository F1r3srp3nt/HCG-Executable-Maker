using System.Net;
using System.Text.Json;
using HCG.ExecutableMaker.Account;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// MOCK of HCG's central Ecosystem API (/api/public/ecosystem/v1). Routes and the error envelope are as observed live on
/// 9 Nov 2026; response bodies follow docs/hcg-connect/HANDOFF.md §6 (pending HCG's confirmation).
/// </summary>
public sealed class MockEcosystem : HttpMessageHandler
{
    public const string Sub = "9b2d7c1e-0000-4000-8000-00000000c0de";
    public string Me { get; set; } = JsonSerializer.Serialize(new
    {
        account = new { id = Sub, email = "founder@example.test", display_name = "Test Founder" },
        organisations = new[] { new { org_id = "org-hcg", name = "Hakmi Capital Group", role = "owner" } },
    });
    public Func<string?, string> Features { get; set; } = _ => Granted("Pro", "active", null);
    public string Check { get; set; } = """{"allowed":false,"blocking_layer":"licence","message":"No active licence."}""";
    public HttpStatusCode? FailWith { get; set; }
    public string? FailBody { get; set; }
    public List<(HttpMethod Method, string PathAndQuery, string? Body, string? Auth, bool ContractHeader)> Calls { get; } = [];

    public static string Granted(string plan, string licenceStatus, string? endsAt, object? links = null) => JsonSerializer.Serialize(new
    {
        product_id = "hcg.executable.maker",
        features = new object[]
        {
            new { feature_key = "support.submit", display_name = "Support requests", allowed = true },
            new { feature_key = "support.attach", display_name = "Attach project files", allowed = true },
            new { feature_key = "ai.use", display_name = "AI assistance", allowed = false },
        },
        plan = new { plan_key = plan.ToLowerInvariant(), name = plan, billing_type = "consumer_subscription" },
        licence = new { status = licenceStatus, license_type = "subscription", starts_at = "2026-11-01T00:00:00Z", ends_at = endsAt },
        links = links ?? new { manage = "https://beta.hakmicapitalgroup.com/client-portal/plans", checkout = "https://evil.example/pay", account = "https://beta.hakmicapitalgroup.com/client-portal" },
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.Parameter, request.Headers.Contains(HcgApiClient.ContractHeader)));
        if (FailWith is { } status) return MockIdentityServer.Json(status, FailBody ?? """{"error":{"code":"unavailable","message":"Down."},"api_version":"v1"}""");
        var path = request.RequestUri.AbsolutePath;
        if (!path.StartsWith("/api/public/ecosystem/v1/", StringComparison.Ordinal)) return MockIdentityServer.Json(HttpStatusCode.NotFound, "{}");
        if (request.Headers.Authorization?.Parameter is not { Length: > 0 })
            return MockIdentityServer.Json(HttpStatusCode.Unauthorized, """{"error":{"code":"unauthorized","message":"Missing, invalid or expired access token."},"api_version":"v1"}""");
        var q = LoopbackCallbackListener.ParseQuery(request.RequestUri.Query.TrimStart('?'));
        return path[("/api/public/ecosystem/v1/".Length)..] switch
        {
            "me" => MockIdentityServer.Json(HttpStatusCode.OK, Me),
            "features" => MockIdentityServer.Json(HttpStatusCode.OK, Features(q.GetValueOrDefault("org"))),
            "check" => MockIdentityServer.Json(HttpStatusCode.OK, Check),
            _ => MockIdentityServer.Json(HttpStatusCode.NotFound, """{"error":{"code":"not_found","message":"No such route."},"api_version":"v1"}"""),
        };
    }
}

public class HcgEcosystemTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (AccountPlanService Plan, MockEcosystem Api, HcgAccountService Account, Clock Clock) Setup(MockEcosystem? api = null)
    {
        api ??= new MockEcosystem();
        var clock = new Clock();
        var config = MockIdentityServer.Config;
        var account = new HcgAccountService(config, new HcgAuthClient(new HttpClient(new MockIdentityServer()), config, TimeProvider.System),
            new SessionOnlyCredentialStore(), TimeProvider.System);
        account.AdoptForTesting(new HcgSession
        {
            AccessToken = "at-1", RefreshToken = "rt-1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Identity = new HcgIdentity(MockEcosystem.Sub, "founder@example.test", "Test Founder"), ClientId = MockIdentityServer.ClientId,
        });
        var client = new HcgEcosystemClient(new HcgApiClient(new HttpClient(api), account));
        return (new AccountPlanService(client, clock), api, account, clock);
    }

    [Fact]
    public async Task Plan_licence_features_and_organisation_come_from_hcgs_central_api()
    {
        var (plan, api, _, _) = Setup();
        await plan.RefreshAsync();
        Assert.Equal(PlanAccessState.Granted, plan.State);
        Assert.Equal("Pro", plan.PlanDisplay);
        Assert.Equal("org-hcg", plan.OrganisationId);
        Assert.True(plan.Allows("support.submit"));
        Assert.False(plan.Allows("ai.use"));
        Assert.Null(plan.ValidUntil);

        Assert.Equal("/api/public/ecosystem/v1/me", api.Calls[0].PathAndQuery);
        Assert.Equal("/api/public/ecosystem/v1/features?org=org-hcg&product=hcg.executable.maker", api.Calls[1].PathAndQuery);
        Assert.All(api.Calls, c => Assert.Equal("at-1", c.Auth));
        Assert.All(api.Calls, c => Assert.False(c.ContractHeader)); // central API: no Executable Maker headers
    }

    [Fact]
    public async Task Only_https_links_on_hcg_hosts_are_kept_and_the_account_link_falls_back_to_the_account_page()
    {
        var (plan, _, _, _) = Setup();
        await plan.RefreshAsync();
        Assert.Equal("https://beta.hakmicapitalgroup.com/client-portal/plans", plan.Access!.Links.Manage!.ToString());
        Assert.Null(plan.Access.Links.Checkout); // https://evil.example is never offered
        var (bare, api2, _, _) = Setup(new MockEcosystem { Features = _ => """{"features":[{"feature_key":"support.submit"}]}""" });
        await bare.RefreshAsync();
        Assert.Equal("Not reported by HCG", bare.PlanDisplay);
        Assert.Equal(MockIdentityServer.Config.AccountPortalUrl, bare.AccountLink);
        Assert.Equal(PlanAccessState.Granted, bare.State); // a listed feature without "allowed" counts as allowed
    }

    [Theory]
    [InlineData("expired", null, PlanAccessState.Expired)]
    [InlineData("active", "2026-11-01T00:00:00Z", PlanAccessState.Expired)] // past its end date
    [InlineData("suspended", null, PlanAccessState.Suspended)]
    [InlineData("revoked", null, PlanAccessState.Suspended)]
    [InlineData("active", "2027-01-01T00:00:00Z", PlanAccessState.Granted)]
    public async Task Licence_status_and_end_date_decide_the_state_hcg_reports(string status, string? endsAt, PlanAccessState expected)
    {
        var (plan, _, _, _) = Setup(new MockEcosystem { Features = _ => MockEcosystem.Granted("Pro", status, endsAt) });
        await plan.RefreshAsync();
        Assert.Equal(expected, plan.State);
    }

    [Fact]
    public async Task No_allowed_feature_and_no_licence_is_not_granted()
    {
        var (plan, _, _, _) = Setup(new MockEcosystem { Features = _ => """{"features":[{"feature_key":"support.submit","allowed":false}]}""" });
        await plan.RefreshAsync();
        Assert.Equal(PlanAccessState.NotGranted, plan.State);
        Assert.False(plan.Allows("support.submit"));
    }

    [Fact]
    public async Task With_several_organisations_the_user_chooses_and_the_choice_is_sent_to_hcg()
    {
        var api = new MockEcosystem
        {
            Me = """{"account":{"id":"9b2d7c1e-0000-4000-8000-00000000c0de"},"memberships":[{"org_id":"a","name":"A Ltd","role":"member"},{"organisation":{"id":"b","name":"B AS"},"role":"admin"}]}""",
            Features = org => org == "b" ? MockEcosystem.Granted("Employee", "active", null) : """{"features":[]}""",
        };
        var (plan, _, account, _) = Setup(api);
        await plan.RefreshAsync();
        Assert.True(plan.NeedsOrganisationChoice);
        Assert.Equal(PlanAccessState.NotGranted, plan.State);
        Assert.DoesNotContain(api.Calls, c => c.PathAndQuery.Contains("features"));
        Assert.Equal(["A Ltd", "B AS"], plan.Organisations.Select(o => o.Name));

        await plan.SelectOrganisationAsync("b");
        Assert.Equal("b", account.OrganisationId);
        Assert.Equal(PlanAccessState.Granted, plan.State);
        Assert.Contains(api.Calls, c => c.PathAndQuery == "/api/public/ecosystem/v1/features?org=b&product=hcg.executable.maker");
        await plan.SelectOrganisationAsync("not-a-member"); // ignored
        Assert.Equal("b", account.OrganisationId);
    }

    [Fact]
    public async Task Information_for_another_account_is_ignored()
    {
        var (plan, _, _, _) = Setup(new MockEcosystem { Me = """{"account":{"id":"someone-else"},"organisations":[]}""" });
        await plan.RefreshAsync();
        Assert.Equal(PlanAccessState.Unavailable, plan.State);
        Assert.Null(plan.Access);
    }

    [Fact]
    public async Task An_outage_is_unknown_not_denied_and_keeps_the_last_answer()
    {
        var api = new MockEcosystem();
        var (plan, _, account, _) = Setup(api);
        await plan.RefreshAsync();
        api.FailWith = HttpStatusCode.ServiceUnavailable;
        await plan.RefreshAsync();
        Assert.Equal(PlanAccessState.Unavailable, plan.State);
        Assert.Equal("Pro", plan.PlanDisplay);
        Assert.True(account.IsSignedIn);
        api.FailWith = HttpStatusCode.NotFound; // route missing (HTML 404) on this HCG site
        api.FailBody = "<!DOCTYPE html>";
        await plan.RefreshAsync();
        Assert.Equal(PlanAccessState.Unavailable, plan.State);
        Assert.Contains("not available on this HCG site", plan.Message);
    }

    [Fact]
    public async Task A_401_in_the_central_envelope_refreshes_once_then_ends_the_session()
    {
        var api = new MockEcosystem { FailWith = HttpStatusCode.Unauthorized, FailBody = """{"error":{"code":"unauthorized","message":"Missing, invalid or expired access token."},"api_version":"v1"}""" };
        var (plan, _, account, _) = Setup(api);
        await plan.RefreshAsync();
        Assert.False(account.IsSignedIn);
        Assert.Equal(PlanAccessState.SignedOut, plan.State);
    }

    [Fact]
    public async Task A_403_in_the_central_envelope_is_not_granted_and_shows_hcgs_message()
    {
        var (plan, _, _, _) = Setup(new MockEcosystem { FailWith = HttpStatusCode.Forbidden, FailBody = """{"error":{"code":"forbidden","message":"Not a member of that organisation."},"api_version":"v1"}""" });
        await plan.RefreshAsync();
        Assert.Equal(PlanAccessState.NotGranted, plan.State);
        Assert.Equal("Not a member of that organisation.", plan.Message);
    }

    [Fact]
    public async Task Returning_from_checkout_proves_nothing_until_hcg_reports_the_new_plan()
    {
        var api = new MockEcosystem { Features = _ => MockEcosystem.Granted("Free", "active", null) };
        var (plan, _, _, _) = Setup(api);
        using var _ = plan;
        await plan.RefreshAsync();
        plan.BeginPlanChange();
        Assert.Equal(PlanChangeState.AwaitingConfirmation, plan.ChangeState);

        // The user comes back (e.g. the checkout page said "success"), but HCG still reports Free.
        await plan.OnReturnedAsync();
        Assert.Equal("Free", plan.PlanDisplay);
        Assert.Equal(PlanChangeState.AwaitingConfirmation, plan.ChangeState);

        // HCG now reports the new plan: only this confirms it.
        api.Features = _ => MockEcosystem.Granted("Pro", "active", "2027-11-09T00:00:00Z");
        await plan.OnReturnedAsync();
        Assert.Equal(PlanChangeState.Confirmed, plan.ChangeState);
        Assert.Equal("Pro", plan.PlanDisplay);
        Assert.Contains("HCG confirmed", plan.ChangeMessage);
    }

    [Fact]
    public async Task Without_hcg_confirmation_the_wait_ends_as_not_confirmed_and_nothing_is_assumed()
    {
        var api = new MockEcosystem { Features = _ => MockEcosystem.Granted("Free", "active", null) };
        var (plan, _, _, clock) = Setup(api);
        using var _ = plan;
        await plan.RefreshAsync();
        plan.BeginPlanChange();
        clock.Now += AccountPlanService.ConfirmationWindow + TimeSpan.FromSeconds(1);
        await plan.OnReturnedAsync();
        Assert.Equal(PlanChangeState.NotConfirmed, plan.ChangeState);
        Assert.Equal("Free", plan.PlanDisplay);
        await plan.OnReturnedAsync(); // no longer waiting: returning again does not even ask HCG
        Assert.Equal(PlanChangeState.NotConfirmed, plan.ChangeState);
    }

    [Fact]
    public async Task Check_asks_hcg_for_one_feature_and_explains_the_blocking_layer()
    {
        var api = new MockEcosystem();
        var (_, _, account, _) = Setup(api);
        var client = new HcgEcosystemClient(new HcgApiClient(new HttpClient(api), account));
        var r = await client.CheckAsync("org-hcg", "support.submit", CancellationToken.None);
        Assert.False(r.Allowed);
        Assert.Equal("licence", r.BlockingLayer);
        var call = api.Calls.Single();
        Assert.Equal(HttpMethod.Post, call.Method);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("hcg.executable.maker", body.RootElement.GetProperty("product_id").GetString());
        Assert.Equal("org-hcg", body.RootElement.GetProperty("org_id").GetString());
        Assert.Equal("support.submit", body.RootElement.GetProperty("feature_key").GetString());
    }

    [Fact]
    public async Task Signing_out_clears_plan_and_access()
    {
        var (plan, _, account, _) = Setup();
        await plan.RefreshAsync();
        await account.SignOutAsync();
        Assert.Equal(PlanAccessState.SignedOut, plan.State);
        Assert.Null(plan.Access);
        Assert.Equal("Not reported by HCG", plan.PlanDisplay);
    }
}
