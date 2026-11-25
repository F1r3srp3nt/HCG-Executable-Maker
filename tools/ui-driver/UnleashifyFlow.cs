using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// Unleashify driven like a user: Import (New Build → Import folder/ZIP) → Mobile Build Studio → "Unleashify…" → choose
/// the scheme → fill the package → confirm trust → Build → Export Candidate Handoff, with a screenshot at each step.
/// Options: --unleashify --scheme S --unleashify-package ID --unleashify-version V [--unleashify-name N --unleashify-description D
/// --unleashify-out DIR --unleashify-devices "Name, 16.5;Name, 13.3" --unleashify-installed FILE --unleashify-tester-confirmed
/// --unleashify-repository O/N --unleashify-commit SHA --unleashify-ci-run ID --unleashify-ci-artifact NAME --unleashify-ci-repository O/N
/// --unleashify-ci-commit SHA --unleashify-owner-approval-file FILE --unleashify-access public|connect|paid --unleashify-change TEXT --unleashify-known-issue TEXT
/// --unleashify-simulator-app PATH (checks that a Simulator build is refused) --expect-deployment-target X.Y --expect-readiness ready|not-ready].
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunUnleashifyAsync(MainWindowViewModel main, BuildWizardViewModel wizard, string output)
    {
        var project = wizard.Project ?? throw new InvalidOperationException("Nothing was imported.");
        Log($"Imported {project.Name} from {project.SourceLocation}: {project.Analysis?.ApplicationType} · {project.Analysis?.SupportSummary}");
        Shot("unleashify-imported");

        // Mobile Build Studio → "Unleashify…".
        await main.NavigateAsync("mobile", project);
        var studio = (MobileBuildStudioViewModel)main.CurrentPage!;
        Check(studio.SelectedProject?.Project.Id == project.Id, "Project selected in Mobile Build Studio");
        Check(studio.CanUnleashify, "Mobile Build Studio offers \"Unleashify…\" for this iOS project");
        Shot("unleashify-studio");
        if (!studio.CanUnleashify) return 1;
        studio.OpenUnleashifyCommand.Execute(null);
        await WaitUntil(() => main.CurrentPage is UnleashifyViewModel { Project: not null }, TimeSpan.FromSeconds(30), "Unleashify page");
        var page = (UnleashifyViewModel)main.CurrentPage!;
        await WaitUntil(() => page.Modes.Count == 3, TimeSpan.FromSeconds(30), "modes");

        // Pinned commit (local build): clone the local repository, check out exactly this commit, import it, select it.
        if (Arg("unleashify-pin-repo") is { } pinRepo)
        {
            page.PinRepository = pinRepo;
            page.PinCommit = Arg("unleashify-pin-commit", "HEAD");
            Check(page.ImportPinnedCommitCommand.CanExecute(null), "Check out and import is offered");
            await page.ImportPinnedCommitCommand.ExecuteAsync(null);
            Log("Pinned source: " + page.PinStatus);
            Check(page.PinStatus.StartsWith("Imported commit ", StringComparison.Ordinal), "The pinned commit was checked out (clean) and imported");
            if (Arg("unleashify-commit") is { } pinned) Check(page.Commit == pinned, $"Provenance commit is the pinned commit {pinned}");
            ShotAt("SOURCE", "unleashify-pinned-source");
        }

        // Source revision.
        Log($"Source revision: [{page.RevisionTag.Text}] {page.RevisionText}");
        Check(page.RevisionTag.Text is "Current" or "From archive", $"Source revision recorded at import ({page.RevisionTag.Text})");
        if (Arg("unleashify-commit") is { } expectedCommit)
            Check(page.RevisionText.StartsWith(expectedCommit, StringComparison.Ordinal), $"Revision shown is the imported commit {expectedCommit}");

        // Modes: all three shown, only the working one buildable.
        foreach (var m in page.Modes) Log($"Mode {m.Label}: [{m.Tag.Text}] {m.Reason ?? m.Description}");
        Check(page.Modes.Select(m => m.Label).SequenceEqual(["Jailbreak App", "Jailbreak Tweak", "App + Tweak"]), "Modes: Jailbreak App, Jailbreak Tweak, App + Tweak");
        Check(page.Modes.Select(m => m.Available).SequenceEqual([true, false, false]), "Only Jailbreak App can be built");
        Check(page.SelectedMode?.Label == "Jailbreak App", "Jailbreak App is selected");
        page.SelectedMode = page.Modes[1];
        Check(!page.BuildCommand.CanExecute(null), "Build is not offered for Jailbreak Tweak");
        page.SelectedMode = page.Modes[2];
        Check(!page.BuildCommand.CanExecute(null), "Build is not offered for App + Tweak");
        page.SelectedMode = page.Modes[0];

        // Scheme and deployment target.
        var scheme = Arg("scheme", "HCGSentinelModern");
        Check(page.Schemes.Contains(scheme), $"Scheme {scheme} offered ({string.Join(", ", page.Schemes)})");
        page.SelectedScheme = scheme;
        Log($"Scheme detail: {page.SchemeDetail}");
        Log($"Deployment target: {page.DeploymentTarget} · bundle ID {page.BundleId}");
        if (Arg("expect-deployment-target") is { } dt) Check(page.DeploymentTarget.StartsWith($"iOS {dt} ", StringComparison.Ordinal), $"Deployment target iOS {dt} shown (kept)");

        // A Simulator build is refused with a clear error.
        if (Arg("unleashify-simulator-app") is { } sim)
        {
            page.UseDeviceBuild = true;
            page.DeviceBuild = sim;
            page.InspectInputCommand.Execute(null);
            Log("Simulator input: " + page.AppSummary);
            Check(page.AppSummary.Contains("Simulator build refused", StringComparison.Ordinal), "A Simulator build is refused with a clear error");
            ShotAt("INPUT", "unleashify-simulator-refused");
            page.DeviceBuild = "";
            page.BuildFromXcode = true;
            page.AppSummary = "No app inspected yet.";
        }

        // Package, devices, installed builds, provenance.
        page.PackageId = Arg("unleashify-package", page.PackageId);
        page.PackageVersion = Arg("unleashify-version", page.PackageVersion);
        page.AppName = Arg("unleashify-name", page.AppName);
        page.Description = Arg("unleashify-description", page.Description);
        page.OutputDir = Path.GetFullPath(Arg("unleashify-out", Path.Combine(output, "unleashify")));
        if (Arg("unleashify-devices") is { } devices) page.TestDevices = devices.Replace(';', '\n');
        if (Arg("unleashify-installed") is { } listing && File.Exists(listing)) page.InstalledListing = File.ReadAllText(listing);
        page.TesterConfirmedNoOtherBuild = _args.ContainsKey("unleashify-tester-confirmed");
        page.Repository = Arg("unleashify-repository", page.Repository);
        page.Commit = Arg("unleashify-commit", page.Commit);
        page.CiRun = Arg("unleashify-ci-run", "");
        page.CiArtifact = Arg("unleashify-ci-artifact", "");
        page.CiRepository = Arg("unleashify-ci-repository", "");
        page.CiCommit = Arg("unleashify-ci-commit", "");
        page.IsLocalBuild = _args.ContainsKey("unleashify-local");
        if (page.IsLocalBuild)
        {
            page.SourceVisibility = Arg("unleashify-visibility", "private");
            page.HostLabel = Arg("unleashify-host", page.HostLabel);
            page.ApprovedSha256 = Arg("unleashify-approved-sha256", "");
        }
        page.ProductionAccess = Arg("unleashify-access", page.ProductionAccess);
        page.Changes = Arg("unleashify-change", "");
        page.KnownIssues = Arg("unleashify-known-issue", "");
        if (Arg("unleashify-owner-approval-file") is { } approvalFile)
        {
            // The owner's recorded approval, typed into the page's "Owner's approval" field exactly as recorded.
            page.OwnerApproval = string.Join(" ", File.ReadAllLines(approvalFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
            Log("Owner approval (from the record): " + page.OwnerApproval);
            Check(page.OwnerApproval.Length > 0, "Owner approval entered on the Unleashify page");
        }
        foreach (var d in page.Devices) Log($"Device {d.Name} iOS {d.Ios}: [{d.Tag.Text}] {d.Detail}");
        Log($"Installed builds: [{page.InstalledTag.Text}] {page.InstalledSummary}");
        Shot("unleashify-panel");
        ShotAt("PACKAGE", "unleashify-package");
        if (page.OwnerApproval.Length > 0) ShotAt("CANDIDATE AND PROVENANCE", "unleashify-owner-approval");

        // Build (after trust).
        Check(!page.BuildCommand.CanExecute(null), "Build refused before trust is confirmed");
        page.TrustAcknowledged = true;
        Check(page.BuildCommand.CanExecute(null), "Build can start after trust");
        if (!page.BuildCommand.CanExecute(null)) return 1;
        await page.BuildCommand.ExecuteAsync(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        foreach (var s in page.Stages) Log($"Stage {s.Name}: [{s.Tag.Text}]");
        foreach (var c in page.Checks) Log($"    [{c.Status.Text}] {c.Name}: {c.Detail}");
        Log($"App: {page.AppSummary}");
        Log($"Package: [{page.ValidationTag.Text}] {page.Result}");
        Log($"Deployment target after the build: {page.DeploymentTarget} · bundle ID {page.BundleId}");
        foreach (var d in page.Devices) Log($"Device {d.Name} iOS {d.Ios}: [{d.Tag.Text}] {d.Detail}");
        Log($"Installed builds: [{page.InstalledTag.Text}] {page.InstalledSummary}");
        foreach (var f in page.InstalledFindings) Log("    " + f);
        Log($"Device testing: [{page.ReadinessTag.Text}] {page.ReadinessText}");
        ShotAt("TEST DEVICES AND INSTALLED BUILDS", "unleashify-devices");
        ShotAt("BUILD OUTPUT", "unleashify-built");
        Check(page.ValidationTag.Tone == "success", $"Package built and validated ({page.ValidationTag.Text})");
        if (Arg("expect-deployment-target") is { } kept) Check(page.DeploymentTarget.StartsWith($"iOS {kept} ", StringComparison.Ordinal), $"The package keeps iOS {kept}");
        if (page.ValidationTag.Tone != "success") { ShotScrolledToEnd("unleashify-log"); return 1; }

        // Export Candidate Handoff.
        Check(page.ExportHandoffCommand.CanExecute(null), "Export Candidate Handoff is available after a validated build");
        await page.ExportHandoffCommand.ExecuteAsync(null);
        foreach (var m in page.Missing) Log("    MISSING " + m);
        Log($"Handoff: [{page.HandoffTag.Text}] {page.HandoffFolder}");
        Log($"Device testing: [{page.ReadinessTag.Text}] {page.ReadinessText}");
        ShotAt("BUILD OUTPUT", "unleashify-handoff");
        ShotScrolledToEnd("unleashify-log");
        Check(page.HandoffFolder is not null && Directory.Exists(page.HandoffFolder), "Candidate handoff exported");
        if (_args.ContainsKey("unleashify-owner-approval-file"))
        {
            Check(!page.Missing.Any(m => m.StartsWith("owner approval", StringComparison.Ordinal)), "The handoff no longer lists the owner approval as missing");
            var toml = File.ReadAllText(Path.Combine(page.HandoffFolder!, "package.toml"));
            Check(toml.Contains("\nexception = \"", StringComparison.Ordinal) && toml.Contains($"\nexception_versions = [\"{page.PackageVersion}\"]", StringComparison.Ordinal),
                "package.toml records the approval as [access] exception for this version only");
            Check(!toml.Contains("HYPOTHETICAL", StringComparison.Ordinal) && !toml.Contains("<owner>", StringComparison.Ordinal), "No placeholder approval in package.toml");
        }
        if (page.IsLocalBuild)
        {
            var req = File.ReadAllText(Path.Combine(page.HandoffFolder!, File.Exists(Path.Combine(page.HandoffFolder!, "import-request.toml")) ? "import-request.toml" : "import-request.INCOMPLETE.toml"));
            Log("Import request:\n" + req);
            Check(req.Contains("kind = \"local-file\"", StringComparison.Ordinal), "A local build exports a local-file import request");
            Check(!req.Contains("run_id", StringComparison.Ordinal) && !req.Contains("artifact", StringComparison.Ordinal), "No CI run or artifact is claimed for a local build");
            Check(page.HandoffTag.Tone == "success", $"The local-file request is pinned ({page.HandoffTag.Text})");
            Log("Toolchain: " + page.ToolchainText);
        }
        if (Arg("unleashify-hcg-repo") is { } hcgRepo)
        {
            page.HcgRepoCheckout = hcgRepo;
            Check(page.PreviewImportCommand.CanExecute(null), "Preview import is offered after the export");
            await page.PreviewImportCommand.ExecuteAsync(null);
            Log($"Import preview: [{page.PreviewTag.Text}]\n{page.PreviewText}");
            ShotAt("HCG REPO IMPORT PREVIEW", "unleashify-import-preview");
            var want = Arg("expect-preview", "blocked-by-approval");
            Check(want == "importable" ? page.PreviewTag.Tone == "success" : page.PreviewTag.Text == "Blocked only by the owner's approval",
                $"HCG Repo's importer (dry run): {page.PreviewTag.Text} (expected {want})");
        }
        if (Arg("expect-readiness") is { } readiness)
            Check(readiness == "ready" ? page.ReadinessTag.Tone == "success" : page.ReadinessTag.Tone != "success", $"Device-testing readiness: {page.ReadinessTag.Text} (expected {readiness})");
        File.WriteAllText(Path.Combine(output, $"{_prefix}-unleashify.json"), JsonSerializer.Serialize(new
        {
            revision = page.RevisionText, revisionState = page.RevisionTag.Text, scheme = page.SelectedScheme, packageId = page.PackageId,
            packageVersion = page.PackageVersion, deploymentTarget = page.DeploymentTarget, bundleId = page.BundleId, app = page.AppSummary,
            modes = page.Modes.Select(m => new { m.Label, m.Available, m.Reason }), validation = page.ValidationTag.Text, result = page.Result,
            devices = page.Devices.Select(d => new { d.Name, d.Ios, d.Compatible, d.Detail }), installed = page.InstalledTag.Text,
            installedSummary = page.InstalledSummary, ownerApproval = page.OwnerApproval, readiness = page.ReadinessTag.Text, readinessText = page.ReadinessText,
            deviceBuild = page.DeviceBuild, handoff = page.HandoffFolder, handoffState = page.HandoffTag.Text, missing = page.Missing,
            stages = page.Stages.Select(s => new { s.Name, state = s.Tag.Text }),
            checks = page.Checks.Select(c => new { c.Name, status = c.Status.Text, c.Detail }), log = page.Log,
        }, new JsonSerializerOptions { WriteIndented = true }));

        // The other input: the device archive just built, imported as an existing build. Same app → same package bytes.
        if (_args.ContainsKey("unleashify-reuse-build") && page.DeviceBuild is { Length: > 0 } archive && page.Result is { } first)
        {
            var firstSha = first.Split("SHA-256 ")[1].Split(' ')[0];
            page.UseDeviceBuild = true;
            page.OutputDir = Path.GetFullPath(Path.Combine(output, "unleashify-from-device-build"));
            page.InspectInputCommand.Execute(null);
            Log("Existing device build: " + page.AppSummary);
            Check(page.BuildCommand.CanExecute(null), "Build is offered for an existing device build (no trust needed: nothing from the project runs)");
            await page.BuildCommand.ExecuteAsync(null);
            Log($"Package from the existing build: [{page.ValidationTag.Text}] {page.Result}");
            ShotAt("INPUT", "unleashify-existing-build");
            Check(page.ValidationTag.Tone == "success" && page.Result?.Contains("SHA-256 " + firstSha, StringComparison.Ordinal) == true,
                $"The existing device build packages to the same bytes (SHA-256 {firstSha})");
        }
        return Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Scrolls the section titled <paramref name="label"/> into view and captures the window.</summary>
    private static void ShotAt(string label, string name)
    {
        if (_window is null) return;
        var target = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(_window).OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == label && t.IsEffectivelyVisible);
        if (target?.Parent?.Parent is Control section)
        {
            if (Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(section).OfType<ScrollViewer>().FirstOrDefault() is { } sv
                && section.TranslatePoint(new Point(0, 0), (Control)sv.Content!) is { } p)
                sv.Offset = new Vector(0, Math.Max(0, p.Y - 8));
            Dispatcher.UIThread.RunJobs();
        }
        Shot(name);
    }
}
