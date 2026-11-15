using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --mobile-diagnostics</c>: Mobile Build Studio without the UI, using the app's own services, data
/// folder and saved selection (the same ones the page uses).
/// <list type="bullet">
/// <item><c>--project &lt;name or id&gt;</c> the imported project (default: the most recently opened Xcode project).</item>
/// <item><c>--import &lt;folder or .zip&gt;</c> import it first, as Import Folder / Import ZIP does.</item>
/// <item><c>--refresh</c> replace HCG's copy with the original's current files first ("Refresh from source").</item>
/// <item><c>--scheme &lt;name&gt;</c> / <c>--configuration &lt;name&gt;</c> select them and save the selection, as the page does
/// (only schemes and configurations the project actually has).</item>
/// <item><c>--build simulator|archive --trust</c> build the selection with the page's build service; <c>--trust</c> is the
/// command-line form of the per-build project-trust checkbox and is required. <c>--signing unsigned|project</c>,
/// <c>--output-dir &lt;dir&gt;</c>.</item>
/// <item><c>--out &lt;file&gt;</c> also write the report to a file.</item>
/// </list>
/// Prints the diagnostics report (no credentials). Exit code 0 when the requested steps succeeded.
/// </summary>
internal static class MobileCommand
{
    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        services.GetRequiredService<DeveloperDirectorySelection>().InitializeAsync().GetAwaiter().GetResult();
        var projects = services.GetRequiredService<ProjectService>();
        var repo = services.GetRequiredService<IProjectRepository>();
        var selections = services.GetRequiredService<IMobileSelectionStore>();
        var toolchains = services.GetRequiredService<IToolchainService>();
        var ct = CancellationToken.None;
        Console.WriteLine(HostInfo.BuildIdentity);
        Console.WriteLine("Data folder: " + paths.Root);

        ProjectRecord? project;
        if (Arg("--import") is { } source)
        {
            var full = Path.GetFullPath(source);
            Console.WriteLine($"Importing {full} …");
            project = (File.Exists(full) && full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? projects.ImportZipAsync(full, null, ct)
                : projects.ImportDirectoryAsync(full, null, ct)).GetAwaiter().GetResult().Project;
        }
        else
        {
            var all = repo.ListAsync(ct).GetAwaiter().GetResult();
            var wanted = Arg("--project");
            project = wanted is null
                ? all.FirstOrDefault(p => p.Analysis?.Mobile?.Ios is not null)
                : all.FirstOrDefault(p => p.Id.ToString() == wanted || p.Id.ToString("N") == wanted || string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                Console.Error.WriteLine(wanted is null ? "No imported Xcode project found. Use --import <folder>." : $"No imported project named or with id “{wanted}”. Imported: {string.Join(", ", all.Select(p => p.Name))}");
                return 2;
            }
        }
        if (args.Contains("--refresh"))
        {
            var (result, before) = projects.RefreshFromSourceAsync(project, null, ct).GetAwaiter().GetResult();
            Console.WriteLine($"Refreshed from {project.SourceLocation}: {before.Describe()} Now: {result.Project.SourceRevision} ({result.Project.SourceFingerprint}).");
        }
        else if (project.Analysis is null)
            projects.InspectAsync(project, ct).GetAwaiter().GetResult();

        var report = toolchains.GetAsync(refresh: true, ct).GetAwaiter().GetResult();
        var saved = selections.LoadAsync(project.Id.ToString(), ct).GetAwaiter().GetResult();
        if ((Arg("--scheme") is not null || Arg("--configuration") is not null) && project.Analysis?.Mobile?.Ios is { } iosSel)
        {
            var current = IosBuildSelection.Restore(project.Id.ToString(), iosSel, saved);
            var scheme = Arg("--scheme") ?? current.Scheme;
            if (!IosBuildSelection.Schemes(iosSel).Contains(scheme))
            {
                Console.Error.WriteLine($"Scheme “{scheme}” is not in this project. Schemes found: {string.Join(", ", IosBuildSelection.Schemes(iosSel))}.");
                return 2;
            }
            var configuration = Arg("--configuration") is { } c ? (c == "default" ? null : c) : current.Configuration;
            if (configuration is not null && !iosSel.Configurations.Contains(configuration))
            {
                Console.Error.WriteLine($"Configuration “{configuration}” is not in this project. Configurations: {string.Join(", ", iosSel.Configurations)}.");
                return 2;
            }
            saved = current with { Scheme = scheme, Configuration = configuration };
            selections.SaveAsync(saved, ct).GetAwaiter().GetResult();
            Console.WriteLine($"Selection saved: scheme {scheme}, configuration {configuration ?? "scheme default"}.");
        }

        var drift = projects.CompareWithSourceAsync(project, ct).GetAwaiter().GetResult();
        var builds = services.GetRequiredService<IBuildRepository>().ListAsync(project.Id, 1, ct).GetAwaiter().GetResult();
        var text = MobileDiagnostics.Create(new MobileDiagnosticsInput
        {
            MakerBuild = HostInfo.BuildIdentity, MakerExecutable = HostInfo.ExecutablePath, MakerAppBundle = HostInfo.AppBundlePath,
            Project = project, Drift = drift, Toolchains = report, Selection = saved, LastBuild = builds.FirstOrDefault(),
        });
        Console.WriteLine();
        Console.WriteLine(text);
        if (Arg("--out") is { } outFile) { File.WriteAllText(outFile, text); Console.WriteLine("Report written to " + Path.GetFullPath(outFile)); }

        if (Arg("--build") is not { } kind) return 0;
        if (!args.Contains("--trust"))
        {
            Console.Error.WriteLine("--build runs the project's build scripts: add --trust to confirm you trust this project (the page's checkbox).");
            return 2;
        }
        if (project.Analysis?.Mobile is not { Ios: { } ios } m) { Console.Error.WriteLine("Not an Xcode project."); return 2; }
        var output = kind switch { "simulator" => MobileOutput.IosSimulatorApp, "archive" => MobileOutput.IosArchive, "ipa" => MobileOutput.IosIpa, _ => (MobileOutput?)null };
        if (output is null) { Console.Error.WriteLine("--build takes simulator, archive or ipa."); return 2; }
        var sel = IosBuildSelection.Restore(project.Id.ToString(), ios, saved);
        var options = new MobileBuildOptions
        {
            Framework = m.Framework, Output = output.Value, Scheme = sel.Scheme, Container = sel.Container,
            Variant = IosBuildSelection.Configuration(ios, sel.Scheme, sel.Configuration, output == MobileOutput.IosSimulatorApp),
            Signing = Arg("--signing") == "unsigned" ? MobileSigningMode.Unsigned : MobileSigningMode.ProjectDefault,
        };
        var settings = MobileBuildPlanning.Settings(m, options, Arg("--output-dir") ?? paths.DefaultOutputDirectory, null, null);
        Console.WriteLine($"Building {output.Value.DisplayName()}: {XcodePlanner.Preview(options, ios)}");
        var builder = services.GetRequiredService<MobileBuildService>();
        var root = MobileBuildService.OutputRootFor(settings);
        var record = builder.RunAsync(new MobileBuildRequest
        {
            Project = project, Settings = settings, TrustAcknowledged = true,
            AllowOverwriteOutput = Directory.Exists(root) && File.Exists(Path.Combine(root, BuildService.OutputMarkerFile)),
        }, new ConsoleObserver(), ct).GetAwaiter().GetResult();
        Console.WriteLine($"Result: {record.Status}{(record.FailureReason is { } f ? " — " + (MobileBuildService.FirstErrorOf(f) ?? f) : "")}");
        foreach (var a in record.Artifacts)
            Console.WriteLine($"Artifact: {a.Format.DisplayName()} {a.Path} — {a.Verification}" +
                              string.Concat(a.Checks.Select(c => $"\n  {c.Status}: {c.Name} — {c.Detail}")));
        if (record.Provenance is { } p) Console.WriteLine($"Provenance: scheme {p.Scheme}, {p.Configuration}, {p.SourceRevision}");
        return record.Status == BuildStatus.Succeeded && record.Artifacts.All(a => a.Verification != VerificationStatus.Failed) ? 0 : 1;
    }

    private sealed class ConsoleObserver : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) => Console.WriteLine($"STAGE {stage}: {description}");
        public void OnLog(BuildLogLine line)
        {
            if (line.Kind is BuildLogKind.Command or BuildLogKind.Error or BuildLogKind.Warning or BuildLogKind.Success or BuildLogKind.Info)
                Console.WriteLine($"{line.Kind.ToString().ToUpperInvariant()} {line.Text}");
        }
    }
}
