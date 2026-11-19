using System.Text.Json;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// A native macOS app built with Xcode, through the real New Build wizard: Inspect (detected settings, preflight) →
/// Configure → Package → Build → Results. Options: --project path.xcodeproj, --scheme NAME, --configuration NAME,
/// --arch arm64|universal|x64, --formats app,zip,dmg, --expect-bundle-id ID, --expect-version V,
/// --expect-blocked-host (on a non-Mac: recognised, never buildable), --cancel-after-seconds N (cancellation test).
/// Writes &lt;prefix&gt;-artifacts.txt (app, zip, dmg, output folder) and &lt;prefix&gt;-build.json.
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunXcodeMacAsync(BuildWizardViewModel wizard, HostEnvironment host, string output)
    {
        var a = wizard.Analysis!;
        Log($"Inspection: {a.InspectorId} · {a.ApplicationType} · {a.Language} · {a.Framework} · {a.Runtime} · {a.Support} — {a.SupportSummary}");
        foreach (var d in a.Diagnostics) Log($"  {d.Severity}: {d.Message}{(d.Path is null ? "" : " (" + d.Path + ")")}");
        foreach (var row in wizard.AnalysisRows) Log($"  {row.Label}: {row.Value.Replace("\n", " | ")}");
        Check(wizard.IsMacXcodeProject && a.ApplicationType == "Native macOS app — Xcode", "Shown as “Native macOS app — Xcode”");
        foreach (var o in wizard.ProjectOptions) Log($"Offered: {o.Path} — {o.Name}");
        var wantedProject = Arg("project");
        var choice = wantedProject is null ? wizard.SelectedProject ?? wizard.ProjectOptions.FirstOrDefault() : wizard.ProjectOptions.FirstOrDefault(p => p.Path == wantedProject);
        Check(choice is not null, $"Project {wantedProject ?? "(primary)"} offered");
        if (choice is null) return 1;
        wizard.SelectedProject = choice;
        LogXcode(wizard);
        Shot("xcode-inspect");

        if (_args.ContainsKey("expect-blocked-host"))
        {
            Check(host.Platform != TargetPlatform.MacOS, "Blocked-host check runs on a non-Mac host");
            Check(a.Support == SupportLevel.RequiresDifferentHost && wizard.XcodeTag.Text == "Needs a Mac", $"State: [{wizard.XcodeTag.Text}] {a.Support}");
            Check(!wizard.CanConfigure && !wizard.NextCommand.CanExecute(null), "Cannot continue to Configure: nothing is built here");
            Check(wizard.XcodeFixes.Any(f => f.Contains("on a Mac", StringComparison.Ordinal)), "Explains that a Mac is required");
            return Failures.Count == 0 ? 0 : 1;
        }

        if (Arg("scheme") is { } scheme) { Check(wizard.XcodeSchemes.Contains(scheme), $"Scheme {scheme} offered"); wizard.SelectedXcodeScheme = scheme; }
        if (Arg("configuration") is { } config) { Check(wizard.XcodeConfigurations.Contains(config), $"Configuration {config} offered"); wizard.SelectedXcodeConfiguration = config; }
        Check(wizard.SelectedXcodeConfiguration == (Arg("configuration") ?? "Release"), $"Configuration: {wizard.SelectedXcodeConfiguration}");
        foreach (var x in wizard.XcodeArchitectures) Log($"Architecture offered: {x.Name}");
        var defaultArch = wizard.SelectedXcodeArchitecture?.Value;
        Check(defaultArch == (host.Architecture == TargetArchitecture.Arm64 ? MacXcodeArchitecture.AppleSilicon : MacXcodeArchitecture.Intel), $"Default architecture is this Mac's: {wizard.SelectedXcodeArchitecture?.Name}");
        if (Arg("arch") is { } archArg)
        {
            var want = archArg switch { "universal" => MacXcodeArchitecture.Universal, "x64" or "x86_64" => MacXcodeArchitecture.Intel, _ => MacXcodeArchitecture.AppleSilicon };
            var opt = wizard.XcodeArchitectures.FirstOrDefault(x => x.Value == want);
            Check(opt is not null, $"Architecture {archArg} offered by the selected SDK");
            if (opt is null) return 1;
            wizard.SelectedXcodeArchitecture = opt;
        }
        Log("Overrides:\n" + wizard.XcodeOverridesText);
        Check(wizard.XcodeTag.Text == "Ready" && wizard.CanConfigure, $"State: [{wizard.XcodeTag.Text}]" + string.Join(" ", wizard.XcodeFixes));
        Shot("xcode-ready");
        ShotScrolledToEnd("xcode-ready-details");
        if (!wizard.CanConfigure) return 1;

        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsConfigureStep && wizard.IsMacXcodeBuild && !wizard.IsDotNetBuild, "Configure step for an Xcode build");
        Check(!wizard.IdentityEditable, "The app's identity is the project's (not editable)");
        Log($"Identity: {wizard.AppName} {wizard.Version} ({wizard.AppId}) · {wizard.PlatformNote}");
        if (Arg("expect-bundle-id") is { } eid) Check(wizard.AppId == eid, $"Bundle ID {wizard.AppId} (expected {eid})");
        if (Arg("expect-version") is { } ev) Check(wizard.Version == ev, $"Version {wizard.Version} (expected {ev})");
        wizard.OutputDirectory = Path.Combine(output, "artifacts");
        foreach (var err in wizard.ConfigErrors) Log("Config error: " + err);
        Check(wizard.ConfigErrors.Count == 0, "Configuration valid");
        Shot("xcode-configure");
        if (wizard.ConfigErrors.Count > 0) return 1;

        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsPackageStep, "Package step");
        foreach (var o in wizard.PackagingOptions) Log($"Format {o.Name}: {o.Status.Text}{(o.HasReason ? " — " + o.Reason : "")}");
        Check(!wizard.ShowDistribution, "No re-signing choices for an Xcode app (the project's signing is kept)");
        var formats = Arg("formats", "app,zip,dmg").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f switch { "app" => PackagingFormat.MacAppBundle, "zip" => PackagingFormat.PortableZip, "dmg" => PackagingFormat.MacDmg, _ => throw new ArgumentException("Unknown format " + f) })
            .ToHashSet();
        foreach (var o in wizard.PackagingOptions) o.IsSelected = o.IsEnabled && formats.Contains(o.Option.Format);
        foreach (var f in formats) Check(wizard.PackagingOptions.Any(o => o.Option.Format == f && o.IsSelected), $"Format {f.DisplayName()} available and selected");
        Shot("xcode-package");

        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsBuildStep, "Build step");
        foreach (var row in wizard.Summary) Log($"Summary {row.Label}: {row.Value}");
        Check(!wizard.StartBuildCommand.CanExecute(null), "Build refused before trust is confirmed");
        wizard.TrustAcknowledged = true;
        var buildTask = wizard.StartBuildCommand.ExecuteAsync(null);
        var cancelAfter = Arg("cancel-after-seconds") is { } cas ? TimeSpan.FromSeconds(double.Parse(cas, System.Globalization.CultureInfo.InvariantCulture)) : (TimeSpan?)null;
        var started = DateTime.UtcNow;
        var cancelled = false;
        while (!buildTask.IsCompleted)
        {
            if (cancelAfter is { } c && !cancelled && wizard.IsBuilding && wizard.CurrentStage.Contains("xcodebuild", StringComparison.Ordinal) && DateTime.UtcNow - started > c)
            {
                Log($"Cancelling during: {wizard.CurrentStage}");
                wizard.CancelBuildCommand.Execute(null);
                cancelled = true;
            }
            await Task.Delay(100);
        }
        await buildTask;
        var stages = wizard.StageHistory.ToList();
        Log("Stages: " + string.Join(" → ", stages));
        var result = wizard.Result;
        if (cancelAfter is not null)
        {
            Check(cancelled, "The build was cancelled while xcodebuild was running");
            Check(result?.Status == BuildStatus.Cancelled, $"Build {result?.Status}: {result?.FailureReason}");
            Check(result?.Artifacts.Count == 0, "No artifact is offered from a cancelled build");
            Shot("xcode-cancelled");
            return Failures.Count == 0 ? 0 : 1;
        }
        Check(result?.Status == BuildStatus.Succeeded, $"Build {result?.Status}: {wizard.ResultDetail}");
        Shot("xcode-results");
        if (result is null) return 1;
        foreach (var (k, v) in result.ToolchainVersions) Log($"Tool {k}: {v}");
        foreach (var m in result.Messages.Take(40)) Log($"Message {m.Severity}: {m.Text}");
        foreach (var art in result.Artifacts)
        {
            Log($"Artifact {art.Format.DisplayName()}: {art.Path} · {art.SizeBytes:N0} bytes · sha256 {art.Sha256} · {art.Verification}");
            foreach (var c in art.Checks) Log($"    [{c.Status}] {c.Name}: {c.Detail}");
            if (art.Verification == VerificationStatus.Failed) Failures.Add($"Artifact {art.Path} failed verification");
        }
        string? Path_(PackagingFormat f) => result.Artifacts.FirstOrDefault(x => x.Format == f)?.Path;
        var outputRoot = Path.GetDirectoryName(Path_(PackagingFormat.MacAppBundle)?.TrimEnd('/') ?? "") ?? "";
        File.WriteAllText(Path.Combine(output, $"{_prefix}-artifacts.txt"),
            string.Join("\n", Path_(PackagingFormat.MacAppBundle) ?? "", Path_(PackagingFormat.PortableZip) ?? "", Path_(PackagingFormat.MacDmg) ?? "", outputRoot) + "\n");
        File.WriteAllText(Path.Combine(output, $"{_prefix}-build.json"), JsonSerializer.Serialize(new
        {
            result.Status, result.FailureReason, result.ToolchainVersions, Settings = result.Settings, Stages = stages,
            Artifacts = result.Artifacts.Select(x => new { x.Format, x.Path, x.Sha256, x.SizeBytes, x.Verification, x.Checks }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return result.Status == BuildStatus.Succeeded && Failures.Count == 0 ? 0 : 1;
    }

    private static void LogXcode(BuildWizardViewModel wizard)
    {
        Log($"Xcode panel [{wizard.XcodeTag.Text}]");
        foreach (var line in wizard.XcodeFacts.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log("  " + line);
        Log($"  Schemes: {string.Join(", ", wizard.XcodeSchemes)} (selected {wizard.SelectedXcodeScheme}) · configurations: {string.Join(", ", wizard.XcodeConfigurations)} (selected {wizard.SelectedXcodeConfiguration})");
        foreach (var p in wizard.XcodePreflight) Log("  Preflight " + p);
        foreach (var f in wizard.XcodeFixes) Log("  Fix: " + f);
    }
}
