using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// Actions on a built iOS artifact: launch a Simulator app, open an archive in Xcode's Organizer, open an IPA in HCG
/// Impactor, and show the final signature and entitlements as codesign reads them. Installing on a connected device is
/// <see cref="InstallOnDeviceCommand"/>.
/// </summary>
public sealed partial class MobileBuildStudioViewModel
{
    [ObservableProperty] private string _artifactActionMessage = "";

    /// <summary>Boots a Simulator, installs the Simulator app, launches it and saves a screenshot.</summary>
    [RelayCommand]
    private async Task LaunchInSimulatorAsync(ArtifactItem? item)
    {
        if (item is null || _runner is null || !OperatingSystem.IsMacOS()) return;
        var bundleId = BundleId(item.Path);
        if (bundleId is null) { ArtifactActionMessage = $"{item.Name} has no readable CFBundleIdentifier."; return; }
        ArtifactActionMessage = $"Launching {item.Name} in the iOS Simulator…";
        var screenshot = Path.Combine(Path.GetDirectoryName(item.Path)!, Path.GetFileNameWithoutExtension(item.Path) + "-simulator.png");
        var result = await new HCG.ExecutableMaker.Porting.DeviceLauncher(_runner).LaunchIosSimulatorAsync(item.Path, bundleId, screenshot, CancellationToken.None,
            progress: line => Avalonia.Threading.Dispatcher.UIThread.Post(() => ArtifactActionMessage = line));
        ArtifactActionMessage = $"{result.Status}: {result.Detail}" + (result.ScreenshotPath is { } s ? $" Screenshot: {s}" : "");
    }

    /// <summary>Opens the archive with Xcode, which shows it in the Organizer (Window › Organizer).</summary>
    [RelayCommand]
    private async Task OpenInOrganizerAsync(ArtifactItem? item)
    {
        if (item is null || _runner is null || !Directory.Exists(item.Path)) return;
        var (r, _, e) = await _runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/open", Arguments = ["-a", "Xcode", item.Path], Timeout = TimeSpan.FromSeconds(30) }, CancellationToken.None);
        ArtifactActionMessage = r.Succeeded ? $"Opened {item.Name} in Xcode's Organizer." : "Xcode could not open the archive: " + e.Trim();
    }

    /// <summary>Opens the IPA in HCG Impactor (inspection, signing check, installation on a device).</summary>
    [RelayCommand]
    private void OpenInImpactor(ArtifactItem? item)
    {
        if (item is not null && File.Exists(item.Path)) _navigation?.Navigate("impactor", item.Path);
    }

    /// <summary>The final signature and entitlements of the app (in an archive: its Products/Applications app), as codesign reads them.</summary>
    [RelayCommand]
    private async Task InspectSignatureAsync(ArtifactItem? item)
    {
        if (item is null || _runner is null) return;
        var app = item.Path.EndsWith(".xcarchive", StringComparison.Ordinal)
            ? Directory.EnumerateDirectories(Path.Combine(item.Path, "Products", "Applications"), "*.app").FirstOrDefault()
            : item.Path;
        if (app is null || !Directory.Exists(app)) { ArtifactActionMessage = "No app bundle to inspect."; return; }
        var (_, o1, e1) = await _runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/codesign", Arguments = ["-d", "--verbose=4", app], Timeout = TimeSpan.FromMinutes(1) }, CancellationToken.None);
        var (_, ents, _) = await _runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/codesign", Arguments = ["-d", "--entitlements", "-", "--xml", app], Timeout = TimeSpan.FromMinutes(1) }, CancellationToken.None);
        var facts = (o1 + "\n" + e1).Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("Identifier=", StringComparison.Ordinal) || l.StartsWith("Authority=", StringComparison.Ordinal) || l.StartsWith("TeamIdentifier=", StringComparison.Ordinal)
                        || l.StartsWith("Timestamp=", StringComparison.Ordinal) || l.StartsWith("Signed Time=", StringComparison.Ordinal) || l.StartsWith("CodeDirectory", StringComparison.Ordinal));
        var entitlements = "";
        try
        {
            if (ents.IndexOf("<?xml", StringComparison.Ordinal) is var start and >= 0)
                entitlements = string.Join("\n", PlistReader.ParseDictionary(System.Text.Encoding.UTF8.GetBytes(ents[start..]))
                    .Select(kv => $"  {kv.Key} = {HCG.ExecutableMaker.Packaging.Signing.AppleCertificates.RenderValue(kv.Value)}"));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { entitlements = "  (unreadable)"; }
        var profile = Path.Combine(app, "embedded.mobileprovision");
        await _dialogs.AlertAsync($"Signature of {Path.GetFileName(app)}",
            string.Join("\n", facts) + "\n\nEntitlements:\n" + (entitlements.Length == 0 ? "  (none)" : entitlements) +
            "\n\nEmbedded provisioning profile: " + (File.Exists(profile) ? "present (open the IPA in HCG Impactor for its type, devices and expiry)" : "none"));
    }

    private static string? BundleId(string app)
    {
        var plist = Path.Combine(app, "Info.plist");
        try { return File.Exists(plist) && PlistReader.ParseDictionary(File.ReadAllBytes(plist)).TryGetValue("CFBundleIdentifier", out var v) ? v as string : null; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException) { return null; }
    }
}
