using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.MacXcode;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// "Native macOS app — Xcode" in the New Build wizard: the project and app target are chosen under Project to build;
/// the panel offers the project's schemes and configurations, the architectures the selected macOS SDK builds
/// (this Mac's own by default; Universal only when the SDK has both), and shows the preflight and every setting HCG
/// passes to xcodebuild. The app keeps its own identity: name, bundle ID, version and icon are the project's, not
/// editable here. Scripts that ship with the project are never run.
/// </summary>
public sealed partial class BuildWizardViewModel
{
    public bool IsMacXcodeProject => Analysis is { InspectorId: MacXcodeProjectInspector.InspectorId, MacXcode: not null };

    public MacXcodeProjectInfo? SelectedXcodeProject =>
        IsMacXcodeProject && SelectedProject is { } sp ? Analysis!.MacXcode!.Projects.FirstOrDefault(p => p.ProjectPath == sp.Path) : null;

    public MacXcodeTargetInfo? SelectedXcodeTarget =>
        SelectedXcodeProject?.AppTargets.FirstOrDefault(t => (t.ProductName ?? t.Name) == SelectedProject!.Project.Name) ?? SelectedXcodeProject?.AppTargets.FirstOrDefault();

    public bool IsMacXcodeBuild => SelectedXcodeTarget is not null;
    public bool ShowXcodePanel => IsMacXcodeProject;

    public ObservableCollection<string> XcodeSchemes { get; } = [];
    [ObservableProperty] private string? _selectedXcodeScheme;
    public ObservableCollection<string> XcodeConfigurations { get; } = [];
    [ObservableProperty] private string? _selectedXcodeConfiguration;
    public ObservableCollection<Choice<MacXcodeArchitecture>> XcodeArchitectures { get; } = [];
    [ObservableProperty] private Choice<MacXcodeArchitecture>? _selectedXcodeArchitecture;
    [ObservableProperty] private bool _xcodeSignLocallyAdHoc;
    /// <summary>Runs the scheme's Test action (xcodebuild test) after the build; a failing test fails the build.</summary>
    [ObservableProperty] private bool _xcodeRunTests;
    /// <summary>Also archives the scheme (xcodebuild archive) and keeps the .xcarchive (zipped) with the outputs.</summary>
    [ObservableProperty] private bool _xcodeCreateArchive;
    [ObservableProperty] private string _xcodeTestsNote = "";
    [ObservableProperty] private bool _xcodeHasTests;
    [ObservableProperty] private string _xcodeDependencies = "";
    [ObservableProperty] private string _xcodeConfigurationFacts = "";
    [ObservableProperty] private StatusTag _xcodeTag = new("—", "neutral");
    [ObservableProperty] private string _xcodeFacts = "";
    [ObservableProperty] private string _xcodeOverridesText = "";
    [ObservableProperty] private string _xcodeSchemeNote = "";
    [ObservableProperty] private bool _xcodeOffersLocalSigning;
    public ObservableCollection<string> XcodePreflight { get; } = [];
    public ObservableCollection<string> XcodeFixes { get; } = [];
    [ObservableProperty] private bool _hasXcodeFixes;
    private bool _refreshingXcode;

    partial void OnSelectedXcodeSchemeChanged(string? value)
    {
        var scheme = SelectedXcodeProject?.Schemes.FirstOrDefault(x => x.Name == value);
        XcodeHasTests = scheme is { TestTargets.Count: > 0 };
        XcodeTestsNote = scheme is null ? "" : scheme.TestTargets.Count == 0 ? $"The scheme {value} has no tests in its Test action."
            : "Tests: " + string.Join("; ", MacXcodeProjectAnalyzer.TestsOf(SelectedXcodeProject!, scheme) is var (own, pkgs)
                ? own.Select(t => $"{t} (xcodebuild test{(scheme.TestConfiguration is { } tc ? ", " + tc : "")})").Concat(pkgs.Select(t => $"{t.TestTarget} (swift test in {t.PackageDirectory})"))
                : []) + ", on this Mac's architecture.";
        if (!XcodeHasTests) XcodeRunTests = false;
        XcodeOptionsChanged();
    }
    partial void OnSelectedXcodeConfigurationChanged(string? value) => XcodeOptionsChanged();
    partial void OnSelectedXcodeArchitectureChanged(Choice<MacXcodeArchitecture>? value)
    {
        // The wizard's architecture follows the Xcode choice (artifact names, summary).
        if (value is not null && IsMacXcodeBuild)
            SelectedArchitecture = ArchitectureOptions.FirstOrDefault(a => a.Value == value.Value.ToTarget()) ?? SelectedArchitecture;
        XcodeOptionsChanged();
    }
    partial void OnXcodeSignLocallyAdHocChanged(bool value) => XcodeOptionsChanged();
    partial void OnXcodeRunTestsChanged(bool value) => XcodeOptionsChanged();
    partial void OnXcodeCreateArchiveChanged(bool value) => XcodeOptionsChanged();

    private void XcodeOptionsChanged()
    {
        if (_refreshingXcode) return;
        XcodeOverridesText = SelectedXcodeTarget is { } t && CreateXcodeOptions() is { } o
            ? string.Join("\n", MacXcodeBuildPlanner.Overrides(o, t.Architectures).Select(x => $"{x.Setting}={(x.Value.Length == 0 ? "\"\"" : x.Value)} — {x.Reason}"))
            : "";
        if (Step == WizardStep.Configure) Validate();
        OnPropertyChanged(nameof(CanConfigure));
        NextCommand.NotifyCanExecuteChanged();
    }

    private MacXcodeCapabilities.Assessment? XcodeAssessment => SelectedXcodeTarget is { } t ? MacXcodeCapabilities.Evaluate(t, _toolchains.Latest) : null;

    private bool XcodeReadyToConfigure => IsMacXcodeBuild && XcodeAssessment?.Support == SupportLevel.Supported && SelectedXcodeScheme is not null && SelectedXcodeConfiguration is not null;

    /// <summary>Fills the panel from the inspection: schemes, configurations, architectures, preflight and facts.</summary>
    private void RefreshXcodeProject()
    {
        foreach (var name in new[] { nameof(IsMacXcodeProject), nameof(IsMacXcodeBuild), nameof(ShowXcodePanel), nameof(IsDotNetBuild), nameof(IdentityEditable), nameof(ShowConfigurationChoice) })
            OnPropertyChanged(name);
        if (SelectedXcodeProject is not { } p || SelectedXcodeTarget is not { } t) return;
        _refreshingXcode = true;
        try
        {
            var keepScheme = SelectedXcodeScheme;
            XcodeSchemes.Clear();
            foreach (var s in p.Schemes) XcodeSchemes.Add(s.Name);
            if (XcodeSchemes.Count == 0) XcodeSchemes.Add(t.Name); // xcodebuild creates a scheme per target when none is committed
            SelectedXcodeScheme = XcodeSchemes.Contains(keepScheme ?? "") ? keepScheme : MacXcodeProjectAnalyzer.SchemeFor(p, t)?.Name ?? XcodeSchemes[0];
            XcodeSchemeNote = p.Schemes.Count == 0 ? $"No shared scheme is committed; xcodebuild creates “{t.Name}” for the target." : p.Schemes.Count > 1 ? $"{p.Schemes.Count} schemes: the one that builds {t.Name} is preselected." : "";

            var keepConfig = SelectedXcodeConfiguration;
            XcodeConfigurations.Clear();
            foreach (var c in p.Configurations) XcodeConfigurations.Add(c);
            var archive = p.Schemes.FirstOrDefault(s => s.Name == SelectedXcodeScheme)?.ArchiveConfiguration;
            SelectedXcodeConfiguration = XcodeConfigurations.Contains(keepConfig ?? "") ? keepConfig
                : XcodeConfigurations.Contains("Release") ? "Release" : archive is not null && XcodeConfigurations.Contains(archive) ? archive : XcodeConfigurations.LastOrDefault();

            var a = XcodeAssessment!;
            var keepArch = SelectedXcodeArchitecture?.Value;
            XcodeArchitectures.Clear();
            var host = HostInfo.Detect();
            var own = MacXcodeCapabilities.DefaultArchitecture(host, a.Architectures);
            foreach (var arch in a.Architectures)
                XcodeArchitectures.Add(new Choice<MacXcodeArchitecture>(arch, arch.Label() + (arch == own ? " — this Mac" : arch == MacXcodeArchitecture.Universal ? " — runs on both, larger" : "")));
            SelectedXcodeArchitecture = XcodeArchitectures.FirstOrDefault(x => x.Value == keepArch) ?? XcodeArchitectures.FirstOrDefault(x => x.Value == own) ?? XcodeArchitectures.FirstOrDefault();

            XcodeDependencies = p.PackageReferences.Count == 0 ? "No Swift packages."
                : string.Join("\n", p.PackageReferences.Select(r => r.IsLocal
                    ? $"Local package {r.Location} (in the source; no download){(r.ManifestPath is null ? " — its Package.swift is missing" : "")}"
                    : $"Remote package {r.Location}{(r.Requirement is { } req ? $" ({req})" : "")}"
                      + (p.ResolvedPackages.FirstOrDefault(pin => string.Equals(pin.Location?.TrimEnd('/').Replace(".git", ""), r.Location.TrimEnd('/').Replace(".git", ""), StringComparison.OrdinalIgnoreCase)) is { } pin
                          ? $" — pinned {pin.Version ?? pin.Revision?[..Math.Min(12, pin.Revision.Length)]}" : " — not pinned in Package.resolved")))
                  + (p.PackageReferences.Any(r => !r.IsLocal) ? "\nRemote packages are resolved by xcodebuild into HCG's build folder before the build (network needed)." : "");
            XcodeConfigurationFacts = string.Join("\n", t.ConfigurationFacts.Select(MacXcodeProjectInspector.Describe));
            XcodeOffersLocalSigning = t.CodeSignIdentity is not "-" && !(t.CodeSignIdentity is null or "" && t.DevelopmentTeam is null && t.CodeSignStyle != "Automatic");
            if (!XcodeOffersLocalSigning) XcodeSignLocallyAdHoc = false;

            XcodePreflight.Clear();
            foreach (var r in a.Requirements) XcodePreflight.Add($"{(r.IsSatisfied ? "✓" : "✗")} {r.DisplayName}: {r.Detail}");
            XcodeFixes.Clear();
            foreach (var f in a.Remediation) XcodeFixes.Add(f);
            if (a.Note is { } note) XcodeFixes.Add(note);
            HasXcodeFixes = XcodeFixes.Count > 0;
            XcodeTag = a.Support switch
            {
                SupportLevel.Supported => new StatusTag("Ready", "success"),
                SupportLevel.RequiresDifferentHost => new StatusTag("Needs a Mac", "warning"),
                _ => new StatusTag("Toolchain missing", "warning"),
            };
            XcodeFacts = string.Join("\n", new[]
            {
                $"Project: {p.WorkspacePath ?? p.ProjectPath}" + (p.WorkspacePath is null ? "" : $" (workspace for {Path.GetFileName(p.ProjectPath)})"),
                $"App target: {t.Name} → {t.ProductName}.app · {t.DisplayName ?? t.ProductName} · {t.BundleIdentifier ?? "bundle ID from build settings"}",
                $"Version {t.MarketingVersion ?? "?"} (build {t.BuildVersion ?? "?"}) · minimum macOS {t.DeploymentTarget ?? "Xcode default"}",
                $"Language: {string.Join(" / ", p.Languages.DefaultIfEmpty("—"))} · UI: {string.Join(" + ", p.UiFrameworks.DefaultIfEmpty("—"))} · frameworks: {string.Join(", ", t.Frameworks.DefaultIfEmpty("—"))}",
                $"Signing (project): {t.SigningDescription}",
                p.LicenseFiles.Count > 0 ? $"Licence: {string.Join(", ", p.LicenseFiles.Select(Path.GetFileName))} — shipped unchanged with the app" : "Licence: none found next to the project",
                p.HelperScripts.Count > 0 ? $"Not run: {string.Join(", ", p.HelperScripts.Select(Path.GetFileName))} — HCG calls xcodebuild directly" : "",
            }.Where(l => l.Length > 0));
        }
        finally { _refreshingXcode = false; }
        XcodeOptionsChanged();
    }

    private MacXcodeBuildOptions? CreateXcodeOptions()
    {
        if (SelectedXcodeProject is not { } p || SelectedXcodeTarget is not { } t || SelectedXcodeScheme is null || SelectedXcodeConfiguration is null) return null;
        return new MacXcodeBuildOptions
        {
            ProjectPath = p.ProjectPath,
            WorkspacePath = p.WorkspacePath,
            Scheme = SelectedXcodeScheme,
            Configuration = SelectedXcodeConfiguration,
            Architecture = SelectedXcodeArchitecture?.Value ?? MacXcodeArchitecture.AppleSilicon,
            AppTarget = t.Name,
            ProductName = t.ProductName,
            BundleIdentifier = t.BundleIdentifier,
            DisplayName = t.DisplayName,
            DeploymentTarget = t.DeploymentTarget,
            SignLocallyAdHoc = XcodeSignLocallyAdHoc,
            RunTests = XcodeRunTests && XcodeHasTests,
            ProjectTestTargets = XcodeRunTests ? MacXcodeProjectAnalyzer.TestsOf(p, p.Schemes.FirstOrDefault(s => s.Name == SelectedXcodeScheme)).ProjectTargets : [],
            PackageTests = XcodeRunTests ? MacXcodeProjectAnalyzer.TestsOf(p, p.Schemes.FirstOrDefault(s => s.Name == SelectedXcodeScheme)).PackageTests : [],
            CreateArchive = XcodeCreateArchive,
            LicenseFiles = p.LicenseFiles,
        };
    }

    /// <summary>The app's identity is the project's: these fields show it and are not edited for an Xcode build.</summary>
    private void PrepareXcodeConfiguration(string key, AppSettings settings)
    {
        var t = SelectedXcodeTarget!;
        if (_identityPrefilledFor != key)
        {
            _identityPrefilledFor = key;
            OutputDirectory = settings.DefaultOutputDirectory ?? _paths.DefaultOutputDirectory;
            TimeoutMinutes = Math.Max(settings.BuildTimeoutMinutes, 30);
            SelectedConfiguration = ConfigurationOptions[0];
        }
        AppName = t.ProductName ?? t.Name;
        Version = t.MarketingVersion is { } v && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+(\.\d+){0,3}$") ? (v.Contains('.') ? v : v + ".0") : "1.0.0";
        AppId = t.BundleIdentifier is { } id && !id.Contains("$(", StringComparison.Ordinal) ? id : SuggestAppId("example", AppName);
        Publisher = "";
        Description = "";
        IconPath = null;
        var archs = new[] { TargetArchitecture.Arm64, TargetArchitecture.X64, TargetArchitecture.Universal };
        if (!ArchitectureOptions.Select(a => a.Value).SequenceEqual(archs))
        {
            ArchitectureOptions.Clear();
            ArchitectureOptions.Add(new(TargetArchitecture.Arm64, MacXcodeArchitecture.AppleSilicon.Label()));
            ArchitectureOptions.Add(new(TargetArchitecture.X64, MacXcodeArchitecture.Intel.Label()));
            ArchitectureOptions.Add(new(TargetArchitecture.Universal, MacXcodeArchitecture.Universal.Label()));
        }
        SelectedArchitecture = ArchitectureOptions.FirstOrDefault(a => a.Value == (SelectedXcodeArchitecture?.Value ?? MacXcodeArchitecture.AppleSilicon).ToTarget());
    }

    private void ValidateXcode()
    {
        if (Project is null) return;
        if (SelectedXcodeScheme is null) ConfigErrors.Add("Choose a scheme.");
        if (SelectedXcodeConfiguration is null) ConfigErrors.Add("Choose a build configuration.");
        if (XcodeAssessment is { Support: not SupportLevel.Supported } a) ConfigErrors.Add(a.Summary + " " + string.Join(" ", a.Remediation));
        if (ConfigErrors.Count > 0) return;
        try { MacXcodeBuildPlanner.Validate(CreateSettings([PackagingFormat.MacAppBundle]), HostInfo.Detect(), PathGuard.GetRealPath(Project.SourceDirectory)); }
        catch (BuildValidationException ex) { ConfigErrors.Add(ex.Message); }
    }

    private static IReadOnlyList<PackagingFormat> DefaultXcodeFormats() => [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg];

    private void ApplyXcodeSettings(MacXcodeBuildOptions o)
    {
        SelectedXcodeScheme = XcodeSchemes.Contains(o.Scheme) ? o.Scheme : SelectedXcodeScheme;
        SelectedXcodeConfiguration = XcodeConfigurations.Contains(o.Configuration) ? o.Configuration : SelectedXcodeConfiguration;
        SelectedXcodeArchitecture = XcodeArchitectures.FirstOrDefault(a => a.Value == o.Architecture) ?? SelectedXcodeArchitecture;
        XcodeSignLocallyAdHoc = o.SignLocallyAdHoc && XcodeOffersLocalSigning;
        XcodeRunTests = o.RunTests && XcodeHasTests;
        XcodeCreateArchive = o.CreateArchive;
    }
}
