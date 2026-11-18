using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed record AccountPermissionRow(string Title, string Detail, StatusTag State);

/// <summary>One feature and HCG's decision for it, with the explanation and what the person can do.</summary>
public sealed record AccountFeatureRow(FeatureDecision Decision)
{
    public string Title => Decision.Feature.Title;
    public string Detail => Decision.Explanation;
    public string Remedy => Decision.RemedyText;
    public bool HasRemedy => Remedy.Length > 0;
    public StatusTag State => new(Decision.StateLabel, Decision.Tone);
    /// <summary>Only when a licence is what is missing: the product's page in HCG Store. Never for staff permissions.</summary>
    public Uri? StoreLink => Decision.Remedy == FeatureRemedy.ViewInStore ? Decision.Link : null;
    public bool HasStoreLink => StoreLink is not null;
}

/// <summary>
/// Account: Sign in with HCG (system browser), profile, organisation, the access HCG granted, sign out. Every state is
/// explained (not configured, signing in, expired, revoked, denied, HCG unavailable). Local builds never need this.
/// </summary>
public sealed partial class AccountViewModel : PageViewModel
{
    private readonly HcgAccountService _account;
    private readonly FeatureAccessService _access;
    private readonly AccountPlanService _plan;
    private readonly IPlatformShell _shell;
    private readonly IAppPaths _paths;
    private readonly HcgConnectionCheck _check;
    private bool _applyingOrganisation;

    public AccountViewModel(HcgAccountService account, FeatureAccessService access, AccountPlanService plan, IPlatformShell shell, IAppPaths paths, HcgConnectionCheck check)
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
    public ObservableCollection<AccountFeatureRow> Permissions { get; } = [];

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
    /// <summary>Exactly which configuration fields are missing or invalid on this computer.</summary>
    public ObservableCollection<ConfigurationIssue> ConfigIssues { get; } = [];
    [ObservableProperty] private bool _hasConfigIssues;
    [ObservableProperty] private bool _isClientRejected;
    [ObservableProperty] private string _organisationSummary = "";
    [ObservableProperty] private string? _exportMessage;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RetryConnectionCommand))] private bool _isRetrying;
    public string LocalBuildsNote => "Local builds, packaging and toolchains work without an HCG account. Sign-in is needed only for support requests and HCG AI assistance.";
    public string PrivacyNote => "Sign-in happens in your own browser on HCG's sign-in page. HCG Executable Maker never sees your password. It keeps a renewable session in the system credential store and never writes it to a file or a log.";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        Refresh();
        if (!_account.IsSignedIn) return;
        await _access.RefreshAsync("account page", force: false);
    }

    private void Refresh()
    {
        var s = _account.Status;
        IsSignedIn = _account.IsSignedIn;
        IsSigningIn = s == HcgAccountStatus.SigningIn;
        IsNotConfigured = s == HcgAccountStatus.NotConfigured;
        IsClientRejected = s == HcgAccountStatus.ClientRejected;
        CanSignIn = s is HcgAccountStatus.SignedOut or HcgAccountStatus.Expired or HcgAccountStatus.Revoked or HcgAccountStatus.Denied or HcgAccountStatus.ClientRejected
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
            HcgAccountStatus.NotConfigured => (new StatusTag("Not set up on this computer", "neutral"), "The HCG connection is not set up on this computer"),
            HcgAccountStatus.ClientRejected => (new StatusTag("Rejected by HCG", "error"), "HCG's identity service rejected this app's sign-in client"),
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
            ? "Sign-in needs this app's public client ID from HCG, and none is configured on this computer or built into this version, so nothing is contacted. " +
              $"Import the {HcgConnectConfiguration.FileName} HCG provides (public values only, never a secret). Whether HCG has registered the client is checked with HCG's answer, not assumed — use Check configuration after importing. " +
              "Until then, Export registration request gives HCG the exact client to register."
            : IsClientRejected
            ? "This is HCG's own answer about the configured client ID. Compare the client ID with the one HCG issued (ERP › App integrations); if none was issued, send HCG the exported registration request. Import the corrected file, then Retry connection."
            : "";
        ConfigIssues.Clear();
        foreach (var issue in _account.Configuration.Issues()) ConfigIssues.Add(issue);
        if (_account.ConfigurationProblem is { } prob) ConfigIssues.Insert(0, new ConfigurationIssue(HcgConnectConfiguration.FileName, prob));
        HasConfigIssues = ConfigIssues.Count > 0;
        OrganisationSummary = !IsSignedIn ? ""
            : _plan.Organisations.Count == 0 ? "Organisation: none reported by HCG (personal account)."
            : _plan.OrganisationId is { } oid ? $"Organisation: {_plan.Organisations.First(o => o.Id == oid).Name}" + (_plan.Organisations.Count > 1 ? $" (one of {_plan.Organisations.Count} available)" : "")
            : $"Organisation: not chosen ({_plan.Organisations.Count} available).";
        RefreshConnect();

        var decisions = _access.DecideAll();
        var central = decisions.Where(d => d.Feature.Kind == FeatureKind.Account).ToList();
        (AccessTag, AccessMessage) = _access.Status switch
        {
            FeatureAccessStatus.Ready => central.Any(d => d.IsAllowed)
                ? (new StatusTag("Decided by HCG", "success"), _access.StatusMessage)
                : (new StatusTag("Nothing enabled", "warning"), _access.StatusMessage + " HCG enables no account feature here yet."),
            FeatureAccessStatus.Checking => (new StatusTag("Checking", "info"), _access.StatusMessage),
            FeatureAccessStatus.Unavailable => (new StatusTag("HCG unavailable", "warning"), _access.StatusMessage),
            FeatureAccessStatus.ChooseOrganisation => (new StatusTag("Choose organisation", "warning"), _access.StatusMessage),
            FeatureAccessStatus.NoOrganisation or FeatureAccessStatus.NotMember => (new StatusTag("No organisation access", "warning"), _access.StatusMessage),
            FeatureAccessStatus.NotConfigured => (new StatusTag("Not set up", "neutral"), _access.StatusMessage),
            _ => (new StatusTag("Sign in required", "neutral"), _access.StatusMessage),
        };
        RefreshPlan();
        Permissions.Clear();
        foreach (var d in decisions) Permissions.Add(new AccountFeatureRow(d));
        _applyingOrganisation = true;
        Organisations.Clear();
        foreach (var o in _plan.Organisations) Organisations.Add(o);
        HasOrganisations = Organisations.Count > 1;
        NeedsOrganisation = _plan.NeedsOrganisationChoice;
        SelectedOrganisation = Organisations.FirstOrDefault(o => o.Id == _plan.OrganisationId);
        _applyingOrganisation = false;
        SignInCommand.NotifyCanExecuteChanged();
    }

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
        // Licence records HCG holds for HCG Executable Maker in this organisation (display only; access is decided per feature).
        Features.Clear();
        foreach (var l in _plan.Access?.Licences ?? [])
        {
            var applies = l.AppliesTo(FeatureAccessService.EditionKey);
            var title = $"Licence · {l.Scope ?? "unknown scope"}{(l.ScopeKey is { } k ? " " + k : "")}";
            var detail = (l.Type ?? "type not reported") + (l.StartsAt is { } s ? $" · from {s.LocalDateTime:d}" : "") +
                         (l.EndsAt is { } e ? $" · until {e.LocalDateTime:d}" : " · no end date") + (applies ? "" : " · another edition: does not apply to the desktop app");
            var tone = !applies ? "neutral" : l.Status switch { "active" => "success", "expired" => "warning", "revoked" or "suspended" => "error", _ => "neutral" };
            Features.Add(new AccountPermissionRow(title, detail, new StatusTag(l.Status ?? "status not reported", tone)));
        }
    }

    partial void OnSelectedOrganisationChanged(EcosystemOrganisation? value)
    {
        if (_applyingOrganisation || value is null || value.Id == _plan.OrganisationId) return;
        _ = SwitchOrganisationAsync(value.Id);
    }

    private async Task SwitchOrganisationAsync(string id)
    {
        await _plan.SelectOrganisationAsync(id); // access may differ per organisation; HCG decides (re-read inside)
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
        // Signing in makes the shared feature-access service ask HCG (it listens to the account).
        await _account.SignInAsync(new ShellBrowser(_shell));
    }

    [RelayCommand] private void CancelSignIn() => _account.CancelSignIn();

    [RelayCommand]
    private async Task SignOutAsync() => await _account.SignOutAsync();

    [RelayCommand]
    private async Task RefreshAccessAsync()
    {
        IsBusy = true;
        try { await _access.RefreshAsync("check again"); }
        finally { IsBusy = false; }
    }

    /// <summary>The product's HCG Store page, offered only where a missing licence is the reason.</summary>
    [RelayCommand]
    private Task OpenStorePageAsync(AccountFeatureRow row) => row.StoreLink is { } u ? _shell.OpenUrlAsync(u.ToString()) : Task.CompletedTask;

    [RelayCommand]
    private async Task RetryAsync()
    {
        await _account.RetryAsync();
        if (!_account.IsSignedIn) return;
        await _access.RefreshAsync("retry");
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => _shell.OpenFolderAsync(_paths.Root);

    private void RefreshConnect()
    {
        var c = _account.Configuration;
        var file = Path.Combine(_paths.Root, HcgConnectConfiguration.FileName);
        var source = HcgConnectConfiguration.DescribeSource(c, File.Exists(file) ? file : null, _account.ConfigurationProblem is not null);
        ConnectTag = c.MissingForSignIn().Count > 0 ? new StatusTag("Not configured", "neutral")
            : c.UsesLocalIdentityServer ? new StatusTag("Local test server", "warning")
            : new StatusTag("Configured, not verified", "info");
        if (_account.IsSignedIn && !c.UsesLocalIdentityServer) ConnectTag = new StatusTag("Signed in with HCG", "success");
        ConnectSummary = string.Join("\n", new[]
        {
            $"Client ID: {(string.IsNullOrWhiteSpace(c.ClientId) ? "(none configured on this computer)" : c.ClientId)} (public client, PKCE S256, no secret)",
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
            // Only HCG's own answer about the client changes the account state.
            if (result.Steps.FirstOrDefault(x => x.ClientRejectedByHcg) is { } rejected) _account.MarkClientRejected(rejected.Detail);
            else if (result.ReadyToSignIn) _account.ClearClientRejection();
            Refresh();
            ConnectionVerdict = result.ReadyToSignIn
                ? "Sign-in can be attempted with this configuration. The check does not sign in: only a completed sign-in proves it works."
                : "Sign-in cannot work with this configuration yet. The failed check says what is missing.";
        }
        finally { IsCheckingConnection = false; }
    }

    [RelayCommand]
    private Task CopyRegistrationAsync() => _shell.CopyAsync(RegistrationJson);

    private bool CanRetryConnection() => !IsRetrying;

    /// <summary>
    /// Re-reads hcg-connect.json from the data folder and applies it (a file replaced by hand counts too), restores a
    /// stored session for the same client, and re-reads plan and access from HCG.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetryConnection))]
    private async Task RetryConnectionAsync()
    {
        IsRetrying = true;
        try
        {
            await _account.ReloadConfigurationAsync(_paths.Root);
            if (_account.Status == HcgAccountStatus.Unavailable) await _account.RetryAsync();
            if (_account.IsSignedIn)
            {
                await _plan.RefreshAsync();
                await _access.RefreshAsync();
            }
        }
        finally { IsRetrying = false; Refresh(); }
    }

    /// <summary>
    /// Writes the registration request HCG needs (derived from this build's callback URIs, scopes and flow) and a
    /// non-secret hcg-connect.json template with exactly the keys the loader accepts. Nothing is sent anywhere.
    /// </summary>
    [RelayCommand]
    private async Task ExportRegistrationAsync()
    {
        ExportMessage = null;
        if (await _shell.PickFolderAsync("Choose where to save the HCG registration request") is not { } folder) return;
        var files = HcgRegistrationExport.Write(folder, _account.Configuration);
        ExportMessage = $"Saved {string.Join(", ", files.Select(Path.GetFileName))} in {folder}. Send the registration request to HCG; nothing was sent from here.";
    }
}

/// <summary>Opens HCG's sign-in page in the user's default browser (never an embedded view).</summary>
public sealed class ShellBrowser(IPlatformShell shell) : ISystemBrowser
{
    public Task<bool> OpenAsync(Uri url) => shell.OpenUrlAsync(url.ToString());
}
