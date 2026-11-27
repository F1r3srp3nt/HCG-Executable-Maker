using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Signing;

/// <summary>One verification of what a build is: its source revision or its compilation flags.</summary>
public sealed record BuildVerificationCheck(string Title, SigningCheckOutcome Outcome, string Detail, string? Remedy = null);

/// <summary>
/// Proves what an iOS build actually is, before compiling:
/// <list type="bullet">
/// <item><b>Source:</b> the commit the imported copy was made from, whether the original folder has moved on since
/// (a stale copy builds old code), and, if one was given, that it is the expected commit.</item>
/// <item><b>Compilation flags:</b> the effective SWIFT_ACTIVE_COMPILATION_CONDITIONS of this configuration and of the
/// other one. Flags present in Debug but not in Release are the project's development-only flags (Unimatch:
/// UNIMATCH_DEVELOPMENT_RUN). A development build must have DEBUG and them; a distribution build must have none.</item>
/// </list>
/// </summary>
public static class DevelopmentBuildVerification
{
    public static IReadOnlySet<string> Flags(string? conditions) =>
        (conditions ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f != "$(inherited)").ToHashSet(StringComparer.Ordinal);

    /// <summary>Flags the Debug configuration compiles with that Release does not (DEBUG itself excluded).</summary>
    public static IReadOnlyList<string> DevelopmentOnlyFlags(XcodeTargetSettings? debug, XcodeTargetSettings? release) =>
        debug is null || release is null ? []
            : Flags(debug.CompilationConditions).Except(Flags(release.CompilationConditions)).Where(f => f != "DEBUG").Order(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<BuildVerificationCheck> CompilationFlags(AppleBuildDestination destination, string configuration,
        XcodeTargetSettings? built, IReadOnlyList<string> developmentOnly, IReadOnlyList<string> expected)
    {
        var checks = new List<BuildVerificationCheck>();
        if (built is null)
        {
            checks.Add(new("Compilation flags", expected.Count > 0 ? SigningCheckOutcome.Failed : SigningCheckOutcome.NotVerified,
                "The effective build settings could not be read, so the compilation flags are unknown.",
                expected.Count > 0 ? "Check that the scheme and configuration exist (xcodebuild -showBuildSettings), then build again." : null));
            return checks;
        }
        var flags = Flags(built.CompilationConditions);
        var shown = flags.Count == 0 ? "none" : string.Join(" ", flags.Order(StringComparer.Ordinal));
        if (destination.IsDistribution())
        {
            var leaked = developmentOnly.Where(flags.Contains).ToList();
            checks.Add(leaked.Count == 0
                ? new("Development flags excluded", SigningCheckOutcome.Passed,
                    $"{configuration} compiles with: {shown}. None of the development-only flags ({(developmentOnly.Count == 0 ? "none found" : string.Join(", ", developmentOnly))}) is active.")
                : new("Development flags excluded", SigningCheckOutcome.Failed,
                    $"{configuration} would compile with the development-only flag(s) {string.Join(", ", leaked)}: development functionality would ship.",
                    "Archive with the project's Release configuration, which does not enable them."));
        }
        else
        {
            var debug = configuration.Contains("debug", StringComparison.OrdinalIgnoreCase);
            if (debug)
                checks.Add(flags.Contains("DEBUG")
                    ? new("DEBUG flag", SigningCheckOutcome.Passed, $"{configuration} compiles with: {shown}.")
                    : new("DEBUG flag", SigningCheckOutcome.Warning, $"{configuration} does not define DEBUG (it compiles with: {shown}); code under #if DEBUG is not included."));
            if (developmentOnly.Count > 0)
            {
                var active = developmentOnly.Where(flags.Contains).ToList();
                var inactive = developmentOnly.Except(active).ToList();
                checks.Add(new("Development-only flags", inactive.Count == 0 ? SigningCheckOutcome.Passed : SigningCheckOutcome.Warning,
                    inactive.Count == 0
                        ? $"Active in this build: {string.Join(", ", active)} (enabled for Debug, never for Release)."
                        : $"Not active in this {configuration} build: {string.Join(", ", inactive)}" + (debug ? " (a local setting such as Config/Local.xcconfig may turn it off)." : " (Release builds never include them; choose Debug to test development functionality)."),
                    inactive.Count == 0 ? null : debug ? "Check Config/Local.xcconfig (or the project's own switch); Maker does not change it." : "Choose the Debug configuration."));
            }
        }
        var missing = expected.Where(e => !flags.Contains(e)).ToList();
        if (expected.Count > 0)
            checks.Add(missing.Count == 0
                ? new("Expected flags", SigningCheckOutcome.Passed, $"{string.Join(", ", expected)} active.")
                : new("Expected flags", SigningCheckOutcome.Failed, $"Expected {string.Join(", ", missing)} to be active, but {configuration} compiles with: {shown}.",
                    destination.IsDistribution() ? "Distribution builds are Release builds." : "Choose the Debug configuration, and check the project's local settings (Config/Local.xcconfig)."));
        return checks;
    }

    /// <summary>The imported copy's commit: current, stale (the original moved on), and the expected commit if one was given.</summary>
    public static IReadOnlyList<BuildVerificationCheck> Source(ProjectRecord project, string? expectedCommit)
    {
        var checks = new List<BuildVerificationCheck>();
        var status = ImportRevisions.Check(project);
        var commit = status.Recorded?.Commit;
        checks.Add(status.State switch
        {
            RevisionState.Current => new("Source commit", SigningCheckOutcome.Passed, status.Detail),
            RevisionState.Stale => new("Source commit", SigningCheckOutcome.Failed, status.Detail,
                "Re-import the project (or refresh it from its source) so the build uses the code you have now."),
            RevisionState.FromArchive => new("Source commit", SigningCheckOutcome.Passed, status.Detail),
            _ => new("Source commit", SigningCheckOutcome.NotVerified, status.Detail),
        });
        if (expectedCommit is { Length: > 0 } want)
        {
            var w = want.Trim().ToLowerInvariant();
            checks.Add(commit is not null && w.Length >= 7 && commit.StartsWith(w, StringComparison.OrdinalIgnoreCase)
                ? new("Expected commit", SigningCheckOutcome.Passed, $"The imported copy is commit {ImportRevisions.Short(commit)}, as expected.")
                : new("Expected commit", SigningCheckOutcome.Failed,
                    commit is null ? $"Expected commit {w}, but the imported copy's commit is unknown ({status.Detail})"
                                   : $"Expected commit {w}, but the imported copy is {ImportRevisions.Short(commit)}.",
                    "Check out the expected commit in the original folder and re-import (or refresh from source)."));
        }
        return checks;
    }
}
