using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// "Create desktop application" through the real New Build wizard: the Vite + React browser app is offered under
/// Project to build (classified, not "No runnable application project found"); name, app id, icon, target, formats and
/// output folder are set; every generated file is previewed; the desktop project is created (the imported project
/// unchanged) and opened at the Build step; after the trust confirmation it is built (install → compile → package →
/// verify). With --convert-again, a second conversion updates the same desktop project instead of adding another.
/// </summary>
internal static partial class Program
{
    private static IServiceProvider? _services;

    private static Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync() =>
        _services!.GetRequiredService<HCG.ExecutableMaker.Core.IProjectRepository>().ListAsync();

    private static async Task<int> RunPrepareDesktopAsync(BuildWizardViewModel wizard, HostEnvironment host, string output)
    {
        var a = wizard.Analysis!;
        Log($"Inspection: {a.InspectorId} · {a.Framework} · {a.ApplicationType} · {a.Support} — {a.SupportSummary}");
        foreach (var row in wizard.AnalysisRows) Log($"  {row.Label}: {row.Value}");
        Check(wizard.IsNodeProject, "Recognized by the Node.js inspector");
        foreach (var o in wizard.ProjectOptions) Log("Offered under Project to build: " + o.Name + " · " + o.Path);
        var wanted = Arg("web-project", "package.json");
        var choice = wizard.ProjectOptions.FirstOrDefault(p => p.Path == wanted);
        Check(choice is not null && choice.Project.IsDesktopConversionCandidate, $"The browser app {wanted} is offered as a desktop conversion candidate");
        Check(wizard.ProjectPickerPlaceholder != "No runnable application project found", "The picker does not claim there is nothing to build");
        Check(!wizard.MissingDependencies.Any(m => m.Contains("No Electron dependency", StringComparison.Ordinal)), "No hand-made Electron checklist is shown for a convertible app");
        Check(wizard.AnalysisRows.Any(r => r.Label == "Recommended outputs" && r.Value.Contains("after Create desktop application", StringComparison.Ordinal)), "Recommended outputs say they come after the conversion");
        if (choice is null) return 1;
        wizard.SelectedProject = choice;
        Check(wizard.ShowDesktopConversion && !wizard.IsElectronBuild && !wizard.ShowSupportActions, "“Create desktop application” is shown (not the build-as-is or support panel)");
        Log($"Create desktop application: [{wizard.ConversionTag.Text}] {wizard.ConversionCategory} — {wizard.ConversionSummary}");
        foreach (var f in wizard.ConversionBlockers) Log($"  Project configuration: {f.Message} {f.Location}");
        foreach (var t in wizard.ConversionToolchainGaps) Log($"  Installed toolchains: {t}");
        foreach (var f in wizard.ConversionWarnings) Log($"  Warning: {f.Message} {f.Location}");
        foreach (var f in wizard.ConversionInfos) Log($"  Info: {f.Message}");
        Log("  " + wizard.ConversionToolchainText);
        Log($"  Targets: {string.Join(", ", wizard.ConversionTargets.Select(t => t.Label))} · formats: {string.Join(", ", wizard.ConversionFormats.Select(f => f.Label))}");
        Log($"  Icon: {wizard.ConversionIconName} — {wizard.ConversionIconStatus}");
        Log($"  Signing: {wizard.SelectedConversionSigning?.Label} — {wizard.ConversionSigningStatus}");
        Log($"  Notarization: {wizard.ConversionNotarizationStatus}");
        Log($"  Settings: {wizard.ConversionEnvironmentText}");
        foreach (var c in wizard.ConversionChanges) Log($"  Change: {c.KindLabel} {c.RelativePath} — {c.Reason}");
        Shot("create-desktop-preview");
        ShotScrolledToEnd("create-desktop-preview-details");

        var plan = wizard.ConversionPlan;
        Check(plan is { CanApply: true }, "No project-configuration blockers");
        Check(!wizard.HasConversionToolchainGaps, "No missing toolchains");
        if (plan is not { CanApply: true } || wizard.HasConversionToolchainGaps) return 1;
        Check(wizard.ConversionChanges.Any(c => c.RelativePath == "hcg-desktop/main.cjs" && c.Preview.Contains("contextIsolation: true", StringComparison.Ordinal)
                                                 && c.Preview.Contains("nodeIntegration: false", StringComparison.Ordinal) && c.Preview.Contains("sandbox: true", StringComparison.Ordinal)),
            "Preview shows the Electron entry with context isolation, sandbox and no Node integration");
        Check(wizard.ConversionChanges.Any(c => c.RelativePath == "hcg-desktop/conversion.json"), "Preview lists the record of generated files");
        var pkg = wizard.ConversionChanges.FirstOrDefault(c => c.RelativePath == "package.json");
        Check(pkg is not null && pkg.KindLabel == "Changed" && pkg.Preview.Contains("+ ", StringComparison.Ordinal) && pkg.Preview.Contains("desktop:build", StringComparison.Ordinal),
            "Preview shows package.json as a diff (scripts and dependencies added)");
        Check(wizard.ConversionSigningStatus.Length > 0 && wizard.ConversionNotarizationStatus.StartsWith("Not ", StringComparison.Ordinal), "Signing and notarization are shown as separate operations");

        // The user's choices.
        if (Arg("desktop-name") is { } name) wizard.ConversionAppName = name;
        if (Arg("desktop-app-id") is { } id) wizard.ConversionAppId = id;
        if (Arg("arch") is { } archArg)
        {
            var arch = archArg is "x64" or "x86_64" ? TargetArchitecture.X64 : TargetArchitecture.Arm64;
            var t = wizard.ConversionTargets.FirstOrDefault(x => x.Combination.Architecture == arch);
            Check(t is not null, $"Target {archArg} offered on this host");
            if (t is not null) wizard.SelectedConversionTarget = t;
        }
        var formats = Arg("formats", host.Platform == TargetPlatform.MacOS ? "app,zip,dmg" : "folder").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var f in wizard.ConversionFormats.Where(f => f.IsEnabled))
            f.IsSelected = formats.Contains(f.Format switch { PackagingFormat.PortableZip => "zip", PackagingFormat.MacDmg => "dmg", _ => "?" });
        wizard.ConversionOutputDirectory = Path.Combine(output, "artifacts");
        if (Arg("desktop-name") is not null) Check(wizard.ConversionPreviewStale, "Changed options mark the preview as out of date");
        wizard.PreviewDesktopConversionCommand.Execute(null);
        Check(!wizard.ConversionPreviewStale && wizard.ConversionPlan?.Options.ProductName == wizard.ConversionAppName, $"Preview updated for “{wizard.ConversionAppName}”");
        Shot("create-desktop-options");

        var importedRoot = wizard.Project!.SourceDirectory;
        var importedPackageJson = File.ReadAllText(Path.Combine(importedRoot, plan.ProjectDirectory, "package.json"));
        var importedFiles = Directory.GetFiles(importedRoot, "*", SearchOption.AllDirectories).Length;
        var webProject = wizard.Project;
        Check(wizard.CreateDesktopWorkspaceCommand.CanExecute(null), "“Create desktop application” is enabled");
        await wizard.CreateDesktopWorkspaceCommand.ExecuteAsync(null);
        foreach (var line in wizard.ConversionLog) Log("  generate: " + line);
        var error = wizard.ConversionError ?? wizard.ImportError;
        Check(error is null, "Desktop project created" + (error is null ? "" : ": " + error));
        if (error is not null) return 1;
        Check(File.ReadAllText(Path.Combine(importedRoot, plan.ProjectDirectory, "package.json")) == importedPackageJson
              && Directory.GetFiles(importedRoot, "*", SearchOption.AllDirectories).Length == importedFiles, "The imported web project was not changed");
        Log("Notice: " + wizard.DesktopWorkspaceNotice);
        var desktop = wizard.Project!;
        Log($"Desktop project: {desktop.Name} · {desktop.SourceDirectory}");
        Check(desktop.Id != webProject.Id && wizard.IsElectronBuild, "The desktop project is a separate project, built as an Electron app");
        Check(wizard.IsBuildStep, "Opened at the Build step with the chosen options");
        Check(wizard.OutputDirectory == Path.Combine(output, "artifacts"), "Output folder carried over");
        Check(File.Exists(Path.Combine(desktop.SourceDirectory, "hcg-desktop", "conversion.json")), "The record of generated files is in the desktop project");
        if (Arg("expect-name") is { } en) Check(wizard.AppName == en, $"Name {wizard.AppName} (expected {en})");

        var code = await RunElectronBuildStepAsync(wizard, output);

        if (_args.ContainsKey("convert-again"))
        {
            // Converting the same web app again updates the same desktop project: no second project, no stale files.
            var before = (await ListProjectsAsync()).Count;
            await wizard.UseProjectCommand.ExecuteAsync(new ProjectItem(webProject));
            wizard.SelectedProject = wizard.ProjectOptions.First(p => p.Path == wanted);
            await WaitUntil(() => wizard.ConversionExistingProjectText.Length > 0, TimeSpan.FromSeconds(20), "existing desktop project lookup");
            Log("Again: " + wizard.ConversionExistingProjectText);
            wizard.ConversionAppName = Arg("desktop-name") ?? wizard.ConversionAppName;
            wizard.ConversionOutputDirectory = Path.Combine(output, "artifacts");
            await wizard.CreateDesktopWorkspaceCommand.ExecuteAsync(null);
            Check(wizard.ConversionError is null && wizard.Project?.Id == desktop.Id, "Converting again updated the same desktop project" + (wizard.ConversionError is { } e2 ? ": " + e2 : ""));
            Check((await ListProjectsAsync()).Count == before, "No additional project was created");
            var generated = Directory.GetFiles(Path.Combine(desktop.SourceDirectory, "hcg-desktop")).Select(Path.GetFileName).Order().ToList();
            Log("hcg-desktop/: " + string.Join(", ", generated));
            Check(generated.Count == generated.Distinct().Count() && !Directory.Exists(desktop.WorkspacePath + "/source.old"), "No duplicated or leftover files");
            Shot("converted-again");
        }
        return code;
    }
}
