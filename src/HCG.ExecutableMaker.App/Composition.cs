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
    public static ServiceProvider Build(IAppPaths paths)
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
        services.AddSingleton<IToolchainDetector, ToolchainDetector>();
        services.AddSingleton<IToolchainService, ToolchainService>();

        // Import & inspection
        services.AddSingleton<IProjectImporter, ProjectImporter>();
        services.AddSingleton<IProjectInspector, DotNetProjectInspector>();
        services.AddSingleton<IProjectInspector, HCG.ExecutableMaker.ProjectInspection.Mobile.MobileProjectInspector>();
        services.AddSingleton<IProjectInspector, HCG.ExecutableMaker.ProjectInspection.Mobile.MobileArtifactProjectInspector>();
        foreach (var inspector in EcosystemSignatureInspector.CreateDefaults(TimeProvider.System))
            services.AddSingleton(inspector);
        services.AddSingleton<IProjectInspectionService, ProjectInspectionService>();
        services.AddSingleton<ProjectService>();

        // Build & packaging
        services.AddSingleton<IBuildWorker, LocalProcessBuildWorker>();
        services.AddSingleton<IPackager, PortableFolderPackager>();
        services.AddSingleton<IPackager, PortableZipPackager>();
        services.AddSingleton<IPackager, MacAppBundlePackager>();
        services.AddSingleton<IPackager, DmgPackager>();
        services.AddSingleton<PackagingRegistry>();
        services.AddSingleton<BuildService>();
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
        return services.BuildServiceProvider();
    }
}
