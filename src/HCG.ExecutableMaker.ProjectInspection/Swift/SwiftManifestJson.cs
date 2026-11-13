using System.Text.Json;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Swift;

/// <summary>
/// Parses the JSON printed by <c>swift package dump-package</c> — SwiftPM's own structured description of the evaluated
/// manifest. This is the authoritative product/target list (conditional declarations already resolved for the host).
/// </summary>
public static class SwiftManifestJson
{
    public const int MaxJsonBytes = 8 * 1024 * 1024;

    public static SwiftPackageManifest Parse(string json)
    {
        if (json.Length > MaxJsonBytes) throw new InvalidDataException("Package description is unexpectedly large.");
        // dump-package may print warnings before the JSON on some toolchains; start at the first '{'.
        var start = json.IndexOf('{');
        if (start < 0) throw new InvalidDataException("swift package dump-package printed no JSON.");
        using var doc = JsonDocument.Parse(json.AsMemory(start), new JsonDocumentOptions { MaxDepth = 64 });
        var root = doc.RootElement;
        var name = Str(root, "name") ?? throw new InvalidDataException("The package description has no name.");

        string? tools = null;
        if (root.TryGetProperty("toolsVersion", out var tv))
            tools = tv.ValueKind == JsonValueKind.Object ? Str(tv, "_version") : tv.ValueKind == JsonValueKind.String ? tv.GetString() : null;

        string? macOS = null;
        if (root.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Array)
            foreach (var p in platforms.EnumerateArray())
                if (Str(p, "platformName") == "macos") macOS = Str(p, "version");

        var products = new List<SwiftProductInfo>();
        if (root.TryGetProperty("products", out var prods) && prods.ValueKind == JsonValueKind.Array)
            foreach (var p in prods.EnumerateArray())
                products.Add(new SwiftProductInfo(Str(p, "name") ?? "?", ProductKind(p), Strings(p, "targets")));

        var targets = new List<SwiftTargetInfo>();
        if (root.TryGetProperty("targets", out var tgts) && tgts.ValueKind == JsonValueKind.Array)
            foreach (var t in tgts.EnumerateArray())
            {
                var resources = t.TryGetProperty("resources", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
                targets.Add(new SwiftTargetInfo(Str(t, "name") ?? "?", TargetKind(Str(t, "type")), Str(t, "path"), resources));
            }

        var dependencies = new List<string>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            foreach (var d in deps.EnumerateArray())
                dependencies.Add(DependencyLabel(d));

        return new SwiftPackageManifest
        {
            Name = name,
            ToolsVersion = tools,
            MinimumMacOS = macOS,
            Products = products,
            Targets = targets,
            Dependencies = dependencies,
        };
    }

    /// <summary>Product "type" is an object with one key: executable, library, plugin, macro, snippet, test.</summary>
    private static SwiftProductKind ProductKind(JsonElement product)
    {
        if (!product.TryGetProperty("type", out var type)) return SwiftProductKind.Other;
        var key = type.ValueKind switch
        {
            JsonValueKind.Object => type.EnumerateObject().Select(p => p.Name).FirstOrDefault(),
            JsonValueKind.String => type.GetString(),
            _ => null,
        };
        return key switch
        {
            "executable" => SwiftProductKind.Executable,
            "library" => SwiftProductKind.Library,
            "plugin" => SwiftProductKind.Plugin,
            "macro" => SwiftProductKind.Macro,
            _ => SwiftProductKind.Other,
        };
    }

    private static SwiftTargetKind TargetKind(string? type) => type switch
    {
        "executable" => SwiftTargetKind.Executable,
        "regular" => SwiftTargetKind.Regular,
        "test" => SwiftTargetKind.Test,
        "plugin" => SwiftTargetKind.Plugin,
        "macro" => SwiftTargetKind.Macro,
        "system" => SwiftTargetKind.System,
        "binary" => SwiftTargetKind.Binary,
        _ => SwiftTargetKind.Other,
    };

    private static string DependencyLabel(JsonElement d)
    {
        foreach (var kind in d.EnumerateObject())
        {
            var first = kind.Value.ValueKind == JsonValueKind.Array ? kind.Value.EnumerateArray().FirstOrDefault() : default;
            var label = first.ValueKind == JsonValueKind.Object
                ? Str(first, "identity") ?? Str(first, "nameForTargetDependencyResolutionOnly") ?? Location(first)
                : null;
            return $"{kind.Name}: {label ?? "?"}";
        }
        return "?";
    }

    private static string? Location(JsonElement e) =>
        e.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
            ? loc.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().FirstOrDefault().ToString() : p.Value.ToString()).FirstOrDefault()
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
