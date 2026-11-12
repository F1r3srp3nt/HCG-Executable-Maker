using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Swift;

/// <summary>
/// Reads a Swift package's packaging contract (<c>AppPackaging.json</c>, schema <c>hcg-app-packaging/1</c>) as data.
/// Nothing in it is run here. Validation is strict so a contract HCG cannot honour is reported, never half-applied:
/// unknown keys and schemas are rejected, commands are argument vectors, every path stays inside the workspace, and
/// helpers and resources may only be placed in Contents/Helpers and Contents/Resources.
/// </summary>
public static partial class SwiftPackagingContractReader
{
    public const int MaxBytes = 64 * 1024;

    public sealed class ContractException(string message) : Exception(message);

    /// <summary>
    /// Reads <c>&lt;packageDir&gt;/AppPackaging.json</c>. Returns (null, null) when the package has none, and an error
    /// (never a partial contract) when the file is present but unusable.
    /// </summary>
    public static (SwiftPackagingContract? Contract, string? Error) Read(string sourceRoot, string packageDir)
    {
        var relative = (packageDir.Length == 0 ? "" : packageDir.TrimEnd('/') + "/") + SwiftPackagingContract.FileName;
        var path = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return (null, null);
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) return (null, $"{relative} is larger than {MaxBytes / 1024} KB.");
            return (Parse(File.ReadAllBytes(path), relative, sourceRoot), null);
        }
        catch (ContractException ex) { return (null, $"{relative}: {ex.Message}"); }
        catch (JsonException ex) { return (null, $"{relative} is not valid JSON: {ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, $"{relative} could not be read: {ex.Message}"); }
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <param name="relativePath">The contract's path relative to <paramref name="sourceRoot"/>, '/' separated.</param>
    public static SwiftPackagingContract Parse(byte[] bytes, string relativePath, string sourceRoot)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ContractException("the top level must be a JSON object.");
        Keys(root, "the top level", "schema", "product", "bundle", "prebuild", "helpers", "resources");
        var schema = Str(root, "schema", "the top level");
        if (schema != SwiftPackagingContract.Schema)
            throw new ContractException($"schema \"{schema}\" is not supported. This version of HCG reads \"{SwiftPackagingContract.Schema}\".");
        var product = Str(root, "product", "the top level");
        if (!ProductRegex().IsMatch(product)) throw new ContractException($"product \"{product}\" is not a valid Swift product name.");

        var contractDir = ContractDirectory(relativePath);
        string RelativePath(string value, string where)
        {
            Resolve(sourceRoot, contractDir, value, where); // throws when unsafe
            return value;
        }

        var bundle = new SwiftPackagingBundle();
        if (root.TryGetProperty("bundle", out var b))
        {
            Object(b, "bundle");
            Keys(b, "bundle", "displayName", "identifier", "infoPlist", "icon", "minimumMacOS");
            var min = OptStr(b, "minimumMacOS", "bundle");
            if (min is not null && !VersionRegex().IsMatch(min)) throw new ContractException($"bundle.minimumMacOS \"{min}\" is not a macOS version such as 13.0.");
            var id = OptStr(b, "identifier", "bundle");
            if (id is not null && !BundleIdRegex().IsMatch(id)) throw new ContractException($"bundle.identifier \"{id}\" is not a valid bundle identifier.");
            bundle = new SwiftPackagingBundle
            {
                DisplayName = OptStr(b, "displayName", "bundle"),
                Identifier = id,
                InfoPlist = OptStr(b, "infoPlist", "bundle") is { } plist ? RelativePath(plist, "bundle.infoPlist") : null,
                Icon = OptStr(b, "icon", "bundle") is { } icon ? RelativePath(icon, "bundle.icon") : null,
                MinimumMacOS = min,
            };
            if (bundle.Icon is { } ic && !ic.EndsWith(".icns", StringComparison.OrdinalIgnoreCase))
                throw new ContractException("bundle.icon must be an .icns file.");
        }

        var prebuild = new List<SwiftPrebuildStep>();
        foreach (var (step, where) in Items(root, "prebuild"))
        {
            Keys(step, where, "description", "requires", "workingDirectory", "command");
            var requires = new List<SwiftToolRequirement>();
            foreach (var (r, rw) in Items(step, "requires", where))
            {
                if (r.ValueKind != JsonValueKind.String) throw new ContractException($"{rw} must be a string such as \"go >= 1.25\".");
                requires.Add(ParseRequirement(r.GetString()!, rw));
            }
            var command = new List<string>();
            foreach (var (c, cw) in Items(step, "command", where))
            {
                if (c.ValueKind != JsonValueKind.String) throw new ContractException($"{cw} must be a string (commands are argument lists, not shell lines).");
                var arg = c.GetString()!;
                CheckVariables(arg, cw);
                command.Add(arg);
            }
            if (command.Count == 0) throw new ContractException($"{where}.command must list the program and its arguments.");
            var program = command[0];
            if (program.Contains(SwiftPackagingContract.HelpersDirVariable, StringComparison.Ordinal))
                throw new ContractException($"{where}.command[0] cannot be inside {SwiftPackagingContract.HelpersDirVariable}.");
            var workingDirectory = OptStr(step, "workingDirectory", where) ?? ".";
            var wd = RelativePath(workingDirectory, where + ".workingDirectory");
            if (program.Contains('/'))
                Resolve(sourceRoot, CombineRelative(contractDir, wd), program, where + ".command[0]");
            else if (!ToolRegex().IsMatch(program))
                throw new ContractException($"{where}.command[0] \"{program}\" is neither a path inside the project nor a tool name.");
            prebuild.Add(new SwiftPrebuildStep
            {
                Description = Str(step, "description", where),
                Requires = requires,
                WorkingDirectory = wd,
                Command = command,
            });
        }

        var helpers = new List<SwiftContractHelper>();
        foreach (var (h, where) in Items(root, "helpers"))
        {
            Keys(h, where, "name", "source", "destination", "required", "architectures", "sign", "verify");
            var name = Str(h, "name", where);
            if (!ToolRegex().IsMatch(name)) throw new ContractException($"{where}.name \"{name}\" must be a plain file name.");
            var source = Str(h, "source", where);
            CheckVariables(source, where + ".source");
            if (!source.StartsWith(SwiftPackagingContract.HelpersDirVariable + "/", StringComparison.Ordinal))
            {
                if (source.Contains('$')) throw new ContractException($"{where}.source may use {SwiftPackagingContract.HelpersDirVariable} only at its start.");
                RelativePath(source, where + ".source");
            }
            else if (source[(SwiftPackagingContract.HelpersDirVariable.Length + 1)..] is var inside && (inside.Length == 0 || inside.Split('/').Any(s => s is "" or "." or "..")))
                throw new ContractException($"{where}.source must name a file inside {SwiftPackagingContract.HelpersDirVariable}.");
            var destination = Str(h, "destination", where);
            if (!HelperDestinationRegex().IsMatch(destination))
                throw new ContractException($"{where}.destination \"{destination}\" must be Contents/Helpers/<file name>. Contents/MacOS holds only the app's own executable.");
            var archs = new List<string>();
            foreach (var (a, aw) in Items(h, "architectures", where))
            {
                var arch = a.ValueKind == JsonValueKind.String ? a.GetString()! : "";
                if (arch is not ("arm64" or "x86_64")) throw new ContractException($"{aw} must be \"arm64\" or \"x86_64\".");
                if (!archs.Contains(arch)) archs.Add(arch);
            }
            if (OptStr(h, "sign", where) is { } sign && sign != "before-app")
                throw new ContractException($"{where}.sign \"{sign}\" is not supported; helpers are signed before the app (\"before-app\").");
            var verify = new List<string>();
            foreach (var (v, vw) in Items(h, "verify", where))
            {
                var check = v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                if (check != "version") throw new ContractException($"{vw} \"{check}\" is not a known check (supported: \"version\").");
                verify.Add(check);
            }
            helpers.Add(new SwiftContractHelper
            {
                Name = name,
                Source = source,
                Destination = destination,
                Required = OptBool(h, "required", where) ?? true,
                Architectures = archs,
                Verify = verify,
            });
        }
        if (helpers.Select(h => h.Destination).Distinct(StringComparer.Ordinal).Count() != helpers.Count)
            throw new ContractException("two helpers have the same destination.");

        var resources = new List<SwiftContractResource>();
        foreach (var (r, where) in Items(root, "resources"))
        {
            Keys(r, where, "source", "destination", "required");
            var source = RelativePath(Str(r, "source", where), where + ".source");
            var destination = Str(r, "destination", where);
            if (!ResourceDestinationRegex().IsMatch(destination) || destination.Split('/').Any(s => s is "." or ".."))
                throw new ContractException($"{where}.destination \"{destination}\" must be a path under Contents/Resources/.");
            if (bundle.Icon is not null && destination == "Contents/Resources/AppIcon.icns")
                throw new ContractException($"{where}.destination is the app icon's place; set bundle.icon instead.");
            resources.Add(new SwiftContractResource { Source = source, Destination = destination, Required = OptBool(r, "required", where) ?? true });
        }
        if (resources.Select(r => r.Destination).Distinct(StringComparer.Ordinal).Count() != resources.Count)
            throw new ContractException("two resources have the same destination.");

        var produced = prebuild.SelectMany(p => p.Command).Where(a => a.Contains(SwiftPackagingContract.HelpersDirVariable, StringComparison.Ordinal)).ToList();
        foreach (var h in helpers.Where(h => h.Source.StartsWith(SwiftPackagingContract.HelpersDirVariable, StringComparison.Ordinal)))
            if (!produced.Any(a => a.Contains(h.Source, StringComparison.Ordinal)))
                throw new ContractException($"helper {h.Name} comes from {h.Source}, but no prebuild command writes it.");

        return new SwiftPackagingContract
        {
            Path = relativePath,
            Sha256 = Sha256(bytes),
            Product = product,
            Bundle = bundle,
            Prebuild = prebuild,
            Helpers = helpers,
            Resources = resources,
        };
    }

    /// <summary>"go >= 1.25" → (go, 1.25); "lipo" → (lipo, null).</summary>
    public static SwiftToolRequirement ParseRequirement(string text, string where = "requirement")
    {
        var m = RequirementRegex().Match(text.Trim());
        if (!m.Success) throw new ContractException($"{where} \"{text}\" must be a tool name, optionally followed by \">= version\" (e.g. \"go >= 1.25\").");
        return new SwiftToolRequirement(m.Groups["tool"].Value, m.Groups["version"].Success ? m.Groups["version"].Value : null);
    }

    /// <summary>The folder holding the contract, relative to the workspace ('/' separated, "" for the root).</summary>
    public static string ContractDirectory(string contractPath)
    {
        var i = contractPath.LastIndexOf('/');
        return i < 0 ? "" : contractPath[..i];
    }

    /// <summary>
    /// Resolves a contract path (relative to the contract's folder) to an absolute path inside the workspace. Throws
    /// when the value is absolute, uses backslashes or a home folder, or leaves the workspace.
    /// </summary>
    public static string Resolve(string sourceRoot, string contractDir, string value, string where)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ContractException($"{where} is empty.");
        if (value.StartsWith('/') || value.StartsWith('~') || value.Contains('\\') || value.Contains('\0') || Path.IsPathRooted(value))
            throw new ContractException($"{where} \"{value}\" must be a relative path inside the project.");
        if (value.Contains('$')) throw new ContractException($"{where} \"{value}\" cannot contain variables.");
        var root = Path.GetFullPath(sourceRoot);
        var full = Path.GetFullPath(Path.Combine(root, CombineRelative(contractDir, value).Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsWithin(root, full)) throw new ContractException($"{where} \"{value}\" points outside the project.");
        return full;
    }

    private static string CombineRelative(string dir, string value) =>
        dir.Length == 0 ? value : value == "." ? dir : dir + "/" + value;

    private static void CheckVariables(string value, string where)
    {
        var rest = value.Replace(SwiftPackagingContract.HelpersDirVariable, "", StringComparison.Ordinal);
        if (rest.Contains("${", StringComparison.Ordinal) || rest.Contains("$(", StringComparison.Ordinal) || rest.Contains('`'))
            throw new ContractException($"{where} \"{value}\" uses a variable or substitution other than {SwiftPackagingContract.HelpersDirVariable}.");
    }

    private static IEnumerable<(JsonElement Item, string Where)> Items(JsonElement parent, string key, string? parentWhere = null)
    {
        var where = parentWhere is null ? key : $"{parentWhere}.{key}";
        if (!parent.TryGetProperty(key, out var array)) yield break;
        if (array.ValueKind != JsonValueKind.Array) throw new ContractException($"{where} must be a list.");
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            var w = $"{where}[{i++}]";
            if (parentWhere is null && item.ValueKind != JsonValueKind.Object) throw new ContractException($"{w} must be an object.");
            yield return (item, w);
        }
    }

    private static void Object(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new ContractException($"{where} must be an object.");
    }

    private static void Keys(JsonElement e, string where, params string[] allowed)
    {
        foreach (var p in e.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal))
                throw new ContractException($"{where} has an unknown key \"{p.Name}\" (allowed: {string.Join(", ", allowed)}).");
    }

    private static string Str(JsonElement e, string key, string where) =>
        OptStr(e, key, where) ?? throw new ContractException($"{where} needs \"{key}\".");

    private static string? OptStr(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new ContractException($"{where}.{key} must be a string.");
        var s = v.GetString()!;
        if (s.Trim().Length == 0) throw new ContractException($"{where}.{key} is empty.");
        if (s.Length > 4096) throw new ContractException($"{where}.{key} is too long.");
        return s;
    }

    private static bool? OptBool(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ContractException($"{where}.{key} must be true or false."),
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,127}$")]
    private static partial Regex ProductRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.+\-]{0,127}$")]
    private static partial Regex ToolRegex();

    [GeneratedRegex(@"^(?<tool>[A-Za-z0-9_][A-Za-z0-9_.+\-]{0,127})(\s*>=\s*(?<version>\d+(\.\d+){0,3}))?$")]
    private static partial Regex RequirementRegex();

    [GeneratedRegex(@"^Contents/Helpers/[A-Za-z0-9_][A-Za-z0-9_.+\-]{0,127}$")]
    private static partial Regex HelperDestinationRegex();

    [GeneratedRegex(@"^Contents/Resources/[A-Za-z0-9_][A-Za-z0-9_. +\-]*(/[A-Za-z0-9_][A-Za-z0-9_. +\-]*)*$")]
    private static partial Regex ResourceDestinationRegex();

    [GeneratedRegex(@"^\d+(\.\d+){0,2}$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-]*(\.[A-Za-z0-9\-]+)+$")]
    private static partial Regex BundleIdRegex();
}
