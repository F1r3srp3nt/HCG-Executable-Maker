namespace HCG.ExecutableMaker.Domain;

/// <summary>What kind of C/C++ project a folder is (judged from its files; nothing runs during inspection).</summary>
public enum NativeProjectKind
{
    /// <summary>CMakeLists.txt with a project() call: targets are discovered with the CMake File API after a trusted configure.</summary>
    CMake,
    /// <summary>A Makefile (GNU make): the build target and the expected output are configured explicitly by the user.</summary>
    Make,
    /// <summary>C/C++ sources without a build system: HCG generates a small CMake configuration for explicitly selected files.</summary>
    Sources,
    /// <summary>A Theos project (jailbreak tweak/tool for iOS). Never built or packaged as a desktop app or IPA.</summary>
    Theos,
    /// <summary>Recognized but not built in this version (Meson, Autotools without a Makefile, Visual Studio .vcxproj).</summary>
    Unsupported,
}

/// <summary>CMake File API target types (codemodel-v2 "type").</summary>
public enum NativeTargetType
{
    Executable,
    StaticLibrary,
    SharedLibrary,
    ModuleLibrary,
    ObjectLibrary,
    InterfaceLibrary,
    Utility,
    Other,
}

/// <summary>A CMake target as the CMake File API reports it after configuration.</summary>
public sealed record NativeTargetInfo
{
    public required string Name { get; init; }
    public required NativeTargetType Type { get; init; }
    /// <summary>Artifact paths as CMake reports them (relative to the build directory, or absolute).</summary>
    public IReadOnlyList<string> Artifacts { get; init; } = [];
    /// <summary>Compile languages used by the target (C, CXX, OBJC, …).</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];
    /// <summary>Source directory relative to the project directory ("" for the root).</summary>
    public string SourceDirectory { get; init; } = "";
    /// <summary>Names of the project's own targets this target depends on (link and build order).</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    /// <summary>An executable built as a macOS application bundle (MACOSX_BUNDLE): its artifact is inside Name.app.</summary>
    public bool IsMacBundle { get; init; }
    public bool IsLibrary => Type is NativeTargetType.StaticLibrary or NativeTargetType.SharedLibrary or NativeTargetType.ModuleLibrary
        or NativeTargetType.ObjectLibrary or NativeTargetType.InterfaceLibrary;
}

/// <summary>A compiler CMake selected (File API toolchains-v1).</summary>
public sealed record NativeCompilerInfo(string Language, string? Id, string? Version, string? Path);

/// <summary>The result of a trusted CMake configure in an HCG-owned build folder, read through the CMake File API.</summary>
public sealed record NativeConfiguration
{
    public required string Generator { get; init; }
    public bool IsMultiConfig { get; init; }
    /// <summary>The configurations CMake generated (one for single-configuration generators).</summary>
    public IReadOnlyList<string> Configurations { get; init; } = [];
    /// <summary>The configuration whose targets are listed (CMAKE_BUILD_TYPE, or the chosen one of a multi-config generator).</summary>
    public string Configuration { get; init; } = "Release";
    public string? Preset { get; init; }
    public string? CMakeVersion { get; init; }
    public IReadOnlyList<NativeCompilerInfo> Compilers { get; init; } = [];
    public IReadOnlyList<NativeTargetInfo> Targets { get; init; } = [];
    /// <summary>Cache values worth showing (language standards, build type, toolchain file, …).</summary>
    public IReadOnlyDictionary<string, string> CacheHighlights { get; init; } = new Dictionary<string, string>();
    public bool DownloadsAllowed { get; init; }
    public DateTimeOffset ConfiguredAt { get; init; }
    public IEnumerable<NativeTargetInfo> Executables => Targets.Where(t => t.Type == NativeTargetType.Executable);
    public IEnumerable<NativeTargetInfo> Libraries => Targets.Where(t => t.IsLibrary);
}

/// <summary>A configure preset from CMakePresets.json / CMakeUserPresets.json.</summary>
public sealed record CMakePresetInfo(string Name, string? DisplayName, string? Generator, string? BinaryDir, bool Hidden, string Source);

/// <summary>A C/C++ source file of a project without a build system.</summary>
public sealed record NativeSourceFile(string Path, string Language, bool HasMain);

/// <summary>One C/C++ project root found in the workspace.</summary>
public sealed record NativeProjectInfo
{
    /// <summary>Project directory relative to the workspace source ("" for the root), '/' separated.</summary>
    public required string Directory { get; init; }
    public required NativeProjectKind Kind { get; init; }
    /// <summary>CMakeLists.txt, Makefile, a marker file or (for sources) the first source file.</summary>
    public required string ManifestPath { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    /// <summary>Why this was classified as it was ("CMakeLists.txt with project(Hello C)", "Makefile includes $(THEOS)/makefiles/common.mk", …).</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Set for <see cref="NativeProjectKind.Unsupported"/> and <see cref="NativeProjectKind.Theos"/>.</summary>
    public string? UnsupportedReason { get; init; }

    // CMake (static hints; the configured File API reply is authoritative)
    public IReadOnlyList<string> Languages { get; init; } = [];
    public string? MinimumCMake { get; init; }
    public IReadOnlyList<string> ExecutableHints { get; init; } = [];
    public IReadOnlyList<string> LibraryHints { get; init; } = [];
    public IReadOnlyList<CMakePresetInfo> Presets { get; init; } = [];
    public IReadOnlyList<string> FindPackages { get; init; } = [];
    /// <summary>FetchContent / ExternalProject use (file:line): configuration would download dependencies.</summary>
    public IReadOnlyList<string> DownloadEvidence { get; init; } = [];
    public NativeConfiguration? Configured { get; init; }

    // Make
    public IReadOnlyList<string> MakeTargets { get; init; } = [];
    public IReadOnlyList<string> PhonyTargets { get; init; } = [];

    // Sources
    public IReadOnlyList<NativeSourceFile> Sources { get; init; } = [];

    // Theos
    public TheosProjectInfo? Theos { get; init; }

    public string DisplayDirectory => Directory.Length == 0 ? "(workspace root)" : Directory + "/";
}

/// <summary>C/C++ analysis of a workspace.</summary>
public sealed record NativeAnalysis
{
    public IReadOnlyList<NativeProjectInfo> Projects { get; init; } = [];
}

/// <summary>C/C++ build settings, persisted with the build record.</summary>
public sealed record NativeBuildOptions
{
    /// <summary>Project directory relative to the workspace source ("" for the root).</summary>
    public required string ProjectDirectory { get; init; }
    public required NativeProjectKind Kind { get; init; }
    /// <summary>CMake target, make target, or (for sources) the executable name.</summary>
    public required string Target { get; init; }
    /// <summary>CMake configure preset (CMakePresets.json); null = HCG chooses the generator.</summary>
    public string? Preset { get; init; }
    /// <summary>Release, Debug, RelWithDebInfo or MinSizeRel.</summary>
    public string BuildType { get; init; } = "Release";
    /// <summary>Extra CMake cache entries, each NAME[:TYPE]=VALUE (passed as one -D argument, never through a shell).</summary>
    public IReadOnlyList<string> CacheEntries { get; init; } = [];
    /// <summary>Let FetchContent / ExternalProject download during configuration (off: FETCHCONTENT_FULLY_DISCONNECTED=ON).</summary>
    public bool AllowDownloads { get; init; }
    /// <summary>make variables, each NAME=VALUE.</summary>
    public IReadOnlyList<string> MakeVariables { get; init; } = [];
    /// <summary>For Make: the file the target produces, relative to the project directory.</summary>
    public string? ExpectedOutput { get; init; }
    /// <summary>For sources: the files compiled, relative to the project directory.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];
    /// <summary>For sources: C or CXX.</summary>
    public string Language { get; init; } = "C";
    /// <summary>For sources: the language standard number (C 99/11/17/23, C++ 11/14/17/20/23).</summary>
    public string Standard { get; init; } = "17";
    /// <summary>Files or folders (relative to the project directory) copied next to the executable: data, plugins, licences.</summary>
    public IReadOnlyList<string> RuntimeFiles { get; init; } = [];
    /// <summary>The executable is a macOS application bundle produced by the project (kept as it is).</summary>
    public bool IsMacBundle { get; init; }
    /// <summary>For Theos: the package configuration the project defines (rootless / rootful …), as the project selects it.</summary>
    public TheosSchemeChoice? TheosScheme { get; init; }
}

/// <summary>What a Theos instance builds (from TWEAK_NAME, BUNDLE_NAME, APPLICATION_NAME, TOOL_NAME, LIBRARY_NAME, FRAMEWORK_NAME).</summary>
public enum TheosInstanceType
{
    Tweak,
    PreferenceBundle,
    Bundle,
    Application,
    Tool,
    Library,
    Framework,
}

/// <summary>One built product of a Theos project or one of its subprojects.</summary>
public sealed record TheosInstance
{
    public required TheosInstanceType Type { get; init; }
    public required string Name { get; init; }
    /// <summary>Makefile that defines it, relative to the workspace source.</summary>
    public required string Makefile { get; init; }
    /// <summary>The instance's *_INSTALL_PATH when the Makefile sets it (relative to the package prefix).</summary>
    public string? InstallPath { get; init; }
    /// <summary>For tweaks: the MobileSubstrate filter plist next to the Makefile, if present.</summary>
    public string? FilterPlist { get; init; }
    public string DisplayType => Type switch
    {
        TheosInstanceType.Tweak => "Jailbreak tweak (injected dynamic library)",
        TheosInstanceType.PreferenceBundle => "Preference bundle (Settings pane)",
        TheosInstanceType.Bundle => "Bundle",
        TheosInstanceType.Application => "Application packaged in the .deb (not an IPA)",
        TheosInstanceType.Tool => "Command-line tool",
        TheosInstanceType.Library => "Dynamic library",
        _ => "Framework",
    };
}

/// <summary>
/// A package configuration the project itself defines, e.g. Nexus' <c>HCG_ENV=rootless</c> → THEOS_PACKAGE_SCHEME=rootless,
/// TARGET iphone:clang:16.5:15.0. When the Makefile has no selector, there is one scheme with <see cref="SelectorVariable"/> null.
/// </summary>
public sealed record TheosScheme
{
    /// <summary>The make variable the project uses to select the configuration (null: the Makefile is fixed).</summary>
    public string? SelectorVariable { get; init; }
    public string? SelectorValue { get; init; }
    /// <summary>"rootless", "rootful" or "roothide" (Theos' THEOS_PACKAGE_SCHEME; empty means rootful).</summary>
    public required string PackageScheme { get; init; }
    /// <summary>SDK version from TARGET (iphone:clang:SDK:DEPLOYMENT), e.g. "16.5"; null = Theos' latest.</summary>
    public string? SdkVersion { get; init; }
    /// <summary>Deployment target from TARGET, e.g. "15.0".</summary>
    public string? DeploymentTarget { get; init; }
    /// <summary>The project's own refusal for this configuration ($(error …) in its branch), shown verbatim.</summary>
    public string? ProjectError { get; init; }
    public bool IsDefault { get; init; }
    public string Label => SelectorVariable is null ? PackageScheme : $"{SelectorVariable}={SelectorValue} ({PackageScheme})";
    /// <summary>Where files are installed on the device.</summary>
    public string InstallPrefix => PackageScheme == "rootless" ? "/var/jb" : "/";
    /// <summary>The Debian Architecture field Theos writes for this scheme.</summary>
    public string DebArchitecture => PackageScheme switch { "rootless" => "iphoneos-arm64", "roothide" => "iphoneos-arm64e", _ => "iphoneos-arm" };
}

/// <summary>The configuration chosen for a Theos build (persisted with the build).</summary>
public sealed record TheosSchemeChoice
{
    public string? SelectorVariable { get; init; }
    public string? SelectorValue { get; init; }
    public required string PackageScheme { get; init; }
    public string? SdkVersion { get; init; }
    public string? DeploymentTarget { get; init; }
    public IReadOnlyList<string> Architectures { get; init; } = [];
    /// <summary>The project's control file Package/Version, to check the built .deb against.</summary>
    public string? PackageId { get; init; }
    public string? PackageVersion { get; init; }
    public IReadOnlyList<TheosInstance> Instances { get; init; } = [];
    public string Label => SelectorVariable is null ? PackageScheme : $"{SelectorVariable}={SelectorValue} ({PackageScheme})";
}

/// <summary>A Theos project: its package (control file), products, subprojects and the configurations it defines.</summary>
public sealed record TheosProjectInfo
{
    public required string Makefile { get; init; }
    public string? ControlFile { get; init; }
    public string? PackageId { get; init; }
    public string? PackageName { get; init; }
    public string? PackageVersion { get; init; }
    /// <summary>The control file's own Architecture field (Theos rewrites it for rootless/roothide).</summary>
    public string? ControlArchitecture { get; init; }
    public string? Depends { get; init; }
    public string? Section { get; init; }
    public IReadOnlyList<TheosInstance> Instances { get; init; } = [];
    /// <summary>SUBPROJECTS directories, relative to the workspace source.</summary>
    public IReadOnlyList<string> Subprojects { get; init; } = [];
    public IReadOnlyList<TheosScheme> Schemes { get; init; } = [];
    /// <summary>ARCHS as the project sets them (after its own defaults), e.g. ["arm64", "arm64e"].</summary>
    public IReadOnlyList<string> Architectures { get; init; } = [];
    /// <summary>The variable the project uses for ARCHS (e.g. HCG_ARCHS), when it has one.</summary>
    public string? ArchitectureVariable { get; init; }
    /// <summary>Maintainer scripts (postinst …) in layout/DEBIAN or next to control: they run as root on install.</summary>
    public IReadOnlyList<string> MaintainerScripts { get; init; } = [];
    /// <summary>Paths outside the project folder its Makefiles reference (e.g. ROOT := .. with ../Sources): the build keeps the repository layout.</summary>
    public bool UsesParentDirectory { get; init; }
    public string Summary => string.Join(" + ", Instances.Select(i => $"{i.DisplayType}: {i.Name}"));
}
