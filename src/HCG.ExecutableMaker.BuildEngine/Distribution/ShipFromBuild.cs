using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

/// <summary>
/// "Ship to Store" offered on a successful build: its verified output files, and what the build knows (name, version).
/// Folders, unverified and failed outputs are not offered, and the page says why.
/// </summary>
public sealed record ShipFromBuild(string Origin, string? Version, IReadOnlyList<string> Files, IReadOnlyList<string> NotOffered)
{
    public static ShipFromBuild? From(BuildRecord build)
    {
        if (build.Status != BuildStatus.Succeeded) return null;
        var files = new List<string>();
        var skipped = new List<string>();
        foreach (var a in build.Artifacts)
        {
            if (a.Kind == ArtifactKind.Directory) skipped.Add($"{a.FileName}: a folder{(a.FileName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && build.Settings.Mobile is not null ? " (iOS Simulator app: runs only in the Simulator; HCG Store does not accept it)" : " — ship its ZIP or installer instead")}.");
            else if (a.Verification is not (VerificationStatus.Passed or VerificationStatus.PassedWithWarnings)) skipped.Add($"{a.FileName}: not verified by HCG ({a.Verification}).");
            else if (!File.Exists(a.Path)) skipped.Add($"{a.FileName}: no longer on disk.");
            else files.Add(a.Path);
        }
        var target = build.Settings.Mobile?.Output.DisplayName() ?? build.Settings.RuntimeIdentifier;
        return new ShipFromBuild($"{build.ProjectName} · {target} · built {(build.EndedAt ?? build.StartedAt).ToLocalTime():yyyy-MM-dd HH:mm}", build.Settings.Identity.Version, files, skipped);
    }
}
