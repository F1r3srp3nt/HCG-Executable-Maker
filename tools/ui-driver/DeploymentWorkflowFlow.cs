using System.Security.Cryptography;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// HCG Nexus in Mobile Build Studio, driven like a user: the folder holds an Xcode iOS app (HCGNexus.xcodeproj, scheme
/// HCGNexus, iOS 14.0) and a Theos jailbreak package; the selected Xcode's SDKs start at iOS 15.0.
/// --deployment-workflow [--build]:
///  · two distinct build targets (Xcode app / Theos package), never switched silently, the Theos one handed to New Build's
///    Theos route (GNU Make → .deb);
///  · one concise problem ("Project minimum: iOS 14.0. Selected toolchain minimum: iOS 15.0 …") with the resolutions;
///  · the build-only minimum iOS: inspected effects, explicit confirmation, applied to the command only, dropped when the
///    selection it was accepted for changes (also while a re-inspection runs);
///  · with --build (a Mac with Xcode): real Simulator build and unsigned device archive with the override, each checked for
///    MinimumOSVersion and the executable's LC_BUILD_VERSION minos; the project's files are compared before and after.
/// </summary>
internal static partial class Program
{
    private static string TreeHash(string dir)
    {
        var lines = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..16];
    }

    private static void LogDeployment(MobileBuildStudioViewModel s, string when)
    {
        Log($"[{when}] target={s.SelectedBuildTarget?.Name} scheme={s.SelectedScheme} configuration={s.SelectedConfiguration?.Name} xcode={s.SelectedXcode?.Name} tag=[{s.SupportTag.Text}]");
        if (s.CompatibilityProblem is { } p) Log($"[{when}] Problem: {p}");
        foreach (var r in s.SchemeChecks) Log($"[{when}]   {r.Label}: {r.Value}");
        foreach (var n in s.InspectionNotices) Log($"[{when}]   Not verified: {n}");
        if (s.ToolchainRouteText is { } k) Log($"[{when}]   Resolution · Xcode: {k}");
        if (s.HasSchemeSuggestion) Log($"[{when}]   Resolution · scheme: {s.UseSuggestedSchemeLabel}");
        if (s.HasRaiseOffer) Log($"[{when}]   Resolution · {s.RaiseOfferLabel} ({(s.RaiseOfferSupported ? "available" : "not available")}): {s.RaiseOfferText}");
        if (s.ActiveOverrideText is { } a) Log($"[{when}]   Active: {a}");
        if (s.OverrideNotice is { } o) Log($"[{when}]   Notice: {o}");
        foreach (var o2 in s.Outputs) Log($"[{when}]   Output {o2.Name}: {(o2.IsEnabled ? "available" : "unavailable — " + o2.Reason)}");
    }

    private static async Task<int> RunDeploymentWorkflowAsync(MainWindowViewModel main, BuildWizardViewModel wizard, string output)
    {
        var project = wizard.Project ?? throw new InvalidOperationException("Nothing was imported.");
        var sourceHash = TreeHash(project.SourceDirectory);
        Log($"Imported {project.Name}: {project.SourceDirectory} (tree {sourceHash})");
        Check(wizard.BuildRouteOptions.Count == 2, $"New Build lists both kinds of project: {string.Join(" | ", wizard.BuildRouteOptions.Select(r => r.Label))}");

        await main.NavigateAsync("mobile", project);
        var studio = (MobileBuildStudioViewModel)main.CurrentPage!;
        await WaitUntil(() => studio.SelectedProject?.Project.Id == project.Id && studio.Schemes.Count > 0, TimeSpan.FromSeconds(20), "Nexus in Mobile Build Studio");
        await studio.SelectionSaved;
        studio.SelectedPlatform = studio.Platforms.First(p => p.Platform == TargetPlatform.IOS);

        // 1. The Xcode app and the Theos package are two distinct build targets; the Xcode app is what is judged against Xcode.
        Check(studio.HasBuildTargets && studio.BuildTargets.Count == 2, $"Build targets: {string.Join(" | ", studio.BuildTargets.Select(b => b.Name))}");
        Check(studio.SelectedBuildTarget?.Id == BuildRoutes.XcodeIpa, $"The Xcode app is selected first ({studio.SelectedBuildTarget?.Name})");
        var theosChoice = studio.BuildTargets.FirstOrDefault(b => b.Id == BuildRoutes.TheosDeb);
        Check(theosChoice?.Detail.Contains("GNU Make", StringComparison.Ordinal) == true && theosChoice.Detail.Contains("does not apply", StringComparison.Ordinal),
            $"The Theos package is built with GNU Make and not judged by Xcode's SDK range: {theosChoice?.Detail}");
        Check(studio.SelectedScheme == "HCGNexus", $"Scheme HCGNexus ({studio.SelectedScheme})");
        LogDeployment(studio, "opened");
        Shot("deployment-incompatible");

        // 2–3. One concise explanation, every output blocked by it, and the three resolutions.
        Check(studio.CompatibilityProblem?.StartsWith("Project minimum: iOS 14.0. Selected toolchain minimum: iOS 15.0", StringComparison.Ordinal) == true,
            $"One concise explanation: {studio.CompatibilityProblem}");
        Check(studio.Outputs.Count > 0 && studio.Outputs.All(o => !o.IsEnabled), "Every Xcode output is unavailable");
        Check(studio.Outputs.All(o => o.Reason?.Contains("see Scheme compatibility above", StringComparison.Ordinal) == true), "Outputs refer to the explanation instead of repeating it");
        Check(studio.HasXcodeChoices && studio.ToolchainRouteText is not null, $"Resolution · another Xcode: {studio.ToolchainRouteText}");
        Check(!studio.HasSchemeSuggestion, "No other scheme of this project is verified compatible, so none is suggested");
        Check(studio.HasRaiseOffer && studio.RaiseOfferSupported && studio.RaiseOfferLabel!.Contains("iOS 15.0", StringComparison.Ordinal), $"Resolution · {studio.RaiseOfferLabel}");
        Check(studio.RaiseOfferText.Contains("no longer install", StringComparison.Ordinal) && studio.RaiseOfferText.Contains("not edited", StringComparison.Ordinal),
            "The offer says older iOS versions lose the app and that the project is not edited");
        Check(studio.ShowIosSelection && studio.ShowSchemes, "Scheme, configuration and Xcode stay selectable while no output is available");

        // 1. Theos target: no Xcode outputs, no Xcode judgement; handed to New Build's Theos route only on request.
        studio.SelectedBuildTarget = theosChoice;
        await studio.SelectionSaved;
        LogDeployment(studio, "Theos package");
        foreach (var r in studio.TheosRows) Log($"  Theos · {r.Label}: {r.Value}");
        Check(studio.IsTheosTarget && studio.Outputs.Count == 0 && !studio.ShowIosSelection && !studio.HasSchemeChecks,
            "Theos package selected: no Xcode outputs, no scheme compatibility");
        Check(studio.TheosRows.Any(r => r.Value.Contains("iOS 15.0", StringComparison.Ordinal)) && studio.TheosRows.Any(r => r.Value.Contains("iOS 14.0", StringComparison.Ordinal)),
            "Theos configurations show their own deployment targets (rootless 15.0, rootful 14.0)");
        Shot("deployment-theos");
        studio.BuildTheosPackageCommand.Execute(null);
        await WaitUntil(() => main.CurrentPage is BuildWizardViewModel { Project: not null, IsImporting: false }, TimeSpan.FromSeconds(20), "New Build on the Theos route");
        var routed = (BuildWizardViewModel)main.CurrentPage!;
        await WaitUntil(() => routed.SelectedBuildRoute is not null, TimeSpan.FromSeconds(10), "route");
        Check(routed.SelectedBuildRoute?.Id == BuildRoutes.TheosDeb && routed.Analysis?.InspectorId == "native",
            $"New Build opened on {routed.SelectedBuildRoute?.Label} with the Theos analysis ({routed.Analysis?.Framework})");
        await main.NavigateAsync("mobile", project);
        studio = (MobileBuildStudioViewModel)main.CurrentPage!;
        await WaitUntil(() => studio.SelectedProject?.Project.Id == project.Id && studio.SelectedBuildTarget is not null, TimeSpan.FromSeconds(20), "Mobile Build Studio again");
        await Task.Delay(300);
        Check(studio.SelectedBuildTarget?.Id == BuildRoutes.TheosDeb, $"Returning keeps the Theos package — never switched silently ({studio.SelectedBuildTarget?.Name})");
        studio.SelectedBuildTarget = studio.BuildTargets.First(b => b.Id == BuildRoutes.XcodeIpa);
        studio.SelectedPlatform = studio.Platforms.First(p => p.Platform == TargetPlatform.IOS);
        Check(studio.CompatibilityProblem is not null && studio.Outputs.All(o => !o.IsEnabled), "Back on the Xcode app, the problem is shown again");

        // 3. Explicitly raise the minimum iOS for this build: explained, confirmed, applied to the command only.
        var confirmations = Report.Count(l => l.StartsWith("Dialog (confirmed) Build HCGNexus for iOS 15.0", StringComparison.Ordinal));
        await studio.RaiseMinimumCommand.ExecuteAsync(null);
        var dialog = Report.LastOrDefault(l => l.StartsWith("Dialog (confirmed) Build HCGNexus for iOS 15.0", StringComparison.Ordinal)) ?? "";
        Check(Report.Count(l => l.StartsWith("Dialog (confirmed) Build HCGNexus for iOS 15.0", StringComparison.Ordinal)) == confirmations + 1, "Raising was confirmed in a dialog first");
        Check(dialog.Contains("HCGNexus (app): iOS 14.0 → iOS 15.0", StringComparison.Ordinal), "The dialog lists the effect on each target");
        Check(dialog.Contains("earlier than 15.0", StringComparison.Ordinal) && dialog.Contains("IPHONEOS_DEPLOYMENT_TARGET=15.0", StringComparison.Ordinal), "…what older devices lose and the exact build setting");
        LogDeployment(studio, "raised to 15.0");
        Shot("deployment-raised");
        Check(studio.CompatibilityProblem is null && studio.HasActiveOverride, $"Applied: {studio.ActiveOverrideText}");
        var sim = studio.Outputs.First(o => o.Option.Output == MobileOutput.IosSimulatorApp);
        var archive = studio.Outputs.First(o => o.Option.Output == MobileOutput.IosArchive);
        Check(sim.IsEnabled && archive.IsEnabled, "Simulator app and device archive are available with the override");
        Check(studio.SchemeChecks.Count == 2 && studio.SchemeChecks.All(r => r.Value.StartsWith("Compatible", StringComparison.Ordinal)), "Device and Simulator are each judged with the override");

        // 4. The same values for validation and the build: options, the command shown and the plan.
        studio.SelectOutputCommand.Execute(sim);
        ExpectOverrideInPlan(studio, project, output, "15.0");
        studio.SelectOutputCommand.Execute(archive);
        ExpectOverrideInPlan(studio, project, output, "15.0");

        // 4. A selection change drops the override (it was accepted for another selection) — also while re-inspection runs.
        var refresh = studio.RefreshCommand.ExecuteAsync(null);
        studio.SelectedConfiguration = studio.Configurations.First(c => c.Value == "Debug");
        await refresh;
        await Task.Delay(300);
        LogDeployment(studio, "configuration changed during re-inspection");
        Check(studio.SelectedConfiguration?.Value == "Debug", $"The configuration chosen during re-inspection survives it ({studio.SelectedConfiguration?.Name})");
        Check(!studio.HasActiveOverride && studio.OverrideNotice?.Contains("build configuration changed", StringComparison.Ordinal) == true,
            $"The override did not carry over: {studio.OverrideNotice}");
        Check(studio.CompatibilityProblem is not null && studio.Outputs.All(o => !o.IsEnabled), "…so the problem and the blocked outputs are back");
        Check(studio.CurrentOptions?.DeploymentTargetOverride is null, "…and the build would not use it");
        studio.SelectedConfiguration = studio.Configurations[0];
        await studio.RaiseMinimumCommand.ExecuteAsync(null);
        Check(studio.HasActiveOverride, "Accepted again for the scheme's default configuration");

        Check(TreeHash(project.SourceDirectory) == sourceHash, "The imported project's files are unchanged");
        if (_args.ContainsKey("build")) await BuildWithOverrideAsync(studio, project, sourceHash);
        Check(TreeHash(project.SourceDirectory) == sourceHash, "The imported project's files are unchanged after everything");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void ExpectOverrideInPlan(MobileBuildStudioViewModel studio, ProjectRecord project, string output, string value)
    {
        var options = studio.CurrentOptions;
        Check(options?.DeploymentTargetOverride == value, $"{options?.Output.DisplayName()}: the build options carry minimum iOS {options?.DeploymentTargetOverride}");
        var command = studio.Summary.FirstOrDefault(r => r.Label == "Command")?.Value;
        Check(command?.Contains("IPHONEOS_DEPLOYMENT_TARGET=" + value, StringComparison.Ordinal) == true, $"Command shown: {command}");
        if (options is null || studio.CurrentToolchains is not { } report) return;
        var work = Path.Combine(output, "plan-work");
        Directory.CreateDirectory(work);
        var plan = XcodePlanner.Plan(new MobilePlanInput
        {
            Options = options, Analysis = project.Analysis!.Mobile!, SourceDirectory = project.SourceDirectory, WorkDirectory = work,
            Toolchains = report, Environment = new Dictionary<string, string>(), ToolHomeDirectory = work,
        });
        var args = plan.Steps[0].Process.Arguments;
        Log("Planned: xcodebuild " + string.Join(' ', args));
        Check(args.Contains("IPHONEOS_DEPLOYMENT_TARGET=" + value) && args[args.ToList().IndexOf("-scheme") + 1] == options.Scheme,
            "The build service passes the same scheme and the same build-only setting");
    }

    /// <summary>A real Xcode: build the Simulator app and an unsigned device archive with the override, then check what came out.</summary>
    private static async Task BuildWithOverrideAsync(MobileBuildStudioViewModel studio, ProjectRecord project, string sourceHash)
    {
        foreach (var output in new[] { MobileOutput.IosSimulatorApp, MobileOutput.IosArchive })
        {
            studio.SelectOutputCommand.Execute(studio.Outputs.First(o => o.Option.Output == output));
            if (output == MobileOutput.IosArchive) studio.SelectedSigning = studio.SigningModes.First(m => m.Value == MobileSigningMode.Unsigned);
            studio.TrustAcknowledged = true;
            Check(studio.StartBuildCommand.CanExecute(null), $"{output.DisplayName()} can start");
            if (!studio.StartBuildCommand.CanExecute(null)) continue;
            await studio.StartBuildCommand.ExecuteAsync(null);
            Log($"{output.DisplayName()}: [{studio.ResultTag.Text}] {studio.ResultDetail}");
            if (studio.LastLogPath is { } log && File.Exists(log))
                foreach (var line in File.ReadLines(log).Where(l => l.Contains("IPHONEOS_DEPLOYMENT_TARGET", StringComparison.Ordinal) || l.Contains("override", StringComparison.OrdinalIgnoreCase)).Take(12))
                    Log("  log: " + line);
            Check(studio.ResultTag.Text is "Succeeded", $"{output.DisplayName()} built ({studio.FirstError})");
            foreach (var a in studio.BuiltArtifacts)
            {
                Log($"  Artifact {a.FormatName}: {a.Path}");
                foreach (var c in a.Artifact.Checks) Log($"    {c.Status}: {c.Name} — {c.Detail}");
                var min = a.Artifact.Checks.FirstOrDefault(c => c.Name == "Minimum iOS (matches build)");
                var bin = a.Artifact.Checks.FirstOrDefault(c => c.Name == "Minimum iOS in executable");
                Check(min?.Status == VerificationStatus.Passed && min.Detail.Contains("15.0", StringComparison.Ordinal), $"{a.FormatName}: {min?.Detail}");
                Check(bin?.Status == VerificationStatus.Passed && bin.Detail.Contains("15.0", StringComparison.Ordinal), $"{a.FormatName}: {bin?.Detail}");
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(_shots)!, "deployment-artifacts.txt"), a.Path + "\n");
            }
            Check(TreeHash(project.SourceDirectory) == sourceHash, $"Project files unchanged after the {output.DisplayName()} build");
        }
    }
}

/// <summary>
/// Test-only: --raise-xcode-minimum VERSION keeps the REAL selected Xcode (so builds are real) but reports its SDKs' minimum
/// deployment target as at least VERSION — the state of a Mac whose Xcode no longer accepts the project's iOS version, on a
/// runner whose Xcode still does. Logged as SIMULATED; the build itself is not affected (xcodebuild applies its own range).
/// </summary>
internal sealed class RaisedMinimumToolchains(IToolchainService inner, string minimum) : IToolchainService
{
    public ToolchainReport? Latest => inner.Latest is { } r ? Apply(r) : null;

    public event EventHandler<ToolchainReport>? Updated
    {
        add => inner.Updated += value is null ? null : (s, r) => value(s, Apply(r));
        remove { }
    }

    public async Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        Apply(await inner.GetAsync(refresh, cancellationToken));

    private IosDeploymentRange? Raise(IosDeploymentRange? r) =>
        r is null ? null : IosDeploymentRange.Parse(r.Minimum) is { } m && m >= IosDeploymentRange.Parse(minimum) ? r : r with { Minimum = minimum };

    private ToolchainReport Apply(ToolchainReport r) => r.Xcode is not { } x ? r : r with
    {
        Xcode = x with
        {
            Diagnosis = x.Diagnosis + $" (SIMULATED: SDK minimum reported as at least iOS {minimum})",
            IosDeploymentRange = Raise(x.IosDeploymentRange),
            IosSimulatorDeploymentRange = Raise(x.IosSimulatorDeploymentRange),
            Installed = x.Installed.Select(i => i with { IosRange = Raise(i.IosRange), SimulatorRange = Raise(i.SimulatorRange) }).ToList(),
        },
    };
}
