using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine;

public sealed class BuildValidationException(string message) : Exception(message);

/// <summary>
/// Turns <see cref="BuildSettings"/> into structured <c>dotnet</c> invocations. Every value is a separate argument
/// (no shell, no string concatenation of commands); MSBuild property values are escaped.
/// </summary>
public static partial class DotNetBuildPlanner
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public sealed record PlanInput(
        BuildSettings Settings,
        string SourceDirectory,
        string DotNetExecutable,
        string PublishDirectory,
        TargetPlatform HostPlatform,
        string? WindowsIconPath = null,
        IReadOnlyDictionary<string, string>? Environment = null);

    public static IReadOnlyList<BuildStep> Plan(PlanInput input)
    {
        var s = input.Settings;
        Validate(s);
        if (!Path.IsPathFullyQualified(input.DotNetExecutable))
            throw new BuildValidationException("The .NET SDK path must be absolute.");
        var project = ResolveProject(input.SourceDirectory, s.ProjectRelativePath);
        if (!Path.IsPathFullyQualified(input.PublishDirectory))
            throw new BuildValidationException("The publish directory must be an absolute path.");
        var rid = s.RuntimeIdentifier ?? throw new BuildValidationException($"{s.Platform.DisplayName()} is not a supported .NET publishing target in M0.1.");
        var configuration = s.Configuration.ToString();
        var workingDirectory = Path.GetDirectoryName(project)!;

        var properties = new List<string>();
        if (s.StampVersion) properties.Add(Property("Version", s.Identity.Version));
        if (s.Platform == TargetPlatform.Windows && input.HostPlatform != TargetPlatform.Windows)
            properties.Add(Property("EnableWindowsTargeting", "true"));
        if (s.Platform == TargetPlatform.Windows && input.WindowsIconPath is { } ico)
        {
            if (!Path.IsPathFullyQualified(ico)) throw new BuildValidationException("Icon path must be absolute.");
            properties.Add(Property("ApplicationIcon", ico));
        }
        if (s.SingleFile)
        {
            properties.Add(Property("PublishSingleFile", "true"));
            properties.Add(Property("IncludeNativeLibrariesForSelfExtract", "true"));
        }
        var selfContained = s.SelfContained ? "--self-contained" : "--no-self-contained";

        ProcessSpec Spec(params IEnumerable<string>[] parts) => new()
        {
            Executable = input.DotNetExecutable,
            Arguments = parts.SelectMany(p => p).ToList(),
            WorkingDirectory = workingDirectory,
            Environment = input.Environment,
        };

        string[] common = ["-nologo", "--disable-build-servers", "-tl:off"];
        return
        [
            new BuildStep(BuildStage.Restoring, "Restore dependencies",
                Spec(["restore", project, "-r", rid], common, s.SelfContained ? [Property("SelfContained", "true")] : [], properties)),
            new BuildStep(BuildStage.Compiling, $"Build ({configuration}, {rid})",
                Spec(["build", project, "-c", configuration, "-r", rid, selfContained, "--no-restore"], common, properties)),
            new BuildStep(BuildStage.Publishing, "Publish self-contained output",
                Spec(["publish", project, "-c", configuration, "-r", rid, selfContained, "--no-restore", "-o", input.PublishDirectory], common, properties)),
        ];
    }

    public static void Validate(BuildSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Identity.ApplicationName))
            throw new BuildValidationException("Application name is required.");
        if (s.Identity.ApplicationName.Length > 100)
            throw new BuildValidationException("Application name must be 100 characters or fewer.");
        if (!VersionRegex().IsMatch(s.Identity.Version))
            throw new BuildValidationException($"Version '{s.Identity.Version}' is invalid. Use a numeric version such as 1.0.0 (optionally with a suffix like 1.0.0-beta.1).");
        if (!ApplicationIdRegex().IsMatch(s.Identity.ApplicationId))
            throw new BuildValidationException($"Application identifier '{s.Identity.ApplicationId}' is invalid. Use reverse-DNS form, e.g. com.company.app.");
        if (string.IsNullOrWhiteSpace(s.OutputDirectory) || !Path.IsPathFullyQualified(s.OutputDirectory))
            throw new BuildValidationException("Choose an absolute output directory.");
        if (s.Timeout <= TimeSpan.Zero || s.Timeout > TimeSpan.FromHours(12))
            throw new BuildValidationException("Build timeout must be between 1 minute and 12 hours.");
        if (s.Platform == TargetPlatform.Android)
            throw new BuildValidationException("Android builds are not implemented in M0.1.");
        if (s.Identity.IconSourcePath is { } icon && (!Path.IsPathFullyQualified(icon) || !File.Exists(icon)))
            throw new BuildValidationException("The selected icon file does not exist.");
    }

    public static string ResolveProject(string sourceDirectory, string relative)
    {
        string full;
        try { full = PathGuard.CombineWithin(sourceDirectory, relative); }
        catch (ArgumentException ex) { throw new BuildValidationException($"Invalid project path: {ex.Message}"); }
        if (!ProjectExtensions.Any(x => full.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            throw new BuildValidationException("The build target must be a .csproj, .fsproj or .vbproj file.");
        if (!File.Exists(full))
            throw new BuildValidationException($"Project file not found: {relative}");
        return full;
    }

    /// <summary>Builds a single <c>-p:Name=Value</c> argument with MSBuild escaping applied to the value.</summary>
    public static string Property(string name, string value)
    {
        if (!PropertyNameRegex().IsMatch(name)) throw new ArgumentException($"Invalid MSBuild property name '{name}'.", nameof(name));
        return $"-p:{name}={EscapeMsBuild(value)}";
    }

    /// <summary>Escapes characters with MSBuild meaning (%, $, @, ', ;, ?, *, and ',' for -p lists) and quotes.</summary>
    public static string EscapeMsBuild(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '%' => "%25", '$' => "%24", '@' => "%40", '\'' => "%27", ';' => "%3B", '?' => "%3F", '*' => "%2A", ',' => "%2C", '"' => "%22",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z][0-9A-Za-z.-]*)?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9-]*(\.[A-Za-z0-9][A-Za-z0-9-]*)+$")]
    private static partial Regex ApplicationIdRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PropertyNameRegex();
}
