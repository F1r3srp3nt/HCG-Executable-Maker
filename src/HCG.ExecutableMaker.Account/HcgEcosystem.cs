using System.Globalization;
using System.Text.Json;

namespace HCG.ExecutableMaker.Account;

/// <summary>An organisation the account belongs to, as <c>GET /me</c> reports it (<c>org_role</c>: owner/admin/member).</summary>
public sealed record EcosystemOrganisation(string Id, string Name, string? Role, string? Kind = null, string? Slug = null);

/// <summary>
/// <c>GET /me</c>: the verified subject, its e-mail and its organisation memberships. HCG reports no display name, so
/// none is shown from here.
/// </summary>
public sealed record EcosystemAccount(string? Id, string? Email, IReadOnlyList<EcosystemOrganisation> Organisations);

public sealed record EcosystemFeature(string Key, bool Allowed);

public sealed record EcosystemPlan(string? Key, string? Name, string? BillingType)
{
    public string Display => Name ?? Key ?? "Unnamed plan";
}

/// <summary>
/// A licence record from <c>GET /features</c>: <c>scope</c> product, module or feature; for editions a module licence
/// with <c>scope_key</c> <c>edition:&lt;key&gt;</c> (or <c>edition:*</c>).
/// </summary>
public sealed record EcosystemLicence(string? Scope, string? ScopeKey, string? Status, string? Type, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt)
{
    /// <summary>
    /// Whether this licence is about <paramref name="editionKey"/> of the product: a product-wide licence, a licence for
    /// exactly that edition, or an explicit all-editions licence. A licence for another edition never counts.
    /// </summary>
    public bool AppliesTo(string editionKey) =>
        Scope == "product" || Scope == "module" && (ScopeKey == "edition:" + editionKey || ScopeKey == "edition:*");
}

/// <summary>Links HCG provided for this product, already filtered to HTTPS links on HCG's own hosts.</summary>
public sealed record EcosystemLinks(Uri? Manage, Uri? Checkout, Uri? Account);

/// <summary>
/// <c>GET /features?org=&amp;product=</c>: the features HCG enables for the product in one organisation (a map; only
/// enabled features are listed and <c>true</c> means allowed), the plan and licence records HCG holds, links, and how
/// long the answer may be used (<c>revalidate_after_seconds</c>).
/// </summary>
public sealed record ProductAccess(string ProductId, string OrganisationId, IReadOnlyDictionary<string, bool> FeatureMap, EcosystemPlan? Plan,
    EcosystemLicence? Licence, IReadOnlyList<EcosystemLicence> Licences, EcosystemLinks Links, TimeSpan RevalidateAfter)
{
    public IReadOnlyList<EcosystemFeature> Features => FeatureMap.Select(kv => new EcosystemFeature(kv.Key, kv.Value)).ToList();
    public bool Allows(string featureKey) => FeatureMap.TryGetValue(featureKey, out var on) && on;

    /// <summary>Compared before and after a checkout: only HCG's own answer can change it.</summary>
    public string Fingerprint => string.Join("|", Plan?.Key, Plan?.Name,
        string.Join(";", Licences.Select(l => $"{l.Scope}:{l.ScopeKey}:{l.Status}:{l.EndsAt?.ToUnixTimeSeconds()}")),
        string.Join(",", FeatureMap.Where(f => f.Value).Select(f => f.Key).Order(StringComparer.Ordinal)));
}

/// <summary>
/// <c>POST /check</c>: one decision. <c>layer</c> availability/identity/licence and <c>reason</c> product_unavailable,
/// feature_unavailable, not_org_member, org_inactive, not_licensed or user_not_permitted (null when allowed).
/// </summary>
public sealed record AccessCheck(bool Allowed, string? Layer, string? Reason, DateTimeOffset? VerifiedAt);

/// <summary>
/// HCG's central Ecosystem API (<c>/api/public/ecosystem/v1</c>), read exactly as HCG's main site documents it
/// (docs/executable-maker/desktop-handoff.md there, 13 Nov 2026; HCG Store's MAIN-SITE-INTEGRATION.md, 16 Nov 2026):
/// <c>GET /me</c> (<c>user</c>, <c>organisations[].org_role</c>), <c>GET /features</c> (a <c>features</c> map,
/// nullable plan/licence, <c>licences</c>, <c>links</c>, <c>revalidate_after_seconds</c>) and <c>POST /check</c>
/// (<c>allowed</c>, <c>layer</c>, <c>reason</c>, <c>verified_at</c>). An answer in any other form, or about another
/// organisation, product or feature than the one asked for, is "not recognised": access unknown, never a denial and never
/// a grant.
/// </summary>
public sealed class HcgEcosystemClient(HcgApiClient api)
{
    public const string ApiVersion = "v1";
    public static readonly TimeSpan DefaultRevalidateAfter = TimeSpan.FromMinutes(5);

    public HcgApiClient Api => api;
    public HcgConnectConfiguration Configuration => api.Account.Configuration;

    public async Task<EcosystemAccount> MeAsync(CancellationToken ct) =>
        ParseMe(await api.SendEcosystemAsync<JsonElement>(HttpMethod.Get, "me", null, ct).ConfigureAwait(false));

    public async Task<ProductAccess> FeaturesAsync(string organisationId, CancellationToken ct)
    {
        var query = "features?org=" + Uri.EscapeDataString(organisationId) + "&product=" + Uri.EscapeDataString(Configuration.ProductId);
        var json = await api.SendEcosystemAsync<JsonElement>(HttpMethod.Get, query, null, ct).ConfigureAwait(false);
        return ParseFeatures(json, Configuration, organisationId);
    }

    public async Task<AccessCheck> CheckAsync(string organisationId, string featureKey, CancellationToken ct) =>
        ParseCheck(await api.SendEcosystemAsync<JsonElement>(HttpMethod.Post, "check",
            new Dictionary<string, string> { ["org_id"] = organisationId, ["product_id"] = Configuration.ProductId, ["feature_key"] = featureKey }, ct)
            .ConfigureAwait(false), organisationId, Configuration.ProductId, featureKey);

    private static HcgApiException NotRecognised(string what) =>
        new(HcgApiFailure.Unavailable, $"HCG answered {what} in a form this version of HCG Executable Maker does not recognise, so access is unknown (not denied).", "unrecognised");

    private static void RequireVersion(JsonElement root, string what)
    {
        if (root.ValueKind != JsonValueKind.Object || Str(root, "api_version") != ApiVersion) throw NotRecognised(what);
    }

    internal static EcosystemAccount ParseMe(JsonElement root)
    {
        RequireVersion(root, "/me");
        if (Obj(root, "user") is not { } user || Str(user, "id") is not { } id || Arr(root, "organisations") is not { } list) throw NotRecognised("/me");
        var orgs = new List<EcosystemOrganisation>();
        foreach (var o in list.EnumerateArray())
            if (Str(o, "org_id") is { } orgId)
                orgs.Add(new EcosystemOrganisation(orgId, Str(o, "name") ?? Str(o, "slug") ?? orgId, Str(o, "org_role"), Str(o, "kind"), Str(o, "slug")));
        return new EcosystemAccount(id, Str(user, "email"), orgs.DistinctBy(o => o.Id).ToList());
    }

    internal static ProductAccess ParseFeatures(JsonElement root, HcgConnectConfiguration config, string organisationId)
    {
        RequireVersion(root, "/features");
        if (Str(root, "org_id") != organisationId || Str(root, "product_id") != config.ProductId) throw NotRecognised("/features (about another organisation or product)");
        if (!root.TryGetProperty("features", out var map) || map.ValueKind != JsonValueKind.Object) throw NotRecognised("/features");
        var features = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var p in map.EnumerateObject())
            if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) features[p.Name] = p.Value.GetBoolean();
        EcosystemPlan? plan = Obj(root, "plan") is { } pl ? new EcosystemPlan(Str(pl, "plan_key"), Str(pl, "name"), Str(pl, "billing_type")) : null;
        static EcosystemLicence Licence(JsonElement x) =>
            new(Str(x, "scope"), Str(x, "scope_key"), Str(x, "status"), Str(x, "license_type"), Date(x, "starts_at"), Date(x, "ends_at"));
        var licences = Arr(root, "licences") is { } ls ? ls.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(Licence).ToList() : [];
        var licence = Obj(root, "licence") is { } single ? Licence(single) : null;
        var links = Obj(root, "links");
        var revalidate = root.TryGetProperty("revalidate_after_seconds", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var secs) && secs > 0
            ? TimeSpan.FromSeconds(Math.Min(secs, 3600)) : DefaultRevalidateAfter;
        return new ProductAccess(config.ProductId, organisationId, features, plan, licence, licences,
            new EcosystemLinks(config.TrustedLink(links is { } a ? Str(a, "manage") : null), config.TrustedLink(links is { } b ? Str(b, "checkout") : null),
                config.TrustedLink(links is { } c ? Str(c, "account") : null)), revalidate);
    }

    internal static AccessCheck ParseCheck(JsonElement root, string organisationId, string productId, string featureKey)
    {
        RequireVersion(root, "/check");
        if (Str(root, "org_id") != organisationId || Str(root, "product_id") != productId || Str(root, "feature_key") != featureKey)
            throw NotRecognised("/check (about another organisation, product or feature)");
        if (Bool(root, "allowed") is not { } allowed) throw NotRecognised("/check");
        return new AccessCheck(allowed, Str(root, "layer"), Str(root, "reason"), Date(root, "verified_at"));
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    private static JsonElement? Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;
    private static string? Str(JsonElement e, string name) => IdTokenValidator.Str(e, name) is { Length: > 0 } s ? s : null;
    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

public enum PlanAccessState
{
    SignedOut,
    Checking,
    /// <summary>HCG grants this account at least one of Executable Maker's account features, or an active licence.</summary>
    Granted,
    /// <summary>Signed in, but HCG grants no Executable Maker feature to this account/organisation.</summary>
    NotGranted,
    /// <summary>HCG reports the licence as expired (or past its end date).</summary>
    Expired,
    /// <summary>HCG reports the licence as suspended or revoked.</summary>
    Suspended,
    /// <summary>HCG could not be reached or answered in an unrecognised form: unknown, never shown as a denial.</summary>
    Unavailable,
}

/// <summary>What the app knows about a plan change the user started in the browser (checkout or plan management).</summary>
public enum PlanChangeState
{
    None,
    /// <summary>The user opened an HCG link; the app re-reads HCG until HCG reports a change or 10 minutes pass.</summary>
    AwaitingConfirmation,
    /// <summary>HCG's API now reports a different plan, licence or feature set.</summary>
    Confirmed,
    /// <summary>10 minutes passed and HCG reports no change: nothing was assumed.</summary>
    NotConfirmed,
}

/// <summary>
/// The plan and licence view of the Account page and <c>--hcg-session status</c>: a projection of the shared
/// <see cref="FeatureAccessService"/> (it never asks HCG by itself). After the user opens a checkout or plan-management
/// link HCG provided, it asks the shared service to re-read HCG; a change counts only when HCG's API reports it.
/// </summary>
public sealed class AccountPlanService : IDisposable
{
    public static readonly TimeSpan ConfirmationPollInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(10);

    private readonly FeatureAccessService _features;
    private readonly TimeProvider _time;
    private string? _fingerprintBeforeChange;
    private DateTimeOffset _changeStartedAt;
    private CancellationTokenSource? _poll;

    public AccountPlanService(FeatureAccessService features, TimeProvider time)
    {
        _features = features;
        _time = time;
        _features.Changed += (_, _) =>
        {
            EvaluatePlanChange();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public FeatureAccessService Features => _features;

    public PlanAccessState State => Project().State;
    public string Message => Project().Message;
    public EcosystemAccount? Account => _features.Account;
    public ProductAccess? Access => _features.Access;
    public PlanChangeState ChangeState { get; private set; }
    public string ChangeMessage { get; private set; } = "";
    public DateTimeOffset? CheckedAt => _features.CheckedAt;
    public IReadOnlyList<EcosystemOrganisation> Organisations => _features.Organisations;
    public string? OrganisationId => _features.OrganisationId;
    public bool NeedsOrganisationChoice => _features.NeedsOrganisationChoice;
    public string PlanDisplay => Access?.Plan?.Display ?? "Not reported by HCG";

    /// <summary>
    /// The Executable Maker licence HCG reports for the desktop edition: the single licence when HCG names one, else the
    /// only product-wide or desktop-edition licence. With several, none is picked (HCG's rule: never invent a winner).
    /// </summary>
    public EcosystemLicence? Licence =>
        Access?.Licence is { } one && one.AppliesTo(FeatureAccessService.EditionKey) ? one
        : Access?.Licences.Where(l => l.AppliesTo(FeatureAccessService.EditionKey)).ToList() is [var only] ? only : null;

    public int OtherEditionLicences => Access?.Licences.Count(l => !l.AppliesTo(FeatureAccessService.EditionKey)) ?? 0;
    public DateTimeOffset? ValidUntil => Licence?.EndsAt;
    public Uri AccountLink => Access?.Links.Account ?? _features.Configuration.AccountPortalUrl;
    public event EventHandler? Changed;

    public bool Allows(string featureKey) => _features.Decide(MakerFeatures.ById(featureKey)).IsAllowed;

    public Task RefreshAsync(CancellationToken ct = default, bool force = true) => _features.RefreshAsync(force ? "refresh" : "recheck", ct, force);

    public Task SelectOrganisationAsync(string organisationId, CancellationToken ct = default) => _features.SelectOrganisationAsync(organisationId, ct);

    private (PlanAccessState State, string Message) Project()
    {
        var f = _features;
        if (f.Status == FeatureAccessStatus.SignedOut) return (PlanAccessState.SignedOut, "Sign in with HCG to see your plan and access. Local builds do not need an account.");
        if (f.Status == FeatureAccessStatus.NotConfigured) return (PlanAccessState.SignedOut, f.StatusMessage);
        if (f.Status == FeatureAccessStatus.Checking) return (PlanAccessState.Checking, f.StatusMessage);
        if (f.Status == FeatureAccessStatus.Unavailable) return (PlanAccessState.Unavailable, f.StatusMessage);
        if (f.Status is FeatureAccessStatus.ChooseOrganisation or FeatureAccessStatus.NoOrganisation or FeatureAccessStatus.NotMember)
            return (PlanAccessState.NotGranted, f.StatusMessage);
        if (Access is null) return (PlanAccessState.Unavailable, f.StatusMessage);
        return Decide(Access, Licence, _time.GetUtcNow());
    }

    internal static (PlanAccessState, string) Decide(ProductAccess access, EcosystemLicence? licence, DateTimeOffset now)
    {
        var status = licence?.Status?.ToLowerInvariant();
        if (status is "suspended" or "revoked")
            return (PlanAccessState.Suspended, $"HCG reports your organisation's HCG Executable Maker licence as {status}.");
        if (status == "expired" || licence?.EndsAt is { } end && end <= now)
            return (PlanAccessState.Expired, "HCG reports your organisation's HCG Executable Maker licence as expired" + (licence?.EndsAt is { } e ? $" (ended {e.LocalDateTime:d})." : "."));
        if (access.FeatureMap.Values.Any(v => v) || status == "active")
            return (PlanAccessState.Granted, "HCG grants HCG Executable Maker's account features in this organisation.");
        return (PlanAccessState.NotGranted, "Signed in, but HCG enables no HCG Executable Maker account feature in this organisation. Local builds do not need one.");
    }

    /// <summary>
    /// The user is about to open a checkout or plan-management page HCG provided. Remembers HCG's current answer and
    /// re-reads HCG until it reports something different (or 10 minutes pass).
    /// </summary>
    public void BeginPlanChange()
    {
        _fingerprintBeforeChange = Access?.Fingerprint ?? "";
        _changeStartedAt = _time.GetUtcNow();
        ChangeState = PlanChangeState.AwaitingConfirmation;
        ChangeMessage = "Waiting for HCG to confirm a change. Finish in your browser, then return here; your plan updates only when HCG reports it.";
        Changed?.Invoke(this, EventArgs.Empty);
        StartPolling();
    }

    /// <summary>The user came back to the app. Only a reason to ask HCG again; it never changes the plan by itself.</summary>
    public Task OnReturnedAsync(CancellationToken ct = default) =>
        ChangeState == PlanChangeState.AwaitingConfirmation ? RefreshAsync(ct) : Task.CompletedTask;

    private void EvaluatePlanChange()
    {
        if (ChangeState != PlanChangeState.AwaitingConfirmation) return;
        if (Access is not null && Access.Fingerprint != _fingerprintBeforeChange)
        {
            ChangeState = PlanChangeState.Confirmed;
            ChangeMessage = $"HCG confirmed the change: {PlanDisplay}" + (ValidUntil is { } v ? $", valid until {v.LocalDateTime:d}." : ".");
            StopPolling();
        }
        else if (_time.GetUtcNow() - _changeStartedAt >= ConfirmationWindow)
        {
            ChangeState = PlanChangeState.NotConfirmed;
            ChangeMessage = Access is null
                ? "HCG could not confirm a change to your plan. Check again later or open your HCG account."
                : "HCG has not reported a change to your plan. If you completed a purchase, check again later or open your HCG account.";
            StopPolling();
        }
    }

    private void StartPolling()
    {
        StopPolling();
        var cts = new CancellationTokenSource();
        _poll = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested && ChangeState == PlanChangeState.AwaitingConfirmation)
                {
                    await Task.Delay(ConfirmationPollInterval, _time, cts.Token).ConfigureAwait(false);
                    await RefreshAsync(cts.Token).ConfigureAwait(false);
                    EvaluatePlanChange();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void StopPolling()
    {
        _poll?.Cancel();
        _poll = null;
    }

    public void Dispose() => StopPolling();
}
