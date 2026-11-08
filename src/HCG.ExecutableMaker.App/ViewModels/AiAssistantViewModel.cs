using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Ai;
using HCG.ExecutableMaker.Account.Support;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>What to explain: a failed build (with its log lines) or an inspection result.</summary>
public sealed record AiRequestContext(BuildRecord? Build, IReadOnlyList<string>? LogLines, ProjectAnalysis? Analysis);

public sealed record CapabilityRow(AiCapability Capability)
{
    public string Title => Capability.Title;
    public StatusTag State => Capability.Available ? new StatusTag("Available", "success") : new StatusTag(Capability.Reason ?? "Unavailable", "neutral");
    public string Quota => Capability.Quota is { } q && q.Limit is { } l ? $"{q.Remaining ?? 0} of {l} left" : "";
}

/// <summary>
/// HCG AI assistance. Shows "Not connected" until HCG publishes the AI gateway contract (the case today). When
/// connected: the user reviews the exact payload, approves it, and gets suggestions to read — nothing is run, installed,
/// changed or published from an answer.
/// </summary>
public sealed partial class AiAssistantViewModel(AiAssistant ai, HcgAccountService account, IBuildRepository builds, NavigationService navigation) : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => "AI assistance";
    public override string Subtitle => "Explanations and suggestions from HCG AI, governed by your HCG account.";

    public string Architecture => "Your HCG account supplies identity; HCG ERP decides whether you may use AI and how much; HCG AI runs the models. " +
                                  "HCG Executable Maker holds no AI provider key and cannot grant itself access.";
    [ObservableProperty] private StatusTag _connection = new("—", "neutral");
    [ObservableProperty] private string _connectionMessage = "";
    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];
    public ObservableCollection<DiagnosticEntry> Payload { get; } = [];
    [ObservableProperty] private string _payloadSummary = "";
    [ObservableProperty] private string _question = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _approved;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _canRun;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand))] private bool _isRunning;
    [ObservableProperty] private string? _answer;
    [ObservableProperty] private string? _failure;
    [ObservableProperty] private bool _hasPayload;
    public ObservableCollection<AiSuggestion> Suggestions { get; } = [];
    private AiPayload? _payload;
    private AiRequestContext? _context;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is AiRequestContext ctx) _context = ctx;
        else if (parameter is BuildRecord b) _context = new AiRequestContext(b, await ReadLogAsync(b), null);
        else if (_context is null)
        {
            var latest = (await builds.ListAsync(null)).FirstOrDefault(x => x.Status == BuildStatus.Failed);
            if (latest is not null) _context = new AiRequestContext(latest, await ReadLogAsync(latest), null);
        }
        await RefreshAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadLogAsync(BuildRecord b)
    {
        if (b.LogPath is not { } p || !File.Exists(p)) return [];
        var lines = await File.ReadAllLinesAsync(p);
        return lines.TakeLast(DiagnosticsCollector.MaxLogLines).ToList();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var status = await ai.StatusAsync();
        Connection = status.State switch
        {
            AiConnectionState.Available => new StatusTag("Connected", "success"),
            AiConnectionState.NotConnected => new StatusTag("Not connected", "neutral"),
            AiConnectionState.SignedOut => new StatusTag("Sign in required", "neutral"),
            AiConnectionState.NotPermitted => new StatusTag("Not granted", "warning"),
            _ => new StatusTag("HCG AI unavailable", "warning"),
        };
        ConnectionMessage = status.Message;
        Capabilities.Clear();
        foreach (var c in status.Capabilities) Capabilities.Add(new CapabilityRow(c));
        _payload = _context?.Build is { } build
            ? AiPayloadBuilder.ExplainBuildError(build, _context.LogLines ?? [], string.IsNullOrWhiteSpace(Question) ? null : Question)
            : _context?.Analysis is { } a ? AiPayloadBuilder.AnalyseUnsupportedProject(a, HostInfo.Detect(), HostInfo.ProductVersion) : null;
        Payload.Clear();
        foreach (var i in _payload?.Items ?? []) Payload.Add(i);
        HasPayload = _payload is not null;
        PayloadSummary = _payload is null
            ? "Nothing selected. Open a failed build's results and choose “Explain this build error”."
            : $"{AiCapabilities.Title(_payload.Capability)} · {Payload.Count} item(s), {Format.Bytes(_payload.SizeBytes)}. Secrets are masked and your home folder is shown as ~. " +
              "Logs are sent as data marked untrusted: they cannot change what the request may do. No source files are sent for this action.";
        CanRun = status.State == AiConnectionState.Available && _payload is not null;
    }

    private bool CanRunNow() => CanRun && Approved && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private async Task RunAsync()
    {
        if (_payload is null) return;
        Failure = null;
        Answer = null;
        Suggestions.Clear();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        try
        {
            var result = await ai.RunAsync(_payload, userApproved: Approved, _cts.Token);
            Answer = result.Explanation;
            foreach (var s in result.Suggestions) Suggestions.Add(s);
        }
        catch (AiException ex) { Failure = ex.Message; }
        catch (OperationCanceledException) { Failure = "Cancelled."; }
        finally
        {
            IsRunning = false;
            Approved = false; // each send needs a new approval
        }
    }

    private bool CanCancelNow() => IsRunning;
    [RelayCommand(CanExecute = nameof(CanCancelNow))] private void Cancel() => _cts?.Cancel();
    [RelayCommand] private void OpenAccount() => navigation.Navigate("account");
    [RelayCommand] private void RequestSupport() => navigation.Navigate("support", new SupportRequestContext(null, _context?.Analysis, _context?.Build, _context?.LogLines));
}
