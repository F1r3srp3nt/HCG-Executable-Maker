using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class MobileOutputItem(MobileOutputOption option) : ObservableObject
{
    public MobileOutputOption Option { get; } = option;
    public string Name => Option.DisplayName;
    public string Description => Option.Description;
    public string? Reason => Option.Reason;
    public bool HasReason => Option.Reason is not null;
    public bool IsEnabled => Option.IsAvailable;
    public StatusTag Status => Option.IsAvailable ? new StatusTag("Available", "success") : new StatusTag("Unavailable", "warning");
    [ObservableProperty] private bool _isSelected;
}

public sealed class PlatformChoice(TargetPlatform platform, bool supported, string? reason)
{
    public TargetPlatform Platform { get; } = platform;
    public string Name => Platform.DisplayName();
    public bool IsSupported { get; } = supported;
    public string Detail { get; } = reason ?? (supported ? "Supported by this project" : "");
    public override string ToString() => Name;
}

/// <summary>
/// Mobile Build Studio: one page for Android (Gradle/Flutter/.NET MAUI) and iOS (Xcode/Flutter/.NET MAUI) builds and
/// for verifying existing .apk/.aab/.ipa artifacts. Only operations the engine really performs are offered; every
/// unavailable one shows the concrete reason.
/// </summary>
public sealed partial class MobileBuildStudioViewModel : PageViewModel
{
    private const int MaxDisplayedLogLines = 6000;
    private readonly ProjectService _projectService;
    private readonly IProjectRepository _projects;
    private readonly MobileBuildService _builder;
    private readonly MobileArtifactVerifier _verifier;
    private readonly IToolchainService _toolchains;
    private readonly IBuildWorker _worker;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly IPlatformShell _shell;
    private readonly ConcurrentQueue<BuildLogLine> _pendingLog = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _buildCts;
    private ToolchainReport? _report;
    private bool _loading;

    public MobileBuildStudioViewModel(ProjectService projectService, IProjectRepository projects, MobileBuildService builder, MobileArtifactVerifier verifier,
        IToolchainService toolchains, IBuildWorker worker, IAppPaths paths, IDialogService dialogs, IPlatformShell shell)
    {
        _projectService = projectService;
        _projects = projects;
        _builder = builder;
        _verifier = verifier;
        _toolchains = toolchains;
        _worker = worker;
        _paths = paths;
        _dialogs = dialogs;
        _shell = shell;
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FlushLog());
        OutputDirectory = paths.DefaultOutputDirectory;
    }

    public override string Title => "Mobile Build Studio";
    public override string Subtitle => "Android APK / App Bundle and iOS builds from real project toolchains — verified, never faked.";

    public bool IsMacHost => OperatingSystem.IsMacOS();

    public string HonestyNote =>
        "Mobile apps are built from their own Android, iOS, Flutter or .NET MAUI projects. Desktop executables cannot be converted into " +
        "iOS or Android apps, renaming a file is not a conversion, and an APK cannot be turned into an App Bundle. Nothing is uploaded to " +
        "Google Play, TestFlight or App Store Connect.";

    // ───────────── Project ─────────────

    public ObservableCollection<ProjectItem> MobileProjects { get; } = [];
    public ObservableCollection<SummaryRow> ProjectRows { get; } = [];
    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProject), nameof(IsArtifactProject))] private ProjectItem? _selectedProject;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string? _importError;
    [ObservableProperty] private StatusTag _supportTag = new("—", "neutral");
    [ObservableProperty] private string _supportSummary = "";
    public bool HasProject => SelectedProject is not null;
    public bool IsArtifactProject => SelectedProject?.Project.Analysis?.Mobile?.ArtifactPath is not null;
    private MobileAnalysis? Mobile => SelectedProject?.Project.Analysis?.Mobile;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _report = await _toolchains.GetAsync();
        await LoadProjectsAsync(parameter as ProjectRecord);
    }

    private async Task LoadProjectsAsync(ProjectRecord? select = null)
    {
        _loading = true;
        var keep = select?.Id ?? SelectedProject?.Project.Id;
        MobileProjects.Clear();
        foreach (var p in await _projects.ListAsync())
            if (p.Analysis?.Mobile is not null) MobileProjects.Add(new ProjectItem(p));
        _loading = false;
        SelectedProject = MobileProjects.FirstOrDefault(p => p.Project.Id == keep) ?? MobileProjects.FirstOrDefault();
        if (SelectedProject is null) ClearProject();
    }

    partial void OnSelectedProjectChanged(ProjectItem? value)
    {
        if (_loading) return;
        if (value is null) { ClearProject(); return; }
        ApplyProject(value.Project);
    }

    private void ClearProject()
    {
        ProjectRows.Clear();
        Diagnostics.Clear();
        Platforms.Clear();
        Outputs.Clear();
        SupportSummary = "Import an Android, iOS, Flutter or .NET MAUI project — or an .apk / .aab / .ipa to verify.";
        SupportTag = new StatusTag("No project", "neutral");
        OnPropertyChanged(nameof(HasOutputs));
    }

    [ObservableProperty] private string? _portResult;

    [RelayCommand]
    private async Task PortMacProjectAsync() => await PortAsync(null);

    [RelayCommand]
    private async Task PortMacAppAsync()
    {
        PortResult = null;
        var app = await _shell.PickFileAsync("Select the Mac application", "Mac applications", "*.app");
        if (app is null) return;
        var info = Path.Combine(app, "Contents", "Info.plist");
        if (!File.Exists(info))
        {
            PortResult = "Choose a macOS .app containing Contents/Info.plist.";
            return;
        }
        try
        {
            // plutil reads binary and XML Info.plists without launching the application.
            var start = new ProcessStartInfo("/usr/bin/plutil") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in new[] { "-extract", "CFBundleIdentifier", "raw", "-o", "-", "--", info })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Could not start plutil.");
            var id = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || string.IsNullOrEmpty(id))
                throw new InvalidDataException("Could not read the Mac app’s bundle identifier.");
            PortResult = $"Source required for {id}. Select the corresponding macOS Xcode project to create its iOS port.";
            await PortAsync(id);
        }
        catch (Exception ex) { PortResult = "Mac app inspection failed: " + ex.Message; }
    }

    private async Task PortAsync(string? bundleId)
    {
        if (bundleId is null) PortResult = null;
        var selected = await _shell.PickFileAsync("Select the source .xcodeproj", "Xcode projects", "*.xcodeproj");
        if (selected is null) return;
        var source = Path.GetDirectoryName(selected);
        if (source is null || !File.Exists(Path.Combine(selected, "project.pbxproj")))
        {
            PortResult = "Select a source .xcodeproj containing project.pbxproj.";
            return;
        }
        IsImporting = true;
        try
        {
            MacProjectPort.Result? port = null;
            var result = await Task.Run(() => _projectService.ImportDirectoryAsync(source, null, CancellationToken.None,
                copy => port = MacProjectPort.PrepareCopy(copy, Path.GetFileName(selected), bundleId)));
            await LoadProjectsAsync(result.Project);
            PortResult = $"Created an editable iPhone and iPad project at {Path.GetDirectoryName(port!.ProjectFile)}. Review macOS-only APIs, linked frameworks, signing and entitlements, then build the Simulator output below.";
            if (port.ReviewFiles.Count > 0)
                PortResult += "\nReview platform-specific references or resources in " + string.Join(", ", port.ReviewFiles);
            await _shell.OpenFolderAsync(result.Project.SourceDirectory);
        }
        catch (Exception ex) { PortResult = "Project port failed: " + ex.Message; }
        finally { IsImporting = false; }
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (await _shell.PickFolderAsync("Select a mobile project folder") is { } dir)
            await RunImportAsync(ct => _projectService.ImportDirectoryAsync(dir, null, ct));
    }

    [RelayCommand]
    private async Task ImportZipAsync()
    {
        if (await _shell.PickZipAsync() is { } zip)
            await RunImportAsync(ct => _projectService.ImportZipAsync(zip, null, ct));
    }

    [RelayCommand]
    private async Task ImportArtifactAsync()
    {
        if (await _shell.PickFileAsync("Import a mobile artifact", "Android / iOS packages", "*.apk", "*.aab", "*.apks", "*.ipa") is { } file)
            await RunImportAsync(ct => _projectService.ImportArtifactAsync(file, null, ct));
    }

    private async Task RunImportAsync(Func<CancellationToken, Task<ImportResult>> import)
    {
        ImportError = null;
        IsImporting = true;
        try
        {
            var result = await Task.Run(() => import(CancellationToken.None));
            if (result.Project.Analysis?.Mobile is null)
                ImportError = $"“{result.Project.Name}” is not a mobile project ({result.Project.Analysis?.ApplicationType ?? "unrecognized"}). Desktop projects are built in New Build.";
            await LoadProjectsAsync(result.Project);
        }
        catch (ImportRejectedException ex) { ImportError = ex.Message; }
        catch (Exception ex) { ImportError = "Import failed: " + ex.Message; }
        finally { IsImporting = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _report = await _toolchains.GetAsync(refresh: true);
        if (SelectedProject is { } p)
        {
            await _projectService.InspectAsync(p.Project, CancellationToken.None);
            ApplyProject(p.Project);
        }
    }

    private void ApplyProject(ProjectRecord project)
    {
        var a = project.Analysis!;
        var m = a.Mobile!;
        SupportTag = new StatusTag(Format.Label(a.Support), Format.Tone(a.Support));
        SupportSummary = a.SupportSummary;
        ProjectRows.Clear();
        ProjectRows.Add(new("Type", a.ApplicationType));
        ProjectRows.Add(new("Framework", m.Framework.DisplayName()));
        ProjectRows.Add(new("Platforms", string.Join(", ", m.Platforms.Select(p => p.DisplayName()))));
        if (m.ApplicationIds.Count > 0) ProjectRows.Add(new("Application ID", string.Join(", ", m.ApplicationIds)));
        if (m.Android is { } android)
        {
            ProjectRows.Add(new("Gradle", $"{(android.HasWrapper ? "wrapper " + android.WrapperGradleVersion : "no wrapper")} · AGP {android.AgpVersion ?? "unknown"} · {(android.UsesKotlin ? "Kotlin" : "Java")}"));
            foreach (var mod in android.AppModules)
                ProjectRows.Add(new("Module " + mod.GradlePath, $"{mod.ApplicationId} · v{mod.VersionName} ({mod.VersionCode}) · minSdk {mod.MinSdk} · targetSdk {mod.TargetSdk} · variants {string.Join(", ", mod.Variants)}{(mod.HasSigningConfig ? " · release signing configured" : "")}"));
        }
        if (m.Ios is { } ios)
        {
            ProjectRows.Add(new("Xcode", ios.WorkspacePath ?? ios.ProjectPath ?? "—"));
            ProjectRows.Add(new("Schemes", ios.Schemes.Count > 0 ? string.Join(", ", ios.Schemes.Select(s => s.Name)) : "none shared"));
            foreach (var t in ios.AppTargets)
                ProjectRows.Add(new("Target " + t.Name, $"{t.BundleIdentifier} · iOS {t.DeploymentTarget} · signing {t.CodeSignStyle ?? "default"}{(t.DevelopmentTeam is null ? "" : " · team " + t.DevelopmentTeam)}"));
        }
        if (m.Flutter is { } f) ProjectRows.Add(new("Flutter", $"{f.Name} {f.Version} · SDK {f.SdkConstraint}"));
        if (m.Maui is { } maui) ProjectRows.Add(new(".NET MAUI", $"{maui.ProjectPath} · {string.Join(", ", maui.TargetFrameworks)}"));
        if (m.ArtifactPath is { } artifact) ProjectRows.Add(new("Artifact", artifact));
        Diagnostics.Clear();
        foreach (var d in a.Diagnostics) Diagnostics.Add(new DiagnosticItem(d));

        var options = MobileCapabilities.Evaluate(m, _report);
        Platforms.Clear();
        foreach (var platform in new[] { TargetPlatform.Android, TargetPlatform.IOS })
        {
            var supported = m.Platforms.Contains(platform);
            Platforms.Add(new PlatformChoice(platform, supported, supported ? null : $"This project has no {platform.DisplayName()} target."));
        }
        _allOptions = options;
        SelectedPlatform = Platforms.FirstOrDefault(p => p.IsSupported && options.Any(o => o.Platform == p.Platform && o.IsAvailable))
                           ?? Platforms.FirstOrDefault(p => p.IsSupported);
        ApplyIdentityDefaults(project, m);
        _ = LoadArtifactReportAsync();
    }

    // ───────────── Target platform & output ─────────────

    private IReadOnlyList<MobileOutputOption> _allOptions = [];
    public ObservableCollection<PlatformChoice> Platforms { get; } = [];
    public ObservableCollection<MobileOutputItem> Outputs { get; } = [];
    [ObservableProperty] private PlatformChoice? _selectedPlatform;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand))] private MobileOutputItem? _selectedOutput;
    public bool HasOutputs => Outputs.Count > 0;

    partial void OnSelectedPlatformChanged(PlatformChoice? value)
    {
        Outputs.Clear();
        if (value is not null)
            foreach (var o in _allOptions.Where(o => o.Platform == value.Platform)) Outputs.Add(new MobileOutputItem(o));
        OnPropertyChanged(nameof(HasOutputs));
        SelectOutput(Outputs.FirstOrDefault(o => o.IsEnabled));
    }

    [RelayCommand]
    private void SelectOutput(MobileOutputItem? item)
    {
        if (item is not null && !item.IsEnabled) return;
        foreach (var o in Outputs) o.IsSelected = o == item;
        SelectedOutput = item;
        RebuildConfiguration();
    }

    // ───────────── Configuration ─────────────

    public ObservableCollection<string> Modules { get; } = [];
    public ObservableCollection<string> Variants { get; } = [];
    public ObservableCollection<string> Schemes { get; } = [];
    public ObservableCollection<Choice<MobileSigningMode>> SigningModes { get; } = [];
    public ObservableCollection<Choice<IosExportMethod>> ExportMethods { get; } = [];
    public ObservableCollection<Choice<ApkSetMode>> ApkSetModes { get; } = [new(ApkSetMode.Universal, "Universal APK (all ABIs and densities)"), new(ApkSetMode.DeviceTargeted, "Device-targeted split APKs (device-spec JSON)")];
    [ObservableProperty] private string? _selectedModule;
    [ObservableProperty] private string? _selectedVariant;
    [ObservableProperty] private string? _selectedScheme;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTeam), nameof(ShowProfile), nameof(ShowProvisioningUpdates), nameof(SigningExplanation))] private Choice<MobileSigningMode>? _selectedSigning;
    [ObservableProperty] private Choice<IosExportMethod>? _selectedExportMethod;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDeviceSpec))] private Choice<ApkSetMode>? _selectedApkSetMode;
    [ObservableProperty] private string _developmentTeam = "";
    [ObservableProperty] private string _provisioningProfile = "";
    [ObservableProperty] private bool _allowProvisioningUpdates;
    [ObservableProperty] private string? _deviceSpecPath;
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private string _appVersion = "";
    [ObservableProperty] private string _outputDirectory;
    [ObservableProperty] private bool _showModules;
    [ObservableProperty] private bool _showVariants;
    [ObservableProperty] private bool _showSchemes;
    [ObservableProperty] private bool _showSigning;
    [ObservableProperty] private bool _showExport;
    [ObservableProperty] private bool _showApkSet;

    public bool ShowTeam => SelectedSigning?.Value is MobileSigningMode.AppleAutomatic or MobileSigningMode.AppleManual;
    public bool ShowProfile => SelectedSigning?.Value is MobileSigningMode.AppleManual;
    public bool ShowProvisioningUpdates => SelectedSigning?.Value is MobileSigningMode.AppleAutomatic;
    public bool ShowDeviceSpec => ShowApkSet && SelectedApkSetMode?.Value == ApkSetMode.DeviceTargeted;

    public string SigningExplanation => SelectedSigning?.Value switch
    {
        MobileSigningMode.ProjectDefault => "Uses only what the project itself configures. HCG never supplies credentials: secret-like environment variables are removed and personal Gradle properties are not read.",
        MobileSigningMode.Unsigned => "Produces an unsigned artifact. It cannot be installed (Android) or exported to an IPA (iOS) until it is signed.",
        MobileSigningMode.DebugKeystore => "Signs with HCG's local Android debug key (created once in HCG's data folder; the conventional public debug password). Installable for testing — never accepted by Google Play.",
        MobileSigningMode.AppleAutomatic => "Xcode automatic signing for your Apple Developer team, using certificates already in this Mac's keychain.",
        MobileSigningMode.AppleManual => "Xcode manual signing with an installed provisioning profile (by name). HCG never creates certificates or profiles.",
        _ => "",
    };

    private void RebuildConfiguration()
    {
        var m = Mobile;
        var output = SelectedOutput?.Option.Output;
        Modules.Clear(); Variants.Clear(); Schemes.Clear(); SigningModes.Clear(); ExportMethods.Clear();
        ShowModules = ShowVariants = ShowSchemes = ShowSigning = ShowExport = ShowApkSet = false;
        if (m is null || output is null) { OnPropertyChanged(nameof(ShowDeviceSpec)); return; }
        var android = output.Value.Platform() == TargetPlatform.Android;
        var debugOutput = output is MobileOutput.AndroidDebugApk or MobileOutput.IosSimulatorApp;

        if (android && m.Android is { } a && m.ArtifactPath is null)
        {
            foreach (var mod in a.AppModules) Modules.Add(mod.GradlePath);
            ShowModules = Modules.Count > 1;
            SelectedModule = Modules.FirstOrDefault();
            var module = a.AppModules.FirstOrDefault();
            var kind = output == MobileOutput.AndroidDebugApk ? "Debug" : "Release";
            foreach (var v in module?.Variants.Where(v => v.EndsWith(kind, StringComparison.OrdinalIgnoreCase)) ?? []) Variants.Add(v);
            ShowVariants = Variants.Count > 1;
            SelectedVariant = Variants.FirstOrDefault();
        }
        if (!android && m.Ios is { } ios && m.ArtifactPath is null)
        {
            foreach (var s in ios.Schemes.Select(s => s.Name).DefaultIfEmpty(ios.AppTargets.FirstOrDefault()?.Name).OfType<string>()) Schemes.Add(s);
            ShowSchemes = Schemes.Count > 1;
            SelectedScheme = Schemes.FirstOrDefault();
            foreach (var c in ios.Configurations) Variants.Add(c);
            ShowVariants = Variants.Count > 1;
            SelectedVariant = Variants.FirstOrDefault(c => c == (debugOutput ? "Debug" : "Release")) ?? Variants.FirstOrDefault();
        }

        if (!debugOutput && output != MobileOutput.AndroidAabToApk)
        {
            ShowSigning = true;
            SigningModes.Add(new(MobileSigningMode.ProjectDefault, Format.Label(MobileSigningMode.ProjectDefault)));
            if (m.Framework is MobileFramework.NativeAndroid or MobileFramework.NativeIos || (m.Framework == MobileFramework.Flutter && output == MobileOutput.IosArchive))
                SigningModes.Add(new(MobileSigningMode.Unsigned, Format.Label(MobileSigningMode.Unsigned)));
            if (android && m.Framework == MobileFramework.NativeAndroid)
                SigningModes.Add(new(MobileSigningMode.DebugKeystore, Format.Label(MobileSigningMode.DebugKeystore)));
            if (!android && m.Framework == MobileFramework.NativeIos)
            {
                SigningModes.Add(new(MobileSigningMode.AppleAutomatic, Format.Label(MobileSigningMode.AppleAutomatic)));
                SigningModes.Add(new(MobileSigningMode.AppleManual, Format.Label(MobileSigningMode.AppleManual)));
            }
            SelectedSigning = SigningModes[0];
        }
        else SelectedSigning = null;
        if (output == MobileOutput.IosIpa)
        {
            ShowExport = true;
            foreach (var e in Enum.GetValues<IosExportMethod>()) ExportMethods.Add(new(e, Format.Label(e)));
            SelectedExportMethod = ExportMethods.First(e => e.Value == IosExportMethod.Debugging);
        }
        ShowApkSet = output == MobileOutput.AndroidAabToApk;
        SelectedApkSetMode = ApkSetModes[0];
        OnPropertyChanged(nameof(ShowDeviceSpec));
        PrepareSummary();
    }

    private void ApplyIdentityDefaults(ProjectRecord project, MobileAnalysis m)
    {
        AppName = project.Analysis?.ProjectName ?? project.Name;
        AppVersion = m.Android?.AppModules.FirstOrDefault()?.VersionName ?? m.Flutter?.Version?.Split('+')[0] ?? m.Maui?.DisplayVersion ?? "1.0.0";
    }

    [RelayCommand]
    private async Task ChooseOutputDirectoryAsync()
    {
        if (await _shell.PickFolderAsync("Choose output directory") is { } dir) OutputDirectory = dir;
    }

    [RelayCommand]
    private async Task ChooseDeviceSpecAsync()
    {
        if (await _shell.PickFileAsync("Choose a device spec (bundletool get-device-spec)", "Device spec JSON", "*.json") is { } file) DeviceSpecPath = file;
    }

    private MobileBuildOptions? CreateOptions()
    {
        var m = Mobile;
        if (m is null || SelectedOutput is null) return null;
        return new MobileBuildOptions
        {
            Framework = m.Framework,
            Output = SelectedOutput.Option.Output,
            Module = SelectedModule,
            Variant = SelectedVariant,
            Scheme = SelectedScheme,
            Signing = SelectedSigning?.Value ?? MobileSigningMode.ProjectDefault,
            DevelopmentTeam = string.IsNullOrWhiteSpace(DevelopmentTeam) ? null : DevelopmentTeam.Trim(),
            ProvisioningProfile = string.IsNullOrWhiteSpace(ProvisioningProfile) ? null : ProvisioningProfile.Trim(),
            AllowProvisioningUpdates = AllowProvisioningUpdates && SelectedSigning?.Value == MobileSigningMode.AppleAutomatic,
            ExportMethod = SelectedExportMethod?.Value,
            ApkSetMode = SelectedApkSetMode?.Value ?? ApkSetMode.Universal,
            DeviceSpecPath = DeviceSpecPath,
            InputArtifact = m.ArtifactPath,
        };
    }

    private BuildSettings CreateSettings(MobileBuildOptions o) => new()
    {
        ProjectRelativePath = Mobile?.Android?.Root ?? Mobile?.Ios?.WorkspacePath ?? Mobile?.Ios?.ProjectPath ?? Mobile?.Flutter?.Root ?? Mobile?.Maui?.ProjectPath ?? Mobile?.ArtifactPath ?? "",
        Platform = o.Output.Platform(),
        Architecture = TargetArchitecture.Universal,
        Configuration = o.Output is MobileOutput.AndroidDebugApk or MobileOutput.IosSimulatorApp ? BuildConfigurationKind.Debug : BuildConfigurationKind.Release,
        OutputDirectory = OutputDirectory,
        Timeout = TimeSpan.FromMinutes(60),
        Identity = new AppIdentity
        {
            ApplicationName = string.IsNullOrWhiteSpace(AppName) ? "App" : AppName.Trim(),
            Version = string.IsNullOrWhiteSpace(AppVersion) ? "1.0.0" : AppVersion.Trim(),
            ApplicationId = Mobile?.ApplicationIds.FirstOrDefault() ?? "app",
        },
        Mobile = o,
    };

    // ───────────── Build status ─────────────

    public string TrustTitle => TrustPolicy.TrustBoundaryTitle;
    public string TrustExplanation =>
        "Building runs code from this project with your user's permissions: the Gradle wrapper and build scripts, Xcode run-script phases, " +
        "Flutter/Dart build hooks or MSBuild targets. Gradle may download the distribution named by the wrapper. Only build projects you trust.";
    public string IsolationDescription => _worker.Description;
    public ObservableCollection<SummaryRow> Summary { get; } = [];
    public ObservableCollection<LogLineItem> LogLines { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand))] private bool _trustAcknowledged;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand), nameof(CancelBuildCommand))] private bool _isBuilding;
    [ObservableProperty] private bool _hasBuildStarted;
    [ObservableProperty] private string _currentStage = "Ready";
    [ObservableProperty] private string _elapsed = "0.0s";
    [ObservableProperty] private int _liveWarnings;
    [ObservableProperty] private int _liveErrors;
    [ObservableProperty] private StatusTag _resultTag = new("Not started", "neutral");
    [ObservableProperty] private string _resultDetail = "";

    public event EventHandler? LogAppended;

    partial void OnSelectedSigningChanged(Choice<MobileSigningMode>? value) => PrepareSummary();
    partial void OnSelectedVariantChanged(string? value) => PrepareSummary();
    partial void OnOutputDirectoryChanged(string value) => PrepareSummary();
    partial void OnAppNameChanged(string value) => PrepareSummary();
    partial void OnAppVersionChanged(string value) => PrepareSummary();

    private void PrepareSummary()
    {
        Summary.Clear();
        if (CreateOptions() is not { } o) return;
        var s = CreateSettings(o);
        Summary.Add(new("Output", $"{o.Output.DisplayName()} · {o.Framework.DisplayName()}"));
        if (o.Variant is not null) Summary.Add(new(o.Output.Platform() == TargetPlatform.Android ? "Variant" : "Configuration", o.Variant));
        if (SelectedSigning is not null) Summary.Add(new("Signing", Format.Label(o.Signing)));
        if (o.ExportMethod is { } e) Summary.Add(new("Export", Format.Label(e)));
        if (o.InputArtifact is not null) Summary.Add(new("Input", o.InputArtifact));
        Summary.Add(new("Destination", MobileBuildService.OutputRootFor(s)));
    }

    private bool CanStartBuild() => TrustAcknowledged && !IsBuilding && SelectedOutput?.IsEnabled == true;

    [RelayCommand(CanExecute = nameof(CanStartBuild))]
    private async Task StartBuildAsync()
    {
        if (SelectedProject is null || CreateOptions() is not { } options) return;
        var settings = CreateSettings(options);
        var root = MobileBuildService.OutputRootFor(settings);
        var allowOverwrite = false;
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (!File.Exists(Path.Combine(root, BuildService.OutputMarkerFile)))
            {
                await _dialogs.AlertAsync("Output folder in use", $"“{root}” already exists and was not created by HCG Executable Maker. It will not be replaced.");
                return;
            }
            allowOverwrite = await _dialogs.ConfirmAsync("Replace previous output?", $"A previous HCG build exists:\n\n{root}\n\nReplace it?", "Replace and build", destructive: true);
            if (!allowOverwrite) return;
        }

        LogLines.Clear();
        while (_pendingLog.TryDequeue(out _)) { }
        LiveWarnings = LiveErrors = 0;
        CurrentStage = "Starting…";
        ResultTag = new StatusTag("Running", "info");
        ResultDetail = "";
        BuiltArtifacts.Clear();
        IsBuilding = true;
        HasBuildStarted = true;
        _buildCts = new CancellationTokenSource();
        _clock.Restart();
        _uiTimer.Start();
        BuildRecord? result = null;
        try
        {
            var request = new MobileBuildRequest { Project = SelectedProject.Project, Settings = settings, TrustAcknowledged = TrustAcknowledged, AllowOverwriteOutput = allowOverwrite };
            var token = _buildCts.Token;
            result = await Task.Run(() => _builder.RunAsync(request, new Observer(this), token));
        }
        catch (Exception ex) { await _dialogs.AlertAsync("Build could not run", ex.Message); }
        finally
        {
            _clock.Stop();
            _uiTimer.Stop();
            FlushLog();
            IsBuilding = false;
            TrustAcknowledged = false; // trust is confirmed per build
            _buildCts.Dispose();
            _buildCts = null;
        }
        if (result is null) return;
        ResultTag = new StatusTag(Format.Label(result.Status), Format.Tone(result.Status));
        CurrentStage = result.Status == BuildStatus.Succeeded ? "Completed" : Format.Label(result.Status);
        ResultDetail = result.Status == BuildStatus.Succeeded
            ? $"{result.Artifacts.Count} artifact(s) in {Format.Duration(result.Duration)}. Nothing was uploaded or published."
            : result.FailureReason ?? "";
        foreach (var a in result.Artifacts) BuiltArtifacts.Add(new ArtifactItem(a, result));
        OnPropertyChanged(nameof(HasBuiltArtifacts));
    }

    private bool CanCancelBuild() => IsBuilding;

    [RelayCommand(CanExecute = nameof(CanCancelBuild))]
    private void CancelBuild()
    {
        CurrentStage = "Cancelling — terminating build processes…";
        _buildCts?.Cancel();
    }

    private void FlushLog()
    {
        Elapsed = Format.Duration(_clock.Elapsed);
        if (_pendingLog.IsEmpty) return;
        var added = 0;
        while (_pendingLog.TryDequeue(out var line) && added < 2000) { LogLines.Add(new LogLineItem(line)); added++; }
        while (LogLines.Count > MaxDisplayedLogLines) LogLines.RemoveAt(0);
        LogAppended?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Observer(MobileBuildStudioViewModel vm) : IBuildObserver
    {
        public void OnLog(BuildLogLine line) => vm._pendingLog.Enqueue(line);
        public void OnStage(BuildStage stage, string description) => Dispatcher.UIThread.Post(() => vm.CurrentStage = description);
        public void OnMessage(BuildMessage message) => Dispatcher.UIThread.Post(() =>
        {
            if (message.Severity == DiagnosticSeverity.Warning) vm.LiveWarnings++;
            if (message.Severity == DiagnosticSeverity.Error) vm.LiveErrors++;
        });
    }

    // ───────────── Artifacts ─────────────

    public ObservableCollection<ArtifactItem> BuiltArtifacts { get; } = [];
    public bool HasBuiltArtifacts => BuiltArtifacts.Count > 0;
    public ObservableCollection<CheckItem> ArtifactChecks { get; } = [];
    [ObservableProperty] private string? _artifactReportTitle;
    [ObservableProperty] private StatusTag _artifactReportTag = new("—", "neutral");
    [ObservableProperty] private bool _isVerifying;

    private async Task LoadArtifactReportAsync()
    {
        ArtifactChecks.Clear();
        ArtifactReportTitle = null;
        if (SelectedProject?.Project is not { } p || Mobile?.ArtifactPath is not { } rel) return;
        await VerifyPathAsync(Path.Combine(p.SourceDirectory, rel), $"Imported artifact · {rel}");
    }

    [RelayCommand]
    private async Task VerifyArtifactFileAsync()
    {
        if (await _shell.PickFileAsync("Verify a mobile artifact (it is read, never executed or modified)", "Android / iOS packages", "*.apk", "*.aab", "*.apks", "*.ipa") is { } file)
            await VerifyPathAsync(file, Path.GetFileName(file));
    }

    private async Task VerifyPathAsync(string path, string title)
    {
        IsVerifying = true;
        try
        {
            var (report, checks) = await Task.Run(() => _verifier.VerifyAsync(path, _report, CancellationToken.None));
            ArtifactChecks.Clear();
            foreach (var c in checks) ArtifactChecks.Add(new CheckItem(c));
            ArtifactReportTitle = $"{title} — {Format.Bytes(report.SizeBytes)}";
            var status = Packaging.Verification.Checks.Combine(checks);
            ArtifactReportTag = new StatusTag(Format.Label(status), Format.Tone(status));
        }
        catch (Exception ex) { await _dialogs.AlertAsync("Verification failed", ex.Message); }
        finally { IsVerifying = false; }
    }

    [RelayCommand]
    private async Task OpenArtifactFolderAsync(ArtifactItem item)
    {
        var dir = item.Artifact.Kind == ArtifactKind.Directory ? Path.GetDirectoryName(item.Path) : Path.GetDirectoryName(item.Path);
        if (dir is not null) await _shell.OpenFolderAsync(dir);
    }

    [RelayCommand]
    private async Task CopyChecksumAsync(ArtifactItem item) => await _shell.CopyAsync(item.Sha256);
}
