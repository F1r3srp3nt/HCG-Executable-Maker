namespace HCG.ExecutableMaker.Domain;

/// <summary>A macOS application target in an Xcode project, with the settings xcodebuild will use (Release values preferred).</summary>
public sealed record MacXcodeTargetInfo
{
    public required string Name { get; init; }
    public required string ProductType { get; init; }
    /// <summary>PRODUCT_NAME with $(TARGET_NAME) resolved: the bundle is PRODUCT_NAME.app.</summary>
    public string? ProductName { get; init; }
    public string? BundleIdentifier { get; init; }
    /// <summary>MACOSX_DEPLOYMENT_TARGET (target, else project level).</summary>
    public string? DeploymentTarget { get; init; }
    public string? MarketingVersion { get; init; }
    public string? BuildVersion { get; init; }
    /// <summary>CFBundleDisplayName / CFBundleName from the project's Info.plist or INFOPLIST_KEY_ settings (literal values only).</summary>
    public string? DisplayName { get; init; }
    public string? InfoPlistFile { get; init; }
    /// <summary>CODE_SIGN_IDENTITY as the project sets it ("-" = ad hoc, "Apple Development", "Developer ID Application", …).</summary>
    public string? CodeSignIdentity { get; init; }
    public string? CodeSignStyle { get; init; }
    public string? DevelopmentTeam { get; init; }
    /// <summary>ARCHS as the project sets it (null = Xcode's default).</summary>
    public string? Architectures { get; init; }
    /// <summary>Frameworks the target links in its Frameworks phase (AVFoundation, ServiceManagement, …).</summary>
    public IReadOnlyList<string> Frameworks { get; init; } = [];
    public string? Entitlements { get; init; }
    public bool HardenedRuntime { get; init; }
    /// <summary>ENABLE_APP_SANDBOX (Xcode 15+ build setting; Xcode generates the entitlement), null when not set.</summary>
    public bool? AppSandbox { get; init; }
    /// <summary>GENERATE_INFOPLIST_FILE = YES: Xcode writes Info.plist from INFOPLIST_KEY_* settings (no file in the project).</summary>
    public bool GeneratedInfoPlist { get; init; }
    /// <summary>Swift package products the target links (XCSwiftPackageProductDependency).</summary>
    public IReadOnlyList<string> PackageProducts { get; init; } = [];
    /// <summary>Folders Xcode 16 keeps in sync with the file system (PBXFileSystemSynchronizedRootGroup), relative to the project folder.</summary>
    public IReadOnlyList<string> SynchronizedFolders { get; init; } = [];
    /// <summary>The target's settings in every build configuration, as the project file sets them (project level, then target).</summary>
    public IReadOnlyList<MacXcodeConfigurationFacts> ConfigurationFacts { get; init; } = [];
    public bool IsApplication => ProductType == "com.apple.product-type.application";

    /// <summary>How the project signs: what a person sees before building.</summary>
    public string SigningDescription => CodeSignIdentity switch
    {
        "-" => "ad hoc (CODE_SIGN_IDENTITY \"-\")" + (DevelopmentTeam is null ? ", no development team" : $", team {DevelopmentTeam}"),
        null or "" => CodeSignStyle == "Automatic" ? $"automatic signing{(DevelopmentTeam is null ? " (no team set)" : $", team {DevelopmentTeam}")}" : "Xcode's default",
        var id => $"\"{id}\"{(DevelopmentTeam is null ? "" : $", team {DevelopmentTeam}")}{(CodeSignStyle is { } s ? $" ({s.ToLowerInvariant()})" : "")}",
    };
}

/// <summary>
/// One build configuration of a target as the project file states it. Debug and Release often differ in what matters for
/// distribution: bundle ID, App Sandbox, entitlements, compilation conditions and signing.
/// </summary>
public sealed record MacXcodeConfigurationFacts
{
    public required string Name { get; init; }
    public string? BundleIdentifier { get; init; }
    public string? CodeSignIdentity { get; init; }
    public string? CodeSignStyle { get; init; }
    public string? DevelopmentTeam { get; init; }
    public string? Entitlements { get; init; }
    public bool? AppSandbox { get; init; }
    public bool HardenedRuntime { get; init; }
    /// <summary>SWIFT_ACTIVE_COMPILATION_CONDITIONS without $(inherited) (e.g. DEBUG, feature flags).</summary>
    public IReadOnlyList<string> CompilationConditions { get; init; } = [];
    /// <summary>ENABLE_USER_SELECTED_FILES etc.: sandbox resource access Xcode turns into entitlements.</summary>
    public IReadOnlyDictionary<string, string> SandboxAccess { get; init; } = new Dictionary<string, string>();
    public string? Architectures { get; init; }
    public bool GeneratedInfoPlist { get; init; }
    public string? InfoPlistFile { get; init; }
}

/// <summary>A Swift package the Xcode project depends on.</summary>
/// <param name="Kind">"local" (XCLocalSwiftPackageReference: a folder in the source) or "remote" (XCRemoteSwiftPackageReference: a URL).</param>
/// <param name="Location">The relative path (local) or repository URL (remote).</param>
/// <param name="Requirement">A remote package's version rule (e.g. "upToNextMajorVersion 1.2.0"), null for local packages.</param>
/// <param name="ManifestPath">The local package's Package.swift, relative to the workspace source; null when it is missing.</param>
public sealed record MacXcodePackageReference(string Kind, string Location, string? Requirement, string? ManifestPath)
{
    public bool IsLocal => Kind == "local";
}

/// <summary>A pinned package from Package.resolved (the exact version or revision the project last resolved).</summary>
public sealed record MacXcodePackagePin(string Identity, string Location, string? Version, string? Revision);

/// <summary>A macOS-only Xcode project found in the workspace (static analysis; xcodebuild is never run to inspect).</summary>
public sealed record MacXcodeProjectInfo
{
    /// <summary>The .xcodeproj, relative to the workspace source (may include an enclosing folder such as "App-master/").</summary>
    public required string ProjectPath { get; init; }
    /// <summary>A sibling .xcworkspace that references the project (built instead of the project when present).</summary>
    public string? WorkspacePath { get; init; }
    public IReadOnlyList<IosSchemeInfo> Schemes { get; init; } = [];
    public IReadOnlyList<MacXcodeTargetInfo> Targets { get; init; } = [];
    public IReadOnlyList<string> Configurations { get; init; } = [];
    public bool UsesSwiftPackages { get; init; }
    public bool UsesCocoaPods { get; init; }
    /// <summary>"Swift", "Objective-C", "C/C++" — by the sources next to the project.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];
    /// <summary>"AppKit", "SwiftUI" — by the imports in the sources.</summary>
    public IReadOnlyList<string> UiFrameworks { get; init; } = [];
    /// <summary>Licence and attribution files next to the project (LICENSE, COPYING, NOTICE, …), relative paths.</summary>
    public IReadOnlyList<string> LicenseFiles { get; init; } = [];
    /// <summary>Scripts that ship with the project (e.g. INSTALL.command). HCG never runs them; it calls xcodebuild itself.</summary>
    public IReadOnlyList<string> HelperScripts { get; init; } = [];
    /// <summary>The asset catalog's app icon set, when present.</summary>
    public string? AppIconSet { get; init; }
    /// <summary>Asset catalogs (.xcassets) next to the project, relative paths.</summary>
    public IReadOnlyList<string> AssetCatalogs { get; init; } = [];
    /// <summary>App icon images the icon set names and the folder actually contains (empty: an icon set with no images).</summary>
    public IReadOnlyList<string> AppIconImages { get; init; } = [];
    /// <summary>Swift packages the project references (local folders and remote repositories).</summary>
    public IReadOnlyList<MacXcodePackageReference> PackageReferences { get; init; } = [];
    /// <summary>Package.resolved pins (the workspace's shared copy), when committed.</summary>
    public IReadOnlyList<MacXcodePackagePin> ResolvedPackages { get; init; } = [];
    /// <summary>Entitlements files next to the project with their keys (path → keys).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EntitlementFiles { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>project.pbxproj objectVersion (77 = Xcode 16 format with synchronized folders).</summary>
    public int? ObjectVersion { get; init; }
    public string? ParseError { get; init; }
    public string Directory => ProjectPath.Contains('/') ? ProjectPath[..ProjectPath.LastIndexOf('/')] : "";
    public string Container => WorkspacePath ?? ProjectPath;
    public IReadOnlyList<MacXcodeTargetInfo> AppTargets => Targets.Where(t => t.IsApplication).ToList();
}

/// <summary>Native macOS apps built with Xcode, found in the workspace.</summary>
public sealed record MacXcodeAnalysis
{
    public IReadOnlyList<MacXcodeProjectInfo> Projects { get; init; } = [];
}

public enum MacXcodeArchitecture
{
    /// <summary>arm64 only (Apple silicon).</summary>
    AppleSilicon,
    /// <summary>arm64 and x86_64 in one binary.</summary>
    Universal,
    /// <summary>x86_64 only.</summary>
    Intel,
}

public static class MacXcodeArchitectureNames
{
    public static string Archs(this MacXcodeArchitecture a) => a switch
    {
        MacXcodeArchitecture.Universal => "arm64 x86_64",
        MacXcodeArchitecture.Intel => "x86_64",
        _ => "arm64",
    };

    public static string Label(this MacXcodeArchitecture a) => a switch
    {
        MacXcodeArchitecture.Universal => "Universal (Apple silicon + Intel)",
        MacXcodeArchitecture.Intel => "Intel (x86_64)",
        _ => "Apple silicon (arm64)",
    };

    public static TargetArchitecture ToTarget(this MacXcodeArchitecture a) => a switch
    {
        MacXcodeArchitecture.Universal => TargetArchitecture.Universal,
        MacXcodeArchitecture.Intel => TargetArchitecture.X64,
        _ => TargetArchitecture.Arm64,
    };
}

/// <summary>Xcode build settings for a native macOS app, persisted with the build record.</summary>
public sealed record MacXcodeBuildOptions
{
    /// <summary>The .xcodeproj, relative to the workspace source.</summary>
    public required string ProjectPath { get; init; }
    /// <summary>Build this .xcworkspace instead of the project (relative), when the project lives in one.</summary>
    public string? WorkspacePath { get; init; }
    public required string Scheme { get; init; }
    /// <summary>The build configuration exactly as the project names it (usually Release).</summary>
    public required string Configuration { get; init; }
    public MacXcodeArchitecture Architecture { get; init; } = MacXcodeArchitecture.AppleSilicon;
    /// <summary>The application target the scheme builds (its product is the .app HCG packages).</summary>
    public required string AppTarget { get; init; }
    /// <summary>Expected bundle name without .app (PRODUCT_NAME); confirmed against xcodebuild's own settings.</summary>
    public string? ProductName { get; init; }
    public string? BundleIdentifier { get; init; }
    public string? DisplayName { get; init; }
    public string? DeploymentTarget { get; init; }
    /// <summary>
    /// Visible override for projects that sign with a certificate this Mac may not have (Apple Development, a team):
    /// sign ad hoc to run locally instead. Off by default: the project's own signing is kept.
    /// </summary>
    public bool SignLocallyAdHoc { get; init; }
    /// <summary>Licence/attribution files (relative to the source) shipped next to the app and inside the DMG.</summary>
    public IReadOnlyList<string> LicenseFiles { get; init; } = [];
    /// <summary>Run the scheme's test action (xcodebuild test) after the build. A failed test fails the build.</summary>
    public bool RunTests { get; init; }
    /// <summary>Also produce an .xcarchive (xcodebuild archive) of the same scheme and configuration.</summary>
    public bool CreateArchive { get; init; }
}
