using System.IO.Compression;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Real-world Swift working copies (after the projects' own build scripts and Xcode have run) go through the same
/// importer and inspector set as the app, and must be recognised as Swift packages — not as "unsupported".
/// </summary>
public class SwiftWorkingCopyTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    /// <summary>The inspector set registered in the app (Composition.cs), in the same order.</summary>
    private static ProjectInspectionService AppInspection() => new(
        new IProjectInspector[]
        {
            new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System),
            new MobileArtifactProjectInspector(TimeProvider.System), new SwiftProjectInspector(TimeProvider.System),
        }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    // Distil as of Oslo-Sprint b5d8697: executable product renamed to Distil, target still TokenLabApp.
    private const string DistilManifest = """
        // swift-tools-version: 5.9
        import PackageDescription
        var products: [Product] = [.library(name: "TokenLabCore", targets: ["TokenLabCore"])]
        var targets: [Target] = [
            .target(name: "TokenLabCore"),
            .testTarget(name: "TokenLabCoreTests", dependencies: ["TokenLabCore"])
        ]
        #if os(macOS)
        products.append(.executable(name: "Distil", targets: ["TokenLabApp"]))
        targets.append(.executableTarget(name: "TokenLabApp", dependencies: ["TokenLabCore"]))
        #endif
        let package = Package(name: "TokenLab", platforms: [.macOS(.v14)], products: products, targets: targets)
        """;

    private void WriteDistilWorkingCopy(string dir)
    {
        _t.Write($"{dir}/Package.swift", DistilManifest);
        _t.Write($"{dir}/Sources/TokenLabApp/TokenLabApp.swift", "import SwiftUI\n@main struct TokenLabApp: App { var body: some Scene { WindowGroup { Text(\"Distil\") } } }\n");
        _t.Write($"{dir}/Sources/TokenLabCore/Core.swift", "public struct Core {}\n");
        _t.Write($"{dir}/Tests/TokenLabCoreTests/CoreTests.swift", "import XCTest\n");
        _t.Write($"{dir}/scripts/build-macos.sh", "#!/bin/bash\nswift build -c release --product Distil\n");
        // Left behind by `swift build` and the project's own packaging script:
        for (var i = 0; i < 40; i++) _t.Write($"{dir}/.build/arm64-apple-macosx/release/TokenLabCore.build/f{i}.o", "obj");
        _t.Write($"{dir}/.build/checkouts/swift-collections/Package.swift", "// swift-tools-version: 5.7\nimport PackageDescription\n");
        _t.Write($"{dir}/.build/arm64-apple-macosx/release/Distil", "binary");
        _t.Write($"{dir}/dist/Distil.app/Contents/Info.plist", "<plist version=\"1.0\"><dict><key>CFBundleExecutable</key><string>Distil</string></dict></plist>");
        Directory.CreateDirectory(_t.Combine(dir, "dist", "Distil.app", "Contents", "MacOS"));
        File.WriteAllBytes(_t.Combine(dir, "dist", "Distil.app", "Contents", "MacOS", "Distil"), SwiftTests.ThinMachO(0x0100000C, 0x000E0000));
        // Opening Package.swift in Xcode creates this workspace:
        _t.Write($"{dir}/.swiftpm/xcode/package.xcworkspace/contents.xcworkspacedata", "<?xml version=\"1.0\"?><Workspace version=\"1.0\"><FileRef location=\"self:\"></FileRef></Workspace>");
        _t.Write($"{dir}/.swiftpm/xcode/xcshareddata/xcschemes/Distil.xcscheme", "<Scheme/>");
    }

    private async Task<(ImportResult Import, ProjectAnalysis Analysis)> ImportFolder(string dir)
    {
        var paths = new AppPaths(_t.Combine("hcg-data"));
        paths.EnsureCreated();
        var import = await new ProjectImporter(paths, TimeProvider.System).ImportDirectoryAsync(_t.Combine(dir), new ImportLimits(), null, CancellationToken.None);
        var analysis = await AppInspection().InspectAsync(import.Project.SourceDirectory, SwiftTests.MacReport(), CancellationToken.None);
        return (import, analysis);
    }

    [Fact]
    public async Task Built_Distil_folder_is_a_Swift_package_with_its_renamed_product()
    {
        WriteDistilWorkingCopy("distil");
        var (import, a) = await ImportFolder("distil");
        Assert.Equal("swift", a.InspectorId);
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.DoesNotContain("planned", a.SupportSummary, StringComparison.OrdinalIgnoreCase);
        var package = Assert.Single(a.Swift!.Packages);
        Assert.Equal("", package.Directory);
        Assert.Contains("Distil", package.ExecutableProductHints);
        var project = Assert.Single(a.Projects);
        Assert.True(project.IsRunnableApplication);
        Assert.Equal("Package.swift", a.PrimaryProjectPath);
        Assert.Empty(a.Swift.MacXcodeProjects); // .swiftpm's Xcode workspace is not a separate Xcode project
        // Generated folders are not copied into the workspace.
        Assert.False(Directory.Exists(Path.Combine(import.Project.SourceDirectory, ".build")));
        Assert.False(Directory.Exists(Path.Combine(import.Project.SourceDirectory, ".swiftpm")));
        Assert.Contains(import.Warnings, w => w.Contains(".build", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parent_repository_lists_both_packages_and_selects_none()
    {
        WriteDistilWorkingCopy("Oslo-Sprint/distil");
        _t.Write("Oslo-Sprint/relay/Package.swift", "// swift-tools-version: 5.9\nimport PackageDescription\nlet package = Package(name: \"Relay\", platforms: [.macOS(.v14)], targets: [.executableTarget(name: \"RelayApp\")])\n");
        _t.Write("Oslo-Sprint/relay/Sources/RelayApp/RelayApp.swift", "import SwiftUI\n@main struct RelayApp: App { var body: some Scene { WindowGroup { Text(\"Relay\") } } }\n");
        _t.Write("Oslo-Sprint/relay/.build/arm64-apple-macosx/debug/Relay", "binary");
        _t.Write("Oslo-Sprint/README.md", "# Oslo Sprint\n");
        var (_, a) = await ImportFolder("Oslo-Sprint");
        Assert.Equal("swift", a.InspectorId);
        Assert.Equal(["distil/Package.swift", "relay/Package.swift"], a.Swift!.Packages.Select(p => p.ManifestPath).ToArray());
        Assert.Null(a.PrimaryProjectPath);
        Assert.Equal(2, a.Projects.Count);
    }

    [Fact]
    public async Task GitHub_ZIP_with_a_wrapper_folder_and_zipped_build_output()
    {
        WriteDistilWorkingCopy("zipsrc/Oslo-Sprint-main/distil");
        var zip = _t.Combine("Oslo-Sprint-main.zip");
        // A zipped working copy: includes .build (without SwiftPM's symlinks) and .swiftpm, which must be skipped.
        ZipFile.CreateFromDirectory(_t.Combine("zipsrc"), zip);
        var paths = new AppPaths(_t.Combine("hcg-data"));
        paths.EnsureCreated();
        var import = await new ProjectImporter(paths, TimeProvider.System).ImportZipAsync(zip, new ImportLimits(), null, CancellationToken.None);
        Assert.Contains(import.Warnings, w => w.Contains("Skipped generated folders", StringComparison.Ordinal));
        var a = await AppInspection().InspectAsync(import.Project.SourceDirectory, SwiftTests.MacReport(), CancellationToken.None);
        Assert.Equal("swift", a.InspectorId);
        Assert.Equal("Oslo-Sprint-main/distil", Assert.Single(a.Swift!.Packages).Directory);
        Assert.False(Directory.Exists(Path.Combine(import.Project.SourceDirectory, "Oslo-Sprint-main", "distil", ".build")));
    }
}
