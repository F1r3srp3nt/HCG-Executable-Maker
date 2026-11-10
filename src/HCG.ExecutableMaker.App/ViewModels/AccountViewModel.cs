using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed record AccountPermissionRow(string Title, string Detail, StatusTag State);

/// <summary>
/// Account: Sign in with HCG (system browser), profile, organisation, the access HCG granted, sign out. Every state is
/// explained (not configured, signing in, expired, revoked, denied, HCG unavailable). Local builds never need this.
/// </summary>
public sealed partial class AccountViewModel : PageViewModel
{
    private readonly HcgAccountService _account;
    private readonly AppAccessService _access;
    private readonly AccountPlanService _plan;
    private readonly IPlatformShell _shell;
    private readonly IAppPaths _paths;
    private readonly HcgConnectionCheck _check;
    private bool _applyingOrganisation;

    public AccountViewModel(HcgAccountService account, AppAccessService access, AccountPlanService plan, IPlatformShell shell, IAppPaths paths, HcgConnectionCheck check)
    {
        _check = check;
        _account = account;
        _access = access;
        _plan = plan;
        _shell = shell;
        _paths = paths;
        _account.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _access.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _plan.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public override string Title => "Account";
    public override string Subtitle => "Your HCG account: sign-in, plan, organisation and the access HCG grants to HCG Executable Maker.";

    [ObservableProperty] private StatusTag _accountTag = new("—", "neutral");
    [ObservableProperty] private string _accountHeadline = "";
    [ObservableProperty] private string _accountMessage = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _accountId = "";
    [ObservableProperty] private string _sessionDetail = "";
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignInCommand))] private bool _canSignIn;
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private bool _isNotConfigured;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private string _configurationHelp = "";
    [ObservableProperty] private StatusTag _accessTag = new("—", "neutral");
    [ObservableProperty] private string _accessMessage = "";
    [ObservableProperty] private bool _hasOrganisations;
    [ObservableProperty] private bool _needsOrganisation;
    [ObservableProperty] private EcosystemOrganisation? _selectedOrganisation;
    public ObservableCollection<EcosystemOrganisation> Organisations { get; } = [];
    public ObservableCollection<AccountPermissionRow> Permissions { get; } = [];

    // Plan and access, as HCG's central API reports them (never decided or assumed by the app).
    [ObservableProperty] private StatusTag _planTag = new("—", "neutral");
    [ObservableProperty] private string _planName = "";
    [ObservableProperty] private string _planMessage = "";
    [ObservableProperty] private string _validUntil = "";
    [ObservableProperty] private string _planChecked = "";
    [ObservableProperty] private bool _hasManageLink;
    [ObservableProperty] private bool _hasCheckoutLink;
    [ObservableProperty] private string _noBillingNote = "";
    [ObservableProperty] private bool _hasChange;
    [ObservableProperty] private string _changeMessage = "";
    [ObservableProperty] private string _changeTone = "info";
    [ObservableProperty] private bool _usesLocalIdentityServer;
    public ObservableCollection<AccountPermissionRow> Features { get; } = [];

    // HCG Connect configuration: what this build uses, importing the issued file, and checking it against HCG.
    [ObservableProperty] private StatusTag _connectTag = new("—", "neutral");
    [ObservableProperty] private string _connectSummary = "";
    [ObservableProperty] private string _registrationJson = "";
    [ObservableProperty] private string? _connectImportError;
    [ObservableProperty] private string? _connectImportMessage;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckConnectionCommand))] private bool _isCheckingConnection;
    [ObservableProperty] private string _connectionVerdict = "";
    [ObservableProperty] private bool _connectionReady;
    public ObservableCollection<AccountPermissionRow> ConnectionSteps { get; } = [];
    public string LocalBuildsNote => "Local builds, packaging and toolchains work without an HCG account. Sign-in is needed only for support requests and HCG AI assistance.";
    public string PrivacyNote => "Sign-in happens in your own browser on HCG's sign-in page. HCG Executable Maker never sees your password. It keeps a renewable session in the system credential store and never writes it to a file or a log.";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        Refresh();
        if (!_account.IsSignedIn) return;
        await _plan.RefreshAsync(force: false);
        await _access.RefreshAsync(force: false);
    }

    private void Refresh()
    {
        var s = _account.Status;
        IsSignedIn = _account.IsSignedIn;
        IsSigningIn = s == HcgAccountStatus.SigningIn;
        IsNotConfigured = s == HcgAccountStatus.NotConfigured;
        CanSignIn = s is HcgAccountStatus.SignedOut or HcgAccountStatus.Expired or HcgAccountStatus.Revoked or HcgAccountStatus.Denied
                    || (s == HcgAccountStatus.Unavailable && _account.Identity is null);
        CanRetry = s == HcgAccountStatus.Unavailable && _account.Identity is not null;
        (AccountTag, AccountHeadline) = s switch
        {
            HcgAccountStatus.SignedIn => (new StatusTag("Signed in", "success"), "Signed in with HCG"),
            HcgAccountStatus.SigningIn => (new StatusTag("Waiting for browser", "info"), "Finish signing in in your browser"),
            HcgAccountStatus.Restoring => (new StatusTag("Restoring", "info"), "Restoring your HCG session"),
            HcgAccountStatus.Expired => (new StatusTag("Session expired", "warning"), "Your HCG session has ended"),
            HcgAccountStatus.Revoked => (new StatusTag("Access withdrawn", "error"), "HCG has withdrawn access"),
            HcgAccountStatus.Denied => (new StatusTag("Not allowed", "error"), "Sign-in was not allowed"),
            HcgAccountStatus.Unavailable => (new StatusTag("HCG unavailable", "warning"), "HCG cannot be reached right now"),
            HcgAccountStatus.NotConfigured => (new StatusTag("Not available yet", "neutral"), "HCG sign-in is not available in this version yet"),
            _ => (new StatusTag("Not signed in", "neutral"), "Sign in with your HCG account"),
        };
        AccountMessage = _account.Message + (_account.ConfigurationProblem is { } p ? " " + p : "");
        DisplayName = _account.Identity?.DisplayName ?? "";
        Email = _account.Identity?.Email ?? "";
        AccountId = _account.Identity?.Subject ?? "";
        SessionDetail = IsSignedIn
            ? (_account.SessionPersisted ? $"Session stored in: {_account.StoreDescription}" : "Session kept for this run only (not stored)") +
              (_account.SessionExpiresAt is { } e ? $" · renews automatically (current token until {e.LocalDateTime:t})" : "")
            : _account.StoreNote ?? "";
        ConfigurationHelp = IsNotConfigured
            ? "HCG Executable Maker's sign-in client has not been registered with HCG yet, so account features are switched off and nothing is contacted. " +
              $"After registration, HCG provides a configuration file ({HcgConnectConfiguration.FileName}). Import it here; it contains the public client ID only and never a secret."
            : "";
        RefreshConnect();

        (AccessTag, AccessMessage) = _access.State switch
        {
            AppAccessState.Granted => (new StatusTag("Granted by HCG", "success"), _access.Message),
            AppAccessState.Checking => (new StatusTag("Checking", "info"), _access.Message),
            AppAccessState.NotGranted => (new StatusTag("Not granted", "warning"), _access.Message),
            AppAccessState.Revoked => (new StatusTag("Revoked", "error"), _access.Message),
            AppAccessState.Unavailable => (new StatusTag("Unknown (HCG unavailable)", "warning"), _access.Message),
            _ => (new StatusTag("Sign in required", "neutral"), _access.Message),
        };
        RefreshPlan();
        Permissions.Clear();
        foreach (var (perm, title, detail) in new[]
                 {
                     (HcgPermissions.SupportSubmit, "Project support requests", "Describe a project HCG Executable Maker cannot build yet and send diagnostics to HCG."),
                     (HcgPermissions.SupportAttach, "Attach project files", "Include selected files or a project archive with a request."),
                     (HcgPermissions.AiUse, "HCG AI assistance", "Explain build errors and other problems with HCG AI (when HCG AI is connected)."),
                 })
        {
            var granted = _access.Has(perm);
            Permissions.Add(new AccountPermissionRow(title, detail, _access.State != AppAccessState.Granted
                ? new StatusTag("—", "neutral") : granted ? new StatusTag("Allowed", "success") : new StatusTag("Not granted", "neutral")));
        }
        _applyingOrganisation = true;
        Organisations.Clear();
        foreach (var o in _plan.Organisations) Organisations.Add(o);
        HasOrganisations = Organisations.Count > 1;
        NeedsOrganisation = _plan.NeedsOrganisationChoice;
        SelectedOrganisation = Organisations.FirstOrDefault(o => o.Id == _plan.OrganisationId);
        _applyingOrganisation = false;
        SignInCommand.NotifyCanExecuteChanged();
    }

    private static readonly Dictionary<string, (string Title, string Detail)> KnownFeatures = new(StringComparer.Ordinal)
    {
        [HcgPermissions.SupportSubmit] = ("Project support requests", "Send diagnostics about a project HCG Executable Maker cannot build yet."),
        [HcgPermissions.SupportAttach] = ("Attach project files", "Include selected files or a project archive with a request."),
        [HcgPermissions.AiUse] = ("HCG AI assistance", "Explain build errors with HCG AI (when HCG AI is connected)."),
    };

    private void RefreshPlan()
    {
        UsesLocalIdentityServer = _account.Configuration.UsesLocalIdentityServer;
        (PlanTag, PlanMessage) = _plan.State switch
        {
            PlanAccessState.Granted => (new StatusTag("Active", "success"), _plan.Message),
            PlanAccessState.Checking => (new StatusTag("Checking", "info"), _plan.Message),
            PlanAccessState.NotGranted => (new StatusTag(_plan.NeedsOrganisationChoice ? "Choose organisation" : "Not granted", "warning"), _plan.Message),
            PlanAccessState.Expired => (new StatusTag("Expired", "warning"), _plan.Message),
            PlanAccessState.Suspended => (new StatusTag("Suspended", "error"), _plan.Message),
            PlanAccessState.Unavailable => (new StatusTag("Unknown (HCG unavailable)", "warning"), _plan.Message),
            _ => (new StatusTag("Sign in required", "neutral"), _plan.Message),
        };
        PlanName = _plan.State == PlanAccessState.SignedOut ? "—" : _plan.PlanDisplay;
        ValidUntil = _plan.ValidUntil is { } until ? until.LocalDateTime.ToString("D") : _plan.Access?.Licence is null ? "Not reported by HCG" : "No end date";
        PlanChecked = _plan.CheckedAt is { } at ? $"Checked with HCG at {at.LocalDateTime:t}" : "";
        HasManageLink = _plan.Access?.Links.Manage is not null;
        HasCheckoutLink = _plan.Access?.Links.Checkout is not null;
        NoBillingNote = _plan.State != PlanAccessState.SignedOut && !HasManageLink && !HasCheckoutLink
            ? "HCG provides no plan-management or purchase link for HCG Executable Maker yet. Plans and licences are managed by HCG."
            : "";
        HasChange = _plan.ChangeState != PlanChangeState.None;
        ChangeMessage = _plan.ChangeMessage;
        ChangeTone = _plan.ChangeState switch { PlanChangeState.Confirmed => "success", PlanChangeState.NotConfirmed => "warning", _ => "info" };
        Features.Clear();
        foreach (var f in _plan.Access?.Features ?? [])
        {
            var (title, detail) = KnownFeatures.TryGetValue(f.Key, out var k) ? k : (f.DisplayName ?? f.Key, f.Key);
            Features.Add(new AccountPermissionRow(title, detail, f.Allowed && _plan.State == PlanAccessState.Granted
                ? new StatusTag("Allowed", "success") : new StatusTag("Not allowed", "neutral")));
        }
    }

    partial void OnSelectedOrganisationChanged(EcosystemOrganisation? value)
    {
        if (_applyingOrganisation || value is null || value.Id == _plan.OrganisationId) return;
        _ = SwitchOrganisationAsync(value.Id);
    }

    private async Task SwitchOrganisationAsync(string id)
    {
        await _plan.SelectOrganisationAsync(id); // access may differ per organisation; HCG decides
        await _access.RefreshAsync();
    }

    [RelayCommand]
    private async Task CheckPlanAsync()
    {
        IsBusy = true;
        try { await _plan.RefreshAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenManageAsync()
    {
        if (_plan.Access?.Links.Manage is not { } url) return;
        _plan.BeginPlanChange();
        await _shell.OpenUrlAsync(url.ToString());
    }

    [RelayCommand]
    private async Task OpenCheckoutAsync()
    {
        if (_plan.Access?.Links.Checkout is not { } url) return;
        _plan.BeginPlanChange();
        await _shell.OpenUrlAsync(url.ToString());
    }

    [RelayCommand]
    private Task OpenHcgAccountAsync() => _shell.OpenUrlAsync(_plan.AccountLink.ToString());

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        await _account.SignInAsync(new ShellBrowser(_shell));
        if (!_account.IsSignedIn) return;
        await _plan.RefreshAsync();
        await _access.RefreshAsync();
    }

    [RelayCommand] private void CancelSignIn() => _account.CancelSignIn();

    [RelayCommand]
    private async Task SignOutAsync() => await _account.SignOutAsync();

    [RelayCommand]
    private async Task RefreshAccessAsync()
    {
        IsBusy = true;
        try { await _access.RefreshAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        await _account.RetryAsync();
        if (!_account.IsSignedIn) return;
        await _plan.RefreshAsync();
        await _access.RefreshAsync();
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => _shell.OpenFolderAsync(_paths.Root);

    private void RefreshConnect()
    {
        var c = _account.Configuration;
        var file = Path.Combine(_paths.Root, HcgConnectConfiguration.FileName);
        var source = _account.ConfigurationProblem is not null ? $"built-in defaults ({HcgConnectConfiguration.FileName} was ignored)"
            : File.Exists(file) ? file : "built-in defaults (no " + HcgConnectConfiguration.FileName + " imported)";
        ConnectTag = c.MissingForSignIn().Count > 0 ? new StatusTag("Not configured", "neutral")
            : c.UsesLocalIdentityServer ? new StatusTag("Local test server", "warning")
            : new StatusTag("Configured, not verified", "info");
        if (_account.IsSignedIn && !c.UsesLocalIdentityServer) ConnectTag = new StatusTag("Signed in with HCG", "success");
        ConnectSummary = string.Join("\n", new[]
        {
            $"Client ID: {(string.IsNullOrWhiteSpace(c.ClientId) ? "(none: not registered yet)" : c.ClientId)} (public client, PKCE S256, no secret)",
            $"Identity server: {c.Issuer}",
            $"Redirect URIs: {string.Join(", ", c.RedirectUris)}",
            $"Scopes: {string.Join(" ", c.Scopes)}",
            $"HCG APIs: {c.ApiBaseUrl}  ({c.EcosystemApiPath}, {c.ApiPath})",
            $"Source: {source}",
        });
        RegistrationJson = c.RegistrationRequestJson();
    }

    /// <summary>Imports the hcg-connect.json HCG issued: validated first; on any problem nothing changes.</summary>
    [RelayCommand]
    private async Task ImportConfigurationAsync()
    {
        ConnectImportError = ConnectImportMessage = null;
        if (await _shell.PickFileAsync("Import the HCG Connect configuration", "HCG Connect configuration", "*.json") is not { } file) return;
        var result = HcgConnectImport.Import(file, _paths.Root);
        if (result.Error is not null) { ConnectImportError = result.Error; return; }
        var previousClient = _account.Configuration.ClientId;
        await _account.ApplyConfigurationAsync(result.Configuration!);
        ConnectImportMessage = $"Imported: client {result.Configuration!.ClientId} on {result.Configuration.Issuer.Host}" +
            (previousClient is not null && previousClient != result.Configuration.ClientId && previousClient.Length > 0 ? " (the previous client's session was removed from this computer)" : "") +
            ". It is in use now. Check the connection, then sign in; only a completed sign-in proves it works.";
        ConnectionSteps.Clear();
        ConnectionVerdict = "";
        Refresh();
    }

    private bool CanCheckConnection() => !IsCheckingConnection;

    [RelayCommand(CanExecute = nameof(CanCheckConnection))]
    private async Task CheckConnectionAsync()
    {
        IsCheckingConnection = true;
        ConnectionSteps.Clear();
        ConnectionVerdict = "Checking…";
        try
        {
            var result = await _check.RunAsync(_account.Configuration);
            foreach (var step in result.Steps)
                ConnectionSteps.Add(new AccountPermissionRow(step.Name, step.Detail, step.State switch
                {
                    ConnectionCheckState.Passed => new StatusTag("Passed", "success"),
                    ConnectionCheckState.Warning => new StatusTag("Not available", "warning"),
                    ConnectionCheckState.Failed => new StatusTag("Failed", "error"),
                    _ => new StatusTag("Not checked", "neutral"),
                }));
            ConnectionReady = result.ReadyToSignIn;
            ConnectionVerdict = result.ReadyToSignIn
                ? "Sign-in can be attempted with this configuration. The check does not sign in: only a completed sign-in proves it works."
                : "Sign-in cannot work with this configuration yet. The failed check says what is missing.";
        }
        finally { IsCheckingConnection = false; }
    }

    [RelayCommand]
    private Task CopyRegistrationAsync() => _shell.CopyAsync(RegistrationJson);
}

/// <summary>Opens HCG's sign-in page in the user's default browser (never an embedded view).</summary>
public sealed class ShellBrowser(IPlatformShell shell) : ISystemBrowser
{
    public Task<bool> OpenAsync(Uri url) => shell.OpenUrlAsync(url.ToString());
}
