namespace HCG.ExecutableMaker.Domain;

/// <summary>What a Node.js project is, judged from its files (never from running it).</summary>
public enum NodeProjectKind
{
    /// <summary>Electron desktop application (electron dependency + main entry).</summary>
    ElectronDesktop,
    /// <summary>Browser application (Vite, Next.js, webpack, …): needs a desktop shell to become an app.</summary>
    WebApp,
    /// <summary>Server / CLI process (Express, Fastify, …).</summary>
    NodeService,
    /// <summary>Package without an application entry.</summary>
    Library,
}

public enum ElectronPackagerKind
{
    None,
    /// <summary>electron-builder: implemented by HCG.</summary>
    ElectronBuilder,
    /// <summary>Electron Forge: recognized, not run by HCG in this version.</summary>
    ElectronForge,
    /// <summary>@electron/packager: recognized, not run by HCG in this version.</summary>
    ElectronPackager,
}

public enum NodePackageManager
{
    Npm,
    Yarn,
    Pnpm,
    Bun,
}

/// <summary>electron-builder configuration values HCG needs (read from YAML/JSON or package.json "build").</summary>
public sealed record ElectronBuilderConfig
{
    /// <summary>File the configuration came from ("electron-builder.yml", "package.json#build", …).</summary>
    public required string Source { get; init; }
    public string? AppId { get; init; }
    public string? ProductName { get; init; }
    /// <summary>directories.output (default "dist").</summary>
    public string OutputDirectory { get; init; } = "dist";
    /// <summary>mac.target entries, e.g. "zip", "dmg", "dir" (empty = electron-builder defaults: dmg + zip).</summary>
    public IReadOnlyList<string> MacTargets { get; init; } = [];
    /// <summary>Architectures named in mac.target (e.g. "arm64").</summary>
    public IReadOnlyList<string> MacArchs { get; init; } = [];
    public IReadOnlyList<string> LinuxTargets { get; init; } = [];
    public IReadOnlyList<string> WinTargets { get; init; } = [];
    public bool HasMac { get; init; }
    public bool HasLinux { get; init; }
    public bool HasWindows { get; init; }
    public string? MacIdentity { get; init; }
    public string? LinuxExecutableName { get; init; }
    /// <summary>extraMetadata.main, when the config overrides package.json "main".</summary>
    public string? ExtraMetadataMain { get; init; }
    /// <summary>The config is JavaScript (electron-builder.config.js…): values are known only when it runs.</summary>
    public bool IsScript { get; init; }
}

/// <summary>
/// One compile step HCG runs before electron-builder: an npm script (<c>npm run NAME</c>) or a direct
/// <c>node FILE ARGS</c> invocation. Never a shell string.
/// </summary>
public sealed record ElectronCompileStep
{
    public string? NpmScript { get; init; }
    public IReadOnlyList<string>? NodeArguments { get; init; }
    public string Display => NpmScript is { } s ? $"npm run {s}" : "node " + string.Join(' ', NodeArguments ?? []);
}

/// <summary>A package.json script that runs electron-builder, split into its compile steps and the electron-builder call.</summary>
public sealed record ElectronPackagingScript
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    /// <summary>The commands before electron-builder, resolved into steps HCG can run without a shell.</summary>
    public IReadOnlyList<ElectronCompileStep> CompileSteps { get; init; } = [];
    /// <summary>Platforms the electron-builder call names ("mac", "linux", "win"); empty = the host platform.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];
    /// <summary>Architectures the call names ("arm64", "x64", "universal", …).</summary>
    public IReadOnlyList<string> Archs { get; init; } = [];
    /// <summary>Targets named on the command line (e.g. "--dir" → "dir").</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];
    /// <summary>--config FILE, when the script passes one.</summary>
    public string? ConfigPath { get; init; }
    /// <summary>Why HCG cannot follow this script (null when it can).</summary>
    public string? Problem { get; init; }
}

/// <summary>One package.json project found in the workspace (static facts only).</summary>
public sealed record NodeProjectInfo
{
    /// <summary>Directory relative to the workspace source ("" for the root), '/' separated.</summary>
    public required string Directory { get; init; }
    public required string ManifestPath { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? ProductName { get; init; }
    public string? Description { get; init; }
    public required NodeProjectKind Kind { get; init; }
    /// <summary>Why it was classified this way ("dependency electron ^45.2.3", "vite.config.ts", …).</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Frameworks { get; init; } = [];
    public string? EnginesNode { get; init; }
    public string? EnginesNpm { get; init; }
    /// <summary>package.json "packageManager" (e.g. "pnpm@9.1.0").</summary>
    public string? PackageManagerField { get; init; }
    public NodePackageManager PackageManager { get; init; } = NodePackageManager.Npm;
    public string? LockFile { get; init; }
    public string? ElectronVersion { get; init; }
    /// <summary>package.json "main" (or electron-builder extraMetadata.main).</summary>
    public string? MainEntry { get; init; }
    public bool MainEntryExists { get; init; }
    public ElectronPackagerKind Packager { get; init; }
    public string? PackagerVersion { get; init; }
    public ElectronBuilderConfig? BuilderConfig { get; init; }
    public IReadOnlyDictionary<string, string> Scripts { get; init; } = new Dictionary<string, string>();
    /// <summary>Scripts that invoke electron-builder (the project's packaging workflow).</summary>
    public IReadOnlyList<ElectronPackagingScript> PackagingScripts { get; init; } = [];
    /// <summary>The main entry does not exist in the source; it is produced by the compile step (checked after it runs).</summary>
    public bool MainEntryBuilt { get; init; }
    /// <summary>Notes that do not block a desktop build (no lockfile, JavaScript config, …).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>What is missing before this project can produce a desktop app (empty when nothing is).</summary>
    public IReadOnlyList<string> MissingForDesktop { get; init; } = [];
    /// <summary>Every declared dependency (all sections): name → version spec.</summary>
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();
    /// <summary>Names declared under "dependencies" (shipped at run time), as opposed to devDependencies.</summary>
    public IReadOnlyList<string> RuntimeDependencyNames { get; init; } = [];
    /// <summary>package.json declares npm/yarn "workspaces" (a monorepo root).</summary>
    public bool DeclaresWorkspaces { get; init; }
    /// <summary>Directory of the monorepo root whose "workspaces" include this project (null when it is standalone).</summary>
    public string? WorkspaceRoot { get; init; }
    /// <summary>The lockfile of <see cref="WorkspaceRoot"/>, relative to the workspace source (it pins this member's dependencies).</summary>
    public string? WorkspaceLockFile { get; init; }
    /// <summary>hcg-desktop/conversion.json: this folder holds a desktop wrapper HCG generated earlier.</summary>
    public string? HcgDesktopRecord { get; init; }
    /// <summary>Whether and how "Create desktop application" applies (static, from the files only).</summary>
    public DesktopEligibility? DesktopEligibility { get; init; }
    public string DisplayDirectory => Directory.Length == 0 ? "(workspace root)" : Directory + "/";
    public string DisplayName => ProductName ?? Name;
}

/// <summary>What "Create desktop application" can do with a Node.js project.</summary>
public enum DesktopConversionCategory
{
    /// <summary>A static browser app (Vite + React) HCG can wrap in a generated Electron shell.</summary>
    BrowserApp,
    /// <summary>Already an Electron app with a main process but no packaging: HCG adds packaging and keeps the main process.</summary>
    ElectronWithoutPackaging,
    /// <summary>Already an Electron app (or a desktop project HCG generated): built as it is, nothing is generated.</summary>
    ExistingElectron,
    /// <summary>Needs a server at run time (server rendering, a backend in the project): not converted without additional setup.</summary>
    ServerDependent,
    /// <summary>Not supported, or ambiguous (another framework or package manager, a monorepo root, no production build).</summary>
    Unsupported,
}

public sealed record DesktopEligibility(DesktopConversionCategory Category, string Summary, IReadOnlyList<string> Reasons)
{
    /// <summary>"Create desktop application" can generate (or regenerate) the desktop wrapper.</summary>
    public bool CanConvert => Category is DesktopConversionCategory.BrowserApp or DesktopConversionCategory.ElectronWithoutPackaging;

    public string Label => Category switch
    {
        DesktopConversionCategory.BrowserApp => "Browser app — can become a desktop app",
        DesktopConversionCategory.ElectronWithoutPackaging => "Electron app without packaging — HCG can add it",
        DesktopConversionCategory.ExistingElectron => "Existing Electron app — built as it is",
        DesktopConversionCategory.ServerDependent => "Needs a server — additional setup required",
        _ => "Not supported for desktop conversion",
    };
}

public sealed record NodeAnalysis
{
    public IReadOnlyList<NodeProjectInfo> Projects { get; init; } = [];
}

/// <summary>One Node.js installation found on this machine.</summary>
public sealed record NodeInstallation(string NodePath, string Version, string Source, string? NpmCli, string? NpmVersion);

/// <summary>Electron build settings, persisted with the build record.</summary>
public sealed record ElectronBuildOptions
{
    /// <summary>Project directory relative to the workspace source ("" for the root).</summary>
    public required string ProjectDirectory { get; init; }
    /// <summary>The package.json script whose electron-builder call HCG follows (null: electron-builder with the project config only).</summary>
    public string? PackagingScript { get; init; }
    /// <summary>The compile steps taken from that script (e.g. npm run desktop:build); empty when none is needed.</summary>
    public IReadOnlyList<ElectronCompileStep> CompileSteps { get; init; } = [];
    /// <summary>--config FILE from the packaging script (null: electron-builder's own config lookup).</summary>
    public string? ConfigPath { get; init; }
    /// <summary>"mac" or "linux": the electron-builder platform flag HCG passes.</summary>
    public string Platform { get; init; } = "mac";
    /// <summary>Install dependencies with <c>npm ci</c> (lockfile present) instead of <c>npm install</c>.</summary>
    public bool UseLockfile { get; init; } = true;
    /// <summary>electron-builder targets for the chosen platform ("dir", "zip", "dmg").</summary>
    public IReadOnlyList<string> Targets { get; init; } = ["dir"];
    public string ProductName { get; init; } = "";
    /// <summary>The project's configured electron-builder output directory (HCG redirects output into its own build folder).</summary>
    public string OutputDirectory { get; init; } = "dist";
    /// <summary>package.json "main" (checked after the compile step).</summary>
    public string? MainEntry { get; init; }
}
