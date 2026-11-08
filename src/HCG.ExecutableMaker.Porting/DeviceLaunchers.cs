using System.Text.Json;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Porting;

public sealed record LaunchResult(VerificationLevel Level, LevelStatus Status, string Detail, string? ScreenshotPath, IReadOnlyList<string> Log);

/// <summary>
/// Installs and launches a compiled port in an iOS Simulator (simctl) or on an Android emulator/device (adb), checks that
/// it is still running, and takes a screenshot. Launching executes the project's code: callers require trust first.
/// Nothing is uploaded; nothing is installed besides the app itself.
/// </summary>
public sealed class DeviceLauncher(IProcessRunner runner)
{
    private async Task<(ProcessResult Result, string Out, string Err)> Run(string exe, IReadOnlyList<string> args, List<string> log, CancellationToken ct, int timeoutSeconds = 120)
    {
        log.Add("$ " + exe + " " + string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
        var r = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = [.. args], Timeout = TimeSpan.FromSeconds(timeoutSeconds) }, ct).ConfigureAwait(false);
        if (!r.Result.Succeeded) log.Add($"  exit {r.Result.ExitCode}: {(r.StdErr + r.StdOut).Trim().Split('\n').LastOrDefault()}");
        return r;
    }

    // ───────────── iOS Simulator ─────────────

    public async Task<LaunchResult> LaunchIosSimulatorAsync(string appBundle, string bundleId, string screenshotPath, CancellationToken ct)
    {
        var log = new List<string>();
        const string xcrun = "/usr/bin/xcrun";
        LaunchResult Fail(string why) => new(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.Failed, why, null, log);
        if (!OperatingSystem.IsMacOS() || !File.Exists(xcrun))
            return new(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.NotPossibleHere, "The iOS Simulator runs only on a Mac with Xcode.", null, log);
        if (!Directory.Exists(appBundle)) return Fail($"The Simulator app bundle was not found: {appBundle}");

        var list = await Run(xcrun, ["simctl", "list", "devices", "available", "--json"], log, ct).ConfigureAwait(false);
        if (!list.Result.Succeeded) return Fail("simctl could not list simulators. Install an iOS Simulator runtime in Xcode › Settings › Components.");
        var device = PickSimulator(list.Out);
        if (device is null) return Fail("No available iPhone simulator. Create one in Xcode › Window › Devices and Simulators.");
        log.Add($"Simulator: {device.Value.Name} ({device.Value.Runtime}, {device.Value.Udid}, {device.Value.State})");
        if (device.Value.State != "Booted")
        {
            await Run(xcrun, ["simctl", "boot", device.Value.Udid], log, ct).ConfigureAwait(false);
            var booted = await Run(xcrun, ["simctl", "bootstatus", device.Value.Udid, "-b"], log, ct, 600).ConfigureAwait(false);
            if (!booted.Result.Succeeded) return Fail("The simulator did not finish booting.");
        }
        var install = await Run(xcrun, ["simctl", "install", device.Value.Udid, appBundle], log, ct, 300).ConfigureAwait(false);
        if (!install.Result.Succeeded) return Fail("simctl install failed: " + FirstLine(install.Err + install.Out));
        var launch = await Run(xcrun, ["simctl", "launch", "--terminate-running-process", device.Value.Udid, bundleId], log, ct).ConfigureAwait(false);
        if (!launch.Result.Succeeded) return Fail("simctl launch failed: " + FirstLine(launch.Err + launch.Out));
        log.Add(launch.Out.Trim());
        await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        var services = await Run(xcrun, ["simctl", "spawn", device.Value.Udid, "launchctl", "list"], log, ct).ConfigureAwait(false);
        var running = services.Out.Split('\n').Any(l => l.Contains("UIKitApplication:" + bundleId, StringComparison.Ordinal));
        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
        var shot = await Run(xcrun, ["simctl", "io", device.Value.Udid, "screenshot", screenshotPath], log, ct).ConfigureAwait(false);
        var screenshot = shot.Result.Succeeded && File.Exists(screenshotPath) ? screenshotPath : null;
        return running
            ? new(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.Passed,
                $"Installed and launched in {device.Value.Name} ({device.Value.Runtime}); still running 8 s after launch ({launch.Out.Trim()}).", screenshot, log)
            : new(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.Failed,
                $"Launched in {device.Value.Name}, but the app was not running 8 s later (crashed or exited). Check the simulator's crash logs.", screenshot, log);
    }

    internal static (string Udid, string Name, string Runtime, string State)? PickSimulator(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var all = new List<(string Udid, string Name, string Runtime, string State, Version Version)>();
        foreach (var runtime in doc.RootElement.GetProperty("devices").EnumerateObject())
        {
            var key = runtime.Name; // com.apple.CoreSimulator.SimRuntime.iOS-18-2
            var marker = key.LastIndexOf(".iOS-", StringComparison.Ordinal);
            if (marker < 0) continue;
            var ver = Version.TryParse(key[(marker + 5)..].Replace('-', '.'), out var v) ? v : new Version(0, 0);
            foreach (var d in runtime.Value.EnumerateArray())
            {
                var name = d.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("iPhone", StringComparison.Ordinal)) continue;
                if (d.TryGetProperty("isAvailable", out var avail) && avail.ValueKind == JsonValueKind.False) continue;
                all.Add((d.GetProperty("udid").GetString()!, name, "iOS " + ver.ToString(), d.GetProperty("state").GetString() ?? "", ver));
            }
        }
        var pick = all.OrderByDescending(d => d.State == "Booted").ThenByDescending(d => d.Version).ThenBy(d => d.Name.Contains("Pro", StringComparison.Ordinal) ? 1 : 0).FirstOrDefault();
        return pick.Udid is null ? null : (pick.Udid, pick.Name, pick.Runtime, pick.State);
    }

    // ───────────── Android emulator / device ─────────────

    public async Task<IReadOnlyList<LaunchResult>> LaunchAndroidAsync(string adb, string apk, string applicationId, string activity, string screenshotPath, CancellationToken ct)
    {
        var log = new List<string>();
        var devices = await Run(adb, ["devices", "-l"], log, ct).ConfigureAwait(false);
        var serials = devices.Out.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Contains(" device ", StringComparison.Ordinal) || l.EndsWith(" device", StringComparison.Ordinal) || l.Contains("\tdevice", StringComparison.Ordinal))
            .Select(l => l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0]).ToList();
        if (serials.Count == 0)
            return [new(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.NotPerformed,
                "No emulator or device is connected (adb devices is empty). Start an emulator (Android Studio › Device Manager) or connect a device with USB debugging, then launch again.", null, log)];
        var results = new List<LaunchResult>();
        foreach (var serial in serials.Take(2))
        {
            var qemu = await Run(adb, ["-s", serial, "shell", "getprop", "ro.kernel.qemu"], log, ct).ConfigureAwait(false);
            var qemuBoot = await Run(adb, ["-s", serial, "shell", "getprop", "ro.boot.qemu"], log, ct).ConfigureAwait(false);
            var emulator = serial.StartsWith("emulator-", StringComparison.Ordinal) || qemu.Out.Trim() == "1" || qemuBoot.Out.Trim() == "1";
            var model = (await Run(adb, ["-s", serial, "shell", "getprop", "ro.product.model"], log, ct).ConfigureAwait(false)).Out.Trim();
            var release = (await Run(adb, ["-s", serial, "shell", "getprop", "ro.build.version.release"], log, ct).ConfigureAwait(false)).Out.Trim();
            var abi = (await Run(adb, ["-s", serial, "shell", "getprop", "ro.product.cpu.abi"], log, ct).ConfigureAwait(false)).Out.Trim();
            var level = emulator ? VerificationLevel.LaunchedInSimulatorOrEmulator : VerificationLevel.TestedOnDevice;
            var where = $"{(emulator ? "emulator" : "device")} {model} (Android {release}, {abi}, {serial})";
            log.Add("Target: " + where);
            var install = await Run(adb, ["-s", serial, "install", "-r", "-t", apk], log, ct, 300).ConfigureAwait(false);
            if (!install.Result.Succeeded || !install.Out.Contains("Success", StringComparison.Ordinal))
            {
                results.Add(new(level, LevelStatus.Failed, $"adb install failed on {where}: {FirstLine(install.Err + install.Out)}", null, log));
                continue;
            }
            await Run(adb, ["-s", serial, "logcat", "-c"], log, ct).ConfigureAwait(false);
            var start = await Run(adb, ["-s", serial, "shell", "am", "start", "-W", "-n", $"{applicationId}/{activity}", "--ez", "hcg.autorun", "true"], log, ct).ConfigureAwait(false);
            if (!start.Result.Succeeded || start.Out.Contains("Error", StringComparison.Ordinal))
            {
                results.Add(new(level, LevelStatus.Failed, $"The activity did not start on {where}: {FirstLine(start.Out + start.Err)}", null, log));
                continue;
            }
            // Wait for the program to finish (or keep running, e.g. waiting for input).
            string output = "";
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                output = (await Run(adb, ["-s", serial, "logcat", "-d", "-s", "HCGPort:I"], log, ct).ConfigureAwait(false)).Out;
                if (output.Contains("[exit code", StringComparison.Ordinal)) break;
            }
            var pid = (await Run(adb, ["-s", serial, "shell", "pidof", applicationId], log, ct).ConfigureAwait(false)).Out.Trim();
            var crash = (await Run(adb, ["-s", serial, "logcat", "-d", "-b", "crash"], log, ct).ConfigureAwait(false)).Out;
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            var deviceShot = "/sdcard/hcg-port-screenshot.png";
            await Run(adb, ["-s", serial, "shell", "screencap", "-p", deviceShot], log, ct).ConfigureAwait(false);
            var shotPath = serials.Count > 1 ? screenshotPath.Replace(".png", "-" + serial + ".png", StringComparison.Ordinal) : screenshotPath;
            var pulled = await Run(adb, ["-s", serial, "pull", deviceShot, shotPath], log, ct).ConfigureAwait(false);
            await Run(adb, ["-s", serial, "shell", "rm", deviceShot], log, ct).ConfigureAwait(false);
            var programLines = output.Split('\n').Select(l => l.Contains("HCGPort", StringComparison.Ordinal) && l.Contains(':') ? l[(l.IndexOf(": ", l.IndexOf("HCGPort", StringComparison.Ordinal), StringComparison.Ordinal) + 2)..].TrimEnd() : "")
                .Where(l => l.Length > 0).ToList();
            log.Add("Program output (logcat HCGPort):");
            log.AddRange(programLines.Take(40).Select(l => "  " + l));
            var exitLine = programLines.LastOrDefault(l => l.StartsWith("[exit code", StringComparison.Ordinal));
            var crashed = crash.Contains(applicationId, StringComparison.Ordinal);
            var ok = !crashed && pid.Length > 0 && programLines.Count > 0 && (exitLine is null || exitLine == "[exit code 0]");
            var screenshot = pulled.Result.Succeeded && File.Exists(shotPath) ? shotPath : null;
            var detail = ok
                ? $"Installed and launched on {where}; the native program ran ({programLines.Count} output line(s){(exitLine is null ? ", still running" : ", " + exitLine.Trim('[', ']'))}) and the app is still running."
                : crashed ? $"The app crashed on {where}. See logcat -b crash."
                : $"Launched on {where}, but {(pid.Length == 0 ? "the app is no longer running" : programLines.Count == 0 ? "the program produced no output" : "the program ended with " + exitLine)}.";
            if (!emulator) detail += " Device smoke test only — functional testing on the device remains manual.";
            results.Add(new(level, ok ? LevelStatus.Passed : LevelStatus.Failed, detail, screenshot, log));
        }
        return results;
    }

    private static string FirstLine(string s) => s.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
}
