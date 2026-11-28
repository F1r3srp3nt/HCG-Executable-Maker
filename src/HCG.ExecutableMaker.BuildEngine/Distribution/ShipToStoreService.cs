using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

public enum ShipItemState { Waiting, Declaring, Uploading, Verifying, Verified, Rejected, Failed, Skipped }

public sealed record ShipItemProgress(string FileName, ShipItemState State, long Received, long Total, string? Detail, string? ArtifactId = null, string? Sha256 = null)
{
    /// <summary>attached · not-found · refused · unsupported (the Store has no passport endpoint) · null when not tried.</summary>
    public string? Passport { get; init; }
}

public sealed record ShipResult(StoreRelease Release, IReadOnlyList<ShipItemProgress> Items)
{
    /// <summary>The draft already existed (an earlier, interrupted attempt with the same details) and was continued.</summary>
    public bool ContinuedExistingDraft { get; init; }
    public bool AllVerified => Items.Count > 0 && Items.All(i => i.State is ShipItemState.Verified or ShipItemState.Skipped);
}

/// <summary>
/// Ship to Store: create (or continue) a private draft release in HCG Store and upload each file to it, resuming
/// interrupted uploads and recovering from rejected ones. Used by the Ship to Store page and by
/// <c>HCGExecutableMaker --ship-to-store</c>. It never submits, approves or publishes on its own.
/// </summary>
public sealed class ShipToStoreService(HcgStoreClient client, FeatureAccessService? features = null)
{
    /// <summary>
    /// HCG Store's rule for a .deb that HCG Repo will deliver (publishing.ts debianVersionMatches): the control file's
    /// Debian version must be the release version, or the release version followed by "-" and a Debian revision.
    /// Sileo asks for the version it saw in HCG Repo's index, and the Store matches it against the release, so a package
    /// numbered otherwise could never be delivered. Checked before anything is created or uploaded.
    /// </summary>
    public static bool DebianVersionMatches(string debVersion, string releaseVersion) =>
        debVersion == releaseVersion
        || (debVersion.StartsWith(releaseVersion + "-", StringComparison.Ordinal) && debVersion.Length > releaseVersion.Length + 1
            && debVersion[(releaseVersion.Length + 1)..].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '~'));

    public static IReadOnlyList<string> RepoDeliveryProblems(string releaseVersion, IReadOnlyList<string> destinations, IReadOnlyList<StoreArtifactCandidate> files)
    {
        if (!destinations.Contains("hcg-repo")) return [];
        var problems = new List<string>();
        foreach (var f in files.Where(f => f.Format == "deb"))
        {
            if (f.Package is not { Id.Length: > 0, Version.Length: > 0 } pkg)
                problems.Add($"{f.FileName}: HCG Repo needs the package identifier and version from the control file, and this .deb's control file could not be read.");
            else if (!DebianVersionMatches(pkg.Version, releaseVersion))
                problems.Add($"{f.FileName}: its Debian version is {pkg.Version}, but the release is {releaseVersion}. HCG Store refuses it for HCG Repo (Sileo asks for the version it saw in the index): " +
                             $"use release version {pkg.Version}, or package version {releaseVersion} (or {releaseVersion}-1).");
        }
        return problems;
    }

    /// <param name="existingReleaseId">Continue this draft (Retry after a failure) instead of creating a new release.</param>
    public async Task<ShipResult> ShipAsync(StoreReleaseRequest request, IReadOnlyList<StoreArtifactCandidate> files, string? existingReleaseId,
        IProgress<ShipItemProgress>? progress, CancellationToken ct)
    {
        if (files.Count == 0) throw new InvalidOperationException("Add at least one file.");
        // The staff publishing permission is asked again now, from the same HCG Store (UI and command line alike).
        if (features is not null) await features.RequireAsync(MakerFeatures.StorePublish, ct, client.Origin).ConfigureAwait(false);
        if (files.FirstOrDefault(f => !f.IsSupported) is { } bad) throw new InvalidOperationException($"{bad.FileName}: {string.Join(" ", bad.Problems)}");
        if (files.GroupBy(f => f.FileName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"Two files are named {twice.Key}; a release holds each file name once.");
        if (RepoDeliveryProblems(request.Version, request.Destinations, files) is { Count: > 0 } repoProblems)
            throw new InvalidOperationException(string.Join(" ", repoProblems));
        StoreRelease release;
        if (existingReleaseId is not null) release = await client.ReleaseAsync(existingReleaseId, ct).ConfigureAwait(false);
        else
        {
            // Creating a draft is idempotent in HCG Store: the same person asking again for the same product, edition,
            // version, build and details gets the existing draft back. So a lost answer is retried once, and a rerun
            // after a crash continues the same draft instead of creating a second release.
            try { release = await client.CreateReleaseAsync(request, ct).ConfigureAwait(false); }
            catch (HcgStoreException ex) when (ex.Status == 0)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                release = await client.CreateReleaseAsync(request, ct).ConfigureAwait(false);
            }
        }
        if (release.Status != "draft") throw new HcgStoreException("release_locked", $"Release {release.Version} ({release.Build}) is {release.Status}; files can be added only to a draft.", 409);
        var items = new List<ShipItemProgress>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            void Report(ShipItemProgress p) { progress?.Report(p); }
            var existing = release.Artifacts.FirstOrDefault(a => a.FileName == f.FileName && a.Status != "removed");
            try
            {
                if (existing is { Status: "verified" } && existing.Sha256 == f.Sha256)
                {
                    var skip = new ShipItemProgress(f.FileName, ShipItemState.Skipped, f.SizeBytes, f.SizeBytes, "Already uploaded and verified (same SHA-256).", existing.Id, existing.Sha256);
                    // A retry after an interruption (or against an HCG Store that now takes passports) still attaches it.
                    var (pp, pnote) = await AttachPassportAsync(existing.Id, f, ct).ConfigureAwait(false);
                    skip = skip with { Passport = pp, Detail = skip.Detail + (pnote is null ? "" : " " + pnote) };
                    items.Add(skip); Report(skip);
                    continue;
                }
                if (existing is { Status: "rejected" } || existing is { Status: "verified" })
                {
                    // A rejected file (or a different verified one) is taken out of the draft first; verified files stay stored.
                    release = await client.RemoveArtifactAsync(existing.Id, ct).ConfigureAwait(false);
                    existing = null;
                }
                StoreUploadUrl url;
                string artifactId;
                if (existing is { Status: "awaiting-upload" or "uploading" } && existing.DeclaredSha256 == f.Sha256 && existing.SizeBytes == f.SizeBytes)
                {
                    artifactId = existing.Id;
                    url = await client.RenewUploadUrlAsync(artifactId, ct).ConfigureAwait(false);
                    Report(new(f.FileName, ShipItemState.Uploading, url.Received ?? 0, f.SizeBytes, $"Resuming at byte {url.Received ?? 0:N0}.", artifactId));
                }
                else
                {
                    if (existing is not null) await client.RemoveArtifactAsync(existing.Id, ct).ConfigureAwait(false);
                    Report(new(f.FileName, ShipItemState.Declaring, 0, f.SizeBytes, "Declaring the file to HCG Store…"));
                    var body = new StoreArtifactRequest
                    {
                        FileName = f.FileName, Platform = f.Platform!, Format = f.Format!, SizeBytes = f.SizeBytes, Sha256 = f.Sha256,
                        Architectures = f.Architectures, MinimumOs = f.MinimumOs, Signing = DeclaredSigning(f), Package = f.Package, Kind = f.Kind,
                        SourceCommit = f.Provenance?.SourceCommit, BuildProvider = f.Provenance?.BuildProvider, Toolchain = f.Provenance?.Toolchain,
                        ValidationReport = f.Provenance?.ValidationReport,
                    };
                    StoreDeclaredArtifact declared;
                    try { declared = await client.DeclareArtifactAsync(release.Id, body, ct).ConfigureAwait(false); }
                    catch (HcgStoreException ex) when (RejectsOnlyProvenance(ex))
                    {
                        // The deployed HCG Store's strict schema predates build provenance (Ship to Store v2). The file is declared
                        // without it, and its Release Passport keeps it. Nothing else is dropped: any other refusal still fails.
                        Report(new(f.FileName, ShipItemState.Declaring, 0, f.SizeBytes,
                            "This HCG Store does not accept build provenance yet (source commit, provider, toolchain, validation report): declared without it; the Release Passport keeps it."));
                        declared = await client.DeclareArtifactAsync(release.Id, body with { SourceCommit = null, BuildProvider = null, Toolchain = null, ValidationReport = null }, ct).ConfigureAwait(false);
                    }
                    artifactId = declared.Artifact.Id;
                    url = declared.Upload;
                }
                await client.UploadAsync(artifactId, f.Path, url,
                    new Progress<StoreUploadProgress>(p => Report(new(f.FileName, ShipItemState.Uploading, p.Received, p.Total, p.Note, artifactId))), ct).ConfigureAwait(false);
                Report(new(f.FileName, ShipItemState.Verifying, f.SizeBytes, f.SizeBytes, "HCG Store is checking the SHA-256 and the file's format…", artifactId));
                var done = await client.CompleteAsync(artifactId, ct).ConfigureAwait(false);
                var final = done.Artifact.Status == "verified"
                    ? new ShipItemProgress(f.FileName, ShipItemState.Verified, f.SizeBytes, f.SizeBytes,
                        done.Artifact.Sha256 == f.Sha256 ? $"Verified by HCG Store: SHA-256 {done.Artifact.Sha256} matches." : "Verified, but the SHA-256 differs from this computer's — check the file.", artifactId, done.Artifact.Sha256)
                    : new ShipItemProgress(f.FileName, ShipItemState.Rejected, f.SizeBytes, f.SizeBytes, done.Message ?? "Rejected by HCG Store.", artifactId, done.Artifact.Sha256);
                if (final.State == ShipItemState.Verified)
                {
                    var (passport, note) = await AttachPassportAsync(artifactId, f, ct).ConfigureAwait(false);
                    final = final with { Passport = passport, Detail = final.Detail + (note is null ? "" : " " + note) };
                }
                items.Add(final); Report(final);
            }
            catch (HcgStoreException ex)
            {
                var failed = new ShipItemProgress(f.FileName, ShipItemState.Failed, 0, f.SizeBytes, ex.Message, existing?.Id);
                items.Add(failed); Report(failed);
            }
        }
        return new ShipResult(await client.ReleaseAsync(release.Id, ct).ConfigureAwait(false), items) { ContinuedExistingDraft = release.Reused == true || existingReleaseId is not null };
    }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Attaches the file's HCG Release Passport to the verified file, so reviewers see the build evidence. Only the passport
    /// that provenance accepted for these exact bytes is used (never one re-discovered after it was rejected), and only when
    /// it names this file and its SHA-256. HCG Store checks name, size, SHA-256 and SHA-512 again. The passport is sent as
    /// it is, except that a missing SHA-512 is filled in from the file (a stated SHA-512 that differs is refused here). It
    /// never fails the upload: any problem is reported as a note.
    /// </summary>
    private async Task<(string Passport, string? Note)> AttachPassportAsync(string artifactId, StoreArtifactCandidate f, CancellationToken ct)
    {
        try
        {
            var path = f.Provenance?.PassportPath;
            if (path is null || !File.Exists(path)) return ("not-found", "No Release Passport for these bytes.");
            if (System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) is not System.Text.Json.Nodes.JsonObject passport
                || passport["artifact"] is not System.Text.Json.Nodes.JsonObject art)
                return ("refused", "The Release Passport is not an object with an artifact section; not attached.");
            static string? Text(System.Text.Json.Nodes.JsonNode? n) => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
            var named = Text(art["filename"]);
            var sha = Text(art["sha256"]);
            if (named != f.FileName || sha != f.Sha256)
                return ("refused", $"The Release Passport describes {named ?? "another file"}{(sha == f.Sha256 ? "" : " with a different SHA-256")}; not attached.");
            string localSha512;
            await using (var stream = File.OpenRead(f.Path))
                localSha512 = Convert.ToHexStringLower(await System.Security.Cryptography.SHA512.HashDataAsync(stream, ct).ConfigureAwait(false));
            var stated = Text(art["sha512"]);
            if (stated is not null && !string.Equals(stated, localSha512, StringComparison.OrdinalIgnoreCase))
                return ("refused", "The Release Passport states a SHA-512 that differs from the file's; not attached.");
            var body = passport.DeepClone().AsObject();
            body["artifact"]!["sha512"] = localSha512;
            await client.AttachPassportAsync(artifactId, body, ct).ConfigureAwait(false);
            return ("attached", "Release Passport attached (HCG Store matched it to the stored bytes" + (stated is null ? "; SHA-512 computed by Executable Maker from the file" : "") + ").");
        }
        catch (HcgStoreException ex) when (ex.Status is 404 or 405) { return ("unsupported", "This HCG Store does not take Release Passports yet; the passport stays with the file."); }
        catch (HcgStoreException ex) { return ("refused", $"HCG Store did not attach the Release Passport: {ex.Message}"); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ("refused", $"The Release Passport could not be attached ({ex.Message})."); }
    }

    private static readonly string[] ProvenanceFields = ["sourceCommit", "buildProvider", "toolchain", "validationReport"];

    /// <summary>
    /// HCG Store's answer when its schema has no build provenance yet (the deployed 835ac1a server):
    /// 400 invalid_request "request: Unrecognized keys: \"sourceCommit\", …" — and no other field is refused.
    /// </summary>
    public static bool RejectsOnlyProvenance(HcgStoreException ex)
    {
        if (ex.Status != 400 || ex.Code != "invalid_request") return false;
        var m = System.Text.RegularExpressions.Regex.Match(ex.Message, "^request: Unrecognized keys?: (?<keys>(\"[A-Za-z]+\"(, )?)+)$");
        if (!m.Success) return false;
        var keys = System.Text.RegularExpressions.Regex.Matches(m.Groups["keys"].Value, "\"([A-Za-z]+)\"").Select(k => k.Groups[1].Value).ToList();
        return keys.Count > 0 && keys.All(k => ProvenanceFields.Contains(k));
    }

    /// <summary>What is reported to HCG Store about signing: the platform tool's result, or "unsigned" when the file
    /// carries no signature structure at all (a fact, not an assumption). Never upgraded beyond what was checked.</summary>
    public static StoreSigningReport DeclaredSigning(StoreArtifactCandidate f) =>
        f.Signing.Status == "not-checked" && f.SignatureStructure == "absent"
            ? new("unsigned", "No signature structure in the file (checked by HCG Executable Maker).", "HCG Executable Maker")
            : f.Signing;
}
