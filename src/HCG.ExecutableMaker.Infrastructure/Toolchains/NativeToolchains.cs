using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// C/C++ tools: CMake, Ninja, make, the host's C and C++ compilers and (Windows) Visual Studio's C++ tools.
/// Detection only: version probes, nothing is installed.
/// </summary>
public sealed partial class ToolchainDetector
{
    public const string NativeCategory = "C / C++";

    private IEnumerable<Task<ToolchainInfo>> NativeTools(CancellationToken ct)
    {
        yield return DetectCMakeAsync(ct);
        yield return DetectSimpleAsync(ToolchainIds.Ninja, "Ninja", "ninja", ["--version"], NinjaVersionRegex(), NinjaHint(), ct);
        yield return DetectSimpleAsync(ToolchainIds.Make, "make", Host.Platform == TargetPlatform.Windows ? "mingw32-make" : "make", ["--version"], MakeVersionRegex(), MakeHint(), ct,
            Host.Platform == TargetPlatform.Windows ? ["make"] : ["gmake"]);
        yield return DetectCompilerAsync(ToolchainIds.CCompiler, "C compiler", ["cc", "clang", "gcc"], ct);
        yield return DetectCompilerAsync(ToolchainIds.CxxCompiler, "C++ compiler", ["c++", "clang++", "g++"], ct);
        yield return Host.Platform == TargetPlatform.Windows
            ? DetectMsvcAsync(ct)
            : Task.FromResult(Unsupported(ToolchainIds.MsvcTools, "Visual Studio C++ tools", NativeCategory, "Windows only. On this computer CMake uses the C/C++ compilers above."));
    }

    private string CompilerHint() => Host.Platform switch
    {
        TargetPlatform.MacOS => "Install the Command Line Tools (xcode-select --install) or Xcode: they provide clang and clang++.",
        TargetPlatform.Windows => "Install Visual Studio or the Build Tools for Visual Studio with the \"Desktop development with C++\" workload (https://visualstudio.microsoft.com/downloads/).",
        _ => "Install a compiler with your package manager, e.g. sudo apt install build-essential (gcc, g++, make) or clang.",
    };

    private string NinjaHint() => Host.Platform switch
    {
        TargetPlatform.MacOS => "Optional: brew install ninja. Without it HCG uses CMake's Unix Makefiles generator.",
        TargetPlatform.Windows => "Optional: Visual Studio includes Ninja; CMake's Visual Studio generator is used otherwise.",
        _ => "Optional: sudo apt install ninja-build. Without it HCG uses CMake's Unix Makefiles generator.",
    };

    private string MakeHint() => Host.Platform switch
    {
        TargetPlatform.MacOS => "Install the Command Line Tools (xcode-select --install): they include make.",
        TargetPlatform.Windows => "Only needed for Makefile projects: install MSYS2/MinGW make. CMake projects build with Visual Studio instead.",
        _ => "sudo apt install make (or build-essential).",
    };

    private async Task<ToolchainInfo> DetectCMakeAsync(CancellationToken ct)
    {
        var hint = Host.Platform switch
        {
            TargetPlatform.MacOS => "Install CMake from https://cmake.org/download/ (CMake.app) or with Homebrew (brew install cmake), then press Refresh.",
            TargetPlatform.Windows => "Install CMake from https://cmake.org/download/ or the \"C++ CMake tools\" component of Visual Studio, then press Refresh.",
            _ => "Install CMake 3.20 or later: sudo apt install cmake (or https://cmake.org/download/), then press Refresh.",
        };
        var exe = locator.Find("cmake", CMakeCandidates());
        if (exe is null) return Missing(ToolchainIds.CMake, "CMake", NativeCategory, hint) with { SetupUrl = "https://cmake.org/download/" };
        var info = await ProbeVersionAsync(ToolchainIds.CMake, "CMake", NativeCategory, exe, ["--version"], CMakeVersionRegex(), hint, ct).ConfigureAwait(false);
        if (info.IsAvailable && info.Version is { } v && Version.TryParse(v.Split('-')[0], out var parsed) && parsed < new Version(3, 20))
            return info with { Status = ToolchainStatus.IncompatibleVersion, Detail = $"CMake {v} is too old: HCG reads targets through the CMake File API and presets, which need CMake 3.20 or later.", InstallHint = hint };
        return info with { InstallHint = hint, Detail = info.IsAvailable ? "Configures projects and reports their targets (File API)." : info.Detail };
    }

    private IEnumerable<string> CMakeCandidates()
    {
        if (Host.Platform == TargetPlatform.MacOS)
            return ["/opt/homebrew/bin/cmake", "/usr/local/bin/cmake", "/Applications/CMake.app/Contents/bin/cmake"];
        if (Host.Platform == TargetPlatform.Windows)
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return [Path.Combine(pf, "CMake", "bin", "cmake.exe")];
        }
        return ["/usr/local/bin/cmake", "/usr/bin/cmake", "/snap/bin/cmake"];
    }

    private async Task<ToolchainInfo> DetectSimpleAsync(string id, string name, string command, string[] args, Regex regex, string hint, CancellationToken ct, IEnumerable<string>? alternatives = null)
    {
        var exe = locator.Find(command) ?? alternatives?.Select(a => locator.Find(a)).FirstOrDefault(p => p is not null);
        if (exe is null) return Missing(id, name, NativeCategory, hint);
        if (Host.Platform == TargetPlatform.MacOS && exe.StartsWith("/usr/bin/", StringComparison.Ordinal) && !await DeveloperToolsPresentAsync(ct).ConfigureAwait(false))
            return Missing(id, name, NativeCategory, hint) with { Detail = $"{exe} is a stub until the Command Line Tools are installed." };
        return await ProbeVersionAsync(id, name, NativeCategory, exe, args, regex, hint, ct).ConfigureAwait(false) with { InstallHint = hint };
    }

    private async Task<ToolchainInfo> DetectCompilerAsync(string id, string name, string[] commands, CancellationToken ct)
    {
        var hint = CompilerHint();
        if (Host.Platform == TargetPlatform.Windows)
        {
            // MSVC (cl.exe) is not on PATH outside a developer prompt; CMake finds it through Visual Studio (see the MSVC row).
            var mingw = commands.Skip(1).Select(c => locator.Find(c)).FirstOrDefault(p => p is not null);
            if (mingw is null)
                return Missing(id, name, NativeCategory, hint) with { Detail = "No clang/gcc on PATH. CMake projects use Visual Studio's compiler (MSVC row) when it is installed." };
            return await ProbeVersionAsync(id, name, NativeCategory, mingw, ["--version"], CompilerVersionRegex(), hint, ct).ConfigureAwait(false) with { InstallHint = hint };
        }
        var exe = commands.Select(c => locator.Find(c)).FirstOrDefault(p => p is not null);
        if (exe is null) return Missing(id, name, NativeCategory, hint);
        // On a Mac without the Command Line Tools, /usr/bin/cc is a stub that opens an installer dialog: never run it then.
        if (Host.Platform == TargetPlatform.MacOS && exe.StartsWith("/usr/bin/", StringComparison.Ordinal) && !await DeveloperToolsPresentAsync(ct).ConfigureAwait(false))
            return Missing(id, name, NativeCategory, hint) with { Detail = "The Command Line Tools are not installed (/usr/bin/cc is only a stub)." };
        try
        {
            var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = ["--version"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var text = stdout + "\n" + stderr;
            if (!r.Succeeded) return Failed(id, name, NativeCategory, exe, $"Version probe exited with code {r.ExitCode}: {FirstLine(text)}", hint);
            var first = FirstLine(text);
            return new ToolchainInfo
            {
                Id = id, DisplayName = name, Category = NativeCategory, Status = ToolchainStatus.Installed, ExecutablePath = exe,
                Version = CompilerVersionRegex().Match(text) is { Success: true } m ? m.Groups[1].Value : null,
                Detail = first, InstallHint = hint,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(id, name, NativeCategory, exe, ex.Message, hint);
        }
    }

    private async Task<bool> DeveloperToolsPresentAsync(CancellationToken ct)
    {
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/xcode-select", Arguments = ["-p"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            return r.Succeeded && Directory.Exists(stdout.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private async Task<ToolchainInfo> DetectMsvcAsync(CancellationToken ct)
    {
        const string name = "Visual Studio C++ tools";
        const string hint = "Install Visual Studio 2022 or the Build Tools for Visual Studio with the \"Desktop development with C++\" workload. HCG does not install it.";
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return Missing(ToolchainIds.MsvcTools, name, NativeCategory, hint) with { SetupUrl = "https://visualstudio.microsoft.com/downloads/" };
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec
            {
                Executable = vswhere,
                Arguments = ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationVersion"],
                Timeout = ProbeTimeout, Environment = ProbeEnvironment(),
            }, ct).ConfigureAwait(false);
            var version = stdout.Trim();
            if (!r.Succeeded || version.Length == 0)
                return Missing(ToolchainIds.MsvcTools, name, NativeCategory, hint) with { Detail = "Visual Studio is installed, but without the C++ build tools (MSVC) workload." };
            return new ToolchainInfo
            {
                Id = ToolchainIds.MsvcTools, DisplayName = name, Category = NativeCategory, Status = ToolchainStatus.Installed, ExecutablePath = vswhere,
                Version = version, Detail = "MSVC is used by CMake's Visual Studio generator.",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.MsvcTools, name, NativeCategory, vswhere, ex.Message, hint);
        }
    }

    [GeneratedRegex(@"cmake version (\S+)")] private static partial Regex CMakeVersionRegex();
    [GeneratedRegex(@"^\s*(\d+\.\d+(?:\.\d+)?)", RegexOptions.Multiline)] private static partial Regex NinjaVersionRegex();
    [GeneratedRegex(@"GNU Make (\S+)")] private static partial Regex MakeVersionRegex();
    [GeneratedRegex(@"(?:version\s+|^\S+ \(.*?\)\s+)(\d+\.\d+(?:\.\d+)?)", RegexOptions.Multiline)] private static partial Regex CompilerVersionRegex();
}
