using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging;

public sealed class PackagingRegistry(IEnumerable<IPackager> packagers)
{
    private readonly IReadOnlyList<IPackager> _all = packagers.ToList();

    /// <summary>The first registered packager for <paramref name="format"/> (format-only lookup, e.g. for .NET builds).</summary>
    public IPackager? Get(PackagingFormat format) => _all.FirstOrDefault(p => p.Format == format);

    /// <summary>The packager that produces <paramref name="format"/> for these settings, or null when none handles them.</summary>
    public IPackager? Get(PackagingFormat format, BuildSettings settings) => _all.FirstOrDefault(p => p.Format == format && p.Handles(settings));

    public IReadOnlyCollection<PackagingFormat> ImplementedFormats => _all.Select(p => p.Format).Distinct().ToList();
}
