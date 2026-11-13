using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account.Ai;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// HCG support and AI entry points in the wizard: on an unsupported (or not-yet-ready) inspection result and on a
/// failed build. They only navigate; nothing is sent from here.
/// </summary>
public sealed partial class BuildWizardViewModel
{
    private AiAssistant? Ai => App.Services?.GetService<AiAssistant>();

    /// <summary>The project cannot be built as it is: offer diagnostics, a support request and (when available) AI analysis.</summary>
    public bool ShowSupportActions => Analysis is { } a && a.Support != SupportLevel.Supported && a.Support != SupportLevel.MissingToolchain && !HasConvertibleWebProjects;
    [ObservableProperty] private bool _aiAnalysisAvailable;
    [ObservableProperty] private string _aiAnalysisNote = "";
    [ObservableProperty] private bool _aiExplainAvailable;
    [ObservableProperty] private string _aiExplainNote = "";

    private async Task RefreshHcgActionsAsync()
    {
        OnPropertyChanged(nameof(ShowSupportActions));
        OnPropertyChanged(nameof(ShowBuildFailureActions));
        if (Ai is not { } ai) return;
        var status = await ai.StatusAsync();
        var explain = status.Capabilities.FirstOrDefault(c => c.Id == AiCapabilities.ExplainBuildError);
        var analyse = status.Capabilities.FirstOrDefault(c => c.Id == AiCapabilities.AnalyseUnsupportedProject);
        AiAnalysisAvailable = status.State == AiConnectionState.Available && analyse is { Available: true };
        AiExplainAvailable = status.State == AiConnectionState.Available && explain is { Available: true };
        AiAnalysisNote = status.State switch
        {
            AiConnectionState.NotConnected => "AI analysis: HCG AI is not connected yet.",
            AiConnectionState.SignedOut => "AI analysis needs HCG sign-in.",
            AiConnectionState.NotPermitted => "AI analysis is not granted to your HCG account.",
            AiConnectionState.Unavailable => "AI analysis: HCG AI is unavailable right now.",
            _ => AiAnalysisAvailable ? "" : "AI analysis of unsupported projects is not enabled by HCG yet.",
        };
        AiExplainNote = AiExplainAvailable ? "" : status.State switch
        {
            AiConnectionState.NotConnected => "“Explain this build error” needs HCG AI, which is not connected yet. You can request support from HCG instead.",
            AiConnectionState.SignedOut => "Sign in with HCG to use “Explain this build error” or to request support.",
            AiConnectionState.NotPermitted => "HCG has not granted AI assistance to your account. You can request support instead.",
            _ => "“Explain this build error” is unavailable right now. You can request support instead.",
        };
    }

    partial void OnAnalysisChanged(ProjectAnalysis? value) => _ = RefreshHcgActionsAsync();
    partial void OnResultChanged(BuildRecord? value)
    {
        _ = RefreshHcgActionsAsync();
        RefreshNativeState();
        OnPropertyChanged(nameof(ShowDistributionSteps));
    }
    partial void OnIsBuildingChanged(bool value) => RefreshNativeState();

    [RelayCommand]
    private async Task ViewDiagnosticsAsync()
    {
        if (Analysis is not { } a) return;
        var text = string.Join("\n", a.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}{(d.Path is null ? "" : $" ({d.Path})")}"));
        await _dialogs.AlertAsync("Diagnostics", a.SupportSummary + "\n\n" + (text.Length == 0 ? "No further findings." : text));
    }

    [RelayCommand]
    private void RequestProjectSupport() => _navigation.Navigate("support", new SupportRequestContext(Project, Analysis));

    [RelayCommand]
    private void AnalyseWithAi() => _navigation.Navigate("ai", new AiRequestContext(null, null, Analysis));

    public bool ShowBuildFailureActions => Result is { Status: BuildStatus.Failed };

    [RelayCommand]
    private async Task ExplainBuildErrorAsync()
    {
        if (Result is not { } build) return;
        _navigation.Navigate("ai", new AiRequestContext(build, await ReadBuildLogAsync(build), Analysis));
    }

    [RelayCommand]
    private async Task RequestBuildSupportAsync()
    {
        if (Result is not { } build) return;
        _navigation.Navigate("support", new SupportRequestContext(Project, Analysis, build, await ReadBuildLogAsync(build)));
    }

    private static async Task<IReadOnlyList<string>> ReadBuildLogAsync(BuildRecord build)
    {
        if (build.LogPath is not { } p || !File.Exists(p)) return [];
        return (await File.ReadAllLinesAsync(p)).TakeLast(Account.Support.DiagnosticsCollector.MaxLogLines).ToList();
    }
}
