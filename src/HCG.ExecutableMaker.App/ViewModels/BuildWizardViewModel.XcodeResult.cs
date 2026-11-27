using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>One issue of an Xcode build, as the diagnostics report states it.</summary>
public sealed record XcodeIssueItem(string Severity, string Category, string Message, string Location, string? Target)
{
    public bool IsError => Severity == "error";
    public StatusTag Tag => new(Category, Severity == "error" ? "error" : "warning");
    public string Detail => string.Join(" · ", new[] { Location, Target is null ? null : "target " + Target }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>
/// The Results step for a native macOS Xcode build: the diagnostics report HCG wrote next to build.log
/// (hcg.build-diagnostics v1). It lists every compiler error and warning with file and line, failed targets and
/// commands, test failures and the duration, and offers the report to view, copy for a development agent, or open in
/// its folder. A failed build is shown as failed; nothing here changes the verdict.
/// </summary>
public sealed partial class BuildWizardViewModel
{
    [ObservableProperty] private bool _hasXcodeDiagnostics;
    [ObservableProperty] private string _xcodeDiagnosticsSummary = "";
    [ObservableProperty] private string _xcodeFailedTargets = "";
    [ObservableProperty] private string _xcodeSigningStatus = "";
    public ObservableCollection<XcodeIssueItem> XcodeIssues { get; } = [];
    private string? _xcodeDiagnosticsMarkdown;

    private void LoadXcodeDiagnostics(BuildRecord build)
    {
        XcodeIssues.Clear();
        HasXcodeDiagnostics = false;
        _xcodeDiagnosticsMarkdown = null;
        XcodeSigningStatus = build.Messages.FirstOrDefault(m => m.Code == BuildEngine.BuildService.SigningStatusCode)?.Text ?? "";
        if (build.Settings.MacXcode is null || build.LogPath is null) return;
        var folder = Path.GetDirectoryName(build.LogPath)!;
        var json = Path.Combine(folder, XcodeDiagnostics.JsonFileName);
        var md = Path.Combine(folder, XcodeDiagnostics.MarkdownFileName);
        if (!File.Exists(json)) return;
        try
        {
            var d = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
            var counts = d["counts"]!;
            var duration = d["durationSeconds"]?.GetValue<double>();
            XcodeDiagnosticsSummary = $"{d["status"]?.GetValue<string>()} · {counts["errors"]} error(s), {counts["warnings"]} warning(s), {counts["testFailures"]} test failure(s)"
                + (duration is { } s ? $" · {TimeSpan.FromSeconds(s):hh\\:mm\\:ss}" : "")
                + (d["xcodebuildResults"] is JsonArray r && r.Count > 0 ? " · " + string.Join(" ", r.Select(x => x!.GetValue<string>())) : "");
            var failed = d["failedTargets"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            XcodeFailedTargets = failed.Count == 0 ? "" : "Failed targets: " + string.Join(", ", failed);
            foreach (var i in d["issues"]!.AsArray().Select(x => x!.AsObject()).OrderByDescending(x => x["severity"]!.GetValue<string>() == "error").Take(200))
            {
                var file = i["file"]?.GetValue<string>();
                var line = i["line"]?.GetValue<int>();
                var col = i["column"]?.GetValue<int>();
                XcodeIssues.Add(new XcodeIssueItem(i["severity"]!.GetValue<string>(), i["category"]!.GetValue<string>(), i["message"]!.GetValue<string>(),
                    file is null ? "" : line is null ? file : col is null ? $"{file}:{line}" : $"{file}:{line}:{col}", i["target"]?.GetValue<string>()));
            }
            _xcodeDiagnosticsMarkdown = File.Exists(md) ? md : null;
            HasXcodeDiagnostics = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            XcodeDiagnosticsSummary = "The diagnostics report could not be read: " + ex.Message;
            HasXcodeDiagnostics = true;
        }
    }

    [RelayCommand]
    private async Task ViewXcodeDiagnosticsAsync()
    {
        if (_xcodeDiagnosticsMarkdown is { } md) await _dialogs.ShowLogAsync("Xcode build diagnostics", md);
    }

    /// <summary>Copies the Markdown report (errors with source excerpts, failed commands, tests) for a development agent.</summary>
    [RelayCommand]
    private async Task CopyXcodeDiagnosticsAsync()
    {
        if (_xcodeDiagnosticsMarkdown is { } md && File.Exists(md)) await _shell.CopyAsync(await File.ReadAllTextAsync(md));
    }

    [RelayCommand]
    private async Task OpenXcodeDiagnosticsFolderAsync()
    {
        if (_xcodeDiagnosticsMarkdown is { } md) await _shell.OpenFolderAsync(Path.GetDirectoryName(md)!);
    }
}
