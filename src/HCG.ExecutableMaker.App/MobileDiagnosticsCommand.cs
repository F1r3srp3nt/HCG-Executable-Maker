using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// `HCGExecutableMaker --diagnose-mobile [--project ID|NAME] [--scheme S] [--reproduce] [--out FILE]`: the mobile
/// diagnostic report without the UI, for every imported Xcode project (or one). The imported copy is inspected again in
/// memory (nothing is saved); --reproduce also runs xcodebuild directly on that copy with HCG's selected Xcode.
/// The report is printed and written to HCG's data folder (diagnostics/).
/// </summary>
internal static class MobileDiagnosticsCommand
{
    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        services.GetRequiredService<DeveloperDirectorySelection>().InitializeAsync().GetAwaiter().GetResult();
        var report = services.GetRequiredService<IToolchainService>().GetAsync(refresh: true).GetAwaiter().GetResult();
        var inspection = services.GetRequiredService<IProjectInspectionService>();
        var projectService = services.GetRequiredService<ProjectService>();
        var all = services.GetRequiredService<IProjectRepository>().ListAsync().GetAwaiter().GetResult();
        var wanted = Arg("project");
        var chosen = all.Where(p => wanted is null ? p.Analysis?.Mobile?.Ios is not null
                : p.Id.ToString("N").StartsWith(wanted.Replace("-", ""), StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (chosen.Count == 0)
        {
            Console.WriteLine(HostInfo.BuildIdentity);
            Console.WriteLine($"HCG data folder: {paths.Root}");
            Console.WriteLine(wanted is null ? "No imported Xcode (iOS) project." : $"No imported project matches “{wanted}”. Imported: {string.Join(", ", all.Select(p => $"{p.Name} ({p.Id:N})"))}");
            return 2;
        }
        var dir = Path.Combine(paths.Root, "diagnostics");
        Directory.CreateDirectory(dir);
        foreach (var project in chosen)
        {
            // The copy as it is now, not the analysis saved at import.
            if (Directory.Exists(project.SourceDirectory))
                project.Analysis = inspection.InspectAsync(project.SourceDirectory, report, CancellationToken.None).GetAwaiter().GetResult();
            var scheme = Arg("scheme") ?? projectService.GetMobileSchemeAsync(project.Id).GetAwaiter().GetResult();
            var text = MobileDiagnosticReport.Create(project, report, scheme, paths, DateTimeOffset.UtcNow);
            if (args.Contains("--reproduce"))
                text += Environment.NewLine + MobileDiagnosticReport.ReproduceAsync(project, report, scheme, services.GetRequiredService<IProcessRunner>(), dir, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(text);
            var file = Arg("out") is { } o && chosen.Count == 1 ? Path.GetFullPath(o) : Path.Combine(dir, $"mobile-{project.Id:N}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, text);
            Console.WriteLine($"Report saved: {file}");
            Console.WriteLine();
        }
        return 0;
    }
}
