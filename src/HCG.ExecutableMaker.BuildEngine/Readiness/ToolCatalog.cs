using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Readiness;

/// <summary>
/// What HCG knows about a build tool: the executable names, how to ask its version (a harmless probe, never a build),
/// where installers put it on each OS, and the official way to get it. Homebrew formula names are fixed here and are
/// never taken from a project's files: a project can say that it needs "go", never what HCG should install.
/// </summary>
public sealed record ToolDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<string> Executables { get; init; }
    /// <summary>Probe argument lists, tried in order until one prints a version.</summary>
    public IReadOnlyList<string[]> VersionArguments { get; init; } = [["--version"]];
    /// <summary>First capture group is the version.</summary>
    public Regex VersionPattern { get; init; } = Patterns.Generic;
    /// <summary>Several versions in one output (dotnet --list-sdks): the highest counts.</summary>
    public bool HighestVersion { get; init; }
    /// <summary>Environment for the version probe (e.g. GOTOOLCHAIN=local so `go version` never downloads a toolchain).</summary>
    public IReadOnlyDictionary<string, string> ProbeEnvironment { get; init; } = new Dictionary<string, string>();
    public required string OfficialUrl { get; init; }
    /// <summary>Homebrew formula, when Homebrew packages the tool as a normal formula (no casks, nothing needing sudo).</summary>
    public string? HomebrewFormula { get; init; }
    /// <summary>On macOS the /usr/bin copy is Apple's shim: running it without the Command Line Tools opens an install dialog.</summary>
    public bool AppleShim { get; init; }
    /// <summary>Apple ships it with Xcode / the Command Line Tools (xcode-select --install is the official route).</summary>
    public bool FromAppleDeveloperTools { get; init; }
    public string? LinuxInstructions { get; init; }
    public string? WindowsInstructions { get; init; }
    /// <summary>Install locations to look in besides PATH (absolute; "~" is the home folder; "*" matches one path segment).</summary>
    public Func<TargetPlatform, IEnumerable<string>> Locations { get; init; } = _ => [];
    /// <summary>Known by name only (declared by a project, not in HCG's catalogue): no install action is offered.</summary>
    public bool Generic { get; init; }

    public static class Patterns
    {
        public static readonly Regex Generic = new(@"(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);
    }
}

public static class ToolCatalog
{
    private static string[] Brew(string exe) => [$"/opt/homebrew/bin/{exe}", $"/usr/local/bin/{exe}"];
    private static readonly string[] VersionManagers = ["~/.asdf/shims/{0}", "~/.local/share/mise/shims/{0}", "/opt/local/bin/{0}"];
    private static IEnumerable<string> Managed(string exe) => VersionManagers.Select(p => string.Format(System.Globalization.CultureInfo.InvariantCulture, p, exe));

    public static readonly ToolDefinition Go = new()
    {
        Id = "go", DisplayName = "Go", Executables = ["go"], VersionArguments = [["version"]],
        VersionPattern = new Regex(@"\bgo(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled),
        ProbeEnvironment = new Dictionary<string, string> { ["GOTOOLCHAIN"] = "local" },
        OfficialUrl = "https://go.dev/dl/", HomebrewFormula = "go",
        LinuxInstructions = "Download the archive for your architecture from https://go.dev/dl/ and extract it to /usr/local/go (distribution packages such as golang-go are often older than projects require).",
        WindowsInstructions = "winget install --id GoLang.Go",
        Locations = p => p switch
        {
            TargetPlatform.MacOS => [.. Brew("go"), "/opt/homebrew/opt/go/libexec/bin/go", "/usr/local/opt/go/libexec/bin/go", "/usr/local/go/bin/go", "~/sdk/go*/bin/go", "~/go/pkg/mod/golang.org/toolchain@*/bin/go", .. Managed("go")],
            TargetPlatform.Linux => ["/usr/local/go/bin/go", "/usr/lib/go/bin/go", "/snap/bin/go", "~/sdk/go*/bin/go", "~/.asdf/shims/go", "~/.local/share/mise/shims/go"],
            TargetPlatform.Windows => [@"C:\Program Files\Go\bin\go.exe", @"~\sdk\go*\bin\go.exe"],
            _ => [],
        },
    };

    public static readonly ToolDefinition Swift = new()
    {
        Id = "swift", DisplayName = "Swift", Executables = ["swift"],
        VersionPattern = new Regex(@"Swift version (\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled),
        OfficialUrl = "https://www.swift.org/install/", AppleShim = true, FromAppleDeveloperTools = true,
        LinuxInstructions = "Install Swift with swiftly (https://www.swift.org/install/linux/).",
        WindowsInstructions = "winget install --id Swift.Toolchain",
        Locations = p => p == TargetPlatform.MacOS ? ["/usr/bin/swift"] : ["~/.local/share/swiftly/bin/swift", "/usr/bin/swift"],
    };

    public static readonly ToolDefinition DotNet = new()
    {
        Id = "dotnet", DisplayName = ".NET SDK", Executables = ["dotnet"], VersionArguments = [["--list-sdks"]],
        VersionPattern = new Regex(@"^(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\s+\[", RegexOptions.Compiled | RegexOptions.Multiline), HighestVersion = true,
        ProbeEnvironment = new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" },
        OfficialUrl = "https://dotnet.microsoft.com/download",
        LinuxInstructions = "Follow https://learn.microsoft.com/dotnet/core/install/linux for your distribution.",
        WindowsInstructions = "winget install --id Microsoft.DotNet.SDK.10",
        Locations = _ => Core.ExecutableLocator.DotNetCandidates().Select(c => c),
    };

    public static readonly ToolDefinition Node = new()
    {
        Id = "node", DisplayName = "Node.js", Executables = ["node"], VersionPattern = new Regex(@"v?(\d+\.\d+\.\d+)", RegexOptions.Compiled),
        OfficialUrl = "https://nodejs.org/en/download", HomebrewFormula = "node",
        LinuxInstructions = "Use the official binaries or NodeSource packages from https://nodejs.org/en/download.",
        WindowsInstructions = "winget install --id OpenJS.NodeJS.LTS",
        Locations = p => p == TargetPlatform.Windows ? [@"C:\Program Files\nodejs\node.exe"] : [.. Brew("node"), "~/.nvm/versions/node/*/bin/node", "~/.volta/bin/node", "~/.fnm/aliases/default/bin/node", .. Managed("node")],
    };

    public static readonly ToolDefinition Npm = Node with
    {
        Id = "npm", DisplayName = "npm", Executables = ["npm"], HomebrewFormula = "node",
        Locations = p => p == TargetPlatform.Windows ? [@"C:\Program Files\nodejs\npm.cmd"] : [.. Brew("npm"), "~/.nvm/versions/node/*/bin/npm", "~/.volta/bin/npm", .. Managed("npm")],
    };

    public static readonly ToolDefinition Python = new()
    {
        Id = "python3", DisplayName = "Python", Executables = ["python3"], VersionPattern = new Regex(@"Python (\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled),
        OfficialUrl = "https://www.python.org/downloads/", HomebrewFormula = "python", AppleShim = true,
        LinuxInstructions = "sudo apt install python3 (Debian/Ubuntu) or your distribution's python3 package.",
        WindowsInstructions = "winget install --id Python.Python.3.13",
        Locations = p => p == TargetPlatform.MacOS
            ? [.. Brew("python3"), "/Library/Frameworks/Python.framework/Versions/*/bin/python3", "~/.pyenv/shims/python3", .. Managed("python3"), "/usr/bin/python3"]
            : ["/usr/bin/python3", "~/.pyenv/shims/python3"],
    };

    public static readonly ToolDefinition Rustc = new()
    {
        Id = "rustc", DisplayName = "Rust (rustc)", Executables = ["rustc"], VersionPattern = new Regex(@"rustc (\d+\.\d+\.\d+)", RegexOptions.Compiled),
        OfficialUrl = "https://www.rust-lang.org/tools/install", HomebrewFormula = "rust",
        LinuxInstructions = "Install rustup from https://rustup.rs (HCG does not run installer scripts; run it yourself).",
        WindowsInstructions = "winget install --id Rustlang.Rustup",
        Locations = p => p == TargetPlatform.Windows ? [@"~\.cargo\bin\rustc.exe"] : ["~/.cargo/bin/rustc", .. Brew("rustc")],
    };

    public static readonly ToolDefinition Cargo = Rustc with
    {
        Id = "cargo", DisplayName = "Cargo", Executables = ["cargo"], VersionPattern = new Regex(@"cargo (\d+\.\d+\.\d+)", RegexOptions.Compiled),
        Locations = p => p == TargetPlatform.Windows ? [@"~\.cargo\bin\cargo.exe"] : ["~/.cargo/bin/cargo", .. Brew("cargo")],
    };

    public static readonly ToolDefinition Java = new()
    {
        Id = "javac", DisplayName = "Java (JDK)", Executables = ["javac"], VersionArguments = [["-version"]],
        VersionPattern = new Regex(@"javac (\d+(?:\.\d+)*)", RegexOptions.Compiled),
        OfficialUrl = "https://adoptium.net/temurin/releases/", HomebrewFormula = "openjdk",
        LinuxInstructions = "sudo apt install openjdk-21-jdk (Debian/Ubuntu), or Temurin from https://adoptium.net.",
        WindowsInstructions = "winget install --id EclipseAdoptium.Temurin.21.JDK",
        Locations = p => p == TargetPlatform.MacOS
            ? ["/opt/homebrew/opt/openjdk/bin/javac", "/usr/local/opt/openjdk/bin/javac", "/Library/Java/JavaVirtualMachines/*/Contents/Home/bin/javac", "/Applications/Android Studio.app/Contents/jbr/Contents/Home/bin/javac"]
            : ["/usr/lib/jvm/*/bin/javac"],
    };

    public static readonly ToolDefinition CMake = new()
    {
        Id = "cmake", DisplayName = "CMake", Executables = ["cmake"], VersionPattern = new Regex(@"cmake version (\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled),
        OfficialUrl = "https://cmake.org/download/", HomebrewFormula = "cmake",
        LinuxInstructions = "sudo apt install cmake, or the official installer from https://cmake.org/download/.",
        WindowsInstructions = "winget install --id Kitware.CMake",
        Locations = p => p == TargetPlatform.MacOS ? [.. Brew("cmake"), "/Applications/CMake.app/Contents/bin/cmake"] : p == TargetPlatform.Windows ? [@"C:\Program Files\CMake\bin\cmake.exe"] : [],
    };

    public static readonly ToolDefinition Ninja = new()
    {
        Id = "ninja", DisplayName = "Ninja", Executables = ["ninja"], VersionPattern = new Regex(@"^(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.Multiline),
        OfficialUrl = "https://github.com/ninja-build/ninja/releases", HomebrewFormula = "ninja",
        LinuxInstructions = "sudo apt install ninja-build", WindowsInstructions = "winget install --id Ninja-build.Ninja",
        Locations = p => p == TargetPlatform.MacOS ? Brew("ninja") : [],
    };

    public static readonly ToolDefinition Clang = new()
    {
        Id = "clang", DisplayName = "Clang", Executables = ["clang"], VersionPattern = new Regex(@"clang version (\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled),
        OfficialUrl = "https://developer.apple.com/xcode/resources/", AppleShim = true, FromAppleDeveloperTools = true,
        LinuxInstructions = "sudo apt install clang", WindowsInstructions = "Install LLVM from https://releases.llvm.org/ or Visual Studio's C++ Clang tools.",
        Locations = p => p == TargetPlatform.MacOS ? ["/usr/bin/clang"] : ["/usr/bin/clang"],
    };

    public static readonly ToolDefinition Gcc = new()
    {
        Id = "gcc", DisplayName = "GCC", Executables = ["gcc"], VersionPattern = new Regex(@"(?:gcc|clang) version (\d+\.\d+(?:\.\d+)?)|\) (\d+\.\d+\.\d+)", RegexOptions.Compiled),
        OfficialUrl = "https://gcc.gnu.org/install/", HomebrewFormula = "gcc", AppleShim = true,
        LinuxInstructions = "sudo apt install build-essential", WindowsInstructions = "Install MSYS2 (https://www.msys2.org) and its mingw-w64 GCC package.",
        Locations = p => p == TargetPlatform.MacOS ? ["/opt/homebrew/bin/gcc-*", "/usr/local/bin/gcc-*", "/usr/bin/gcc"] : ["/usr/bin/gcc"],
    };

    public static readonly ToolDefinition Make = new()
    {
        Id = "make", DisplayName = "make", Executables = ["make"], VersionPattern = new Regex(@"Make (\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled),
        OfficialUrl = "https://www.gnu.org/software/make/", AppleShim = true, FromAppleDeveloperTools = true,
        LinuxInstructions = "sudo apt install make", Locations = _ => ["/usr/bin/make"],
    };

    public static readonly ToolDefinition Lipo = new()
    {
        // lipo has no portable version flag; it is checked by presence (and, on macOS, the developer tools behind the shim).
        Id = "lipo", DisplayName = "lipo", Executables = ["lipo"], VersionArguments = [],
        OfficialUrl = "https://developer.apple.com/xcode/resources/", AppleShim = true, FromAppleDeveloperTools = true,
        Locations = p => p == TargetPlatform.MacOS ? ["/usr/bin/lipo"] : [],
    };

    public static readonly ToolDefinition Git = new()
    {
        Id = "git", DisplayName = "Git", Executables = ["git"], VersionPattern = new Regex(@"git version (\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled),
        OfficialUrl = "https://git-scm.com/downloads", HomebrewFormula = "git", AppleShim = true,
        LinuxInstructions = "sudo apt install git", WindowsInstructions = "winget install --id Git.Git",
        Locations = p => p == TargetPlatform.MacOS ? [.. Brew("git"), "/usr/bin/git"] : p == TargetPlatform.Windows ? [@"C:\Program Files\Git\cmd\git.exe"] : ["/usr/bin/git"],
    };

    public static readonly ToolDefinition Docker = new()
    {
        Id = "docker", DisplayName = "Docker", Executables = ["docker"], VersionPattern = new Regex(@"Docker version (\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled),
        OfficialUrl = "https://docs.docker.com/get-started/get-docker/",
        LinuxInstructions = "Follow https://docs.docker.com/engine/install/ for your distribution.",
        WindowsInstructions = "winget install --id Docker.DockerDesktop",
        Locations = p => p == TargetPlatform.MacOS ? [.. Brew("docker"), "/Applications/Docker.app/Contents/Resources/bin/docker", "~/.docker/bin/docker", "~/.orbstack/bin/docker"] : [],
    };

    public static readonly ToolDefinition Perl = new()
    {
        Id = "perl", DisplayName = "Perl", Executables = ["perl"], VersionPattern = new Regex(@"\(v(\d+\.\d+\.\d+)\)", RegexOptions.Compiled),
        OfficialUrl = "https://www.perl.org/get.html", HomebrewFormula = "perl", Locations = _ => ["/usr/bin/perl"],
    };

    public static readonly ToolDefinition PkgConfig = new()
    {
        Id = "pkg-config", DisplayName = "pkg-config", Executables = ["pkg-config"], OfficialUrl = "https://github.com/pkgconf/pkgconf",
        HomebrewFormula = "pkgconf", LinuxInstructions = "sudo apt install pkg-config", Locations = p => p == TargetPlatform.MacOS ? Brew("pkg-config") : [],
    };

    public static IReadOnlyList<ToolDefinition> All { get; } =
        [Go, Swift, DotNet, Node, Npm, Python, Rustc, Cargo, Java, CMake, Ninja, Clang, Gcc, Make, Lipo, Git, Docker, Perl, PkgConfig];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["golang"] = "go", ["python"] = "python3", ["rust"] = "rustc", ["java"] = "javac", ["jdk"] = "javac", ["nodejs"] = "node",
        [".net"] = "dotnet", ["dotnet-sdk"] = "dotnet", ["cc"] = "clang", ["c++"] = "clang", ["clang++"] = "clang", ["g++"] = "gcc",
    };

    /// <summary>The catalogue entry for a tool name (case-insensitive, with common aliases), or null.</summary>
    public static ToolDefinition? Find(string name)
    {
        var id = Aliases.GetValueOrDefault(name, name);
        return All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A tool a project declares that HCG has no entry for: found on PATH and probed with --version / version. No
    /// install action is offered for it (HCG never installs what a project names).
    /// </summary>
    public static ToolDefinition Generic(string name) => new()
    {
        Id = name, DisplayName = name, Executables = [name], VersionArguments = [["--version"], ["version"]], Generic = true,
        OfficialUrl = "",
    };

    public static ToolDefinition For(string name) => Find(name) ?? Generic(name);

    /// <summary>Where an installation comes from, from its location (and, for links, the file they point to).</summary>
    public static string SourceOf(string path, string? linkTarget, string home)
    {
        string Rel(string p) => p.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + p[home.Length..] : p;
        foreach (var p in new[] { linkTarget, path }.OfType<string>())
        {
            var r = Rel(p);
            if (r.StartsWith("/opt/homebrew/", StringComparison.Ordinal)) return "Homebrew (Apple Silicon)";
            if (r.StartsWith("/usr/local/Cellar/", StringComparison.Ordinal) || r.StartsWith("/usr/local/opt/", StringComparison.Ordinal)) return "Homebrew (Intel)";
            if (r.StartsWith("/usr/local/go/", StringComparison.Ordinal)) return "Official Go installer";
            if (r.StartsWith("~/sdk/", StringComparison.Ordinal)) return "golang.org/dl SDK";
            if (r.StartsWith("~/go/pkg/mod/golang.org/toolchain", StringComparison.Ordinal)) return "Go toolchain cache";
            if (r.StartsWith("~/.asdf/", StringComparison.Ordinal)) return "asdf";
            if (r.StartsWith("~/.local/share/mise/", StringComparison.Ordinal)) return "mise";
            if (r.StartsWith("~/.nvm/", StringComparison.Ordinal)) return "nvm";
            if (r.StartsWith("~/.volta/", StringComparison.Ordinal)) return "Volta";
            if (r.StartsWith("~/.cargo/", StringComparison.Ordinal)) return "rustup";
            if (r.StartsWith("~/.pyenv/", StringComparison.Ordinal)) return "pyenv";
            if (r.StartsWith("/opt/local/", StringComparison.Ordinal)) return "MacPorts";
            if (r.StartsWith("/Library/Frameworks/Python.framework/", StringComparison.Ordinal)) return "python.org installer";
            if (r.StartsWith("/Applications/Docker.app/", StringComparison.Ordinal)) return "Docker Desktop";
            if (r.StartsWith("/Library/Java/JavaVirtualMachines/", StringComparison.Ordinal)) return "JDK installer";
        }
        if (path.StartsWith("/usr/bin/", StringComparison.Ordinal)) return OperatingSystem.IsMacOS() ? "macOS (Xcode / Command Line Tools)" : "System package";
        if (path.StartsWith("/usr/local/bin/", StringComparison.Ordinal)) return "/usr/local/bin";
        return "";
    }
}
