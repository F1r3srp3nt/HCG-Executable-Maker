using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.BuildEngine.Swift;

/// <summary>
/// Carries out a Swift package's packaging contract (AppPackaging.json) around <c>swift build</c>: checks the
/// contract is unchanged since it was confirmed, locates the tools its prebuild steps require, plans those steps as
/// argument vectors (never a shell), and stages helpers, resources, the Info.plist template and the icon into the
/// publish folder at the paths the packager places them in the .app.
/// </summary>
public static partial class SwiftContractPlanner
{
    public const string StagingFolder = Packaging.Packagers.SwiftContractLayout.StagingFolder;
    public const string InfoPlistTemplateFile = Packaging.Packagers.SwiftContractLayout.InfoPlistTemplateFile;

    public static readonly TimeSpan PrebuildTimeout = TimeSpan.FromMinutes(30);

    public const string DeclinedMessage =
        "This app needs its helper programs, which AppPackaging.json builds with the commands listed under “Packaging contract”. " +
        "Confirm those commands to continue: packaging without them would produce an app that is missing parts it needs to run.";

    /// <summary>Configuration checks that need no files (wizard and build).</summary>
    public static void Validate(BuildSettings settings)
    {
        var swift = settings.Swift;
        if (swift?.PackagingContract is not { } contract) return;
        if (contract.Product != swift.Product)
            throw new BuildValidationException($"{contract.Path} describes the product {contract.Product}, not {swift.Product}.");
        if (swift.Kind != SwiftAppKind.GuiApp)
            throw new BuildValidationException($"{contract.Path} describes a macOS app (.app bundle). Deliver {swift.Product} as a macOS app.");
        if (contract.Prebuild.Count > 0 && !swift.PackagingContractConfirmed)
            throw new BuildValidationException(DeclinedMessage);
        if (contract.Helpers.Count > 0 && settings.PackagingFormats.Contains(PackagingFormat.MacAppStore))
            throw new BuildValidationException("Mac App Store packages of apps with helper executables are not supported in this version: each helper would need its own sandbox entitlements. Choose Developer ID or ad-hoc packaging.");
        foreach (var h in contract.Helpers)
        {
            var missing = MissingArchitectures(h, settings.Architecture);
            if (missing.Count > 0)
                throw new BuildValidationException($"The app is built for {string.Join(" + ", Packaging.Verification.MachO.ExpectedSlices(settings.Architecture))}, but {contract.Path} only promises {h.Name} for {string.Join(", ", h.Architectures)}.");
        }
    }

    /// <summary>App slices a helper does not promise (empty when it promises nothing or covers them all).</summary>
    public static IReadOnlyList<string> MissingArchitectures(SwiftContractHelper helper, TargetArchitecture arch) =>
        helper.Architectures.Count == 0 ? [] : Packaging.Verification.MachO.ExpectedSlices(arch).Except(helper.Architectures).ToList();

    /// <summary>
    /// Re-reads the contract from the workspace and returns it, or an error when it changed after confirmation, is
    /// gone, or no longer parses. The build always uses the file as it is now, never a stale copy.
    /// </summary>
    public static (SwiftPackagingContract? Contract, string? Error) Reload(string sourceDir, SwiftBuildOptions swift)
    {
        if (swift.PackagingContract is not { } confirmed) return (null, null);
        var (current, error) = SwiftPackagingContractReader.Read(sourceDir, swift.PackageDirectory);
        if (error is not null) return (null, error);
        if (current is null) return (null, $"{confirmed.Path} was removed after it was confirmed. Inspect the project again.");
        if (current.Sha256 != confirmed.Sha256)
            return (null, $"{confirmed.Path} changed after its commands were confirmed. Review it again (Configure › Packaging contract) before building.");
        return (current, null);
    }

    /// <summary>A tool a prebuild step requires, found on this computer.</summary>
    public sealed record ResolvedTool(string Name, string Path, string? Version);

    /// <summary>
    /// Locates every required tool (PATH plus the usual install folders, since apps opened from Finder do not inherit
    /// the shell PATH) and checks minimum versions with <c>&lt;tool&gt; version</c> or <c>&lt;tool&gt; --version</c>.
    /// </summary>
    public static async Task<(IReadOnlyList<ResolvedTool> Tools, string? Error)> ResolveToolsAsync(
        SwiftPackagingContract contract, IExecutableLocator locator, Func<ProcessSpec, CancellationToken, Task<(int ExitCode, string Output)>> capture,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var tools = new List<ResolvedTool>();
        foreach (var group in contract.Prebuild.SelectMany(p => p.Requires.Select(r => (Step: p, Req: r))).GroupBy(x => x.Req.Tool, StringComparer.Ordinal))
        {
            var name = group.Key;
            var path = locator.Find(name, ExtraCandidates(name));
            var step = group.First().Step;
            if (path is null)
                return (tools, $"{name} is required to “{step.Description}” ({contract.Path}), but it was not found on PATH or in /opt/homebrew/bin, /usr/local/bin. Install {name}, then build again.");
            var minimum = group.Select(x => x.Req.MinimumVersion).OfType<string>().OrderByDescending(v => v, Comparer<string>.Create(SwiftCapabilities.Compare)).FirstOrDefault();
            string? version = null;
            if (minimum is not null)
            {
                foreach (var args in new[] { new[] { "version" }, new[] { "--version" } })
                {
                    var (exit, output) = await capture(new ProcessSpec { Executable = path, Arguments = args, Environment = environment, Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
                    if (exit == 0 && VersionRegex().Match(output) is { Success: true } m) { version = m.Value; break; }
                }
                if (version is null)
                    return (tools, $"{name} at {path} did not report its version, and {contract.Path} needs {name} >= {minimum}.");
                if (SwiftCapabilities.Compare(version, minimum) < 0)
                    return (tools, $"{contract.Path} needs {name} >= {minimum} to “{step.Description}”, but {path} is version {version}. Update {name}, then build again.");
            }
            tools.Add(new ResolvedTool(name, path, version));
        }
        return (tools, null);
    }

    private static IEnumerable<string> ExtraCandidates(string tool) => tool switch
    {
        "go" => ["/usr/local/go/bin/go", "/opt/homebrew/bin/go"],
        _ => [],
    };

    /// <summary>
    /// The contract's prebuild commands as build steps. Each runs directly (no shell) in its working directory with the
    /// scrubbed build environment, whose PATH starts with the folders of the tools it requires.
    /// </summary>
    public static IReadOnlyList<BuildStep> PlanPrebuild(SwiftPackagingContract contract, string sourceDir, string helpersDir,
        IReadOnlyList<ResolvedTool> tools, IReadOnlyDictionary<string, string> environment, IExecutableLocator locator)
    {
        var contractDir = SwiftPackagingContractReader.ContractDirectory(contract.Path);
        var steps = new List<BuildStep>();
        foreach (var p in contract.Prebuild)
        {
            var wd = Resolve(sourceDir, contractDir, p.WorkingDirectory, "prebuild workingDirectory");
            if (!Directory.Exists(wd)) throw new BuildValidationException($"The working directory {p.WorkingDirectory} of “{p.Description}” does not exist.");
            var program = p.Command[0];
            string executable;
            if (program.Contains('/'))
            {
                executable = Path.GetFullPath(Path.Combine(wd, program.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathGuard.IsWithin(sourceDir, executable)) throw new BuildValidationException($"“{p.Description}” runs {program}, which is outside the project.");
                if (!File.Exists(executable)) throw new BuildValidationException($"“{p.Description}” runs {program}, which does not exist in the project.");
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
                    throw new BuildValidationException($"“{p.Description}” runs {program}, which is not executable. Run chmod +x {program} in the project (and commit it).");
            }
            else
            {
                executable = tools.FirstOrDefault(t => t.Name == program)?.Path ?? locator.Find(program)
                    ?? throw new BuildValidationException($"“{p.Description}” runs {program}, which was not found on this computer.");
            }
            var env = new Dictionary<string, string>(environment, StringComparer.Ordinal);
            var toolDirs = p.Requires.Select(r => tools.FirstOrDefault(t => t.Name == r.Tool)?.Path).OfType<string>()
                .Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (toolDirs.Count > 0)
                env["PATH"] = string.Join(Path.PathSeparator, toolDirs.Append(env.GetValueOrDefault("PATH") ?? "/usr/bin:/bin:/usr/sbin:/sbin"));
            steps.Add(new BuildStep(BuildStage.Preparing, $"Prebuild: {p.Description}", new ProcessSpec
            {
                Executable = executable,
                Arguments = p.Command.Skip(1).Select(a => a.Replace(SwiftPackagingContract.HelpersDirVariable, helpersDir, StringComparison.Ordinal)).ToList(),
                WorkingDirectory = wd,
                Environment = env,
                Timeout = PrebuildTimeout,
            })
            { StdErrIsProgress = true });
        }
        return steps;
    }

    /// <summary>
    /// Copies the contract's helpers, resources, Info.plist template and icon into
    /// <c>&lt;publish&gt;/.hcg-contract/</c>, mirroring the paths they take in the bundle. A missing required file fails
    /// the build with the step that should have produced it.
    /// </summary>
    public static IReadOnlyList<string> Stage(SwiftPackagingContract contract, string sourceDir, string helpersDir, string publishDir)
    {
        var contractDir = SwiftPackagingContractReader.ContractDirectory(contract.Path);
        var staging = Path.Combine(publishDir, StagingFolder);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var notes = new List<string>();

        string Source(string value, string what) => value.StartsWith(SwiftPackagingContract.HelpersDirVariable + "/", StringComparison.Ordinal)
            ? Path.Combine(helpersDir, value[(SwiftPackagingContract.HelpersDirVariable.Length + 1)..].Replace('/', Path.DirectorySeparatorChar))
            : Resolve(sourceDir, contractDir, value, what);
        string Target(string destination) => Path.Combine(staging, destination.Replace('/', Path.DirectorySeparatorChar));

        foreach (var h in contract.Helpers)
        {
            var src = Source(h.Source, $"helper {h.Name}");
            if (!File.Exists(src))
            {
                var producer = contract.Prebuild.FirstOrDefault(p => p.Command.Any(a => a.Contains(h.Source, StringComparison.Ordinal)));
                if (!h.Required) { notes.Add($"Optional helper {h.Name} was not produced ({h.Source}); the app is packaged without it."); continue; }
                throw new BuildValidationException(producer is null
                    ? $"The helper {h.Name} ({h.Source}) does not exist, and {contract.Path} marks it required."
                    : $"The prebuild step “{producer.Description}” finished, but the helper {h.Name} it should have written ({h.Source}) is missing.");
            }
            var dst = Target(h.Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dst, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            notes.Add($"Helper {h.Name} → {h.Destination}");
        }
        foreach (var r in contract.Resources)
        {
            var src = Source(r.Source, "resource");
            if (!File.Exists(src))
            {
                if (!r.Required) { notes.Add($"Optional resource {r.Source} is missing; skipped."); continue; }
                throw new BuildValidationException($"The resource {r.Source} required by {contract.Path} does not exist.");
            }
            var dst = Target(r.Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            notes.Add($"Resource {r.Source} → {r.Destination}");
        }
        if (contract.Bundle.InfoPlist is { } plist)
        {
            var src = Source(plist, "bundle.infoPlist");
            if (!File.Exists(src)) throw new BuildValidationException($"The Info.plist template {plist} named in {contract.Path} does not exist.");
            File.Copy(src, Path.Combine(staging, InfoPlistTemplateFile), overwrite: true);
            notes.Add($"Info.plist template {plist}");
        }
        if (contract.Bundle.Icon is { } icon)
        {
            var src = Source(icon, "bundle.icon");
            if (!File.Exists(src)) throw new BuildValidationException($"The icon {icon} named in {contract.Path} does not exist.");
            var dst = Target("Contents/Resources/AppIcon.icns");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            notes.Add($"Icon {icon} → Contents/Resources/AppIcon.icns");
        }
        return notes;
    }

    private static string Resolve(string sourceDir, string contractDir, string value, string what)
    {
        try { return SwiftPackagingContractReader.Resolve(sourceDir, contractDir, value, what); }
        catch (SwiftPackagingContractReader.ContractException ex) { throw new BuildValidationException(ex.Message); }
    }

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionRegex();
}
