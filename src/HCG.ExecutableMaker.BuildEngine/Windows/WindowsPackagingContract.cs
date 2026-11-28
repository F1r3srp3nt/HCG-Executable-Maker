using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.BuildEngine.Windows;

/// <summary>
/// A project's own Windows packaging contract (<c>windows/WindowsPackaging.json</c>, schema
/// <c>hcg-windows-packaging/1</c>), as HCG Server declares it. Executable Maker reads it as data and follows it; it does
/// not substitute its own installer or naming. The file says:
/// <list type="bullet">
/// <item>the product, identifier, platform, architecture and runtime identifier;</item>
/// <item>the tools it requires (<c>"go &gt;= 1.25"</c>), each step's command (an argument list, never a shell string) and
/// environment, with <c>${STAGE}</c> as the only placeholder in commands;</item>
/// <item>the outputs (<c>${VERSION}</c> and <c>*</c> in names), what is installed, and the code-signing state.</item>
/// </list>
/// A command runs only after the person confirmed this exact file (its SHA-256).
/// </summary>
public sealed partial record WindowsPackagingContract
{
    public const string Schema = "hcg-windows-packaging/1";
    public static readonly string[] Locations = ["windows/WindowsPackaging.json", "WindowsPackaging.json"];

    public sealed record Step(string Description, IReadOnlyList<SwiftToolRequirement> Requires, IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Environment);
    public sealed record Output(string Name, string Kind);

    /// <summary>Path relative to the source root ('/' separated).</summary>
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required string Product { get; init; }
    public string? Identifier { get; init; }
    public required string Architecture { get; init; }
    public string? RuntimeIdentifier { get; init; }
    public IReadOnlyList<SwiftToolRequirement> Requires { get; init; } = [];
    /// <summary>Requirement text → why it is optional (as the contract states it; Executable Maker does not act on the prose).</summary>
    public IReadOnlyDictionary<string, string> OptionalRequires { get; init; } = new Dictionary<string, string>();
    public required IReadOnlyList<Step> Steps { get; init; }
    public required IReadOnlyList<Output> Outputs { get; init; }
    public IReadOnlyList<string> InstalledFiles { get; init; } = [];
    public string? InstallLocation { get; init; }
    public string? CodeSigning { get; init; }

    public sealed class ContractException(string message) : Exception(message);

    /// <summary>The contract in <paramref name="sourceRoot"/>, or (null, null) when there is none, or (null, reason) when it is invalid.</summary>
    public static (WindowsPackagingContract? Contract, string? Problem) Find(string sourceRoot)
    {
        foreach (var rel in Locations)
        {
            var file = System.IO.Path.Combine(sourceRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(file)) continue;
            try { return (Parse(File.ReadAllBytes(file), rel), null); }
            catch (Exception ex) when (ex is ContractException or JsonException or SwiftPackagingContractReader.ContractException) { return (null, $"{rel}: {ex.Message}"); }
        }
        return (null, null);
    }

    public static WindowsPackagingContract Parse(byte[] bytes, string path)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ContractException("the file is not a JSON object");
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string Req(JsonElement e, string name, string where) => Str(e, name) is { Length: > 0 } s ? s : throw new ContractException($"{where}{name} is missing or not text");
        IReadOnlyList<string> Strings(JsonElement e, string name, string where)
        {
            if (!e.TryGetProperty(name, out var a)) return [];
            if (a.ValueKind != JsonValueKind.Array) throw new ContractException($"{where}{name} must be an array of text");
            return a.EnumerateArray().Select((x, i) => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new ContractException($"{where}{name}[{i}] must be text")).ToList();
        }

        var schema = Str(root, "schema");
        if (schema != Schema) throw new ContractException($"schema is {(schema is null ? "missing" : $"\"{schema}\"")}; this Executable Maker reads \"{Schema}\"");
        if (Str(root, "platform") is { } platform && platform != "windows") throw new ContractException($"platform is \"{platform}\", not \"windows\"");
        var arch = Req(root, "architecture", "");
        if (arch is not ("x64" or "arm64" or "x86")) throw new ContractException($"architecture \"{arch}\" is not x64, arm64 or x86");

        var requires = Strings(root, "requires", "").Select((r, i) => SwiftPackagingContractReader.ParseRequirement(r, $"requires[{i}]")).ToList();
        var optional = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("optionalRequires", out var opt))
        {
            if (opt.ValueKind != JsonValueKind.Object) throw new ContractException("optionalRequires must be an object");
            foreach (var p in opt.EnumerateObject()) optional[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : "";
        }

        if (!root.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array || stepsEl.GetArrayLength() == 0)
            throw new ContractException("steps must be a non-empty array");
        var steps = new List<Step>();
        var i = 0;
        foreach (var s in stepsEl.EnumerateArray())
        {
            var where = $"steps[{i++}].";
            if (s.ValueKind != JsonValueKind.Object) throw new ContractException($"{where[..^1]} must be an object");
            var command = Strings(s, "command", where);
            if (command.Count == 0) throw new ContractException($"{where}command must be a non-empty argument list (it is never run through a shell)");
            CheckProgram(command[0], where + "command[0]");
            foreach (var a in command)
                if (Placeholder().Matches(a).Select(m => m.Groups[1].Value).FirstOrDefault(n => n != "STAGE") is { } unknown)
                    throw new ContractException($"{where}command uses ${{{unknown}}}; only ${{STAGE}} is defined");
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (s.TryGetProperty("environment", out var envEl))
            {
                if (envEl.ValueKind != JsonValueKind.Object) throw new ContractException($"{where}environment must be an object");
                foreach (var p in envEl.EnumerateObject())
                {
                    if (!EnvName().IsMatch(p.Name)) throw new ContractException($"{where}environment name \"{p.Name}\" is not valid");
                    if (Blocked.Contains(p.Name)) throw new ContractException($"{where}environment may not set {p.Name}");
                    env[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new ContractException($"{where}environment.{p.Name} must be text");
                }
            }
            steps.Add(new Step(Req(s, "description", where),
                Strings(s, "requires", where).Select((r, j) => SwiftPackagingContractReader.ParseRequirement(r, $"{where}requires[{j}]")).ToList(), command, env));
        }

        if (!root.TryGetProperty("outputs", out var outEl) || outEl.ValueKind != JsonValueKind.Array || outEl.GetArrayLength() == 0)
            throw new ContractException("outputs must be a non-empty array");
        var outputs = new List<Output>();
        i = 0;
        foreach (var o in outEl.EnumerateArray())
        {
            var where = $"outputs[{i++}].";
            var name = Req(o, "name", where);
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)) throw new ContractException($"{where}name \"{name}\" must be a file name, not a path");
            outputs.Add(new Output(name, Req(o, "kind", where)));
        }

        IReadOnlyList<string> installed = [];
        string? location = null;
        if (root.TryGetProperty("installed", out var inst) && inst.ValueKind == JsonValueKind.Object)
        {
            installed = Strings(inst, "files", "installed.");
            location = Str(inst, "location");
        }
        return new WindowsPackagingContract
        {
            Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Product = Req(root, "product", ""), Identifier = Str(root, "identifier"), Architecture = arch, RuntimeIdentifier = Str(root, "runtimeIdentifier"),
            Requires = requires, OptionalRequires = optional, Steps = steps, Outputs = outputs, InstalledFiles = installed, InstallLocation = location,
            CodeSigning = Str(root, "codeSigning"),
        };
    }

    /// <summary>
    /// A step's program is a tool name the requirements resolve (go, dotnet, …) or a script inside the repository
    /// (relative, no "..", no drive or leading slash). Never a shell, an absolute path or a URL.
    /// </summary>
    private static void CheckProgram(string program, string where)
    {
        if (program.Length == 0) throw new ContractException($"{where} is empty");
        if (System.IO.Path.IsPathRooted(program) || program.Contains(':') || program.Contains('\\'))
            throw new ContractException($"{where} \"{program}\" must be a tool name or a script path inside the repository");
        if (program.Split('/').Any(p => p is ".." or "")) throw new ContractException($"{where} \"{program}\" may not leave the repository");
        if (Shells.Contains(System.IO.Path.GetFileName(program))) throw new ContractException($"{where} \"{program}\" is a shell; steps are argument lists, not shell commands");
    }

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "sh", "bash", "zsh", "cmd", "cmd.exe", "powershell", "pwsh", "powershell.exe", "pwsh.exe" };
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase) { "PATH", "HOME", "USERPROFILE", "LD_PRELOAD", "DYLD_INSERT_LIBRARIES", "DOTNET_ROOT", "GOROOT" };

    /// <summary>Every tool the contract declares, top-level and per step, exactly as declared.</summary>
    public IReadOnlyList<ToolRequirement> ToolRequirements()
    {
        var list = new List<ToolRequirement>();
        foreach (var r in Requires)
        {
            var def = Readiness.ToolCatalog.For(r.Tool);
            var text = r.MinimumVersion is null ? r.Tool : $"{r.Tool} >= {r.MinimumVersion}";
            var optional = OptionalRequires.FirstOrDefault(o => o.Key.StartsWith(r.Tool, StringComparison.OrdinalIgnoreCase));
            list.Add(new ToolRequirement
            {
                ToolId = def.Id, DisplayName = def.DisplayName, MinimumVersion = r.MinimumVersion, DeclaredBy = $"{Path} requires “{text}”",
                Purpose = optional.Key is null ? $"packaging {Product} for Windows" : $"packaging {Product} for Windows ({optional.Value})",
            });
        }
        foreach (var s in Steps)
            foreach (var r in s.Requires)
            {
                var def = Readiness.ToolCatalog.For(r.Tool);
                list.Add(new ToolRequirement
                {
                    ToolId = def.Id, DisplayName = def.DisplayName, MinimumVersion = r.MinimumVersion,
                    DeclaredBy = $"{Path} › step “{s.Description}”", Purpose = s.Description,
                });
            }
        return Readiness.BuildRequirementsInspector.Merge(list);
    }

    /// <summary>Whether a file name matches an output name (<c>${VERSION}</c> and <c>*</c> match any text).</summary>
    public static bool Matches(string outputName, string fileName)
    {
        var pattern = "^" + string.Join(".*", Regex.Escape(outputName.Replace("${VERSION}", "\u0001", StringComparison.Ordinal)).Split(["\u0001", @"\*"], StringSplitOptions.None)) + "$";
        return Regex.IsMatch(fileName, pattern, RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvName();
}
