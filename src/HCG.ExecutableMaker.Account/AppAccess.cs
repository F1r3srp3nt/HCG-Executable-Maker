using System.Text.Json.Serialization;

namespace HCG.ExecutableMaker.Account;

/// <summary>
/// <c>GET {api}/access</c>: Executable Maker's App Access, decided by the database from the caller's verified token and
/// ERP App Access. Same shape as the website's <c>/api/public/mobile/workspaces</c> directory, plus the permissions the
/// server computed for this app. The app only displays this and uses it to explain; the server enforces it again on
/// every protected request.
/// </summary>
public sealed record AppAccessDirectory
{
    public AccessUser? User { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<AccessWorkspace> Workspaces { get; init; } = [];
    /// <summary>Permissions the server granted this caller for Executable Maker (e.g. support.submit, support.attach, ai.use).</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];
    /// <summary>Organisations the account belongs to (from the ERP), when it has any.</summary>
    public IReadOnlyList<AccessOrganisation> Organisations { get; init; } = [];
    /// <summary>The organisation the server applied to this request (echo of X-HCG-Organisation when valid).</summary>
    public string? Organisation { get; init; }
    public AccessLimits? Limits { get; init; }
    public AccessIntegration? Integration { get; init; }
}

public sealed record AccessUser(string Id, string? Email, string? DisplayName);
public sealed record AccessWorkspace(string Id, string? Title, string? Route, bool Permitted, bool Available, string? Requires);
public sealed record AccessOrganisation(string Id, string Name, string? Role);
public sealed record AccessIntegration(int? Version, string? Contract);

/// <summary>Upload limits the server enforces (shown before upload).</summary>
public sealed record AccessLimits
{
    public long MaxAttachmentBytes { get; init; }
    public long MaxRequestBytes { get; init; }
    public int MaxAttachments { get; init; }
    public long MaxArchiveExpandedBytes { get; init; }
    public int MaxArchiveEntries { get; init; }
    public int AttachmentRetentionDays { get; init; }
}

public static class HcgPermissions
{
    public const string SupportSubmit = "support.submit";
    public const string SupportAttach = "support.attach";
    public const string SupportStaff = "support.staff";
    public const string AiUse = "ai.use";
}

public enum AppAccessState
{
    /// <summary>Not signed in / not configured: account features need sign-in.</summary>
    SignedOut,
    Checking,
    Granted,
    /// <summary>Signed in, but ERP App Access does not grant Executable Maker to this account.</summary>
    NotGranted,
    Revoked,
    /// <summary>HCG could not be reached or the route is not deployed: access is unknown, not denied.</summary>
    Unavailable,
}

/// <summary>Reads and caches App Access; re-checks on demand and every 15 minutes while signed in.</summary>
public sealed class AppAccessService
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(15);
    private readonly HcgApiClient _api;
    private readonly TimeProvider _time;
    private DateTimeOffset _checkedAt;

    public AppAccessService(HcgApiClient api, TimeProvider time)
    {
        _api = api;
        _time = time;
        _api.Account.Changed += (_, _) =>
        {
            if (!_api.Account.IsSignedIn && State != AppAccessState.SignedOut)
            {
                Directory = null;
                State = _api.Account.Status == HcgAccountStatus.Revoked ? AppAccessState.Revoked : AppAccessState.SignedOut;
                Message = _api.Account.Message;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public AppAccessState State { get; private set; } = AppAccessState.SignedOut;
    public string Message { get; private set; } = "Sign in with HCG to use account features. Local builds do not need an account.";
    public AppAccessDirectory? Directory { get; private set; }
    public event EventHandler? Changed;

    public bool Has(string permission) => State == AppAccessState.Granted && Directory?.Permissions.Contains(permission) == true;

    public async Task RefreshAsync(CancellationToken ct = default, bool force = true)
    {
        if (!_api.Account.IsSignedIn)
        {
            Update(_api.Account.Status == HcgAccountStatus.Revoked ? AppAccessState.Revoked : AppAccessState.SignedOut, _api.Account.Message, null);
            return;
        }
        if (!force && State == AppAccessState.Granted && _time.GetUtcNow() - _checkedAt < RecheckInterval) return;
        Update(AppAccessState.Checking, "Checking your Executable Maker access with HCG…", Directory);
        try
        {
            var directory = await _api.GetAsync<AppAccessDirectory>("access", ct).ConfigureAwait(false);
            _checkedAt = _time.GetUtcNow();
            // The directory must be about the signed-in account; anything else is ignored.
            if (directory.User?.Id is { } id && _api.Account.Identity?.Subject is { } sub && id != sub)
            {
                Update(AppAccessState.Unavailable, "HCG returned access information for a different account; it was ignored.", null);
                return;
            }
            var entry = directory.Workspaces.FirstOrDefault(w => w.Id == _api.Account.Configuration.AppIdentifier);
            if (entry is null)
                Update(AppAccessState.NotGranted, "HCG does not list HCG Executable Maker for your account yet. Access is granted by HCG in ERP ▸ App Access.", directory);
            else if (!entry.Available)
                Update(AppAccessState.Unavailable, "HCG Executable Maker's account services are temporarily unavailable on HCG.", directory);
            else if (!entry.Permitted)
                Update(AppAccessState.NotGranted, "Your HCG account is signed in, but HCG has not granted it HCG Executable Maker's account features. " +
                                                  (entry.Requires is { } r ? "Requires: " + r + "." : "Ask HCG to grant access in ERP ▸ App Access."), directory);
            else
                Update(AppAccessState.Granted, "HCG Executable Maker access granted by HCG.", directory);
        }
        catch (HcgApiException ex) when (ex.Failure == HcgApiFailure.Revoked)
        {
            Update(AppAccessState.Revoked, ex.Message, null);
        }
        catch (HcgApiException ex) when (ex.Failure is HcgApiFailure.Forbidden)
        {
            Update(AppAccessState.NotGranted, ex.Message, null);
        }
        catch (HcgApiException ex) when (ex.Failure is HcgApiFailure.NotSignedIn or HcgApiFailure.Unauthorized)
        {
            Update(AppAccessState.SignedOut, ex.Message, null);
        }
        catch (HcgApiException ex)
        {
            Update(AppAccessState.Unavailable, ex.Failure == HcgApiFailure.NotDeployed
                ? "HCG Executable Maker's account services are not live on HCG yet (the website routes are not deployed). Local builds keep working."
                : ex.Message, Directory);
        }
    }

    private void Update(AppAccessState state, string message, AppAccessDirectory? directory)
    {
        State = state;
        Message = message;
        Directory = directory;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
