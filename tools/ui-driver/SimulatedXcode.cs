using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// Test-only: --simulate-xcode VERSION:DEVICE_MIN:SIMULATOR_MIN presents a Mac whose selected Xcode accepts deployment
/// targets from DEVICE_MIN (iPhoneOS) / SIMULATOR_MIN (Simulator), e.g. 27.0:15.0:15.0, so Mobile Build Studio's
/// compatibility workflow can be reproduced on any host. Every line it affects is logged as SIMULATED; nothing is built
/// with it (a real build needs a real Xcode).
/// </summary>
internal sealed class SimulatedXcodeToolchains(IToolchainService inner, string spec) : IToolchainService
{
    public ToolchainReport? Latest => inner.Latest is { } r ? Apply(r) : null;

    public event EventHandler<ToolchainReport>? Updated
    {
        add => inner.Updated += value is null ? null : (s, r) => value(s, Apply(r));
        remove { }
    }

    public async Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        Apply(await inner.GetAsync(refresh, cancellationToken));

    private ToolchainReport Apply(ToolchainReport r)
    {
        var parts = spec.Split(':');
        var (version, device, sim) = (parts[0], parts.ElementAtOrDefault(1) ?? "15.0", parts.ElementAtOrDefault(2) ?? parts.ElementAtOrDefault(1) ?? "15.0");
        var dev = $"/Applications/Xcode_{version}.app/Contents/Developer (SIMULATED)";
        return r with
        {
            Host = r.Host with { Platform = TargetPlatform.MacOS, OsDescription = "SIMULATED macOS (" + r.Host.OsDescription + ")" },
            Xcode = new XcodeStatus
            {
                State = XcodeState.Ready, ActiveDeveloperDirectory = dev, VerifiedVersion = version, Diagnosis = "Ready (SIMULATED for the UI driver)",
                Sdks = [$"iphoneos {version}", $"iphonesimulator {version}"],
                IosDeploymentRange = new IosDeploymentRange(device, version + ".99", null, version),
                IosSimulatorDeploymentRange = new IosDeploymentRange(sim, version + ".99", null, version),
                Installed = [new XcodeInstallation($"/Applications/Xcode_{version}.app", dev, version, null)
                    { IosRange = new IosDeploymentRange(device, version + ".99", null, version), SimulatorRange = new IosDeploymentRange(sim, version + ".99", null, version) }],
            },
        };
    }
}
