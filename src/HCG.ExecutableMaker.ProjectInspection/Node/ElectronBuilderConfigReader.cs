using System.Text.Json;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Node;

/// <summary>
/// Locates and reads an electron-builder configuration the way electron-builder does: an explicit <c>--config</c> file,
/// then electron-builder.{yml,yaml,json,json5,toml,js,cjs,mjs,ts} / electron-builder.config.*, then package.json "build".
/// Data files are read; JavaScript configs are recognized but never executed (their values are reported as unknown).
/// </summary>
public static class ElectronBuilderConfigReader
{
    public static readonly string[] ConfigFileNames =
    [
        "electron-builder.yml", "electron-builder.yaml", "electron-builder.json", "electron-builder.json5", "electron-builder.toml",
        "electron-builder.config.yml", "electron-builder.config.yaml", "electron-builder.config.json", "electron-builder.config.json5", "electron-builder.config.toml",
        "electron-builder.config.js", "electron-builder.config.cjs", "electron-builder.config.mjs", "electron-builder.config.ts",
        "electron-builder.js", "electron-builder.cjs", "electron-builder.mjs", "electron-builder.ts",
    ];

    public sealed record ReadResult(ElectronBuilderConfig? Config, string? Problem);

    /// <param name="projectDir">Absolute project directory.</param>
    /// <param name="explicitConfig">Value of <c>--config</c> in the packaging script, relative to the project.</param>
    /// <param name="packageBuildSection">package.json "build" (null when absent).</param>
    public static ReadResult Read(string projectDir, string? explicitConfig, JsonElement? packageBuildSection)
    {
        if (explicitConfig is { Length: > 0 })
        {
            var full = Path.GetFullPath(Path.Combine(projectDir, explicitConfig));
            if (!full.StartsWith(Path.GetFullPath(projectDir), StringComparison.Ordinal) || !File.Exists(full))
                return new ReadResult(null, $"The packaging script passes --config {explicitConfig}, but that file does not exist in the project.");
            return ReadFile(full, explicitConfig);
        }
        foreach (var name in ConfigFileNames)
        {
            var full = Path.Combine(projectDir, name);
            if (File.Exists(full)) return ReadFile(full, name);
        }
        if (packageBuildSection is { ValueKind: JsonValueKind.Object } build)
            return new ReadResult(FromTree("package.json \"build\"", FromJson(build)), null);
        return new ReadResult(null, null);
    }

    private static ReadResult ReadFile(string full, string display)
    {
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is ".js" or ".cjs" or ".mjs" or ".ts")
            return new ReadResult(new ElectronBuilderConfig { Source = display, IsScript = true }, null);
        try
        {
            if (new FileInfo(full).Length > 1_000_000) return new ReadResult(null, $"{display} is larger than 1 MB; HCG did not read it.");
            var text = File.ReadAllText(full);
            object? tree = ext switch
            {
                ".yml" or ".yaml" => MiniYaml.Parse(text),
                ".json" => FromJson(JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement),
                ".json5" => FromJson(JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement),
                _ => null,
            };
            if (ext == ".toml")
                return new ReadResult(new ElectronBuilderConfig { Source = display, IsScript = true }, null);
            return tree is Dictionary<string, object?> map
                ? new ReadResult(FromTree(display, map), null)
                : new ReadResult(null, $"{display} does not contain a configuration object.");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IOException)
        {
            return new ReadResult(null, $"{display} could not be read: {ex.Message}");
        }
    }

    public static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.GetRawText(),
    };

    public static ElectronBuilderConfig FromTree(string source, object? tree)
    {
        var map = tree as Dictionary<string, object?> ?? [];
        var directories = map.GetValueOrDefault("directories") as Dictionary<string, object?>;
        var mac = map.GetValueOrDefault("mac") as Dictionary<string, object?>;
        var linux = map.GetValueOrDefault("linux") as Dictionary<string, object?>;
        var win = map.GetValueOrDefault("win") as Dictionary<string, object?>;
        var extra = map.GetValueOrDefault("extraMetadata") as Dictionary<string, object?>;
        var (macTargets, macArchs) = Targets(mac?.GetValueOrDefault("target"));
        var (linuxTargets, _) = Targets(linux?.GetValueOrDefault("target"));
        var (winTargets, _) = Targets(win?.GetValueOrDefault("target"));
        return new ElectronBuilderConfig
        {
            Source = source,
            AppId = map.GetValueOrDefault("appId") as string,
            ProductName = map.GetValueOrDefault("productName") as string,
            OutputDirectory = directories?.GetValueOrDefault("output") as string is { Length: > 0 } o ? o.TrimEnd('/') : "dist",
            MacTargets = macTargets,
            MacArchs = macArchs,
            LinuxTargets = linuxTargets,
            WinTargets = winTargets,
            HasMac = map.ContainsKey("mac"),
            HasLinux = map.ContainsKey("linux"),
            HasWindows = map.ContainsKey("win"),
            MacIdentity = mac is not null && mac.TryGetValue("identity", out var id) ? id as string ?? "null" : null,
            LinuxExecutableName = linux?.GetValueOrDefault("executableName") as string ?? map.GetValueOrDefault("executableName") as string,
            ExtraMetadataMain = extra?.GetValueOrDefault("main") as string,
        };
    }

    /// <summary>target: "dmg" | ["zip","dmg"] | [{target: zip, arch: [arm64]}] | {target: dmg, arch: x64}.</summary>
    private static (IReadOnlyList<string> Targets, IReadOnlyList<string> Archs) Targets(object? node)
    {
        var targets = new List<string>();
        var archs = new List<string>();
        void Add(object? item)
        {
            switch (item)
            {
                case string s: targets.Add(s); break;
                case Dictionary<string, object?> d:
                    if (d.GetValueOrDefault("target") is string t) targets.Add(t);
                    switch (d.GetValueOrDefault("arch"))
                    {
                        case string a: archs.Add(a); break;
                        case List<object?> l: archs.AddRange(l.OfType<string>()); break;
                    }
                    break;
            }
        }
        if (node is List<object?> list) foreach (var i in list) Add(i);
        else Add(node);
        return (targets.Distinct(StringComparer.Ordinal).ToList(), archs.Distinct(StringComparer.Ordinal).ToList());
    }
}
