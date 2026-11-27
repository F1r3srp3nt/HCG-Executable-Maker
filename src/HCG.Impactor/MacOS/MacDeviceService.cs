using System.Diagnostics;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.Impactor.Devices;
using HCG.Impactor.Diagnostics;

namespace HCG.Impactor.MacOS;

/// <summary>
/// Physical iPhone/iPad discovery, installation and launch through Xcode's supported command-line interface to
/// CoreDevice, <c>xcrun devicectl</c> (Xcode 15+). Uses the Xcode HCG has selected (DEVELOPER_DIR in HCG's process
/// environment). Every call writes devicectl's JSON result to a private temporary file and reads it back; the
/// human-readable output is kept, redacted, for diagnostics. Nothing is uninstalled, ever.
/// </summary>
public sealed partial class MacDeviceService(IProcessRunner runner) : IDeviceService
{
    private const string Xcrun = "/usr/bin/xcrun";
    private bool? _available;
    private string? _version;

    public bool IsAvailable => OperatingSystem.IsMacOS() && File.Exists(Xcrun) && (_available ?? true);

    public async Task<DeviceListing> ListAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) return new([], InstallDiagnostics.Diagnose("", toolMissing: true), null, "");
        _version ??= await VersionAsync(cancellationToken).ConfigureAwait(false);
        if (_available == false) return new([], InstallDiagnostics.Diagnose("xcrun: error: unable to find devicectl", toolMissing: true), null, "");
        var run = await RunJsonAsync(["list", "devices"], TimeSpan.FromSeconds(60), null, cancellationToken).ConfigureAwait(false);
        if (!run.Ok || run.Json is null)
            return new([], InstallDiagnostics.Diagnose(run.Text, run.Cancelled, run.TimedOut), _version, InstallDiagnostics.Redact(run.Text));
        try
        {
            var devices = DevicectlJson.ParseDevices(run.Json, "devicectl " + (_version ?? "?"));
            return new(devices.OrderBy(d => d.Connection).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(), null, _version, InstallDiagnostics.Redact(run.Json));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new([], InstallDiagnostics.Diagnose("Could not read devicectl's device list: " + ex.Message), _version, InstallDiagnostics.Redact(run.Json));
        }
    }

    public async Task<InstalledApp?> FindInstalledAsync(AppleDevice device, string bundleIdentifier, CancellationToken cancellationToken)
    {
        if (!device.Capabilities.Contains(AppleDeviceCapabilities.ListApps) && device.Capabilities.Count > 0) return null;
        var run = await RunJsonAsync(["device", "info", "apps", "--device", device.Identifier, "--bundle-id", bundleIdentifier], TimeSpan.FromSeconds(60), null, cancellationToken).ConfigureAwait(false);
        if (!run.Ok || run.Json is null) return null;
        try { return DevicectlJson.ParseApps(run.Json).FirstOrDefault(a => a.BundleIdentifier == bundleIdentifier); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public async Task<InstallResult> InstallAsync(AppleDevice device, string appPath, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        progress?.Report(new InstallProgress(0, $"Installing on {device.Name} …"));
        var run = await RunJsonAsync(["device", "install", "app", "--device", device.Identifier, appPath], TimeSpan.FromMinutes(30), line =>
        {
            var m = PercentRegex().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct)) progress?.Report(new InstallProgress(Math.Clamp(pct, 0, 100), InstallDiagnostics.Redact(line.Trim())));
            else if (line.Trim().Length > 0) progress?.Report(new InstallProgress(null, InstallDiagnostics.Redact(line.Trim())));
        }, cancellationToken).ConfigureAwait(false);
        var raw = InstallDiagnostics.Redact(run.Text + "\n" + run.Json);
        if (run.Ok && run.Json is not null)
        {
            var (bundleId, location) = DevicectlJson.ParseInstall(run.Json);
            progress?.Report(new InstallProgress(100, "Installed."));
            return new InstallResult { Succeeded = true, InstalledBundleIdentifier = bundleId, InstallationLocation = location is null ? null : InstallDiagnostics.Redact(location), Duration = watch.Elapsed, RawRedacted = raw };
        }
        return new InstallResult { Succeeded = false, Duration = watch.Elapsed, Failure = InstallDiagnostics.Diagnose(run.Text + "\n" + run.Json, run.Cancelled, run.TimedOut), RawRedacted = raw };
    }

    public async Task<LaunchResult> LaunchAsync(AppleDevice device, string bundleIdentifier, CancellationToken cancellationToken)
    {
        var run = await RunJsonAsync(["device", "process", "launch", "--device", device.Identifier, bundleIdentifier], TimeSpan.FromMinutes(2), null, cancellationToken).ConfigureAwait(false);
        var raw = InstallDiagnostics.Redact(run.Text + "\n" + run.Json);
        return run.Ok ? new LaunchResult(true, null, raw) : new LaunchResult(false, InstallDiagnostics.Diagnose(run.Text + "\n" + run.Json, run.Cancelled, run.TimedOut), raw);
    }

    private async Task<string?> VersionAsync(CancellationToken ct)
    {
        var find = await runner.CaptureAsync(new ProcessSpec { Executable = Xcrun, Arguments = ["--find", "devicectl"], Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        _available = find.Result.Succeeded;
        if (!find.Result.Succeeded) return null;
        var v = await runner.CaptureAsync(new ProcessSpec { Executable = Xcrun, Arguments = ["devicectl", "--version"], Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        return v.Result.Succeeded ? v.StdOut.Trim().Split('\n').FirstOrDefault()?.Trim() : null;
    }

    private sealed record JsonRun(bool Ok, string? Json, string Text, bool Cancelled, bool TimedOut);

    private async Task<JsonRun> RunJsonAsync(IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hcg-impactor-devicectl-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, "result.json");
        try
        {
            var full = new List<string> { "devicectl" };
            full.AddRange(args);
            full.AddRange(["--json-output", jsonPath]);
            var text = new System.Text.StringBuilder();
            var gate = new object();
            var r = await runner.RunAsync(new ProcessSpec { Executable = Xcrun, Arguments = full, Timeout = timeout }, (_, line) =>
            {
                lock (gate) text.AppendLine(line);
                onLine?.Invoke(line);
            }, ct).ConfigureAwait(false);
            var json = File.Exists(jsonPath) ? await File.ReadAllTextAsync(jsonPath, CancellationToken.None).ConfigureAwait(false) : null;
            var outcome = json is null ? null : DevicectlJson.ReadOutcome(json);
            var ok = r.Succeeded && (outcome?.Succeeded ?? false);
            var all = $"$ xcrun devicectl {string.Join(' ', args)}\n{text}" + (outcome is { Succeeded: false } ? "\n" + outcome.ErrorText : "");
            return new JsonRun(ok, json, all, r.Cancelled, r.TimedOut);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [GeneratedRegex(@"(\d{1,3})\s?%")]
    private static partial Regex PercentRegex();
}
