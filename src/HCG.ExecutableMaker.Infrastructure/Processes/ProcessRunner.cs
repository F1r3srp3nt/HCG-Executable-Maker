using System.Diagnostics;
using HCG.ExecutableMaker.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.Infrastructure.Processes;

/// <summary>
/// Executes processes with structured arguments (<see cref="ProcessStartInfo.ArgumentList"/>), never via a shell.
/// Cancellation and timeouts kill the whole process tree.
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner>? logger = null) : IProcessRunner
{
    private readonly ILogger _logger = logger ?? NullLogger<ProcessRunner>.Instance;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(spec.Executable))
            throw new ArgumentException($"Executable must be an absolute path: '{spec.Executable}'.", nameof(spec));
        if (!File.Exists(spec.Executable))
            throw new FileNotFoundException("Executable not found.", spec.Executable);
        if (spec.WorkingDirectory is { } wd && !Directory.Exists(wd))
            throw new DirectoryNotFoundException($"Working directory not found: '{wd}'.");

        var psi = new ProcessStartInfo(spec.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);
        if (spec.Environment is { } env)
        {
            psi.Environment.Clear();
            foreach (var (k, v) in env) psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(OutputStream.StdOut, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(OutputStream.StdErr, e.Data); };

        using var timeoutCts = spec.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var sw = Stopwatch.StartNew();
        _logger.LogDebug("Starting process {Command}", spec.DisplayCommand);
        process.Start();
        process.StandardInput.Close(); // builds must never wait on interactive input
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            process.WaitForExit(); // flush async output handlers
            sw.Stop();
            return new ProcessResult(process.ExitCode, sw.Elapsed, Cancelled: false, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            sw.Stop();
            var timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            _logger.LogWarning("Process {Executable} {Reason} after {Elapsed}", spec.Executable, timedOut ? "timed out" : "was cancelled", sw.Elapsed);
            int exit;
            try { exit = process.HasExited ? process.ExitCode : -1; } catch (InvalidOperationException) { exit = -1; }
            return new ProcessResult(exit, sw.Elapsed, Cancelled: !timedOut, TimedOut: timedOut);
        }
    }

    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Failed to kill process tree");
        }
    }
}
