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
        => await Run(exe, args, log, ct, timeoutSeconds, null).ConfigureAwait(false);

    private async Task<(ProcessResult Result, string Out, string Err)> Run(string exe, IReadOnlyList<string> args, List<string> log, CancellationToken ct, int timeoutSeconds, ProgressLog? progress)
    {
        void Add(string line) { if (progress is not null) progress.Add(line); else log.Add(line); }
        Add($"$ {exe} {string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a))}   [{DateTimeOffset.UtcNow:HH:mm:ss}]");
        var r = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = [.. args], Timeout = TimeSpan.FromSeconds(timeoutSeconds) }, ct).ConfigureAwait(false);
        Add($"  exit {r.Result.ExitCode} after {r.Result.Duration.TotalSeconds:F1} s{(r.Result.TimedOut ? " (timed out)" : r.Result.Cancelled ? " (cancelled)" : "")}" +
            (r.Result.Succeeded ? "" : ": " + (r.StdErr + r.StdOut).Trim().Split('\n').LastOrDefault()));
        return r;
    }

    // ───────────── iOS Simulator ─────────────

    /// <summary>Budgets of the Simulator stages (each bounded on its own; a stage that exceeds its budget fails with that reason).</summary>
    public sealed record SimulatorBudgets
    {
        public TimeSpan Boot { get; init; } = TimeSpan.FromMinutes(10);
        public TimeSpan Install { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan Launch { get; init; } = TimeSpan.FromMinutes(2);
        /// <summary>Listed by launchd, the settle time, and the screenshots of the app and of the screen without it.</summary>
        public TimeSpan Ready { get; init; } = TimeSpan.FromMinutes(4);
        /// <summary>How long the app must stay up, with the same process, after launchd first listed it.</summary>
        public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Boot → install → launch → ready, each a timestamped stage with its own budget and exit codes logged.
    /// <para><b>Ready</b> means more than an accepted launch command: the process simctl launched (pid) is listed by the
    /// Simulator's launchd as the app's UIKit process, the same process is still alive <see cref="SimulatorBudgets.Settle"/>
    /// later, and the app's screen (below the status bar) differs from the screen left when it is terminated — it drew its
    /// own UI.</para>
    /// </summary>
    /// <param name="simulatorUdid">
    /// The Simulator to use, chosen explicitly (e.g. a device created for one CI job). Null = the first available iPhone,
    /// preferring one already booted.
    /// </param>
    /// <param name="progress">Each log line as it happens (stage starts/ends, commands, exit codes).</param>
    public async Task<LaunchResult> LaunchIosSimulatorAsync(string appBundle, string bundleId, string screenshotPath, CancellationToken ct,
        string? simulatorUdid = null, Action<string>? progress = null, SimulatorBudgets? budgets = null)
    {
        budgets ??= new SimulatorBudgets();
        var log = new ProgressLog(progress);
        const string xcrun = "/usr/bin/xcrun";
        LaunchResult Fail(string why) { log.Add("RESULT failed: " + why); return new(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.Failed, why, null, log.Lines); }
        if (!OperatingSystem.IsMacOS() || !File.Exists(xcrun))
            return new(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.NotPossibleHere, "The iOS Simulator runs only on a Mac with Xcode.", null, log.Lines);
        if (!Directory.Exists(appBundle)) return Fail($"The Simulator app bundle was not found: {appBundle}");

        Task<(ProcessResult Result, string Out, string Err)> Sim(IReadOnlyList<string> args, CancellationToken token, TimeSpan timeout) =>
            Run(xcrun, ["simctl", .. args], log.Lines, token, (int)Math.Ceiling(timeout.TotalSeconds), log);

        var list = await Sim(["list", "devices", "available", "--json"], ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (!list.Result.Succeeded) return Fail("simctl could not list simulators. Install an iOS Simulator runtime in Xcode › Settings › Components.");
        var device = simulatorUdid is null ? PickSimulator(list.Out) : FindSimulator(list.Out, simulatorUdid);
        if (device is null)
            return Fail(simulatorUdid is null ? "No available iPhone simulator. Create one in Xcode › Window › Devices and Simulators."
                : $"The selected Simulator {simulatorUdid} is not available (simctl list devices available).");
        var udid = device.Value.Udid;
        log.Add($"Simulator: {device.Value.Name} ({device.Value.Runtime}, {udid}, {device.Value.State}){(simulatorUdid is null ? "" : " — selected explicitly")}");

        // ── boot ──
        if (device.Value.State != "Booted")
        {
            var boot = await Stage("boot", budgets.Boot, async token =>
            {
                var b = await Sim(["boot", udid], token, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                // "Unable to boot device in current state: Booted" is not a failure; bootstatus decides.
                var s = await Sim(["bootstatus", udid, "-b"], token, budgets.Boot).ConfigureAwait(false);
                return s.Result.Succeeded ? null : $"the Simulator did not finish booting (simctl bootstatus exit {s.Result.ExitCode}{(s.Result.TimedOut ? ", timed out" : "")}; boot exit {b.Result.ExitCode})";
            }).ConfigureAwait(false);
            if (boot is not null) return Fail(boot);
        }

        // ── install ──
        var installError = await Stage("install", budgets.Install, async token =>
        {
            var i = await Sim(["install", udid, appBundle], token, budgets.Install).ConfigureAwait(false);
            return i.Result.Succeeded ? null : "simctl install failed: " + FirstLine(i.Err + i.Out);
        }).ConfigureAwait(false);
        if (installError is not null) return Fail(installError);

        // ── launch ──
        string launchOut = "";
        var launchError = await Stage("launch", budgets.Launch, async token =>
        {
            var l = await Sim(["launch", "--terminate-running-process", udid, bundleId], token, budgets.Launch).ConfigureAwait(false);
            launchOut = l.Out.Trim();
            return l.Result.Succeeded ? null : "simctl launch failed: " + FirstLine(l.Err + l.Out);
        }).ConfigureAwait(false);
        if (launchError is not null) return Fail(launchError);
        log.Add(launchOut);
        var pid = ParseLaunchPid(launchOut, bundleId);
        if (pid is null) return Fail($"simctl launch did not report a process id for {bundleId} (\"{launchOut}\").");

        // ── ready ──
        // 1. launchd lists the process simctl launched as the app's UIKit process (not a relaunch, not exited);
        // 2. it is still the same process after the settle time;
        // 3. the app's screen differs from the screen it leaves behind when it is terminated: the app drew its own UI.
        // Both screenshots are taken once the app is up (a screenshot right after install stalled on a fresh Simulator).
        double changed = 0;
        string? screenshot = null;
        var dir = Path.GetDirectoryName(screenshotPath)!;
        var appBmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(screenshotPath) + "-app.bmp");
        var homeBmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(screenshotPath) + "-home.bmp");
        Directory.CreateDirectory(dir);
        async Task<bool> Screenshot(string path, string type, CancellationToken token, int attempts)
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var r = await Sim(["io", udid, "screenshot", "--type=" + type, path], token, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                if (r.Result.Succeeded && File.Exists(path)) return true;
                if (attempt < attempts) log.Add($"  screenshot attempt {attempt} of {attempts} did not complete; trying once more");
            }
            return false;
        }
        var readyError = await Stage("ready", budgets.Ready, async token =>
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var services = await Sim(["spawn", udid, "launchctl", "list"], token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (!services.Result.Succeeded) { await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); continue; }
                var current = AppPid(services.Out, bundleId);
                if (current is not null && current != pid) return $"the app was relaunched (pid {pid} → {current}): it crashed and was started again";
                if (current is null && ExitedWithStatus(services.Out, bundleId) is { } status) return $"the app exited (launchd status {status}) before it was ready";
                if (current == pid) { log.Add($"  ready check: pid {pid} listed by launchd as UIKitApplication:{bundleId}"); break; }
                log.Add($"  ready check: UIKitApplication:{bundleId} with pid {pid} not listed yet");
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            await Task.Delay(budgets.Settle, token).ConfigureAwait(false);
            var again = await Sim(["spawn", udid, "launchctl", "list"], token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var still = AppPid(again.Out, bundleId);
            if (still != pid) return $"the app did not stay up: pid {pid} {(still is null ? "is gone" : $"was replaced by {still}")} {budgets.Settle.TotalSeconds:F0} s after it was listed";
            log.Add($"  ready check: still pid {pid} after {budgets.Settle.TotalSeconds:F0} s");
            if (await Screenshot(screenshotPath, "png", token, 1).ConfigureAwait(false)) screenshot = screenshotPath;
            if (!await Screenshot(appBmp, "bmp", token, 2).ConfigureAwait(false)) return "the app's screen could not be captured (simctl io screenshot)";
            await Sim(["terminate", udid, bundleId], token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            if (!await Screenshot(homeBmp, "bmp", token, 2).ConfigureAwait(false)) return "the screen after terminating the app could not be captured (simctl io screenshot)";
            changed = ScreenDifference(appBmp, homeBmp);
            log.Add($"  ready check: {changed:P1} of the screen below the status bar differs between the running app and the screen it leaves behind");
            return changed >= 0.02 ? null : "the app's screen was the same as the screen without it: the app did not draw its own UI";
        }, timeoutReason: $"the app did not reach a verified ready state within {budgets.Ready.TotalSeconds:F0} s").ConfigureAwait(false);

        // Leave the Simulator as found: the app this check started is stopped (the Simulator itself belongs to the caller).
        await Sim(["terminate", udid, bundleId], ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (readyError is not null) return Fail(readyError) with { ScreenshotPath = screenshot };
        log.Add("RESULT passed");
        return new(VerificationLevel.LaunchedInSimulatorOrEmulator, LevelStatus.Passed,
            $"Installed, launched and ready in {device.Value.Name} ({device.Value.Runtime}): process {pid} listed by launchd as the app, the same process " +
            $"{budgets.Settle.TotalSeconds:F0} s later, and its own screen drawn ({changed:P0} of the screen differs from the screen without the app).", screenshot, log.Lines);

        async Task<string?> Stage(string name, TimeSpan budget, Func<CancellationToken, Task<string?>> work, string? timeoutReason = null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.Add($"STAGE {name} start {DateTimeOffset.UtcNow:O} (budget {budget.TotalSeconds:F0} s)");
            string? error;
            try { error = await work(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = timeoutReason ?? $"the {name} stage exceeded its {budget.TotalSeconds:F0} s budget";
            }
            log.Add($"STAGE {name} end {DateTimeOffset.UtcNow:O} {(error is null ? "ok" : "failed")} after {sw.Elapsed.TotalSeconds:F1} s{(error is null ? "" : ": " + error)}");
            return error;
        }
    }

    /// <summary>The log of one launch: kept for the report and streamed to the caller as it happens.</summary>
    private sealed class ProgressLog(Action<string>? progress)
    {
        public List<string> Lines { get; } = [];
        public void Add(string line)
        {
            lock (Lines) Lines.Add(line);
            progress?.Invoke(line);
        }
    }

    /// <summary>"com.example.app: 1234" → 1234.</summary>
    internal static int? ParseLaunchPid(string output, string bundleId)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(bundleId + ":", StringComparison.Ordinal) && int.TryParse(t[(bundleId.Length + 1)..].Trim(), out var pid)) return pid;
        }
        return null;
    }

    /// <summary>The pid launchctl lists for UIKitApplication:&lt;bundle id&gt;[…] ("PID Status Label"; "-" when not running).</summary>
    internal static int? AppPid(string launchctlList, string bundleId)
    {
        foreach (var line in launchctlList.Split('\n'))
        {
            var parts = line.Split('\t', ' ').Where(p => p.Length > 0).ToArray();
            if (parts.Length >= 3 && IsAppLabel(parts[^1], bundleId) && int.TryParse(parts[0], out var pid)) return pid;
        }
        return null;
    }

    /// <summary>The last exit status launchctl lists for the app's label when it is no longer running.</summary>
    internal static string? ExitedWithStatus(string launchctlList, string bundleId)
    {
        foreach (var line in launchctlList.Split('\n'))
        {
            var parts = line.Split('\t', ' ').Where(p => p.Length > 0).ToArray();
            if (parts.Length >= 3 && IsAppLabel(parts[^1], bundleId) && parts[0] == "-" && parts[1] != "0") return parts[1];
        }
        return null;
    }

    private static bool IsAppLabel(string label, string bundleId) =>
        label.StartsWith("UIKitApplication:" + bundleId, StringComparison.Ordinal)
        && (label.Length == "UIKitApplication:".Length + bundleId.Length || label["UIKitApplication:".Length + bundleId.Length] == '[');

    /// <summary>
    /// Fraction of sampled pixels that differ between two Simulator screenshots (BMP), ignoring the status bar (top 12 %,
    /// its clock changes) and the home indicator (bottom 5 %). 0 when either image cannot be read or the sizes differ.
    /// </summary>
    internal static double ScreenDifference(string beforeBmp, string afterBmp)
    {
        if (ReadBmp(beforeBmp) is not { } a || ReadBmp(afterBmp) is not { } b || a.Width != b.Width || a.Height != b.Height) return 0;
        int total = 0, differ = 0;
        for (var y = (int)(a.Height * 0.12); y < (int)(a.Height * 0.95); y += 4)
            for (var x = 0; x < a.Width; x += 4)
            {
                total++;
                var (r1, g1, b1) = a.Pixel(x, y);
                var (r2, g2, b2) = b.Pixel(x, y);
                if (Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2) > 48) differ++;
            }
        return total == 0 ? 0 : (double)differ / total;
    }

    internal sealed record Bmp(int Width, int Height, int BytesPerPixel, int Stride, bool TopDown, int Offset, byte[] Data)
    {
        public (int R, int G, int B) Pixel(int x, int y)
        {
            var row = TopDown ? y : Height - 1 - y;
            var i = Offset + row * Stride + x * BytesPerPixel;
            return (Data[i + 2], Data[i + 1], Data[i]);
        }
    }

    /// <summary>An uncompressed 24- or 32-bit BMP (what simctl io screenshot --type=bmp writes); null otherwise.</summary>
    internal static Bmp? ReadBmp(string path)
    {
        try
        {
            var d = File.ReadAllBytes(path);
            if (d.Length < 54 || d[0] != 'B' || d[1] != 'M') return null;
            var offset = BitConverter.ToInt32(d, 10);
            var width = BitConverter.ToInt32(d, 18);
            var height = BitConverter.ToInt32(d, 22);
            var bpp = BitConverter.ToInt16(d, 28);
            var compression = BitConverter.ToInt32(d, 30);
            if (bpp is not (24 or 32) || compression is not (0 or 3) || width <= 0 || height == 0) return null;
            var bytes = bpp / 8;
            var stride = (width * bytes + 3) & ~3;
            var h = Math.Abs(height);
            if (offset + (long)stride * h > d.Length) return null;
            return new Bmp(width, h, bytes, stride, height < 0, offset, d);
        }
        catch (IOException) { return null; }
    }

    /// <summary>The device with this UDID from simctl list devices available --json (any runtime, any state).</summary>
    internal static (string Udid, string Name, string Runtime, string State)? FindSimulator(string json, string udid)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var runtime in doc.RootElement.GetProperty("devices").EnumerateObject())
            foreach (var d in runtime.Value.EnumerateArray())
            {
                if (!string.Equals(d.GetProperty("udid").GetString(), udid, StringComparison.OrdinalIgnoreCase)) continue;
                if (d.TryGetProperty("isAvailable", out var avail) && avail.ValueKind == JsonValueKind.False) return null;
                var key = runtime.Name;
                var marker = key.LastIndexOf(".iOS-", StringComparison.Ordinal);
                var version = marker < 0 ? key : "iOS " + key[(marker + 5)..].Replace('-', '.');
                return (d.GetProperty("udid").GetString()!, d.GetProperty("name").GetString() ?? "", version, d.GetProperty("state").GetString() ?? "");
            }
        return null;
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

    /// <summary>
    /// Installs and launches an app (not a console program): checks that it is still running after it settles, that it
    /// did not crash, takes a screenshot, and reads the text the screen shows through the accessibility tree
    /// (uiautomator), which includes WebView content. <paramref name="expectText"/>, when given, must be among it.
    /// </summary>
    public async Task<IReadOnlyList<LaunchResult>> LaunchAndroidAppAsync(string adb, string apk, string applicationId, string activity, string screenshotPath, string? expectText, CancellationToken ct)
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
            var qemu = (await Run(adb, ["-s", serial, "shell", "getprop", "ro.kernel.qemu"], log, ct).ConfigureAwait(false)).Out.Trim();
            var qemuBoot = (await Run(adb, ["-s", serial, "shell", "getprop", "ro.boot.qemu"], log, ct).ConfigureAwait(false)).Out.Trim();
            var emulator = serial.StartsWith("emulator-", StringComparison.Ordinal) || qemu == "1" || qemuBoot == "1";
            var model = (await Run(adb, ["-s", serial, "shell", "getprop", "ro.product.model"], log, ct).ConfigureAwait(false)).Out.Trim();
            var release = (await Run(adb, ["-s", serial, "shell", "getprop", "ro.build.version.release"], log, ct).ConfigureAwait(false)).Out.Trim();
            var level = emulator ? VerificationLevel.LaunchedInSimulatorOrEmulator : VerificationLevel.TestedOnDevice;
            var where = $"{(emulator ? "emulator" : "device")} {model} (Android {release}, {serial})";
            var install = await Run(adb, ["-s", serial, "install", "-r", "-t", apk], log, ct, 300).ConfigureAwait(false);
            if (!install.Result.Succeeded || !install.Out.Contains("Success", StringComparison.Ordinal))
            {
                results.Add(new(level, LevelStatus.Failed, $"adb install failed on {where}: {FirstLine(install.Err + install.Out)}", null, log));
                continue;
            }
            await Run(adb, ["-s", serial, "logcat", "-c"], log, ct).ConfigureAwait(false);
            var start = await Run(adb, ["-s", serial, "shell", "am", "start", "-W", "-n", $"{applicationId}/{activity}"], log, ct).ConfigureAwait(false);
            if (!start.Result.Succeeded || start.Out.Contains("Error", StringComparison.Ordinal))
            {
                results.Add(new(level, LevelStatus.Failed, $"The app did not start on {where}: {FirstLine(start.Out + start.Err)}", null, log));
                continue;
            }
            // Let the WebView load the packaged page, then read what the screen shows (retrying while it renders).
            var texts = new List<string>();
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                await Run(adb, ["-s", serial, "shell", "uiautomator", "dump", "/sdcard/hcg-window.xml"], log, ct, 60).ConfigureAwait(false);
                var xml = (await Run(adb, ["-s", serial, "shell", "cat", "/sdcard/hcg-window.xml"], log, ct).ConfigureAwait(false)).Out;
                texts = System.Text.RegularExpressions.Regex.Matches(xml, @"\b(?:text|content-desc)=""([^""]+)""").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim())
                    .Where(t => t.Length > 0).Distinct().ToList();
                if (expectText is null ? texts.Count > 0 : texts.Any(t => t.Contains(expectText, StringComparison.Ordinal))) break;
            }
            await Run(adb, ["-s", serial, "shell", "rm", "-f", "/sdcard/hcg-window.xml"], log, ct).ConfigureAwait(false);
            log.Add("Visible text (uiautomator): " + string.Join(" | ", texts.Take(25)));
            var pid = (await Run(adb, ["-s", serial, "shell", "pidof", applicationId], log, ct).ConfigureAwait(false)).Out.Trim();
            var crash = (await Run(adb, ["-s", serial, "logcat", "-d", "-b", "crash"], log, ct).ConfigureAwait(false)).Out;
            var console = (await Run(adb, ["-s", serial, "logcat", "-d", "-s", "Capacitor:*", "Capacitor/Console:*", "chromium:*"], log, ct).ConfigureAwait(false)).Out;
            log.Add("App log (Capacitor/WebView):");
            log.AddRange(console.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(40).Select(l => "  " + l.TrimEnd()));
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            var deviceShot = "/sdcard/hcg-app-screenshot.png";
            await Run(adb, ["-s", serial, "shell", "screencap", "-p", deviceShot], log, ct).ConfigureAwait(false);
            var shotPath = serials.Count > 1 ? screenshotPath.Replace(".png", "-" + serial + ".png", StringComparison.Ordinal) : screenshotPath;
            var pulled = await Run(adb, ["-s", serial, "pull", deviceShot, shotPath], log, ct).ConfigureAwait(false);
            await Run(adb, ["-s", serial, "shell", "rm", deviceShot], log, ct).ConfigureAwait(false);
            var crashed = crash.Contains(applicationId, StringComparison.Ordinal);
            var shows = expectText is null ? texts.Count > 0 : texts.Any(t => t.Contains(expectText, StringComparison.Ordinal));
            var ok = !crashed && pid.Length > 0 && shows;
            var detail = ok
                ? $"Installed and launched on {where}; the app is still running and shows {(expectText is null ? $"{texts.Count} text element(s)" : $"\"{expectText}\"")} ({string.Join(" | ", texts.Take(6))})."
                : crashed ? $"The app crashed on {where}. See logcat -b crash."
                : pid.Length == 0 ? $"Launched on {where}, but the app is no longer running."
                : $"Launched on {where} and still running, but the screen does not show {(expectText is null ? "any text" : $"\"{expectText}\"")} (visible: {string.Join(" | ", texts.Take(8))}).";
            if (!emulator) detail += " Device smoke test only — functional testing on the device remains manual.";
            results.Add(new(level, ok ? LevelStatus.Passed : LevelStatus.Failed, detail, pulled.Result.Succeeded && File.Exists(shotPath) ? shotPath : null, log));
        }
        return results;
    }

    private static string FirstLine(string s) => s.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
}
