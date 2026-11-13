using System.Text.Json;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Native;

/// <summary>
/// The CMake File API (cmake-file-api(7)): HCG writes a stateless query into the build directory before configuring,
/// and reads CMake's reply afterwards — the targets, their types and actual artifact paths for each configuration,
/// the compilers CMake selected, and a few cache values. Nothing is guessed from CMakeLists.txt.
/// </summary>
public static class CMakeFileApi
{
    public const string ClientName = "client-hcg";

    public sealed record Reply(string Generator, bool IsMultiConfig, string? CMakeVersion, IReadOnlyList<string> Configurations,
        IReadOnlyList<NativeCompilerInfo> Compilers, IReadOnlyDictionary<string, string> Cache, IReadOnlyDictionary<string, IReadOnlyList<NativeTargetInfo>> TargetsByConfiguration);

    /// <summary>Requests codemodel v2, toolchains v1 and cache v2 for the next configure of <paramref name="buildDirectory"/>.</summary>
    public static void WriteQuery(string buildDirectory)
    {
        var dir = Path.Combine(buildDirectory, ".cmake", "api", "v1", "query", ClientName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "query.json"),
            """{"requests":[{"kind":"codemodel","version":2},{"kind":"toolchains","version":1},{"kind":"cache","version":2}]}""");
    }

    /// <summary>Reads the newest reply. Throws <see cref="InvalidDataException"/> with the reason when there is none or it is incomplete.</summary>
    public static Reply ReadReply(string buildDirectory, string sourceDirectory)
    {
        var replyDir = Path.Combine(buildDirectory, ".cmake", "api", "v1", "reply");
        var index = Directory.Exists(replyDir) ? Directory.GetFiles(replyDir, "index-*.json").Order(StringComparer.Ordinal).LastOrDefault() : null;
        if (index is null) throw new InvalidDataException("CMake wrote no File API reply: the configure step did not complete.");
        using var idx = JsonDocument.Parse(File.ReadAllText(index));
        var root = idx.RootElement;
        var cmake = root.GetProperty("cmake");
        var generator = cmake.GetProperty("generator");
        var generatorName = generator.GetProperty("name").GetString() ?? "?";
        var multi = generator.TryGetProperty("multiConfig", out var mc) && mc.ValueKind == JsonValueKind.True;
        var version = cmake.TryGetProperty("version", out var v) && v.TryGetProperty("string", out var vs) ? vs.GetString() : null;
        if (!root.GetProperty("reply").TryGetProperty(ClientName, out var client) || !client.TryGetProperty("query.json", out var query))
            throw new InvalidDataException("The File API reply does not answer HCG's query.");
        string? ReplyFile(string kind) => query.GetProperty("responses").EnumerateArray()
            .Where(r => r.TryGetProperty("kind", out var k) && k.GetString() == kind && r.TryGetProperty("jsonFile", out _))
            .Select(r => r.GetProperty("jsonFile").GetString()).FirstOrDefault();

        var codemodelFile = ReplyFile("codemodel") ?? throw new InvalidDataException("The File API reply has no codemodel.");
        using var codemodel = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(replyDir, codemodelFile)));
        var configurations = new List<string>();
        var targets = new Dictionary<string, IReadOnlyList<NativeTargetInfo>>(StringComparer.Ordinal);
        var src = Path.GetFullPath(sourceDirectory);
        foreach (var config in codemodel.RootElement.GetProperty("configurations").EnumerateArray())
        {
            var name = config.GetProperty("name").GetString() ?? "";
            configurations.Add(name);
            var ids = config.GetProperty("targets").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!, t => t.GetProperty("name").GetString()!);
            var list = new List<NativeTargetInfo>();
            foreach (var t in config.GetProperty("targets").EnumerateArray())
            {
                using var target = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(replyDir, t.GetProperty("jsonFile").GetString()!)));
                var tr = target.RootElement;
                var artifacts = tr.TryGetProperty("artifacts", out var a) ? a.EnumerateArray().Select(x => x.GetProperty("path").GetString()!).ToList() : [];
                var languages = tr.TryGetProperty("compileGroups", out var g)
                    ? g.EnumerateArray().Select(x => x.GetProperty("language").GetString()!).Distinct().ToList() : [];
                var deps = tr.TryGetProperty("dependencies", out var d)
                    ? d.EnumerateArray().Select(x => ids.GetValueOrDefault(x.GetProperty("id").GetString()!)).OfType<string>().ToList() : [];
                var sourceDir = tr.TryGetProperty("paths", out var p) && p.TryGetProperty("source", out var ps) ? ps.GetString() ?? "." : ".";
                if (Path.IsPathRooted(sourceDir)) sourceDir = Path.GetRelativePath(src, sourceDir);
                list.Add(new NativeTargetInfo
                {
                    Name = tr.GetProperty("name").GetString()!,
                    Type = ParseType(tr.GetProperty("type").GetString()),
                    Artifacts = artifacts,
                    Languages = languages,
                    Dependencies = deps,
                    SourceDirectory = sourceDir is "." ? "" : sourceDir.Replace('\\', '/'),
                    IsMacBundle = artifacts.Any(x => x.Replace('\\', '/').Contains(".app/Contents/MacOS/", StringComparison.Ordinal)),
                });
            }
            targets[name] = list.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        }

        var compilers = new List<NativeCompilerInfo>();
        if (ReplyFile("toolchains") is { } toolchainsFile)
        {
            using var tc = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(replyDir, toolchainsFile)));
            foreach (var t in tc.RootElement.GetProperty("toolchains").EnumerateArray())
            {
                var c = t.TryGetProperty("compiler", out var cc) ? cc : default;
                string? S(string n) => c.ValueKind == JsonValueKind.Object && c.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                compilers.Add(new NativeCompilerInfo(t.GetProperty("language").GetString() ?? "?", S("id"), S("version"), S("path")));
            }
        }
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReplyFile("cache") is { } cacheFile)
        {
            using var ca = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(replyDir, cacheFile)));
            foreach (var e in ca.RootElement.GetProperty("entries").EnumerateArray())
            {
                var n = e.GetProperty("name").GetString() ?? "";
                if (n is "CMAKE_BUILD_TYPE" or "CMAKE_CONFIGURATION_TYPES" or "CMAKE_C_STANDARD" or "CMAKE_CXX_STANDARD" or "CMAKE_TOOLCHAIN_FILE" or "CMAKE_OSX_ARCHITECTURES"
                    or "CMAKE_OSX_DEPLOYMENT_TARGET" or "CMAKE_C_FLAGS" or "CMAKE_CXX_FLAGS" or "FETCHCONTENT_FULLY_DISCONNECTED" or "CMAKE_GENERATOR_PLATFORM")
                    if (e.GetProperty("value").GetString() is { Length: > 0 } value) cache[n] = value;
            }
        }
        return new Reply(generatorName, multi, version, configurations, compilers, cache, targets);
    }

    /// <summary>The artifact of a target as an absolute path (CMake reports paths relative to the build directory).</summary>
    public static string ArtifactPath(string buildDirectory, string artifact) =>
        Path.GetFullPath(Path.IsPathRooted(artifact) ? artifact : Path.Combine(buildDirectory, artifact.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>For an executable inside Name.app/Contents/MacOS/, the Name.app folder; null otherwise.</summary>
    public static string? BundleOf(string executablePath)
    {
        var p = executablePath.Replace('\\', '/');
        var i = p.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        return i < 0 ? null : executablePath[..(i + 4)];
    }

    public static NativeTargetType ParseType(string? type) => type switch
    {
        "EXECUTABLE" => NativeTargetType.Executable,
        "STATIC_LIBRARY" => NativeTargetType.StaticLibrary,
        "SHARED_LIBRARY" => NativeTargetType.SharedLibrary,
        "MODULE_LIBRARY" => NativeTargetType.ModuleLibrary,
        "OBJECT_LIBRARY" => NativeTargetType.ObjectLibrary,
        "INTERFACE_LIBRARY" => NativeTargetType.InterfaceLibrary,
        "UTILITY" => NativeTargetType.Utility,
        _ => NativeTargetType.Other,
    };
}
