using System.Text.Json;
using HCG.Impactor.Devices;

namespace HCG.Impactor.MacOS;

/// <summary>
/// Parses the JSON files Xcode's <c>devicectl … --json-output</c> writes. Defensive: every field is optional, unknown
/// fields are ignored, and missing values stay null — never guessed. The shapes follow devicectl in Xcode 15–26;
/// a newer Xcode that renames a field produces "not reported", not a wrong value.
/// </summary>
public static class DevicectlJson
{
    public sealed record Outcome(bool Succeeded, string ErrorText);

    /// <summary>info.outcome ("success"/"failed") and every error string (domain, code, descriptions, underlying errors).</summary>
    public static Outcome ReadOutcome(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var outcome = Str(root, "info", "outcome");
            var errors = new List<string>();
            if (root.TryGetProperty("error", out var err)) CollectStrings(err, errors, 0);
            var ok = outcome is null ? errors.Count == 0 : outcome.Equals("success", StringComparison.OrdinalIgnoreCase);
            return new Outcome(ok, string.Join("\n", errors.Distinct()));
        }
        catch (JsonException) { return new Outcome(false, "devicectl wrote a result that is not valid JSON."); }
    }

    public static IReadOnlyList<AppleDevice> ParseDevices(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return [];
        var list = new List<AppleDevice>();
        foreach (var d in devices.EnumerateArray())
        {
            var id = Str(d, "identifier");
            if (id is null) continue;
            var transport = Str(d, "connectionProperties", "transportType");
            var tunnel = Str(d, "connectionProperties", "tunnelState");
            var pairing = Str(d, "connectionProperties", "pairingState");
            var reality = Str(d, "hardwareProperties", "reality");
            var caps = new List<string>();
            if (d.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var cap in c.EnumerateArray())
                    if (Str(cap, "featureIdentifier") is { } f) caps.Add(f);
            var connection = transport switch
            {
                "wired" => DeviceConnection.Usb,
                "localNetwork" or "network" => DeviceConnection.Network,
                _ => DeviceConnection.Unavailable,
            };
            if (tunnel is "unavailable" && transport is null) connection = DeviceConnection.Unavailable;
            list.Add(new AppleDevice
            {
                Identifier = id,
                Udid = Str(d, "hardwareProperties", "udid"),
                Name = Str(d, "deviceProperties", "name") ?? Str(d, "hardwareProperties", "marketingName") ?? "Apple device",
                ProductType = Str(d, "hardwareProperties", "productType"),
                MarketingName = Str(d, "hardwareProperties", "marketingName"),
                DeviceClass = Str(d, "hardwareProperties", "deviceType"),
                Platform = Str(d, "hardwareProperties", "platform"),
                OsVersion = Str(d, "deviceProperties", "osVersionNumber"),
                OsBuild = Str(d, "deviceProperties", "osBuildUpdate"),
                CpuArchitecture = Str(d, "hardwareProperties", "cpuType", "name"),
                Connection = connection,
                Transport = transport,
                Pairing = pairing switch { "paired" => PairingState.Paired, "unpaired" or "unsupported" => PairingState.Unpaired, null => PairingState.Unknown, _ => PairingState.Unknown },
                DeveloperMode = Str(d, "deviceProperties", "developerModeStatus") switch { "enabled" => DeveloperModeState.Enabled, "disabled" => DeveloperModeState.Disabled, _ => DeveloperModeState.Unknown },
                DeveloperServicesAvailable = Bool(d, "deviceProperties", "ddiServicesAvailable"),
                TunnelState = tunnel,
                BootState = Str(d, "deviceProperties", "bootState"),
                IsPhysical = reality is null or "physical",
                Capabilities = caps,
                Source = source,
            });
        }
        return list;
    }

    /// <summary>result.installedApplications[0] → (bundle id, installation URL).</summary>
    public static (string? BundleId, string? Location) ParseInstall(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("installedApplications", out var apps) && apps.ValueKind == JsonValueKind.Array)
            foreach (var a in apps.EnumerateArray())
                return (Str(a, "bundleID") ?? Str(a, "bundleIdentifier"), Str(a, "installationURL") ?? Str(a, "url"));
        return (null, null);
    }

    /// <summary>result.apps[] of <c>devicectl device info apps</c>.</summary>
    public static IReadOnlyList<InstalledApp> ParseApps(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<InstalledApp>();
        if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
            foreach (var a in apps.EnumerateArray())
                if ((Str(a, "bundleIdentifier") ?? Str(a, "bundleID")) is { } id)
                    list.Add(new InstalledApp(id, Str(a, "name"), Str(a, "version"), Str(a, "bundleVersion")));
        return list;
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetRawText(), _ => null };
    }

    private static bool? Bool(JsonElement e, params string[] path)
    {
        foreach (var p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        return e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static void CollectStrings(JsonElement e, List<string> into, int depth)
    {
        if (depth > 12 || into.Count > 60) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.String: if (e.GetString() is { Length: > 0 } s) into.Add(s); break;
            case JsonValueKind.Number: into.Add(e.GetRawText()); break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name is "code" or "domain") into.Add($"{p.Name}: {(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}");
                    else CollectStrings(p.Value, into, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray()) CollectStrings(i, into, depth + 1);
                break;
        }
    }
}
