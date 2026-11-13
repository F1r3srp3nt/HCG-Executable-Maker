using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Swift;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.BuildEngine.Swift;

public sealed record SwiftEvaluationResult(bool Succeeded, SwiftPackageInfo? Package, string? Error, IReadOnlyList<string> Log);

/// <summary>
/// Authoritative package discovery: evaluates Package.swift with <c>swift package dump-package</c> on this Mac.
/// Package.swift is executable code, so this runs only after the user trusted the project, through the same
/// <see cref="IBuildWorker"/> boundary (scrubbed environment, cancellation, timeout) as a build.
/// </summary>
public sealed class SwiftPackageEvaluator(IBuildWorker worker, IToolchainService toolchains, IAppPaths paths)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<SwiftEvaluationResult> EvaluateAsync(ProjectRecord project, string packageDirectory, bool trustAcknowledged,
        Action<string>? onOutput, CancellationToken cancellationToken)
    {
        if (!trustAcknowledged)
            throw new InvalidOperationException("Package.swift is executable code. Evaluating it requires the user's explicit trust confirmation. " + TrustPolicy.TrustBoundaryExplanation);
        var log = new List<string>();
        void Log(string line) { lock (log) log.Add(line); onOutput?.Invoke(line); }

        var package = project.Analysis?.Swift?.Packages.FirstOrDefault(p => p.Directory == packageDirectory)
                      ?? throw new ArgumentException($"'{packageDirectory}' is not a Swift package in this project.", nameof(packageDirectory));
        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var assessment = SwiftCapabilities.Evaluate(package, report);
        if (assessment.Support is not SupportLevel.Supported)
            return new SwiftEvaluationResult(false, null, assessment.Summary + " " + string.Join(" ", assessment.Remediation), log);

        var swift = report.Find(ToolchainIds.Swift)!;
        var source = PathGuard.GetRealPath(project.SourceDirectory);
        var dir = Path.GetFullPath(Path.Combine(source, packageDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsWithin(source, dir) || !File.Exists(Path.Combine(dir, "Package.swift")))
            return new SwiftEvaluationResult(false, null, $"Package.swift was not found in '{package.DisplayDirectory}'.", log);

        var stdout = new StringBuilder();
        var spec = new ProcessSpec
        {
            Executable = swift.ExecutablePath!,
            Arguments = ["package", "--scratch-path", ScratchPath(project, packageDirectory), "dump-package"],
            WorkingDirectory = dir,
            Environment = worker.CreateEnvironment().Environment,
            Timeout = Timeout,
        };
        Log("$ " + spec.DisplayCommand);
        var result = await worker.RunAsync(spec, (stream, line) =>
        {
            if (stream == OutputStream.StdOut) { lock (stdout) stdout.AppendLine(line); }
            else Log(line);
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut) return new SwiftEvaluationResult(false, null, $"Evaluating Package.swift exceeded {Timeout.TotalMinutes:0} minutes.", log);
        if (result.ExitCode != 0)
            return new SwiftEvaluationResult(false, null, $"swift package dump-package failed with exit code {result.ExitCode}. See the output for the manifest error.", log);

        SwiftPackageManifest manifest;
        try { manifest = SwiftManifestJson.Parse(stdout.ToString()); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            return new SwiftEvaluationResult(false, null, "SwiftPM printed a package description HCG could not read: " + ex.Message, log);
        }

        // Re-read executable targets' sources at their real (evaluated) paths for GUI and privacy facts.
        var index = SourceIndex.Build(source, cancellationToken);
        var facts = manifest.Targets.Where(t => t.Kind == SwiftTargetKind.Executable)
            .Select(t => SwiftPackageScanner.FactsFor(index, packageDirectory, t.Name, t.Path)).ToList();
        var updated = package with
        {
            Evaluated = manifest,
            EvaluatedAt = DateTimeOffset.UtcNow,
            EvaluatedWith = $"Swift {swift.Version} on macOS {report.Find(ToolchainIds.MacOSVersion)?.Version ?? "?"}",
            SourceFacts = facts,
        };
        Log($"Package {manifest.Name}: {manifest.ExecutableProducts.Count()} executable product(s), {manifest.LibraryProducts.Count()} library product(s), {manifest.TestTargets.Count()} test target(s).");
        return new SwiftEvaluationResult(true, updated, null, log);
    }

    /// <summary>Replaces one package in the analysis (for persisting an evaluation result).</summary>
    public static ProjectAnalysis WithPackage(ProjectAnalysis analysis, SwiftPackageInfo package)
    {
        var swift = analysis.Swift ?? new SwiftAnalysis();
        return analysis with
        {
            Swift = swift with { Packages = swift.Packages.Select(p => p.Directory == package.Directory ? package : p).ToList() },
        };
    }

    /// <summary>SwiftPM scratch space outside the imported source, per project and package (never inside the workspace copy).</summary>
    public string ScratchPath(ProjectRecord project, string packageDirectory) =>
        Path.Combine(paths.Root, "swiftpm", project.Id.ToString("N"), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(packageDirectory)))[..12]);
}
