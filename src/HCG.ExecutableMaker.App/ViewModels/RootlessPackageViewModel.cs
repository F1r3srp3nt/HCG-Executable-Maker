using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Packaging.Jailbreak;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class StageItem(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private StatusTag _tag = new("Waiting", "neutral");
}

/// <summary>
/// Rootless package: Inspect the iOS app a build produced → Package it as a rootless jailbreak .deb → Validate →
/// Export the HCG Repo candidate handoff. The same <see cref="RootlessAppWorkflow"/> as <c>--rootless-deb</c>.
/// Nothing is installed, uploaded or published, and the app's capabilities are not changed.
/// </summary>
public sealed partial class RootlessPackageViewModel(
    IProcessRunner runner,
    IToolchainDetector toolchains,
    IPlatformShell shell) : PageViewModel
{
    public override string Title => "Rootless package";
    public override string Subtitle => "Package an iOS app a build produced as a rootless jailbreak .deb, validate it, and export an HCG Repo candidate handoff.";

    public static IReadOnlyList<string> AccessLevels { get; } = ["public", "connect", "paid", "undecided"];

    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _packageId = "";
    [ObservableProperty] private string _packageVersion = "";
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private string _maintainer = RootlessAppPackager.HcgMaintainer;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _deviceIos = "16.5";
    [ObservableProperty] private string _deviceTarget = "iPhone 13 Pro, iOS 16.5, Dopamine (rootless)";
    [ObservableProperty] private string _productionAccess = "public";
    [ObservableProperty] private string _repository = "";
    [ObservableProperty] private string _commit = "";
    [ObservableProperty] private string _ciRun = "";
    [ObservableProperty] private string _ciArtifact = "";
    [ObservableProperty] private string _ciRepository = "";
    [ObservableProperty] private string _ciCommit = "";
    [ObservableProperty] private string _changes = "";
    [ObservableProperty] private string _knownIssues = "";
    [ObservableProperty] private string _outputDir = "";
    [ObservableProperty] private string? _ldid;
    [ObservableProperty] private string _appSummary = "No app inspected yet.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string? _result;
    [ObservableProperty] private StatusTag _resultTag = new("Not run", "neutral");
    [ObservableProperty] private string? _handoffFolder;

    public ObservableCollection<StageItem> Stages { get; } = [new("Inspect"), new("Package"), new("Validate"), new("Handoff")];
    public ObservableCollection<CheckItem> Checks { get; } = [];
    public ObservableCollection<string> Missing { get; } = [];
    public string LdidStatus => Ldid is null ? "ldid not found: needed only when the build's Mach-O files are unsigned (macOS: brew install ldid)" : "ldid: " + Ldid;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is string path && path.Length > 0) Source = path;
        try
        {
            var report = await toolchains.DetectAsync(CancellationToken.None);
            Ldid = report.Find(ToolchainIds.Ldid) is { IsAvailable: true, ExecutablePath: { } p } ? p : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Ldid = null; }
        OnPropertyChanged(nameof(LdidStatus));
        if (Source.Length > 0) Inspect();
    }

    [RelayCommand]
    private async Task BrowseAppAsync()
    {
        if (await shell.PickFolderAsync("Choose the device .app or .xcarchive a build produced") is { } folder) { Source = folder; Inspect(); }
    }

    [RelayCommand]
    private async Task BrowseIpaAsync()
    {
        if (await shell.PickFileAsync("Choose an .ipa a build produced", "iOS app archive", "*.ipa") is { } file) { Source = file; Inspect(); }
    }

    [RelayCommand]
    private void Inspect()
    {
        if (Source.Length == 0) return;
        try
        {
            var app = IosAppBundle.Open(Source, Path.Combine(Path.GetTempPath(), "hcg-rootless-inspect"));
            var signed = app.Binaries.Count(b => b.Slices.All(s => s.HasCodeSignature));
            AppSummary = $"{app.Name} · {app.BundleId} · version {app.ShortVersion ?? "?"} ({app.BuildVersion ?? "?"}) · iOS {app.MinimumOS ?? "?"} or later · {app.Binaries.Count} Mach-O file(s), {signed} signed"
                         + (app.BuildInfo.TryGetValue("DTXcode", out var x) ? $" · Xcode {x}" : "")
                         + (app.Problems.Count > 0 ? "\nProblems: " + string.Join(" ", app.Problems) : "");
            if (AppName.Length == 0) AppName = app.Name;
            if (PackageVersion.Length == 0 && app.ShortVersion is { } v) PackageVersion = v + "~beta1";
            if (OutputDir.Length == 0) OutputDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Source).TrimEnd(Path.DirectorySeparatorChar)) ?? Path.GetTempPath(), "rootless-deb");
            Stages[0].Tag = app.Problems.Count == 0 ? new("Inspected", "success") : new("Problems", "error");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or InvalidCastException)
        {
            AppSummary = "Cannot use this input: " + ex.Message;
            Stages[0].Tag = new("Failed", "error");
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        IsBusy = true;
        Checks.Clear();
        Missing.Clear();
        Log = "";
        Result = null;
        HandoffFolder = null;
        foreach (var s in Stages) s.Tag = new("Waiting", "neutral");
        var lines = new List<string>();
        try
        {
            var spec = new RootlessAppPackageSpec(PackageId.Trim(), PackageVersion.Trim(), AppName.Trim(), Maintainer.Trim(), Description.Trim());
            var prov = new PackageProvenance(Repository.Trim() is { Length: > 0 } r ? r : null, Commit.Trim() is { Length: > 0 } sha ? sha : null, Commit.Trim().Length > 0 ? false : null,
                long.TryParse(CiRun.Trim(), out var run) ? run : null, CiArtifact.Trim() is { Length: > 0 } a ? a : null, null, null, HostInfo.BuildIdentity,
                CiRepository.Trim() is { Length: > 0 } cr ? cr : null, CiCommit.Trim() is { Length: > 0 } cc ? cc : null);
            var texts = new CandidateTexts(Description.Split('\n')[0].Trim(), Split(Changes), Split(KnownIssues), ProductionAccess, DeviceTarget.Trim() is { Length: > 0 } dt ? dt : null);
            var request = new RootlessAppWorkflow.Request(Source, OutputDir.Length > 0 ? OutputDir : Path.Combine(Path.GetTempPath(), "rootless-deb"), spec, prov, texts, Ldid,
                DeviceIos.Trim() is { Length: > 0 } d ? d : null);
            var outcome = await Task.Run(() => new RootlessAppWorkflow(runner, TimeProvider.System).RunAsync(request, (stage, line) => Dispatcher.UIThread.Post(() =>
            {
                lines.Add($"[{stage}] {line}");
                Log = string.Join("\n", lines.TakeLast(400));
                var i = (int)stage;
                if (i < Stages.Count)
                {
                    for (var k = 0; k < i; k++) if (Stages[k].Tag.Tone is "neutral" or "info") Stages[k].Tag = new("Done", "success");
                    Stages[i].Tag = new("Running", "info");
                }
            }), CancellationToken.None));
            await Dispatcher.UIThread.InvokeAsync(() => { });   // let posted log lines land first
            foreach (var c in outcome.Validation?.Checks ?? []) Checks.Add(new CheckItem(c));
            foreach (var m in outcome.Handoff?.Missing ?? []) Missing.Add(m);
            for (var k = 0; k < Stages.Count; k++)
                Stages[k].Tag = k < (int)outcome.ReachedStage || outcome.Succeeded ? new("Done", "success")
                    : k == (int)outcome.ReachedStage ? new("Failed", "error") : new("Not run", "neutral");
            if (outcome.Succeeded)
            {
                var f = outcome.Validation!.Facts;
                Result = $"{outcome.Package!.DebPath}\nSHA-256 {f.Sha256} · {f.Size:N0} bytes";
                ResultTag = new(outcome.Handoff?.ImportRequestComplete == true ? "Ready for HCG Repo review" : "Built and validated · import request incomplete", "success");
                HandoffFolder = outcome.Handoff?.Folder;
            }
            else
            {
                Result = outcome.Failure;
                ResultTag = new($"Stopped at {outcome.ReachedStage}", "error");
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenOutputAsync()
    {
        if ((HandoffFolder ?? OutputDir) is { Length: > 0 } dir && Directory.Exists(dir)) await shell.OpenFolderAsync(dir);
    }

    private static List<string> Split(string text) => text.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('-', '•').Trim()).Where(l => l.Length > 0).ToList();
}
