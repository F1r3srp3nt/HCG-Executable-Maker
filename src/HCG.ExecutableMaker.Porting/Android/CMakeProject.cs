using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.ProjectInspection;

namespace HCG.ExecutableMaker.Porting.Android;

/// <summary>
/// Static reading of a CMake project for the porting assessment: executables, libraries, link libraries and
/// find_package dependencies. The generated Android build does not rely on this reading for compilation — it includes
/// the project's own CMakeLists.txt with add_subdirectory — so dynamic constructs only reduce assessment precision.
/// </summary>
internal sealed partial class CMakeProject
{
    public sealed record Target(string Name, bool Executable, string Directory, IReadOnlyList<string> Sources, IReadOnlyList<string> LinkLibraries, bool DynamicSources);

    public string RootDirectory { get; }
    public string? ProjectName { get; private set; }
    public List<Target> Targets { get; } = [];
    public List<(string Package, string File)> Packages { get; } = [];
    public List<(string Construct, string File)> Unsupported { get; } = [];
    public List<string> Files { get; } = [];

    private CMakeProject(string root) => RootDirectory = root;

    /// <summary>Directories never treated as CMake project roots or sources.</summary>
    public static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "build", "_build", "out", ".git", "_deps", "CMakeFiles", "node_modules", ".cache" };

    public static IReadOnlyList<string> FindRoots(SourceIndex index) =>
        index.Named("CMakeLists.txt")
            .Where(f => !f.Split('/').SkipLast(1).Any(d => Ignored.Contains(d) || d.StartsWith("cmake-build", StringComparison.Ordinal)))
            .Where(f => ProjectCall().IsMatch(ReadText(index, f)))
            .Select(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
            // Keep only top-level projects (nested CMake projects are reached through add_subdirectory).
            .OrderBy(d => d.Length).Aggregate(new List<string>(), (acc, d) => { if (!acc.Any(a => a.Length == 0 || d.StartsWith(a + "/", StringComparison.Ordinal))) acc.Add(d); return acc; });

    public static CMakeProject Read(SourceIndex index, string rootDir)
    {
        var project = new CMakeProject(rootDir);
        project.Parse(index, rootDir, new Dictionary<string, string>(StringComparer.Ordinal), 0);
        return project;
    }

    private void Parse(SourceIndex index, string dir, Dictionary<string, string> vars, int depth)
    {
        var file = Join(dir, "CMakeLists.txt");
        if (depth > 8 || !index.Files.Contains(file)) return;
        Files.Add(file);
        vars = new Dictionary<string, string>(vars, StringComparer.Ordinal) { ["CMAKE_CURRENT_SOURCE_DIR"] = "", ["CMAKE_CURRENT_LIST_DIR"] = "" };
        foreach (var (name, rawArgs) in Commands(ReadText(index, file)))
        {
            var args = Arguments(rawArgs).Select(a => Expand(a, vars)).ToList();
            switch (name)
            {
                case "project" when depth == 0 && args.Count > 0: ProjectName ??= args[0]; vars["PROJECT_NAME"] = args[0]; break;
                case "set" when args.Count >= 1 && !args.Contains("CACHE") && !args.Contains("PARENT_SCOPE"):
                    vars[args[0]] = string.Join(';', args.Skip(1)); break;
                case "list" when args.Count >= 3 && args[0] == "APPEND":
                    vars[args[1]] = string.Join(';', new[] { vars.GetValueOrDefault(args[1]) }.Concat(args.Skip(2)).Where(v => !string.IsNullOrEmpty(v))); break;
                case "add_executable" or "add_library" when args.Count >= 1:
                {
                    var keywords = new HashSet<string> { "WIN32", "MACOSX_BUNDLE", "EXCLUDE_FROM_ALL", "STATIC", "SHARED", "MODULE", "OBJECT", "INTERFACE", "IMPORTED", "ALIAS", "GLOBAL" };
                    if (args.Contains("IMPORTED") || args.Contains("ALIAS")) break;
                    var sources = args.Skip(1).Where(a => !keywords.Contains(a)).SelectMany(a => a.Split(';')).Where(a => a.Length > 0).ToList();
                    var dynamic = sources.Any(s => s.Contains("$<", StringComparison.Ordinal) || s.Contains("${", StringComparison.Ordinal));
                    Targets.Add(new Target(args[0], name == "add_executable", dir,
                        sources.Where(s => !s.Contains('$')).Select(s => NormalizeSource(dir, s)).ToList(), [], dynamic));
                    break;
                }
                case "target_sources" when args.Count >= 2:
                {
                    var i = Targets.FindIndex(t => t.Name == args[0]);
                    if (i < 0) break;
                    var add = args.Skip(1).Where(a => a is not ("PRIVATE" or "PUBLIC" or "INTERFACE")).Where(a => !a.Contains('$')).Select(s => NormalizeSource(dir, s));
                    Targets[i] = Targets[i] with { Sources = [.. Targets[i].Sources, .. add] };
                    break;
                }
                case "target_link_libraries" when args.Count >= 2:
                {
                    var i = Targets.FindIndex(t => t.Name == args[0]);
                    if (i < 0) break;
                    var libs = args.Skip(1).Where(a => a is not ("PRIVATE" or "PUBLIC" or "INTERFACE" or "debug" or "optimized" or "general")).SelectMany(a => a.Split(';')).Where(a => a.Length > 0);
                    Targets[i] = Targets[i] with { LinkLibraries = [.. Targets[i].LinkLibraries, .. libs] };
                    break;
                }
                case "find_package" when args.Count >= 1: Packages.Add((args[0], file)); break;
                case "pkg_check_modules" or "pkg_search_module" when args.Count >= 2:
                    foreach (var m in args.Skip(1).Where(a => a is not ("REQUIRED" or "QUIET" or "IMPORTED_TARGET" or "GLOBAL")))
                        Packages.Add(("pkg-config:" + Regex.Replace(m, @"[<>=].*$", ""), file));
                    break;
                case "add_subdirectory" when args.Count >= 1 && !args[0].Contains('$'):
                    Parse(index, NormalizeSource(dir, args[0]), vars, depth + 1); break;
                case "file" when args.Count >= 1 && args[0] is "GLOB" or "GLOB_RECURSE":
                    Unsupported.Add(("file(" + args[0] + ")", file)); break;
                case "ExternalProject_Add" or "FetchContent_Declare":
                    Unsupported.Add((name, file)); break;
            }
        }
    }

    private static string NormalizeSource(string dir, string source)
    {
        var parts = new List<string>(dir.Length == 0 ? [] : dir.Split('/'));
        foreach (var p in source.Replace('\\', '/').Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else parts.Add(p);
        }
        return string.Join('/', parts);
    }

    private static string Expand(string arg, Dictionary<string, string> vars) =>
        VarRef().Replace(arg, m => m.Groups[1].Value switch
        {
            "CMAKE_CURRENT_SOURCE_DIR" or "CMAKE_CURRENT_LIST_DIR" or "PROJECT_SOURCE_DIR" or "CMAKE_SOURCE_DIR" => ".",
            var v when vars.TryGetValue(v, out var value) => value,
            _ => m.Value,
        }).Replace("./", "", StringComparison.Ordinal);

    /// <summary>Commands as (lower-case name, raw argument text); comments and bracket arguments handled.</summary>
    internal static IEnumerable<(string Name, string Args)> Commands(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '#')
            {
                if (i + 1 < text.Length && text[i + 1] == '[' && BracketEnd(text, i + 1) is var be and > 0) { i = be; continue; }
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var name = text[start..i].ToLowerInvariant();
                var j = i;
                while (j < text.Length && text[j] is ' ' or '\t') j++;
                if (j < text.Length && text[j] == '(')
                {
                    var depth = 0;
                    var k = j;
                    var inQuote = false;
                    for (; k < text.Length; k++)
                    {
                        var ch = text[k];
                        if (inQuote) { if (ch == '\\') k++; else if (ch == '"') inQuote = false; continue; }
                        if (ch == '"') inQuote = true;
                        else if (ch == '#') { while (k < text.Length && text[k] != '\n') k++; }
                        else if (ch == '(') depth++;
                        else if (ch == ')' && --depth == 0) break;
                    }
                    yield return (name, text[(j + 1)..Math.Min(k, text.Length)]);
                    i = k + 1;
                    continue;
                }
                continue;
            }
            i++;
        }
    }

    private static int BracketEnd(string text, int open)
    {
        var eq = 0;
        var i = open + 1;
        while (i < text.Length && text[i] == '=') { eq++; i++; }
        if (i >= text.Length || text[i] != '[') return -1;
        var close = "]" + new string('=', eq) + "]";
        var end = text.IndexOf(close, i, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + close.Length;
    }

    internal static List<string> Arguments(string raw)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        var hadQuote = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < raw.Length) { sb.Append(raw[++i]); continue; }
                if (c == '"') { quoted = false; continue; }
                sb.Append(c);
                continue;
            }
            if (c == '"') { quoted = true; hadQuote = true; continue; }
            if (c == '#') { while (i < raw.Length && raw[i] != '\n') i++; continue; }
            if (char.IsWhiteSpace(c) || c is '(' or ')')
            {
                if (sb.Length > 0 || hadQuote) { args.Add(sb.ToString()); sb.Clear(); hadQuote = false; }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0 || hadQuote) args.Add(sb.ToString());
        return args;
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;

    internal static string ReadText(SourceIndex index, string rel)
    {
        var path = index.FullPath(rel);
        return new FileInfo(path).Length > 2_000_000 ? "" : File.ReadAllText(path);
    }

    [GeneratedRegex(@"(?im)^\s*project\s*\(")]
    private static partial Regex ProjectCall();

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex VarRef();
}
