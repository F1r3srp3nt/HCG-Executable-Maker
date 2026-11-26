using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCG.Impactor.Devices;

public enum DeviceConnection
{
    /// <summary>Connected by cable (devicectl transportType "wired").</summary>
    Usb,
    /// <summary>Reachable over the local network through a paired-device tunnel (devicectl transportType "localNetwork").</summary>
    Network,
    /// <summary>Known to this Mac (paired earlier) but not reachable now.</summary>
    Unavailable,
}

public enum PairingState { Paired, Unpaired, Unknown }

public enum DeveloperModeState { Enabled, Disabled, Unknown }

/// <summary>
/// An iPhone, iPad or other Apple device as discovered on this computer. This is the shared HCG device record
/// (<see cref="AppleDeviceContract.Schema"/>, <c>hcg.apple-device/1</c>) that HCG Impactor and HCG System Tester's Apple
/// Device Intelligence both use, so a device is identified the same way in both apps: by its CoreDevice identifier on
/// this Mac and its hardware UDID. Fields that the source tool did not report are null, never guessed.
/// </summary>
public sealed record AppleDevice
{
    /// <summary>CoreDevice identifier (a UUID, stable for this device on this Mac). Used for devicectl commands.</summary>
    public required string Identifier { get; init; }
    /// <summary>The hardware UDID (what provisioning profiles list). Sensitive: redacted in exports.</summary>
    public string? Udid { get; init; }
    public required string Name { get; init; }
    /// <summary>e.g. "iPhone17,1".</summary>
    public string? ProductType { get; init; }
    /// <summary>e.g. "iPhone 16 Pro".</summary>
    public string? MarketingName { get; init; }
    /// <summary>"iPhone", "iPad", "appleWatch", "appleTV", "realityDevice" …</summary>
    public string? DeviceClass { get; init; }
    /// <summary>"iOS", "watchOS", "tvOS", "xrOS".</summary>
    public string? Platform { get; init; }
    public string? OsVersion { get; init; }
    public string? OsBuild { get; init; }
    /// <summary>"arm64e" on A12 and later, "arm64" before.</summary>
    public string? CpuArchitecture { get; init; }
    public DeviceConnection Connection { get; init; } = DeviceConnection.Unavailable;
    /// <summary>The raw transport name the tool reported ("wired", "localNetwork").</summary>
    public string? Transport { get; init; }
    public PairingState Pairing { get; init; } = PairingState.Unknown;
    public DeveloperModeState DeveloperMode { get; init; } = DeveloperModeState.Unknown;
    /// <summary>Developer disk image services are available (needed to launch and debug apps).</summary>
    public bool? DeveloperServicesAvailable { get; init; }
    /// <summary>CoreDevice tunnel state ("connected", "disconnected", "unavailable").</summary>
    public string? TunnelState { get; init; }
    public string? BootState { get; init; }
    /// <summary>True for a physical device (not a Simulator).</summary>
    public bool IsPhysical { get; init; } = true;
    /// <summary>Feature identifiers the device offers through CoreDevice (e.g. "com.apple.coredevice.feature.installapp").</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    /// <summary>Which tool produced this record ("devicectl 397.x", "synthetic").</summary>
    public string Source { get; init; } = "devicectl";

    public string DisplayModel => MarketingName ?? ProductType ?? DeviceClass ?? "Apple device";
    public bool CanInstall => Capabilities.Count == 0 ? Connection != DeviceConnection.Unavailable : Capabilities.Contains(AppleDeviceCapabilities.InstallApp);
    public bool CanLaunch => Capabilities.Contains(AppleDeviceCapabilities.LaunchApp);
}

/// <summary>CoreDevice feature identifiers HCG uses. Only values devicectl reports are meaningful; absence means "not offered now".</summary>
public static class AppleDeviceCapabilities
{
    public const string InstallApp = "com.apple.coredevice.feature.installapp";
    public const string LaunchApp = "com.apple.coredevice.feature.launchapplication";
    public const string ListApps = "com.apple.coredevice.feature.listapps";
    public const string UninstallApp = "com.apple.coredevice.feature.uninstallapp";
}

/// <summary>
/// The shared device-record contract, <c>hcg.apple-device/1</c>: a JSON array of <see cref="AppleDevice"/> objects with
/// camelCase names and enums as strings. Exported by HCG Impactor (Device Manager › Copy device record / Open in System
/// Tester) and proposed for System Tester's Apple Device Intelligence (docs/IMPACTOR.md). UDIDs are redacted unless the
/// user includes them explicitly.
/// </summary>
public static class AppleDeviceContract
{
    public const string Schema = "hcg.apple-device/1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public sealed record Document(string Schema, DateTimeOffset GeneratedAt, string Producer, IReadOnlyList<AppleDevice> Devices);

    public static string Serialize(IEnumerable<AppleDevice> devices, string producer, DateTimeOffset now, bool includeUdid = false) =>
        JsonSerializer.Serialize(new Document(Schema, now, producer, devices.Select(d => includeUdid ? d : d with { Udid = d.Udid is null ? null : Redact(d.Udid) }).ToList()), Json);

    public static Document? Deserialize(string json)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, Json);
        return doc?.Schema == Schema ? doc : null;
    }

    /// <summary>Keeps the first 4 and last 4 characters ("00008140-…ABCD") so the user can still tell devices apart.</summary>
    public static string Redact(string udid) => udid.Length <= 10 ? "…" : udid[..4] + "…" + udid[^4..];
}
