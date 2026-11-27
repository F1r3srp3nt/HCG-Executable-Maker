using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.Packaging.Distribution;

/// <summary>
/// HCG Executable Maker's OWN build provenance (the source the running Maker was built from), as embedded at build time
/// (tools/self-package writes build-source.txt; Directory.Build.targets embeds it). It needs no git on this computer.
/// It is never used for the projects Maker packages: their provenance comes from their own source (SourceProvenance).
/// </summary>
public static class MakerBuildProvenance
{
    public const string Basis = "embedded in HCG Executable Maker at build time (assembly metadata HcgSourceCommit)";

    public static SourceProvenance Embedded() => From(HostInfo.SourceCommit, HostInfo.SourceDirty, HostInfo.SourceRepository);

    /// <summary>The provenance for an embedded commit; unknown (with the reason) when the build recorded none.</summary>
    public static SourceProvenance From(string? commit, bool? dirty, string? repository) =>
        commit is { Length: 40 } c && c.All(Uri.IsHexDigit)
            ? new SourceProvenance(repository, c.ToLowerInvariant(), null, dirty, Basis)
            : SourceProvenance.Unknown with { Basis = "this HCG Executable Maker build recorded no source commit (built without git and without build-source.txt)" };
}
