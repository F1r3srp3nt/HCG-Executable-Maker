using System.Security.Cryptography;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// HCG Sentinel's scheme workflow in Mobile Build Studio, driven like a user (run with --simulate-xcode on a host without the
/// right Xcode): selection before outputs, one shared deployment-target message, "Use HCGSentinelModern" with its explanation,
/// re-inspection, New Build Back/Next and leaving/returning to the page, and the command the build would run.
/// --scheme-workflow [--expect-simulator-only] ; --reopen-mobile (second run on the same --output: the saved selection is restored).
/// </summary>
internal static partial class Program
{
    private const string Legacy = "HCGSentinel";
    private const string Modern = "HCGSentinelModern";

    private static async Task<MobileBuildStudioViewModel> OpenStudioAsync(MainWindowViewModel main, ProjectRecord? project)
    {
        await main.NavigateAsync("mobile", project);
        var studio = (MobileBuildStudioViewModel)main.CurrentPage!;
        await WaitUntil(() => studio.SelectedProject is not null && studio.Schemes.Count > 0, TimeSpan.FromSeconds(20), "project and saved selection");
        await Task.Delay(300);
        await studio.SelectionSaved;
        return studio;
    }

    private static void LogSelection(MobileBuildStudioViewModel s, string when)
    {
        Log($"[{when}] container={s.SelectedContainer} scheme={s.SelectedScheme} configuration={s.SelectedConfiguration?.Name} xcode={s.SelectedXcode?.Name} " +
            $"project tag=[{s.SupportTag.Text}]");
        if (s.CompatibilityProblem is { } p) Log($"[{when}] Shared message: {p}");
        foreach (var r in s.SchemeChecks) Log($"[{when}]   {r.Label}: {r.Value}");
        if (s.HasSchemeSuggestion) Log($"[{when}]   Action: {s.UseSuggestedSchemeLabel}");
        foreach (var o in s.Outputs) Log($"[{when}]   Output {o.Name}: {(o.IsEnabled ? "available" : "unavailable — " + o.Reason)}");
    }

    private static string Hash(string dir)
    {
        var lines = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..16];
    }

    private static async Task<int> RunSchemeWorkflowAsync(MainWindowViewModel main, BuildWizardViewModel wizard, string output)
    {
        var project = wizard.Project ?? throw new InvalidOperationException("Nothing was imported.");
        var sourceHash = Hash(project.SourceDirectory);
        Check(wizard.IsMobileProject, "New Build hands this project to Mobile Build Studio");
        wizard.OpenInMobileStudioCommand.Execute(null);
        await WaitUntil(() => main.CurrentPage is MobileBuildStudioViewModel, TimeSpan.FromSeconds(10), "Mobile Build Studio");
        var studio = await OpenStudioAsync(main, project);
        studio.SelectedPlatform = studio.Platforms.First(p => p.Platform == TargetPlatform.IOS);
        LogSelection(studio, "opened");
        Shot("scheme-legacy");

        // 1–2. Selection before outputs, usable when every output is unavailable.
        Check(studio.ShowIosSelection && studio.ShowSchemes, "Scheme and configuration are offered before the outputs");
        Check(studio.HasXcodeChoices, "Xcode can be chosen on the page");
        Check(studio.Configurations.Count >= 3, $"Configurations offered: {string.Join(", ", studio.Configurations.Select(c => c.Name))}");
        Check(studio.SelectedScheme == Legacy, $"The legacy scheme is selected first ({studio.SelectedScheme})");

        var simulatorOnly = _args.ContainsKey("expect-simulator-only");
        var sim = studio.Outputs.First(o => o.Option.Output == MobileOutput.IosSimulatorApp);
        var archive = studio.Outputs.First(o => o.Option.Output == MobileOutput.IosArchive);
        if (simulatorOnly)
        {
            // 8. The Simulator SDK accepts iOS 14.7 while the device SDK does not: judged separately.
            Check(sim.IsEnabled, "Simulator build available (its own SDK accepts iOS 14.7)");
            Check(!archive.IsEnabled, "Device archive unavailable (the iPhoneOS SDK does not)");
            Check(studio.CompatibilityProblem?.Contains("(device)", StringComparison.Ordinal) == true, $"The shared message names the device SDK only: {studio.CompatibilityProblem}");
            Check(Source(project) == sourceHash, "Project files unchanged");
            return Failures.Count == 0 ? 0 : 1;
        }
        Check(studio.Outputs.All(o => !o.IsEnabled), "Every output is unavailable for HCGSentinel with this Xcode");
        Check(studio.Schemes.Contains(Modern), "…and the scheme can still be changed");

        // 7. One concise shared message; outputs only refer to it.
        Check(studio.CompatibilityProblem is { Length: < 160 } p && p.Contains("14.7") && p.Contains("15.0"), $"One concise shared message: {studio.CompatibilityProblem}");
        Check(studio.Outputs.All(o => o.Reason?.Contains("see Scheme compatibility above", StringComparison.Ordinal) == true), "Each output refers to it instead of repeating it");
        Check(studio.Outputs.All(o => o.Reason?.Contains("xcodebuild would stop", StringComparison.Ordinal) != true), "The long paragraph is not repeated per output");
        Check(studio.SchemeChecks.Count == 2 && studio.SchemeChecks.Any(r => r.Label.StartsWith("Simulator")) && studio.SchemeChecks.Any(r => r.Label.StartsWith("Device")),
            "Simulator and device are listed with their own SDK");
        Check(studio.SupportTag.Text == "Selected toolchain incompatible", $"Project state is current: {studio.SupportTag.Text}");

        // 5. The verified alternative, with its minimum iOS explained before switching.
        Check(studio.SuggestedScheme == Modern, $"Suggested: {studio.SuggestedScheme}");
        Check(studio.UseSuggestedSchemeLabel.Contains("iOS 15.0"), $"Action names its minimum: {studio.UseSuggestedSchemeLabel}");
        Check(studio.SchemeSuggestionText.Contains("only on iOS 15.0 or later") && studio.SchemeSuggestionText.Contains("unchanged"),
            "The explanation says what iOS 15.0 means and that HCGSentinel stays unchanged");
        var generation = studio.SelectionGeneration;
        var dialogsBefore = Report.Count(l => l.StartsWith("Dialog (confirmed) Use " + Modern, StringComparison.Ordinal));
        await studio.UseSuggestedSchemeCommand.ExecuteAsync(null);
        Check(Report.Count(l => l.StartsWith("Dialog (confirmed) Use " + Modern, StringComparison.Ordinal)) == dialogsBefore + 1, "The switch was explained in a confirmation first");
        await studio.SelectionSaved;
        LogSelection(studio, "after Use HCGSentinelModern");
        Shot("scheme-modern");

        // 4. Recomputed for the new scheme.
        Check(studio.SelectedScheme == Modern && studio.SelectionGeneration > generation, "Scheme switched and results recomputed");
        Check(studio.CompatibilityProblem is null, "No deployment-target problem for HCGSentinelModern");
        Check(studio.Outputs.First(o => o.Option.Output == MobileOutput.IosSimulatorApp).IsEnabled, "Simulator build available");
        Check(studio.Outputs.First(o => o.Option.Output == MobileOutput.IosArchive).IsEnabled, "Device archive available");
        Check(studio.Schemes.Contains(Legacy), "HCGSentinel is still offered (legacy support kept)");
        Check(studio.SupportTag.Text == "Supported", $"Project state updated: {studio.SupportTag.Text} — {studio.SupportSummary}");

        // Configuration chosen before the output, applied to it.
        studio.SelectedConfiguration = studio.Configurations.First(c => c.Value == "Debug");
        studio.SelectOutputCommand.Execute(studio.Outputs.First(o => o.Option.Output == MobileOutput.IosArchive));
        Check(studio.CurrentOptions?.Variant == "Debug", $"Archive uses the chosen configuration ({studio.CurrentOptions?.Variant})");
        studio.SelectedConfiguration = studio.Configurations[0];
        studio.SelectOutputCommand.Execute(studio.Outputs.First(o => o.Option.Output == MobileOutput.IosArchive));
        Check(studio.CurrentOptions?.Variant == "Release", $"Scheme default for the archive is Release ({studio.CurrentOptions?.Variant})");

        // 6. Displayed = executed: the command shown and the plan the build service runs.
        await ExpectSchemeAgreementAsync(studio, project, Modern, output);

        // Re-inspection keeps the selection; a change made while it runs is not overwritten by it.
        var refresh = studio.RefreshCommand.ExecuteAsync(null);
        studio.SelectedScheme = Legacy;
        await refresh;
        await Task.Delay(300);
        Check(studio.SelectedScheme == Legacy, $"A scheme chosen during re-inspection survives it ({studio.SelectedScheme})");
        Check(studio.CompatibilityProblem is not null, "…and its compatibility is the legacy scheme's");
        studio.SelectedScheme = Modern;
        await studio.RefreshCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check(studio.SelectedScheme == Modern && studio.CompatibilityProblem is null, "Re-inspection keeps HCGSentinelModern and its result");

        // Back/Next in New Build, then Open in Mobile Build Studio again; and leaving/returning to the page.
        await studio.SelectionSaved;
        await main.NavigateAsync("new-build", null);
        var wiz = (BuildWizardViewModel)main.CurrentPage!;
        if (wiz.BackCommand.CanExecute(null)) wiz.BackCommand.Execute(null);
        if (wiz.NextCommand.CanExecute(null)) await wiz.NextCommand.ExecuteAsync(null);
        Log($"New Build after Back/Next: step {wiz.Step}, mobile={wiz.IsMobileProject}");
        if (wiz.IsMobileProject) wiz.OpenInMobileStudioCommand.Execute(null);
        else await main.NavigateAsync("mobile", project);
        studio = await OpenStudioAsync(main, null);
        Check(studio.SelectedScheme == Modern, $"Back/Next and Open in Mobile Build Studio keep the scheme ({studio.SelectedScheme})");
        await main.NavigateAsync("toolchains", null);
        studio = await OpenStudioAsync(main, null);
        Check(studio.SelectedScheme == Modern, $"Leaving and returning keeps the scheme ({studio.SelectedScheme})");

        // Reopening the project (deselect, select again) restores the saved selection.
        var item = studio.SelectedProject!;
        studio.SelectedProject = null;
        studio.SelectedProject = item;
        await WaitUntil(() => studio.SelectedScheme is not null, TimeSpan.FromSeconds(10), "restored selection");
        await Task.Delay(300);
        Check(studio.SelectedScheme == Modern, $"Reopening the project restores HCGSentinelModern ({studio.SelectedScheme})");

        // 9. Nothing in the project was changed.
        Check(Source(project) == sourceHash, "Project files unchanged (HCGSentinel and its iOS 14.7 target kept)");
        File.WriteAllText(Path.Combine(output, "scheme-workflow-project.txt"), project.Id.ToString());
        return Failures.Count == 0 ? 0 : 1;

        static string Source(ProjectRecord p) => Hash(p.SourceDirectory);
    }

    /// <summary>The scheme shown, the options the build would use, and the xcodebuild arguments planned from them agree.</summary>
    private static Task ExpectSchemeAgreementAsync(MobileBuildStudioViewModel studio, ProjectRecord project, string scheme, string output)
    {
        var options = studio.CurrentOptions;
        Check(options?.Scheme == scheme, $"Build options use {options?.Scheme}");
        var command = studio.Summary.FirstOrDefault(r => r.Label == "Command")?.Value;
        Log("Command shown: " + command);
        Check(command?.Contains($"-scheme {scheme} ", StringComparison.Ordinal) == true, "The command shown names the scheme");
        if (options is null || studio.CurrentToolchains is not { } report) return Task.CompletedTask;
        var work = Path.Combine(output, "plan-work");
        Directory.CreateDirectory(work);
        var plan = XcodePlanner.Plan(new MobilePlanInput
        {
            Options = options, Analysis = project.Analysis!.Mobile!, SourceDirectory = project.SourceDirectory, WorkDirectory = work,
            Toolchains = report, Environment = new Dictionary<string, string>(), ToolHomeDirectory = work,
        });
        var args = plan.Steps[0].Process.Arguments;
        Log("Planned: xcodebuild " + string.Join(' ', args));
        var i = args.ToList().IndexOf("-scheme");
        Check(i >= 0 && args[i + 1] == scheme, $"The build service runs -scheme {(i >= 0 ? args[i + 1] : "?")}");
        var c = args.ToList().IndexOf("-configuration");
        Check(c >= 0 && args[c + 1] == options.Variant && command?.Contains($"-configuration {options.Variant} ") == true, $"Configuration agrees ({options.Variant})");
        return Task.CompletedTask;
    }

    /// <summary>A second app run on the same data folder: the project opens with the selection saved in the first run.</summary>
    private static async Task<int> RunReopenMobileAsync(MainWindowViewModel main, string output)
    {
        var id = Guid.Parse(File.ReadAllText(Path.Combine(output, "scheme-workflow-project.txt")).Trim());
        var project = await _services!.GetRequiredService<IProjectRepository>().GetAsync(id) ?? throw new InvalidOperationException("Project not found.");
        var studio = await OpenStudioAsync(main, project);
        studio.SelectedPlatform = studio.Platforms.First(p => p.Platform == TargetPlatform.IOS);
        LogSelection(studio, "after restart");
        Check(studio.SelectedScheme == Modern, $"After restarting HCG the project opens with {studio.SelectedScheme}");
        studio.SelectOutputCommand.Execute(studio.Outputs.First(o => o.Option.Output == MobileOutput.IosArchive));
        await ExpectSchemeAgreementAsync(studio, project, Modern, output);
        Shot("reopened");
        return Failures.Count == 0 ? 0 : 1;
    }
}
