using HCG.Impactor.Model;

namespace HCG.Impactor.Devices;

/// <summary>Checks an inspected app against a discovered device before anything is installed.</summary>
public static class DeviceCompatibility
{
    public static IReadOnlyList<Finding> Evaluate(IpaReport ipa, AppleDevice device)
    {
        var f = new List<Finding>();
        var name = device.Name;
        if (!device.IsPhysical)
            f.Add(Finding.Block("device.simulator", "Simulator, not a device", $"{name} is a Simulator. Device-signed IPAs install on physical iPhones and iPads.", "Install Simulator builds with Xcode or xcrun simctl."));
        if (device.Platform is { } plat && plat is not ("iOS" or "iPadOS"))
            f.Add(Finding.Block("device.platform", "Different platform", $"{name} runs {plat}; this is an iOS/iPadOS app."));

        switch (device.Connection)
        {
            case DeviceConnection.Unavailable:
                f.Add(Finding.Block("device.unavailable", "Device not connected", $"{name} is known to this Mac but not reachable now.",
                    "Connect it with a cable and unlock it. For Wi-Fi, keep it on the same network and unlocked; it must have been paired over USB first."));
                break;
            case DeviceConnection.Network:
                f.Add(Finding.Warn("device.wireless", "Wireless connection", $"{name} is connected over the network. HCG has validated USB installation; wireless installation uses the same Apple tooling but is slower and can drop.",
                    "Use a cable for the most reliable installation."));
                break;
        }
        if (device.Pairing == PairingState.Unpaired)
            f.Add(Finding.Block("device.not-paired", "Not paired with this Mac", $"{name} has not trusted this Mac.",
                "Unlock the device, connect it by cable, tap Trust in the \"Trust This Computer?\" prompt and enter the passcode. Then refresh."));
        if (device.DeveloperMode == DeveloperModeState.Disabled)
            f.Add(Finding.Block("device.developer-mode", "Developer Mode is off", $"{name} must have Developer Mode on to run apps signed for development or ad hoc distribution (iOS 16 and later).",
                "On the device: Settings › Privacy & Security › Developer Mode › On, restart, then confirm Turn On after unlocking. If the switch is missing, connect the device to Xcode once (Window › Devices and Simulators)."));

        var app = ipa.App;
        if (app?.MinimumOSVersion is { } min && device.OsVersion is { } os && Compare(os, min) < 0)
            f.Add(Finding.Block("device.os-too-old", "iOS version too old", $"The app needs iOS {min} or later; {name} runs {os}.", "Update the device (Settings › General › Software Update) or build the app with a lower deployment target."));
        foreach (var ext in ipa.Extensions.Where(e => e.MinimumOSVersion is not null && device.OsVersion is not null && Compare(device.OsVersion, e.MinimumOSVersion) < 0))
            f.Add(Finding.Warn("device.extension-os", "Extension needs newer iOS", $"{ext.RelativePath} needs iOS {ext.MinimumOSVersion}; {name} runs {device.OsVersion}.") with { Subject = ext.RelativePath });

        var cls = device.DeviceClass?.ToLowerInvariant();
        if (cls == "iphone" && !ipa.Families.HasFlag(DeviceFamilies.IPhone))
            f.Add(Finding.Block("device.family", "iPad-only app", $"The app supports {Families(ipa.Families)} only; {name} is an iPhone."));
        if (cls == "ipad" && !ipa.Families.HasFlag(DeviceFamilies.IPad) && ipa.Families.HasFlag(DeviceFamilies.IPhone))
            f.Add(Finding.Info("device.iphone-on-ipad", "iPhone app on iPad", "The app is iPhone-only; iPadOS runs it in compatibility mode."));

        if (app is not null && device.CpuArchitecture is { } cpu)
        {
            var archs = app.Architectures.ToList();
            if (cpu == "arm64" && archs.Count > 0 && !archs.Contains("arm64"))
                f.Add(Finding.Block("device.architecture", "Unsupported architecture", $"The app contains {string.Join(", ", archs)} only; {name} needs arm64 (arm64e requires an A12 chip or later)."));
        }
        if (ipa.RequiredCapabilities.Contains("arm64") && device.CpuArchitecture is "armv7" or "armv7s")
            f.Add(Finding.Block("device.capability", "Device lacks a required capability", "The app requires arm64."));
        if (!device.CanInstall && device.Connection != DeviceConnection.Unavailable)
            f.Add(Finding.Warn("device.no-install-capability", "Installation not offered", $"CoreDevice does not currently offer app installation on {name}.",
                "Unlock the device and keep it connected; if it persists, open Xcode › Window › Devices and Simulators once so Xcode prepares the device."));
        return f;
    }

    private static string Families(DeviceFamilies f) => string.Join(" and ", Enum.GetValues<DeviceFamilies>().Where(x => x != DeviceFamilies.None && f.HasFlag(x)).Select(x => x switch
    {
        DeviceFamilies.IPhone => "iPhone", DeviceFamilies.IPad => "iPad", DeviceFamilies.AppleTv => "Apple TV", DeviceFamilies.Watch => "Apple Watch", _ => "Apple Vision",
    }));

    /// <summary>Compares dotted versions numerically ("17.10" &gt; "17.9").</summary>
    public static int Compare(string a, string b)
    {
        var pa = a.Split('.').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var c = (i < pa.Length ? pa[i] : 0).CompareTo(i < pb.Length ? pb[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
