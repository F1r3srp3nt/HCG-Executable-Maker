using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.Logging;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

public sealed partial class ToolchainDetector
{
    /// <summary>Overridable for tests: folders searched for Xcode*.app (depth 1).</summary>
    public IReadOnlyList<string>? XcodeSearchFolders { get; init; }

    /// <summary>Overridable for tests: whether Spotlight (mdfind) is asked for further Xcode apps.</summary>
    public bool UseSpotlight { get; init; } = true;

    /// <summary>
    /// The developer directory HCG's child processes use: DEVELOPER_DIR when set (HCG's own selection or the launch
    /// environment), otherwise <c>xcode-select -p</c>. Never changes anything.
    /// </summary>
    public async Task<string?> ActiveDeveloperDirectoryAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(DeveloperDirectorySelection.Variable) is { Length: > 0 } env) return env.TrimEnd('/');
        const string exe = "/usr/bin/xcode-select";
        if (!File.Exists(exe)) return null;
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = ["-p"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var dir = stdout.Trim();
            return r.Succeeded && dir.Length > 0 ? dir.TrimEnd('/') : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "xcode-select -p failed");
            return null;
        }
    }

    /// <summary>Finds installed Xcode apps, probes the active developer directory and diagnoses the result.</summary>
    public async Task<XcodeStatus> DetectXcodeAsync(CancellationToken ct)
    {
        var active = await ActiveDeveloperDirectoryAsync(ct).ConfigureAwait(false);
        var extra = new List<string>();
        if (UseSpotlight) extra.AddRange(await SpotlightXcodesAsync(ct).ConfigureAwait(false));
        if (active is not null && active.EndsWith(".app/Contents/Developer", StringComparison.Ordinal)) extra.Add(active);
        var installed = XcodeDiscovery.FindInstallations(XcodeSearchFolders ?? XcodeDiscovery.DefaultSearchFolders().ToList(), extra, active);

        int versionExit = -1;
        string versionOut = "", versionErr = "";
        int? firstLaunch = null;
        var sdks = new List<string>();
        const string xcodebuild = "/usr/bin/xcodebuild";
        if (File.Exists(xcodebuild) && active is not null)
        {
            try
            {
                var (r, o, e) = await runner.CaptureAsync(new ProcessSpec { Executable = xcodebuild, Arguments = ["-version"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
                (versionExit, versionOut, versionErr) = (r.ExitCode, o, e);
                if (r.Succeeded)
                {
                    var (fl, _, _) = await runner.CaptureAsync(new ProcessSpec { Executable = xcodebuild, Arguments = ["-checkFirstLaunchStatus"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
                    firstLaunch = fl.ExitCode;
                    var (sr, so, _) = await runner.CaptureAsync(new ProcessSpec { Executable = xcodebuild, Arguments = ["-showsdks", "-json"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
                    if (sr.Succeeded) sdks.AddRange(ParseXcodeSdks(so).Select(s => $"{s.Platform} {s.Version}").Distinct());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "xcodebuild probe failed");
                versionErr = ex.Message;
            }
        }
        var source = active is null ? DeveloperDirectorySource.None : DeveloperDirectorySelection.ProcessSource;
        return XcodeDiscovery.Diagnose(new XcodeDiscovery.ProbeResults(active, source, installed, versionExit, versionOut, versionErr, firstLaunch, sdks));
    }

    private async Task<IReadOnlyList<string>> SpotlightXcodesAsync(CancellationToken ct)
    {
        const string mdfind = "/usr/bin/mdfind";
        if (!File.Exists(mdfind)) return [];
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec
            {
                Executable = mdfind, Arguments = ["kMDItemCFBundleIdentifier == \"com.apple.dt.Xcode\""],
                Timeout = TimeSpan.FromSeconds(10), Environment = ProbeEnvironment(),
            }, ct).ConfigureAwait(false);
            return r.Succeeded
                ? stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => p.EndsWith(".app", StringComparison.Ordinal) && !p.Contains("/.Trash/", StringComparison.Ordinal)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "mdfind failed");
            return [];
        }
    }

    /// <summary>The Xcode row of the toolchain list, derived from the diagnosis (the exact action goes in the hint).</summary>
    public static ToolchainInfo XcodeToolchainInfo(XcodeStatus x) => new()
    {
        Id = ToolchainIds.Xcode, DisplayName = "Xcode", Category = "macOS",
        Status = x.State switch
        {
            XcodeState.Ready => ToolchainStatus.Installed,
            XcodeState.NotInstalled or XcodeState.InstalledNotSelected => ToolchainStatus.Missing,
            XcodeState.NotApplicable => ToolchainStatus.UnsupportedOnHost,
            _ => ToolchainStatus.ValidationFailed,
        },
        Version = x.VerifiedVersion,
        ExecutablePath = x.ActiveDeveloperDirectory,
        Detail = x.Diagnosis + (x.RawError is null ? "" : $" Tool output: {x.RawError}"),
        InstallHint = x.CorrectiveAction.Length > 0 ? x.CorrectiveAction : null,
        AdditionalVersions = x.Installed.Select(i => i.Display).ToList(),
    };
}
