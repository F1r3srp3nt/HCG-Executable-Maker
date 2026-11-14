using System.Diagnostics;
using System.Text.Json;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// Mobile Build Studio driven like a user, in the order the page shows: the project imported in New Build is opened in
/// Mobile Build Studio, the SCHEME is chosen first (it decides which outputs exist), then the output, configuration and
/// signing; readiness, the precise current state and the prerequisites are recorded, and the build runs after the trust
/// confirmation. Options:
///   --mobile-output sim|archive|ipa (--output is the output folder) · --scheme S · --signing unsigned|project
///   --expect-default-state "TAG"   the state shown on arrival, before anything is chosen (e.g. "Blocked — deployment target incompatible")
///   --expect-recommended S         the scheme offered by “Use scheme S” on arrival
///   --use-recommended              press “Use scheme …” (instead of --scheme)
///   --then-checkout REV            after import, check out REV in the ORIGINAL git folder: the copy must show as changed,
///                                  then Refresh source must bring it up to date, keeping the project id and the scheme
///   --expect-persisted             leave the page and come back: the chosen scheme must still be selected
///   --expect-state "TAG" · --expect-blocked-by "Readiness row" · --record-only (a failure is recorded, not fatal)
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
        LogStudio(studio, "On arrival");
        Shot("mobile-arrival");

        if (Arg("expect-default-state") is { } arrival)
            Check(studio.BuildStateTag.Text == arrival, $"State on arrival: [{studio.BuildStateTag.Text}] (expected [{arrival}])");
        if (Arg("expect-recommended") is { } rec)
        {
            Check(studio.RecommendedScheme == rec, $"Recommended scheme on arrival: {studio.RecommendedScheme ?? "(none)"} (expected {rec})");
            Check(studio.SelectedScheme != rec, $"The recommended scheme is offered, not selected for the user (selected: {studio.SelectedScheme})");
        }

        if (Arg("then-checkout") is { } rev) await RefreshAfterCheckoutAsync(studio, project, rev);

        var wanted = Arg("mobile-output", "sim") switch { "archive" => MobileOutput.IosArchive, "ipa" => MobileOutput.IosIpa, _ => MobileOutput.IosSimulatorApp };
        if (_args.ContainsKey("expect-no-unsigned"))
        {
            // .NET for iOS archives only with code signing: the archive is listed but unavailable, with that reason,
            // and cannot be selected or started; no unsigned signing mode exists for it.
            var item0 = studio.Outputs.FirstOrDefault(o => o.Option.Output == wanted);
            Check(item0 is not null && !item0.IsEnabled, $"Output {wanted} is unavailable without code signing");
            Check(item0?.Reason?.Contains("only with code signing", StringComparison.Ordinal) == true, $"Reason names the SDK requirement: {item0?.Reason}");
            studio.SelectOutputCommand.Execute(item0);
            Check(studio.SelectedOutput?.Option.Output != wanted, "The unavailable archive cannot be selected");
            return Failures.Count == 0 ? 0 : 1;
        }

        // Scheme first — as on the page — then the output.
        if (_args.ContainsKey("use-recommended"))
        {
            var target = studio.RecommendedScheme;
            Check(target is not null && studio.UseRecommendedSchemeCommand.CanExecute(null), $"“Use scheme {target}” is offered");
            studio.UseRecommendedSchemeCommand.Execute(null);
            Check(studio.SelectedScheme == target, $"Scheme {studio.SelectedScheme} selected by the user's click");
        }
        else if (Arg("scheme") is { } scheme)
        {
            Check(studio.Schemes.Contains(scheme), $"Scheme {scheme} offered ({string.Join(", ", studio.Schemes)})");
            studio.SelectedScheme = scheme;
        }
        LogStudio(studio, $"With scheme {studio.SelectedScheme}");
        var item = studio.Outputs.FirstOrDefault(o => o.Option.Output == wanted);
        Check(item is not null, $"Output {wanted} offered");
        if (item is null) return 1;
        if (Arg("expect-unavailable-reason") is { } why)
        {
            // The output is listed, unavailable for the stated reason, cannot be selected, and nothing can start it.
            Check(!item.IsEnabled, $"Output {wanted} is unavailable with scheme {studio.SelectedScheme}");
            Check(item.Reason?.Contains(why, StringComparison.Ordinal) == true, $"Reason contains “{why}”: {item.Reason}");
            studio.SelectOutputCommand.Execute(item);
            Check(studio.SelectedOutput?.Option.Output != wanted, $"Output {wanted} cannot be selected");
            if (Arg("expect-state") is { } st) Check(studio.BuildStateTag.Text == st, $"State [{studio.BuildStateTag.Text}] (expected [{st}])");
            foreach (var p in studio.Prerequisites) Log("Before you can build: " + p);
            Shot("mobile-unavailable");
            WriteResult(studio, output, wanted);
            return Failures.Count == 0 ? 0 : 1;
        }
        if (!item.IsEnabled && !_args.ContainsKey("record-only") && Arg("expect-blocked-by") is null && Arg("expect-state") is null)
            Check(false, $"Output {wanted} is available with scheme {studio.SelectedScheme} ({item.Reason})");
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
        foreach (var p in studio.Prerequisites) Log("Before you can build: " + p);
        Shot("mobile-ready");

        if (_args.ContainsKey("expect-persisted") && studio.SelectedScheme is { } chosen)
        {
            // Leave the page and return: the scheme is per project and saved, never reset to the first one.
            await main.NavigateAsync("dashboard", null);
            await main.NavigateAsync("mobile", project);
            studio = (MobileBuildStudioViewModel)main.CurrentPage!;
            studio.SelectedPlatform = studio.Platforms.First(p => p.Platform == TargetPlatform.IOS);
            Check(studio.SelectedScheme == chosen && studio.SchemeIsSaved, $"Scheme kept after leaving and reopening the page: {studio.SelectedScheme} (saved: {studio.SchemeIsSaved})");
            var again = studio.Outputs.FirstOrDefault(o => o.Option.Output == wanted);
            if (again is not null) studio.SelectOutputCommand.Execute(again);
            if (Arg("signing") is { } s2 && studio.SigningModes.FirstOrDefault(m => m.Value == (s2 == "unsigned" ? MobileSigningMode.Unsigned : MobileSigningMode.ProjectDefault)) is { } c2)
                studio.SelectedSigning = c2;
            studio.OutputDirectory = Path.Combine(output, "artifacts");
        }

        if (Arg("expect-blocked-by") is { } blocker)
        {
            Check(studio.ReadinessRequired.Any(r => r.Tool == blocker && r.Item.State is not (ReadinessState.Ready or ReadinessState.NotVerifiable)), $"Blocked before building by: {blocker}");
            Check(!studio.StartBuildCommand.CanExecute(null), "Build cannot start");
            if (Arg("expect-state") is { } st) Check(studio.BuildStateTag.Text == st, $"State {studio.BuildStateTag.Text} (expected {st})");
            WriteResult(studio, output, wanted);
            return Failures.Count == 0 ? 0 : 1;
        }
        if (Arg("expect-state") is { } expectedState)
        {
            Check(studio.BuildStateTag.Text == expectedState, $"State {studio.BuildStateTag.Text} (expected {expectedState})");
            if (expectedState != MobileBuildState.Ready)
            {
                studio.TrustAcknowledged = true;
                Check(!studio.StartBuildCommand.CanExecute(null), "Build cannot start in this state, even after trust");
                WriteResult(studio, output, wanted);
                return Failures.Count == 0 ? 0 : 1;
            }
        }
        Check(!studio.StartBuildCommand.CanExecute(null), "Build refused before trust is confirmed");
        Check(studio.Prerequisites.Any(p => p.Contains("I trust this project", StringComparison.Ordinal)), "The trust confirmation is listed as a prerequisite");
        studio.TrustAcknowledged = true;
        Check(studio.StartBuildCommand.CanExecute(null) || _args.ContainsKey("record-only"), "Build can start after trust");
        if (studio.ReadinessCanBuild) Check(studio.Prerequisites.Count == 0, $"No prerequisites left ({string.Join(" | ", studio.Prerequisites)})");
        if (!studio.StartBuildCommand.CanExecute(null)) { Log("Not buildable here; recorded."); WriteResult(studio, output, wanted); return _args.ContainsKey("record-only") ? 0 : 1; }
        var builtScheme = studio.SelectedScheme;
        await studio.StartBuildCommand.ExecuteAsync(null);
        Log($"Result: [{studio.ResultTag.Text}] {studio.ResultDetail}");
        Log($"Current state after the build: [{studio.BuildStateTag.Text}] {studio.BuildStateDetail}");
        Log($"Previous build line: {studio.PreviousBuildSummary}");
        if (studio.FirstError is { } fe) Log("FIRST ERROR: " + fe);
        foreach (var a in studio.BuiltArtifacts)
        {
            Log($"Artifact {a.FormatName}: {a.Path} · {a.Size}");
            foreach (var c in a.Artifact.Checks) Log($"    [{c.Status}] {c.Name}: {c.Detail}");
        }
        Shot("mobile-result");
        var succeeded = studio.ResultTag.Text == "Artifact verified";
        // The build log must show the scheme that was selected and checked — the same one xcodebuild received.
        if (studio.LastLogPath is { } logPath && File.Exists(logPath) && builtScheme is not null)
        {
            var log = File.ReadAllText(logPath);
            var cmd = log.Split('\n').FirstOrDefault(l => l.Contains("xcodebuild", StringComparison.Ordinal) && l.Contains(" -scheme ", StringComparison.Ordinal));
            Log("xcodebuild command: " + (cmd?.Trim() ?? "(none)"));
            Check(cmd?.Contains($" -scheme {builtScheme} ", StringComparison.Ordinal) == true, $"xcodebuild ran with -scheme {builtScheme}");
            Check(log.Contains($"Scheme: {builtScheme} ", StringComparison.Ordinal), $"The build log states scheme {builtScheme} and its targets");
        }
        WriteResult(studio, output, wanted);
        if (studio.LastLogPath is { } lp) File.Copy(lp, Path.Combine(output, $"{_prefix}-build.log"), overwrite: true);
        if (_args.ContainsKey("record-only")) return 0;
        Check(succeeded, $"Build produced verified artifacts ({studio.ResultTag.Text})");
        Check(studio.BuildStateTag.Text != "Artifact verified" && studio.HasPreviousBuild, "The current state and the previous build's result are shown separately");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void LogStudio(MobileBuildStudioViewModel studio, string when)
    {
        Log($"{when}: scheme {studio.SelectedScheme ?? "(none)"} (saved: {studio.SchemeIsSaved}) · recommended {studio.RecommendedScheme ?? "(none)"}");
        foreach (var c in studio.SchemeChoices) Log($"  Scheme choice: {c.Label}");
        foreach (var o in studio.Outputs) Log($"  Output {o.Name}: {(o.IsEnabled ? "available" : "unavailable — " + o.Reason)}");
        Log($"  State: [{studio.BuildStateTag.Text}] {studio.BuildStateDetail}");
        Log($"  Stage heading: {studio.CurrentStage}");
        foreach (var p in studio.Prerequisites) Log("  Before you can build: " + p);
    }

    /// <summary>The original moves on after import (git checkout REV): the copy must show as changed; Refresh source fixes it in place.</summary>
    private static async Task RefreshAfterCheckoutAsync(MobileBuildStudioViewModel studio, ProjectRecord project, string rev)
    {
        var source = project.SourceLocation;
        var scheme = studio.SelectedScheme;
        var git = Process.Start(new ProcessStartInfo("git", ["-C", source, "checkout", "--quiet", rev]) { UseShellExecute = false })!;
        await git.WaitForExitAsync();
        Check(git.ExitCode == 0, $"Original folder checked out at {rev}");
        await studio.RefreshCommand.ExecuteAsync(null); // re-inspect: snapshot status is computed again
        for (var i = 0; i < 300 && studio.SnapshotStatus?.StartsWith("Comparing", StringComparison.Ordinal) != false; i++) await Task.Delay(100);
        Log("Imported copy: " + studio.SnapshotStatus);
        Check(studio.SnapshotStale, "The copy is reported as older than its original");
        await studio.RefreshSourceCommand.ExecuteAsync(null);
        Log("Refresh: " + studio.RefreshResult);
        for (var i = 0; i < 300 && studio.SnapshotStatus?.StartsWith("Comparing", StringComparison.Ordinal) != false; i++) await Task.Delay(100);
        Log("Imported copy after refresh: " + studio.SnapshotStatus);
        Check(studio.SelectedProject?.Project.Id == project.Id, "Refresh kept the same project");
        Check(!studio.SnapshotStale && studio.SnapshotStatus?.Contains("matches the original", StringComparison.Ordinal) == true, "The copy now matches the original");
        Check(studio.SnapshotStatus?.Contains(rev[..Math.Min(12, rev.Length)], StringComparison.Ordinal) == true || rev.Length < 12, $"The copy's revision is {rev}");
        Check(studio.SelectedScheme == scheme, $"Scheme choice kept across the refresh ({studio.SelectedScheme})");
        LogStudio(studio, "After Refresh source");
    }

    private static void WriteResult(MobileBuildStudioViewModel studio, string output, MobileOutput wanted) =>
        File.WriteAllText(Path.Combine(output, $"{_prefix}-mobile.json"), JsonSerializer.Serialize(new
        {
            output = wanted.ToString(), scheme = studio.SelectedScheme, schemeSaved = studio.SchemeIsSaved, recommended = studio.RecommendedScheme,
            state = studio.BuildStateTag.Text, detail = studio.BuildStateDetail, prerequisites = studio.Prerequisites,
            result = studio.ResultTag.Text, previousBuild = studio.PreviousBuildSummary, snapshot = studio.SnapshotStatus,
            firstError = studio.FirstError, log = studio.LastLogPath, artifacts = studio.BuiltArtifacts.Select(a => new { a.FormatName, a.Path }),
        }, new JsonSerializerOptions { WriteIndented = true }));
}
