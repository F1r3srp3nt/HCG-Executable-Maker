using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Native;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Native;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>A Theos package configuration the project defines (shown with the project's own refusal, if any).</summary>
public sealed class TheosSchemeItem(TheosScheme scheme)
{
    public TheosScheme Scheme { get; } = scheme;
    public string Name => Scheme.Label + (Scheme.IsDefault ? " — the project's default" : "") + (Scheme.ProjectError is null ? "" : " — refused by the project");
    public override string ToString() => Name;
}

/// <summary>A C/C++ source file offered for an explicitly configured build of a project without a build system.</summary>
public sealed partial class NativeSourceItem(NativeSourceFile file) : ObservableObject
{
    public string Path => file.Path;
    public string Detail => (file.Language == "C" ? "C" : "C++") + (file.HasMain ? " · defines main()" : "");
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// C/C++ in the New Build wizard. Under Project to build the user sees real executable targets (after a CMake project
/// was configured on this computer), Makefile projects, source-only projects, and — explained, never buildable —
/// Theos and other unsupported projects. States: Toolchain missing · Configuration required · Ready · Building ·
/// Failed · Verified. Configuring a CMake project and building anything run project code, so both need the user's
/// explicit trust confirmation; builds run for this computer's platform with its own compilers.
/// </summary>
public sealed partial class BuildWizardViewModel
{
    private const int MaxNativeLogLines = 600;
    private CancellationTokenSource? _nativeConfigureCts;

    public bool IsNativeProject => Analysis is { InspectorId: "native", Native: not null };

    /// <summary>
    /// The C/C++ project the panel shows: the one chosen under Project to build, or — when a configured project has
    /// several executables and none is chosen yet — the project that was just configured, so its targets stay listed.
    /// </summary>
    public NativeProjectInfo? SelectedNativeProject =>
        !IsNativeProject ? null
        : SelectedProject is { } sp
            ? Analysis!.Native!.Projects.FirstOrDefault(p => sp.Path == p.ManifestPath || sp.Path.StartsWith(p.ManifestPath + "#", StringComparison.Ordinal))
            : Analysis!.Native!.Projects.FirstOrDefault(p => p.ManifestPath == _nativePrefilledFor);

    /// <summary>The CMake target chosen under Project to build (entries are "dir/CMakeLists.txt#target").</summary>
    public string? SelectedNativeTarget => SelectedProject?.Path is { } path && path.Contains('#') ? path[(path.IndexOf('#') + 1)..] : null;

    public NativeTargetInfo? SelectedNativeTargetInfo => SelectedNativeProject?.Configured?.Targets.FirstOrDefault(t => t.Name == SelectedNativeTarget);

    /// <summary>Configure/Package/Build work on a C/C++ build (CMake target, Make target or selected sources).</summary>
    public bool IsNativeBuild => SelectedNativeProject is { } p && (p.Kind is NativeProjectKind.Make or NativeProjectKind.Sources || (p.Kind == NativeProjectKind.CMake && SelectedNativeTarget is not null)
                                                                || p is { Kind: NativeProjectKind.Theos, UnsupportedReason: null });
    /// <summary>A Theos package build (jailbreak .deb).</summary>
    public bool IsTheosBuild => SelectedNativeProject is { Kind: NativeProjectKind.Theos, UnsupportedReason: null };
    public bool ShowNativeTheos => IsTheosBuild;

    // Theos
    public ObservableCollection<TheosSchemeItem> TheosSchemes { get; } = [];
    [ObservableProperty] private TheosSchemeItem? _selectedTheosScheme;
    [ObservableProperty] private string _theosProducts = "";
    [ObservableProperty] private string _theosPackage = "";
    [ObservableProperty] private string _theosSchemeDetail = "";
    partial void OnSelectedTheosSchemeChanged(TheosSchemeItem? value)
    {
        TheosSchemeDetail = value?.Scheme is { } sc
            ? $"Installs under {sc.InstallPrefix} · Debian Architecture {sc.DebArchitecture} · TARGET iphone:clang:{sc.SdkVersion ?? "latest"}:{sc.DeploymentTarget ?? "(Theos default)"} → iOS {sc.DeploymentTarget ?? "(Theos default)"} and later, iPhoneOS {sc.SdkVersion ?? "latest"} SDK" +
              $" · ARCHS {string.Join(" ", SelectedNativeProject?.Theos?.Architectures.DefaultIfEmpty("(Theos default)") ?? [])}{(SelectedNativeProject?.Theos?.ArchitectureVariable is { } av ? $" (from {av})" : "")}" +
              (sc.ProjectError is { } e ? $"\nThe project refuses this configuration: {e}" : "")
            : "";
        NativeOptionsChanged();
    }

    private TheosSchemeChoice? CreateTheosChoice()
    {
        if (SelectedNativeProject?.Theos is not { } t || SelectedTheosScheme?.Scheme is not { } sc) return null;
        return new TheosSchemeChoice
        {
            SelectorVariable = sc.SelectorVariable, SelectorValue = sc.SelectorValue, PackageScheme = sc.PackageScheme, SdkVersion = sc.SdkVersion,
            DeploymentTarget = sc.DeploymentTarget, Architectures = t.Architectures, PackageId = t.PackageId, PackageVersion = t.PackageVersion, Instances = t.Instances,
        };
    }
    public bool IsNativeMacBundle => SelectedNativeTargetInfo?.IsMacBundle == true || (SelectedNativeProject?.Kind == NativeProjectKind.Make && NativeExpectedOutput.TrimEnd('/').EndsWith(".app", StringComparison.Ordinal));

    /// <summary>Notarize / test install / validate: not offered for a C/C++ executable outside a macOS app bundle.</summary>
    public bool ShowDistributionSteps => ResultSucceeded && Result?.Settings.Native is not { IsMacBundle: false };
    partial void OnResultSucceededChanged(bool value) => OnPropertyChanged(nameof(ShowDistributionSteps));

    /// <summary>.NET/Swift configuration choice; a C/C++ build uses the build type chosen in the C/C++ panel.</summary>
    public bool ShowConfigurationChoice => IdentityEditable && !IsNativeBuild;
    public bool ShowNativePanel => SelectedNativeProject is not null;
    public bool ShowNativeCMake => SelectedNativeProject?.Kind == NativeProjectKind.CMake;
    public bool ShowNativeMake => SelectedNativeProject?.Kind == NativeProjectKind.Make;
    public bool ShowNativeSources => SelectedNativeProject?.Kind == NativeProjectKind.Sources;
    public bool ShowNativeUnsupported => SelectedNativeProject is { Kind: NativeProjectKind.Unsupported } or { Kind: NativeProjectKind.Theos, UnsupportedReason: not null };
    public bool ShowNativeRuntimeFiles => SelectedNativeProject?.Kind is NativeProjectKind.CMake or NativeProjectKind.Make or NativeProjectKind.Sources;

    [ObservableProperty] private StatusTag _nativeTag = new("—", "neutral");
    [ObservableProperty] private string _nativeKindLabel = "";
    [ObservableProperty] private string _nativeStatus = "";
    [ObservableProperty] private string _nativeFacts = "";
    [ObservableProperty] private string _nativeConfigurationProblem = "";
    public ObservableCollection<string> NativeToolchainGaps { get; } = [];
    public ObservableCollection<string> NativeNotes { get; } = [];
    [ObservableProperty] private bool _hasNativeToolchainGaps;
    [ObservableProperty] private bool _hasNativeNotes;

    // CMake
    public ObservableCollection<string> NativePresets { get; } = [];
    [ObservableProperty] private string? _selectedNativePreset;
    public IReadOnlyList<string> NativeBuildTypes => NativeBuildPlanner.BuildTypes;
    [ObservableProperty] private string _selectedNativeBuildType = "Release";
    [ObservableProperty] private string _nativeCacheEntriesText = "";
    [ObservableProperty] private bool _nativeAllowDownloads;
    [ObservableProperty] private bool _nativeHasDownloads;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfigureNativeCommand))] private bool _nativeConfigureTrusted;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfigureNativeCommand), nameof(CancelNativeConfigureCommand), nameof(NextCommand), nameof(BackCommand))]
    private bool _isConfiguringNative;
    [ObservableProperty] private string? _nativeConfigureError;
    [ObservableProperty] private string _nativeConfiguredSummary = "";
    [ObservableProperty] private string _nativeLibrariesText = "";
    public ObservableCollection<string> NativeConfigureLog { get; } = [];
    [ObservableProperty] private bool _hasNativeConfigureLog;

    // Make
    public ObservableCollection<string> NativeMakeTargets { get; } = [];
    [ObservableProperty] private bool _hasNativeMakeTargets;
    [ObservableProperty] private string _nativeMakeTarget = "";
    [ObservableProperty] private string _nativeExpectedOutput = "";
    [ObservableProperty] private string _nativeMakeVariablesText = "";

    // Sources
    public ObservableCollection<NativeSourceItem> NativeSourceItems { get; } = [];
    public IReadOnlyList<string> NativeLanguages { get; } = ["C", "C++"];
    [ObservableProperty] private string _selectedNativeLanguage = "C";
    public ObservableCollection<string> NativeStandards { get; } = [];
    [ObservableProperty] private string? _selectedNativeStandard;
    [ObservableProperty] private string _nativeOutputName = "";
    [ObservableProperty] private string _nativeGeneratedPreview = "";

    // Runtime files (all buildable kinds)
    [ObservableProperty] private string _nativeRuntimeFilesText = "";

    private string? _nativePrefilledFor;

    partial void OnSelectedNativePresetChanged(string? value) => NativeOptionsChanged();
    partial void OnSelectedNativeBuildTypeChanged(string value) => NativeOptionsChanged();
    partial void OnNativeCacheEntriesTextChanged(string value) => NativeOptionsChanged();
    partial void OnNativeMakeTargetChanged(string value) => NativeOptionsChanged();
    partial void OnNativeExpectedOutputChanged(string value) => NativeOptionsChanged();
    partial void OnNativeMakeVariablesTextChanged(string value) => NativeOptionsChanged();
    partial void OnNativeOutputNameChanged(string value) => NativeOptionsChanged();
    partial void OnNativeRuntimeFilesTextChanged(string value) => NativeOptionsChanged();
    partial void OnSelectedNativeStandardChanged(string? value) => NativeOptionsChanged();
    partial void OnSelectedNativeLanguageChanged(string value)
    {
        var keep = SelectedNativeStandard;
        NativeStandards.Clear();
        foreach (var s in value == "C" ? NativeBuildPlanner.CStandards : NativeBuildPlanner.CxxStandards) NativeStandards.Add(s);
        SelectedNativeStandard = NativeStandards.Contains(keep ?? "") ? keep : "17";
        NativeOptionsChanged();
    }

    private bool _refreshingNative;
    private void NativeOptionsChanged()
    {
        if (_refreshingNative) return;
        RefreshNativeState();
        OnPropertyChanged(nameof(CanConfigure));
        NextCommand.NotifyCanExecuteChanged();
    }

    private void RefreshNativeProject()
    {
        foreach (var n in new[] { nameof(IsNativeProject), nameof(SelectedNativeProject), nameof(SelectedNativeTarget), nameof(SelectedNativeTargetInfo), nameof(IsNativeBuild),
                     nameof(IsNativeMacBundle), nameof(ShowNativePanel), nameof(ShowNativeCMake), nameof(ShowNativeMake), nameof(ShowNativeSources), nameof(ShowNativeUnsupported),
                     nameof(ShowNativeRuntimeFiles), nameof(IsDotNetBuild), nameof(IdentityEditable), nameof(ShowConfigurationChoice), nameof(IsTheosBuild), nameof(ShowNativeTheos) })
            OnPropertyChanged(n);
        NativeToolchainGaps.Clear();
        NativeNotes.Clear();
        var p = SelectedNativeProject;
        if (p is null)
        {
            NativeTag = new StatusTag("—", "neutral");
            NativeStatus = NativeFacts = NativeKindLabel = NativeConfigurationProblem = "";
            FinishNativeRefresh();
            return;
        }
        _refreshingNative = true;
        try
        {
            NativeKindLabel = NativeCapabilities.KindLabel(p.Kind);
            if (p.Theos is { } theos && p.UnsupportedReason is null)
            {
                var keep = SelectedTheosScheme?.Scheme.Label;
                TheosSchemes.Clear();
                foreach (var sc in theos.Schemes) TheosSchemes.Add(new TheosSchemeItem(sc));
                SelectedTheosScheme = TheosSchemes.FirstOrDefault(x => x.Scheme.Label == keep)
                                      ?? TheosSchemes.FirstOrDefault(x => x.Scheme.IsDefault && x.Scheme.ProjectError is null) ?? TheosSchemes.FirstOrDefault(x => x.Scheme.ProjectError is null);
                TheosProducts = string.Join("\n", theos.Instances.Select(i => $"• {i.DisplayType}: {i.Name}{(i.Makefile != theos.Makefile ? $" (subproject {Path.GetDirectoryName(i.Makefile)?.Replace('\\', '/')})" : "")}{(i.FilterPlist is { } f ? $" · filter {f}" : "")}"));
                TheosPackage = $"{theos.PackageName ?? "(no Name)"} · {theos.PackageId ?? "(no Package)"} {theos.PackageVersion} · control Architecture {theos.ControlArchitecture ?? "—"} (Theos sets it per configuration)" +
                               $"\nDepends: {theos.Depends ?? "nothing declared"}{(theos.Section is { } sec ? " · Section " + sec : "")}" +
                               (theos.MaintainerScripts.Count > 0 ? "\nMaintainer scripts (run as root on install): " + string.Join(", ", theos.MaintainerScripts) : "\nNo maintainer scripts: installing runs nothing.");
            }
            var assessment = p.Kind == NativeProjectKind.Theos && SelectedTheosScheme is { } chosenScheme
                ? NativeCapabilities.EvaluateTheos(p, chosenScheme.Scheme, _toolchains.Latest)
                : NativeCapabilities.Evaluate(p, _toolchains.Latest);
            foreach (var r in assessment.Remediation) NativeToolchainGaps.Add(r);
            foreach (var n in p.Notes) NativeNotes.Add(n);
            var facts = new List<string> { $"Folder: {p.DisplayDirectory}", $"Evidence: {string.Join("; ", p.Evidence)}" };
            if (p.Languages.Count > 0) facts.Add($"Languages: {NativeProjectInspector.LanguageLabel(p.Languages)}");
            if (p.Kind == NativeProjectKind.CMake)
            {
                if (p.MinimumCMake is { } min) facts.Add($"cmake_minimum_required: {min}");
                if (p.ExecutableHints.Count > 0 || p.LibraryHints.Count > 0)
                    facts.Add($"Read from CMakeLists (hints; configuration is authoritative): executables {string.Join(", ", p.ExecutableHints.DefaultIfEmpty("none"))} · libraries {string.Join(", ", p.LibraryHints.DefaultIfEmpty("none"))}");
                if (p.FindPackages.Count > 0) facts.Add("find_package: " + string.Join(", ", p.FindPackages));
            }
            NativeFacts = string.Join("\n", facts);
            if (p.Kind == NativeProjectKind.CMake)
            {
                var keepPreset = SelectedNativePreset;
                NativePresets.Clear();
                NativePresets.Add(NoPreset);
                foreach (var preset in p.Presets.Where(x => !x.Hidden)) NativePresets.Add(preset.Name);
                SelectedNativePreset = NativePresets.Contains(keepPreset ?? "") ? keepPreset : p.Configured?.Preset ?? NoPreset;
                NativeHasDownloads = p.DownloadEvidence.Count > 0;
                var c = p.Configured;
                NativeConfiguredSummary = c is null
                    ? "Not configured on this computer yet. Configure it to list its real targets (the CMake File API reports executables, libraries and where each artifact is written)."
                    : $"Configured {c.ConfiguredAt.ToLocalTime():yyyy-MM-dd HH:mm} with CMake {c.CMakeVersion} · {c.Generator}{(c.IsMultiConfig ? $" (multi-configuration: {string.Join(", ", c.Configurations)})" : $" ({(c.Configuration.Length == 0 ? "no CMAKE_BUILD_TYPE" : c.Configuration)})")}" +
                      $"{(c.Preset is { } pr ? $" · preset {pr}" : "")}\nCompilers: {string.Join("; ", c.Compilers.Select(x => $"{(x.Language == "CXX" ? "C++" : x.Language)} {x.Id} {x.Version}"))}" +
                      (c.CacheHighlights.Count > 0 ? "\n" + string.Join(" · ", c.CacheHighlights.Select(kv => $"{kv.Key}={kv.Value}")) : "") +
                      $"\nExecutable targets (choose one under Project to build): {string.Join(", ", c.Executables.Select(t => t.Name + (t.IsMacBundle ? " (.app)" : "")).DefaultIfEmpty("none"))}";
                NativeLibrariesText = c is null ? "" : c.Libraries.Any()
                    ? $"Libraries (built as dependencies; not runnable applications): {string.Join(", ", c.Libraries.Select(t => $"{t.Name} ({t.Type})"))}" : "";
            }
            if (_nativePrefilledFor != p.ManifestPath)
            {
                _nativePrefilledFor = p.ManifestPath;
                NativeConfigureTrusted = false;
                NativeConfigureError = null;
                NativeConfigureLog.Clear();
                HasNativeConfigureLog = false;
                NativeRuntimeFilesText = "";
                NativeAllowDownloads = p.Configured?.DownloadsAllowed ?? false;
                if (p.Kind == NativeProjectKind.Make)
                {
                    NativeMakeTargets.Clear();
                    foreach (var t in p.MakeTargets) NativeMakeTargets.Add(t);
                    HasNativeMakeTargets = NativeMakeTargets.Count > 0;
                    NativeMakeTarget = p.MakeTargets.FirstOrDefault() ?? "";
                    NativeExpectedOutput = "";
                    NativeMakeVariablesText = "";
                }
                if (p.Kind == NativeProjectKind.Sources)
                {
                    NativeSourceItems.Clear();
                    var firstMain = p.Sources.FirstOrDefault(s => s.HasMain)?.Path;
                    foreach (var s in p.Sources)
                    {
                        // Preselected: the first file with main() and files without main() in its folder. Every choice is the user's.
                        var item = new NativeSourceItem(s) { IsSelected = s.Path == firstMain || (!s.HasMain && Dir(s.Path) == Dir(firstMain ?? "")) };
                        item.PropertyChanged += (_, _) => NativeOptionsChanged();
                        NativeSourceItems.Add(item);
                    }
                    SelectedNativeLanguage = p.Sources.Any(s => s.Language == "CXX") ? "C++" : "C";
                    OnSelectedNativeLanguageChanged(SelectedNativeLanguage);
                    NativeOutputName = Slug(p.Directory.Length == 0 && Project is not null ? Project.Name : p.Name);
                }
            }
        }
        finally { _refreshingNative = false; }
        RefreshNativeState();
        FinishNativeRefresh();
    }

    private const string NoPreset = "(none — HCG chooses the generator)";
    private static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private void FinishNativeRefresh()
    {
        HasNativeToolchainGaps = NativeToolchainGaps.Count > 0;
        HasNativeNotes = NativeNotes.Count > 0;
        ConfigureNativeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfigure));
        NextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Toolchain missing · Configuration required · Ready · Building · Failed · Verified (or Not supported).</summary>
    private void RefreshNativeState()
    {
        var p = SelectedNativeProject;
        if (p is null) return;
        if (p.Kind == NativeProjectKind.Sources)
            NativeGeneratedPreview = NativeBuildPlanner.GeneratedCMakeLists(CreateNativeOptions() ?? new NativeBuildOptions { ProjectDirectory = p.Directory, Kind = p.Kind, Target = "app" }, "<project folder>/" + p.Directory);
        NativeConfigurationProblem = NativeProblem(p) ?? "";
        var assessment = p.Kind == NativeProjectKind.Theos && SelectedTheosScheme is { } ts ? NativeCapabilities.EvaluateTheos(p, ts.Scheme, _toolchains.Latest) : NativeCapabilities.Evaluate(p, _toolchains.Latest);
        var gaps = assessment.Support is SupportLevel.MissingToolchain or SupportLevel.RequiresDifferentHost;
        if (p.Kind == NativeProjectKind.Theos)
        {
            NativeToolchainGaps.Clear();
            foreach (var r in assessment.Remediation) NativeToolchainGaps.Add(r);
            HasNativeToolchainGaps = NativeToolchainGaps.Count > 0;
        }
        var forThis = Result?.Settings.Native is { } done && done.ProjectDirectory == p.Directory && done.Target == (CreateNativeOptions()?.Target ?? "");
        (NativeTag, NativeStatus) = p.Kind switch
        {
            NativeProjectKind.Unsupported => (new StatusTag("Build adapter unavailable", "error"), p.UnsupportedReason ?? ""),
            NativeProjectKind.Theos when p.UnsupportedReason is { } incomplete => (new StatusTag("Project implementation incomplete", "error"), incomplete),
            _ when gaps => (new StatusTag("Required toolchain missing", "warning"), "This computer is missing tools this project needs. Install them (see below and the Toolchains page), then press Re-inspect. HCG never installs tools."),
            _ when IsBuilding && IsNativeBuild => (new StatusTag("Building", "info"), "Building… follow the stages and the log on the Build step."),
            _ when NativeConfigurationProblem.Length > 0 => (new StatusTag("Configuration required", "warning"), NativeConfigurationProblem),
            _ when forThis && Result!.Status == BuildStatus.Succeeded && Result.Artifacts.Count > 0 => (new StatusTag("Artifact verified", "success"), "The last build of this target succeeded and its artifacts passed verification (see Results)."),
            _ when forThis && Result!.Status is BuildStatus.Failed or BuildStatus.TimedOut or BuildStatus.Cancelled => (new StatusTag("Build failed", "error"), $"The last build of this target {Result.Status.ToString().ToLowerInvariant()}: {Result.FailureReason}"),
            _ => (new StatusTag("Ready to build", "success"), p.Kind switch
            {
                NativeProjectKind.Theos => $"Ready to build {p.Name} ({SelectedTheosScheme?.Scheme.Label}) into a .deb for jailbroken devices with Theos on this computer. Not an IPA; nothing is installed.",
                NativeProjectKind.CMake => $"Ready to build target {SelectedNativeTarget} for this computer ({HostInfo.CurrentPlatform.DisplayName()}).",
                NativeProjectKind.Make => $"Ready to run make {NativeMakeTarget.Trim()} and package {NativeExpectedOutput.Trim()}.",
                _ => $"Ready to compile {NativeSourceItems.Count(s => s.IsSelected)} selected file(s) into {NativeOutputName.Trim()}.",
            }),
        };
    }

    /// <summary>What is still missing before a build (null when the configuration is complete and valid).</summary>
    private string? NativeProblem(NativeProjectInfo p)
    {
        if (p.Kind == NativeProjectKind.CMake)
        {
            if (p.Configured is null) return "Configure the project on this computer to list its executable targets (it runs the project's CMake code, so confirm that you trust it first).";
            if (!p.Configured.Executables.Any()) return "The project defines no executable target — only libraries, which are not runnable applications. HCG packages executables.";
            if (SelectedNativeTarget is null) return "Choose an executable target under Project to build.";
        }
        if (p.Kind == NativeProjectKind.Make)
        {
            if (NativeMakeTarget.Trim().Length == 0) return "Enter the make target to build.";
            if (NativeExpectedOutput.Trim().Length == 0) return "Enter the file that target produces, relative to the project folder (HCG does not guess what a Makefile builds).";
        }
        if (p.Kind == NativeProjectKind.Sources && !NativeSourceItems.Any(s => s.IsSelected)) return "Select the source files to compile.";
        if (p.Kind == NativeProjectKind.Theos)
        {
            if (SelectedTheosScheme is null) return "Choose the package configuration the project defines (rootless / rootful).";
            if (SelectedTheosScheme.Scheme.ProjectError is { } refused) return $"The project refuses {SelectedTheosScheme.Scheme.Label}: {refused}";
            return null;
        }
        if (CreateNativeOptions() is not { } options || Project is null) return null;
        var host = HostInfo.Detect();
        try
        {
            NativeBuildPlanner.Validate(new BuildSettings
            {
                ProjectRelativePath = p.ManifestPath, Platform = host.Platform, Architecture = host.Platform == TargetPlatform.MacOS ? TargetArchitecture.Arm64 : host.Architecture,
                OutputDirectory = "/", Identity = new AppIdentity { ApplicationName = "x", Version = "1.0.0", ApplicationId = "x.y" }, Native = options,
            }, host, Project.SourceDirectory);
            return null;
        }
        catch (BuildValidationException ex) { return ex.Message; }
    }

    private bool NativeReadyToConfigure =>
        SelectedNativeProject is { } p && IsNativeBuild && NativeCapabilities.Evaluate(p, _toolchains.Latest).Support == SupportLevel.Supported && NativeProblem(p) is null && !IsConfiguringNative;

    private static IReadOnlyList<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith('#')).ToList();

    private NativeBuildOptions? CreateNativeOptions()
    {
        if (SelectedNativeProject is not { } p || p.Kind is NativeProjectKind.Unsupported || p is { Kind: NativeProjectKind.Theos, UnsupportedReason: not null }) return null;
        if (p.Kind == NativeProjectKind.Theos)
            return new NativeBuildOptions
            {
                ProjectDirectory = p.Directory, Kind = NativeProjectKind.Theos, Target = "package", TheosScheme = CreateTheosChoice(),
            };
        var preset = SelectedNativePreset is null || SelectedNativePreset == NoPreset ? null : SelectedNativePreset;
        return new NativeBuildOptions
        {
            ProjectDirectory = p.Directory,
            Kind = p.Kind,
            Target = p.Kind switch
            {
                NativeProjectKind.CMake => SelectedNativeTarget ?? "",
                NativeProjectKind.Make => NativeMakeTarget.Trim(),
                _ => NativeOutputName.Trim(),
            },
            Preset = p.Kind == NativeProjectKind.CMake ? preset : null,
            BuildType = SelectedNativeBuildType,
            CacheEntries = p.Kind == NativeProjectKind.CMake ? Lines(NativeCacheEntriesText).Select(l => l.StartsWith("-D", StringComparison.Ordinal) ? l[2..] : l).ToList() : [],
            AllowDownloads = NativeAllowDownloads,
            MakeVariables = p.Kind == NativeProjectKind.Make ? Lines(NativeMakeVariablesText) : [],
            ExpectedOutput = p.Kind == NativeProjectKind.Make ? NativeExpectedOutput.Trim().Replace('\\', '/') : null,
            Sources = p.Kind == NativeProjectKind.Sources ? NativeSourceItems.Where(s => s.IsSelected).Select(s => s.Path).ToList() : [],
            Language = SelectedNativeLanguage == "C++" ? "CXX" : "C",
            Standard = SelectedNativeStandard ?? "17",
            RuntimeFiles = Lines(NativeRuntimeFilesText).Select(l => l.Replace('\\', '/')).ToList(),
            IsMacBundle = IsNativeMacBundle,
        };
    }

    [RelayCommand]
    private void UseNativeMakeTarget(string target)
    {
        NativeMakeTarget = target;
        // A non-phony rule target is a file the rule builds: offered as the expected output (shown, editable).
        if (NativeExpectedOutput.Trim().Length == 0 && SelectedNativeProject is { } p && !p.PhonyTargets.Contains(target) && target != "all")
            NativeExpectedOutput = target;
    }

    private bool CanConfigureNative() =>
        SelectedNativeProject is { Kind: NativeProjectKind.CMake } p && NativeConfigureTrusted && !IsConfiguringNative && Project is not null
        && NativeCapabilities.Evaluate(p, _toolchains.Latest).Support == SupportLevel.Supported;

    /// <summary>Configures the CMake project in HCG's own folder (after trust) and lists its real targets.</summary>
    [RelayCommand(CanExecute = nameof(CanConfigureNative))]
    private async Task ConfigureNativeAsync()
    {
        if (Project is null || Analysis is null || SelectedNativeProject is not { } native) return;
        var options = new NativeBuildOptions
        {
            ProjectDirectory = native.Directory, Kind = NativeProjectKind.CMake, Target = "-",
            Preset = SelectedNativePreset is null || SelectedNativePreset == NoPreset ? null : SelectedNativePreset,
            BuildType = SelectedNativeBuildType, AllowDownloads = NativeAllowDownloads,
            CacheEntries = Lines(NativeCacheEntriesText).Select(l => l.StartsWith("-D", StringComparison.Ordinal) ? l[2..] : l).ToList(),
        };
        IsConfiguringNative = true;
        NativeConfigureError = null;
        NativeConfigureLog.Clear();
        HasNativeConfigureLog = false;
        _nativeConfigureCts = new CancellationTokenSource();
        var keep = native.ManifestPath;
        try
        {
            var project = Project;
            var trusted = NativeConfigureTrusted;
            var token = _nativeConfigureCts.Token;
            var result = await Task.Run(() => _nativeConfigurator.ConfigureAsync(project, keep, options, trusted,
                line => Dispatcher.UIThread.Post(() => AppendNativeLog(line)), token));
            if (result is { Succeeded: true, Project: { } configured })
            {
                var updated = NativeProjectInspector.WithProject(Analysis, configured, _toolchains.Latest, DateTimeOffset.UtcNow);
                await _projectService.UpdateAnalysisAsync(project, updated);
                var trustedBefore = NativeConfigureTrusted;
                ApplyAnalysis(updated);
                // Keep the panel on this project: its only executable is selected; with several, the user chooses.
                var executables = configured.Configured!.Executables.ToList();
                SelectedProject = executables.Count == 1
                    ? ProjectOptions.FirstOrDefault(o => o.Path == NativeProjectInspector.TargetKey(configured, executables[0].Name))
                    : ProjectOptions.FirstOrDefault(o => o.Path == keep);
                NativeConfigureTrusted = trustedBefore;
            }
            else NativeConfigureError = result.Error ?? "Configuration failed.";
        }
        catch (OperationCanceledException) { NativeConfigureError = "Configuration cancelled. The processes CMake started were stopped."; }
        catch (Exception ex) { NativeConfigureError = "Configuration failed: " + SecretRedactor.Redact(ex.Message); }
        finally
        {
            IsConfiguringNative = false;
            _nativeConfigureCts?.Dispose();
            _nativeConfigureCts = null;
            RefreshNativeProject();
        }
    }

    private bool CanCancelNativeConfigure() => IsConfiguringNative;
    [RelayCommand(CanExecute = nameof(CanCancelNativeConfigure))] private void CancelNativeConfigure() => _nativeConfigureCts?.Cancel();

    private void AppendNativeLog(string line)
    {
        if (NativeConfigureLog.Count >= MaxNativeLogLines) return;
        NativeConfigureLog.Add(line);
        HasNativeConfigureLog = true;
    }

    /// <summary>Identity defaults and the architectures this host builds (a Mac: arm64, x86_64 or both; others: the host's own).</summary>
    private void PrepareNativeConfiguration(string key, AppSettings settings)
    {
        var p = SelectedNativeProject!;
        if (_identityPrefilledFor != key)
        {
            _identityPrefilledFor = key;
            var o = CreateNativeOptions();
            var named = p.Kind == NativeProjectKind.Theos ? p.Name : p.Kind == NativeProjectKind.Make ? Path.GetFileNameWithoutExtension(o?.ExpectedOutput?.TrimEnd('/') ?? "") : Path.GetFileName(o?.Target ?? "");
            AppName = named is { Length: > 0 } ? named : p.Name;
            Version = p.Version is { } v && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+(\.\d+){1,3}$") ? v : "1.0.0";
            Publisher = settings.LastPublisher ?? "";
            AppId = p.Theos?.PackageId ?? SuggestAppId(Publisher, AppName);
            Description = "";
            IconPath = null;
            OutputDirectory = settings.DefaultOutputDirectory ?? _paths.DefaultOutputDirectory;
            TimeoutMinutes = Math.Max(settings.BuildTimeoutMinutes, 30);
            SelectedConfiguration = ConfigurationOptions[0];
        }
        var host = HostInfo.Detect();
        var wanted = IsTheosBuild
            ? [new Choice<TargetArchitecture>(TargetArchitecture.Arm64, $"{string.Join(" + ", p.Theos!.Architectures.DefaultIfEmpty("Theos default"))} (the project's ARCHS)")]
            : host.Platform == TargetPlatform.MacOS
            ? SwiftArchitectures
            : [new Choice<TargetArchitecture>(host.Architecture, host.Architecture == TargetArchitecture.Arm64 ? "ARM64 (this computer)" : "x64 (this computer)")];
        if (!ArchitectureOptions.Select(a => a.Value).SequenceEqual(wanted.Select(a => a.Value)))
        {
            var current = SelectedArchitecture?.Value;
            ArchitectureOptions.Clear();
            foreach (var a in wanted) ArchitectureOptions.Add(a);
            SelectedArchitecture = ArchitectureOptions.FirstOrDefault(a => a.Value == current) ?? ArchitectureOptions.FirstOrDefault(a => a.Value == HostInfo.CurrentArchitecture) ?? ArchitectureOptions[0];
        }
    }

    private void ValidateNative()
    {
        if (Project is null) return;
        try
        {
            if (IsTheosBuild) HCG.ExecutableMaker.BuildEngine.Native.TheosBuildPlanner.Validate(CreateSettings(DefaultNativeFormats()), HostInfo.Detect(), PathGuard.GetRealPath(Project.SourceDirectory));
            else NativeBuildPlanner.Validate(CreateSettings(DefaultNativeFormats()), HostInfo.Detect(), PathGuard.GetRealPath(Project.SourceDirectory));
        }
        catch (BuildValidationException ex) { ConfigErrors.Add(ex.Message); }
    }

    private IReadOnlyList<PackagingFormat> DefaultNativeFormats() => IsTheosBuild ? [PackagingFormat.JailbreakDeb] :
        IsNativeMacBundle ? [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip] : [PackagingFormat.PortableFolder, PackagingFormat.PortableZip];

    /// <summary>Restores a previous build's options (Build again).</summary>
    private void ApplyNativeSettings(NativeBuildOptions o)
    {
        _refreshingNative = true;
        try
        {
            SelectedNativePreset = o.Preset ?? NoPreset;
            SelectedNativeBuildType = o.BuildType;
            NativeCacheEntriesText = string.Join("\n", o.CacheEntries);
            NativeAllowDownloads = o.AllowDownloads;
            NativeMakeVariablesText = string.Join("\n", o.MakeVariables);
            NativeRuntimeFilesText = string.Join("\n", o.RuntimeFiles);
            if (o.Kind == NativeProjectKind.Make) { NativeMakeTarget = o.Target; NativeExpectedOutput = o.ExpectedOutput ?? ""; }
            if (o.Kind == NativeProjectKind.Sources)
            {
                NativeOutputName = o.Target;
                SelectedNativeLanguage = o.Language == "CXX" ? "C++" : "C";
                SelectedNativeStandard = o.Standard;
                foreach (var s in NativeSourceItems) s.IsSelected = o.Sources.Contains(s.Path);
            }
        }
        finally { _refreshingNative = false; }
        RefreshNativeState();
    }
}
