using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.Account;

public enum HcgAccountStatus
{
    /// <summary>
    /// The HCG connection is not configured on this computer (fields missing or invalid; see the configuration issues).
    /// Nothing is contacted. This says nothing about HCG's registration.
    /// </summary>
    NotConfigured,
    /// <summary>
    /// HCG's identity service answered that it does not accept this app's client (evidence: its own error code).
    /// Only set from HCG's answer, never inferred from local configuration.
    /// </summary>
    ClientRejected,
    SignedOut,
    Restoring,
    SigningIn,
    SignedIn,
    /// <summary>The session ended (refresh refused): sign in again.</summary>
    Expired,
    /// <summary>HCG revoked Executable Maker access for this account (ERP App Access): the session was removed.</summary>
    Revoked,
    /// <summary>Sign-in was declined or the account was refused.</summary>
    Denied,
    /// <summary>HCG could not be reached. A restored identity is kept and retried; nothing about access is implied.</summary>
    Unavailable,
}

/// <summary>Opens a URL in the user's default (system) browser. Never an embedded web view.</summary>
public interface ISystemBrowser
{
    Task<bool> OpenAsync(Uri url);
}

/// <summary>
/// The HCG session lifecycle: restore from the OS credential store, proactive refresh before expiry, sign-in through
/// the system browser, sign-out, and the expired / revoked / unavailable states. Tokens are never logged; only the
/// refresh token and identity are persisted (the access token stays in memory).
/// </summary>
public sealed class HcgAccountService : IDisposable
{
    private const string StoreAccount = "session";
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly HcgAuthClient _auth;
    private readonly ISecureCredentialStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private HcgSession? _session;
    private CancellationTokenSource? _refreshTimer;
    private CancellationTokenSource? _signInCts;

    private readonly HcgConnectionCheck? _check;

    public HcgAccountService(HcgConnectConfiguration config, HcgAuthClient auth, ISecureCredentialStore store, TimeProvider time,
        string? configurationProblem = null, string? storeProblem = null, ILogger<HcgAccountService>? logger = null, HcgConnectionCheck? check = null)
    {
        _check = check;
        Configuration = config;
        _auth = auth;
        _store = store;
        _time = time;
        _log = logger ?? NullLogger<HcgAccountService>.Instance;
        ConfigurationProblem = configurationProblem;
        StoreNote = storeProblem;
        var missing = config.MissingForSignIn();
        Status = missing.Count == 0 && configurationProblem is null ? HcgAccountStatus.SignedOut : HcgAccountStatus.NotConfigured;
        Message = Status == HcgAccountStatus.SignedOut ? "Not signed in." : NotConfiguredMessage(config, configurationProblem);
    }

    public HcgConnectConfiguration Configuration { get; private set; }
    public string? ConfigurationProblem { get; private set; }
    /// <summary>Set when the session cannot be kept between runs (no OS credential store).</summary>
    public string? StoreNote { get; }
    public string StoreDescription => _store.Description;
    public HcgAccountStatus Status { get; private set; }
    public string Message { get; private set; }
    public HcgIdentity? Identity { get; private set; }
    public string? OrganisationId => _session?.OrganisationId;
    public DateTimeOffset? SessionExpiresAt => _session?.ExpiresAt;
    public bool IsSignedIn => Status == HcgAccountStatus.SignedIn && _session is not null;
    /// <summary>True when the current session is saved in the OS credential store and survives a restart.</summary>
    public bool SessionPersisted { get; private set; }
    public event EventHandler? Changed;

    private void Set(HcgAccountStatus status, string message, HcgIdentity? identity)
    {
        lock (_gate)
        {
            Status = status;
            Message = message;
            Identity = identity;
        }
        _log.LogInformation("HCG account status {Status}", status); // never tokens or e-mail
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores a stored session (refreshing it) at startup.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (Status == HcgAccountStatus.NotConfigured) return;
        StoredSession? stored;
        try
        {
            var json = await _store.ReadAsync(StoreAccount, ct).ConfigureAwait(false);
            stored = json is null ? null : JsonSerializer.Deserialize<StoredSession>(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("The stored HCG session could not be read: {Error}", ex.GetType().Name);
            Set(HcgAccountStatus.SignedOut, $"The {_store.Description} could not be reached ({Security.SecretRedactor.Redact(ex.Message)}). " +
                "You can still sign in; the session then lasts for this run only unless the keyring becomes available.", null);
            return;
        }
        if (stored is null || stored.Version != 1 || stored.ClientId != Configuration.ClientId)
        {
            if (stored is not null) await SafeDeleteAsync().ConfigureAwait(false); // another client's or an old format: never reused
            return;
        }
        var identity = new HcgIdentity(stored.Subject, stored.Email, stored.DisplayName);
        Set(HcgAccountStatus.Restoring, "Restoring your HCG session…", identity);
        var placeholder = new HcgSession
        {
            AccessToken = "", RefreshToken = stored.RefreshToken, ExpiresAt = DateTimeOffset.MinValue, Identity = identity,
            ClientId = stored.ClientId, OrganisationId = stored.OrganisationId,
        };
        await RefreshCoreAsync(placeholder, ct).ConfigureAwait(false);
    }

    /// <summary>Sign in with HCG: system browser → HCG sign-in/consent → loopback callback → verified session.</summary>
    public async Task SignInAsync(ISystemBrowser browser, CancellationToken ct = default)
    {
        if (Configuration.MissingForSignIn().Count > 0)
        {
            Set(HcgAccountStatus.NotConfigured, NotConfiguredMessage(Configuration, ConfigurationProblem), null);
            return;
        }
        // Preflight (no browser, no credentials): ask HCG's identity service whether it accepts this client and redirect
        // URI, so a rejected client is reported with HCG's own answer instead of an error page in the browser.
        if (_check is not null)
        {
            var probe = await _check.ProbeClientAsync(Configuration, ct).ConfigureAwait(false);
            if (probe.State == ConnectionCheckState.Failed)
            {
                Set(probe.Code is { } code && HcgConnectionCheck.IsClientRejection(code) ? HcgAccountStatus.ClientRejected : HcgAccountStatus.Unavailable, probe.Detail, null);
                return;
            }
        }
        using var listener = LoopbackCallbackListener.StartFirstAvailable(Configuration.RedirectUris);
        if (listener is null)
        {
            Set(HcgAccountStatus.SignedOut, $"Sign-in needs one of these local ports, and all are in use by another program: {string.Join(", ", Configuration.RedirectUris.Select(u => u.Port))}. Close that program and try again.", null);
            return;
        }
        _signInCts?.Cancel();
        // Separate sources so a user's Cancel is never reported as a timeout.
        using var timeout = new CancellationTokenSource(SignInTimeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        _signInCts = cts;
        try
        {
            var attempt = _auth.BeginSignIn(listener.RedirectUri);
            Set(HcgAccountStatus.SigningIn, "Continue in your browser: sign in to HCG and allow HCG Executable Maker. This window updates when you are done.", null);
            if (!await browser.OpenAsync(attempt.AuthorizationUrl).ConfigureAwait(false))
            {
                Set(HcgAccountStatus.SignedOut, "Your browser could not be opened, so sign-in did not start.", null);
                return;
            }
            var callback = await listener.WaitForCallbackAsync(cts.Token).ConfigureAwait(false);
            var session = await _auth.CompleteSignInAsync(attempt, callback, cts.Token).ConfigureAwait(false);
            await AcceptAsync(session, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Set(HcgAccountStatus.SignedOut, timeout.IsCancellationRequested && !ct.IsCancellationRequested
                ? "Sign-in timed out after 5 minutes (no answer came back from the browser). Start it again when you are ready." : "Sign-in was cancelled.", null);
        }
        catch (HcgAuthException ex)
        {
            Set(ex.Failure switch
            {
                HcgAuthFailure.Denied => HcgAccountStatus.Denied,
                HcgAuthFailure.Unavailable => HcgAccountStatus.Unavailable,
                HcgAuthFailure.NotConfigured => HcgAccountStatus.NotConfigured,
                HcgAuthFailure.ClientRejected => HcgAccountStatus.ClientRejected,
                _ => HcgAccountStatus.SignedOut,
            }, ex.Message, null);
        }
        finally
        {
            if (ReferenceEquals(_signInCts, cts)) _signInCts = null;
        }
    }

    public void CancelSignIn() => _signInCts?.Cancel();

    /// <summary>Tests only: adopt a session as if sign-in had just completed (not stored).</summary>
    internal void AdoptForTesting(HcgSession session)
    {
        lock (_gate) _session = session;
        Set(HcgAccountStatus.SignedIn, "Signed in (test session).", session.Identity);
    }

    /// <summary>A valid access token for an HCG call (refreshed first when it is about to expire), or null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var session = _session;
        if (session is null) return null;
        if (!session.ExpiresSoon(_time.GetUtcNow())) return session.AccessToken;
        await RefreshCoreAsync(session, ct).ConfigureAwait(false);
        return Status == HcgAccountStatus.SignedIn ? _session?.AccessToken : null;
    }

    /// <summary>HCG answered 401 for a request made with the current token: refresh once; a second 401 ends the session.</summary>
    public async Task<bool> RecoverFromUnauthorizedAsync(string usedToken, CancellationToken ct = default)
    {
        var session = _session;
        if (session is null) return false;
        if (session.AccessToken != usedToken) return true; // already refreshed by another call
        await RefreshCoreAsync(session with { ExpiresAt = DateTimeOffset.MinValue }, ct).ConfigureAwait(false);
        return Status == HcgAccountStatus.SignedIn;
    }

    /// <summary>HCG revoked access (ERP App Access): forget the session and say so.</summary>
    public async Task MarkRevokedAsync(string message)
    {
        await ForgetAsync().ConfigureAwait(false);
        Set(HcgAccountStatus.Revoked, message, null);
    }

    /// <summary>HCG no longer accepts the session (401 after a refresh): forget it and ask for a new sign-in.</summary>
    public async Task MarkExpiredAsync(string message)
    {
        await ForgetAsync().ConfigureAwait(false);
        Set(HcgAccountStatus.Expired, message, null);
    }

    public async Task SetOrganisationAsync(string? organisationId, CancellationToken ct = default)
    {
        var session = _session;
        if (session is null) return;
        await AcceptAsync(session with { OrganisationId = organisationId }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs out on this computer: the stored session is deleted and every account feature stops. The identity server
    /// has no sign-out endpoint for app (OAuth client) sessions, so the server-side session ends when it expires or
    /// when HCG ends it; HCG's API checks that the session still exists on every request.
    /// </summary>
    /// <summary>
    /// Uses a new HCG Connect configuration from now on (an imported hcg-connect.json). A session issued to a different
    /// client or identity server is removed from this computer; a session for the same client is kept. Nothing is
    /// contacted: the next sign-in, restore or connection check uses the new configuration.
    /// </summary>
    public async Task ApplyConfigurationAsync(HcgConnectConfiguration config, CancellationToken ct = default)
    {
        _signInCts?.Cancel();
        var previous = Configuration;
        var sameClient = previous.ClientId == config.ClientId && previous.Issuer == config.Issuer && previous.TokenEndpoint == config.TokenEndpoint;
        Configuration = config;
        _auth.Configuration = config;
        ConfigurationProblem = null;
        var missing = config.MissingForSignIn();
        if (!sameClient) await ForgetAsync().ConfigureAwait(false);
        if (missing.Count > 0)
        {
            await ForgetAsync().ConfigureAwait(false);
            Set(HcgAccountStatus.NotConfigured, NotConfiguredMessage(config, null), null);
            return;
        }
        if (sameClient && _session is not null) { Changed?.Invoke(this, EventArgs.Empty); return; }
        Set(HcgAccountStatus.SignedOut, sameClient ? "Not signed in." : "The new HCG Connect configuration is in use. Sign in to connect your HCG account.", null);
        if (sameClient) await InitializeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Plain-language "configuration missing locally" message with every missing or invalid field.</summary>
    public static string NotConfiguredMessage(HcgConnectConfiguration config, string? problem) =>
        "The HCG connection is not set up on this computer" + (problem is null ? "" : $" ({problem})") +
        (config.Issues().Count > 0 ? ". Missing or invalid: " + string.Join(", ", config.Issues().Select(i => i.Field).Distinct()) + " (details under HCG Connect)" : "") +
        ". This is a local configuration state; it does not mean HCG rejected the app.";

    /// <summary>A later check found that HCG's identity service accepts the client: the rejection no longer applies.</summary>
    public void ClearClientRejection()
    {
        if (Status == HcgAccountStatus.ClientRejected) Set(HcgAccountStatus.SignedOut, "HCG's identity service accepts this app's client. Sign in to connect your HCG account.", null);
    }

    /// <summary>HCG's identity service rejected the client (evidence from its answer). Removes nothing but the in-progress sign-in.</summary>
    public void MarkClientRejected(string evidence)
    {
        _signInCts?.Cancel();
        Set(HcgAccountStatus.ClientRejected, evidence, null);
    }

    /// <summary>
    /// Re-reads hcg-connect.json from the data folder (e.g. after it was replaced) and applies it; a stored session for
    /// the same client is restored. Used by "Retry connection".
    /// </summary>
    public async Task ReloadConfigurationAsync(string appDataRoot, CancellationToken ct = default)
    {
        var (config, problem) = HcgConnectConfiguration.Load(appDataRoot);
        await ApplyConfigurationAsync(config, ct).ConfigureAwait(false);
        if (problem is not null || config.MissingForSignIn().Count > 0)
        {
            ConfigurationProblem = problem;
            await ForgetSessionInMemoryAsync().ConfigureAwait(false);
            Set(HcgAccountStatus.NotConfigured, NotConfiguredMessage(config, problem), null);
        }
        else if (!IsSignedIn) await InitializeAsync(ct).ConfigureAwait(false);
    }

    private Task ForgetSessionInMemoryAsync()
    {
        _refreshTimer?.Cancel();
        lock (_gate) _session = null;
        return Task.CompletedTask;
    }

    public async Task SignOutAsync()
    {
        await ForgetAsync().ConfigureAwait(false);
        Set(HcgAccountStatus.SignedOut, "Signed out. Your HCG session was removed from this computer.", null);
    }

    private async Task ForgetAsync()
    {
        _refreshTimer?.Cancel();
        lock (_gate) _session = null;
        SessionPersisted = false;
        await SafeDeleteAsync().ConfigureAwait(false);
    }

    private async Task SafeDeleteAsync()
    {
        try { await _store.DeleteAsync(StoreAccount).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning("The stored HCG session could not be deleted: {Error}", ex.GetType().Name); }
    }

    private async Task AcceptAsync(HcgSession session, CancellationToken ct)
    {
        lock (_gate) _session = session;
        SessionPersisted = false;
        if (session.RefreshToken.Length == 0)
        {
            // No refresh token was issued: usable until the access token expires, never stored.
            await SafeDeleteAsync().ConfigureAwait(false);
            Set(HcgAccountStatus.SignedIn, $"Signed in as {session.Identity.Email ?? session.Identity.DisplayName ?? "your HCG account"} for this run only: HCG issued no refresh token, so you will sign in again next time.", session.Identity);
            ScheduleRefresh(session);
            return;
        }
        try
        {
            await _store.WriteAsync(StoreAccount, JsonSerializer.Serialize(new StoredSession
            {
                Version = 1, ClientId = session.ClientId, RefreshToken = session.RefreshToken, Subject = session.Identity.Subject,
                Email = session.Identity.Email, DisplayName = session.Identity.DisplayName, OrganisationId = session.OrganisationId,
            }), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never claim the session will survive a restart when it was not saved.
            _log.LogWarning("The HCG session could not be stored: {Error}", ex.GetType().Name);
            Set(HcgAccountStatus.SignedIn, $"Signed in as {session.Identity.Email ?? session.Identity.DisplayName ?? "your HCG account"} for this run only: " +
                $"the session could not be saved in the {_store.Description} ({Security.SecretRedactor.Redact(ex.Message)}). You will sign in again next time.", session.Identity);
            ScheduleRefresh(session);
            return;
        }
        SessionPersisted = _store.IsPersistent;
        Set(HcgAccountStatus.SignedIn, $"Signed in as {session.Identity.Email ?? session.Identity.DisplayName ?? "your HCG account"}.", session.Identity);
        ScheduleRefresh(session);
    }

    private async Task RefreshCoreAsync(HcgSession current, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var latest = _session ?? current;
            if (latest.AccessToken.Length > 0 && !latest.ExpiresSoon(_time.GetUtcNow()) && latest.AccessToken != current.AccessToken) return; // refreshed meanwhile
            if (latest.RefreshToken.Length == 0)
            {
                await ForgetAsync().ConfigureAwait(false);
                Set(HcgAccountStatus.Expired, "Your HCG session ended (HCG issued no refresh token for it). Sign in again.", null);
                return;
            }
            try
            {
                var refreshed = await _auth.RefreshAsync(latest, ct).ConfigureAwait(false);
                await AcceptAsync(refreshed, ct).ConfigureAwait(false);
            }
            catch (HcgAuthException ex) when (ex.Failure == HcgAuthFailure.Unavailable)
            {
                // Keep the stored refresh token: an outage is not a sign-out. Retry later.
                lock (_gate) _session = latest.AccessToken.Length > 0 ? latest : null;
                Set(HcgAccountStatus.Unavailable, "HCG could not be reached, so your session could not be renewed. Account features are paused; local builds keep working. Try again later.", latest.Identity);
                ScheduleRetry(current);
            }
            catch (HcgAuthException ex) when (ex.Failure != HcgAuthFailure.Cancelled)
            {
                await ForgetAsync().ConfigureAwait(false);
                Set(HcgAccountStatus.Expired, ex.Message, null);
            }
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Retries a session restore/renewal that failed only because HCG was unreachable.</summary>
    public async Task RetryAsync(CancellationToken ct = default)
    {
        if (Status != HcgAccountStatus.Unavailable) return;
        var json = await _store.ReadAsync(StoreAccount, ct).ConfigureAwait(false);
        if (json is null) { Set(HcgAccountStatus.SignedOut, "Not signed in.", null); return; }
        var stored = JsonSerializer.Deserialize<StoredSession>(json)!;
        await RefreshCoreAsync(new HcgSession
        {
            AccessToken = "", RefreshToken = stored.RefreshToken, ExpiresAt = DateTimeOffset.MinValue,
            Identity = new HcgIdentity(stored.Subject, stored.Email, stored.DisplayName), ClientId = stored.ClientId, OrganisationId = stored.OrganisationId,
        }, ct).ConfigureAwait(false);
    }

    private void ScheduleRefresh(HcgSession session)
    {
        _refreshTimer?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshTimer = cts;
        var due = session.ExpiresAt - TimeSpan.FromSeconds(60) - _time.GetUtcNow();
        if (due < TimeSpan.FromSeconds(5)) due = TimeSpan.FromSeconds(5);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(due, _time, cts.Token).ConfigureAwait(false);
                await RefreshCoreAsync(session, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    private void ScheduleRetry(HcgSession session)
    {
        _refreshTimer?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshTimer = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), _time, cts.Token).ConfigureAwait(false);
                if (Status == HcgAccountStatus.Unavailable) await RetryAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning("HCG session retry failed: {Error}", ex.GetType().Name); }
        });
    }

    public void Dispose()
    {
        _refreshTimer?.Cancel();
        _signInCts?.Cancel();
    }

    private sealed class StoredSession
    {
        public int Version { get; set; }
        public string ClientId { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string Subject { get; set; } = "";
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
        public string? OrganisationId { get; set; }
    }
}
