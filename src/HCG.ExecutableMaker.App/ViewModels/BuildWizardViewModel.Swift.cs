using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Swift;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Swift;
using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// Swift Package Manager (macOS) flow of the New Build wizard: choose a package (never auto-selected when there are
/// several), evaluate Package.swift on this Mac after an explicit trust confirmation, choose an executable product,
/// and configure the .app (display name, bundle id, build number, deployment target, category, privacy texts).
/// </summary>
public sealed partial class BuildWizardViewModel
{
    private const int MaxEvaluationLogLines = 400;
    private CancellationTokenSource? _swiftEvalCts;

    // ───────────── Inspect: package & products ─────────────

    /// <summary>The workspace contains Swift packages (this wizard builds them on a Mac).</summary>
    public bool IsSwiftProject => Analysis?.Swift is { Packages.Count: > 0 };
    /// <summary>Only macOS Xcode projects/workspaces were found: recognized, but not built in this version.</summary>
    public bool IsMacXcodeOnly => Analysis?.Swift is { Packages.Count: 0, MacXcodeProjects.Count: > 0 };
    public bool HasMacXcodeProjects => Analysis?.Swift is { MacXcodeProjects.Count: > 0 };
    public string MacXcodeProjectsText => Analysis?.Swift is { MacXcodeProjects.Count: > 0 } s
        ? "macOS Xcode project(s): " + string.Join(", ", s.MacXcodeProjects) + ". Building .xcodeproj/.xcworkspace macOS apps (xcodebuild schemes, " +
          "signing settings, asset catalogs) is a separate capability that is NOT implemented in this version. HCG builds Swift Package Manager packages " +
          "(Package.swift) on a Mac. iOS Xcode projects are built in Mobile Build Studio, which does not produce macOS apps."
        : "";
    public string MissingHint => Analysis?.Support switch
    {
        SupportLevel.RequiresDifferentHost => "Installing tools on this computer does not help: open the project in HCG Executable Maker on a suitable host.",
        SupportLevel.RequiresConfiguration when ShowDesktopConversion =>
            "These are missing from the project's configuration, not from this computer. “Prepare desktop app” makes these changes in a separate copy after showing them; or change the project yourself, then press Re-inspect.",
        SupportLevel.RequiresConfiguration => "These are missing from the project's configuration, not from this computer: installing tools does not help. Change the project, then press Re-inspect.",
        _ => "Install the missing tools (see the Toolchains page), then press Re-inspect.",
    };
    public string MissingTitle => Analysis?.Support == SupportLevel.RequiresConfiguration ? "Missing project configuration" : "Missing requirements";
    public string ProjectPickerLabel => IsSwiftProject ? "SWIFT PACKAGE TO BUILD" : "PROJECT TO BUILD";
    public string ProjectPickerPlaceholder => IsSwiftProject ? "Choose a Swift package…" : ProjectOptions.Count > 0 ? "Choose a project…" : "No runnable application project found";

    public SwiftPackageInfo? SelectedSwiftPackage =>
        Analysis?.Swift?.Packages.FirstOrDefault(p => SelectedProject is not null && p.ManifestPath == SelectedProject.Path);
    public bool HasSelectedSwiftPackage => IsSwiftProject && SelectedSwiftPackage is not null;
    public bool IsSwiftEvaluated => SelectedSwiftPackage?.Evaluated is not null;

    [ObservableProperty] private StatusTag _swiftPackageTag = new("—", "neutral");
    [ObservableProperty] private string _swiftPackageStatus = "";
    [ObservableProperty] private string _swiftPackageFacts = "";
    public ObservableCollection<string> SwiftRemediation { get; } = [];
    [ObservableProperty] private bool _hasSwiftRemediation;
    [ObservableProperty] private bool _swiftHostCanEvaluate;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EvaluateSwiftPackageCommand))] private bool _swiftEvaluationTrusted;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EvaluateSwiftPackageCommand), nameof(CancelSwiftEvaluationCommand), nameof(NextCommand), nameof(BackCommand))]
    private bool _isEvaluatingSwift;
    [ObservableProperty] private string? _swiftEvaluationError;
    public ObservableCollection<string> SwiftEvaluationLog { get; } = [];
    [ObservableProperty] private bool _hasSwiftEvaluationLog;

    public ObservableCollection<SwiftProductChoice> SwiftExecutableProducts { get; } = [];
    public ObservableCollection<string> SwiftLibraryProducts { get; } = [];
    public ObservableCollection<string> SwiftTestTargets { get; } = [];
    [ObservableProperty] private bool _hasSwiftProducts;
    [ObservableProperty] private string _swiftLibrariesText = "";
    [ObservableProperty] private string _swiftTestsText = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyPropertyChangedFor(nameof(IsSwiftBuild), nameof(IsDotNetBuild))]
    private SwiftProductChoice? _selectedSwiftProduct;

    partial void OnSelectedSwiftProductChanged(SwiftProductChoice? value) => OnPropertyChanged(nameof(CanConfigure));

    /// <summary>Configure/Package/Build steps work on a Swift package product rather than a .NET project.</summary>
    public bool IsSwiftBuild => HasSelectedSwiftPackage && SelectedSwiftProduct is not null;
    public bool IsDotNetBuild => !IsSwiftBuild && !IsElectronBuild;

    private bool SwiftReadyToConfigure =>
        SelectedSwiftPackage is { Evaluated: not null } pkg && SelectedSwiftProduct is not null && !IsEvaluatingSwift
        && SwiftCapabilities.Evaluate(pkg, _toolchains.Latest).Support == SupportLevel.Supported;

    private void RefreshSwiftPackage()
    {
        OnPropertyChanged(nameof(IsSwiftProject));
        OnPropertyChanged(nameof(IsMacXcodeOnly));
        OnPropertyChanged(nameof(HasMacXcodeProjects));
        OnPropertyChanged(nameof(MacXcodeProjectsText));
        OnPropertyChanged(nameof(ProjectPickerLabel));
        OnPropertyChanged(nameof(MissingHint));
        OnPropertyChanged(nameof(ProjectPickerPlaceholder));
        OnPropertyChanged(nameof(SelectedSwiftPackage));
        OnPropertyChanged(nameof(HasSelectedSwiftPackage));
        OnPropertyChanged(nameof(IsSwiftEvaluated));
        SwiftRemediation.Clear();
        SwiftExecutableProducts.Clear();
        SwiftLibraryProducts.Clear();
        SwiftTestTargets.Clear();
        SwiftEvaluationError = null;
        var pkg = SelectedSwiftPackage;
        if (pkg is null)
        {
            SwiftPackageTag = new StatusTag("—", "neutral");
            SwiftPackageStatus = IsSwiftProject ? "Choose the Swift package to build." : "";
            SwiftPackageFacts = "";
            SwiftHostCanEvaluate = false;
            FinishSwiftRefresh(null);
            return;
        }

        var assessment = SwiftCapabilities.Evaluate(pkg, _toolchains.Latest);
        foreach (var r in assessment.Remediation) SwiftRemediation.Add(r);
        SwiftHostCanEvaluate = assessment.Support == SupportLevel.Supported;
        var facts = new List<string> { $"Manifest: {pkg.ManifestPath}" };
        facts.Add(pkg.ToolsVersion is { } tv ? $"swift-tools-version {tv}" : "No swift-tools-version line (SwiftPM will reject the manifest)");
        if (pkg.MinimumMacOSHint is { } min) facts.Add($"Declares macOS {min}+");
        if (pkg.HasConditionalDeclarations) facts.Add("Declares products conditionally (#if os(…)): the product list is known only after evaluation on this Mac");
        if (pkg.HasPackageDependencies) facts.Add("Has package dependencies: SwiftPM downloads them while building (network required)");
        SwiftPackageFacts = string.Join("\n", facts);

        if (pkg.Evaluated is { } m)
        {
            SwiftPackageTag = new StatusTag(assessment.Support == SupportLevel.Supported ? "Evaluated" : Format.Label(assessment.Support),
                assessment.Support == SupportLevel.Supported ? "success" : Format.Tone(assessment.Support));
            SwiftPackageStatus = $"Package “{m.Name}” evaluated {pkg.EvaluatedAt?.ToLocalTime():g} with {pkg.EvaluatedWith}." +
                                 (m.MinimumMacOS is { } mm ? $" Minimum macOS {mm}." : " No macOS platform declared (SwiftPM's default deployment target applies).");
            foreach (var p in m.ExecutableProducts)
            {
                var targetFacts = pkg.SourceFacts.FirstOrDefault(f => p.Targets.Contains(f.Target));
                SwiftExecutableProducts.Add(new SwiftProductChoice(p.Name, p.Targets, targetFacts));
            }
            foreach (var l in m.LibraryProducts) SwiftLibraryProducts.Add(l.Name);
            foreach (var t in m.TestTargets) SwiftTestTargets.Add(t.Name);
        }
        else
        {
            SwiftPackageTag = new StatusTag(assessment.Support == SupportLevel.Supported ? "Not evaluated" : Format.Label(assessment.Support),
                assessment.Support == SupportLevel.Supported ? "warning" : Format.Tone(assessment.Support));
            SwiftPackageStatus = assessment.Support switch
            {
                SupportLevel.Supported => "Package.swift has not been run. Its executable products are confirmed by evaluating it on this Mac (swift package dump-package).",
                SupportLevel.RequiresDifferentHost => assessment.Summary,
                _ => assessment.Summary,
            };
        }
        FinishSwiftRefresh(pkg);
    }

    private void FinishSwiftRefresh(SwiftPackageInfo? pkg)
    {
        HasSwiftRemediation = SwiftRemediation.Count > 0;
        HasSwiftProducts = SwiftExecutableProducts.Count > 0;
        SwiftLibrariesText = SwiftLibraryProducts.Count > 0 ? string.Join(", ", SwiftLibraryProducts) : "None";
        SwiftTestsText = SwiftTestTargets.Count > 0 ? string.Join(", ", SwiftTestTargets) : "None";
        // One executable product is the obvious choice; several (e.g. Relay's app and its CLI) must be chosen.
        SelectedSwiftProduct = SwiftExecutableProducts.Count == 1 ? SwiftExecutableProducts[0] : null;
        if (pkg?.Evaluated is { } m && !m.ExecutableProducts.Any())
            SwiftEvaluationError = "This package declares no executable product on this Mac (only libraries/tests), so there is nothing to build into an app.";
        OnPropertyChanged(nameof(CanConfigure));
        EvaluateSwiftPackageCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private bool CanEvaluateSwift() => SelectedSwiftPackage is not null && SwiftHostCanEvaluate && SwiftEvaluationTrusted && !IsEvaluatingSwift && Project is not null;

    /// <summary>Runs <c>swift package dump-package</c> through the build worker (trusted, scrubbed environment, cancellable).</summary>
    [RelayCommand(CanExecute = nameof(CanEvaluateSwift))]
    private async Task EvaluateSwiftPackageAsync()
    {
        if (Project is null || Analysis is null || SelectedSwiftPackage is not { } pkg) return;
        IsEvaluatingSwift = true;
        SwiftEvaluationError = null;
        SwiftEvaluationLog.Clear();
        HasSwiftEvaluationLog = false;
        _swiftEvalCts = new CancellationTokenSource();
        var keep = SelectedProject?.Path;
        try
        {
            var project = Project;
            var trusted = SwiftEvaluationTrusted;
            var token = _swiftEvalCts.Token;
            var result = await Task.Run(() => _swiftEvaluator.EvaluateAsync(project, pkg.Directory, trusted,
                line => Dispatcher.UIThread.Post(() => AppendEvaluationLog(line)), token));
            foreach (var line in result.Log.Where(l => !SwiftEvaluationLog.Contains(l)).Take(MaxEvaluationLogLines)) AppendEvaluationLog(line);
            if (result is { Succeeded: true, Package: { } evaluated })
            {
                var updated = SwiftPackageEvaluator.WithPackage(Analysis, evaluated);
                await _projectService.UpdateAnalysisAsync(project, updated);
                ApplyAnalysis(updated);
                SelectedProject = ProjectOptions.FirstOrDefault(p => p.Path == keep);
            }
            else
            {
                SwiftEvaluationError = result.Error ?? "Evaluation failed.";
            }
        }
        catch (OperationCanceledException) { SwiftEvaluationError = "Evaluation cancelled."; }
        catch (Exception ex) { SwiftEvaluationError = "Evaluation failed: " + ex.Message; }
        finally
        {
            IsEvaluatingSwift = false;
            _swiftEvalCts.Dispose();
            _swiftEvalCts = null;
            OnPropertyChanged(nameof(CanConfigure));
        }
    }

    private void AppendEvaluationLog(string line)
    {
        if (SwiftEvaluationLog.Count >= MaxEvaluationLogLines) return;
        SwiftEvaluationLog.Add(line);
        HasSwiftEvaluationLog = true;
    }

    private bool CanCancelSwiftEvaluation() => IsEvaluatingSwift;
    [RelayCommand(CanExecute = nameof(CanCancelSwiftEvaluation))] private void CancelSwiftEvaluation() => _swiftEvalCts?.Cancel();

    // ───────────── Configure: macOS app options ─────────────

    public IReadOnlyList<Choice<SwiftAppKind>> SwiftKindOptions { get; } =
    [
        new(SwiftAppKind.GuiApp, "macOS app (.app bundle) — SwiftUI / AppKit"),
        new(SwiftAppKind.CommandLine, "Command-line tool (executable in a folder / ZIP)"),
    ];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSwiftGui))] private Choice<SwiftAppKind>? _selectedSwiftKind;
    public bool IsSwiftGui => IsSwiftBuild && SelectedSwiftKind?.Value == SwiftAppKind.GuiApp;
    [ObservableProperty] private string _swiftBuildNumber = "1";
    [ObservableProperty] private string _swiftMinimumMacOS = "";
    [ObservableProperty] private string _swiftKindNote = "";
    public IReadOnlyList<Choice<string>> SwiftCategoryOptions { get; } =
    [
        new("", "None"),
        new("public.app-category.developer-tools", "Developer Tools"),
        new("public.app-category.productivity", "Productivity"),
        new("public.app-category.utilities", "Utilities"),
        new("public.app-category.business", "Business"),
        new("public.app-category.education", "Education"),
        new("public.app-category.graphics-design", "Graphics & Design"),
        new("public.app-category.music", "Music"),
        new("public.app-category.video", "Video"),
        new("public.app-category.finance", "Finance"),
        new("public.app-category.social-networking", "Social Networking"),
    ];
    [ObservableProperty] private Choice<string>? _selectedSwiftCategory;
    public ObservableCollection<SwiftUsageItem> SwiftUsageDescriptions { get; } = [];
    [ObservableProperty] private bool _hasSwiftUsageDescriptions;

    partial void OnSelectedSwiftKindChanged(Choice<SwiftAppKind>? value)
    {
        var suggested = SelectedSwiftProduct?.SuggestedKind;
        SwiftKindNote = value is null || suggested is null ? ""
            : value.Value == suggested ? $"Detected from the sources of {SelectedSwiftProduct!.TargetsText}: {SelectedSwiftProduct.Evidence}."
            : value.Value == SwiftAppKind.GuiApp
                ? "Overridden: the sources show no SwiftUI/AppKit entry point. A command-line program inside an .app opens no window."
                : "Overridden: the sources look like a SwiftUI/AppKit app. Started from Terminal it may not behave like a normal Mac app.";
        Validate();
    }
    partial void OnSwiftBuildNumberChanged(string value) => Validate();
    partial void OnSwiftMinimumMacOSChanged(string value) => Validate();

    private static readonly IReadOnlyList<Choice<TargetArchitecture>> DotNetArchitectures =
        [new(TargetArchitecture.X64, "x64 (Intel / AMD)"), new(TargetArchitecture.Arm64, "ARM64 (Apple Silicon / Windows on ARM)")];
    private static readonly IReadOnlyList<Choice<TargetArchitecture>> SwiftArchitectures =
    [
        new(TargetArchitecture.Arm64, "Apple Silicon (arm64)"),
        new(TargetArchitecture.X64, "Intel (x86_64)"),
        new(TargetArchitecture.Universal, "Universal (arm64 + x86_64)"),
    ];

    private void SetArchitectureOptions(bool swift)
    {
        var wanted = swift ? SwiftArchitectures : DotNetArchitectures;
        if (ArchitectureOptions.SequenceEqual(wanted)) return;
        var current = SelectedArchitecture?.Value;
        ArchitectureOptions.Clear();
        foreach (var a in wanted) ArchitectureOptions.Add(a);
        SelectedArchitecture = ArchitectureOptions.FirstOrDefault(a => a.Value == current)
                               ?? ArchitectureOptions.FirstOrDefault(a => a.Value == HostInfo.CurrentArchitecture) ?? ArchitectureOptions[0];
    }

    // ───────────── Packaging contract (AppPackaging.json) ─────────────

    /// <summary>The selected package's contract, when it describes the selected product.</summary>
    private SwiftPackagingContract? ActiveContract =>
        SelectedSwiftPackage?.PackagingContract is { } c && SelectedSwiftProduct is { } p && c.Product == p.Name ? c : null;
    [ObservableProperty] private bool _hasSwiftContract;
    [ObservableProperty] private string _swiftContractSummary = "";
    [ObservableProperty] private string? _swiftContractError;
    [ObservableProperty] private bool _hasSwiftContractCommands;
    [ObservableProperty] private bool _hasSwiftContractContents;
    /// <summary>Each prebuild command exactly as it will run, for review before confirming.</summary>
    public ObservableCollection<SwiftContractLine> SwiftContractCommands { get; } = [];
    /// <summary>What the contract adds to the .app: helpers and resources with their destinations.</summary>
    public ObservableCollection<string> SwiftContractContents { get; } = [];
    [ObservableProperty] private bool _swiftContractConfirmed;

    partial void OnSwiftContractConfirmedChanged(bool value) => Validate();

    private void RefreshSwiftContract(bool keepConfirmation = false)
    {
        SwiftContractCommands.Clear();
        SwiftContractContents.Clear();
        var pkg = SelectedSwiftPackage;
        SwiftContractError = pkg?.PackagingContractError;
        var contract = ActiveContract;
        HasSwiftContract = contract is not null || SwiftContractError is not null;
        if (!keepConfirmation) SwiftContractConfirmed = false;
        if (contract is null)
        {
            SwiftContractSummary = pkg?.PackagingContract is { } other && SelectedSwiftProduct is { } p
                ? $"{other.Path} describes the product {other.Product}; it does not apply to {p.Name}." : "";
            HasSwiftContract |= SwiftContractSummary.Length > 0;
            HasSwiftContractCommands = false;
            HasSwiftContractContents = false;
            return;
        }
        foreach (var step in contract.Prebuild)
        {
            var wd = step.WorkingDirectory == "." ? SwiftPackagingContractReader.ContractDirectory(contract.Path) : $"{SwiftPackagingContractReader.ContractDirectory(contract.Path)}/{step.WorkingDirectory}";
            SwiftContractCommands.Add(new SwiftContractLine(step.Description,
                string.Join(' ', step.Command.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a)),
                $"in {(wd.Length == 0 ? "(project root)" : wd)}" + (step.Requires.Count > 0 ? $" · requires {string.Join(", ", step.Requires)}" : "")));
        }
        foreach (var h in contract.Helpers)
            SwiftContractContents.Add($"{h.Destination}  ←  {h.Source}" + (h.Architectures.Count > 0 ? $"  ({string.Join(" + ", h.Architectures)})" : "") +
                (h.Verify.Contains("version") ? "  · checked with “version”" : "") + (h.Required ? "" : "  · optional"));
        foreach (var r in contract.Resources)
            SwiftContractContents.Add($"{r.Destination}  ←  {r.Source}" + (r.Required ? "" : "  · optional"));
        if (contract.Bundle.InfoPlist is { } plist) SwiftContractContents.Add($"Contents/Info.plist  ←  template {plist}");
        if (contract.Bundle.Icon is { } icon) SwiftContractContents.Add($"Contents/Resources/AppIcon.icns  ←  {icon}");
        HasSwiftContractCommands = SwiftContractCommands.Count > 0;
        HasSwiftContractContents = SwiftContractContents.Count > 0;
        SwiftContractSummary = $"{contract.Path} lists what a complete {contract.Product}.app contains. HCG follows it: " +
            (contract.Prebuild.Count > 0 ? $"{contract.Prebuild.Count} command(s) run before swift build, then " : "") +
            "the files below are placed in the app, signed (helpers before the app) and verified.";
    }

    /// <summary>Prefills the macOS app options for the chosen product (only when the product changes).</summary>
    /// <summary>Where the prefilled identity came from (e.g. the package's own packaging script).</summary>
    [ObservableProperty] private string _swiftPrefillNote = "";

    private void PrefillSwift(SwiftPackageInfo pkg, SwiftProductChoice product)
    {
        // Preserve the identity the project already ships with: its own packaging script (read as text, never run).
        PackagingScriptHints? hints = null;
        if (Project is not null)
        {
            try { hints = PackagingScriptHints.Read(SourceIndex.Build(Project.SourceDirectory), pkg.Directory, product.Name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hints = null; }
        }
        // A packaging contract (AppPackaging.json) is the project's declared identity; its Info.plist template too.
        var contract = pkg.PackagingContract is { } pc && pc.Product == product.Name ? pc : null;
        Dictionary<string, string> template = [];
        if (contract?.Bundle.InfoPlist is { } plistPath && Project is not null)
        {
            try
            {
                var full = SwiftPackagingContractReader.Resolve(Project.SourceDirectory, SwiftPackagingContractReader.ContractDirectory(contract.Path), plistPath, "bundle.infoPlist");
                if (File.Exists(full)) template = HCG.ExecutableMaker.Packaging.Packagers.InfoPlist.Read(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException or SwiftPackagingContractReader.ContractException) { template = []; }
        }
        static string? Real(string? v, string placeholder) => v is not null && v != placeholder && Regex.IsMatch(v, @"^\d+(\.\d+){0,2}$") ? v : null;
        var templateVersion = Real(template.GetValueOrDefault("CFBundleShortVersionString"), "0.0.0");
        var templateBuild = Real(template.GetValueOrDefault("CFBundleVersion"), "0");
        AppName = contract?.Bundle.DisplayName ?? template.GetValueOrDefault("CFBundleDisplayName") ?? hints?.DisplayName ?? product.Name;
        Version = templateVersion ?? (hints?.Version is { } v && Regex.IsMatch(v, @"^\d+(\.\d+){0,2}$") ? v : "1.0.0");
        Description = "";
        AppId = contract?.Bundle.Identifier ?? template.GetValueOrDefault("CFBundleIdentifier") ?? hints?.BundleId ?? SuggestAppId(Publisher, product.Name);
        SwiftBuildNumber = templateBuild ?? (hints?.BuildNumber is { } bn && Regex.IsMatch(bn, @"^\d+(\.\d+){0,2}$") ? bn : "1");
        SwiftMinimumMacOS = contract?.Bundle.MinimumMacOS ?? template.GetValueOrDefault("LSMinimumSystemVersion") ?? hints?.MinimumMacOS ?? pkg.Evaluated?.MinimumMacOS ?? "";
        var category = template.GetValueOrDefault("LSApplicationCategoryType") ?? hints?.Category ?? "";
        SelectedSwiftCategory = SwiftCategoryOptions.FirstOrDefault(c => c.Value == category) ?? SwiftCategoryOptions[0];
        SelectedSwiftKind = SwiftKindOptions.First(k => k.Value == product.SuggestedKind);
        foreach (var old in SwiftUsageDescriptions) old.PropertyChanged -= OnUsageChanged;
        SwiftUsageDescriptions.Clear();
        foreach (var u in product.Facts?.PrivacyUsages ?? [])
            AddUsage(new SwiftUsageItem(u.InfoPlistKey, u.Purpose, u.Evidence, hints?.UsageDescriptions.GetValueOrDefault(u.InfoPlistKey) ?? ""));
        foreach (var (key, text) in template.Where(k => k.Key.StartsWith("NS", StringComparison.Ordinal) && k.Key.EndsWith("UsageDescription", StringComparison.Ordinal)))
        {
            if (SwiftUsageDescriptions.FirstOrDefault(x => x.Key == key) is { } existing) { if (existing.Text.Length == 0) existing.Text = text; }
            else AddUsage(new SwiftUsageItem(key, key, "declared in " + contract!.Bundle.InfoPlist, text));
        }
        // Usage keys the script declares that the source scan did not detect are kept too (the script knows best).
        foreach (var (key, text) in hints?.UsageDescriptions ?? new Dictionary<string, string>())
            if (SwiftUsageDescriptions.All(x => x.Key != key)) AddUsage(new SwiftUsageItem(key, key, "declared in " + hints!.Script, text));
        HasSwiftUsageDescriptions = SwiftUsageDescriptions.Count > 0;
        RefreshSwiftContract();
        if (contract is not null)
        {
            SwiftPrefillNote = $"Prefilled from the project's packaging contract {contract.Path}" +
                (contract.Bundle.InfoPlist is { } tp ? $" and its Info.plist template {tp}" : "") + " (read, not run)." +
                (templateVersion is null ? " The template has no real version, so check the version above: helpers checked with “version” must report the same one." : "");
            return;
        }
        SwiftPrefillNote = hints is null
            ? "No packaging script with an app identity was found; the identity below is a suggestion."
            : $"Prefilled from the project's own packaging script {hints.Script} (read, not run): bundle id {hints.BundleId}" +
              (hints.Version is null ? "" : $", version {hints.Version} ({hints.BuildNumber})") + "." +
              (hints.ComputedCustomKeys.Count > 0 ? $" That script also writes {string.Join(", ", hints.ComputedCustomKeys)} at build time (e.g. its git commit); HCG does not add those keys, so the app shows them as missing." : "");
    }

    private void AddUsage(SwiftUsageItem item)
    {
        item.PropertyChanged += OnUsageChanged;
        SwiftUsageDescriptions.Add(item);
    }

    private void OnUsageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Validate();

    private SwiftBuildOptions? CreateSwiftOptions()
    {
        if (!IsSwiftBuild || SelectedSwiftPackage is not { } pkg || SelectedSwiftProduct is not { } product) return null;
        return new SwiftBuildOptions
        {
            PackageDirectory = pkg.Directory,
            Product = product.Name,
            Kind = SelectedSwiftKind?.Value ?? product.SuggestedKind,
            BuildNumber = SwiftBuildNumber.Trim(),
            MinimumMacOS = string.IsNullOrWhiteSpace(SwiftMinimumMacOS) ? null : SwiftMinimumMacOS.Trim(),
            Category = string.IsNullOrEmpty(SelectedSwiftCategory?.Value) ? null : SelectedSwiftCategory!.Value,
            UsageDescriptions = SwiftUsageDescriptions.ToDictionary(u => u.Key, u => u.Text.Trim()),
            PackagingContract = ActiveContract,
            PackagingContractConfirmed = ActiveContract is not null && SwiftContractConfirmed,
        };
    }

    /// <summary>Swift-specific configuration checks beyond <see cref="SwiftBuildPlanner.Validate"/>.</summary>
    private void ValidateSwift(BuildSettings settings)
    {
        try { SwiftBuildPlanner.Validate(settings, HostInfo.CurrentPlatform); }
        catch (BuildValidationException ex) { ConfigErrors.Add(ex.Message); }
        if (SelectedSwiftPackage?.PackagingContractError is { } contractError)
            ConfigErrors.Add($"The packaging contract cannot be used: {contractError} Fix the file, then press Re-inspect.");
        try { SwiftContractPlanner.Validate(settings); }
        catch (BuildValidationException ex) { ConfigErrors.Add(ex.Message); }
        if (string.IsNullOrWhiteSpace(settings.Identity.ApplicationName)) ConfigErrors.Add("Enter the app's display name.");
        var packageMin = SelectedSwiftPackage?.Evaluated?.MinimumMacOS;
        if (settings.Swift?.MinimumMacOS is { } min && packageMin is not null && SwiftCapabilities.Compare(min, packageMin) < 0)
            ConfigErrors.Add($"Minimum macOS {min} is lower than the package's declared macOS {packageMin}. The executable is compiled for {packageMin}+ and would not start on older systems.");
    }

    private void ApplySwiftSettings(SwiftBuildOptions s)
    {
        var product = SwiftExecutableProducts.FirstOrDefault(p => p.Name == s.Product);
        if (product is null)
        {
            // Repeating a build of a package that is not evaluated in this session: keep the recorded product.
            product = new SwiftProductChoice(s.Product, [], null, s.Kind);
            SwiftExecutableProducts.Add(product);
        }
        SelectedSwiftProduct = product;
        SetArchitectureOptions(true);
        SelectedSwiftKind = SwiftKindOptions.First(k => k.Value == s.Kind);
        SwiftBuildNumber = s.BuildNumber;
        SwiftMinimumMacOS = s.MinimumMacOS ?? "";
        SelectedSwiftCategory = SwiftCategoryOptions.FirstOrDefault(c => c.Value == (s.Category ?? "")) ?? SwiftCategoryOptions[0];
        foreach (var old in SwiftUsageDescriptions) old.PropertyChanged -= OnUsageChanged;
        SwiftUsageDescriptions.Clear();
        foreach (var (key, text) in s.UsageDescriptions) AddUsage(new SwiftUsageItem(key, key, "from the previous build", text));
        HasSwiftUsageDescriptions = SwiftUsageDescriptions.Count > 0;
        // A repeated build keeps its confirmation only while the contract file is byte-for-byte the one confirmed.
        RefreshSwiftContract();
        SwiftContractConfirmed = s.PackagingContractConfirmed && ActiveContract is { } c && c.Sha256 == s.PackagingContract?.Sha256;
    }

    private static string TargetLabel(BuildSettings s) =>
        s.Swift is null ? $"{s.Platform.DisplayName()} {s.Architecture.DisplayName()} ({s.RuntimeIdentifier})"
        : $"{s.Platform.DisplayName()} · {string.Join(" + ", HCG.ExecutableMaker.Packaging.Verification.MachO.ExpectedSlices(s.Architecture))}";
}

/// <summary>An executable product from the evaluated package, with what its sources suggest.</summary>
public sealed class SwiftProductChoice(string name, IReadOnlyList<string> targets, SwiftTargetSourceFacts? facts, SwiftAppKind? recordedKind = null)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Targets { get; } = targets;
    public SwiftTargetSourceFacts? Facts { get; } = facts;
    public SwiftAppKind SuggestedKind { get; } = facts?.SuggestedKind ?? recordedKind ?? SwiftAppKind.CommandLine;
    public string KindText => SuggestedKind == SwiftAppKind.GuiApp ? "macOS app (SwiftUI/AppKit)" : "command-line tool";
    public string TargetsText => Targets.Count == 0 ? "the recorded product" : "target " + string.Join(", ", Targets);
    public string Evidence => Facts is null ? "recorded in the previous build"
        : Facts.HasGuiEntryPoint ? "GUI entry point (@main … App / NSApplication)"
        : Facts.ImportsSwiftUI || Facts.ImportsAppKit ? $"imports {(Facts.ImportsSwiftUI ? "SwiftUI" : "AppKit")}"
        : "no SwiftUI/AppKit import";
    public string Display => $"{Name}  ·  {KindText}";
    public string Detail => $"{TargetsText} · {Evidence}" + (Facts is { PrivacyUsages.Count: > 0 } f ? $" · uses {string.Join(", ", f.PrivacyUsages.Select(u => u.Purpose))}" : "");
    public override string ToString() => Display;
}

/// <summary>An Info.plist privacy usage description the user must write (macOS shows it in the permission prompt).</summary>
public sealed partial class SwiftUsageItem(string key, string purpose, string evidence, string text) : ObservableObject
{
    public string Key { get; } = key;
    public string Purpose { get; } = purpose;
    public string Evidence { get; } = evidence;
    public string Heading => $"{Key} — {Purpose}";
    [ObservableProperty] private string _text = text;
}

/// <summary>One prebuild command of a packaging contract, shown verbatim for review.</summary>
public sealed record SwiftContractLine(string Description, string Command, string Detail);
