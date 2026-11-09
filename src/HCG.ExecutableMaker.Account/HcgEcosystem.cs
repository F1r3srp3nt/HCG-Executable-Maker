using System.Globalization;
using System.Text.Json;

namespace HCG.ExecutableMaker.Account;

public sealed record EcosystemOrganisation(string Id, string Name, string? Role);
public sealed record EcosystemAccount(string? Id, string? Email, string? DisplayName, IReadOnlyList<EcosystemOrganisation> Organisations);
public sealed record EcosystemFeature(string Key, string? DisplayName, bool Allowed);
public sealed record EcosystemPlan(string? Key, string? Name, string? BillingType)
{
    public string Display => Name ?? Key ?? "Unnamed plan";
}
public sealed record EcosystemLicence(string? Status, string? Type, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);
/// <summary>Links HCG provided for this product, already filtered to HTTPS links on HCG's own hosts.</summary>
public sealed record EcosystemLinks(Uri? Manage, Uri? Checkout, Uri? Account);
public sealed record ProductAccess(string ProductId, string? OrganisationId, IReadOnlyList<EcosystemFeature> Features, EcosystemPlan? Plan,
    EcosystemLicence? Licence, EcosystemLinks Links)
{
    /// <summary>Compared before and after a checkout: only HCG's own answer can change it.</summary>
    public string Fingerprint => string.Join("|", Plan?.Key, Plan?.Name, Licence?.Status, Licence?.EndsAt?.ToUnixTimeSeconds(),
        string.Join(",", Features.Where(f => f.Allowed).Select(f => f.Key).Order(StringComparer.Ordinal)));
}
public sealed record AccessCheck(bool Allowed, string? BlockingLayer, string? Message);

/// <summary>
/// HCG's central Ecosystem API (<c>/api/public/ecosystem/v1</c>): the account's organisation memberships
/// (<c>GET /me</c>), the features of a product it may use in an organisation (<c>GET /features?org=&amp;product=</c>)
/// and single access decisions (<c>POST /check</c>). The routes and error envelope are live-verified; the response
/// field names follow the names HCG's ERP uses for the same records and are pending HCG's confirmation
/// (docs/hcg-connect/HANDOFF.md §6). Parsing is tolerant: a missing field is reported as "not reported by HCG",
/// never invented.
/// </summary>
public sealed class HcgEcosystemClient(HcgApiClient api)
{
    public HcgApiClient Api => api;
    public HcgConnectConfiguration Configuration => api.Account.Configuration;

    public async Task<EcosystemAccount> MeAsync(CancellationToken ct) =>
        ParseMe(await api.SendEcosystemAsync<JsonElement>(HttpMethod.Get, "me", null, ct).ConfigureAwait(false));

    public async Task<ProductAccess> FeaturesAsync(string? organisationId, CancellationToken ct)
    {
        var query = "features?" + (organisationId is null ? "" : "org=" + Uri.EscapeDataString(organisationId) + "&") +
                    "product=" + Uri.EscapeDataString(Configuration.ProductId);
        var json = await api.SendEcosystemAsync<JsonElement>(HttpMethod.Get, query, null, ct).ConfigureAwait(false);
        return ParseFeatures(json, Configuration, organisationId);
    }

    public async Task<AccessCheck> CheckAsync(string? organisationId, string featureKey, CancellationToken ct) =>
        ParseCheck(await api.SendEcosystemAsync<JsonElement>(HttpMethod.Post, "check",
            new Dictionary<string, string?> { ["org_id"] = organisationId, ["product_id"] = Configuration.ProductId, ["feature_key"] = featureKey }, ct).ConfigureAwait(false));

    internal static EcosystemAccount ParseMe(JsonElement root)
    {
        var account = Obj(root, "account") ?? Obj(root, "user") ?? root;
        var orgs = new List<EcosystemOrganisation>();
        foreach (var name in new[] { "organisations", "organizations", "memberships" })
            if (Arr(root, name) is { } list)
            {
                foreach (var o in list.EnumerateArray())
                {
                    var org = Obj(o, "organisation") ?? Obj(o, "organization") ?? o;
                    var id = Str(o, "org_id") ?? Str(org, "org_id") ?? Str(org, "id");
                    if (id is null) continue;
                    orgs.Add(new EcosystemOrganisation(id, Str(org, "name") ?? Str(o, "name") ?? id, Str(o, "role")));
                }
                break;
            }
        return new EcosystemAccount(Str(account, "id") ?? Str(account, "user_id"), Str(account, "email"),
            Str(account, "display_name") ?? Str(account, "name"), orgs.DistinctBy(o => o.Id).ToList());
    }

    internal static ProductAccess ParseFeatures(JsonElement root, HcgConnectConfiguration config, string? organisationId)
    {
        var features = new List<EcosystemFeature>();
        var list = root.ValueKind == JsonValueKind.Array ? root : Arr(root, "features");
        if (list is { } items)
            foreach (var f in items.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.String) { features.Add(new EcosystemFeature(f.GetString()!, null, true)); continue; }
                var key = Str(f, "feature_key") ?? Str(f, "key");
                if (key is null) continue;
                // A listed feature is allowed unless HCG says otherwise.
                var allowed = Bool(f, "allowed") ?? Bool(f, "enabled") ?? true;
                features.Add(new EcosystemFeature(key, Str(f, "display_name") ?? Str(f, "name"), allowed));
            }
        EcosystemPlan? plan = Obj(root, "plan") is { } p ? new EcosystemPlan(Str(p, "plan_key") ?? Str(p, "key"), Str(p, "name"), Str(p, "billing_type")) : null;
        var l = Obj(root, "licence") ?? Obj(root, "license");
        EcosystemLicence? licence = l is { } x
            ? new EcosystemLicence(Str(x, "status"), Str(x, "license_type") ?? Str(x, "licence_type") ?? Str(x, "type"), Date(x, "starts_at"), Date(x, "ends_at"))
            : null;
        var links = Obj(root, "links");
        return new ProductAccess(Str(root, "product_id") ?? config.ProductId, Str(root, "org_id") ?? organisationId, features, plan, licence,
            new EcosystemLinks(config.TrustedLink(links is { } a ? Str(a, "manage") : null), config.TrustedLink(links is { } b ? Str(b, "checkout") : null),
                config.TrustedLink(links is { } c ? Str(c, "account") : null)));
    }

    internal static AccessCheck ParseCheck(JsonElement root) =>
        new(Bool(root, "allowed") ?? false, Str(root, "blocking_layer") ?? Str(root, "layer") ?? Str(root, "blocked_by"), Str(root, "message") ?? Str(root, "reason"));

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
    /// <summary>HCG grants this account Executable Maker's account features (an active licence or allowed features).</summary>
    Granted,
    /// <summary>Signed in, but HCG grants no Executable Maker feature to this account/organisation.</summary>
    NotGranted,
    /// <summary>HCG reports the licence as expired (or past its end date).</summary>
    Expired,
    /// <summary>HCG reports the licence as suspended or revoked.</summary>
    Suspended,
    /// <summary>HCG could not be reached or the route does not exist: unknown, never shown as a denial.</summary>
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
/// Plan and access from HCG's central APIs: organisation memberships, the product's features in the chosen
/// organisation, the plan and licence HCG reports and the links HCG provides. Re-checked every 15 minutes, on demand,
/// and after the user returns from a checkout or plan-management page, where a change counts only when HCG's API
/// reports it (returning, or a browser page saying "success", proves nothing). Local builds never depend on this.
/// </summary>
public sealed class AccountPlanService : IDisposable
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ConfirmationPollInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(10);

    private readonly HcgEcosystemClient _ecosystem;
    private readonly HcgAccountService _account;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _checkedAt;
    private string? _fingerprintBeforeChange;
    private DateTimeOffset _changeStartedAt;
    private CancellationTokenSource? _poll;

    public AccountPlanService(HcgEcosystemClient ecosystem, TimeProvider time)
    {
        _ecosystem = ecosystem;
        _account = ecosystem.Api.Account;
        _time = time;
        _account.Changed += (_, _) =>
        {
            if (_account.IsSignedIn || State == PlanAccessState.SignedOut) return;
            StopPolling();
            Account = null;
            Access = null;
            ChangeState = PlanChangeState.None;
            Set(PlanAccessState.SignedOut, "Sign in with HCG to see your plan and access. Local builds do not need an account.");
        };
    }

    public PlanAccessState State { get; private set; } = PlanAccessState.SignedOut;
    public string Message { get; private set; } = "Sign in with HCG to see your plan and access. Local builds do not need an account.";
    public EcosystemAccount? Account { get; private set; }
    public ProductAccess? Access { get; private set; }
    public PlanChangeState ChangeState { get; private set; }
    public string ChangeMessage { get; private set; } = "";
    public DateTimeOffset? CheckedAt => _checkedAt == default ? null : _checkedAt;
    public IReadOnlyList<EcosystemOrganisation> Organisations => Account?.Organisations ?? [];
    /// <summary>The organisation the app asks HCG about: the stored choice when it is a membership, else the only one.</summary>
    public string? OrganisationId =>
        Organisations.Any(o => o.Id == _account.OrganisationId) ? _account.OrganisationId : Organisations.Count == 1 ? Organisations[0].Id : null;
    public bool NeedsOrganisationChoice => Organisations.Count > 1 && OrganisationId is null;
    public string PlanDisplay => Access?.Plan?.Display ?? "Not reported by HCG";
    public DateTimeOffset? ValidUntil => Access?.Licence?.EndsAt;
    public Uri AccountLink => Access?.Links.Account ?? _account.Configuration.AccountPortalUrl;
    public event EventHandler? Changed;

    public bool Allows(string featureKey) => State == PlanAccessState.Granted && Access?.Features.Any(f => f.Key == featureKey && f.Allowed) == true;

    public async Task RefreshAsync(CancellationToken ct = default, bool force = true)
    {
        if (!_account.IsSignedIn)
        {
            Set(PlanAccessState.SignedOut, "Sign in with HCG to see your plan and access. Local builds do not need an account.");
            return;
        }
        if (!force && State is (PlanAccessState.Granted or PlanAccessState.NotGranted) && _time.GetUtcNow() - _checkedAt < RecheckInterval) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Set(PlanAccessState.Checking, "Checking your plan and access with HCG…");
            var me = await _ecosystem.MeAsync(ct).ConfigureAwait(false);
            if (me.Id is { } id && _account.Identity?.Subject is { } sub && id != sub)
            {
                Account = null;
                Access = null;
                Set(PlanAccessState.Unavailable, "HCG returned information for a different account; it was ignored.");
                return;
            }
            Account = me;
            if (NeedsOrganisationChoice)
            {
                Access = null;
                _checkedAt = _time.GetUtcNow();
                Set(PlanAccessState.NotGranted, "Choose the organisation whose HCG access HCG Executable Maker should use.");
                return;
            }
            var access = await _ecosystem.FeaturesAsync(OrganisationId, ct).ConfigureAwait(false);
            Access = access;
            _checkedAt = _time.GetUtcNow();
            var (state, message) = Decide(access, _time.GetUtcNow());
            Set(state, message);
            EvaluatePlanChange();
        }
        catch (HcgApiException ex) when (ex.Failure == HcgApiFailure.Revoked)
        {
            Access = null;
            Set(PlanAccessState.Suspended, ex.Message);
        }
        catch (HcgApiException ex) when (ex.Failure == HcgApiFailure.Forbidden)
        {
            Access = null;
            Set(PlanAccessState.NotGranted, ex.Message);
        }
        catch (HcgApiException ex) when (ex.Failure is HcgApiFailure.NotSignedIn or HcgApiFailure.Unauthorized)
        {
            Access = null;
            Set(PlanAccessState.SignedOut, ex.Message);
        }
        catch (HcgApiException ex)
        {
            // Outages, missing routes and unreadable answers: the plan is unknown, never denied. The last answer stays visible.
            Set(PlanAccessState.Unavailable, ex.Failure == HcgApiFailure.NotDeployed
                ? "HCG's account service is not available on this HCG site yet. Local builds keep working."
                : ex.Message + " Your plan is unknown until HCG answers; local builds keep working.");
        }
        finally { _gate.Release(); }
    }

    internal static (PlanAccessState, string) Decide(ProductAccess access, DateTimeOffset now)
    {
        var status = access.Licence?.Status?.ToLowerInvariant();
        if (status is "suspended" or "revoked" or "cancelled" or "canceled")
            return (PlanAccessState.Suspended, $"HCG reports your HCG Executable Maker licence as {status}.");
        if (status == "expired" || access.Licence?.EndsAt is { } end && end <= now)
            return (PlanAccessState.Expired, "HCG reports your HCG Executable Maker licence as expired" + (access.Licence?.EndsAt is { } e ? $" (ended {e.LocalDateTime:d})." : "."));
        if (access.Features.Any(f => f.Allowed) || status is "active" or "trial" or "trialing")
            return (PlanAccessState.Granted, "HCG grants HCG Executable Maker's account features to this account.");
        return (PlanAccessState.NotGranted, "Signed in, but HCG grants no HCG Executable Maker account features to this account" +
                                            (access.OrganisationId is null ? "." : " in this organisation."));
    }

    public async Task SelectOrganisationAsync(string organisationId, CancellationToken ct = default)
    {
        if (Organisations.All(o => o.Id != organisationId)) return;
        await _account.SetOrganisationAsync(organisationId, ct).ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// The user came back to the app (window activated) or a checkout return was noticed. This is only a reason to ask
    /// HCG again; it never changes the plan by itself.
    /// </summary>
    public Task OnReturnedAsync(CancellationToken ct = default) =>
        ChangeState == PlanChangeState.AwaitingConfirmation ? RefreshAsync(ct) : Task.CompletedTask;

    private void EvaluatePlanChange()
    {
        if (ChangeState != PlanChangeState.AwaitingConfirmation || Access is null) return;
        if (Access.Fingerprint != _fingerprintBeforeChange)
        {
            ChangeState = PlanChangeState.Confirmed;
            ChangeMessage = $"HCG confirmed the change: {PlanDisplay}" + (ValidUntil is { } v ? $", valid until {v.LocalDateTime:d}." : ".");
            StopPolling();
        }
        else if (_time.GetUtcNow() - _changeStartedAt >= ConfirmationWindow)
        {
            ChangeState = PlanChangeState.NotConfirmed;
            ChangeMessage = "HCG has not reported a change to your plan. If you completed a purchase, check again later or open your HCG account.";
            StopPolling();
        }
        Changed?.Invoke(this, EventArgs.Empty);
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
                    if (ChangeState == PlanChangeState.AwaitingConfirmation && Access is null) EvaluateTimeoutOnly();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void EvaluateTimeoutOnly()
    {
        if (_time.GetUtcNow() - _changeStartedAt < ConfirmationWindow) return;
        ChangeState = PlanChangeState.NotConfirmed;
        ChangeMessage = "HCG could not confirm a change to your plan. Check again later or open your HCG account.";
        StopPolling();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StopPolling()
    {
        _poll?.Cancel();
        _poll = null;
    }

    private void Set(PlanAccessState state, string message)
    {
        State = state;
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => StopPolling();
}
