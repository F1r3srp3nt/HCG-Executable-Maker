using HCG.ExecutableMaker.BuildEngine.Swift;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// HCG Store Manager's package shape (tests/Fixtures/StoreShapedApp): a library, a command-line tool and a SwiftUI app,
/// with an AppPackaging.json that names the app and brings its icon, Info.plist template and artwork.
/// </summary>
public class StoreShapedPackageTests
{
    private static string Fixture => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "StoreShapedApp");
    private static readonly SwiftProductGuidance.Product App = new("ShapeManager", SwiftAppKind.GuiApp);
    private static readonly SwiftProductGuidance.Product Tool = new("shape-tool", SwiftAppKind.CommandLine);

    [Fact]
    public void The_package_is_found_with_its_app_and_tool_and_the_contract_names_the_app()
    {
        var analysis = new SwiftProjectInspector(TimeProvider.System).Inspect(SourceIndex.Build(Fixture), null);
        var pkg = Assert.Single(analysis.Swift!.Packages);
        Assert.Equal("5.10", pkg.ToolsVersion);
        Assert.Equal("14.0", pkg.MinimumMacOSHint);
        Assert.True(pkg.HasPackageDependencies);
        Assert.Equal(SwiftAppKind.GuiApp, pkg.SourceFacts.Single(f => f.Target == "ShapeManager").SuggestedKind);
        Assert.Equal(SwiftAppKind.CommandLine, pkg.SourceFacts.Single(f => f.Target == "shape-tool").SuggestedKind);

        var (contract, error) = SwiftPackagingContractReader.Read(Fixture, "");
        Assert.Null(error);
        Assert.Equal("ShapeManager", contract!.Product);
        Assert.Equal("Assets/AppIcon.icns", contract.Bundle.Icon);
        Assert.Equal("Packaging/Info.plist", contract.Bundle.InfoPlist);
        Assert.Equal("14.0", contract.Bundle.MinimumMacOS);
        Assert.Empty(contract.Prebuild); // nothing to confirm or run
        Assert.Empty(contract.Helpers);
        var r = Assert.Single(contract.Resources);
        Assert.Equal("Contents/Resources/Brand-512.png", r.Destination);
        Assert.True(r.Required);
        Assert.True(File.Exists(Path.Combine(Fixture, "Assets", "AppIcon.icns")));
        Assert.Equal("icns"u8.ToArray(), File.ReadAllBytes(Path.Combine(Fixture, "Assets", "AppIcon.icns"))[..4]);
    }

    [Fact]
    public void The_contracts_app_is_preselected_never_the_command_line_tool()
    {
        var (contract, _) = SwiftPackagingContractReader.Read(Fixture, "");
        Assert.Equal("ShapeManager", SwiftProductGuidance.Preselect([Tool, App], contract));
        Assert.Null(SwiftProductGuidance.Preselect([Tool, App], null)); // without a contract the person chooses
        Assert.Equal("shape-tool", SwiftProductGuidance.Preselect([Tool], null));
        Assert.Contains("Selected by the package's AppPackaging.json", SwiftProductGuidance.Note([Tool, App], "ShapeManager", contract));
    }

    [Fact]
    public void Choosing_the_tool_next_to_an_app_says_it_is_not_the_app()
    {
        var (contract, _) = SwiftPackagingContractReader.Read(Fixture, "");
        var note = SwiftProductGuidance.Note([Tool, App], "shape-tool", contract)!;
        Assert.StartsWith("shape-tool is a command-line tool", note, StringComparison.Ordinal);
        Assert.Contains("The app of this package is ShapeManager (named by its AppPackaging.json)", note, StringComparison.Ordinal);
        Assert.Contains("The macOS app is ShapeManager", SwiftProductGuidance.Note([Tool, App], null, null));
        Assert.Null(SwiftProductGuidance.Note([Tool], "shape-tool", null)); // a CLI-only package needs no warning
        var other = contract! with { Product = "Other" };
        Assert.Contains("describes Other, not ShapeManager", SwiftProductGuidance.Note([Tool, App], "ShapeManager", other));
    }
}
