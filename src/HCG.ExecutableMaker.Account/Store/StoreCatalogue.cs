namespace HCG.ExecutableMaker.Account.Store;

/// <summary>
/// The products HCG lets this account manage, with the official metadata HCG Store states. A Store with the
/// productMetadata capability sends it in the product list; for an older Store, the summary, development status, platforms
/// and product page come from its public release resolution. Nothing is filled in by Maker, and the catalogue is never
/// kept as a permanent local copy: Refresh asks HCG Store again.
/// </summary>
public static class StoreCatalogue
{
    public static async Task<IReadOnlyList<StoreProduct>> EnrichAsync(HcgStoreClient client, IReadOnlyList<StoreProduct> products, StoreCapabilityFlags? flags, CancellationToken ct = default)
    {
        if (flags?.ProductMetadata == true) return products;
        var tasks = products.Select(async p =>
        {
            if (p.Source != "catalogue" || p.Summary is not null) return p;
            StoreResolution? r;
            try { r = await client.ResolutionAsync(p.Slug ?? p.Id, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { r = null; }
            if (r?.Product is not { } rp) return p;
            return p with
            {
                Slug = p.Slug ?? rp.Slug, Summary = rp.Summary, DevelopmentStatus = p.DevelopmentStatus ?? rp.DevelopmentStatus,
                Platforms = p.Platforms.Count > 0 ? p.Platforms : rp.Platforms, ProductUrl = p.ProductUrl ?? rp.ProductUrl,
            };
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Products matching a search (name, id, summary, category) and filters. Empty filters match everything.</summary>
    public static IReadOnlyList<StoreProduct> Filter(IEnumerable<StoreProduct> products, string? search, string? status, string? platform) =>
        products.Where(p =>
            (string.IsNullOrWhiteSpace(search) || new[] { p.Title, p.Id, p.Summary, p.Category }.Any(f => f?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true)) &&
            (string.IsNullOrWhiteSpace(status) || string.Equals(p.DevelopmentStatus ?? p.Source, status, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(platform) || p.Platforms.Any(x => x.Platform == platform) || p.Editions.Any(e => e.Platforms.Contains(platform))))
        .ToList();

    /// <summary>The platforms a product names (product platforms, else its editions'), distinct and in a stable order.</summary>
    public static IReadOnlyList<string> PlatformsOf(StoreProduct p) =>
        (p.Platforms.Count > 0 ? p.Platforms.Select(x => x.Platform) : p.Editions.SelectMany(e => e.Platforms)).Distinct().ToList();

    /// <summary>An icon URL is used only when it is on the Store's own origin (never a third-party host).</summary>
    public static Uri? IconUri(StoreProduct p, Uri storeOrigin) =>
        p.Icon is { } s && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == storeOrigin.Scheme && u.Host == storeOrigin.Host && u.Port == storeOrigin.Port ? u : null;
}
