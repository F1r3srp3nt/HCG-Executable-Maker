using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// The HCG account pages as a user meets them in this build: an unsupported project offers diagnostics and a support
/// request; the request form lists exactly what would be sent; Account / My requests / AI assistance explain their
/// state (sign-in not configured, AI not connected). Nothing is sent: no HCG client is registered for this build.
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunAccountPagesAsync(MainWindowViewModel main, BuildWizardViewModel wizard)
    {
        var a = wizard.Analysis!;
        Log($"Inspection: {a.InspectorId} · {a.Support} — {a.SupportSummary}");
        Check(a.Support != SupportLevel.Supported, "The project is not buildable as it is");
        await Task.Delay(300);
        Check(wizard.ShowSupportActions, "Unsupported result offers diagnostics and a support request");
        Check(!wizard.AiAnalysisAvailable, $"AI analysis is not offered: {wizard.AiAnalysisNote}");
        Shot("inspect-unsupported-actions");
        ShotScrolledToEnd("inspect-unsupported-actions-end");

        wizard.RequestProjectSupportCommand.Execute(null);
        await WaitUntil(() => main.CurrentPage is SupportViewModel, TimeSpan.FromSeconds(10), "support page");
        var support = (SupportViewModel)main.CurrentPage!;
        Log($"Support availability: {support.Availability}");
        foreach (var d in support.Diagnostics) Log($"Diagnostic: [{d.Section}] {d.Label}: {d.Value}");
        Check(support.Diagnostics.Count > 0, "Diagnostics listed before anything is sent");
        Check(!support.SubmitCommand.CanExecute(null), "Submit is disabled without HCG sign-in (explained above it)");
        support.Goal = Arg("goal", "Build this project as a desktop app");
        support.TargetOs = "macOS";
        support.OutputFormat = ".app";
        Shot("support-diagnostics-only");
        support.IncludeFiles = true;
        foreach (var f in support.Files.Where(f => f.IsExcludedByDefault).Take(40)) Log($"Excluded by default: {f.Path} — {f.Explanation}");
        foreach (var d in support.SkippedFolders) Log("Skipped: " + d);
        Log("Summary: " + support.Summary);
        Check(support.Files.Any(f => f.Included), "Project files are listed for review");
        Check(support.Files.Where(f => !f.CanInclude).All(f => !f.Included), "Credentials and signing material cannot be included");
        Shot("support-with-files");
        ShotScrolledToEnd("support-review");

        foreach (var (key, name) in new[] { ("account", "account"), ("requests", "my-requests"), ("ai", "ai-assistance") })
        {
            await main.NavigateAsync(key, null);
            await Task.Delay(500);
            switch (main.CurrentPage)
            {
                case AccountViewModel acc:
                    Log($"Account: [{acc.AccountTag.Text}] {acc.AccountHeadline} — {acc.AccountMessage}");
                    Log("Configuration: " + acc.ConfigurationHelp);
                    Check(acc.IsNotConfigured, "Sign-in shows 'Not available yet' (no registered client) and contacts nothing");
                    break;
                case MyRequestsViewModel req:
                    Log("My requests: " + req.State);
                    Check(req.State.Length > 0, "My requests explains why it is empty");
                    break;
                case AiAssistantViewModel ai:
                    Log($"AI: [{ai.Connection.Text}] {ai.ConnectionMessage}");
                    Check(ai.Connection.Text == "Not connected", "AI assistance shows Not connected (no gateway contract), never a fabricated answer");
                    break;
            }
            Shot(name);
        }
        return 0;
    }
}
