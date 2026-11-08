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
    private readonly IPlatformShell _shell;
    private readonly IAppPaths _paths;
    private bool _applyingOrganisation;

    public AccountViewModel(HcgAccountService account, AppAccessService access, IPlatformShell shell, IAppPaths paths)
    {
        _account = account;
        _access = access;
        _shell = shell;
        _paths = paths;
        _account.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _access.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public override string Title => "Account";
    public override string Subtitle => "Your HCG account: sign-in, organisation and the access HCG grants to HCG Executable Maker.";

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
    [ObservableProperty] private AccessOrganisation? _selectedOrganisation;
    public ObservableCollection<AccessOrganisation> Organisations { get; } = [];
    public ObservableCollection<AccountPermissionRow> Permissions { get; } = [];
    public string LocalBuildsNote => "Local builds, packaging and toolchains work without an HCG account. Sign-in is needed only for support requests and HCG AI assistance.";
    public string PrivacyNote => "Sign-in happens in your own browser on HCG's sign-in page. HCG Executable Maker never sees your password. It keeps a renewable session in the system credential store and never writes it to a file or a log.";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        Refresh();
        if (_account.IsSignedIn) await _access.RefreshAsync(force: false);
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
            ? $"Session stored in: {_account.StoreDescription}{(_account.SessionExpiresAt is { } e ? $" · renews automatically (current token until {e.LocalDateTime:t})" : "")}"
            : _account.StoreNote ?? "";
        ConfigurationHelp = IsNotConfigured
            ? "HCG Executable Maker's sign-in client has not been registered with HCG yet, so account features are switched off and nothing is contacted. " +
              $"After registration, HCG provides a configuration file ({HcgConnectConfiguration.FileName}) for {_paths.Root}. It contains the public client ID only and never a secret."
            : "";

        (AccessTag, AccessMessage) = _access.State switch
        {
            AppAccessState.Granted => (new StatusTag("Granted by HCG", "success"), _access.Message),
            AppAccessState.Checking => (new StatusTag("Checking", "info"), _access.Message),
            AppAccessState.NotGranted => (new StatusTag("Not granted", "warning"), _access.Message),
            AppAccessState.Revoked => (new StatusTag("Revoked", "error"), _access.Message),
            AppAccessState.Unavailable => (new StatusTag("Unknown (HCG unavailable)", "warning"), _access.Message),
            _ => (new StatusTag("Sign in required", "neutral"), _access.Message),
        };
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
        foreach (var o in _access.Directory?.Organisations ?? []) Organisations.Add(o);
        HasOrganisations = Organisations.Count > 1;
        SelectedOrganisation = Organisations.FirstOrDefault(o => o.Id == _account.OrganisationId) ?? Organisations.FirstOrDefault();
        _applyingOrganisation = false;
        SignInCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedOrganisationChanged(AccessOrganisation? value)
    {
        if (_applyingOrganisation || value is null || value.Id == _account.OrganisationId) return;
        _ = SwitchOrganisationAsync(value.Id);
    }

    private async Task SwitchOrganisationAsync(string id)
    {
        await _account.SetOrganisationAsync(id);
        await _access.RefreshAsync(); // permissions may differ per organisation; HCG decides
    }

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        await _account.SignInAsync(new ShellBrowser(_shell));
        if (_account.IsSignedIn) await _access.RefreshAsync();
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
        if (_account.IsSignedIn) await _access.RefreshAsync();
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => _shell.OpenFolderAsync(_paths.Root);
}

/// <summary>Opens HCG's sign-in page in the user's default browser (never an embedded view).</summary>
public sealed class ShellBrowser(IPlatformShell shell) : ISystemBrowser
{
    public Task<bool> OpenAsync(Uri url) => shell.OpenUrlAsync(url.ToString());
}
