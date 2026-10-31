namespace HCG.ExecutableMaker.Security;

/// <summary>Describes the isolation actually provided by a build worker. Shown to the user verbatim.</summary>
public enum IsolationLevel
{
    /// <summary>Runs directly on this machine with the current user's privileges. Not sandboxed.</summary>
    None,
    /// <summary>Reserved for a future container/VM/remote worker. No implementation exists in M0.1.</summary>
    Isolated,
}

public static class TrustPolicy
{
    public const string TrustBoundaryTitle = "Building runs the project's own code";

    public const string TrustBoundaryExplanation =
        "Restoring, building and publishing a .NET project executes code supplied by that project: MSBuild targets, " +
        "source generators, analyzers, NuGet package scripts, and anything they invoke. HCG Executable Maker M0.1 runs " +
        "these steps locally with your user account's permissions. This is NOT a sandbox. Only build projects whose " +
        "source you trust.";

    public const string SecretsStatement =
        "Before each build, environment variables that look like credentials, tokens, signing secrets or cloud/CI " +
        "identities are removed from the build process. HCG never passes signing keys or passwords to a build.";

    public const string AcknowledgementLabel = "I trust this project's source and accept that building it runs its code on this computer.";

    public static string Describe(IsolationLevel level) => level switch
    {
        IsolationLevel.None => "Local execution — not sandboxed. Trusted projects only.",
        IsolationLevel.Isolated => "Isolated worker.",
        _ => level.ToString(),
    };
}
