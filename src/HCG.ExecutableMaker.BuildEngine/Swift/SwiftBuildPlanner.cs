using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Swift;

/// <summary>
/// Plans <c>swift build</c> for one executable product of a Swift package. Structured arguments only; product and
/// package names are validated before use.
/// </summary>
public static partial class SwiftBuildPlanner
{
    public sealed record PlanInput(BuildSettings Settings, string SourceDirectory, string SwiftExecutable, string ScratchPath, IReadOnlyDictionary<string, string> Environment);

    public static void Validate(BuildSettings settings, TargetPlatform host)
    {
        var swift = settings.Swift ?? throw new BuildValidationException("Swift build options are missing.");
        if (settings.Platform != TargetPlatform.MacOS)
            throw new BuildValidationException("Swift packages are built for macOS in this version.");
        if (host != TargetPlatform.MacOS)
            throw new BuildValidationException("Swift macOS apps can only be built on a Mac (Xcode or the Command Line Tools are required).");
        if (!ProductNameRegex().IsMatch(swift.Product))
            throw new BuildValidationException($"'{swift.Product}' is not a valid Swift product name.");
        if (swift.PackageDirectory.Split('/').Any(s => s is ".." or "."))
            throw new BuildValidationException("The package directory must be inside the project.");
        if (swift.MinimumMacOS is { } min && !VersionRegex().IsMatch(min))
            throw new BuildValidationException($"'{min}' is not a valid macOS version (expected e.g. 14.0).");
        if (!VersionRegex().IsMatch(swift.BuildNumber))
            throw new BuildValidationException($"Build number '{swift.BuildNumber}' must be numeric, e.g. 1 or 1.2.3 (CFBundleVersion).");
        if (swift.Kind == SwiftAppKind.GuiApp)
        {
            foreach (var (key, text) in swift.UsageDescriptions)
                if (string.IsNullOrWhiteSpace(text))
                    throw new BuildValidationException($"{key} needs a description: macOS shows it to users and terminates the app without it.");
            if (!BundleIdRegex().IsMatch(settings.Identity.ApplicationId))
                throw new BuildValidationException($"'{settings.Identity.ApplicationId}' is not a valid bundle identifier (letters, digits, '-' and '.', e.g. com.example.app).");
        }
    }

    public static IReadOnlyList<BuildStep> Plan(PlanInput input)
    {
        var s = input.Settings;
        var swift = s.Swift!;
        var dir = Path.GetFullPath(Path.Combine(input.SourceDirectory, swift.PackageDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsWithin(input.SourceDirectory, dir))
            throw new BuildValidationException("The package directory is outside the project workspace.");
        var configuration = s.Configuration == BuildConfigurationKind.Debug ? "debug" : "release";
        List<string> Common() =>
        [
            "-c", configuration, "--product", swift.Product, "--scratch-path", input.ScratchPath,
            .. ArchArguments(s.Architecture),
        ];
        ProcessSpec Spec(List<string> args) => new()
        {
            Executable = input.SwiftExecutable, Arguments = args, WorkingDirectory = dir, Environment = input.Environment,
        };
        return
        [
            new BuildStep(BuildStage.Restoring, "swift package resolve", Spec(["package", "--scratch-path", input.ScratchPath, "resolve"])),
            new BuildStep(BuildStage.Compiling, $"swift build --product {swift.Product}", Spec(["build", .. Common()])),
            new BuildStep(BuildStage.Publishing, "Locate build products", Spec(["build", .. Common(), "--show-bin-path"]), CaptureStdout: true),
        ];
    }

    /// <summary>Single architecture, or both slices for a universal binary.</summary>
    public static IReadOnlyList<string> ArchArguments(TargetArchitecture arch) => arch switch
    {
        TargetArchitecture.Arm64 => ["--arch", "arm64"],
        TargetArchitecture.X64 => ["--arch", "x86_64"],
        TargetArchitecture.Universal => ["--arch", "arm64", "--arch", "x86_64"],
        _ => [],
    };

    /// <summary>
    /// Copies the built executable and SwiftPM resource bundles (*.bundle) from the bin path into HCG's staging
    /// folder. Object files, module caches and other build intermediates are not copied.
    /// </summary>
    public static (string Executable, IReadOnlyList<string> ResourceBundles) Stage(string binPath, string product, string staging)
    {
        var exe = Path.Combine(binPath, product);
        if (!File.Exists(exe))
            throw new BuildValidationException($"swift build reported success, but the product executable was not found at {exe}.");
        Directory.CreateDirectory(staging);
        File.Copy(exe, Path.Combine(staging, product), overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(staging, product), File.GetUnixFileMode(exe));
        var bundles = new List<string>();
        foreach (var bundle in Directory.EnumerateDirectories(binPath, "*.bundle"))
        {
            var target = Path.Combine(staging, Path.GetFileName(bundle));
            Packaging.Packagers.FileOps.CopyDirectory(bundle, target, CancellationToken.None);
            bundles.Add(Path.GetFileName(bundle));
        }
        return (product, bundles);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,127}$")]
    private static partial Regex ProductNameRegex();

    [GeneratedRegex(@"^\d+(\.\d+){0,2}$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-]*(\.[A-Za-z0-9\-]+)+$")]
    private static partial Regex BundleIdRegex();
}

/// <summary>Swift compiler / SwiftPM diagnostics: "file:line:col: error: message" and "error: message".</summary>
public sealed partial class SwiftOutputParser : IBuildOutputParser
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IBuildOutputParser Clone()
    {
        var copy = new SwiftOutputParser();
        copy._seen.UnionWith(_seen);
        return copy;
    }

    public BuildMessage? Parse(string line)
    {
        BuildMessage? m = null;
        if (FileRegex().Match(line) is { Success: true } f)
            m = new BuildMessage(Severity(f.Groups["sev"].Value), f.Groups["msg"].Value.Trim(), null, f.Groups["file"].Value, int.Parse(f.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture));
        else if (PlainRegex().Match(line) is { Success: true } p)
            m = new BuildMessage(Severity(p.Groups["sev"].Value), p.Groups["msg"].Value.Trim());
        if (m is null || m.Text.Length == 0) return null;
        return _seen.Add($"{m.Severity}|{m.File}|{m.Line}|{m.Text}") ? m : null;
    }

    private static DiagnosticSeverity Severity(string s) => s == "warning" ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;

    [GeneratedRegex(@"^(?<file>/[^:]+\.(?:swift|h|m|c|cpp)):(?<line>\d+):(?:\d+:)?\s*(?<sev>error|warning):\s*(?<msg>.+)$")]
    private static partial Regex FileRegex();

    [GeneratedRegex(@"^\s*(?<sev>error|warning):\s+(?<msg>.+)$")]
    private static partial Regex PlainRegex();
}
