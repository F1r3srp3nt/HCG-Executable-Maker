using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Node;

namespace HCG.ExecutableMaker.BuildEngine.Electron;

/// <summary>Build settings for an inspected Electron project, following its own packaging script and configuration.</summary>
public static class ElectronBuildDefaults
{
    /// <summary>
    /// The package.json script HCG follows for a platform: one whose electron-builder call names that platform, else
    /// one without a platform flag (electron-builder's host default), else none (electron-builder with the config only).
    /// </summary>
    public static ElectronPackagingScript? ChooseScript(NodeProjectInfo project, string builderPlatform)
    {
        var usable = project.PackagingScripts.Where(s => s.Problem is null).ToList();
        return usable.FirstOrDefault(s => s.Platforms.Contains(builderPlatform) && s.Platforms.Count == 1)
               ?? usable.FirstOrDefault(s => s.Platforms.Contains(builderPlatform))
               ?? usable.FirstOrDefault(s => s.Platforms.Count == 0);
    }

    public static IReadOnlyList<string> Targets(IEnumerable<PackagingFormat> formats)
    {
        var targets = formats.Select(NodeCapabilities.BuilderTarget).OfType<string>().Distinct().ToList();
        // zip/dmg are built from the app, which electron-builder leaves in its output: "dir" is then redundant.
        if (targets.Count > 1) targets.Remove("dir");
        return targets.Count == 0 ? ["dir"] : targets;
    }

    public static ElectronBuildOptions Options(NodeProjectInfo project, NodeCapabilities.Combination combo, IEnumerable<PackagingFormat> formats)
    {
        var script = ChooseScript(project, combo.BuilderPlatform);
        return new ElectronBuildOptions
        {
            ProjectDirectory = project.Directory,
            PackagingScript = script?.Name,
            CompileSteps = script?.CompileSteps ?? [],
            ConfigPath = script?.ConfigPath,
            Platform = combo.BuilderPlatform,
            Targets = Targets(formats),
            ProductName = project.BuilderConfig?.ProductName ?? project.ProductName ?? project.Name,
            OutputDirectory = project.BuilderConfig?.OutputDirectory ?? "dist",
            MainEntry = project.MainEntry,
            UseLockfile = project.LockFile is "package-lock.json" or "npm-shrinkwrap.json",
        };
    }

    public static AppIdentity Identity(NodeProjectInfo project) => new()
    {
        ApplicationName = project.BuilderConfig?.ProductName ?? project.ProductName ?? project.Name,
        Version = project.Version ?? "0.0.0",
        ApplicationId = project.BuilderConfig?.AppId ?? "com.electron." + new string(project.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray()).Trim('.'),
        Description = project.Description ?? "",
    };

    public static BuildSettings Settings(NodeProjectInfo project, NodeCapabilities.Combination combo, IReadOnlyList<PackagingFormat> formats, string outputDirectory) => new()
    {
        ProjectRelativePath = project.ManifestPath,
        Platform = combo.Platform,
        Architecture = combo.Architecture,
        OutputDirectory = outputDirectory,
        Identity = Identity(project),
        PackagingFormats = formats,
        Electron = Options(project, combo, formats),
        SelfContained = true,
        Timeout = TimeSpan.FromMinutes(45),
    };
}
