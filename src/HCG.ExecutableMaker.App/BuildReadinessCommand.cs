using System.Text.Json;
using HCG.ExecutableMaker.BuildEngine.Readiness;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --build-readiness &lt;project folder&gt; [--json &lt;file&gt;]
/// [--tool-path &lt;tool&gt;=&lt;path&gt;] [--clear-tool-path &lt;tool&gt;] [--install &lt;tool&gt; [--approve]]</c>
/// <para>
/// The Build Readiness Center without the UI: the tools the project's files declare, what this computer has, and how to
/// get what is missing. <c>--tool-path</c> verifies a location and saves it for all projects (as Toolchains › Build
/// Readiness › Choose location does). <c>--install</c> prints the exact install command; it runs only with
/// <c>--approve</c>, only for a Homebrew formula from HCG's catalogue or Apple's Command Line Tools installer.
/// </para>
/// Exit codes: 0 every required tool ready · 5 something required is missing · 4 install needs --approve ·
/// 6 install or location rejected · 2 usage.
/// </summary>
internal static class BuildReadinessCommand
{
    public static int Run(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var dir = Arg("--build-readiness");
        if (dir is null || !Directory.Exists(dir))
        {
            Console.Error.WriteLine("Usage: --build-readiness <project folder> [--json <file>] [--tool-path <tool>=<path>] [--clear-tool-path <tool>] [--install <tool> [--approve]]");
            return 2;
        }
        dir = Path.GetFullPath(dir);
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        services.GetRequiredService<DeveloperDirectorySelection>().InitializeAsync().GetAwaiter().GetResult();
        var readiness = services.GetRequiredService<BuildReadinessService>();

        if (Arg("--tool-path") is { } tp)
        {
            var eq = tp.IndexOf('=');
            if (eq <= 0) { Console.Error.WriteLine("--tool-path expects <tool>=<path>, e.g. go=/usr/local/go/bin/go"); return 2; }
            var (check, saved) = readiness.ChooseLocationAsync(tp[..eq], tp[(eq + 1)..], null, new ToolPathScope.AllProjects()).GetAwaiter().GetResult();
            Console.WriteLine(saved ? $"Saved for all projects: {check.Detail}" : $"Not saved — {check.Detail}");
            if (!saved) return 6;
        }
        if (Arg("--clear-tool-path") is { } clear)
        {
            readiness.ClearLocationAsync(clear, new ToolPathScope.AllProjects()).GetAwaiter().GetResult();
            Console.WriteLine($"{clear}: automatic detection applies again.");
        }

        var report = readiness.CheckAsync(Path.GetFileName(dir), dir, null, null, null).GetAwaiter().GetResult();
        Print(report);

        if (Arg("--install") is { } toolName)
        {
            var item = report.Items.FirstOrDefault(i => string.Equals(i.Requirement.ToolId, ToolCatalog.For(toolName).Id, StringComparison.OrdinalIgnoreCase));
            var option = item?.Install.FirstOrDefault(o => o.Runnable)
                ?? readiness.Installer.Options(ToolCatalog.For(toolName), ReadinessState.NotFound, null).FirstOrDefault(o => o.Runnable);
            if (item?.State == ReadinessState.Ready) { Console.WriteLine($"{toolName} is already ready; nothing to install."); }
            else if (option is null)
            {
                Console.WriteLine($"HCG cannot install {toolName} here. " + string.Join(" ", (item?.Install ?? []).Select(o => $"{o.Label}: {o.CommandLine ?? o.Url}")));
                return 6;
            }
            else if (!args.Contains("--approve"))
            {
                Console.WriteLine($"Install command (not run): {option.CommandLine}");
                Console.WriteLine("Run again with --approve to let HCG run exactly this command.");
                return 4;
            }
            else
            {
                Console.WriteLine($"Approved: {option.CommandLine}");
                var result = readiness.InstallAsync(option, approved: true, line => Console.WriteLine("  " + line)).GetAwaiter().GetResult();
                if (!result.Succeeded) { Console.WriteLine($"Install failed (exit code {result.ExitCode})."); return 6; }
                Console.WriteLine("Installed. Checking again:");
                report = readiness.CheckAsync(Path.GetFileName(dir), dir, null, null, null).GetAwaiter().GetResult();
                Print(report);
            }
        }

        if (Arg("--json") is { } json)
            File.WriteAllText(json, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        return report.CanBuild ? 0 : 5;
    }

    private static void Print(BuildReadinessReport report)
    {
        Console.WriteLine($"{HostInfo.BuildIdentity}");
        Console.WriteLine($"Build Readiness — {report.Project} ({report.Target}){(report.Profile is null ? "" : $" · profile “{report.Profile}”")}");
        foreach (var i in report.Items)
        {
            var r = i.Requirement;
            Console.WriteLine($"{r.ToolId}: {i.State}{(r.Required ? "" : " (optional)")} — needs {r.DisplayName}{(r.MinimumVersion is null ? "" : $" >= {r.MinimumVersion}")}; declared by {r.DeclaredBy}");
            if (i.Selected is { } s) Console.WriteLine($"    using {s.Path} (version {s.Version ?? "not reported"}; {s.Source})");
            foreach (var f in i.Found.Where(f => f != i.Selected)) Console.WriteLine($"    also found {f.Path} (version {f.Version ?? "not reported"}; {f.Source})");
            if (i.State != ReadinessState.Ready) Console.WriteLine("    " + i.Detail);
            foreach (var n in r.Notes) Console.WriteLine("    note: " + n);
            foreach (var o in i.Install) Console.WriteLine($"    {(o.Runnable ? "install (needs approval)" : "how to get it")}: {o.Label}{(o.CommandLine is null ? "" : $" — {o.CommandLine}")}{(o.Url is null ? "" : $" — {o.Url}")}");
            foreach (var h in i.Remote) Console.WriteLine($"    remote build host: {h.Label} ({h.ToolVersion}) — {h.Transfer}");
        }
        Console.WriteLine(report.CanBuild ? "READY: every required tool is available." : $"NOT READY: {report.Blockers.Count()} required tool(s) missing.");
    }
}
