using HCG.Impactor.Devices;

namespace HCG.Impactor.Ecosystem;

/// <summary>
/// How HCG Impactor connects to other HCG products. Only contracts that exist are used; nothing here invents
/// endpoints, credentials or URL schemes.
/// </summary>
public static class HcgLinks
{
    /// <summary>
    /// HCG System Tester's navigation route (SystemTesterRoute in HCG-System-Tester/Sources/HCGCore/Ecosystem). It takes
    /// exactly one segment and no query, so it opens Connected Systems; the device itself is handed over as an
    /// <c>hcg.apple-device/1</c> record (clipboard / file) until System Tester adds a device-specific route.
    /// </summary>
    public static readonly Uri SystemTesterConnectedSystems = new("hcgsystemtester://open/connected-systems");

    public static readonly Uri SystemTesterDiagnostics = new("hcgsystemtester://open/diagnostics");

    /// <summary>The device record System Tester (and anything else reading hcg.apple-device/1) can import.</summary>
    public static string DeviceHandoff(AppleDevice device, DateTimeOffset now, bool includeUdid = false) =>
        AppleDeviceContract.Serialize([device], "HCG Executable Maker › HCG Impactor", now, includeUdid);

    /// <summary>The ecosystem integrations and their real status (shown on the Impactor Dashboard and in docs).</summary>
    public static IReadOnlyList<(string Product, string Integration, string Status)> Integrations { get; } =
    [
        ("HCG System Tester", "Shared device record hcg.apple-device/1; \"Open in System Tester\" opens hcgsystemtester://open/connected-systems and hands the device record over.", "Available (navigation); device-specific route proposed"),
        ("HCG Sentinel", "Impactor's diagnostics export is hcg.diagnostic-report/1, the format Sentinel and Device Link read; signing and integrity findings included.", "Available (file export)"),
        ("HCG Terminal", "Deployment diagnostics export (Markdown + hcg.diagnostic-report/1) for Terminal's workflows; Terminal's deep links are navigation-only.", "Export available; no command hand-off"),
        ("HCG Connect", "HCG Impactor runs inside Executable Maker under its HCG Connect sign-in and licence; no separate credentials.", "Uses the existing sign-in"),
        ("HCG Store", "Ship to Store prepares releases; Impactor inspects a downloaded IPA. Authorized IPA retrieval needs the Store's download API, which is not deployed.", "Inspect after download; retrieval pending Store API"),
        ("HCG Server", "Future authenticated macOS build host for signing from Windows/Linux. Not implemented.", "Planned"),
        ("HCG Repo", "Jailbreak .deb packages are a separate workflow (Mobile Build Studio › Unleashify, HCG Repo). Impactor installs standard signed IPAs only.", "Kept separate by design"),
    ];
}
