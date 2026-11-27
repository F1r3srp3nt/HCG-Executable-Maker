namespace HCG.ExecutableMaker.Domain;

/// <summary>
/// A tool one build needs, and who says so: the packaging contract, a manifest (go.mod, package.json, Cargo.toml,
/// global.json, Package.swift, CMakeLists.txt, pyproject.toml), a build script, or the build kind and target itself.
/// Read from files only; nothing in the project is run to find it.
/// </summary>
public sealed record ToolRequirement
{
    /// <summary>Catalogue id ("go", "swift", "cmake", …) or, for a tool HCG has no entry for, the declared name.</summary>
    public required string ToolId { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Lowest acceptable version where one is declared ("1.25"), else null.</summary>
    public string? MinimumVersion { get; init; }
    /// <summary>The file (and place in it) that declares the need, e.g. "macos/HCGServerApp/AppPackaging.json › prebuild “Build the host agent”".</summary>
    public required string DeclaredBy { get; init; }
    /// <summary>What the tool is used for in this build.</summary>
    public required string Purpose { get; init; }
    public bool Required { get; init; } = true;
    /// <summary>Further facts from manifests that do not change the minimum (e.g. go.mod's toolchain directive).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>One installation of a tool HCG found and ran (for its version).</summary>
public sealed record ToolInstallation(string Path, string? Version, string Source);

/// <summary>A way to obtain a missing tool. Only <see cref="Runnable"/> options can be run by HCG, and only after approval.</summary>
public sealed record InstallOption
{
    public required InstallMethod Method { get; init; }
    public required string Label { get; init; }
    /// <summary>The exact command, as shown to the person (also what HCG runs for a runnable option).</summary>
    public string? CommandLine { get; init; }
    /// <summary>Executable and arguments HCG runs (absolute executable; no shell). Null for instructions only.</summary>
    public string? Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string? Url { get; init; }
    public string? Note { get; init; }
    public bool Runnable => Executable is not null;
}

public enum InstallMethod
{
    /// <summary>The tool's official download or installer page.</summary>
    Official,
    /// <summary>Homebrew (macOS): a fixed formula from HCG's catalogue, run only after the person approves.</summary>
    Homebrew,
    /// <summary>Apple's Command Line Tools installer (xcode-select --install), after approval.</summary>
    AppleCommandLineTools,
    /// <summary>A package manager command shown for the person to run (apt, dnf, winget). HCG does not run it.</summary>
    PackageManagerInstructions,
}

/// <summary>An authorized remote build host that reports the tool, offered instead of a local installation.</summary>
public sealed record RemoteBuildOption(string HostId, string Label, string ToolVersion, string Transfer);

/// <summary>A requirement, checked on this computer.</summary>
public sealed record RequirementStatus
{
    public required ToolRequirement Requirement { get; init; }
    public required ReadinessState State { get; init; }
    /// <summary>The installation the build will use (null when none fits).</summary>
    public ToolInstallation? Selected { get; init; }
    /// <summary>Every installation found, compatible or not.</summary>
    public IReadOnlyList<ToolInstallation> Found { get; init; } = [];
    public required string Detail { get; init; }
    public IReadOnlyList<InstallOption> Install { get; init; } = [];
    public IReadOnlyList<RemoteBuildOption> Remote { get; init; } = [];
    /// <summary>The executable names HCG accepts for a manual location (e.g. "go").</summary>
    public IReadOnlyList<string> ExecutableNames { get; init; } = [];
    public string? OfficialUrl { get; init; }
    public bool Blocks => Requirement.Required && State is not (ReadinessState.Ready or ReadinessState.NotNeeded or ReadinessState.NotVerifiable);
}

/// <summary>Build Readiness: every requirement of one build (project + kind + target), checked before compilation.</summary>
public sealed record BuildReadinessReport
{
    public required string Project { get; init; }
    public required string Target { get; init; }
    public required IReadOnlyList<RequirementStatus> Items { get; init; }
    /// <summary>The toolchain profile the project uses (null = HCG's global choices and automatic detection).</summary>
    public string? Profile { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public bool CanBuild => !Items.Any(i => i.Blocks);
    public IEnumerable<RequirementStatus> Blockers => Items.Where(i => i.Blocks);
    public string Summary => CanBuild
        ? $"{Project} ({Target}): every required tool is ready."
        : $"{Project} ({Target}): " + string.Join(" ", Blockers.Select(b => b.Detail));
}

/// <summary>
/// A reusable set of tool locations ("go" → /usr/local/go/bin/go). Assigned to projects so that two projects can use
/// different SDK versions side by side. A profile only points at installations; it never installs or replaces one.
/// </summary>
public sealed record ToolchainProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public Dictionary<string, string> ToolPaths { get; init; } = new(StringComparer.Ordinal);
}
