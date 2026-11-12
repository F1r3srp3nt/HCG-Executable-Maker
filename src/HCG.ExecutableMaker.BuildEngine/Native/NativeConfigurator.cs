using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Native;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.BuildEngine.Native;

public sealed record NativeConfigureResult(bool Succeeded, NativeProjectInfo? Project, string? Error, IReadOnlyList<string> Log);

/// <summary>
/// Configures a CMake project on this computer to discover its real targets through the CMake File API. Configuring
/// runs the project's CMake code (and anything it executes), so this needs the user's explicit trust confirmation and
/// runs through the same <see cref="IBuildWorker"/> boundary as a build: scrubbed environment, cancellation, timeout,
/// and a build folder owned by HCG (never inside the imported source). Downloads by FetchContent are blocked unless
/// the user allowed them.
/// </summary>
public sealed class NativeConfigurator(IBuildWorker worker, IToolchainService toolchains, IAppPaths paths)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<NativeConfigureResult> ConfigureAsync(ProjectRecord project, string manifestPath, NativeBuildOptions options, bool trustAcknowledged,
        Action<string>? onOutput, CancellationToken cancellationToken)
    {
        if (!trustAcknowledged)
            throw new InvalidOperationException("Configuring a CMake project runs its code. It requires the user's explicit trust confirmation. " + TrustPolicy.TrustBoundaryExplanation);
        var log = new List<string>();
        void Log(string line) { lock (log) log.Add(line); onOutput?.Invoke(SecretRedactor.Redact(line)); }

        var native = project.Analysis?.Native?.Projects.FirstOrDefault(p => p.ManifestPath == manifestPath)
                     ?? throw new ArgumentException($"'{manifestPath}' is not a C/C++ project in this workspace.", nameof(manifestPath));
        if (native.Kind != NativeProjectKind.CMake) throw new ArgumentException("Only CMake projects are configured.", nameof(manifestPath));
        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var assessment = NativeCapabilities.Evaluate(native, report);
        if (assessment.Support != SupportLevel.Supported)
            return new(false, null, assessment.Summary + " " + string.Join(" ", assessment.Remediation), log);

        var source = PathGuard.GetRealPath(project.SourceDirectory);
        var dir = NativeBuildPlanner.ProjectDirectory(source, native.Directory);
        var buildDir = BuildDirectory(project, native.Directory);
        if (Directory.Exists(buildDir)) Directory.Delete(buildDir, recursive: true);
        CMakeFileApi.WriteQuery(buildDir);
        var tools = NativeBuildPlanner.Tools.From(report);
        var args = NativeBuildPlanner.ConfigureArguments(dir, buildDir, options, tools, report.Host.Architecture == TargetArchitecture.X64 ? TargetArchitecture.X64 : TargetArchitecture.Arm64);
        var spec = new ProcessSpec { Executable = tools.CMake!, Arguments = args, WorkingDirectory = dir, Environment = worker.CreateEnvironment().Environment, Timeout = Timeout };
        Log("$ " + spec.DisplayCommand);
        var result = await worker.RunAsync(spec, (_, line) => Log(line), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut) return new(false, null, $"CMake configuration exceeded {Timeout.TotalMinutes:0} minutes and was stopped.", log);
        if (result.ExitCode != 0)
        {
            var hint = !options.AllowDownloads && native.DownloadEvidence.Count > 0
                ? " The project downloads dependencies during configuration (FetchContent), which HCG blocked: allow downloads for this project if you expect that."
                : log.Any(l => l.Contains("Could not find a package", StringComparison.Ordinal) || l.Contains("find_package", StringComparison.Ordinal))
                    ? " A package the project needs (find_package) is not installed on this computer; HCG does not install it."
                    : "";
            return new(false, null, $"CMake configuration failed with exit code {result.ExitCode}. The output above contains CMake's reason.{hint}", log);
        }

        CMakeFileApi.Reply reply;
        try { reply = CMakeFileApi.ReadReply(buildDir, dir); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException or KeyNotFoundException)
        {
            return new(false, null, "CMake configured the project, but its File API reply could not be read: " + ex.Message, log);
        }
        var config = reply.IsMultiConfig ? (reply.Configurations.Contains(options.BuildType) ? options.BuildType : reply.Configurations.FirstOrDefault() ?? "")
                                         : reply.Configurations.FirstOrDefault() ?? "";
        var configured = new NativeConfiguration
        {
            Generator = reply.Generator, IsMultiConfig = reply.IsMultiConfig, Configurations = reply.Configurations, Configuration = config,
            Preset = options.Preset, CMakeVersion = reply.CMakeVersion, Compilers = reply.Compilers,
            Targets = reply.TargetsByConfiguration.GetValueOrDefault(config) ?? [], CacheHighlights = reply.Cache,
            DownloadsAllowed = options.AllowDownloads, ConfiguredAt = DateTimeOffset.UtcNow,
        };
        Log($"Configured with {reply.Generator}{(reply.IsMultiConfig ? " (multi-configuration: " + string.Join(", ", reply.Configurations) + ")" : $" ({(config.Length == 0 ? "no CMAKE_BUILD_TYPE" : config)})")}: " +
            $"{configured.Executables.Count()} executable target(s), {configured.Libraries.Count()} librar{(configured.Libraries.Count() == 1 ? "y" : "ies")}.");
        foreach (var c in reply.Compilers) Log($"  {c.Language} compiler: {c.Id} {c.Version} ({c.Path})");
        return new(true, native with { Configured = configured }, null, log);
    }

    /// <summary>HCG's configure folder for one project directory: outside the imported source, per project.</summary>
    public string BuildDirectory(ProjectRecord project, string projectDirectory) =>
        Path.Combine(paths.Root, "native", project.Id.ToString("N"), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projectDirectory)))[..12], "configure");
}
