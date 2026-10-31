using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.BuildEngine;

/// <summary>
/// Executes build steps that run untrusted project code. This abstraction is the seam for future isolated workers
/// (containers, VMs, remote build hosts). Each implementation must report its real isolation level.
/// </summary>
public interface IBuildWorker
{
    IsolationLevel Isolation { get; }
    string Description { get; }
    /// <summary>Environment passed to untrusted build processes (secrets removed).</summary>
    BuildEnvironmentPolicy.ScrubResult CreateEnvironment();
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string> onOutput, CancellationToken cancellationToken);
}

/// <summary>Runs builds directly on this machine as the current user. NOT a sandbox.</summary>
public sealed class LocalProcessBuildWorker(IProcessRunner runner) : IBuildWorker
{
    public IsolationLevel Isolation => IsolationLevel.None;
    public string Description => TrustPolicy.Describe(IsolationLevel.None);

    public BuildEnvironmentPolicy.ScrubResult CreateEnvironment() => BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment();

    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string> onOutput, CancellationToken cancellationToken)
    {
        if (spec.Environment is null)
            throw new InvalidOperationException("Untrusted build steps must run with an explicit, scrubbed environment.");
        return runner.RunAsync(spec, onOutput, cancellationToken);
    }
}
