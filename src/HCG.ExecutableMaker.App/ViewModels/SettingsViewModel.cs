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
    IDialogService dialogs) : PageViewModel
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

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _loaded = await store.LoadAsync();
        SelectedTheme = Themes.FirstOrDefault(t => t.Id == _loaded.ThemeId) ?? Themes[0];
        MaxImportGb = Math.Round((decimal)_loaded.ImportLimits.MaxTotalBytes / (1L << 30), 2);
        MaxFileGb = Math.Round((decimal)_loaded.ImportLimits.MaxFileBytes / (1L << 30), 2);
        MaxFiles = _loaded.ImportLimits.MaxFileCount;
        TimeoutMinutes = _loaded.BuildTimeoutMinutes;
        DefaultOutput = _loaded.DefaultOutputDirectory ?? paths.DefaultOutputDirectory;
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
        };
        await store.SaveAsync(_loaded);
        StatusMessage = "Settings saved.";
    }

    [RelayCommand] private async Task OpenDataFolderAsync() => await shell.OpenFolderAsync(paths.Root);
    [RelayCommand] private async Task OpenLogsFolderAsync() => await shell.OpenFolderAsync(paths.LogsDirectory);
}
