using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Readiness;

/// <summary>
/// How a missing tool can be obtained, and the only installs HCG itself performs:
/// <list type="bullet">
/// <item>Homebrew on macOS, when Homebrew is already installed (HCG never installs Homebrew or runs installer scripts),
/// with the formula from HCG's catalogue, as an argument vector (no shell), and only after the person approves the exact
/// command. A formula that is already installed is never upgraded or replaced (another project may depend on it):
/// HOMEBREW_NO_INSTALL_UPGRADE and HOMEBREW_NO_INSTALLED_DEPENDENTS_CHECK keep Homebrew from changing what exists.</item>
/// <item>Apple's Command Line Tools installer (xcode-select --install), which opens Apple's own dialog.</item>
/// </list>
/// Everything else is a link to the official download or a command shown for the person to run.
/// </summary>
public sealed class DependencyInstaller(IProcessRunner runner, ToolHost? host = null)
{
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    private readonly ToolHost _host = host ?? ToolHost.Current;

    /// <summary>Homebrew's own executable: Apple Silicon prefix first, then Intel. Null when Homebrew is not installed.</summary>
    public string? Homebrew()
    {
        if (_host.Platform != TargetPlatform.MacOS) return null;
        foreach (var brew in new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" })
            if (_host.FileExists(brew)) return brew;
        return null;
    }

    /// <summary>The prefix a Homebrew executable belongs to (/opt/homebrew or /usr/local).</summary>
    public static string PrefixOf(string brew) => Path.GetDirectoryName(Path.GetDirectoryName(brew)!)!;

    /// <summary>Homebrew records each installed formula as &lt;prefix&gt;/opt/&lt;formula&gt;.</summary>
    public bool FormulaInstalled(string brew, string formula) =>
        _host.FileExists(Path.Combine(PrefixOf(brew), "opt", formula)) || _host.ListDirectory(Path.Combine(PrefixOf(brew), "opt")).Any(e => Path.GetFileName(e) == formula);

    /// <summary>Install routes for <paramref name="tool"/> on this computer, given what resolution found.</summary>
    public IReadOnlyList<InstallOption> Options(ToolDefinition tool, ReadinessState state, string? minimumVersion)
    {
        var options = new List<InstallOption>();
        if (tool.Generic) return options; // a name a project declared: HCG installs nothing it does not know
        var p = _host.Platform;
        if (p == TargetPlatform.MacOS && tool.FromAppleDeveloperTools && state is ReadinessState.NotFound or ReadinessState.SetupRequired)
            options.Add(new InstallOption
            {
                Method = InstallMethod.AppleCommandLineTools,
                Label = "Install Apple's Command Line Tools",
                CommandLine = "xcode-select --install",
                Executable = "/usr/bin/xcode-select",
                Arguments = ["--install"],
                Note = "Opens Apple's installer. Xcode from the App Store also provides these tools.",
            });
        if (p == TargetPlatform.MacOS && tool.HomebrewFormula is { } formula)
        {
            var brew = Homebrew();
            if (brew is null)
                options.Add(new InstallOption
                {
                    Method = InstallMethod.PackageManagerInstructions, Label = "Homebrew is not installed",
                    CommandLine = $"brew install {formula}", Url = "https://brew.sh",
                    Note = "HCG does not install Homebrew. Install it yourself from brew.sh if you want to use it, then press Refresh.",
                });
            else if (FormulaInstalled(brew, formula))
                options.Add(new InstallOption
                {
                    Method = InstallMethod.PackageManagerInstructions, Label = $"Homebrew's {formula} is already installed",
                    CommandLine = $"brew upgrade {formula}",
                    Note = $"HCG does not upgrade it: other projects may rely on the installed version. Upgrade it yourself only if every project can move" +
                           (minimumVersion is null ? "" : $", or install {tool.DisplayName} {minimumVersion}+ side by side from {tool.OfficialUrl} and choose it for this project") + ".",
                });
            else
                options.Add(new InstallOption
                {
                    Method = InstallMethod.Homebrew,
                    Label = $"Install with Homebrew ({(PrefixOf(brew) == "/opt/homebrew" ? "Apple Silicon" : "Intel")})",
                    CommandLine = $"{brew} install {formula}",
                    Executable = brew,
                    Arguments = ["install", formula],
                    Environment = new Dictionary<string, string>
                    {
                        ["HOMEBREW_NO_INSTALL_UPGRADE"] = "1",
                        ["HOMEBREW_NO_INSTALLED_DEPENDENTS_CHECK"] = "1",
                        ["HOMEBREW_NO_ENV_HINTS"] = "1",
                        ["HOMEBREW_NO_ANALYTICS"] = "1",
                    },
                    Note = $"Installs Homebrew's current {formula} next to any other installation; nothing installed is upgraded or removed.",
                });
        }
        if (tool.OfficialUrl.Length > 0)
            options.Add(new InstallOption { Method = InstallMethod.Official, Label = $"Official {tool.DisplayName} download", Url = tool.OfficialUrl });
        var instructions = p switch { TargetPlatform.Linux => tool.LinuxInstructions, TargetPlatform.Windows => tool.WindowsInstructions, _ => null };
        if (instructions is not null)
            options.Add(new InstallOption { Method = InstallMethod.PackageManagerInstructions, Label = "Install it yourself", CommandLine = instructions, Note = "HCG shows this command; it does not run it." });
        return options;
    }

    /// <summary>
    /// Runs a runnable option. <paramref name="approved"/> must come from the person confirming the exact
    /// <see cref="InstallOption.CommandLine"/>; HCG never calls this on its own.
    /// </summary>
    public async Task<ProcessResult> InstallAsync(InstallOption option, bool approved, Action<string> output, CancellationToken ct)
    {
        if (!approved) throw new InvalidOperationException("Installing needs the person's explicit approval of the command.");
        if (!option.Runnable) throw new InvalidOperationException($"“{option.Label}” is an instruction, not something HCG runs.");
        if (option.Method == InstallMethod.Homebrew)
        {
            if (Homebrew() != option.Executable) throw new InvalidOperationException("Homebrew is not where this install option expected it. Press Refresh and try again.");
            if (option.Arguments is not ["install", var formula] || ToolCatalog.All.All(t => t.HomebrewFormula != formula))
                throw new InvalidOperationException("Only formulae from HCG's catalogue are installed.");
            if (_host.IsRoot()) throw new InvalidOperationException("Homebrew must not run as root.");
        }
        else if (option.Method == InstallMethod.AppleCommandLineTools)
        {
            if (option.Executable != "/usr/bin/xcode-select" || option.Arguments is not ["--install"]) throw new InvalidOperationException("Unexpected Command Line Tools command.");
        }
        else throw new InvalidOperationException($"{option.Method} is not run by HCG.");

        var env = new Dictionary<string, string>(Security.BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment, StringComparer.Ordinal);
        if (option.Method == InstallMethod.Homebrew)
            env["PATH"] = $"{PrefixOf(option.Executable!)}/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        foreach (var kv in option.Environment) env[kv.Key] = kv.Value;
        output($"$ {option.CommandLine}");
        return await runner.RunAsync(new ProcessSpec
        {
            Executable = option.Executable!, Arguments = option.Arguments, Environment = env, Timeout = InstallTimeout,
            WorkingDirectory = _host.Home,
        }, (_, line) => output(line), ct).ConfigureAwait(false);
    }
}
