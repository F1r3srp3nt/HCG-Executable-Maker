namespace HCG.Impactor.Model;

/// <summary>How much a finding matters for the next step (signing or installing).</summary>
public enum FindingSeverity
{
    /// <summary>Context only.</summary>
    Info,
    /// <summary>The step can go ahead, but something deserves attention (expiry soon, data may be affected, unverified).</summary>
    Warning,
    /// <summary>The step cannot succeed as configured; the remedy says what to change.</summary>
    Blocker,
}

/// <summary>
/// One result of an Impactor check. <see cref="Code"/> is stable (used by tests, history and the diagnostic export);
/// <see cref="Title"/> and <see cref="Detail"/> are what the user reads; <see cref="Remedy"/> is the next action.
/// </summary>
public sealed record Finding(FindingSeverity Severity, string Code, string Title, string Detail, string? Remedy = null)
{
    /// <summary>The bundle or item the finding is about (e.g. "PlugIns/Share.appex"), when it is not the whole app.</summary>
    public string? Subject { get; init; }

    public static Finding Info(string code, string title, string detail) => new(FindingSeverity.Info, code, title, detail);
    public static Finding Warn(string code, string title, string detail, string? remedy = null) => new(FindingSeverity.Warning, code, title, detail, remedy);
    public static Finding Block(string code, string title, string detail, string? remedy = null) => new(FindingSeverity.Blocker, code, title, detail, remedy);
}

public static class FindingExtensions
{
    public static bool HasBlockers(this IEnumerable<Finding> findings) => findings.Any(f => f.Severity == FindingSeverity.Blocker);
    public static IEnumerable<Finding> Blockers(this IEnumerable<Finding> findings) => findings.Where(f => f.Severity == FindingSeverity.Blocker);
    public static IEnumerable<Finding> Warnings(this IEnumerable<Finding> findings) => findings.Where(f => f.Severity == FindingSeverity.Warning);
}
