namespace HCG.ExecutableMaker.Core;

/// <summary>
/// A structured process invocation. Arguments are passed as a list and never joined into a shell string.
/// </summary>
public sealed record ProcessSpec
{
    /// <summary>Absolute path to the executable.</summary>
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    /// <summary>
    /// When non-null, the child process receives exactly these variables (the parent environment is not inherited).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public TimeSpan? Timeout { get; init; }

    /// <summary>Human-readable rendering for logs only. Never executed.</summary>
    public string DisplayCommand => string.Join(' ', new[] { Executable }.Concat(Arguments).Select(QuoteForDisplay));

    private static string QuoteForDisplay(string s) =>
        s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
}

public enum OutputStream
{
    StdOut,
    StdErr,
}

public sealed record ProcessResult(int ExitCode, TimeSpan Duration, bool Cancelled, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !Cancelled && !TimedOut;
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs a process, streaming output lines to <paramref name="onOutput"/>. On cancellation or timeout the entire
    /// process tree is killed and the result is flagged accordingly (no exception is thrown for those cases).
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken);
}

public static class ProcessRunnerExtensions
{
    /// <summary>Runs a short probe command and captures combined output. Used for toolchain detection.</summary>
    public static async Task<(ProcessResult Result, string StdOut, string StdErr)> CaptureAsync(
        this IProcessRunner runner, ProcessSpec spec, CancellationToken cancellationToken)
    {
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var gate = new object();
        var result = await runner.RunAsync(spec, (stream, line) =>
        {
            lock (gate)
            {
                (stream == OutputStream.StdOut ? stdout : stderr).AppendLine(line);
            }
        }, cancellationToken).ConfigureAwait(false);
        return (result, stdout.ToString(), stderr.ToString());
    }
}
