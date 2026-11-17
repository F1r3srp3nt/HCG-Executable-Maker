namespace HCG.ExecutableMaker.Account;

public enum FeatureKind
{
    /// <summary>Local, offline functionality: never needs an account, a network or a licence.</summary>
    Free,
    /// <summary>Decided by HCG's central access check for <c>hcg.executable.maker</c> (a central feature key).</summary>
    Account,
    /// <summary>A staff permission decided by the service that performs the operation (HCG Store). Never sold.</summary>
    StaffOnly,
}

/// <summary>One feature of HCG Executable Maker and who decides it.</summary>
/// <param name="CentralKey">The feature key registered for <c>hcg.executable.maker</c> in HCG's catalogue (null when HCG does not decide it centrally).</param>
public sealed record MakerFeature(string Id, string Title, string Description, FeatureKind Kind, string? CentralKey);

/// <summary>
/// Executable Maker's features. Central keys are the ones HCG registered for <c>hcg.executable.maker</c> (main site,
/// 13 Nov 2026): <c>support.submit</c>, <c>support.attach</c> and <c>ai.use</c>, catalogued disabled/planned with no
/// grants. Local building is free and offline. Publishing to HCG Store is a staff permission HCG Store checks.
/// </summary>
public static class MakerFeatures
{
    public static readonly MakerFeature LocalBuilds = new("local.builds", "Local builds and packaging",
        "Import, inspect, build and package projects on this computer, with your toolchains. Projects, builds and settings stay on this computer. No account or network needed.",
        FeatureKind.Free, null);
    public static readonly MakerFeature SupportSubmit = new("support.submit", "Project support requests",
        "Describe a project HCG Executable Maker cannot build yet and send diagnostics to HCG.", FeatureKind.Account, "support.submit");
    public static readonly MakerFeature SupportAttach = new("support.attach", "Attach project files",
        "Include selected files or a project archive with a support request.", FeatureKind.Account, "support.attach");
    public static readonly MakerFeature AiUse = new("ai.use", "HCG AI assistance",
        "Explain build errors and other problems with HCG AI.", FeatureKind.Account, "ai.use");
    public static readonly MakerFeature StorePublish = new("store.publish", "Ship to Store (publishing)",
        "Upload builds to HCG Store as private drafts that a person reviews and approves in HCG Store.", FeatureKind.StaffOnly, null);

    public static IReadOnlyList<MakerFeature> All { get; } = [LocalBuilds, SupportSubmit, SupportAttach, AiUse, StorePublish];

    public static MakerFeature ById(string id) =>
        All.FirstOrDefault(f => f.Id == id || f.CentralKey == id) ?? throw new ArgumentException($"Unknown HCG Executable Maker feature \"{id}\".", nameof(id));
}

public enum FeatureAccessState
{
    /// <summary>Free, local functionality: always available.</summary>
    Free,
    Allowed,
    /// <summary>HCG (or HCG Store) decided: not permitted. Never used for an outage.</summary>
    NotPermitted,
    /// <summary>HCG has not made this available yet (product or feature not enabled, service not running).</summary>
    NotAvailableYet,
    SignInRequired,
    NotConfigured,
    ChooseOrganisation,
    /// <summary>HCG could not be reached or its answer was not recognised: unknown, never a denial.</summary>
    Unavailable,
    Checking,
    /// <summary>Decided when the operation is used (staff permissions checked by HCG Store).</summary>
    NotYetChecked,
}

/// <summary>What the person can do about a locked feature.</summary>
public enum FeatureRemedy
{
    None,
    SignIn,
    ChooseOrganisation,
    /// <summary>Open the product's page in HCG Store (only where a licence is what is missing).</summary>
    ViewInStore,
    /// <summary>Ask the organisation's HCG administrator: staff permissions and organisation access are never bought.</summary>
    AskAdministrator,
    TryAgain,
    WaitForHcg,
}

public sealed record FeatureDecision(MakerFeature Feature, FeatureAccessState State, string Explanation, FeatureRemedy Remedy = FeatureRemedy.None,
    Uri? Link = null, string? Layer = null, string? Reason = null, DateTimeOffset? DecidedAt = null, DateTimeOffset? ValidUntil = null,
    string? OrganisationId = null, string Source = "HCG")
{
    public bool IsAllowed => State is FeatureAccessState.Free or FeatureAccessState.Allowed;
    /// <summary>True when access is unknown because HCG could not answer: retry later, nothing was denied.</summary>
    public bool IsUnavailable => State is FeatureAccessState.Unavailable;

    public string StateLabel => State switch
    {
        FeatureAccessState.Free => "Free · works offline",
        FeatureAccessState.Allowed => "Allowed by HCG",
        FeatureAccessState.NotPermitted => "Not permitted",
        FeatureAccessState.NotAvailableYet => "Not available yet",
        FeatureAccessState.SignInRequired => "Sign in required",
        FeatureAccessState.NotConfigured => "HCG connection not set up",
        FeatureAccessState.ChooseOrganisation => "Choose organisation",
        FeatureAccessState.Unavailable => "HCG unavailable",
        FeatureAccessState.Checking => "Checking with HCG",
        _ => "Checked when used",
    };

    public string Tone => State switch
    {
        FeatureAccessState.Free or FeatureAccessState.Allowed => "success",
        FeatureAccessState.NotPermitted => "error",
        FeatureAccessState.Unavailable or FeatureAccessState.ChooseOrganisation or FeatureAccessState.NotAvailableYet => "warning",
        FeatureAccessState.Checking => "info",
        _ => "neutral",
    };

    public string RemedyText => Remedy switch
    {
        FeatureRemedy.SignIn => "Sign in with HCG on the Account page.",
        FeatureRemedy.ChooseOrganisation => "Choose the organisation on the Account page.",
        FeatureRemedy.ViewInStore => "See HCG Executable Maker in HCG Store.",
        FeatureRemedy.AskAdministrator => "Ask your organisation's HCG administrator. This is not something you can buy.",
        FeatureRemedy.TryAgain => "Try again when HCG is reachable. Local builds keep working.",
        FeatureRemedy.WaitForHcg => "HCG has not enabled this yet. Nothing to buy or change on your side.",
        _ => "",
    };
}

/// <summary>Thrown when an operation needs a feature HCG (or HCG Store) did not allow at that moment.</summary>
public sealed class FeatureAccessDeniedException(FeatureDecision decision) : Exception(decision.Explanation)
{
    public FeatureDecision Decision { get; } = decision;
}

/// <summary>
/// Command-line exit codes for a decision (the same for <c>--hcg-session require</c> and <c>--ship-to-store</c>):
/// 0 allowed, 3 sign-in needed, 8 not permitted, 9 HCG unavailable or not available yet, 10 choose an organisation
/// (not a refusal: HCG has not been asked about one yet).
/// </summary>
public static class FeatureExitCodes
{
    public static int For(FeatureDecision d) => d.State switch
    {
        FeatureAccessState.Free or FeatureAccessState.Allowed => 0,
        FeatureAccessState.SignInRequired or FeatureAccessState.NotConfigured => 3,
        FeatureAccessState.NotPermitted => 8,
        FeatureAccessState.ChooseOrganisation => 10,
        _ => 9,
    };
}

/// <summary>Decides the staff-only publishing permission by asking HCG Store (which asks HCG). Implemented by the app.</summary>
public interface IStorePublishingAuthority
{
    /// <param name="storeOrigin">The HCG Store the operation will use (null: the configured one).</param>
    Task<FeatureDecision> CheckAsync(MakerFeature feature, Uri? storeOrigin, CancellationToken ct);
}

public enum FeatureAccessStatus
{
    NotConfigured,
    SignedOut,
    Checking,
    /// <summary>HCG answered for the chosen organisation.</summary>
    Ready,
    ChooseOrganisation,
    /// <summary>HCG reports no organisation: licences belong to organisations.</summary>
    NoOrganisation,
    /// <summary>HCG says the account is not a member of the chosen organisation.</summary>
    NotMember,
    Unavailable,
}

/// <summary>
/// The one place HCG Executable Maker decides whether a feature may be used, shared by the UI and the command line.
/// <list type="bullet">
/// <item>Local builds are free and offline; nothing here can lock them.</item>
/// <item>Account features are decided by HCG: <c>GET /me</c> (organisations), <c>GET /features</c> (what the chosen
/// organisation has, valid for <c>revalidate_after_seconds</c>) for display, and a fresh <c>POST /check</c> before every
/// privileged remote operation (<see cref="RequireAsync"/>). The answer is never cached for an operation.</item>
/// <item>Decisions are re-read on sign-in, when the account or organisation changes, when HCG's answer expires, and
/// before privileged operations.</item>
/// <item>"Not permitted" (HCG's decision) and "HCG unavailable" (no decision) are never confused.</item>
/// <item>Staff-only publishing is decided by HCG Store and points to the administrator, never to a purchase.</item>
/// </list>
/// The servers enforce everything again; this decides what the app offers and refuses before it calls them.
/// </summary>
public sealed class FeatureAccessService : IDisposable
{
    /// <summary>Executable Maker's edition key in HCG's registry (D3): one desktop edition.</summary>
    public const string EditionKey = "desktop";
    public static readonly Uri DefaultStoreProductPage = new("https://store.hakmicapitalgroup.com/apps/hcg-executable-maker");
    public static readonly TimeSpan StoreDecisionLifetime = TimeSpan.FromMinutes(5);

    private readonly HcgEcosystemClient _ecosystem;
    private readonly HcgAccountService _account;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AccessCheck> _checks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FeatureDecision> _storeDecisions = new(StringComparer.Ordinal);
    private ITimer? _expiry;
    private string? _subject;
    private string? _organisation;

    public FeatureAccessService(HcgEcosystemClient ecosystem, TimeProvider time, Uri? storeProductPage = null)
    {
        _ecosystem = ecosystem;
        _account = ecosystem.Api.Account;
        _time = time;
        StoreProductPage = storeProductPage ?? DefaultStoreProductPage;
        Status = _account.Status == HcgAccountStatus.NotConfigured ? FeatureAccessStatus.NotConfigured : FeatureAccessStatus.SignedOut;
        StatusMessage = _account.Status == HcgAccountStatus.NotConfigured ? _account.Message : SignedOutMessage;
        _account.Changed += (_, _) => OnAccountChanged();
    }

    private const string SignedOutMessage = "Sign in with HCG to use account features. Local builds do not need an account.";
    /// <summary>HCG could not renew the session because it could not be reached: not a sign-out and not a refusal.</summary>
    private bool SessionPaused => _account.Status == HcgAccountStatus.Unavailable;
    private string PausedMessage => _account.Message is { Length: > 0 } m ? m : "HCG could not be reached to renew your session. Access is unknown until it answers; local builds keep working.";

    public Uri StoreProductPage { get; }
    public HcgConnectConfiguration Configuration => _account.Configuration;
    public IStorePublishingAuthority? StoreAuthority { get; set; }
    public FeatureAccessStatus Status { get; private set; }
    public string StatusMessage { get; private set; }
    public EcosystemAccount? Account { get; private set; }
    public ProductAccess? Access { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }
    /// <summary>When HCG's answer must be re-read (<c>revalidate_after_seconds</c>).</summary>
    public DateTimeOffset? ValidUntil { get; private set; }
    public event EventHandler? Changed;

    public IReadOnlyList<EcosystemOrganisation> Organisations => Account?.Organisations ?? [];
    /// <summary>The organisation HCG is asked about: the stored choice when it is a membership, else the only one.</summary>
    public string? OrganisationId =>
        Organisations.Any(o => o.Id == _account.OrganisationId) ? _account.OrganisationId : Organisations.Count == 1 ? Organisations[0].Id : null;
    public bool NeedsOrganisationChoice => Organisations.Count > 1 && OrganisationId is null;
    public bool IsExpired => ValidUntil is { } v && _time.GetUtcNow() >= v;

    private void OnAccountChanged()
    {
        if (!_account.IsSignedIn)
        {
            if (_account.Status is HcgAccountStatus.Restoring or HcgAccountStatus.SigningIn) return;
            if (SessionPaused) { Set(FeatureAccessStatus.Unavailable, PausedMessage); return; }
            var notConfigured = _account.Status == HcgAccountStatus.NotConfigured;
            if (Account is null && Status == (notConfigured ? FeatureAccessStatus.NotConfigured : FeatureAccessStatus.SignedOut)) return;
            Clear(notConfigured ? FeatureAccessStatus.NotConfigured : FeatureAccessStatus.SignedOut, notConfigured ? _account.Message : SignedOutMessage);
            return;
        }
        // Signed in, another account, or another organisation chosen: HCG is asked again.
        var subject = _account.Identity?.Subject;
        if (subject != _subject || _account.OrganisationId != _organisation)
        {
            if (subject != _subject) Clear(FeatureAccessStatus.Checking, "Checking your access with HCG…");
            _ = RefreshAsync(subject != _subject ? "sign-in" : "organisation change");
        }
    }

    private void Clear(FeatureAccessStatus status, string message)
    {
        _expiry?.Dispose();
        _expiry = null;
        lock (_checks) { _checks.Clear(); _storeDecisions.Clear(); }
        Account = null;
        Access = null;
        CheckedAt = null;
        ValidUntil = null;
        _subject = null;
        _organisation = null;
        Set(status, message);
    }

    private void Set(FeatureAccessStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-reads HCG: <c>/me</c>, then <c>/features</c> for the chosen organisation, and <c>/check</c> for each account
    /// feature HCG does not enable there (so a locked feature can say why). <paramref name="force"/> false keeps an answer
    /// that has not expired.
    /// </summary>
    public async Task RefreshAsync(string reason = "refresh", CancellationToken ct = default, bool force = true)
    {
        if (!_account.IsSignedIn)
        {
            if (SessionPaused) { Set(FeatureAccessStatus.Unavailable, PausedMessage); return; }
            if (Status is not (FeatureAccessStatus.SignedOut or FeatureAccessStatus.NotConfigured))
                Clear(_account.Status == HcgAccountStatus.NotConfigured ? FeatureAccessStatus.NotConfigured : FeatureAccessStatus.SignedOut,
                    _account.Status == HcgAccountStatus.NotConfigured ? _account.Message : SignedOutMessage);
            return;
        }
        if (!force && Status == FeatureAccessStatus.Ready && !IsExpired) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Set(FeatureAccessStatus.Checking, "Checking your access with HCG…");
            var subject = _account.Identity?.Subject;
            var me = await _ecosystem.MeAsync(ct).ConfigureAwait(false);
            if (me.Id is null || me.Id != subject || _account.Identity?.Subject != subject)
            {
                Account = null;
                Access = null;
                Set(FeatureAccessStatus.Unavailable, "HCG answered about a different account; the answer was ignored and access is unknown.");
                return;
            }
            Account = me;
            _subject = subject;
            _organisation = _account.OrganisationId;
            var now = _time.GetUtcNow();
            if (me.Organisations.Count == 0)
            {
                Access = null;
                Stamp(now, HcgEcosystemClient.DefaultRevalidateAfter);
                Set(FeatureAccessStatus.NoOrganisation, "HCG reports no organisation for this account. HCG licences belong to organisations, so account features stay locked. Ask your HCG administrator to add you to one. Local builds do not need it.");
                return;
            }
            if (OrganisationId is not { } org)
            {
                Access = null;
                Stamp(now, HcgEcosystemClient.DefaultRevalidateAfter);
                Set(FeatureAccessStatus.ChooseOrganisation, "Choose the organisation whose HCG access HCG Executable Maker should use.");
                return;
            }
            ProductAccess access;
            try { access = await _ecosystem.FeaturesAsync(org, ct).ConfigureAwait(false); }
            catch (HcgApiException ex) when (ex.Failure == HcgApiFailure.Forbidden && ex.Code == "not_org_member")
            {
                Access = null;
                Stamp(now, HcgEcosystemClient.DefaultRevalidateAfter);
                Set(FeatureAccessStatus.NotMember, $"HCG says you are not a member of {OrganisationName(org)} (anymore). Choose another organisation, or ask its HCG administrator.");
                return;
            }
            var checks = new Dictionary<string, AccessCheck>(StringComparer.Ordinal);
            foreach (var f in MakerFeatures.All.Where(f => f.Kind == FeatureKind.Account && !access.Allows(f.CentralKey!)))
                checks[f.CentralKey!] = await _ecosystem.CheckAsync(org, f.CentralKey!, ct).ConfigureAwait(false);
            lock (_checks)
            {
                _checks.Clear();
                foreach (var (k, v) in checks) _checks[k] = v;
            }
            Access = access;
            Stamp(now, access.RevalidateAfter);
            Set(FeatureAccessStatus.Ready, $"HCG answered for {OrganisationName(org)}.");
        }
        catch (HcgApiException ex) when (ex.Failure is HcgApiFailure.NotSignedIn or HcgApiFailure.Unauthorized or HcgApiFailure.Revoked)
        {
            Clear(FeatureAccessStatus.SignedOut, ex.Message);
        }
        catch (HcgApiException ex)
        {
            // Outages, missing routes and unrecognised answers: unknown, never a denial. The last answer stays visible but
            // is no longer used to allow anything, and HCG is asked again after a minute.
            ArmRetry(OutageRetry);
            Set(FeatureAccessStatus.Unavailable, (ex.Failure == HcgApiFailure.NotDeployed
                ? "HCG's access service is not available on this HCG site."
                : ex.Message) + " Access is unknown until HCG answers; local builds keep working.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Re-reads HCG when its last answer has expired (called by the expiry timer and before showing decisions).</summary>
    public Task RefreshIfExpiredAsync(CancellationToken ct = default) =>
        _account.IsSignedIn && (IsExpired || Status == FeatureAccessStatus.Unavailable) ? RefreshAsync("policy expiry", ct) : Task.CompletedTask;

    private void Stamp(DateTimeOffset now, TimeSpan lifetime)
    {
        CheckedAt = now;
        ValidUntil = now + lifetime;
        ArmRetry(lifetime + TimeSpan.FromSeconds(1));
    }

    internal static readonly TimeSpan OutageRetry = TimeSpan.FromSeconds(60);

    private void ArmRetry(TimeSpan after)
    {
        _expiry?.Dispose();
        _expiry = _time.CreateTimer(_ => _ = RefreshIfExpiredAsync(), null, after, Timeout.InfiniteTimeSpan);
    }

    public async Task SelectOrganisationAsync(string organisationId, CancellationToken ct = default)
    {
        if (Organisations.All(o => o.Id != organisationId)) return;
        await _account.SetOrganisationAsync(organisationId, ct).ConfigureAwait(false);
        await RefreshAsync("organisation change", ct).ConfigureAwait(false);
    }

    private string OrganisationName(string id) => Organisations.FirstOrDefault(o => o.Id == id)?.Name ?? id;

    /// <summary>The current decision for <paramref name="feature"/> from what HCG last said (no network). For display.</summary>
    public FeatureDecision Decide(MakerFeature feature)
    {
        if (feature.Kind == FeatureKind.Free)
            return new(feature, FeatureAccessState.Free, feature.Description, Source: "this computer");
        if (_account.Status == HcgAccountStatus.NotConfigured)
            return new(feature, FeatureAccessState.NotConfigured, "The HCG connection is not set up on this computer, so HCG cannot be asked. " + _account.Message);
        if (SessionPaused)
            return new(feature, FeatureAccessState.Unavailable, $"HCG cannot confirm {feature.Title} right now. This is not a refusal: {PausedMessage}", FeatureRemedy.TryAgain);
        if (!_account.IsSignedIn)
            return new(feature, FeatureAccessState.SignInRequired, $"{feature.Title} needs your HCG account. Sign in with HCG; local builds keep working without it.", FeatureRemedy.SignIn);
        if (feature.Kind == FeatureKind.StaffOnly)
        {
            lock (_checks)
                if (_storeDecisions.TryGetValue(feature.Id, out var d) && (d.ValidUntil is null || d.ValidUntil > _time.GetUtcNow())) return d;
            return new(feature, FeatureAccessState.NotYetChecked,
                $"{feature.Title} is for HCG staff with HCG Store's publishing permission. HCG Store checks it when you connect.", Source: "HCG Store");
        }
        var org = OrganisationId;
        switch (Status)
        {
            case FeatureAccessStatus.Checking:
                return new(feature, FeatureAccessState.Checking, "Checking with HCG…");
            case FeatureAccessStatus.Unavailable:
                return new(feature, FeatureAccessState.Unavailable, $"HCG cannot confirm {feature.Title} right now. This is not a refusal: {StatusMessage}", FeatureRemedy.TryAgain);
            case FeatureAccessStatus.ChooseOrganisation:
                return new(feature, FeatureAccessState.ChooseOrganisation, "You belong to several HCG organisations. Choose which one HCG Executable Maker uses.", FeatureRemedy.ChooseOrganisation);
            case FeatureAccessStatus.NoOrganisation:
                return new(feature, FeatureAccessState.NotPermitted, StatusMessage, FeatureRemedy.AskAdministrator, Reason: "no_organisation");
            case FeatureAccessStatus.NotMember:
                return new(feature, FeatureAccessState.NotPermitted, StatusMessage, FeatureRemedy.ChooseOrganisation, Layer: "identity", Reason: "not_org_member", OrganisationId: org);
            case FeatureAccessStatus.SignedOut or FeatureAccessStatus.NotConfigured:
                return new(feature, FeatureAccessState.SignInRequired, StatusMessage, FeatureRemedy.SignIn);
        }
        if (Access is null || org is null || Access.OrganisationId != org)
            return new(feature, FeatureAccessState.Checking, "Checking with HCG…");
        if (IsExpired)
            return new(feature, FeatureAccessState.Checking, "HCG's last answer has expired; checking again…", OrganisationId: org);
        if (Access.Allows(feature.CentralKey!))
            return new(feature, FeatureAccessState.Allowed, $"HCG allows {feature.Title} in {OrganisationName(org)}.", DecidedAt: CheckedAt, ValidUntil: ValidUntil, OrganisationId: org);
        AccessCheck? check;
        lock (_checks) check = _checks.GetValueOrDefault(feature.CentralKey!);
        return check is { Allowed: false }
            ? Explain(feature, check, org)
            : new(feature, FeatureAccessState.NotPermitted, $"HCG does not enable {feature.Title} in {OrganisationName(org)}.", FeatureRemedy.AskAdministrator,
                DecidedAt: CheckedAt, ValidUntil: ValidUntil, OrganisationId: org);
    }

    public IReadOnlyList<FeatureDecision> DecideAll() => MakerFeatures.All.Select(Decide).ToList();

    /// <summary>
    /// Asks the deciding service again, now, before a privileged remote operation, and returns the decision when it
    /// allows the feature. Otherwise throws <see cref="FeatureAccessDeniedException"/> (its decision says whether HCG
    /// refused or could not be reached). Never answered from a cache.
    /// </summary>
    public async Task<FeatureDecision> RequireAsync(MakerFeature feature, CancellationToken ct = default, Uri? storeOrigin = null)
    {
        var decision = await CheckNowAsync(feature, ct, storeOrigin).ConfigureAwait(false);
        return decision.IsAllowed ? decision : throw new FeatureAccessDeniedException(decision);
    }

    /// <summary>Like <see cref="RequireAsync"/> but returns the decision instead of throwing.</summary>
    public async Task<FeatureDecision> CheckNowAsync(MakerFeature feature, CancellationToken ct = default, Uri? storeOrigin = null)
    {
        if (feature.Kind == FeatureKind.Free) return Decide(feature);
        if (!_account.IsSignedIn) return Decide(feature);
        if (feature.Kind == FeatureKind.StaffOnly)
        {
            var store = StoreAuthority is { } authority
                ? await authority.CheckAsync(feature, storeOrigin, ct).ConfigureAwait(false)
                : new FeatureDecision(feature, FeatureAccessState.NotAvailableYet, "This build has no HCG Store connection.", Source: "HCG Store");
            store = store with { DecidedAt = store.DecidedAt ?? _time.GetUtcNow(), ValidUntil = _time.GetUtcNow() + StoreDecisionLifetime };
            lock (_checks) _storeDecisions[feature.Id] = store;
            Changed?.Invoke(this, EventArgs.Empty);
            return store;
        }
        // The organisation and membership come from a current /me; an expired or missing answer is re-read first.
        if (Account is null || IsExpired || Status != FeatureAccessStatus.Ready || OrganisationId is null || _account.OrganisationId != _organisation)
            await RefreshAsync("before " + feature.Id, ct).ConfigureAwait(false);
        if (Status != FeatureAccessStatus.Ready || OrganisationId is not { } org) return Decide(feature);
        var askedFor = _account.Identity?.Subject;
        AccessCheck check;
        try { check = await _ecosystem.CheckAsync(org, feature.CentralKey!, ct).ConfigureAwait(false); }
        catch (HcgApiException ex) when (ex.Failure is HcgApiFailure.NotSignedIn or HcgApiFailure.Unauthorized or HcgApiFailure.Revoked)
        {
            return new(feature, FeatureAccessState.SignInRequired, ex.Message, FeatureRemedy.SignIn);
        }
        catch (HcgApiException ex) when (ex.Failure == HcgApiFailure.Forbidden && ex.Code == "not_org_member")
        {
            return new(feature, FeatureAccessState.NotPermitted, $"HCG says you are not a member of {OrganisationName(org)} (anymore).", FeatureRemedy.ChooseOrganisation,
                Layer: "identity", Reason: "not_org_member", OrganisationId: org);
        }
        catch (HcgApiException ex)
        {
            return new(feature, FeatureAccessState.Unavailable, $"HCG cannot confirm {feature.Title} right now, so it was not used. This is not a refusal: {ex.Message}",
                FeatureRemedy.TryAgain, OrganisationId: org);
        }
        // Signed out, another account or another organisation while HCG answered: the answer is about someone else now.
        if (_account.Identity?.Subject != askedFor || !_account.IsSignedIn || OrganisationId != org)
            return new(feature, FeatureAccessState.Unavailable, $"Your HCG account or organisation changed while HCG was checking {feature.Title}, so the answer was not used. Try again.",
                FeatureRemedy.TryAgain);
        // The fresh answer replaces what the app showed: a revocation or a new grant shows at once.
        lock (_checks) _checks[feature.CentralKey!] = check;
        if (Access is { } a && a.OrganisationId == org && a.Allows(feature.CentralKey!) != check.Allowed)
        {
            var map = new Dictionary<string, bool>(a.FeatureMap, StringComparer.Ordinal);
            if (check.Allowed) map[feature.CentralKey!] = true; else map.Remove(feature.CentralKey!);
            Access = a with { FeatureMap = map };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return check.Allowed
            ? new(feature, FeatureAccessState.Allowed, $"HCG allows {feature.Title} in {OrganisationName(org)}.", DecidedAt: check.VerifiedAt ?? _time.GetUtcNow(), OrganisationId: org)
            : Explain(feature, check, org);
    }

    /// <summary>HCG's reason in plain language. Unknown reasons are shown as HCG sent them, not mapped to a guess.</summary>
    private FeatureDecision Explain(MakerFeature feature, AccessCheck check, string org)
    {
        var name = OrganisationName(org);
        var (state, text, remedy, link) = check.Reason switch
        {
            "not_licensed" => (FeatureAccessState.NotPermitted,
                $"{name} has no active HCG Executable Maker licence that includes {feature.Title} (none was granted, or it expired or was revoked).",
                FeatureRemedy.ViewInStore, StoreProductPage),
            "feature_unavailable" => (FeatureAccessState.NotAvailableYet, $"HCG has not enabled {feature.Title} for HCG Executable Maker yet.", FeatureRemedy.WaitForHcg, (Uri?)null),
            "product_unavailable" => (FeatureAccessState.NotAvailableYet, "HCG Executable Maker's account features are not available on HCG yet.", FeatureRemedy.WaitForHcg, null),
            "not_org_member" => (FeatureAccessState.NotPermitted, $"You are not a member of {name} (anymore).", FeatureRemedy.ChooseOrganisation, null),
            "org_inactive" => (FeatureAccessState.NotPermitted, $"{name} is inactive in HCG.", FeatureRemedy.AskAdministrator, null),
            "user_not_permitted" => (FeatureAccessState.NotPermitted, $"{name} has HCG Executable Maker, but HCG has not given your account access to {feature.Title}.",
                FeatureRemedy.AskAdministrator, null),
            null => (FeatureAccessState.NotPermitted, $"HCG did not allow {feature.Title} in {name} and gave no reason.", FeatureRemedy.None, null),
            var other => (FeatureAccessState.NotPermitted, $"HCG did not allow {feature.Title} in {name} (HCG's reason: {other}).", FeatureRemedy.None, null),
        };
        return new(feature, state, text, remedy, link, check.Layer, check.Reason, check.VerifiedAt ?? CheckedAt, ValidUntil, org);
    }

    public void Dispose()
    {
        _expiry?.Dispose();
        _gate.Dispose();
    }
}
