using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.ProjectInspection.Node;

namespace HCG.ExecutableMaker.BuildEngine.Electron;

/// <summary>
/// Plans an Electron build that follows the project's own electron-builder workflow, as three visible stages:
/// install dependencies (npm ci / npm install), compile (the npm scripts the packaging script runs before
/// electron-builder), and package (electron-builder with the project's configuration, for the chosen platform,
/// architecture and targets, publishing disabled). Every command is an argument list run with the inspected Node.js;
/// no shell string is built by HCG. electron-builder's output is directed into HCG's own build folder.
/// </summary>
public static partial class ElectronBuildPlanner
{
    public sealed record PlanInput(BuildSettings Settings, string SourceDirectory, NodeInstallation Node, string OutputDirectory, IReadOnlyDictionary<string, string> Environment);

    public sealed record Manifest(string? EnginesNode, string? EnginesNpm, IReadOnlyDictionary<string, string> Scripts, string? Main, string? Name, string? ProductName);

    public static readonly IReadOnlySet<string> MacTargets = new HashSet<string> { "dir", "zip", "dmg" };
    public static readonly IReadOnlySet<string> LinuxTargets = new HashSet<string> { "dir" };

    public static string ProjectDirectory(BuildSettings s, string sourceDirectory)
    {
        var e = s.Electron ?? throw new BuildValidationException("Electron build options are missing.");
        if (e.ProjectDirectory.Split('/').Any(seg => seg is ".." or "."))
            throw new BuildValidationException("The Electron project directory must be inside the workspace.");
        var dir = Path.GetFullPath(Path.Combine(sourceDirectory, e.ProjectDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsWithin(sourceDirectory, dir))
            throw new BuildValidationException("The Electron project directory is outside the project workspace.");
        return dir;
    }

    /// <summary>package.json as it is now (the build re-reads it; it may have changed since inspection).</summary>
    public static Manifest ReadManifest(string projectDir)
    {
        var path = Path.Combine(projectDir, "package.json");
        if (!File.Exists(path)) throw new BuildValidationException($"{path} does not exist.");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var engines = root.TryGetProperty("engines", out var en) ? en : default;
            var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("scripts", out var sc) && sc.ValueKind == JsonValueKind.Object)
                foreach (var p in sc.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) scripts[p.Name] = p.Value.GetString()!;
            return new Manifest(Str(engines, "node"), Str(engines, "npm"), scripts, Str(root, "main"), Str(root, "name"), Str(root, "productName"));
        }
        catch (JsonException ex) { throw new BuildValidationException($"package.json could not be read: {ex.Message}"); }
    }

    public static void Validate(BuildSettings settings, HostEnvironment host, string sourceDirectory)
    {
        var e = settings.Electron ?? throw new BuildValidationException("Electron build options are missing.");
        var combos = NodeCapabilities.Combinations(host);
        var combo = combos.FirstOrDefault(c => c.Platform == settings.Platform && c.Architecture == settings.Architecture);
        if (combo is null)
            throw new BuildValidationException(combos.Count == 0
                ? $"HCG does not package Electron apps on {host.Platform.DisplayName()} in this version (macOS and Linux hosts are implemented)."
                : $"{settings.Platform.DisplayName()} {settings.Architecture.DisplayName()} is not an Electron target HCG builds on this {host.Platform.DisplayName()} host. Available: {string.Join(", ", combos.Select(c => $"{c.Platform.DisplayName()} {c.Architecture.DisplayName()}"))}.");
        if (e.Platform != combo.BuilderPlatform)
            throw new BuildValidationException($"The electron-builder platform '{e.Platform}' does not match the target {settings.Platform.DisplayName()}.");
        var allowed = combo.BuilderPlatform == "mac" ? MacTargets : LinuxTargets;
        if (e.Targets.Count == 0 || e.Targets.Any(t => !allowed.Contains(t)))
            throw new BuildValidationException($"electron-builder targets must be among: {string.Join(", ", allowed)} (got: {string.Join(", ", e.Targets)}).");

        var dir = ProjectDirectory(settings, sourceDirectory);
        var manifest = ReadManifest(dir);
        foreach (var step in e.CompileSteps)
        {
            if (step.NpmScript is { } s)
            {
                if (!ElectronPackagingScripts.ScriptNameRegex().IsMatch(s))
                    throw new BuildValidationException($"'{s}' is not a valid npm script name.");
                if (!manifest.Scripts.ContainsKey(s))
                    throw new BuildValidationException($"package.json no longer has the script \"{s}\". Inspect the project again.");
            }
            else if (step.NodeArguments is [var file, ..] args)
            {
                if (file.StartsWith('-') || file.Split('/', '\\').Contains("..") || Path.IsPathRooted(file) || !args.All(a => SafeArgRegex().IsMatch(a)))
                    throw new BuildValidationException($"The compile command node {string.Join(' ', args)} is not one HCG runs (the script must be a file inside the project).");
                if (!File.Exists(Path.Combine(dir, file)))
                    throw new BuildValidationException($"The compile script {file} does not exist in the project.");
            }
            else throw new BuildValidationException("A compile step has neither an npm script nor a node command.");
        }
        if (e.ConfigPath is { } cfg)
        {
            var full = Path.GetFullPath(Path.Combine(dir, cfg));
            if (!PathGuard.IsWithin(dir, full) || !File.Exists(full))
                throw new BuildValidationException($"The electron-builder configuration {cfg} does not exist inside the project.");
        }
        if (e.UseLockfile && !File.Exists(Path.Combine(dir, "package-lock.json")) && !File.Exists(Path.Combine(dir, "npm-shrinkwrap.json")))
            throw new BuildValidationException("package-lock.json was removed since inspection. Inspect the project again.");
        if (!BundleIdRegex().IsMatch(settings.Identity.ApplicationId) && settings.Identity.ApplicationId.Length > 0)
            throw new BuildValidationException($"'{settings.Identity.ApplicationId}' is not a valid application id.");
    }

    /// <summary>The environment for npm/electron-builder: HCG's scrubbed environment, with the inspected Node first on PATH.</summary>
    public static IReadOnlyDictionary<string, string> Environment(IReadOnlyDictionary<string, string> scrubbed, NodeInstallation node)
    {
        var env = new Dictionary<string, string>(scrubbed, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var bin = Path.GetDirectoryName(node.NodePath)!;
        var pathKey = env.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        var current = env.GetValueOrDefault(pathKey, "");
        // A Finder launch has a minimal PATH; the system tool folders electron-builder needs (codesign, hdiutil, ditto) are appended.
        var extra = OperatingSystem.IsWindows() ? [] : new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" };
        var parts = new List<string> { bin };
        parts.AddRange(current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Where(p => p != bin));
        parts.AddRange(extra.Where(x => !parts.Contains(x)));
        env[pathKey] = string.Join(Path.PathSeparator, parts);
        env["npm_config_update_notifier"] = "false";
        env["npm_config_fund"] = "false";
        env["npm_config_audit"] = "false";
        env["npm_config_progress"] = "false";
        return env;
    }

    public static IReadOnlyList<BuildStep> Plan(PlanInput input)
    {
        var s = input.Settings;
        var e = s.Electron!;
        var dir = ProjectDirectory(s, input.SourceDirectory);
        var node = input.Node.NodePath;
        var npm = input.Node.NpmCli ?? throw new BuildValidationException($"npm was not found next to Node.js {input.Node.Version} ({input.Node.NodePath}).");
        ProcessSpec Spec(IReadOnlyList<string> args) => new() { Executable = node, Arguments = args, WorkingDirectory = dir, Environment = input.Environment };

        var steps = new List<BuildStep>
        {
            new(BuildStage.Restoring, e.UseLockfile ? "Install dependencies (npm ci)" : "Install dependencies (npm install)",
                Spec([npm, e.UseLockfile ? "ci" : "install", "--no-audit", "--no-fund"])) { StdErrIsProgress = true },
        };
        foreach (var c in e.CompileSteps)
            steps.Add(new BuildStep(BuildStage.Compiling, $"Compile: {c.Display}",
                c.NpmScript is { } script ? Spec([npm, "run", script]) : Spec([.. c.NodeArguments!])) { StdErrIsProgress = true });

        var builderCli = Path.Combine(dir, "node_modules", "electron-builder", "cli.js");
        var arch = s.Architecture switch { TargetArchitecture.Arm64 => "--arm64", TargetArchitecture.X64 => "--x64", _ => "--universal" };
        var args = new List<string> { builderCli, "--" + e.Platform };
        args.AddRange(e.Targets);
        args.Add(arch);
        args.AddRange(["--publish", "never"]);
        if (e.ConfigPath is { } cfg) args.AddRange(["--config", cfg]);
        args.Add("--config.directories.output=" + input.OutputDirectory);
        var required = new List<(string, string)>
        {
            (builderCli, "electron-builder is not installed in node_modules after npm install. Add electron-builder to devDependencies."),
        };
        if (e.MainEntry is { } main)
            required.Add((Path.Combine(dir, main.TrimStart('.', '/').Replace('/', Path.DirectorySeparatorChar)),
                e.CompileSteps.Count > 0
                    ? $"The compile step ({string.Join(" && ", e.CompileSteps.Select(c => c.Display))}) finished, but the main entry {main} was not produced."
                    : $"The main entry {main} does not exist."));
        steps.Add(new BuildStep(BuildStage.Publishing, $"Package with electron-builder (--{e.Platform} {string.Join(' ', e.Targets)} {arch})", Spec(args))
        {
            RequiredFiles = required, StdErrIsProgress = true,
        });
        return steps;
    }

    /// <summary>
    /// Finds what electron-builder produced: the .app (mac: mac-arm64/, mac/ for x64, mac-universal/) or the Linux
    /// executable in linux-unpacked/. Returns the path relative to the output folder, or a precise problem.
    /// </summary>
    public static (string? Relative, string? Problem) Discover(string outputDir, BuildSettings settings, string? productName)
    {
        if (!Directory.Exists(outputDir)) return (null, $"electron-builder did not create its output folder ({outputDir}).");
        var e = settings.Electron!;
        if (e.Platform == "mac")
        {
            var folder = settings.Architecture switch { TargetArchitecture.Arm64 => "mac-arm64", TargetArchitecture.X64 => "mac", _ => "mac-universal" };
            var dir = Path.Combine(outputDir, folder);
            var apps = Directory.Exists(dir) ? Directory.GetDirectories(dir, "*.app") : [];
            if (apps.Length == 0)
            {
                var found = Directory.EnumerateDirectories(outputDir).Select(Path.GetFileName);
                return (null, $"electron-builder reported success, but no .app was found in {folder}/ (output contains: {string.Join(", ", found.DefaultIfEmpty("nothing"))}).");
            }
            var app = apps.FirstOrDefault(a => productName is not null && Path.GetFileName(a) == productName + ".app") ?? (apps.Length == 1 ? apps[0] : null);
            if (app is null) return (null, $"Several apps were found in {folder}/: {string.Join(", ", apps.Select(Path.GetFileName))}.");
            return (folder + "/" + Path.GetFileName(app), null);
        }
        var unpacked = Directory.GetDirectories(outputDir, "linux*-unpacked").FirstOrDefault();
        if (unpacked is null) return (null, "electron-builder reported success, but no linux-unpacked folder was produced.");
        var helpers = new HashSet<string>(StringComparer.Ordinal) { "chrome-sandbox", "chrome_crashpad_handler" };
        var exes = Directory.GetFiles(unpacked)
            .Where(f => !helpers.Contains(Path.GetFileName(f)) && !Path.GetFileName(f).Contains(".so"))
            .Where(f => BinaryInspector.Inspect(f).Format == BinaryFormat.Elf)
            .ToList();
        if (exes.Count != 1)
            return (null, exes.Count == 0 ? $"No application executable was found in {Path.GetFileName(unpacked)}/." : $"Several executables were found in {Path.GetFileName(unpacked)}/: {string.Join(", ", exes.Select(Path.GetFileName))}.");
        return (Path.GetFileName(unpacked) + "/" + Path.GetFileName(exes[0]), null);
    }

    /// <summary>Version of a package installed in node_modules (read from its package.json), for the build record.</summary>
    public static string? InstalledVersion(string projectDir, string package)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDir, "node_modules", package, "package.json")));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._@/=:+,-]+$")]
    private static partial Regex SafeArgRegex();

    [GeneratedRegex(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")]
    private static partial Regex BundleIdRegex();
}

/// <summary>npm and electron-builder diagnostics ("npm error …", "npm warn …", electron-builder's "⨯" failures).</summary>
public sealed partial class ElectronOutputParser : IBuildOutputParser
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IBuildOutputParser Clone()
    {
        var copy = new ElectronOutputParser();
        copy._seen.UnionWith(_seen);
        return copy;
    }

    public BuildMessage? Parse(string line)
    {
        BuildMessage? m = null;
        if (NpmRegex().Match(line) is { Success: true } n)
            m = new BuildMessage(n.Groups["sev"].Value.StartsWith("warn", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, n.Groups["msg"].Value.Trim(), "npm");
        else if (BuilderErrorRegex().Match(line) is { Success: true } b)
            m = new BuildMessage(DiagnosticSeverity.Error, b.Groups["msg"].Value.Trim(), "electron-builder");
        else if (TscRegex().Match(line) is { Success: true } t)
            m = new BuildMessage(DiagnosticSeverity.Error, t.Groups["msg"].Value.Trim(), t.Groups["code"].Value, t.Groups["file"].Value, int.Parse(t.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture));
        if (m is null || m.Text.Length == 0) return null;
        // npm repeats boilerplate lines ("A complete log of this run can be found in") — keep the first of each.
        return _seen.Add($"{m.Severity}|{m.File}|{m.Line}|{m.Text}") ? m : null;
    }

    [GeneratedRegex(@"^npm (?<sev>error|ERR!|warn|WARN)\s+(?<msg>.+)$")]
    private static partial Regex NpmRegex();

    [GeneratedRegex(@"^\s*⨯\s+(?<msg>.+)$")]
    private static partial Regex BuilderErrorRegex();

    [GeneratedRegex(@"^(?<file>[^\s(]+\.(?:ts|tsx|mts|cts))\((?<line>\d+),\d+\): error (?<code>TS\d+): (?<msg>.+)$")]
    private static partial Regex TscRegex();
}
