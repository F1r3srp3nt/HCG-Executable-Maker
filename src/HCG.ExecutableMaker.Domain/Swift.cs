namespace HCG.ExecutableMaker.Domain;

public enum SwiftProductKind
{
    Executable,
    Library,
    Plugin,
    Macro,
    Other,
}

public enum SwiftTargetKind
{
    Executable,
    Regular,
    Test,
    Plugin,
    Macro,
    System,
    Binary,
    Other,
}

/// <summary>How an executable product is delivered on macOS.</summary>
public enum SwiftAppKind
{
    /// <summary>SwiftUI/AppKit application: packaged as a .app bundle.</summary>
    GuiApp,
    /// <summary>Command-line tool: packaged as an executable in a portable folder/ZIP.</summary>
    CommandLine,
}

/// <summary>Privacy-sensitive API usage found in sources that requires an Info.plist usage description.</summary>
public sealed record SwiftPrivacyUsage(string InfoPlistKey, string Purpose, string Evidence);

/// <summary>Static facts about a target's sources (read as text, never compiled or executed).</summary>
public sealed record SwiftTargetSourceFacts
{
    public required string Target { get; init; }
    /// <summary>Source directory relative to the package directory.</summary>
    public required string Path { get; init; }
    public int SwiftFileCount { get; init; }
    public bool ImportsSwiftUI { get; init; }
    public bool ImportsAppKit { get; init; }
    /// <summary>A SwiftUI <c>@main struct …: App</c> or an AppKit <c>NSApplication</c> entry point.</summary>
    public bool HasGuiEntryPoint { get; init; }
    public IReadOnlyList<SwiftPrivacyUsage> PrivacyUsages { get; init; } = [];
    /// <summary>Sandbox / hardened-runtime evidence (entitlements a feature needs, sandbox blockers).</summary>
    public IReadOnlyList<EntitlementEvidence> EntitlementEvidence { get; init; } = [];
    public bool HasResourceDirectory { get; init; }
    public SwiftAppKind SuggestedKind => HasGuiEntryPoint || ImportsSwiftUI || ImportsAppKit ? SwiftAppKind.GuiApp : SwiftAppKind.CommandLine;
}

public sealed record SwiftProductInfo(string Name, SwiftProductKind Kind, IReadOnlyList<string> Targets);

public sealed record SwiftTargetInfo(string Name, SwiftTargetKind Kind, string? Path, int ResourceCount);

/// <summary>
/// Authoritative package description produced by evaluating Package.swift with <c>swift package dump-package</c>
/// on the build host, after the user trusted the project. Conditional declarations (<c>#if os(macOS)</c>) are resolved.
/// </summary>
public sealed record SwiftPackageManifest
{
    public required string Name { get; init; }
    public string? ToolsVersion { get; init; }
    /// <summary>Minimum macOS from <c>platforms:</c>, e.g. "14.0"; null when the package does not declare one.</summary>
    public string? MinimumMacOS { get; init; }
    public IReadOnlyList<SwiftProductInfo> Products { get; init; } = [];
    public IReadOnlyList<SwiftTargetInfo> Targets { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IEnumerable<SwiftProductInfo> ExecutableProducts => Products.Where(p => p.Kind == SwiftProductKind.Executable);
    public IEnumerable<SwiftProductInfo> LibraryProducts => Products.Where(p => p.Kind == SwiftProductKind.Library);
    public IEnumerable<SwiftTargetInfo> TestTargets => Targets.Where(t => t.Kind == SwiftTargetKind.Test);
}

/// <summary>One Swift package found in the workspace.</summary>
public sealed record SwiftPackageInfo
{
    /// <summary>Package directory relative to the workspace source, '/' separated ("" for the root).</summary>
    public required string Directory { get; init; }
    public required string ManifestPath { get; init; }
    /// <summary>From the <c>// swift-tools-version:</c> comment (static, reliable: SwiftPM reads it the same way).</summary>
    public string? ToolsVersion { get; init; }
    /// <summary>Best-effort static reading of <c>name:</c>; the evaluated manifest is authoritative.</summary>
    public string? NameHint { get; init; }
    public string? MinimumMacOSHint { get; init; }
    /// <summary>The manifest contains compiler conditionals (<c>#if os(…)</c>): products may differ per host.</summary>
    public bool HasConditionalDeclarations { get; init; }
    public bool HasPackageDependencies { get; init; }
    /// <summary>Executable product names seen statically (hint only; may miss or include conditional products).</summary>
    public IReadOnlyList<string> ExecutableProductHints { get; init; } = [];
    public IReadOnlyList<SwiftTargetSourceFacts> SourceFacts { get; init; } = [];
    /// <summary>The package's packaging contract (AppPackaging.json next to Package.swift), read as data.</summary>
    public SwiftPackagingContract? PackagingContract { get; init; }
    /// <summary>Why AppPackaging.json could not be used; set when the file exists but is invalid.</summary>
    public string? PackagingContractError { get; init; }
    /// <summary>Set after trusted evaluation on a Mac.</summary>
    public SwiftPackageManifest? Evaluated { get; init; }
    public DateTimeOffset? EvaluatedAt { get; init; }
    public string? EvaluatedWith { get; init; }
    public string DisplayDirectory => Directory.Length == 0 ? "(workspace root)" : Directory + "/";
}

/// <summary>Swift/Xcode desktop analysis of a workspace.</summary>
public sealed record SwiftAnalysis
{
    public IReadOnlyList<SwiftPackageInfo> Packages { get; init; } = [];
    /// <summary>.xcodeproj/.xcworkspace paths whose targets are macOS-only (not built in this increment).</summary>
    public IReadOnlyList<string> MacXcodeProjects { get; init; } = [];
}

/// <summary>Swift package build settings, persisted with the build record.</summary>
public sealed record SwiftBuildOptions
{
    /// <summary>Package directory relative to the workspace source ("" for the root).</summary>
    public required string PackageDirectory { get; init; }
    public required string Product { get; init; }
    public required SwiftAppKind Kind { get; init; }
    /// <summary>CFBundleVersion (build number). The marketing version comes from <see cref="AppIdentity.Version"/>.</summary>
    public string BuildNumber { get; init; } = "1";
    /// <summary>LSMinimumSystemVersion; never lower than the package's declared minimum.</summary>
    public string? MinimumMacOS { get; init; }
    /// <summary>LSApplicationCategoryType, e.g. public.app-category.developer-tools.</summary>
    public string? Category { get; init; }
    /// <summary>Info.plist privacy usage descriptions, e.g. NSMicrophoneUsageDescription → text.</summary>
    public IReadOnlyDictionary<string, string> UsageDescriptions { get; init; } = new Dictionary<string, string>();
    /// <summary>The package's packaging contract, as confirmed in the wizard (null when the package has none).</summary>
    public SwiftPackagingContract? PackagingContract { get; init; }
    /// <summary>
    /// The user confirmed that the contract's prebuild commands may run. The contract file is re-read at build time and
    /// must still match <see cref="SwiftPackagingContract.Sha256"/>.
    /// </summary>
    public bool PackagingContractConfirmed { get; init; }
}

/// <summary>
/// A Swift package's packaging contract (<c>AppPackaging.json</c>, schema <c>hcg-app-packaging/1</c>): what a complete
/// .app contains besides the SwiftPM product — helper executables built by prebuild commands, resources, an Info.plist
/// template and an icon. Read as data; its commands run only after the user confirms them.
/// </summary>
public sealed record SwiftPackagingContract
{
    public const string Schema = "hcg-app-packaging/1";
    public const string FileName = "AppPackaging.json";
    /// <summary>Placeholder for the per-build staging folder that prebuild commands write helpers into.</summary>
    public const string HelpersDirVariable = "${HELPERS_DIR}";

    /// <summary>Path of AppPackaging.json relative to the workspace source, '/' separated.</summary>
    public required string Path { get; init; }
    /// <summary>SHA-256 of the file as read; the build refuses to run if the file changed after confirmation.</summary>
    public required string Sha256 { get; init; }
    public required string Product { get; init; }
    public SwiftPackagingBundle Bundle { get; init; } = new();
    public IReadOnlyList<SwiftPrebuildStep> Prebuild { get; init; } = [];
    public IReadOnlyList<SwiftContractHelper> Helpers { get; init; } = [];
    public IReadOnlyList<SwiftContractResource> Resources { get; init; } = [];
}

/// <summary>Paths are relative to the folder holding AppPackaging.json.</summary>
public sealed record SwiftPackagingBundle
{
    public string? DisplayName { get; init; }
    public string? Identifier { get; init; }
    public string? InfoPlist { get; init; }
    public string? Icon { get; init; }
    public string? MinimumMacOS { get; init; }
}

/// <summary>A command run before <c>swift build</c>: an argument vector, executed directly (never through a shell).</summary>
public sealed record SwiftPrebuildStep
{
    public required string Description { get; init; }
    /// <summary>Tools the step needs, e.g. "go >= 1.25". Each is located before the step runs and added to its PATH.</summary>
    public IReadOnlyList<SwiftToolRequirement> Requires { get; init; } = [];
    /// <summary>Relative to the contract folder; must stay inside the workspace.</summary>
    public string WorkingDirectory { get; init; } = ".";
    public required IReadOnlyList<string> Command { get; init; }
}

public sealed record SwiftToolRequirement(string Tool, string? MinimumVersion)
{
    public override string ToString() => MinimumVersion is null ? Tool : $"{Tool} >= {MinimumVersion}";
}

/// <summary>A helper executable placed inside the .app (e.g. Contents/Helpers/hcg-agent).</summary>
public sealed record SwiftContractHelper
{
    public required string Name { get; init; }
    /// <summary><c>${HELPERS_DIR}/…</c> (produced by a prebuild step) or a path relative to the contract folder.</summary>
    public required string Source { get; init; }
    /// <summary>Inside the bundle, under Contents/Helpers/.</summary>
    public required string Destination { get; init; }
    public bool Required { get; init; } = true;
    /// <summary>Slices the helper must contain (arm64, x86_64); empty means no requirement.</summary>
    public IReadOnlyList<string> Architectures { get; init; } = [];
    /// <summary><c>version</c>: <c>&lt;helper&gt; version</c> must exit 0 and print "&lt;name&gt; &lt;app version&gt; …".</summary>
    public IReadOnlyList<string> Verify { get; init; } = [];
}

/// <summary>A file copied into the bundle, under Contents/Resources/.</summary>
public sealed record SwiftContractResource
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public bool Required { get; init; } = true;
}
