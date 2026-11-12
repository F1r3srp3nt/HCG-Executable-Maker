using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Native;

/// <summary>
/// Theos package builds: <c>make package FINALPACKAGE=1</c> with the project's own makefiles and the configuration the
/// project defines (e.g. HCG_ENV=rootless), in a private copy. Each configuration gets its own folder, so rootless and
/// rootful object files and packages never mix. Never <c>make install</c>, <c>do</c> or anything that touches a device.
/// </summary>
public static partial class TheosBuildPlanner
{
    public sealed record Tools(string Theos, string Make)
    {
        public static Tools? From(ToolchainReport report) =>
            report.Find(ToolchainIds.Theos) is { IsAvailable: true, ExecutablePath: { } theos } && report.Find(ToolchainIds.TheosMake) is { IsAvailable: true, ExecutablePath: { } make }
                ? new Tools(theos, make) : null;
    }

    public sealed record Prepared(string WorkRoot, string ProjectDirectory, string PackageDirectory, bool IsTemporary);

    public static void Validate(BuildSettings settings, HostEnvironment host, string sourceDirectory)
    {
        var o = settings.Native ?? throw new BuildValidationException("Theos build options are missing.");
        if (o.Kind != NativeProjectKind.Theos) throw new BuildValidationException("Not a Theos build.");
        var scheme = o.TheosScheme ?? throw new BuildValidationException("Choose the package configuration (rootless / rootful) the project defines.");
        if (host.Platform is not (TargetPlatform.MacOS or TargetPlatform.Linux)) throw new BuildValidationException("Theos packages are built on macOS or Linux.");
        if (settings.Platform != TargetPlatform.IOS) throw new BuildValidationException("A Theos package targets jailbroken iOS devices; the target platform must be iOS.");
        if (scheme.SelectorVariable is { } v && (!VariableRegex().IsMatch(v) || scheme.SelectorValue is null || !ValueRegex().IsMatch(scheme.SelectorValue)))
            throw new BuildValidationException($"'{v}={scheme.SelectorValue}' is not a valid make variable assignment.");
        if (scheme.PackageScheme is not ("rootless" or "rootful" or "roothide")) throw new BuildValidationException($"Unknown package scheme '{scheme.PackageScheme}'.");
        if (o.ProjectDirectory.Split('/').Any(seg => seg is ".." or ".") || Path.IsPathRooted(o.ProjectDirectory))
            throw new BuildValidationException("The project directory must be inside the workspace.");
        var dir = NativeBuildPlanner.ProjectDirectory(sourceDirectory, o.ProjectDirectory);
        if (!new[] { "Makefile", "makefile", "GNUmakefile" }.Any(m => File.Exists(Path.Combine(dir, m)))) throw new BuildValidationException("The Theos Makefile is missing from the project directory.");
    }

    /// <summary>
    /// Copies the imported source into a folder for this build and configuration. The whole repository is copied when
    /// the Makefiles reach outside the project folder (ROOT := ..), so the repository structure is preserved. Theos'
    /// makefiles cannot handle spaces in paths: when HCG's build folder contains one, a private temporary folder is used.
    /// </summary>
    public static Prepared Prepare(BuildSettings settings, string sourceDirectory, string buildWork, bool copyWholeSource, CancellationToken ct)
    {
        var o = settings.Native!;
        var key = Regex.Replace(o.TheosScheme!.Label.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var root = Path.Combine(buildWork, "theos-" + key);
        var temporary = false;
        if (root.Any(char.IsWhiteSpace))
        {
            root = Path.Combine(Path.GetTempPath(), "hcg-theos-" + Guid.NewGuid().ToString("N")[..12], key);
            temporary = true;
            if (root.Any(char.IsWhiteSpace)) throw new BuildValidationException($"Theos cannot build in a path containing spaces, and the temporary folder ({Path.GetTempPath()}) has one.");
        }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var src = Path.Combine(root, "src");
        var projectRel = o.ProjectDirectory.Replace('/', Path.DirectorySeparatorChar);
        if (copyWholeSource) CopyTree(sourceDirectory, src, ct);
        else CopyTree(Path.Combine(sourceDirectory, projectRel), Path.Combine(src, projectRel), ct);
        var packages = Path.Combine(root, "packages");
        Directory.CreateDirectory(packages);
        return new Prepared(root, Path.Combine(src, projectRel), packages, temporary);
    }

    public static BuildStep Plan(BuildSettings settings, Prepared prepared, Tools tools, IReadOnlyDictionary<string, string> environment)
    {
        var scheme = settings.Native!.TheosScheme!;
        var args = new List<string> { "-C", prepared.ProjectDirectory, "package", "FINALPACKAGE=1", "THEOS_PACKAGE_DIR=" + prepared.PackageDirectory, "messages=yes" };
        if (scheme.SelectorVariable is { } v) args.Add($"{v}={scheme.SelectorValue}");
        var env = new Dictionary<string, string>(environment) { ["THEOS"] = tools.Theos };
        return new BuildStep(BuildStage.Compiling, $"Theos: make package ({scheme.Label})", new ProcessSpec
        {
            Executable = tools.Make, Arguments = args, WorkingDirectory = prepared.ProjectDirectory, Environment = env,
        });
    }

    /// <summary>The single .deb this build wrote.</summary>
    public static string Locate(Prepared prepared)
    {
        var debs = Directory.Exists(prepared.PackageDirectory) ? Directory.GetFiles(prepared.PackageDirectory, "*.deb") : [];
        return debs.Length switch
        {
            1 => debs[0],
            0 => throw new InvalidDataException($"make package succeeded, but no .deb was written to {prepared.PackageDirectory}."),
            _ => throw new InvalidDataException($"make package wrote {debs.Length} packages ({string.Join(", ", debs.Select(Path.GetFileName))}); expected one."),
        };
    }

    private static void CopyTree(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(to, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                // Relative links inside the tree keep working; absolute links are not reproduced.
                if (!Path.IsPathRooted(link)) { if (entry is DirectoryInfo) Directory.CreateSymbolicLink(target, link); else File.CreateSymbolicLink(target, link); }
                continue;
            }
            if (entry is DirectoryInfo d)
            {
                if (d.Name is ".git" or ".theos" || (d.Name == "packages" && d.EnumerateFiles("*", SearchOption.AllDirectories).All(f => f.Extension == ".deb"))) continue;
                CopyTree(d.FullName, target, ct);
            }
            else
            {
                File.Copy(entry.FullName, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName));
            }
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")] private static partial Regex VariableRegex();
    [GeneratedRegex("^[A-Za-z0-9_.+-]{1,64}$")] private static partial Regex ValueRegex();
}
