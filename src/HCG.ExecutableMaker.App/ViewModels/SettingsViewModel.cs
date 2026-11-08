using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class SettingsViewModel(
    ISettingsStore store,
    ThemeService themes,
    IAppPaths paths,
    IPlatformShell shell,
    IDialogService dialogs,
    HCG.ExecutableMaker.BuildEngine.MacDistribution.AppleCredentialsService credentials) : PageViewModel
{
    public override string Title => "Settings";
    public override string Subtitle => "Appearance, import safety limits, build defaults and data locations.";

    public IReadOnlyList<ThemeOption> Themes => ThemeService.Themes;
    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private decimal? _maxImportGb;
    [ObservableProperty] private decimal? _maxFileGb;
    [ObservableProperty] private decimal? _maxFiles;
    [ObservableProperty] private decimal? _timeoutMinutes;
    [ObservableProperty] private string _defaultOutput = "";
    [ObservableProperty] private string? _statusMessage;

    public string DataRoot => paths.Root;
    public string DatabasePath => paths.DatabasePath;
    public string WorkspacesPath => paths.WorkspacesDirectory;
    public string LogsPath => paths.LogsDirectory;
    public string Version => HostInfo.ProductVersion;
    public string Commit => HostInfo.ProductCommit;
    public string BuildDate => HostInfo.BuildDate;
    public string ExecutablePath => HostInfo.ExecutablePath;
    public string AppBundlePath => HostInfo.AppBundlePath ?? "Not running from an .app bundle";
    public string BuildIdentity => HostInfo.BuildIdentity;

    private AppSettings _loaded = new();

    // ---------- Apple credentials (names / IDs only; secrets stay in the keychain or the key file) ----------
    [ObservableProperty] private string _notaryProfile = "";
    [ObservableProperty] private string _notaryAppleId = "";
    [ObservableProperty] private string _notaryTeamId = "";
    [ObservableProperty] private string _notaryStatus = "";
    [ObservableProperty] private string _ascKeyId = "";
    [ObservableProperty] private string _ascIssuerId = "";
    [ObservableProperty] private string _ascStatus = "";
    public string StoreCredentialsCommand => HCG.ExecutableMaker.BuildEngine.MacDistribution.AppleCredentialsService.StoreCredentialsCommand(
        string.IsNullOrWhiteSpace(NotaryProfile) ? "HCG-Notary" : NotaryProfile.Trim(), NotaryAppleId.Trim(), NotaryTeamId.Trim());
    partial void OnNotaryProfileChanged(string value) => OnPropertyChanged(nameof(StoreCredentialsCommand));
    partial void OnNotaryAppleIdChanged(string value) => OnPropertyChanged(nameof(StoreCredentialsCommand));
    partial void OnNotaryTeamIdChanged(string value) => OnPropertyChanged(nameof(StoreCredentialsCommand));
    partial void OnAscKeyIdChanged(string value) => UpdateAscStatus();
    partial void OnAscIssuerIdChanged(string value) => UpdateAscStatus();

    private void UpdateAscStatus() =>
        AscStatus = HCG.ExecutableMaker.BuildEngine.MacDistribution.AppStoreValidationService.Blocker(AscKeyId.Trim(), AscIssuerId.Trim())
                    ?? "Ready: the key file was found. HCG passes only the key ID and issuer ID to altool.";

    [RelayCommand] private async Task CopyStoreCredentialsCommandAsync() => await shell.CopyAsync(StoreCredentialsCommand);

    [RelayCommand]
    private async Task CheckNotaryProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(NotaryProfile)) { NotaryStatus = "Enter the keychain profile name first."; return; }
        NotaryStatus = "Checking with Apple's notary service…";
        var (_, detail) = await credentials.CheckNotaryProfileAsync(NotaryProfile.Trim(), CancellationToken.None);
        NotaryStatus = detail;
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _loaded = await store.LoadAsync();
        SelectedTheme = Themes.FirstOrDefault(t => t.Id == _loaded.ThemeId) ?? Themes[0];
        MaxImportGb = Math.Round((decimal)_loaded.ImportLimits.MaxTotalBytes / (1L << 30), 2);
        MaxFileGb = Math.Round((decimal)_loaded.ImportLimits.MaxFileBytes / (1L << 30), 2);
        MaxFiles = _loaded.ImportLimits.MaxFileCount;
        TimeoutMinutes = _loaded.BuildTimeoutMinutes;
        DefaultOutput = _loaded.DefaultOutputDirectory ?? paths.DefaultOutputDirectory;
        NotaryProfile = _loaded.NotaryKeychainProfile ?? "";
        AscKeyId = _loaded.AppStoreConnectKeyId ?? "";
        AscIssuerId = _loaded.AppStoreConnectIssuerId ?? "";
        NotaryStatus = string.IsNullOrEmpty(NotaryProfile) ? "Not set up: notarization is unavailable until a keychain profile is stored and named here." : "";
        UpdateAscStatus();
        StatusMessage = null;
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value is not null && value.Id != themes.CurrentId) themes.Apply(value.Id);
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (await shell.PickFolderAsync("Default output directory") is { } dir) DefaultOutput = dir;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (MaxImportGb is not > 0 || MaxFileGb is not > 0 || MaxFiles is not >= 1 || TimeoutMinutes is not (>= 1 and <= 720))
        {
            await dialogs.AlertAsync("Invalid settings", "Limits must be positive and the build timeout must be between 1 and 720 minutes.");
            return;
        }
        if (!Path.IsPathFullyQualified(DefaultOutput))
        {
            await dialogs.AlertAsync("Invalid output directory", "The default output directory must be an absolute path.");
            return;
        }
        _loaded = _loaded with
        {
            ThemeId = SelectedTheme?.Id ?? "hcg-future",
            ImportLimits = new ImportLimits
            {
                MaxTotalBytes = (long)(MaxImportGb.Value * (1L << 30)),
                MaxFileBytes = (long)(MaxFileGb.Value * (1L << 30)),
                MaxFileCount = (int)MaxFiles.Value,
                MaxCompressionRatio = _loaded.ImportLimits.MaxCompressionRatio,
            },
            BuildTimeoutMinutes = (int)TimeoutMinutes.Value,
            DefaultOutputDirectory = DefaultOutput,
            NotaryKeychainProfile = string.IsNullOrWhiteSpace(NotaryProfile) ? null : NotaryProfile.Trim(),
            AppStoreConnectKeyId = string.IsNullOrWhiteSpace(AscKeyId) ? null : AscKeyId.Trim(),
            AppStoreConnectIssuerId = string.IsNullOrWhiteSpace(AscIssuerId) ? null : AscIssuerId.Trim(),
        };
        await store.SaveAsync(_loaded);
        StatusMessage = "Settings saved.";
    }

    [RelayCommand] private async Task OpenDataFolderAsync() => await shell.OpenFolderAsync(paths.Root);
    [RelayCommand] private async Task OpenLogsFolderAsync() => await shell.OpenFolderAsync(paths.LogsDirectory);
}
