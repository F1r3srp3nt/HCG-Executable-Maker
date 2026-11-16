using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

public enum ShipItemState { Waiting, Declaring, Uploading, Verifying, Verified, Rejected, Failed, Skipped }

public sealed record ShipItemProgress(string FileName, ShipItemState State, long Received, long Total, string? Detail, string? ArtifactId = null, string? Sha256 = null);

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
                    var declared = await client.DeclareArtifactAsync(release.Id, new StoreArtifactRequest
                    {
                        FileName = f.FileName, Platform = f.Platform!, Format = f.Format!, SizeBytes = f.SizeBytes, Sha256 = f.Sha256,
                        Architectures = f.Architectures, MinimumOs = f.MinimumOs, Signing = DeclaredSigning(f), Package = f.Package, Kind = f.Kind,
                    }, ct).ConfigureAwait(false);
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

    /// <summary>What is reported to HCG Store about signing: the platform tool's result, or "unsigned" when the file
    /// carries no signature structure at all (a fact, not an assumption). Never upgraded beyond what was checked.</summary>
    public static StoreSigningReport DeclaredSigning(StoreArtifactCandidate f) =>
        f.Signing.Status == "not-checked" && f.SignatureStructure == "absent"
            ? new("unsigned", "No signature structure in the file (checked by HCG Executable Maker).", "HCG Executable Maker")
            : f.Signing;
}
