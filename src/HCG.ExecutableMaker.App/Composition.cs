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
        services.AddSingleton<IProjectInspector, HCG.ExecutableMaker.ProjectInspection.MacXcode.MacXcodeProjectInspector>();
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
        services.AddSingleton<IPackager, HCG.ExecutableMaker.Packaging.Packagers.JailbreakDebPackager>();
        // C/C++ app bundles (a project's own .app) before the generic packagers.
        services.AddSingleton<IPackager, NativeAppBundlePackager>();
        services.AddSingleton<IPackager, NativeAppZipPackager>();
        // Native macOS apps built with Xcode (the project's own .app) before the generic packagers.
        services.AddSingleton<IPackager, XcodeAppBundlePackager>();
        services.AddSingleton<IPackager, XcodeAppZipPackager>();
        services.AddSingleton<IPackager, XcodeArchivePackager>();
        services.AddSingleton<IPackager, PortableFolderPackager>();
        services.AddSingleton<IPackager, PortableZipPackager>();
        services.AddSingleton<IPackager, MacAppBundlePackager>();
        services.AddSingleton<IPackager, DmgPackager>();
        services.AddSingleton<IPackager, SwiftAppBundlePackager>();
        services.AddSingleton<IPackager, SwiftAppZipPackager>();
        services.AddSingleton<IPackager, HCG.ExecutableMaker.Packaging.MacDistribution.MacPkgPackager>();
        services.AddSingleton<IPackager, HCG.ExecutableMaker.Packaging.MacDistribution.MacAppStorePackager>();
        services.AddSingleton<HCG.ExecutableMaker.Packaging.MacDistribution.MacSigningInventoryReader>();
        // HCG Signing Vault (local): keychain, verified provisioning profiles, project signing settings, pre-build validation.
        services.AddSingleton<HCG.ExecutableMaker.Packaging.Signing.IAppleKeychain, HCG.ExecutableMaker.Packaging.Signing.MacAppleKeychain>();
        services.AddSingleton(_ => HCG.ExecutableMaker.Packaging.Signing.ProvisioningProfileStore.ForCurrentUser());
        services.AddSingleton<HCG.ExecutableMaker.Packaging.Signing.AppleSigningAssetService>();
        services.AddSingleton<IProjectSigningStore, HCG.ExecutableMaker.Infrastructure.Persistence.SqliteProjectSigningStore>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.Signing.SigningPrebuildCheck>();
        // Apple Signing Assistant: effective build settings + Signing Vault + this Mac's assets → signing for each destination.
        services.AddSingleton(sp => new HCG.ExecutableMaker.BuildEngine.Signing.AppleSigningAssistantService(
            sp.GetRequiredService<HCG.ExecutableMaker.Packaging.Signing.AppleSigningAssetService>(), sp.GetRequiredService<IProjectSigningStore>(), TimeProvider.System));
        // HCG Impactor: IPA inspection, signing (codesign with Keychain identities), devices (devicectl), installation, history.
        services.AddSingleton(sp => new HCG.Impactor.Storage.ImpactorStore(sp.GetRequiredService<IAppPaths>().Root));
        services.AddSingleton<HCG.Impactor.ISigningInventorySource>(sp => new HCG.Impactor.Signing.SigningInventorySource(sp.GetRequiredService<HCG.ExecutableMaker.Packaging.Signing.AppleSigningAssetService>()));
        services.AddSingleton<HCG.Impactor.IAppSigner>(sp => new HCG.Impactor.MacOS.MacAppSigner(sp.GetRequiredService<IProcessRunner>()));
        services.AddSingleton<HCG.Impactor.IDeviceService>(sp => new HCG.Impactor.MacOS.MacDeviceService(sp.GetRequiredService<IProcessRunner>()));
        services.AddSingleton<HCG.Impactor.ImpactorService>();
        services.AddSingleton(sp => new HCG.Impactor.MacOS.XcodeProvisioningAssistant(sp.GetRequiredService<IProcessRunner>(),
            Path.Combine(sp.GetRequiredService<IAppPaths>().Root, "impactor"), sp.GetRequiredService<HCG.ExecutableMaker.Packaging.Signing.ProvisioningProfileStore>()));
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.MacDistribution.NotarizationService>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.MacDistribution.PkgTestInstaller>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.MacDistribution.AppStoreValidationService>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.MacDistribution.AppleCredentialsService>();
        services.AddSingleton<IPackager, InnoSetupPackager>();
        services.AddSingleton<PackagingRegistry>();
        // Build Readiness: what a build needs, checked before compilation; installs only with the person's approval.
        // Remote build hosts are whatever IBuildHost implementations are registered (none are, until the Build Host lands).
        services.AddSingleton(sp => new HCG.ExecutableMaker.BuildEngine.Readiness.BuildReadinessService(sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IToolchainService>(), sp.GetServices<IBuildHost>(), TimeProvider.System));
        services.AddSingleton<BuildService>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.Swift.SwiftPackageEvaluator>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.Native.NativeConfigurator>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.Mobile.MobileArtifactVerifier>();
        services.AddSingleton<HCG.ExecutableMaker.BuildEngine.Mobile.MobileBuildService>();
        services.AddSingleton<HCG.ExecutableMaker.Porting.DeviceLauncher>();
        services.AddSingleton<HCG.ExecutableMaker.Porting.PortingService>();

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
        // Build hosts: this computer, and HCG Server's Build Host (hcg.buildhost/1, loopback only; its client token lives in
        // the OS credential store, never in a file). GitHub Actions stays a remote option recorded in the package provenance.
        services.AddSingleton(_ => new HCG.ExecutableMaker.BuildEngine.BuildHost.BuildHostCredentials(credentialStore));
        services.AddSingleton(sp => new HCG.ExecutableMaker.BuildEngine.BuildHost.HcgBuildHostClient(
            HCG.ExecutableMaker.BuildEngine.BuildHost.HcgBuildHostClient.CreateHttpClient(),
            sp.GetRequiredService<HCG.ExecutableMaker.BuildEngine.BuildHost.BuildHostCredentials>(), BuildHostCommand.SettingsFromEnvironment()));
        services.AddSingleton(sp => new HCG.ExecutableMaker.BuildEngine.BuildHost.HcgServerBuildHost(
            sp.GetRequiredService<HCG.ExecutableMaker.BuildEngine.BuildHost.HcgBuildHostClient>(), sp.GetRequiredService<IAppPaths>(), TimeProvider.System,
            sp.GetRequiredService<IBuildRepository>()));
        services.AddSingleton<IBuildHost, LocalBuildHost>();
        services.AddSingleton<IBuildHost>(sp => sp.GetRequiredService<HCG.ExecutableMaker.BuildEngine.BuildHost.HcgServerBuildHost>());
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.HcgAuthClient(hcgHttp, hcgConfig, TimeProvider.System));
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.HcgAccountService(hcgConfig, sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgAuthClient>(),
            credentialStore, TimeProvider.System, hcgConfigProblem, storeProblem, sp.GetService<Microsoft.Extensions.Logging.ILogger<HCG.ExecutableMaker.Account.HcgAccountService>>(),
            sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgConnectionCheck>()));
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.HcgApiClient(hcgHttp, sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgAccountService>()));
        // Connection check: never follows redirects (the authorization request is probed, not opened), 15 s per step.
        services.AddSingleton(_ => new HCG.ExecutableMaker.Account.HcgConnectionCheck(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) }));
        // One shared feature-access service (UI and command line): organisation, features and fresh access checks from
        // HCG's central Ecosystem API; the staff-only publishing permission from HCG Store.
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.HcgEcosystemClient(sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgApiClient>()));
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.FeatureAccessService(sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgEcosystemClient>(), TimeProvider.System)
        {
            StoreAuthority = sp.GetRequiredService<StorePublishingAuthority>(),
        });
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.AccountPlanService(sp.GetRequiredService<HCG.ExecutableMaker.Account.FeatureAccessService>(), TimeProvider.System));
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.Support.SupportService(sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgApiClient>(), uploadHttp,
            sp.GetRequiredService<HCG.ExecutableMaker.Account.FeatureAccessService>()));
        // HCG AI: no verified gateway contract exists yet, so the "Not connected" adapter is used (never simulated answers).
        // Optional account metadata for signing assets (never private keys; the encrypted vault is not available).
        services.AddSingleton(sp => new HCG.ExecutableMaker.Account.Signing.SigningAccountService(sp.GetRequiredService<HCG.ExecutableMaker.Account.HcgApiClient>()));
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
        services.AddSingleton<PortingStudioViewModel>();
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<BuildHistoryViewModel>();
        services.AddSingleton<ArtifactsViewModel>();
        services.AddSingleton<StoreClientFactory>();
        services.AddSingleton<StorePublishingAuthority>();
        services.AddSingleton<ShipToStoreViewModel>();
        services.AddSingleton<UnleashifyViewModel>();
        services.AddSingleton<ImpactorViewModel>();
        services.AddTransient<BuildReadinessPanelViewModel>();
        services.AddSingleton<ToolchainsViewModel>();
        services.AddSingleton<AppleSigningViewModel>();
        services.AddSingleton<SigningViewModel>();
        services.AddSingleton<SettingsViewModel>();
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
