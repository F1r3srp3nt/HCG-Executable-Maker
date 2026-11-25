using Avalonia.Threading;
using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// HCG Impactor as a user meets it: open an IPA (--source), read the inspection report, review the signing plan, and
/// visit every workspace section (screenshots). With --impactor-sign the IPA is signed through Signing Studio's own
/// command (real codesign on macOS; --identity picks the Keychain identity). Devices are listed with the real devicectl.
/// Nothing is installed by this flow.
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunImpactorAsync(MainWindowViewModel main)
    {
        var ipa = Path.GetFullPath(_args["source"]);
        await main.NavigateAsync("impactor", ipa);
        var page = (ImpactorViewModel)main.CurrentPage!;
        await WaitUntil(() => !page.IsBusy && page.Inspection is not null, TimeSpan.FromMinutes(3), "IPA inspection");
        Log("Impactor: " + page.PlatformText + (page.PlatformLimitation is { } l ? " — " + l : ""));
        Log($"Inspected: {page.IpaTitle} · {page.IpaSubtitle}");
        foreach (var f in page.IpaFacts) Log($"  {f.Key}: {f.Value}");
        foreach (var b in page.Bundles) Log($"  Bundle {b.Kind} {b.Path} {b.Identifier} · {b.Detail} · {b.Signature}");
        foreach (var f in page.IpaFindings) Log($"  [{f.Tag.Text}] {f.Title}: {f.Detail}");
        Check(page.HasIpa, "The IPA was inspected");
        if (Arg("expect-bundle-id") is { } bid) Check(page.IpaFacts.Any(f => f.Key == "Bundle identifier" && f.Value == bid), $"Bundle identifier is {bid}");

        async Task Visit(ImpactorSection s, string name)
        {
            page.SelectedSectionItem = page.Sections.First(x => x.Section == s);
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Shot(name);
        }

        await Visit(ImpactorSection.Dashboard, "impactor-dashboard");
        await Visit(ImpactorSection.Inspector, "impactor-inspector");
        ShotScrolledToEnd("impactor-inspector-end");

        await page.RefreshInventoryCommand.ExecuteAsync(null);
        foreach (var i in page.Identities) Log($"Identity: {i.Name} · {i.Kind} · {i.Team} · {i.Expires} · [{i.Tag.Text}] · {i.Sha1}");
        foreach (var p in page.Profiles) Log($"Profile: {p.Name} · {p.Kind} · {p.AppId} · {p.Expires} · {p.Devices} · [{p.Tag.Text}] · {p.Fit}");
        if (page.InventoryNote is { } note) Log("Inventory note: " + note);
        if (Arg("identity") is { } sha) page.SelectedIdentity = page.IdentityChoices.FirstOrDefault(i => i.Sha1.Equals(sha, StringComparison.OrdinalIgnoreCase)) ?? page.SelectedIdentity;
        page.BuildPlanCommand.Execute(null);
        Log("Plan: " + page.PlanSummary);
        foreach (var a in page.Assignments) Log($"  Assign {a.Bundle}: {a.Identifier} · {a.Profile} · {a.Entitlements}");
        foreach (var f in page.PlanFindings) Log($"  [{f.Tag.Text}] {f.Title}: {f.Detail}");
        await Visit(ImpactorSection.Signing, "impactor-signing-studio");

        if (_args.ContainsKey("impactor-sign"))
        {
            Check(page.PlanReady, "Signing Studio has a ready plan: " + page.PlanSummary);
            if (page.PlanReady)
            {
                await page.SignCommand.ExecuteAsync(null);
                foreach (var line in page.SignLog) Log("  sign: " + line);
                Check(page.SignedIpaPath is not null && page.SignResult is not null && File.Exists(page.SignedIpaPath), "Signed through Signing Studio: " + (page.SignResult ?? page.DiagnosisTitle));
                ShotScrolledToEnd("impactor-signed");
            }
        }

        await page.RefreshDevicesCommand.ExecuteAsync(null);
        Log("Devices: " + page.DevicesNote);
        foreach (var d in page.Devices) Log($"  Device {d.Name} · {d.Model} · {d.Connection} · {d.PairingTag.Text} · {d.DeveloperModeTag.Text} · {d.InstallTag.Text}");
        await Visit(ImpactorSection.Devices, "impactor-device-manager");
        await page.CheckDeploymentCommand.ExecuteAsync(null);
        Log("Queue: " + page.QueueSummary);
        await Visit(ImpactorSection.Queue, "impactor-deployment-queue");
        await Visit(ImpactorSection.Certificates, "impactor-certificates");
        await Visit(ImpactorSection.Profiles, "impactor-provisioning");
        await Visit(ImpactorSection.Monitor, "impactor-signing-monitor");
        foreach (var a in page.SignedApps) Log($"Monitor: {a.App} · {a.Detail} · until {a.Expires} · [{a.Tag.Text}]");
        await Visit(ImpactorSection.History, "impactor-history");

        page.PastedOutput = "ERROR: Failed to install the app on the device. (com.apple.dt.CoreDeviceError error 3002)\nA valid provisioning profile for this executable was not found. (0xe8008015) udid 00008140-001A2B3C4D5E001E";
        page.DiagnosePastedCommand.Execute(null);
        Log($"Diagnosis: {page.DiagnosisTitle} · {page.DiagnosisRemedy}");
        Check(page.DiagnosisTitle.Contains("provisioning profile", StringComparison.OrdinalIgnoreCase), "A pasted installation error is explained");
        Check(!page.DiagnosisRaw.Contains("001A2B3C4D5E", StringComparison.Ordinal), "The device UDID is redacted in the diagnostics");
        await Visit(ImpactorSection.Diagnostics, "impactor-diagnostics");
        return 0;
    }
}
