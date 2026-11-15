using HCG.ExecutableMaker.Account.Store;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

public enum ShipItemState { Waiting, Declaring, Uploading, Verifying, Verified, Rejected, Failed, Skipped }

public sealed record ShipItemProgress(string FileName, ShipItemState State, long Received, long Total, string? Detail, string? ArtifactId = null, string? Sha256 = null);

public sealed record ShipResult(StoreRelease Release, IReadOnlyList<ShipItemProgress> Items)
{
    public bool AllVerified => Items.Count > 0 && Items.All(i => i.State is ShipItemState.Verified or ShipItemState.Skipped);
}

/// <summary>
/// Ship to Store: create (or continue) a private draft release in HCG Store and upload each file to it, resuming
/// interrupted uploads and recovering from rejected ones. Used by the Ship to Store page and by
/// <c>HCGExecutableMaker --ship-to-store</c>. It never submits, approves or publishes on its own.
/// </summary>
public sealed class ShipToStoreService(HcgStoreClient client)
{
    /// <param name="existingReleaseId">Continue this draft (Retry after a failure) instead of creating a new release.</param>
    public async Task<ShipResult> ShipAsync(StoreReleaseRequest request, IReadOnlyList<StoreArtifactCandidate> files, string? existingReleaseId,
        IProgress<ShipItemProgress>? progress, CancellationToken ct)
    {
        if (files.Count == 0) throw new InvalidOperationException("Add at least one file.");
        if (files.FirstOrDefault(f => !f.IsSupported) is { } bad) throw new InvalidOperationException($"{bad.FileName}: {string.Join(" ", bad.Problems)}");
        var release = existingReleaseId is null
            ? await client.CreateReleaseAsync(request, ct).ConfigureAwait(false)
            : await client.ReleaseAsync(existingReleaseId, ct).ConfigureAwait(false);
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
                        Architectures = f.Architectures, MinimumOs = f.MinimumOs, Signing = f.Signing, Package = f.Package,
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
        return new ShipResult(await client.ReleaseAsync(release.Id, ct).ConfigureAwait(false), items);
    }
}
