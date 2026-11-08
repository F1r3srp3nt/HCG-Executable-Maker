using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.Infrastructure.Logging;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HCG.ExecutableMaker.App;

public static class Composition
{
    /// <param name="overrides">Optional replacements applied last (used by the headless UI driver to script file pickers and dialogs).</param>
    public static ServiceProvider Build(IAppPaths paths, Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new FileLoggerProvider(paths.LogsDirectory)));

        // Persistence
        services.AddSingleton(sp => new SqliteDatabase(sp.GetRequiredService<IAppPaths>()));
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.AddSingleton<IBuildRepository, SqliteBuildRepository>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();

        // Environment & toolchains
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IExecutableLocator, ExecutableLocator>();
        services.AddSingleton<DeveloperDirectorySelection>();
        services.AddSingleton<ToolPathSelection>();
        services.AddSingleton<IToolchainDetector, ToolchainDetector>();
        services.AddSingleton<IToolchainService, ToolchainService>();

        // Import & inspection
        services.AddSingleton<IProjectImporter, ProjectImporter>();
        services.AddSingleton<IProjectInspector, DotNetProjectInspector>();
        services.AddSingleton<IProjectInspector, HCG.ExecutableMaker.ProjectInspection.Mobile.MobileProjectInspector>();
        services.AddSingleton<IProjectInspector, HCG.ExecutableMaker.ProjectInspection.Mobile.MobileArtifactProjectInspector>();
        services.AddSingleton<IProjectInspector, HCG.ExecutableMaker.ProjectInspection.Swift.SwiftProjectInspector>();
        foreach (var inspector in EcosystemSignatureInspector.CreateDefaults(TimeProvider.System))
            services.AddSingleton(inspector);
        services.AddSingleton<IProjectInspectionService, ProjectInspectionService>();
        services.AddSingleton<ProjectService>();

        // Build & packaging
        services.AddSingleton<IBuildWorker, LocalProcessBuildWorker>();
        // Electron packagers first: they handle only builds with Electron options (electron-builder output).
        services.AddSingleton<IPackager, ElectronAppBundlePackager>();
        services.AddSingleton<IPackager, ElectronZipPackager>();
        services.AddSingleton<IPackager, ElectronDmgPackager>();
        services.AddSingleton<IPackager, ElectronFolderPackager>();
        services.AddSingleton<IPackager, PortableFolderPackager>();
        services.AddSingleton<IPackager, PortableZipPackager>();
        services.AddSingleton<IPackager, MacAppBundlePackager>();
        services.AddSingleton<IPackager, DmgPackager>();
        services.AddSingleton<IPackager, SwiftAppBundlePackager>();
        services.AddSingleton<IPackager, SwiftAppZipPackager>();
        services.AddSingleton<IPackager, HCG.ExecutableMaker.Packaging.MacDistribution.MacPkgPackager>();
        services.AddSingleton<IPackager, HCG.ExecutableMaker.Packaging.MacDistribution.MacAppStorePackager>();
        services.AddSingleton<HCG.ExecutableMaker.Packaging.MacDistribution.MacSigningInventoryReader>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.MacDistribution.NotarizationService>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.MacDistribution.PkgTestInstaller>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.MacDistribution.AppStoreValidationService>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.MacDistribution.AppleCredentialsService>();
        services.AddSingleton<IPackager, InnoSetupPackager>();
        services.AddSingleton<PackagingRegistry>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.Swift.SwiftPackageEvaluator>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.Mobile.MobileArtifactVerifier>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.Mobile.MobileBuildService>();

        // UI services
        services.AddSingleton<MainWindowProvider>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IPlatformShell, PlatformShell>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ArtifactExporter>();

        // View models (pages are singletons so a running build survives navigation)
        // HCG account (sign-in, ERP App Access, support requests, HCG AI). Public client: no secret exists anywhere.
        var (hcgConfig, hcgConfigProblem) = HCG.ExecutableMaker.Account.HcgConnectConfiguration.Load(paths.Root);
        var (credentialStore, storeProblem) = HCG.ExecutableMaker.Account.SecureCredentialStores.ForCurrentPlatform();
        var hcgHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        hcgHttp.DefaultRequestHeaders.UserAgent.ParseAdd($"HCGExecutableMaker/{HostInfo.ProductVersion}");
        var uploadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        services.AddSingleton(hcgConfig);
        services.AddSingleton(credentialStore);
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.HcgAuthClient(hcgHttp, hcgConfig, TimeProvider.System));
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.HcgAccountService(hcgConfig, sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgAuthClient>(),
            credentialStore, TimeProvider.System, hcgConfigProblem, storeProblem, sp.GetService<Microsoft.Extensions.Logging.ILogger<HCG.ExecutableMaker.Account.HcgAccountService>>()));
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.HcgApiClient(hcgHttp, sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgAccountService>()));
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.AppAccessService(sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgApiClient>(), TimeProvider.System));
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.Support.SupportService(sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgApiClient>(), uploadHttp));
        // HCG AI: no verified gateway contract exists yet, so the "Not connected" adapter is used (never simulated answers).
        services.AddSingleton<HCG.ExecutableMaker.Account.Ai.IHcgAiGateway, HCG.ExecutableMaker.Account.Ai.NotConnectedAiGateway>();
        services.AddSingleton<HCG.ExecutableMaker.Account.Ai.AiAssistant>();
        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<SupportViewModel>();
        services.AddSingleton<MyRequestsViewModel>();
        services.AddSingleton<AiAssistantViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<BuildWizardViewModel>();
        services.AddSingleton<MobileBuildStudioViewModel>();
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<BuildHistoryViewModel>();
        services.AddSingleton<ArtifactsViewModel>();
        services.AddSingleton<ToolchainsViewModel>();
        services.AddSingleton<SigningViewModel>();
        services.AddSingleton<SettingsViewModel>();
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
