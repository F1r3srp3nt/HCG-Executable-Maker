using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// Sign &amp; Notarize (macOS) through the real page: --mac-distribute --source &lt;app.zip|App.app&gt; [--sums SHA256SUMS.txt]
/// [--origin text] [--expect-bundle-id id] [--expect-nested N] [--entitlement key] [--release-version v] [--build-number n]
/// [--sign --out-folder dir [--expect-sign-status Succeeded|Failed] [--expect-failure text]]. Signing uses whatever
/// Developer ID Application identity the keychain holds (CI: a synthetic one); nothing is ever notarized here.
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunMacDistributeAsync(MainWindowViewModel main, string output)
    {
        await main.NavigateAsync("mac-distribute", null);
        var page = (MacDistributionViewModel)main.CurrentPage!;
        page.InputPath = Path.GetFullPath(_args["source"]);
        if (Arg("sums") is { } sums) page.ChecksumsPath = Path.GetFullPath(sums);
        page.Origin = Arg("origin", "");
        await page.InspectCommand.ExecuteAsync(null);
        Log($"Inspect: {page.AppSummary}");
        Log(page.SignatureAsReceived);
        Log(page.NestedCode);
        foreach (var c in page.InputChecks) Log($"  [{c.StatusText}] {c.Name}: {c.Detail}");
        Check(page.IsInspected, "the input is usable: " + page.Status);
        if (Arg("expect-bundle-id") is { } id) Check(page.BundleIdentifier == id, $"bundle identifier {page.BundleIdentifier} (expected {id})");
        if (Arg("expect-nested") is { } n) Check(page.NestedCode.Contains($"({n})", StringComparison.Ordinal), $"nested code: {page.NestedCode} (expected {n})");
        if (Arg("expect-input-failure") is { } fail)
        {
            Check(page.InputChecks.Any(c => c.Status == VerificationStatus.Failed && c.Detail.Contains(fail, StringComparison.Ordinal)), $"input refused with \"{fail}\"");
            Shot("mac-distribute-refused");
            return 0;
        }
        if (Arg("release-version") is { } v) page.ReleaseVersion = v;
        if (Arg("build-number") is { } b) page.BuildNumber = b;
        page.EntitlementsText = Arg("entitlement", "");
        page.EntitlementsConfirmed = true;
        await page.RefreshIdentitiesCommand.ExecuteAsync(null);
        Log("Identities: " + page.IdentitiesNote);
        foreach (var o in page.ApplicationIdentities) Log("  Developer ID Application: " + o.Label);
        Shot("mac-distribute-inspected");
        if (!_args.ContainsKey("sign")) return 0;

        page.MakePkg = page.InstallerIdentities.Any(i => i.Identity is not null);
        page.OutputFolder = Path.GetFullPath(Arg("out-folder", Path.Combine(output, "mac-distribute")));
        Check(page.SelectedApplicationIdentity?.Identity is not null, "a Developer ID Application identity is selected from the keychain");
        await page.SignCommand.ExecuteAsync(null);
        Log("Sign: " + page.Status);
        foreach (var r in page.Results) Log($"  [{r.StatusText}] {r.Name}: {r.Detail}");
        Check(page.Job is not null, "the signing job was recorded: " + page.JobSummary);
        if (page.Job is { } job)
        {
            Check(ExistingMacAppDistribution.IsExistingAppBuild(job), "the job is an existing-app distribution job");
            if (Arg("expect-sign-status") is { } st) Check(job.Status.ToString() == st, $"job status {job.Status} (expected {st}): {job.FailureReason}");
            if (Arg("expect-failure") is { } f) Check(job.FailureReason?.Contains(f, StringComparison.Ordinal) == true, $"failure names \"{f}\": {job.FailureReason}");
            Check(!page.IsReady, "not offered for distribution before notarization");
            Log("Signed app: " + job.Artifacts.FirstOrDefault()?.Path);
        }
        ShotScrolledToEnd("mac-distribute-signed");
        return 0;
    }
}
