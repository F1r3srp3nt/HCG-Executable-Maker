using System.Text.Json;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// Mobile Build Studio driven like a user: the project imported in New Build is opened in Mobile Build Studio, the
/// output (simulator / archive / ipa), scheme and signing are chosen, readiness and the precise state are recorded, and
/// the build runs after the trust confirmation. Options: --mobile, --mobile-output sim|archive|ipa (--output is the output folder), --scheme S, --signing
/// unsigned|project, --expect-state "Ready to build", --expect-blocked-by "Deployment target", --record-only (a failure is
/// recorded, not fatal — used to reproduce HCG Sentinel's archive across Xcode versions).
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunMobileAsync(MainWindowViewModel main, BuildWizardViewModel wizard, string output)
    {
        var project = wizard.Project ?? throw new InvalidOperationException("Nothing was imported.");
        Log($"Inspection: {project.Analysis?.InspectorId} · {project.Analysis?.ApplicationType} · {project.Analysis?.Support} — {project.Analysis?.SupportSummary}");
        Check(project.Analysis?.Mobile is not null, "Mobile Build Studio can build this project");
        await main.NavigateAsync("mobile", project);
        var studio = (MobileBuildStudioViewModel)main.CurrentPage!;
        Check(studio.SelectedProject?.Project.Id == project.Id, "Project selected in Mobile Build Studio");
        foreach (var row in studio.ProjectRows) Log($"  {row.Label}: {row.Value}");
        foreach (var d in studio.Diagnostics) Log($"  {d.Severity}: {d.Message}");
        studio.SelectedPlatform = studio.Platforms.First(p => p.Platform == TargetPlatform.IOS);
        // The scheme is chosen first (it is offered even when the current scheme cannot be built), then the output.
        if (Arg("scheme") is { } scheme)
        {
            Check(studio.Schemes.Contains(scheme), $"Scheme {scheme} offered ({string.Join(", ", studio.Schemes)})");
            studio.SelectedScheme = scheme;
            foreach (var row in studio.SchemeChecks) Log($"Scheme check {row.Label}: {row.Value}");
            if (studio.HasSchemeSuggestion) Log($"Suggested: {studio.UseSuggestedSchemeLabel}");
        }
        foreach (var o in studio.Outputs) Log($"Output {o.Name}: {(o.IsEnabled ? "available" : "unavailable — " + o.Reason)}");
        Shot("mobile-scheme");
        var wanted = Arg("mobile-output", "sim") switch { "archive" => MobileOutput.IosArchive, "ipa" => MobileOutput.IosIpa, _ => MobileOutput.IosSimulatorApp };
        var item = studio.Outputs.FirstOrDefault(o => o.Option.Output == wanted);
        Check(item is not null, $"Output {wanted} offered");
        if (item is null) return 1;
        if (_args.ContainsKey("expect-no-unsigned"))
        {
            // .NET for iOS archives only with code signing: the archive is listed but unavailable, with that reason,
            // and cannot be selected or started; no unsigned signing mode exists for it.
            Check(!item.IsEnabled, $"Output {wanted} is unavailable without code signing");
            Check(item.Reason?.Contains("only with code signing", StringComparison.Ordinal) == true, $"Reason names the SDK requirement: {item.Reason}");
            studio.SelectOutputCommand.Execute(item);
            Check(studio.SelectedOutput?.Option.Output != wanted, "The unavailable archive cannot be selected");
            return Failures.Count == 0 ? 0 : 1;
        }
        studio.SelectOutputCommand.Execute(item);
        if (Arg("signing") is { } signing)
        {
            var mode = signing == "unsigned" ? MobileSigningMode.Unsigned : MobileSigningMode.ProjectDefault;
            var choice = studio.SigningModes.FirstOrDefault(m => m.Value == mode);
            Check(choice is not null, $"Signing {mode} offered");
            if (choice is null) return 1;
            studio.SelectedSigning = choice;
        }
        studio.OutputDirectory = Path.Combine(output, "artifacts");
        foreach (var r in studio.ReadinessRequired) Log($"Readiness [{r.Status.Text}] {r.Tool}: {r.Detail}{(r.HasFix ? " → " + r.Fix : "")}");
        foreach (var row in studio.Summary) Log($"Summary {row.Label}: {row.Value}");
        Log($"State: [{studio.BuildStateTag.Text}] {studio.BuildStateDetail}");
        Shot("mobile-ready");
        if (Arg("expect-blocked-by") is { } blocker)
        {
            Check(studio.ReadinessRequired.Any(r => r.Tool == blocker && r.Item.State is not (ReadinessState.Ready or ReadinessState.NotVerifiable)), $"Blocked before building by: {blocker}");
            Check(!studio.StartBuildCommand.CanExecute(null), "Build cannot start");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (Arg("expect-state") is { } expectedState)
        {
            Check(studio.BuildStateTag.Text == expectedState, $"State {studio.BuildStateTag.Text} (expected {expectedState})");
            if (expectedState != "Ready to build")
            {
                studio.TrustAcknowledged = true;
                Check(!studio.StartBuildCommand.CanExecute(null), "Build cannot start in this state, even after trust");
                return Failures.Count == 0 ? 0 : 1;
            }
        }
        Check(!studio.StartBuildCommand.CanExecute(null), "Build refused before trust is confirmed");
        studio.TrustAcknowledged = true;
        Check(studio.StartBuildCommand.CanExecute(null) || _args.ContainsKey("record-only"), "Build can start after trust");
        if (!studio.StartBuildCommand.CanExecute(null)) { Log("Not buildable here; recorded."); return _args.ContainsKey("record-only") ? 0 : 1; }
        await studio.StartBuildCommand.ExecuteAsync(null);
        Log($"Result: [{studio.ResultTag.Text}] {studio.ResultDetail}");
        Log($"State after the build: [{studio.BuildStateTag.Text}] {studio.BuildStateDetail}");
        if (studio.FirstError is { } fe) Log("FIRST ERROR: " + fe);
        foreach (var a in studio.BuiltArtifacts)
        {
            Log($"Artifact {a.FormatName}: {a.Path} · {a.Size}");
            foreach (var c in a.Artifact.Checks) Log($"    [{c.Status}] {c.Name}: {c.Detail}");
        }
        Shot("mobile-result");
        var succeeded = studio.BuildStateTag.Text == "Artifact verified";
        File.WriteAllText(Path.Combine(output, $"{_prefix}-mobile.json"), JsonSerializer.Serialize(new
        {
            output = wanted.ToString(), scheme = studio.SelectedScheme, state = studio.BuildStateTag.Text, detail = studio.BuildStateDetail,
            firstError = studio.FirstError, log = studio.LastLogPath, artifacts = studio.BuiltArtifacts.Select(a => new { a.FormatName, a.Path }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (studio.LastLogPath is { } log) File.Copy(log, Path.Combine(output, $"{_prefix}-build.log"), overwrite: true);
        if (_args.ContainsKey("record-only")) return 0;
        Check(succeeded, $"Build produced verified artifacts ({studio.BuildStateTag.Text})");
        return Failures.Count == 0 ? 0 : 1;
    }
}
