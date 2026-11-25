using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.Impactor;
using HCG.Impactor.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary><c>--impactor &lt;command&gt;</c>: HCG Impactor without the UI (see <see cref="ImpactorCli.Usage"/>).</summary>
internal static class ImpactorCommand
{
    public static int Run(string[] args)
    {
        var rest = args.SkipWhile(a => a != "--impactor").Skip(1).ToArray();
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        Console.WriteLine(HostInfo.BuildIdentity);
        // Apply HCG's own Xcode choice (DEVELOPER_DIR) before devicectl/codesign run, exactly as the app does.
        try { services.GetRequiredService<DeveloperDirectorySelection>().InitializeAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("Note: could not load HCG's Xcode selection: " + ex.Message); }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var cli = new ImpactorCli(services.GetRequiredService<ImpactorService>(), Console.Out, HostInfo.ProductVersion, InstallationId(paths));
        return cli.RunAsync(rest, cts.Token).GetAwaiter().GetResult();
    }

    /// <summary>A random per-installation identifier for reports (no hardware identifiers).</summary>
    internal static string InstallationId(IAppPaths paths)
    {
        var file = Path.Combine(paths.Root, "impactor", "installation-id");
        try
        {
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var id = Guid.NewGuid().ToString().ToLowerInvariant();
            File.WriteAllText(file, id);
            return id;
        }
        catch (IOException) { return "unknown"; }
    }
}
