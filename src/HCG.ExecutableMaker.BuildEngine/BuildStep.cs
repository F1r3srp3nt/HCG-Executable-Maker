using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.BuildEngine;

public enum BuildStage
{
    Preparing,
    Restoring,
    Compiling,
    Publishing,
    VerifyingOutput,
    Packaging,
    Checksumming,
    Completed,
}

/// <summary>One external tool invocation in a build plan.</summary>
/// <param name="CaptureStdout">The step's standard output is collected for the engine (e.g. swift build --show-bin-path).</param>
public sealed record BuildStep(BuildStage Stage, string Title, ProcessSpec Process, bool CaptureStdout = false)
{
    /// <summary>Files that must exist before the step runs (absolute path → the failure message when one does not).</summary>
    public IReadOnlyList<(string Path, string Message)> RequiredFiles { get; init; } = [];
    /// <summary>The tool writes progress to stderr (npm, electron-builder): such lines are logged as output, not errors.</summary>
    public bool StdErrIsProgress { get; init; }
}

public sealed record BuildLogLine(DateTimeOffset Timestamp, BuildLogKind Kind, string Text);

public enum BuildLogKind
{
    Info,
    Command,
    Output,
    Error,
    Warning,
    Success,
}
