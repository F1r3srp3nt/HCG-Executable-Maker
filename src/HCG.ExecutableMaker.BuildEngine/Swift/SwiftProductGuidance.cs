using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Swift;

/// <summary>
/// Which executable product of a Swift package is its app, and what to tell the person about the choice. A package can
/// have a SwiftUI app and command-line tools side by side (HCG Store Manager: HCGStoreManager and hcg-store-manager);
/// building the tool gives a folder or ZIP, not an app. A packaging contract (AppPackaging.json) names the product it
/// packages, so that product is preselected.
/// </summary>
public static class SwiftProductGuidance
{
    public sealed record Product(string Name, SwiftAppKind Kind);

    /// <summary>The product to preselect: the only one, or the one the packaging contract names. Otherwise none.</summary>
    public static string? Preselect(IReadOnlyCollection<Product> products, SwiftPackagingContract? contract) =>
        products.Count == 1 ? products.First().Name
        : contract is not null && products.Any(p => p.Name == contract.Product) ? contract.Product
        : null;

    public static string? Note(IReadOnlyCollection<Product> products, string? selected, SwiftPackagingContract? contract)
    {
        var apps = products.Where(p => p.Kind == SwiftAppKind.GuiApp).Select(p => p.Name).ToList();
        var chosen = products.FirstOrDefault(p => p.Name == selected);
        if (chosen is null)
            return products.Count > 1 && apps.Count > 0
                ? $"This package has {products.Count} executable products. The macOS app is {string.Join(" or ", apps)}; the others are command-line tools, which HCG builds as a folder or ZIP, not as an app."
                : null;
        if (contract is not null && contract.Product == chosen.Name)
            return $"Selected by the package's AppPackaging.json, which describes how {chosen.Name} is packaged (icon, Info.plist, resources{(contract.Helpers.Count > 0 ? ", helpers" : "")}).";
        if (chosen.Kind == SwiftAppKind.CommandLine && apps.Count > 0)
            return $"{chosen.Name} is a command-line tool: the result is the executable in a folder or ZIP, not a macOS app. The app of this package is {string.Join(" or ", apps)}" +
                   (contract is not null && apps.Contains(contract.Product) ? " (named by its AppPackaging.json)." : ".");
        if (contract is not null && contract.Product != chosen.Name)
            return $"The package's AppPackaging.json describes {contract.Product}, not {chosen.Name}: its icon, Info.plist and resources are not applied to this product.";
        return null;
    }
}
