using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging;

public sealed class PackagingRegistry(IEnumerable<IPackager> packagers)
{
    private readonly Dictionary<PackagingFormat, IPackager> _byFormat = packagers.ToDictionary(p => p.Format);

    public IPackager? Get(PackagingFormat format) => _byFormat.GetValueOrDefault(format);
    public IReadOnlyCollection<PackagingFormat> ImplementedFormats => _byFormat.Keys;
}
