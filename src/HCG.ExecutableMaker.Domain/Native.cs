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
}
