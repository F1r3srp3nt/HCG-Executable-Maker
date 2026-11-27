using System.Globalization;
using HCG.ExecutableMaker.Account.Store;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

/// <summary>What the publisher wants to do in Distribution Studio.</summary>
public enum DistributionOperation
{
    /// <summary>A new version (or build) of a product HCG Store already lists.</summary>
    NewUpdate,
    /// <summary>A Store-internal draft product (central registration and listing stay with HCG).</summary>
    NewProduct,
    /// <summary>Another platform, architecture or package of a listed product (an open draft gets the files; a published
    /// release is never changed — a new revision is prepared instead).</summary>
    AddToListedProduct,
    /// <summary>Current, superseded, withdrawn and private releases, with recorded availability requests.</summary>
    ManageVersions,
}

/// <summary>Where a file in a draft came from. Maker-built files are distinguished from files the person chose; provenance is
/// reported only from a Release Passport of the exact bytes, never from where the file was picked.</summary>
public enum ArtifactSource { LatestBuild, BuildHistory, ArtifactVault, LocalFile }

public enum FindingLevel { Info, Warning, Blocker }

public sealed record DistributionFinding(string Code, FindingLevel Level, string Message);

/// <summary>A published file of a release, as HCG Store lists it.</summary>
public sealed record PlatformRelease(string Platform, string Architecture, string Format, string FileName, string ReleaseId, string Version, string Build, string? Channel, string Status);

/// <summary>Distribution Studio's comparison of a proposed version with what HCG Store and this computer hold. Read-only: it
/// never disables or replaces a release.</summary>
public sealed record VersionComparison
{
    public StoreRelease? CurrentPublished { get; init; }
    /// <summary>catalogue (the Store's currentReleaseId) · newest-published (no current release is stated) · none.</summary>
    public string CurrentBasis { get; init; } = "none";
    public StoreRelease? LatestPrivateDraft { get; init; }
    public string? LatestLocalBuild { get; init; }
    public string Proposed { get; init; } = "";
    public IReadOnlyList<PlatformRelease> PlatformReleases { get; init; } = [];
    public IReadOnlyList<DistributionFinding> Findings { get; init; } = [];
    /// <summary>The open draft (same product, edition, version and build) that this upload continues, if any.</summary>
    public StoreRelease? ContinuesDraft { get; init; }
    public bool Blocked => Findings.Any(f => f.Level == FindingLevel.Blocker);
}

/// <summary>How a new platform or package is added to a listed product.</summary>
public sealed record AdditionPlan(bool AddsToOpenDraft, string? ReleaseId, string Version, string Build, string Explanation);

/// <summary>One line of the Synchronization Center, from HCG Store's own answers (never assumed).</summary>
/// <summary>How an availability change is made on this Store.</summary>
public enum AvailabilityRoute
{
    /// <summary>Through HCG Store's own endpoint from Maker (HCG Store checks the permission and the typed version).</summary>
    Direct,
    /// <summary>A publication decision made by a person in HCG Store's browser review; Maker opens the release there.</summary>
    Browser,
    /// <summary>Recorded as a request for the people who approve and publish (proposed Store contract).</summary>
    Request,
    /// <summary>Not offered by this Store; explained, nothing is sent.</summary>
    Unavailable,
}

public sealed record AvailabilityOption(string Action, string Name, AvailabilityRoute Route, string Explanation);

/// <summary>
/// Which HCG Store publishing contract answers, from its own capability discovery: the release lifecycle of HCG Store
/// main (operations listed in capabilities), the proposed Distribution Studio requests (capability flags), or the
/// deployed contract (drafts and uploads only). Nothing is assumed: a Store that states neither gets neither.
/// </summary>
public sealed record StoreContract(StoreCapabilityFlags? Flags, StoreOperations? Operations)
{
    public bool Lifecycle => Operations is not null && (Operations.Availability.Count > 0 || Operations.Review.Count > 0);
    public bool Requests => !Lifecycle && Flags?.AvailabilityRequests == true;
    public bool PublicationChoice => Lifecycle || Flags?.PublicationRequests == true;
    public string Name => Lifecycle ? "HCG Store release lifecycle" : Requests ? "Distribution Studio requests (proposed)" : "drafts and uploads only";
    public string Explanation => Lifecycle
        ? "HCG Store manages availability itself: disable and enable from Maker; supersede, restore and revoke in HCG Store's browser review by a person with HCG's publication permission."
        : Requests
            ? "HCG Store records availability requests for the people who approve and publish; nothing changes until they act."
            : "This HCG Store does not offer release management to apps yet: private drafts and uploads work, and disable, supersede and restore are done in HCG Store's Release Manager by a person. Maker changes no availability here.";
}

public sealed record SyncGroup(string Key, string Title, string Tone, IReadOnlyList<string> Items)
{
    public int Count => Items.Count;
}

/// <summary>
/// Distribution Studio's decisions, as pure functions of HCG Store's answers and the files on this computer: version
/// comparison (duplicates, downgrades, artifact conflicts), discrepancies between typed metadata and verified file facts,
/// how a platform is added to a listed product, the publication request sent with a draft, and the synchronization
/// summary. Nothing here contacts HCG Store or changes a release.
/// </summary>
public static class DistributionStudio
{
    public static readonly string[] OpenStatuses = ["draft", "submitted", "approved"];

    public static string SourceLabel(ArtifactSource s) => s switch
    {
        ArtifactSource.LatestBuild => "Maker build (this session)",
        ArtifactSource.BuildHistory => "Maker build (Build History)",
        ArtifactSource.ArtifactVault => "Artifact Vault",
        _ => "Selected manually",
    };

    /// <summary>Maker built it (a build record names it); provenance is still only what a passport of these bytes says.</summary>
    public static bool MakerBuilt(ArtifactSource s) => s is ArtifactSource.LatestBuild or ArtifactSource.BuildHistory;

    // ───────────── Versions ─────────────

    /// <summary>Semantic Versioning precedence (2.0.0): -1, 0 or 1. Build metadata is ignored. Null when either is not SemVer.</summary>
    public static int? CompareVersions(string a, string b)
    {
        if (!TryParse(a, out var x) || !TryParse(b, out var y)) return null;
        for (var i = 0; i < 3; i++) if (x.Core[i] != y.Core[i]) return x.Core[i].CompareTo(y.Core[i]);
        if (x.Pre.Length == 0 || y.Pre.Length == 0) return x.Pre.Length == y.Pre.Length ? 0 : x.Pre.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(x.Pre.Length, y.Pre.Length); i++)
        {
            var (p, q) = (x.Pre[i], y.Pre[i]);
            var pn = long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var pv);
            var qn = long.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var qv);
            var c = pn && qn ? pv.CompareTo(qv) : pn ? -1 : qn ? 1 : string.CompareOrdinal(p, q);
            if (c != 0) return Math.Sign(c);
        }
        return x.Pre.Length.CompareTo(y.Pre.Length);
    }

    private static bool TryParse(string v, out (long[] Core, string[] Pre) parsed)
    {
        parsed = ([], []);
        var s = v.Trim();
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = (dash >= 0 ? s[..dash] : s).Split('.');
        if (core.Length != 3) return false;
        var nums = new long[3];
        for (var i = 0; i < 3; i++) if (!long.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return false;
        parsed = (nums, dash >= 0 ? s[(dash + 1)..].Split('.') : []);
        return true;
    }

    /// <summary>The product's current release: the one HCG Store names (currentReleaseId), else the newest published release
    /// of the edition and channel. Never inferred from a local build.</summary>
    public static (StoreRelease? Release, string Basis) CurrentPublished(StoreProduct? product, IReadOnlyList<StoreRelease> history, string? editionId, string? channel)
    {
        if (product?.CurrentReleaseId is { } id && history.FirstOrDefault(r => r.Id == id) is { } named) return (named, "catalogue");
        // HCG Store main names the current release per target (platform, architecture, channel).
        if (history.Where(r => r.EditionId == editionId && r.Targets.Any(t => t.Current && (channel is null || t.Channel == channel)))
                .OrderByDescending(r => r.PublishedAt ?? 0).FirstOrDefault() is { } targeted) return (targeted, "current-target");
        var published = history.Where(r => r.Status == "published" && r.Availability is null or "available" && r.EditionId == editionId && (channel is null || r.Channel == channel))
            .OrderByDescending(r => r.PublishedAt ?? 0).FirstOrDefault();
        return published is null ? (null, "none") : (published, "newest-published");
    }

    /// <summary>
    /// Compares the proposed version and build with the product's history and the latest local build: duplicates (refused),
    /// a continued draft, revisions, downgrades, identical bytes already in the Store and platform/architecture overlaps.
    /// It never proposes disabling the current release because a newer local build exists.
    /// </summary>
    public static VersionComparison Compare(StoreProduct? product, IReadOnlyList<StoreRelease> history, string? editionId, string channel,
        string version, string build, IReadOnlyList<StoreArtifactCandidate> files, string? latestLocalBuild = null, string? targetReleaseId = null)
    {
        var findings = new List<DistributionFinding>();
        var edition = history.Where(r => r.EditionId == editionId).ToList();
        var (current, basis) = CurrentPublished(product, history, editionId, channel);
        var draft = edition.Where(r => OpenStatuses.Contains(r.Status)).OrderByDescending(r => r.CreatedAt ?? 0).FirstOrDefault();
        var platforms = edition.Where(r => r.Status == "published")
            .SelectMany(r => r.Artifacts.Where(a => a.Status == "verified").SelectMany(a => (a.Architectures.Count > 0 ? a.Architectures : ["any"])
                .Select(arch => new PlatformRelease(a.Platform, arch, a.Format, a.FileName, r.Id, r.Version, r.Build, r.Channel, r.Status))))
            .ToList();
        StoreRelease? continues = null;
        var v = version.Trim();
        var b = build.Trim();

        if (v.Length > 0 && CompareVersions(v, v) is null)
            findings.Add(new("not-semver", FindingLevel.Blocker, $"\"{v}\" is not a Semantic Version (e.g. 1.2.0 or 0.3.0-beta.1); HCG Store refuses it."));
        if (v.Length > 0 && b.Length > 0)
        {
            var same = edition.FirstOrDefault(r => r.Version == v && r.Build == b);
            if (same is not null && same.Status == "draft") { continues = same; findings.Add(new("continues-draft", FindingLevel.Info, $"Version {v} build {b} is an open private draft ({same.Id}); the files are added to it. No second release is created.")); }
            else if (same is not null)
                findings.Add(new("duplicate", FindingLevel.Blocker, $"Version {v} build {b} already exists in HCG Store ({same.Status}, {same.Id}). Releases are immutable once submitted: choose a new build number."));
            else if (edition.Where(r => r.Version == v).ToList() is { Count: > 0 } builds)
                findings.Add(new("revision", FindingLevel.Info, $"Version {v} already exists with build(s) {string.Join(", ", builds.Select(r => $"{r.Build} ({r.Status})"))}: this is a new revision of that version."));
        }
        if (current is not null && v.Length > 0 && CompareVersions(v, current.Version) is < 0)
            findings.Add(new("downgrade", FindingLevel.Warning, $"{v} is older than the current published version {current.Version} (build {current.Build}). Publishing it would not replace the current release unless an approver decides so."));
        if (current is not null && v.Length > 0 && CompareVersions(v, current.Version) == 0 && b != current.Build)
            findings.Add(new("same-as-current", FindingLevel.Info, $"Same version as the current release ({current.Version} build {current.Build}); a different build number."));
        if (latestLocalBuild is { Length: > 0 } local && v.Length > 0 && CompareVersions(v, local) is < 0)
            findings.Add(new("older-than-local", FindingLevel.Info, $"A newer local build exists ({local}). Nothing in HCG Store changes because of it."));
        if (latestLocalBuild is { Length: > 0 } local2 && current is not null && CompareVersions(local2, current.Version) is > 0)
            findings.Add(new("newer-local", FindingLevel.Info, $"The latest local build ({local2}) is newer than the current published version ({current.Version}). The current release stays available until a replacement is approved and published."));

        // Artifact conflicts: the same bytes already in another release; a file name already in the draft with other bytes.
        foreach (var f in files.Where(f => f.Sha256.Length == 64))
        {
            foreach (var r in history.Where(r => r.Id != continues?.Id && r.Id != targetReleaseId))
                if (r.Artifacts.FirstOrDefault(a => a.Status is "verified" && (a.Sha256 ?? a.DeclaredSha256) == f.Sha256) is { } same)
                    findings.Add(new("same-bytes", FindingLevel.Warning, $"{f.FileName}: identical bytes (SHA-256) are already in {r.Version} build {r.Build} ({r.Status}) as {same.FileName}."));
            var target = continues ?? history.FirstOrDefault(r => r.Id == targetReleaseId);
            if (target?.Artifacts.FirstOrDefault(a => a.FileName == f.FileName && a.Status != "removed") is { } named && (named.Sha256 ?? named.DeclaredSha256) != f.Sha256)
                findings.Add(new("name-conflict", FindingLevel.Warning, $"{f.FileName}: the draft already holds a different file with this name ({named.Status}); it is replaced in the draft only."));
            if (f.Platform is { } plat)
                foreach (var arch in f.Architectures.Count > 0 ? f.Architectures : ["any"])
                    if (platforms.FirstOrDefault(p => p.Platform == plat && p.Architecture == arch && p.Format == f.Format && p.Channel == channel) is { } pr)
                        findings.Add(new("supersedes-platform", FindingLevel.Info, $"{f.FileName}: {StoreArtifacts.PlatformName(plat)} {arch} {f.Format} is published in {pr.Version} build {pr.Build}; that release stays available until this one is published."));
        }
        // Distinct messages only (one file can match several rules once).
        var distinct = findings.GroupBy(f => f.Message).Select(g => g.First()).ToList();
        return new VersionComparison
        {
            CurrentPublished = current, CurrentBasis = basis, LatestPrivateDraft = draft, LatestLocalBuild = latestLocalBuild,
            Proposed = v.Length == 0 ? "" : b.Length == 0 ? v : $"{v} (build {b})", PlatformReleases = platforms, Findings = distinct, ContinuesDraft = continues,
        };
    }

    // ───────────── Verified facts vs. typed metadata ─────────────

    /// <summary>
    /// Differences between what the person typed and what the files themselves (or the Release Passport of their exact bytes)
    /// state. Typed descriptive values are allowed, but a discrepancy is never hidden: each one becomes a compatibility note
    /// sent with the draft, so reviewers see it.
    /// </summary>
    public static IReadOnlyList<DistributionFinding> Discrepancies(string version, string build, string? productTitle, IReadOnlyList<StoreArtifactCandidate> files)
    {
        var list = new List<DistributionFinding>();
        foreach (var f in files)
        {
            var p = f.Provenance;
            if (p?.Version is { Length: > 0 } pv && version.Length > 0 && !SameVersion(pv, version))
                list.Add(new("version-discrepancy", FindingLevel.Warning, $"Discrepancy: {f.FileName} — Release Passport states version {pv}; entered {version}."));
            if (p?.BuildNumber is { Length: > 0 } pb && build.Length > 0 && pb != build)
                list.Add(new("build-discrepancy", FindingLevel.Warning, $"Discrepancy: {f.FileName} — Release Passport states build {pb}; entered {build}."));
            if (f.Package?.Version is { Length: > 0 } dv && version.Length > 0 && !SameVersion(dv, version))
                list.Add(new("package-version-discrepancy", FindingLevel.Warning, $"Discrepancy: {f.FileName} — package control file states version {dv}; entered {version}."));
            if (p?.Architecture is { Length: > 0 } pa && f.Architectures.Count > 0 && !f.Architectures.Contains(pa, StringComparer.OrdinalIgnoreCase) && pa != "universal")
                list.Add(new("architecture-discrepancy", FindingLevel.Warning, $"Discrepancy: {f.FileName} — Release Passport states architecture {pa}; the file contains {string.Join(", ", f.Architectures)}."));
            if (p?.ProductName is { Length: > 0 } pn && productTitle is { Length: > 0 } t && !pn.Equals(t, StringComparison.OrdinalIgnoreCase) && !t.Contains(pn, StringComparison.OrdinalIgnoreCase) && !pn.Contains(t, StringComparison.OrdinalIgnoreCase))
                list.Add(new("name-discrepancy", FindingLevel.Info, $"Discrepancy: {f.FileName} — Release Passport names the application \"{pn}\"; the product is \"{t}\"."));
        }
        return list;
    }

    /// <summary>"1.2.0" and "1.2.0~beta1"/"1.2.0-beta1" style package versions compare by their upstream part.</summary>
    private static bool SameVersion(string fact, string entered)
    {
        static string Norm(string s) => s.Trim().TrimStart('v', 'V').Replace('~', '-');
        var (a, b) = (Norm(fact), Norm(entered));
        if (a == b) return true;
        if (CompareVersions(a, b) == 0) return true;
        // A Debian revision ("1.2.0-1") or a four-part Windows file version ("1.2.0.0") describes the same upstream version.
        return a.StartsWith(b + "-", StringComparison.Ordinal) || a == b + ".0";
    }

    /// <summary>Compatibility notes for the draft: what the person wrote, then every discrepancy (HCG Store keeps up to 20).</summary>
    public static IReadOnlyList<string> CompatibilityNotes(IEnumerable<string> typed, IEnumerable<DistributionFinding> discrepancies) =>
        typed.Select(t => t.Trim()).Where(t => t.Length > 0).Concat(discrepancies.Select(d => d.Message)).Distinct().Select(n => n.Length > 500 ? n[..499] + "…" : n).Take(20).ToList();

    // ───────────── Adding a platform to a listed product ─────────────

    /// <summary>
    /// How files for another platform, architecture or package are added to a release: an open draft takes them directly;
    /// a submitted, approved or published release is never changed, so a new revision (same version, next build) is
    /// prepared. The earlier release keeps every file it has.
    /// </summary>
    public static AdditionPlan PlanAddition(StoreRelease target, IReadOnlyList<StoreRelease> history)
    {
        if (target.Status == "draft")
            return new(true, target.Id, target.Version, target.Build, $"{target.Version} build {target.Build} is an open private draft: the files are added to it.");
        var builds = history.Where(r => r.ProductId == target.ProductId && r.EditionId == target.EditionId && r.Version == target.Version).Select(r => r.Build).ToList();
        var next = NextBuild(target.Build, builds);
        return new(false, null, target.Version, next,
            $"{target.Version} build {target.Build} is {target.Status}; its files cannot change. A new revision, {target.Version} build {next}, is prepared as a private draft. " +
            $"{target.Version} build {target.Build} stays as it is.");
    }

    /// <summary>The next free build label: numeric builds count up; others get a "-r2", "-r3"… suffix.</summary>
    public static string NextBuild(string build, IReadOnlyCollection<string> taken)
    {
        if (long.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            var max = taken.Select(t => long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : 0).DefaultIfEmpty(n).Max();
            return (Math.Max(max, n) + 1).ToString(CultureInfo.InvariantCulture);
        }
        for (var i = 2; ; i++) { var c = $"{build}-r{i}"; if (!taken.Contains(c) && c.Length <= 64) return c; if (c.Length > 64) return $"r{i}"; }
    }

    // ───────────── Publication choice prepared with the draft ─────────────

    /// <summary>
    /// The request sent with the draft (only to a Store with the publicationRequests capability). It asks; the person who
    /// publishes decides. "compatible" supersedes only releases of the same channel whose every platform and architecture the
    /// new release also provides, and only once the new release is published.
    /// </summary>
    public static StorePublicationRequest? PublicationRequest(bool makeCurrent, bool supersedeCompatible, StoreCapabilityFlags? flags) =>
        flags?.PublicationRequests == true && (makeCurrent || supersedeCompatible)
            ? new StorePublicationRequest(makeCurrent, supersedeCompatible && flags.SupersedeCompatible ? "compatible" : "none")
            : null;

    /// <summary>HCG Store main's draft field: once published, the release replaces the current release of each target
    /// (platform, architecture, channel) it serves; other targets are untouched. Sent only to a Store that lists its
    /// operations. Until publication succeeds the previous release stays current.</summary>
    public static bool? SupersedeOnPublish(bool makeCurrent, bool supersede, StoreContract contract) =>
        contract.Lifecycle && (makeCurrent || supersede) ? true : null;

    // ───────────── Version management (capability discovery) ─────────────

    public static IReadOnlyList<AvailabilityOption> AvailabilityOptions(StoreRelease r, StoreContract contract)
    {
        var list = new List<AvailabilityOption>();
        var availability = r.Availability ?? (r.Status == "withdrawn" ? "withdrawn" : "available");
        if (contract.Lifecycle)
        {
            var ops = contract.Operations!;
            if (availability == "available" && ops.Offers("releases.disable") && r.Status != "withdrawn")
                list.Add(new("disable", "Disable version", AvailabilityRoute.Direct,
                    "Stops delivery of this release now; it stays in the history with its files and can be enabled again. Other platforms, architectures and channels are not touched. HCG Store checks your HCG permission."));
            if (availability is "disabled" or "archived" && r.PublishedAt is null && ops.Offers("releases.enable"))
                list.Add(new("enable", "Enable version", AvailabilityRoute.Direct, "Makes this unpublished release available again for review."));
            if (r.Status == "published" && availability == "available" && ops.InBrowser("releases.supersede"))
                list.Add(new("supersede", "Supersede version (make current)", AvailabilityRoute.Browser,
                    "Making a published release current on its targets is a publication decision: a person with HCG's publication permission does it in HCG Store's browser review, typing the version."));
            if (r.Status is "published" or "superseded" && availability != "revoked" && r.PublishedAt is not null && ops.InBrowser("releases.restore"))
                list.Add(new("restore", "Restore previous version", AvailabilityRoute.Browser,
                    "Restoring an earlier published release (rollback) is a publication decision made in HCG Store's browser review by a person with HCG's publication permission."));
            if (availability is "disabled" or "archived" && r.PublishedAt is not null)
                list.Add(new("enable", "Enable version", AvailabilityRoute.Browser, "Re-enabling a published release is a publication decision made in HCG Store's browser review."));
            return list;
        }
        if (contract.Requests)
        {
            foreach (var a in AvailabilityActions(r.Status))
                list.Add(new(a, a switch { "disable" => "Disable version", "restore" => "Restore previous version", "supersede" => "Supersede version", _ => "Make current" }, AvailabilityRoute.Request,
                    "Recorded in HCG Store for the people who approve and publish; nothing changes until they act."));
            return list;
        }
        foreach (var (a, n) in new[] { ("disable", "Disable version"), ("supersede", "Supersede version"), ("restore", "Restore previous version") })
            list.Add(new(a, n, AvailabilityRoute.Unavailable, contract.Explanation));
        return list;
    }

    /// <summary>The availability actions a release offers under the recorded-request contract.</summary>
    public static IReadOnlyList<string> AvailabilityActions(string status) => status switch
    {
        "published" => ["make-current", "supersede", "disable"],
        "withdrawn" or "superseded" => ["restore"],
        _ => [],
    };

    public static string ActionName(string action) => action switch
    {
        "make-current" => "Make current",
        "supersede" => "Supersede",
        "disable" => "Disable",
        "restore" => "Restore",
        "enable" => "Enable",
        _ => action,
    };

    // ───────────── Synchronization Center ─────────────

    /// <summary>
    /// What is where, from HCG Store's Release Manager list and this computer's files and upload results — never assumed.
    /// </summary>
    public static IReadOnlyList<SyncGroup> Sync(IReadOnlyList<StoreManagedRelease> managed, IReadOnlyCollection<string> currentReleaseIds,
        IReadOnlyList<(string FileName, string State)> local, IReadOnlyList<DistributionFinding> conflicts, string? productId = null)
    {
        var rel = managed.Where(r => productId is null || r.ProductId == productId).ToList();
        static string L(StoreManagedRelease r) => $"{r.ProductTitle ?? r.ProductId} {r.Version} (build {r.Build}){(r.Channel is { } c ? " · " + c : "")} · {r.Stage ?? r.Status} · {r.Files} file(s)";
        return
        [
            new("local", "Local artifacts", "neutral", local.Where(l => l.State is "local").Select(l => l.FileName).ToList()),
            new("uploaded", "Uploaded and verified", "success", local.Where(l => l.State is "verified" or "skipped").Select(l => l.FileName).ToList()),
            new("drafts", "Private drafts", "info", rel.Where(r => r.Status == "draft").Select(L).ToList()),
            new("review", "Pending review", "warning", rel.Where(r => r.Status is "submitted" or "approved").Select(L).ToList()),
            new("published", "Published", "success", rel.Where(r => r.Status == "published").Select(L).ToList()),
            new("current", "Current", "success", rel.Where(r => currentReleaseIds.Contains(r.Id)).Select(L).ToList()),
            new("disabled", "Superseded or withdrawn", "neutral", rel.Where(r => r.Status is "superseded" or "withdrawn").Select(L).ToList()),
            new("failures", "Upload failures", "error", local.Where(l => l.State is "failed" or "rejected").Select(l => $"{l.FileName} ({l.State})").ToList()),
            new("conflicts", "Conflicts", "warning", conflicts.Where(c => c.Level != FindingLevel.Info).Select(c => c.Message).ToList()),
        ];
    }
}
