using System.Net;
using System.Text.Json;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Support;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// MOCK of HCG's central Ecosystem API (/api/public/ecosystem/v1) in the shapes HCG's main site documents and deploys
/// (main site docs/executable-maker/desktop-handoff.md, 13 Nov 2026; HCG Store MAIN-SITE-INTEGRATION.md, 16 Nov 2026):
/// <c>GET /me</c> → user + organisations[].org_role; <c>GET /features</c> → features map, plan, licence(s), links,
/// revalidate_after_seconds, 403 not_org_member for a non-member; <c>POST /check</c> → allowed/layer/reason/verified_at.
/// Never HCG itself.
/// </summary>
public sealed class MockEcosystem : HttpMessageHandler
{
    public const string Sub = "9b2d7c1e-0000-4000-8000-00000000c0de";
    public const string OrgA = "0d4b6a52-0000-4000-8000-0000000000a1";
    public const string OrgB = "0d4b6a52-0000-4000-8000-0000000000b2";
    public string Subject { get; set; } = Sub;
    public List<(string Id, string Name, string Role)> Organisations { get; } = [(OrgA, "Hakmi Capital Group", "admin")];
    /// <summary>Per organisation: the enabled features (true = allowed).</summary>
    public Dictionary<string, Dictionary<string, bool>> Features { get; } = new() { [OrgA] = new() { ["support.submit"] = true, ["support.attach"] = true } };
    /// <summary>Per organisation: the licence records HCG holds.</summary>
    public Dictionary<string, List<object>> Licences { get; } = new() { [OrgA] = [Licence("product", null, "active", null)] };
    /// <summary>Organisations the caller is not (or no longer) a member of: /features answers 403 not_org_member.</summary>
    public HashSet<string> NotMemberOf { get; } = [];
    public int RevalidateSeconds { get; set; } = 300;
    /// <summary>The /check decision for (org, feature): null = derive from <see cref="Features"/> (not_licensed when off).</summary>
    public Func<string, string, (bool Allowed, string? Layer, string? Reason)?> Check { get; set; } = (_, _) => null;
    /// <summary>Replaces a whole response body for one route (to test unrecognised answers).</summary>
    public Dictionary<string, string> Override { get; } = new(StringComparer.Ordinal);
    public HttpStatusCode? FailWith { get; set; }
    public bool NetworkDown { get; set; }
    public List<(HttpMethod Method, string PathAndQuery, string? Body, string? Auth, bool ContractHeader)> Calls { get; } = [];

    public static object Licence(string scope, string? scopeKey, string status, string? endsAt) =>
        new { scope, scope_key = scopeKey, status, license_type = "subscription", starts_at = "2026-11-01T00:00:00Z", ends_at = endsAt };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.Parameter, request.Headers.Contains(HcgApiClient.ContractHeader)));
        if (NetworkDown) throw new HttpRequestException("network is unreachable");
        if (FailWith is { } status) return MockIdentityServer.Json(status, """{"error":{"code":"evaluation_failed","message":"Down."},"api_version":"v1"}""");
        var path = request.RequestUri.AbsolutePath;
        if (!path.StartsWith("/api/public/ecosystem/v1/", StringComparison.Ordinal)) return MockIdentityServer.Json(HttpStatusCode.NotFound, "{}");
        if (request.Headers.Authorization?.Parameter is not { Length: > 0 })
            return MockIdentityServer.Json(HttpStatusCode.Unauthorized, """{"error":{"code":"unauthorized","message":"Missing, invalid or expired access token."},"api_version":"v1"}""");
        var route = path["/api/public/ecosystem/v1/".Length..];
        if (Override.TryGetValue(route, out var replaced)) return MockIdentityServer.Json(HttpStatusCode.OK, replaced);
        var q = LoopbackCallbackListener.ParseQuery(request.RequestUri.Query.TrimStart('?'));
        switch (route)
        {
            case "me":
                return MockIdentityServer.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    api_version = "v1",
                    user = new { id = Subject, email = "founder@example.test" },
                    organisations = Organisations.Select(o => new { org_id = o.Id, slug = o.Name.ToLowerInvariant().Replace(' ', '-'), name = o.Name, kind = "internal", org_role = o.Role }),
                }));
            case "features":
            {
                var org = q.GetValueOrDefault("org") ?? "";
                if (NotMemberOf.Contains(org) || Organisations.All(o => o.Id != org))
                    return MockIdentityServer.Json(HttpStatusCode.Forbidden, """{"error":{"code":"not_org_member","message":"Not a member of this organisation."},"api_version":"v1"}""");
                var licences = Licences.GetValueOrDefault(org) ?? [];
                return MockIdentityServer.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    api_version = "v1", org_id = org, product_id = q.GetValueOrDefault("product"),
                    features = (Features.GetValueOrDefault(org) ?? []).Where(f => f.Value).ToDictionary(f => f.Key, f => f.Value),
                    plan = (object?)null,
                    // HCG names a single licence only when exactly one product-wide licence exists.
                    licence = licences.Where(l => JsonSerializer.Serialize(l).Contains("\"scope\":\"product\"")).ToList() is [var only] ? only : null,
                    licences,
                    links = new { manage = (string?)null, checkout = (string?)null, account = "https://beta.hakmicapitalgroup.com/client-portal" },
                    revalidate_after_seconds = RevalidateSeconds,
                }));
            }
            case "check":
            {
                using var doc = JsonDocument.Parse(body!);
                var org = doc.RootElement.GetProperty("org_id").GetString()!;
                var product = doc.RootElement.GetProperty("product_id").GetString()!;
                var key = doc.RootElement.GetProperty("feature_key").GetString()!;
                var d = Check(org, key) ?? (NotMemberOf.Contains(org) ? (false, "identity", "not_org_member")
                    : Features.GetValueOrDefault(org)?.GetValueOrDefault(key) == true ? (true, null, null) : (false, "licence", "not_licensed"));
                return MockIdentityServer.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    api_version = "v1", org_id = org, product_id = product, feature_key = key, allowed = d.Allowed, layer = d.Layer, reason = d.Reason,
                    verified_at = "2026-11-16T12:00:00Z",
                }));
            }
            default:
                return MockIdentityServer.Json(HttpStatusCode.NotFound, "<!doctype html><title>Not found</title>");
        }
    }
}

public class HcgEcosystemTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStore(FeatureDecision? decision = null) : IStorePublishingAuthority
    {
        public List<Uri?> Origins { get; } = [];
        public Task<FeatureDecision> CheckAsync(MakerFeature feature, Uri? storeOrigin, CancellationToken ct)
        {
            Origins.Add(storeOrigin);
            return Task.FromResult(decision ?? new FeatureDecision(feature, FeatureAccessState.NotPermitted, "HCG has not given this account HCG Store's publishing permission.",
                FeatureRemedy.AskAdministrator, Source: "HCG Store"));
        }
    }

    internal static (FeatureAccessService Features, AccountPlanService Plan, MockEcosystem Api, HcgAccountService Account, Clock Clock) Setup(MockEcosystem? api = null, bool signIn = true)
    {
        api ??= new MockEcosystem();
        var clock = new Clock();
        var config = MockIdentityServer.Config;
        var account = new HcgAccountService(config, new HcgAuthClient(new HttpClient(new MockIdentityServer()), config, TimeProvider.System),
            new SessionOnlyCredentialStore(), TimeProvider.System);
        var client = new HcgEcosystemClient(new HcgApiClient(new HttpClient(api), account));
        var features = new FeatureAccessService(client, clock) { StoreAuthority = new FakeStore() };
        if (signIn) SignIn(account);
        return (features, new AccountPlanService(features, clock), api, account, clock);
    }

    internal static void SignIn(HcgAccountService account, string subject = MockEcosystem.Sub) => account.AdoptForTesting(new HcgSession
    {
        AccessToken = "at-" + subject[..4], RefreshToken = "rt-1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Identity = new HcgIdentity(subject, "founder@example.test", null), ClientId = MockIdentityServer.ClientId,
    });

    private static async Task Ready(FeatureAccessService f)
    {
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessStatus.Ready, f.Status);
    }

    // ───────────── The confirmed contract ─────────────

    [Fact]
    public async Task Reads_hcgs_confirmed_me_features_and_check_shapes_and_asks_about_the_chosen_organisation()
    {
        var (f, plan, api, _, _) = Setup();
        await Ready(f);
        Assert.Equal(MockEcosystem.OrgA, f.OrganisationId);
        Assert.Equal("admin", f.Organisations.Single().Role);
        Assert.Equal("founder@example.test", f.Account!.Email);
        Assert.True(f.Decide(MakerFeatures.SupportSubmit).IsAllowed);
        Assert.Equal(FeatureAccessState.NotPermitted, f.Decide(MakerFeatures.AiUse).State);
        Assert.Equal("/api/public/ecosystem/v1/me", api.Calls[0].PathAndQuery);
        Assert.Equal($"/api/public/ecosystem/v1/features?org={MockEcosystem.OrgA}&product=hcg.executable.maker", api.Calls[1].PathAndQuery);
        // /check only for the account feature HCG does not enable, with exactly the three confirmed fields.
        Assert.All(api.Calls.Where(c => c.Method == HttpMethod.Post), check =>
            Assert.Equal($$"""{"org_id":"{{MockEcosystem.OrgA}}","product_id":"hcg.executable.maker","feature_key":"ai.use"}""", check.Body));
        Assert.All(api.Calls, c => Assert.False(c.ContractHeader));
        Assert.Equal(PlanAccessState.Granted, plan.State);
        Assert.Equal("Not reported by HCG", plan.PlanDisplay); // plan null: never inferred free
        Assert.Equal(TimeSpan.FromMinutes(5), f.ValidUntil - f.CheckedAt);
    }

    [Theory]
    [InlineData("me", """{"account":{"id":"x"},"organisations":[{"org_id":"o","role":"owner"}]}""")]           // the old mock shape
    [InlineData("me", """{"api_version":"v2","user":{"id":"x"},"organisations":[]}""")]
    [InlineData("features", """{"api_version":"v1","org_id":"another","product_id":"hcg.executable.maker","features":{}}""")]
    [InlineData("features", """{"api_version":"v1","features":[{"feature_key":"support.submit","allowed":true}]}""")] // an array, not the map
    public async Task An_answer_in_another_shape_or_about_another_organisation_is_unknown_never_a_grant_or_denial(string route, string body)
    {
        var api = new MockEcosystem();
        api.Override[route] = body;
        var (f, _, _, _, _) = Setup(api);
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessStatus.Unavailable, f.Status);
        var d = f.Decide(MakerFeatures.SupportSubmit);
        Assert.Equal(FeatureAccessState.Unavailable, d.State);
        Assert.Contains("not a refusal", d.Explanation);
    }

    [Fact]
    public async Task A_check_answer_about_another_feature_or_organisation_is_not_used()
    {
        var api = new MockEcosystem();
        api.Override["check"] = $$"""{"api_version":"v1","org_id":"{{MockEcosystem.OrgB}}","product_id":"hcg.executable.maker","feature_key":"support.submit","allowed":true,"layer":null,"reason":null,"verified_at":"2026-11-16T12:00:00Z"}""";
        api.Features[MockEcosystem.OrgA].Clear();
        var (f, _, _, _, _) = Setup(api);
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessStatus.Unavailable, f.Status);
        var ex = await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => f.RequireAsync(MakerFeatures.SupportSubmit));
        Assert.True(ex.Decision.IsUnavailable);
    }

    // ───────────── Allowed and denied, with the right explanation ─────────────

    [Theory]
    [InlineData("licence", "not_licensed", FeatureAccessState.NotPermitted, FeatureRemedy.ViewInStore)]
    [InlineData("availability", "feature_unavailable", FeatureAccessState.NotAvailableYet, FeatureRemedy.WaitForHcg)]
    [InlineData("availability", "product_unavailable", FeatureAccessState.NotAvailableYet, FeatureRemedy.WaitForHcg)]
    [InlineData("identity", "user_not_permitted", FeatureAccessState.NotPermitted, FeatureRemedy.AskAdministrator)]
    [InlineData("identity", "org_inactive", FeatureAccessState.NotPermitted, FeatureRemedy.AskAdministrator)]
    [InlineData("identity", "not_org_member", FeatureAccessState.NotPermitted, FeatureRemedy.ChooseOrganisation)]
    [InlineData("licence", "something_new", FeatureAccessState.NotPermitted, FeatureRemedy.None)]
    public async Task Each_reason_hcg_gives_has_its_own_explanation_and_only_a_missing_licence_points_to_hcg_store(string layer, string reason, FeatureAccessState state, FeatureRemedy remedy)
    {
        var api = new MockEcosystem { Check = (_, key) => key == "ai.use" ? (false, layer, reason) : null };
        var (f, _, _, _, _) = Setup(api);
        await Ready(f);
        var d = f.Decide(MakerFeatures.AiUse);
        Assert.Equal((state, remedy, reason, layer), (d.State, d.Remedy, d.Reason, d.Layer));
        Assert.Equal(remedy == FeatureRemedy.ViewInStore ? FeatureAccessService.DefaultStoreProductPage : null, d.Link);
        if (reason == "something_new") Assert.Contains("something_new", d.Explanation); // shown as HCG sent it, not mapped to a guess
        var fresh = await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => f.RequireAsync(MakerFeatures.AiUse));
        Assert.False(fresh.Decision.IsUnavailable);
    }

    [Fact]
    public async Task Staff_only_publishing_points_to_the_administrator_never_to_a_purchase_and_is_asked_again_every_time()
    {
        var (f, _, _, _, _) = Setup();
        await Ready(f);
        Assert.Equal(FeatureAccessState.NotYetChecked, f.Decide(MakerFeatures.StorePublish).State);
        var store = (FakeStore)f.StoreAuthority!;
        var origin = new Uri("https://store.example/");
        var d = await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => f.RequireAsync(MakerFeatures.StorePublish, storeOrigin: origin));
        Assert.Equal((FeatureAccessState.NotPermitted, FeatureRemedy.AskAdministrator, (Uri?)null), (d.Decision.State, d.Decision.Remedy, d.Decision.Link));
        Assert.Contains("not something you can buy", d.Decision.RemedyText);
        await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => f.RequireAsync(MakerFeatures.StorePublish, storeOrigin: origin));
        Assert.Equal([origin, origin], store.Origins); // asked again, from the Store the operation uses
        Assert.Equal(FeatureAccessState.NotPermitted, f.Decide(MakerFeatures.StorePublish).State); // and shown in the UI
    }

    [Fact]
    public async Task Local_builds_are_free_signed_out_offline_and_unconfigured()
    {
        var api = new MockEcosystem { NetworkDown = true };
        var (f, _, _, account, _) = Setup(api, signIn: false);
        Assert.Equal(FeatureAccessState.Free, f.Decide(MakerFeatures.LocalBuilds).State);
        Assert.Equal(FeatureAccessState.SignInRequired, f.Decide(MakerFeatures.SupportSubmit).State);
        SignIn(account);
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessState.Free, f.Decide(MakerFeatures.LocalBuilds).State);
        Assert.True((await f.RequireAsync(MakerFeatures.LocalBuilds)).IsAllowed); // no network needed

        var unconfigured = new HcgAccountService(HcgConnectConfiguration.Development, new HcgAuthClient(new HttpClient(new MockIdentityServer()), HcgConnectConfiguration.Development, TimeProvider.System),
            new SessionOnlyCredentialStore(), TimeProvider.System);
        var g = new FeatureAccessService(new HcgEcosystemClient(new HcgApiClient(new HttpClient(api), unconfigured)), TimeProvider.System);
        Assert.Equal(FeatureAccessState.NotConfigured, g.Decide(MakerFeatures.AiUse).State);
        Assert.Equal(FeatureAccessState.Free, g.Decide(MakerFeatures.LocalBuilds).State);
    }

    // ───────────── Offline: not permitted ≠ HCG unavailable ─────────────

    [Theory]
    [InlineData(null)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task An_outage_is_hcg_unavailable_never_not_permitted_and_allows_nothing(HttpStatusCode? status)
    {
        var (f, plan, api, _, _) = Setup();
        await Ready(f);
        Assert.True(f.Decide(MakerFeatures.SupportSubmit).IsAllowed);
        if (status is null) api.NetworkDown = true; else api.FailWith = status;
        await f.RefreshAsync();
        var d = f.Decide(MakerFeatures.SupportSubmit);
        Assert.Equal((FeatureAccessState.Unavailable, FeatureRemedy.TryAgain), (d.State, d.Remedy));
        Assert.Equal(PlanAccessState.Unavailable, plan.State);
        var ex = await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => f.RequireAsync(MakerFeatures.SupportSubmit));
        Assert.True(ex.Decision.IsUnavailable);
        Assert.Equal(FeatureAccessState.Free, f.Decide(MakerFeatures.LocalBuilds).State);
        // Back online: decided again.
        api.NetworkDown = false;
        api.FailWith = null;
        await f.RefreshIfExpiredAsync();
        Assert.True(f.Decide(MakerFeatures.SupportSubmit).IsAllowed);
    }

    [Fact]
    public async Task A_403_that_is_not_hcgs_not_org_member_answer_is_unknown_never_a_denial()
    {
        // A proxy or firewall 403, or another 403 code: not evidence that the person left the organisation.
        var (f, _, api, _, _) = Setup();
        await Ready(f);
        api.FailWith = HttpStatusCode.Forbidden; // body code "evaluation_failed"
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessStatus.Unavailable, f.Status);
        Assert.Equal(FeatureAccessState.Unavailable, f.Decide(MakerFeatures.SupportSubmit).State);
        api.FailWith = null;
        await Ready(f);
        api.FailWith = HttpStatusCode.Forbidden;
        var d = await f.CheckNowAsync(MakerFeatures.SupportSubmit);
        Assert.Equal((FeatureAccessState.Unavailable, (string?)null), (d.State, d.Reason));
        Assert.Equal(9, FeatureExitCodes.For(d));
    }

    [Fact]
    public async Task An_identity_service_outage_while_renewing_is_hcg_unavailable_not_sign_in()
    {
        var server = new MockIdentityServer();
        var store = new SessionOnlyCredentialStore();
        var config = MockIdentityServer.Config;
        var account = new HcgAccountService(config, new HcgAuthClient(new HttpClient(server), config, TimeProvider.System), store, TimeProvider.System);
        var f = new FeatureAccessService(new HcgEcosystemClient(new HcgApiClient(new HttpClient(new MockEcosystem()), account)), new Clock());
        await store.WriteAsync(config.SessionStoreAccount, JsonSerializer.Serialize(new { Version = 1, ClientId = MockIdentityServer.ClientId, RefreshToken = "keep", Subject = server.Subject, Email = "a@b.c" }));
        server.Override = _ => MockIdentityServer.Json(HttpStatusCode.BadGateway, "{}");
        await account.InitializeAsync();
        Assert.Equal(HcgAccountStatus.Unavailable, account.Status);
        var shown = f.Decide(MakerFeatures.SupportSubmit);
        Assert.Equal((FeatureAccessState.Unavailable, FeatureRemedy.TryAgain), (shown.State, shown.Remedy));
        var asked = await f.CheckNowAsync(MakerFeatures.SupportSubmit);
        Assert.Equal(FeatureAccessState.Unavailable, asked.State);
        Assert.Equal(9, FeatureExitCodes.For(asked)); // not 3: nobody signed out
        Assert.Equal(FeatureAccessState.Free, f.Decide(MakerFeatures.LocalBuilds).State);
    }

    [Fact]
    public async Task An_answer_that_arrives_after_another_account_signed_in_is_not_used_or_shown()
    {
        var api = new MockEcosystem();
        api.Features[MockEcosystem.OrgA].Clear(); // nothing enabled for the first account
        var (f, _, _, account, _) = Setup(api);
        await Ready(f);
        const string other = "7a1e0000-0000-4000-8000-0000000000ee";
        // While HCG answers "allowed" about the first account, a different account signs in on this computer.
        api.Check = (_, key) =>
        {
            if (key == "ai.use" && account.Identity?.Subject == MockEcosystem.Sub) { api.Subject = other; SignIn(account, other); }
            return key == "ai.use" ? (true, null, null) : null;
        };
        var d = await f.CheckNowAsync(MakerFeatures.AiUse);
        Assert.Equal(FeatureAccessState.Unavailable, d.State);
        Assert.Contains("changed while HCG was checking", d.Explanation);
        api.Check = (_, _) => null;
        await WaitFor(() => f.Status == FeatureAccessStatus.Ready && f.Account?.Id == other);
        Assert.False(f.Decide(MakerFeatures.AiUse).IsAllowed); // the first account's "allowed" never reached the second
    }

    [Fact]
    public void Choosing_an_organisation_is_its_own_exit_code_not_a_refusal()
    {
        var choose = new FeatureDecision(MakerFeatures.SupportSubmit, FeatureAccessState.ChooseOrganisation, "Choose.", FeatureRemedy.ChooseOrganisation);
        Assert.Equal(10, FeatureExitCodes.For(choose));
        Assert.Equal(8, FeatureExitCodes.For(choose with { State = FeatureAccessState.NotPermitted }));
    }

    // ───────────── Organisations ─────────────

    [Fact]
    public async Task With_several_organisations_the_user_chooses_and_access_is_decided_per_organisation()
    {
        var api = new MockEcosystem();
        api.Organisations.Add((MockEcosystem.OrgB, "Client Co", "member"));
        api.Features[MockEcosystem.OrgB] = new() { ["ai.use"] = true };
        var (f, plan, _, account, _) = Setup(api);
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessStatus.ChooseOrganisation, f.Status);
        Assert.Equal(FeatureAccessState.ChooseOrganisation, f.Decide(MakerFeatures.SupportSubmit).State);
        Assert.Equal(PlanAccessState.NotGranted, plan.State);

        await f.SelectOrganisationAsync(MockEcosystem.OrgB);
        Assert.Equal(MockEcosystem.OrgB, account.OrganisationId);
        Assert.True(f.Decide(MakerFeatures.AiUse).IsAllowed);
        Assert.False(f.Decide(MakerFeatures.SupportSubmit).IsAllowed); // OrgA's grant does not leak into OrgB

        await f.SelectOrganisationAsync(MockEcosystem.OrgA);
        Assert.True(f.Decide(MakerFeatures.SupportSubmit).IsAllowed);
        Assert.False(f.Decide(MakerFeatures.AiUse).IsAllowed);
        await f.SelectOrganisationAsync("not-a-membership"); // ignored
        Assert.Equal(MockEcosystem.OrgA, f.OrganisationId);
    }

    [Fact]
    public async Task Removed_from_the_organisation_the_feature_is_not_permitted_with_the_reason()
    {
        var api = new MockEcosystem();
        api.Organisations.Add((MockEcosystem.OrgB, "Client Co", "member"));
        var (f, _, _, account, _) = Setup(api);
        await account.SetOrganisationAsync(MockEcosystem.OrgB);
        api.NotMemberOf.Add(MockEcosystem.OrgB); // HCG removed the membership after /me
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessStatus.NotMember, f.Status);
        var d = f.Decide(MakerFeatures.SupportSubmit);
        Assert.Equal((FeatureAccessState.NotPermitted, "not_org_member"), (d.State, d.Reason));
        Assert.Null(d.Link);
    }

    [Fact]
    public async Task No_organisation_means_no_licence_and_points_to_the_administrator()
    {
        var api = new MockEcosystem();
        api.Organisations.Clear();
        var (f, _, _, _, _) = Setup(api);
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessStatus.NoOrganisation, f.Status);
        var d = f.Decide(MakerFeatures.SupportSubmit);
        Assert.Equal((FeatureAccessState.NotPermitted, FeatureRemedy.AskAdministrator), (d.State, d.Remedy));
    }

    [Fact]
    public async Task Information_for_another_account_is_ignored()
    {
        var api = new MockEcosystem { Subject = "someone-else" };
        var (f, _, _, _, _) = Setup(api);
        await f.RefreshAsync();
        Assert.Equal(FeatureAccessStatus.Unavailable, f.Status);
        Assert.Null(f.Access);
    }

    // ───────────── Editions ─────────────

    [Fact]
    public async Task Only_product_wide_desktop_or_all_edition_licences_apply_to_the_desktop_app()
    {
        var api = new MockEcosystem();
        api.Licences[MockEcosystem.OrgA] = [MockEcosystem.Licence("module", "edition:ios", "active", null), MockEcosystem.Licence("module", "edition:desktop", "expired", "2026-11-10T00:00:00Z")];
        var (f, plan, _, _, _) = Setup(api);
        await Ready(f);
        Assert.Equal("expired", plan.Licence!.Status); // the desktop one, never the iOS edition's
        Assert.Equal(1, plan.OtherEditionLicences);
        Assert.Equal(PlanAccessState.Expired, plan.State);

        api.Licences[MockEcosystem.OrgA] = [MockEcosystem.Licence("module", "edition:windows-desktop", "active", null)];
        await f.RefreshAsync();
        Assert.Null(plan.Licence); // another product's edition key, not "desktop"
        api.Licences[MockEcosystem.OrgA] = [MockEcosystem.Licence("module", "edition:*", "active", null), MockEcosystem.Licence("module", "edition:desktop", "revoked", null)];
        await f.RefreshAsync();
        Assert.Null(plan.Licence); // two apply and HCG names none: none is picked (HCG: never invent a winner)
        api.Licences[MockEcosystem.OrgA] = [MockEcosystem.Licence("module", "edition:*", "active", null), MockEcosystem.Licence("product", null, "revoked", null)];
        await f.RefreshAsync();
        Assert.Equal("revoked", plan.Licence!.Status); // HCG names the single product-wide licence; that one is shown
        Assert.True(new EcosystemLicence("module", "edition:*", "active", null, null, null).AppliesTo("desktop"));
        Assert.False(new EcosystemLicence("module", "edition:ios", "active", null, null, null).AppliesTo("desktop"));
    }

    // ───────────── Revocation, expiry, refresh triggers ─────────────

    [Fact]
    public async Task A_revocation_shows_at_the_next_privileged_operation_without_waiting_for_expiry()
    {
        var (f, _, api, _, _) = Setup();
        await Ready(f);
        Assert.True((await f.RequireAsync(MakerFeatures.SupportSubmit)).IsAllowed);
        // HCG revokes the licence; /features is still within its validity, but every operation asks /check now.
        api.Features[MockEcosystem.OrgA]["support.submit"] = false;
        var posts = api.Calls.Count(c => c.Method == HttpMethod.Post);
        var ex = await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => f.RequireAsync(MakerFeatures.SupportSubmit));
        Assert.Equal(posts + 1, api.Calls.Count(c => c.Method == HttpMethod.Post));
        Assert.Equal("not_licensed", ex.Decision.Reason);
        Assert.Equal(FeatureAccessState.NotPermitted, f.Decide(MakerFeatures.SupportSubmit).State); // the UI shows it at once
    }

    [Fact]
    public async Task When_hcgs_answer_expires_it_is_not_used_and_is_read_again()
    {
        var api = new MockEcosystem { RevalidateSeconds = 60 };
        var (f, _, _, _, clock) = Setup(api);
        await Ready(f);
        var reads = api.Calls.Count(c => c.PathAndQuery.Contains("/features"));
        clock.Now += TimeSpan.FromSeconds(30);
        await f.RefreshIfExpiredAsync();
        Assert.Equal(reads, api.Calls.Count(c => c.PathAndQuery.Contains("/features")));
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.True(f.IsExpired);
        Assert.Equal(FeatureAccessState.Checking, f.Decide(MakerFeatures.SupportSubmit).State); // never allowed from an expired answer
        await f.RefreshIfExpiredAsync();
        Assert.Equal(reads + 1, api.Calls.Count(c => c.PathAndQuery.Contains("/features")));
        Assert.True(f.Decide(MakerFeatures.SupportSubmit).IsAllowed);
    }

    [Fact]
    public async Task Sign_in_account_change_and_sign_out_each_change_the_decisions()
    {
        var api = new MockEcosystem();
        var (f, _, _, account, _) = Setup(api, signIn: false);
        Assert.Equal(FeatureAccessState.SignInRequired, f.Decide(MakerFeatures.SupportSubmit).State);
        SignIn(account);
        await WaitFor(() => f.Status == FeatureAccessStatus.Ready);
        Assert.True(f.Decide(MakerFeatures.SupportSubmit).IsAllowed);

        // Another account signs in on this computer: the previous decisions are dropped and HCG is asked about the new one.
        api.Subject = "7a1e0000-0000-4000-8000-0000000000ee";
        api.Features[MockEcosystem.OrgA].Clear();
        SignIn(account, api.Subject);
        await WaitFor(() => f.Status == FeatureAccessStatus.Ready && f.Account?.Id == api.Subject);
        Assert.False(f.Decide(MakerFeatures.SupportSubmit).IsAllowed);

        await account.SignOutAsync();
        Assert.Equal(FeatureAccessState.SignInRequired, f.Decide(MakerFeatures.SupportSubmit).State);
        Assert.Null(f.Access);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_401_refreshes_once_then_ends_the_session_and_access()
    {
        var (f, _, api, account, _) = Setup();
        api.FailWith = HttpStatusCode.Unauthorized;
        await f.RefreshAsync();
        Assert.False(account.IsSignedIn);
        Assert.Equal(FeatureAccessState.SignInRequired, f.Decide(MakerFeatures.SupportSubmit).State);
    }

    // ───────────── Enforcement where the operation happens ─────────────

    [Fact]
    public async Task Support_requests_are_refused_before_anything_is_sent_when_hcg_does_not_allow_them()
    {
        var api = new MockEcosystem();
        api.Features[MockEcosystem.OrgA].Clear();
        var (f, _, _, account, _) = Setup(api);
        var site = new MockHcgWebsite();
        var service = new SupportService(new HcgApiClient(new HttpClient(site), account), new HttpClient(site), f);
        var request = new CreateSupportRequest
        {
            ClientRequestId = Guid.NewGuid(), Title = "t", Details = new SupportRequestDetails { Goal = "g", Problem = "p" }, Mode = SubmissionMode.DiagnosticsOnly,
            Diagnostics = [], AppVersion = "test",
        };
        var ex = await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => service.SubmitAsync(request, [], null, CancellationToken.None));
        Assert.Equal("support.submit", ex.Decision.Feature.Id);
        Assert.Empty(site.Calls); // nothing reached the support routes
    }

    // ───────────── Plan view and checkout ─────────────

    [Fact]
    public async Task Returning_from_a_checkout_proves_nothing_until_hcg_reports_a_change()
    {
        var api = new MockEcosystem();
        api.Features[MockEcosystem.OrgA].Clear();
        api.Licences[MockEcosystem.OrgA] = [];
        var (f, plan, _, _, clock) = Setup(api);
        await Ready(f);
        Assert.Equal(PlanAccessState.NotGranted, plan.State);
        plan.BeginPlanChange();
        await plan.OnReturnedAsync();
        Assert.Equal(PlanChangeState.AwaitingConfirmation, plan.ChangeState);
        api.Licences[MockEcosystem.OrgA] = [MockEcosystem.Licence("product", null, "active", null)];
        await plan.OnReturnedAsync();
        Assert.Equal(PlanChangeState.Confirmed, plan.ChangeState);
        Assert.Equal(PlanAccessState.Granted, plan.State);
        plan.Dispose();
    }

    [Theory]
    [InlineData("revoked", null, PlanAccessState.Suspended)]
    [InlineData("suspended", null, PlanAccessState.Suspended)]
    [InlineData("expired", null, PlanAccessState.Expired)]
    [InlineData("active", "2026-11-01T00:00:00Z", PlanAccessState.Expired)]
    [InlineData("active", null, PlanAccessState.Granted)]
    public async Task Licence_status_and_end_date_decide_the_plan_state(string status, string? endsAt, PlanAccessState expected)
    {
        var api = new MockEcosystem();
        api.Features[MockEcosystem.OrgA].Clear();
        api.Licences[MockEcosystem.OrgA] = [MockEcosystem.Licence("product", null, status, endsAt)];
        var (f, plan, _, _, _) = Setup(api);
        await Ready(f);
        Assert.Equal(expected, plan.State);
    }
}
