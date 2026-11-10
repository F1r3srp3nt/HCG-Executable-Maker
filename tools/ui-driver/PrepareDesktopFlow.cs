using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// "Prepare desktop app" through the real New Build wizard: a Vite + React browser app is shown as not buildable, the
/// proposed changes are previewed, the desktop workspace is created (the imported project unchanged) and imported, and
/// the Electron flow then builds it.
/// </summary>
internal static partial class Program
{
    private static async Task<bool> RunPrepareDesktopAsync(BuildWizardViewModel wizard)
    {
        var a = wizard.Analysis!;
        Log($"Inspection: {a.InspectorId} · {a.Framework} · {a.ApplicationType} · {a.Support} — {a.SupportSummary}");
        Check(wizard.IsNodeProject, "Recognized by the Node.js inspector");
        Check(wizard.ProjectOptions.Count == 0 && !wizard.CanConfigure, "The browser app itself is not offered as a desktop build");
        Check(wizard.ShowDesktopConversion, "“Prepare desktop app” is offered for the browser app");
        foreach (var w in wizard.ConvertibleWebProjects) Log("Browser app: " + w.Name + " · " + w.Path);
        if (wizard.SelectedWebProject is null && wizard.ConvertibleWebProjects.FirstOrDefault(p => p.Path == Arg("web-project", "package.json")) is { } pick)
            wizard.SelectedWebProject = pick;
        Log($"Prepare desktop app: [{wizard.ConversionTag.Text}] {wizard.ConversionSummary}");
        foreach (var f in wizard.ConversionBlockers) Log($"  Project configuration: {f.Message} {f.Location}");
        foreach (var t in wizard.ConversionToolchainGaps) Log($"  Installed toolchains: {t}");
        foreach (var f in wizard.ConversionWarnings) Log($"  Warning: {f.Message} {f.Location}");
        foreach (var f in wizard.ConversionInfos) Log($"  Info: {f.Message}");
        Log("  " + wizard.ConversionToolchainText);
        foreach (var c in wizard.ConversionChanges) Log($"  Change: {c.KindLabel} {c.RelativePath} — {c.Reason}");
        Shot("prepare-desktop-preview");
        ShotScrolledToEnd("prepare-desktop-preview-details");

        var plan = wizard.ConversionPlan;
        Check(plan is { CanApply: true }, "No project-configuration blockers");
        Check(!wizard.HasConversionToolchainGaps, "No missing toolchains for preparing the workspace");
        if (plan is not { CanApply: true } || wizard.HasConversionToolchainGaps) return false;
        Check(wizard.ConversionChanges.Any(c => c.RelativePath == "hcg-desktop/main.cjs" && c.Preview.Contains("contextIsolation: true", StringComparison.Ordinal)
                                                 && c.Preview.Contains("nodeIntegration: false", StringComparison.Ordinal) && c.Preview.Contains("sandbox: true", StringComparison.Ordinal)),
            "Preview shows the Electron entry with context isolation, sandbox and no Node integration");
        Check(wizard.ConversionChanges.Any(c => c.RelativePath == "electron-builder.yml"), "Preview shows the electron-builder configuration");
        var pkg = wizard.ConversionChanges.FirstOrDefault(c => c.RelativePath == "package.json");
        Check(pkg is not null && pkg.KindLabel == "Changed" && pkg.Preview.Contains("+ ", StringComparison.Ordinal) && pkg.Preview.Contains("desktop:build", StringComparison.Ordinal),
            "Preview shows package.json as a diff (scripts and dependencies added)");
        if (pkg is not null) foreach (var line in pkg.Preview.Split('\n').Take(60)) Log("    " + line);

        if (Arg("desktop-name") is { } name)
        {
            var changed = name != wizard.ConversionAppName;
            wizard.ConversionAppName = name;
            if (changed)
                Check(wizard.ConversionPreviewStale && !wizard.CreateDesktopWorkspaceCommand.CanExecute(null), "Editing the name requires an updated preview before anything is written");
            wizard.PreviewDesktopConversionCommand.Execute(null);
            Check(wizard.ConversionPlan?.Options.ProductName == name && !wizard.ConversionPreviewStale, $"Preview updated for the name “{name}”");
        }
        if (Arg("desktop-app-id") is { } id)
        {
            wizard.ConversionAppId = id;
            wizard.PreviewDesktopConversionCommand.Execute(null);
            Check(wizard.ConversionPlan?.Options.AppId == id, $"Preview updated for the app id {id}");
        }

        var importedRoot = wizard.Project!.SourceDirectory;
        var importedPackageJson = File.ReadAllText(Path.Combine(importedRoot, plan.ProjectDirectory, "package.json"));
        var importedFiles = Directory.GetFiles(importedRoot, "*", SearchOption.AllDirectories).Length;
        Check(wizard.CreateDesktopWorkspaceCommand.CanExecute(null), "“Create desktop workspace” is enabled after the preview");
        await wizard.CreateDesktopWorkspaceCommand.ExecuteAsync(null);
        foreach (var line in wizard.ConversionLog.TakeLast(15)) Log("  output: " + line);
        var error = wizard.ConversionError ?? wizard.ImportError;
        Check(error is null, "Desktop workspace created and imported" + (error is null ? "" : ": " + error));
        Check(File.ReadAllText(Path.Combine(importedRoot, plan.ProjectDirectory, "package.json")) == importedPackageJson
              && Directory.GetFiles(importedRoot, "*", SearchOption.AllDirectories).Length == importedFiles, "The imported web project was not changed");
        Log("Notice: " + wizard.DesktopWorkspaceNotice);
        Log($"New project: {wizard.Project?.Name} · {wizard.Project?.SourceDirectory}");
        Check(wizard.Project is not null && wizard.Project.SourceDirectory != importedRoot && wizard.IsInspectStep, "The desktop workspace is a separate project, opened at Inspect");
        Check(wizard.ProjectOptions.Any(p => p.Path == "package.json"), "The desktop workspace is offered as an Electron app");
        Shot("prepared-desktop-workspace");
        return wizard.ConversionError is null && wizard.ImportError is null;
    }
}
