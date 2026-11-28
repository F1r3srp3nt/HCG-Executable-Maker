using System.Text.Json;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// Mobile Build Studio driven like a user: the project imported in New Build is opened in Mobile Build Studio, the
/// output (simulator / archive / ipa), scheme and signing are chosen, readiness and the precise state are recorded, and
/// the build runs after the trust confirmation. Options: --mobile, --mobile-output sim|archive|ipa (--output is the output folder), --scheme S, --signing
/// unsigned|project, --expect-state "Ready to build", --expect-blocked-by "Deployment target", --mobile-expect-not-mobile (a desktop repository whose mobile projects are only test fixtures), --record-only (a failure is
/// recorded, not fatal — used to reproduce HCG Sentinel's archive across Xcode versions).
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunMobileAsync(MainWindowViewModel main, BuildWizardViewModel wizard, string output)
    {
        var project = wizard.Project ?? throw new InvalidOperationException("Nothing was imported.");
        Log($"Inspection: {project.Analysis?.InspectorId} · {project.Analysis?.ApplicationType} · {project.Analysis?.Support} — {project.Analysis?.SupportSummary}");
        if (_args.ContainsKey("mobile-expect-not-mobile"))
        {
            // A desktop repository (HCG Executable Maker itself) whose tests/Fixtures hold mobile samples: none of them may be
            // offered as its app; New Build builds the desktop app.
            foreach (var d in project.Analysis?.Diagnostics ?? []) Log($"  {d.Severity}: {d.Message}");
            Check(project.Analysis?.Mobile is null, "Not offered as a mobile project (its mobile projects are test fixtures)");
            Check(project.Analysis?.Native is null, "No Theos route attached from a test fixture");
            Check(project.Analysis?.Diagnostics.Any(d => d.Message.Contains("inside test-fixture folders", StringComparison.Ordinal)) == true, "The test fixtures are listed as samples, not apps");
            await main.NavigateAsync("mobile", project);
            var mbs = (MobileBuildStudioViewModel)main.CurrentPage!;
            Check(mbs.MobileProjects.All(p => p.Project.Id != project.Id), "Mobile Build Studio does not list it");
            Shot("not-mobile");
            return Failures.Count == 0 ? 0 : 1;
        }
        Check(project.Analysis?.Mobile is not null, "Mobile Build Studio can build this project");
        await main.NavigateAsync("mobile", project);
        var studio = (MobileBuildStudioViewModel)main.CurrentPage!;
        Check(studio.SelectedProject?.Project.Id == project.Id, "Project selected in Mobile Build Studio");
        foreach (var row in studio.ProjectRows) Log($"  {row.Label}: {row.Value}");
        foreach (var d in studio.Diagnostics) Log($"  {d.Severity}: {d.Message}");
        studio.SelectedPlatform = studio.Platforms.First(p => p.Platform == TargetPlatform.IOS);
        foreach (var o in studio.Outputs) Log($"Output {o.Name}: {(o.IsEnabled ? "available" : "unavailable — " + o.Reason)}");
        var wanted = Arg("mobile-output", "sim") switch { "archive" => MobileOutput.IosArchive, "ipa" => MobileOutput.IosIpa, "device" => MobileOutput.IosDeviceApp, _ => MobileOutput.IosSimulatorApp };
        var item = studio.Outputs.FirstOrDefault(o => o.Option.Output == wanted);
        Check(item is not null, $"Output {wanted} offered");
        if (item is null) return 1;
        if (_args.ContainsKey("expect-no-unsigned"))
        {
            // .NET for iOS archives only with code signing. The archive is judged with the project's own signing (not
            // as "unsigned", which made it unavailable whatever signing was chosen), and no unsigned mode is offered.
            Check(item.Reason?.Contains("only with code signing", StringComparison.Ordinal) != true, $"Output {wanted} is judged with the project's signing, not as unsigned: {item.Reason ?? "available"}");
            if (item.IsEnabled)
            {
                studio.SelectOutputCommand.Execute(item);
                Check(studio.SigningModes.All(m => m.Value != MobileSigningMode.Unsigned), $"No unsigned signing mode is offered ({string.Join(", ", studio.SigningModes.Select(m => m.Name))})");
            }
            else
            {
                studio.SelectOutputCommand.Execute(item);
                Check(studio.SelectedOutput?.Option.Output != wanted, $"The unavailable archive cannot be selected ({item.Reason})");
            }
            return Failures.Count == 0 ? 0 : 1;
        }
        studio.SelectOutputCommand.Execute(item);
        if (Arg("scheme") is { } scheme)
        {
            Check(studio.Schemes.Contains(scheme), $"Scheme {scheme} offered ({string.Join(", ", studio.Schemes)})");
            studio.SelectedScheme = scheme;
        }
        if (Arg("variant") is { } variant)
        {
            Check(studio.Variants.Contains(variant), $"Configuration {variant} offered ({string.Join(", ", studio.Variants)})");
            studio.SelectedVariant = variant;
        }
        if (Arg("signing") is { } signing)
        {
            var mode = signing switch { "unsigned" => MobileSigningMode.Unsigned, "manual" => MobileSigningMode.AppleManual, _ => MobileSigningMode.ProjectDefault };
            var choice = studio.SigningModes.FirstOrDefault(m => m.Value == mode);
            Check(choice is not null, $"Signing {mode} offered");
            if (choice is null) return 1;
            studio.SelectedSigning = choice;
            // A named certificate and installed profile (CodesignKey / CodesignProvision), as typed in the Signing section.
            if (Arg("identity") is { } identity) studio.SigningIdentity = identity;
            if (Arg("profile") is { } profile) studio.ProvisioningProfile = profile;
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
        if (_args.ContainsKey("launch-simulator") && studio.BuiltArtifacts.FirstOrDefault(x => x.IsSimulatorApp) is { } simApp)
        {
            // The result card's own "Launch in Simulator" button: boot, install, launch, screenshot.
            await studio.LaunchInSimulatorCommand.ExecuteAsync(simApp);
            Log("Simulator launch: " + studio.ArtifactActionMessage);
            Check(studio.ArtifactActionMessage.StartsWith("Passed", StringComparison.Ordinal), "The Simulator app launched from the result card: " + studio.ArtifactActionMessage);
        }
        if (Arg("expect-artifacts") is { } expectedFormats)
            foreach (var f in expectedFormats.Split(','))
                Check(studio.BuiltArtifacts.Any(x => x.Artifact.Format.ToString() == f), $"Artifact {f} produced ({string.Join(", ", studio.BuiltArtifacts.Select(x => x.Artifact.Format))})");
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
