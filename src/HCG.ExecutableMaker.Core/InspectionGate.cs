namespace HCG.ExecutableMaker.Core;

/// <summary>
/// Keeps only the newest asynchronous result. Every change of selection (project, scheme, configuration, output,
/// toolchain) takes a new ticket; work started under an older ticket is discarded when it completes, so a slow inspection
/// can never overwrite what a newer selection shows.
/// </summary>
public sealed class InspectionGate
{
    private long _current;

    /// <summary>Starts a new generation; results of earlier tickets become stale.</summary>
    public long Next() => Interlocked.Increment(ref _current);

    /// <summary>The ticket of the current generation (for work that belongs to the selection as it is now).</summary>
    public long Current => Interlocked.Read(ref _current);

    public bool IsCurrent(long ticket) => Interlocked.Read(ref _current) == ticket;

    /// <summary>Runs <paramref name="apply"/> only when <paramref name="ticket"/> is still current; returns whether it ran.</summary>
    public bool TryApply(long ticket, Action apply)
    {
        if (!IsCurrent(ticket)) return false;
        apply();
        return true;
    }
}
