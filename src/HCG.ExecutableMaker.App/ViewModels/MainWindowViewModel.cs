using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed record NavItem(string Key, string Title, string IconKey, Type PageType);

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly IToolchainService _toolchains;
    private bool _suppressSelection;

    public MainWindowViewModel(IServiceProvider services, NavigationService navigation, IToolchainService toolchains, IAppPaths paths)
    {
        _services = services;
        _toolchains = toolchains;
        DataDirectory = paths.Root;
        navigation.NavigationRequested += (key, parameter) => _ = NavigateAsync(key, parameter);
        toolchains.Updated += (_, report) => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateFooter(report));
        NavItems =
        [
            new("dashboard", "Dashboard", "Icon.Dashboard", typeof(DashboardViewModel)),
            new("new-build", "New Build", "Icon.NewBuild", typeof(BuildWizardViewModel)),
            new("mobile", "Mobile Build Studio", "Icon.Mobile", typeof(MobileBuildStudioViewModel)),
            new("porting", "Porting Studio", "Icon.Repeat", typeof(PortingStudioViewModel)),
            new("projects", "Projects", "Icon.Projects", typeof(ProjectsViewModel)),
            new("history", "Build History", "Icon.History", typeof(BuildHistoryViewModel)),
            new("artifacts", "Artifacts", "Icon.Artifacts", typeof(ArtifactsViewModel)),
            new("toolchains", "Toolchains", "Icon.Toolchains", typeof(ToolchainsViewModel)),
            new("security", "Signing & Security", "Icon.Security", typeof(SigningViewModel)),
            new("ai", "AI assistance", "Icon.Ai", typeof(AiAssistantViewModel)),
            new("support", "Help & Support", "Icon.Support", typeof(SupportViewModel)),
            new("requests", "My requests", "Icon.Requests", typeof(MyRequestsViewModel)),
            new("account", "Account", "Icon.Account", typeof(AccountViewModel)),
            new("settings", "Settings", "Icon.Settings", typeof(SettingsViewModel)),
        ];
    }

    public ObservableCollection<NavItem> NavItems { get; }
    public string AppVersion => $"Version {HostInfo.ProductVersion} · {HostInfo.ProductCommit}";
    public string MilestoneLabel => "Windows installer · Mobile Build Studio";
    public string DataDirectory { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private string _engineStatus = "Detecting build engine…";
    [ObservableProperty] private string _engineTone = "neutral";
    [ObservableProperty] private string _environmentStatus = HostInfo.Detect().DisplayName;

    public async Task InitializeAsync()
    {
        // HCG's own Xcode choice (DEVELOPER_DIR for HCG's processes) must be in place before anything probes tools.
        try { await _services.GetRequiredService<HCG.ExecutableMaker.Infrastructure.Toolchains.DeveloperDirectorySelection>().InitializeAsync(); }
        catch (Exception ex) { EngineStatus = "Could not load the Xcode selection: " + ex.Message; EngineTone = "warning"; }
        await NavigateAsync(NavItems.Any(n => n.Key == Program.StartPage) ? Program.StartPage : "dashboard", null);
        // Restore a saved HCG session (account features only; local builds never wait for it).
        _ = Task.Run(async () =>
        {
            try
            {
                var account = _services.GetRequiredService<HCG.ExecutableMaker.Account.HcgAccountService>();
                await account.InitializeAsync();
                if (account.IsSignedIn)
                {
                    await _services.GetRequiredService<HCG.ExecutableMaker.Account.AccountPlanService>().RefreshAsync();
                    await _services.GetRequiredService<HCG.ExecutableMaker.Account.AppAccessService>().RefreshAsync();
                }
            }
            catch (Exception) { /* reported on the Account page */ }
        });
        try { await _toolchains.GetAsync(); }
        catch (Exception ex) { EngineStatus = "Toolchain detection failed: " + ex.Message; EngineTone = "error"; }
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (!_suppressSelection && value is not null) _ = NavigateAsync(value.Key, null);
    }

    public async Task NavigateAsync(string key, object? parameter)
    {
        var item = NavItems.FirstOrDefault(n => n.Key == key) ?? NavItems[0];
        var page = (PageViewModel)_services.GetRequiredService(item.PageType);
        _suppressSelection = true;
        SelectedNav = item;
        _suppressSelection = false;
        CurrentPage = page;
        try { await page.OnNavigatedToAsync(parameter); }
        catch (Exception ex) { page.ErrorMessage = ex.Message; }
    }

    private void UpdateFooter(ToolchainReport report)
    {
        var sdk = report.Find(ToolchainIds.DotNetSdk);
        var swift = report.Host.Platform == TargetPlatform.MacOS ? report.Find(ToolchainIds.Swift) : null;
        (EngineStatus, EngineTone) = (sdk?.IsAvailable == true, swift?.IsAvailable == true) switch
        {
            (true, true) => ($".NET SDK {sdk!.Version} · Swift {swift!.Version} ready", "success"),
            (true, false) => ($".NET SDK {sdk!.Version} ready", "success"),
            (false, true) => ($"Swift {swift!.Version} ready · no .NET SDK (.NET builds unavailable)", "warning"),
            _ => ("No .NET SDK — builds unavailable", "warning"),
        };
        EnvironmentStatus = $"{report.Host.DisplayName} · {Format.Bytes(report.AvailableDiskBytes)} free";
    }
}
