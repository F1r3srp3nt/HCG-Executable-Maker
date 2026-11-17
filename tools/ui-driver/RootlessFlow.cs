using System.Text.Json;
using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// After Mobile Build Studio archived an iOS app (--mobile … --mobile-output archive), --rootless continues like a user:
/// "Rootless .deb (jailbreak)…" on the build result opens the Rootless package page with that archive, the identity and
/// provenance fields are filled from --rootless-* options, and "Build package and handoff" runs the workflow.
/// Options: --rootless-package ID --rootless-version V --rootless-name N --rootless-description D --rootless-out DIR
/// [--rootless-repository O/N --rootless-commit SHA --rootless-ci-run ID --rootless-ci-artifact NAME --rootless-access public|connect|paid
///  --rootless-change TEXT --rootless-known-issue TEXT --rootless-device-ios X.Y].
/// </summary>
internal static partial class Program
{
    private static async Task<int> RootlessFromStudioAsync(MainWindowViewModel main, MobileBuildStudioViewModel studio, string output)
    {
        Check(studio.CanPackageRootless, "Mobile Build Studio offers \"Rootless .deb (jailbreak)…\" for the device archive");
        if (!studio.CanPackageRootless) return 1;
        studio.PackageRootlessCommand.Execute(null);
        await WaitUntil(() => main.CurrentPage is RootlessPackageViewModel, TimeSpan.FromSeconds(30), "Rootless package page");
        var page = (RootlessPackageViewModel)main.CurrentPage!;
        await WaitUntil(() => page.Source.Length > 0 && page.AppSummary != "No app inspected yet.", TimeSpan.FromSeconds(60), "inspection");
        Log($"Rootless page source: {page.Source}");
        Log($"Inspected: {page.AppSummary}");
        Log(page.LdidStatus);
        page.PackageId = Arg("rootless-package", "");
        page.PackageVersion = Arg("rootless-version", page.PackageVersion);
        page.AppName = Arg("rootless-name", page.AppName);
        page.Description = Arg("rootless-description", "");
        page.Repository = Arg("rootless-repository", "");
        page.Commit = Arg("rootless-commit", "");
        page.CiRun = Arg("rootless-ci-run", "");
        page.CiArtifact = Arg("rootless-ci-artifact", "");
        page.CiRepository = Arg("rootless-ci-repository", "");
        page.CiCommit = Arg("rootless-ci-commit", "");
        page.ProductionAccess = Arg("rootless-access", "public");
        page.DeviceIos = Arg("rootless-device-ios", "16.5");
        page.Changes = Arg("rootless-change", "");
        page.KnownIssues = Arg("rootless-known-issue", "");
        page.OutputDir = Path.GetFullPath(Arg("rootless-out", Path.Combine(output, "rootless")));
        Shot("rootless-filled");
        await page.RunCommand.ExecuteAsync(null);
        foreach (var s in page.Stages) Log($"Stage {s.Name}: [{s.Tag.Text}]");
        foreach (var c in page.Checks) Log($"    [{c.Status.Text}] {c.Name}: {c.Detail}");
        foreach (var m in page.Missing) Log("    MISSING " + m);
        Log($"Rootless result: [{page.ResultTag.Text}] {page.Result}");
        Shot("rootless-result");
        ShotScrolledToEnd("rootless-log");
        File.WriteAllText(Path.Combine(output, $"{_prefix}-rootless.json"), JsonSerializer.Serialize(new
        {
            state = page.ResultTag.Text, result = page.Result, handoff = page.HandoffFolder,
            stages = page.Stages.Select(s => new { s.Name, state = s.Tag.Text }),
            checks = page.Checks.Select(c => new { c.Name, status = c.Status.Text, c.Detail }), missing = page.Missing, log = page.Log,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(page.ResultTag.Tone == "success", $"Rootless package built, validated and handed off ({page.ResultTag.Text})");
        return Failures.Count == 0 ? 0 : 1;
    }
}
