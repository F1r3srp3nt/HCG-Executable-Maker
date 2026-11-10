using System.Text.Json;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>The Electron path through the real New Build wizard: Inspect → Configure → Package → Build → Results.</summary>
internal static partial class Program
{
    private static async Task<int> RunElectronAsync(BuildWizardViewModel wizard, HostEnvironment host, string output)
    {
        var a = wizard.Analysis!;
        Log($"Inspection: {a.InspectorId} · {a.Framework} · {a.ApplicationType} · {a.Support} — {a.SupportSummary}");
        foreach (var d in a.Diagnostics) Log($"  {d.Severity}: {d.Message}{(d.Path is null ? "" : " (" + d.Path + ")")}");
        Check(wizard.IsNodeProject, "Recognized by the Node.js / Electron inspector");
        Check(!a.SupportSummary.Contains("planned for a later milestone", StringComparison.Ordinal), "No milestone placeholder in the inspection result");
        Log("Electron apps offered: " + string.Join(", ", wizard.ProjectOptions.Select(p => p.Path)));
        if (wizard.ProjectOptions.Count > 1) Check(wizard.SelectedProject is null, "Several Electron apps: none selected automatically");

        if (_args.ContainsKey("expect-not-desktop"))
        {
            foreach (var m in wizard.MissingDependencies) Log("Missing: " + m);
            Check(wizard.ProjectOptions.Count == 0, "No desktop app is offered for a project without Electron");
            Check(!wizard.CanConfigure && !wizard.NextCommand.CanExecute(null), "Cannot continue to Configure");
            Check(wizard.MissingDependencies.Any(m => m.Contains("No Electron dependency", StringComparison.Ordinal)), "Explains the missing Electron dependency");
            Check(wizard.MissingDependencies.Any(m => m.Contains("electron-builder", StringComparison.Ordinal)), "Explains the missing packaging configuration");
            Shot("inspect-not-desktop");
            ShotScrolledToEnd("inspect-not-desktop-details");
            return 0;
        }

        var wanted = Arg("project", "").Trim('/') is { Length: > 0 } dir ? dir + "/package.json" : "package.json";
        var choice = wizard.ProjectOptions.FirstOrDefault(p => p.Path == wanted);
        Check(choice is not null, $"Electron app {wanted} offered");
        if (choice is null) { Shot("inspect"); return 1; }
        wizard.SelectedProject = choice;
        Log($"Electron app: [{wizard.ElectronTag.Text}] {wizard.ElectronStatus}");
        foreach (var line in wizard.ElectronFacts.Split('\n')) Log("  " + line);
        Log("Workflow: " + wizard.ElectronWorkflow);
        foreach (var r in wizard.ElectronRemediation) Log("Missing (project configuration): " + r);
        foreach (var r in wizard.ElectronToolchainRemediation) Log("Missing (installed toolchains): " + r);
        foreach (var n in wizard.ElectronNotes) Log("Note: " + n);
        Check(wizard.IsElectronBuild && wizard.CanConfigure, "Electron app is ready to configure");
        Shot("inspect");
        ShotScrolledToEnd("inspect-details");
        if (!wizard.CanConfigure) return 1;

        // Configure: identity comes from electron-builder's configuration (read-only), target from the implemented combinations.
        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsConfigureStep && wizard.IsElectronBuild && !wizard.IdentityEditable, "Configure step for an Electron app (identity read from its configuration)");
        foreach (var p in wizard.PlatformOptions) Log($"Platform {p.Name}: {(p.IsEnabled ? "available" : "not available")}");
        foreach (var o in wizard.ArchitectureOptions) Log($"Architecture option: {o.Name}");
        if (Arg("expect-name") is { } en) Check(wizard.AppName == en, $"Name {wizard.AppName} (expected {en})");
        if (Arg("expect-bundle-id") is { } eb) Check(wizard.AppId == eb, $"App id {wizard.AppId} (expected {eb})");
        if (Arg("expect-version") is { } ev) Check(wizard.Version == ev, $"Version {wizard.Version} (expected {ev})");
        if (Arg("arch") is { } archArg)
        {
            var arch = archArg is "x64" or "x86_64" ? TargetArchitecture.X64 : TargetArchitecture.Arm64;
            var opt = wizard.ArchitectureOptions.FirstOrDefault(o => o.Value == arch);
            Check(opt is not null, $"Architecture {archArg} offered on this host");
            if (opt is not null) wizard.SelectedArchitecture = opt;
        }
        Log($"Target: {wizard.SelectedPlatform?.Name} {wizard.SelectedArchitecture?.Name} · {wizard.PlatformNote}");
        wizard.OutputDirectory = Path.Combine(output, "artifacts");
        foreach (var err in wizard.ConfigErrors) Log("Config error: " + err);
        Check(wizard.ConfigErrors.Count == 0, "Configuration valid");
        Shot("configure");
        if (wizard.ConfigErrors.Count > 0) return 1;

        // Package: only implemented electron-builder targets are selectable.
        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsPackageStep, "Package step");
        foreach (var o in wizard.PackagingOptions) Log($"Format {o.Name}: {o.Status.Text}{(o.HasReason ? " — " + o.Reason : "")}");
        Check(!wizard.ShowDistribution, "HCG's re-signing options are not shown (electron-builder signs per the project's configuration)");
        var formats = Arg("formats", host.Platform == TargetPlatform.MacOS ? "app" : "folder").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f switch
            {
                "app" => PackagingFormat.MacAppBundle, "zip" => PackagingFormat.PortableZip, "dmg" => PackagingFormat.MacDmg, "folder" => PackagingFormat.PortableFolder,
                _ => throw new ArgumentException("Unknown format " + f),
            }).ToHashSet();
        foreach (var o in wizard.PackagingOptions) o.IsSelected = o.IsEnabled && formats.Contains(o.Option.Format);
        foreach (var f in formats)
            Check(wizard.PackagingOptions.Any(o => o.Option.Format == f && o.IsSelected), $"Format {f.DisplayName()} available and selected");
        Check(wizard.PackagingOptions.Where(o => o.Option.Availability != PackagingAvailability.Available).All(o => !o.IsSelected), "Unimplemented targets cannot be selected");
        Shot("package");

        // Build: trust confirmation, then the three stages stream into the log.
        await wizard.NextCommand.ExecuteAsync(null);
        foreach (var row in wizard.Summary) Log($"Summary {row.Label}: {row.Value}");
        Check(!wizard.StartBuildCommand.CanExecute(null), "Build refused before trust is confirmed");
        wizard.TrustAcknowledged = true;
        Shot("build-summary");
        var stages = new List<string>();
        var shotPackaging = false;
        var buildTask = wizard.StartBuildCommand.ExecuteAsync(null);
        while (!buildTask.IsCompleted)
        {
            if (stages.LastOrDefault() != wizard.CurrentStage) { stages.Add(wizard.CurrentStage); Log("Stage: " + wizard.CurrentStage); }
            if (!shotPackaging && wizard.CurrentStage.Contains("electron-builder", StringComparison.Ordinal)) { shotPackaging = true; Shot("building-electron-builder"); }
            await Task.Delay(250);
        }
        await buildTask;
        Check(stages.Any(s => s.Contains("Install dependencies", StringComparison.Ordinal)), "Stage shown: install dependencies");
        Check(stages.Any(s => s.Contains("Compile", StringComparison.Ordinal)), "Stage shown: compile");
        Check(stages.Any(s => s.Contains("electron-builder", StringComparison.Ordinal)), "Stage shown: package with electron-builder");
        var result = wizard.Result;
        Check(result?.Status == BuildStatus.Succeeded, $"Build {result?.Status}: {wizard.ResultDetail}");
        Shot("results");
        if (result is null) return 1;
        foreach (var (k, v) in result.ToolchainVersions) Log($"Tool {k}: {v}");
        foreach (var m in result.Messages.Take(30)) Log($"Message {m.Severity}: {m.Text}");
        foreach (var art in result.Artifacts)
        {
            Log($"Artifact {art.Format.DisplayName()}: {art.Path} · {art.SizeBytes:N0} bytes · sha256 {art.Sha256} · {art.Verification}");
            foreach (var c in art.Checks) Log($"    [{c.Status}] {c.Name}: {c.Detail}");
            if (art.Verification == VerificationStatus.Failed) Failures.Add($"Artifact {art.Path} failed verification");
        }
        // Where the app is, for the launch test and the project's own packaged-app test.
        var app = result.Artifacts.FirstOrDefault(x => x.Format == PackagingFormat.MacAppBundle)?.Path;
        var folder = result.Artifacts.FirstOrDefault(x => x.Format == PackagingFormat.PortableFolder)?.Path;
        string? executable = null;
        if (app is not null)
        {
            Check(app.EndsWith(".app", StringComparison.Ordinal) && Directory.Exists(app), $"The .app exists: {app}");
            var macOs = Path.Combine(app, "Contents", "MacOS");
            executable = Directory.Exists(macOs) ? Directory.GetFiles(macOs).FirstOrDefault() : null;
        }
        else if (folder is not null)
        {
            executable = Directory.GetFiles(folder).FirstOrDefault(f => !Path.GetFileName(f).Contains('.') && Path.GetFileName(f) is not ("chrome-sandbox" or "chrome_crashpad_handler"));
        }
        Check(executable is not null && File.Exists(executable), $"App executable: {executable}");
        File.WriteAllText(Path.Combine(output, $"{_prefix}-app.txt"), (app ?? folder ?? "") + "\n" + (executable ?? "") + "\n");
        File.WriteAllText(Path.Combine(output, $"{_prefix}-build.json"), JsonSerializer.Serialize(new
        {
            result.Status, result.FailureReason, result.ToolchainVersions, Settings = result.Settings, Stages = stages,
            Artifacts = result.Artifacts.Select(x => new { x.Format, x.Path, x.Sha256, x.SizeBytes, x.Verification, x.Checks }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return result.Status == BuildStatus.Succeeded ? 0 : 1;
    }
}
