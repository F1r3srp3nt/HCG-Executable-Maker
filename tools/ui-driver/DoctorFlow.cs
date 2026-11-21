using System.Text.Json;
using Avalonia.Threading;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// Build Doctor driven like a user in Mobile Build Studio: the imported project is diagnosed, the findings are recorded,
/// a repair can be reviewed and approved (--doctor-repair import-repository|restore|install-workload), MSBuild can
/// evaluate the projects (--doctor-evaluate, after the trust confirmation), and the Simulator app can then be built
/// (--doctor-build). Options: --doctor, --doctor-expect-readiness "Configuration required", --doctor-expect-category ImportScope,
/// --doctor-expect-no-category ProjectReferences,MsBuildProperties,ImportScope.
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunDoctorAsync(MainWindowViewModel main, BuildWizardViewModel wizard, string output)
    {
        var project = wizard.Project ?? throw new InvalidOperationException("Nothing was imported.");
        await main.NavigateAsync("mobile", project);
        var studio = (MobileBuildStudioViewModel)main.CurrentPage!;
        Check(studio.SelectedProject?.Project.Id == project.Id, "Project selected in Mobile Build Studio");
        SelectIosSimulator(studio);
        LogDoctor(studio, "initial");
        CheckOutputReasons(studio);
        Shot("doctor-initial");
        if (Arg("doctor-expect-readiness") is { } want) Check(studio.DoctorReadiness.Text == want, $"Build readiness: {studio.DoctorReadiness.Text} (expected {want})");
        foreach (var c in (Arg("doctor-expect-category") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            Check(studio.DoctorFindings.Any(f => f.Finding.Category.ToString() == c), $"Build Doctor reports {c}");

        if (Arg("doctor-repair") is { } repairId)
        {
            var finding = studio.DoctorFindings.FirstOrDefault(f => f.Finding.Repair?.Id == repairId);
            Check(finding is not null, $"Build Doctor proposes the repair {repairId}");
            if (finding is null) return 1;
            Log($"Repair: {finding.RepairTitle} [{finding.RepairMode.Text}] — {finding.Finding.Repair!.Description}");
            Check(finding.RepairMode.Text == "Requires approval", "The repair requires approval");
            var projectsBefore = studio.MobileProjects.Count;
            Check(!studio.ApproveRepairCommand.CanExecute(null), "Approve is unavailable before the repair is reviewed");
            studio.ReviewRepairCommand.Execute(finding);
            Log("Preview:\n" + studio.RepairPreview);
            ShotScrolledToEnd("doctor-review");
            Check(studio.MobileProjects.Count == projectsBefore && studio.SelectedProject?.Project.Id == project.Id, "Reviewing changes nothing: no import before approval");
            if (studio.ReviewNeedsTrust)
            {
                Check(!studio.ApproveRepairCommand.CanExecute(null), "Approve is refused before the trust confirmation");
                studio.RepairTrustAcknowledged = true;
            }
            Check(studio.ApproveRepairCommand.CanExecute(null), "Approve is available after review");
            await studio.ApproveRepairCommand.ExecuteAsync(null);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Log("Repair outcome: " + studio.RepairOutcome);
            Check(!studio.IsReviewing && !studio.ApproveRepairCommand.CanExecute(null), "The review closes after the repair ran (it cannot be approved twice)");
            Check(studio.RepairOutcome?.Contains("Verification:", StringComparison.Ordinal) == true && (repairId != "import-repository" || studio.RepairOutcome.StartsWith("Imported ", StringComparison.Ordinal)), "The repair outcome and its verification stay visible");
            if (repairId == "import-repository")
                Check(studio.MobileProjects.Count == projectsBefore + 1 && studio.MobileProjects.Any(p => p.Project.Id == project.Id), "After approval the repository is a new project; the original import is kept");
            if (studio.RepairLog.Length > 0) Log("Repair log:\n" + studio.RepairLog);
            SelectIosSimulator(studio);
            LogDoctor(studio, "after repair");
            CheckOutputReasons(studio);
            Shot("doctor-after-repair");
        }
        foreach (var c in (Arg("doctor-expect-no-category") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            Check(!studio.DoctorFindings.Any(f => f.Finding.Category.ToString() == c && f.Finding.Severity != DoctorSeverity.Info), $"No {c} problems remain");

        if (_args.ContainsKey("doctor-evaluate"))
        {
            Check(!studio.EvaluateWithMsBuildCommand.CanExecute(null), "MSBuild evaluation is refused before the trust confirmation");
            studio.RepairTrustAcknowledged = true;
            await studio.EvaluateWithMsBuildCommand.ExecuteAsync(null);
            Log("MSBuild evaluation: " + studio.RepairOutcome);
            Log(studio.RepairLog);
            Check(studio.RepairOutcome?.Contains("every project reference resolves", StringComparison.Ordinal) == true, "MSBuild confirms every project reference");
        }

        if (_args.ContainsKey("doctor-archive-check"))
        {
            // Device builds: an archive needs a signing identity and profile; Build Doctor must say so, never fake it.
            if (studio.Outputs.FirstOrDefault(o => o.Option.Output == MobileOutput.IosArchive) is { IsEnabled: true } archive)
            {
                studio.SelectOutputCommand.Execute(archive);
                LogDoctor(studio, "device archive");
                ShotScrolledToEnd("doctor-archive");
                var signing = studio.DoctorFindings.Where(f => f.Finding.Category is DoctorCategory.Signing or DoctorCategory.Provisioning).ToList();
                Check(signing.Count > 0 || studio.DoctorReadiness.Tone == "success", "Device archive: signing requirements are reported (or a signing identity is present)");
                Check(!signing.Any() || studio.DoctorReadiness.Text != "Ready to build", "No device archive is offered as ready without signing");
            }
            else Log("Device archive output is not offered on this host.");
            SelectIosSimulator(studio);
        }

        File.WriteAllText(Path.Combine(output, $"{_prefix}-doctor.json"), JsonSerializer.Serialize(new
        {
            readiness = studio.DoctorReadiness.Text, summary = studio.DoctorSummary, project = studio.SelectedProject?.Project.SourceLocation,
            findings = studio.DoctorFindings.Select(f => new { category = f.Category, severity = f.Severity.Text, f.Problem, f.RootCause, f.AffectedFile, f.Recommendation,
                repair = f.Finding.Repair?.Title, mode = f.RepairMode.Text, verification = f.Verification.Text }),
            facts = studio.DoctorFacts, repairOutcome = studio.RepairOutcome,
        }, new JsonSerializerOptions { WriteIndented = true }));

        if (_args.ContainsKey("doctor-build"))
        {
            SelectIosSimulator(studio);
            studio.OutputDirectory = Path.Combine(output, "artifacts");
            studio.TrustAcknowledged = true;
            Log($"State before build: [{studio.BuildStateTag.Text}] {studio.BuildStateDetail}");
            Check(studio.StartBuildCommand.CanExecute(null), "The Simulator build can start after Build Doctor");
            if (!studio.StartBuildCommand.CanExecute(null)) return 1;
            await studio.StartBuildCommand.ExecuteAsync(null);
            Log($"Result: [{studio.ResultTag.Text}] {studio.ResultDetail}");
            if (studio.FirstError is { } fe) Log("FIRST ERROR: " + fe);
            foreach (var a in studio.BuiltArtifacts) Log($"Artifact {a.FormatName}: {a.Path}");
            Shot("doctor-built");
            Check(studio.BuildStateTag.Text == "Artifact verified", $"Simulator app built and verified ({studio.BuildStateTag.Text})");
        }
        return Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>No reason is repeated across output cards: a reason shared by several outputs is shown once, above them.</summary>
    private static void CheckOutputReasons(MobileBuildStudioViewModel studio)
    {
        foreach (var o in studio.Outputs) Log($"Output {o.Name}: {o.Status.Text}{(o.ShowReason ? " — " + o.ShortReason : "")}");
        if (studio.HasSharedOutputReason) Log($"Shared output reason: {studio.SharedOutputReasonScope} — {studio.SharedOutputReason}");
        // Compared on the text a card displays (its first sentence), not the full reason in its tooltip.
        Check(studio.Outputs.Where(o => o.ShowReason).GroupBy(o => o.ShortReason).All(g => g.Count() == 1), "No output card repeats another card's displayed reason");
        Check(studio.Outputs.Where(o => o.HasReason && !o.ShowReason).All(o => o.ShortReason == studio.SharedOutputReason), "Every hidden card reason is shown once in the shared note");
        Check(studio.Outputs.Where(o => o.ShowReason).All(o => o.ShortReason!.Length <= 220), "Displayed output reasons are short (details are in Build Doctor and the tooltip)");
    }

    private static void SelectIosSimulator(MobileBuildStudioViewModel studio)
    {
        if (studio.Platforms.FirstOrDefault(p => p.Platform == TargetPlatform.IOS) is { } ios) studio.SelectedPlatform = ios;
        if (studio.Outputs.FirstOrDefault(o => o.Option.Output == MobileOutput.IosSimulatorApp) is { } sim) studio.SelectOutputCommand.Execute(sim);
    }

    private static void LogDoctor(MobileBuildStudioViewModel studio, string when)
    {
        Log($"Build Doctor ({when}): {studio.DoctorHeading} — readiness [{studio.DoctorReadiness.Text}]");
        foreach (var s in studio.DoctorSummary) Log("  • " + s);
        foreach (var f in studio.DoctorFindings)
        {
            Log($"  [{f.Severity.Text}] {f.Category}: {f.Problem}");
            if (f.HasRootCause) Log($"      cause: {f.RootCause}");
            if (f.HasFile) Log($"      file: {f.AffectedFile}");
            Log($"      fix: {f.Recommendation}{(f.HasRepair ? $" · repair: {f.RepairTitle} [{f.RepairMode.Text}]" : "")} · {f.Verification.Text}");
        }
    }
}
