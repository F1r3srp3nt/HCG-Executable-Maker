using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Native;

/// <summary>
/// Reads a Theos project as text (Makefiles, control file, layout): what it builds (tweak, preference bundle, app, tool,
/// library), its subprojects, the package configurations it defines (rootless / rootful / roothide, each with the SDK and
/// deployment target from TARGET) and its architectures. Nothing is run; values the Makefiles compute in ways this reader
/// does not follow are left unknown rather than guessed.
/// </summary>
public static partial class TheosAnalyzer
{
    private static readonly (string Variable, TheosInstanceType Type)[] InstanceVariables =
    [
        ("TWEAK_NAME", TheosInstanceType.Tweak), ("BUNDLE_NAME", TheosInstanceType.Bundle), ("APPLICATION_NAME", TheosInstanceType.Application),
        ("TOOL_NAME", TheosInstanceType.Tool), ("LIBRARY_NAME", TheosInstanceType.Library), ("FRAMEWORK_NAME", TheosInstanceType.Framework),
    ];

    private static readonly string[] MaintainerScriptNames = ["preinst", "postinst", "prerm", "postrm", "extrainst_"];

    /// <summary>A Makefile is a Theos project when it includes Theos' makefiles.</summary>
    public static bool IsTheosMakefile(string text) => TheosIncludeRegex().IsMatch(text);

    /// <summary>The SUBPROJECTS directories a Theos Makefile names, relative to its own directory.</summary>
    public static IReadOnlyList<string> SubprojectNames(string text) =>
        Lines(text).Select(l => SubprojectsRegex().Match(l)).Where(m => m.Success)
            .SelectMany(m => m.Groups["v"].Value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).Distinct().ToList();

    public static TheosProjectInfo Analyze(SourceIndex index, string dir, string makefile)
    {
        var text = Read(index, makefile) ?? "";
        var vars = Variables(text);
        var instances = Instances(index, dir, makefile, text, vars).ToList();
        var subprojects = new List<string>();
        var usesParent = UsesParent(text);
        foreach (var sub in SubprojectNames(text))
        {
            var subDir = NativeProjectScanner.Join(dir, sub.TrimEnd('/'));
            if (sub.Contains("..", StringComparison.Ordinal) || sub.StartsWith('/')) continue;
            subprojects.Add(subDir);
            var subMakefile = new[] { "Makefile", "makefile", "GNUmakefile" }.Select(n => NativeProjectScanner.Join(subDir, n)).FirstOrDefault(f => index.Files.Contains(f));
            if (subMakefile is null) continue;
            var subText = Read(index, subMakefile) ?? "";
            usesParent |= UsesParent(subText);
            instances.AddRange(Instances(index, subDir, subMakefile, subText, Variables(subText)));
        }

        var control = new[] { "control", "layout/DEBIAN/control" }.Select(c => NativeProjectScanner.Join(dir, c)).FirstOrDefault(f => index.Files.Contains(f));
        var fields = control is null ? new Dictionary<string, string>() : ControlFields(Read(index, control) ?? "");
        var scripts = MaintainerScriptNames
            .SelectMany(n => new[] { NativeProjectScanner.Join(dir, "layout/DEBIAN/" + n), NativeProjectScanner.Join(dir, n) })
            .Where(f => index.Files.Contains(f)).ToList();
        var (archs, archVar) = Architectures(text, vars);
        return new TheosProjectInfo
        {
            Makefile = makefile, ControlFile = control,
            PackageId = fields.GetValueOrDefault("Package"), PackageName = fields.GetValueOrDefault("Name"), PackageVersion = fields.GetValueOrDefault("Version"),
            ControlArchitecture = fields.GetValueOrDefault("Architecture"), Depends = fields.GetValueOrDefault("Depends"), Section = fields.GetValueOrDefault("Section"),
            Instances = instances, Subprojects = subprojects, Schemes = Schemes(text, vars), Architectures = archs, ArchitectureVariable = archVar,
            MaintainerScripts = scripts, UsesParentDirectory = usesParent,
        };
    }

    // ------------------------------------------------------------------ instances

    private static IEnumerable<TheosInstance> Instances(SourceIndex index, string dir, string makefile, string text, IReadOnlyDictionary<string, string> vars)
    {
        foreach (var (variable, type) in InstanceVariables)
            foreach (var m in Lines(text).Select(l => Regex.Match(l, @"^\s*" + variable + @"\s*[:?+]?=\s*(?<v>.+)$")).Where(m => m.Success))
                foreach (var name in Expand(m.Groups["v"].Value, vars).Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!NameRegex().IsMatch(name)) continue;
                    var install = Lines(text).Select(l => Regex.Match(l, @"^\s*" + Regex.Escape(name) + @"_INSTALL_PATH\s*[:?+]?=\s*(?<v>\S+)")).FirstOrDefault(x => x.Success)?.Groups["v"].Value;
                    var actualType = type == TheosInstanceType.Bundle && (install?.Contains("PreferenceBundles", StringComparison.Ordinal) == true
                                                                            || text.Contains("_PRIVATE_FRAMEWORKS", StringComparison.Ordinal) && text.Contains("Preferences", StringComparison.Ordinal))
                        ? TheosInstanceType.PreferenceBundle : type;
                    var filter = type == TheosInstanceType.Tweak && index.Files.Contains(NativeProjectScanner.Join(dir, name + ".plist")) ? NativeProjectScanner.Join(dir, name + ".plist") : null;
                    yield return new TheosInstance { Type = actualType, Name = name, Makefile = makefile, InstallPath = install, FilterPlist = filter };
                }
    }

    // ------------------------------------------------------------------ schemes

    /// <summary>
    /// The configurations the Makefile defines. Recognized shapes: a fixed THEOS_PACKAGE_SCHEME / TARGET, or an
    /// ifeq ($(VAR),value) … else ifeq … else … endif chain whose branches set THEOS_PACKAGE_SCHEME or TARGET.
    /// </summary>
    internal static IReadOnlyList<TheosScheme> Schemes(string text, IReadOnlyDictionary<string, string> vars)
    {
        var lines = Lines(text).ToList();
        var schemes = new List<TheosScheme>();
        for (var i = 0; i < lines.Count; i++)
        {
            var first = IfeqRegex().Match(lines[i]);
            if (!first.Success) continue;
            var variable = first.Groups["var"].Value;
            var branches = new List<(string? Value, List<string> Body)> { (first.Groups["val"].Value, []) };
            var depth = 0;
            var j = i + 1;
            for (; j < lines.Count; j++)
            {
                var l = lines[j].Trim();
                if (depth == 0 && ElseIfeqRegex().Match(l) is { Success: true } e)
                {
                    branches.Add(e.Groups["var"].Value == variable ? (e.Groups["val"].Value, []) : ("\0other-condition", []));
                    continue;
                }
                if (depth == 0 && l == "else") { branches.Add((null, [])); continue; }
                if (depth == 0 && l == "endif") break;
                if (Regex.IsMatch(l, @"^if(n?eq|n?def)\b")) depth++;
                else if (l == "endif") depth--;
                branches[^1].Body.Add(lines[j]);
            }
            var relevant = branches.Where(b => b.Value != "\0other-condition" && b.Body.Any(l => SchemeRegex().IsMatch(l) || TargetRegex().IsMatch(l))).ToList();
            if (relevant.Count == 0) { continue; }
            var defaultValue = DefaultRegex(variable).Match(text) is { Success: true } d ? Expand(d.Groups["v"].Value.Trim(), vars) : null;
            var explicitValues = relevant.Where(b => b.Value is not null).Select(b => b.Value!).ToHashSet(StringComparer.Ordinal);
            foreach (var (value, body) in relevant)
            {
                var scheme = body.Select(l => SchemeRegex().Match(l)).FirstOrDefault(m => m.Success)?.Groups["v"].Value.Trim() ?? "rootful";
                var (sdk, deploy) = ParseTarget(body.Select(l => TargetRegex().Match(l)).FirstOrDefault(m => m.Success)?.Groups["v"].Value);
                var error = body.Select(l => ErrorRegex().Match(l)).FirstOrDefault(m => m.Success)?.Groups["msg"].Value.Trim();
                // An "else" branch applies to any other value; the conventional value for it is its scheme's name.
                var selected = value ?? (explicitValues.Contains(scheme) ? null : scheme);
                if (selected is null) continue;
                schemes.Add(new TheosScheme
                {
                    SelectorVariable = variable, SelectorValue = selected, PackageScheme = scheme, SdkVersion = sdk, DeploymentTarget = deploy,
                    ProjectError = error, IsDefault = defaultValue == selected,
                });
            }
            // The roothide-style branch that only contains $(error …) is kept: the project refuses it, and says why.
            foreach (var (value, body) in branches.Where(b => b.Value is not null && b.Value != "\0other-condition" && !relevant.Contains(b)))
                if (body.Select(l => ErrorRegex().Match(l)).FirstOrDefault(m => m.Success) is { } err && value!.Length > 0 && !schemes.Any(s => s.SelectorValue == value))
                    schemes.Add(new TheosScheme
                    {
                        SelectorVariable = variable, SelectorValue = value, PackageScheme = value is "rootless" or "roothide" ? value : "rootful",
                        ProjectError = err.Groups["msg"].Value.Trim(), IsDefault = defaultValue == value,
                    });
            if (schemes.Count > 0) return schemes;
            i = j;
        }
        // Fixed configuration.
        var fixedScheme = vars.GetValueOrDefault("THEOS_PACKAGE_SCHEME") is { Length: > 0 } fs ? fs : "rootful";
        var (fsdk, fdeploy) = ParseTarget(vars.GetValueOrDefault("TARGET"));
        return [new TheosScheme { PackageScheme = fixedScheme, SdkVersion = fsdk, DeploymentTarget = fdeploy, IsDefault = true }];
    }

    /// <summary>iphone:clang:16.5:15.0 → ("16.5", "15.0"); "latest" SDK → null.</summary>
    internal static (string? Sdk, string? Deployment) ParseTarget(string? target)
    {
        if (target is null) return (null, null);
        var parts = target.Trim().Split(':');
        if (parts.Length < 2 || parts[0] != "iphone") return (null, null);
        string? Ver(int i) => parts.Length > i && VersionRegex().IsMatch(parts[i]) ? parts[i] : null;
        return (Ver(2), Ver(3));
    }

    private static (IReadOnlyList<string> Archs, string? Variable) Architectures(string text, IReadOnlyDictionary<string, string> vars)
    {
        var raw = Lines(text).Select(l => ArchsRegex().Match(l)).LastOrDefault(m => m.Success)?.Groups["v"].Value.Trim();
        if (raw is null) return ([], null);
        var variable = VarRefRegex().Match(raw) is { Success: true } r && r.Value == raw ? r.Groups["n"].Value : null;
        var expanded = Expand(raw, vars).Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(a => ArchRegex().IsMatch(a)).ToList();
        return (expanded, variable);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Unconditional and default (?=) assignments, first definition wins for ?=, last for = / :=.</summary>
    internal static IReadOnlyDictionary<string, string> Variables(string text)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var depth = 0;
        foreach (var line in Lines(text))
        {
            var t = line.Trim();
            if (Regex.IsMatch(t, @"^if(n?eq|n?def)\b")) { depth++; continue; }
            if (t == "endif") { depth = Math.Max(0, depth - 1); continue; }
            if (depth > 0) continue;
            var m = AssignRegex().Match(line);
            if (!m.Success) continue;
            var name = m.Groups["n"].Value;
            var value = m.Groups["v"].Value.Trim();
            switch (m.Groups["op"].Value)
            {
                case "?=": vars.TryAdd(name, value); break;
                case "+=": vars[name] = vars.TryGetValue(name, out var old) ? old + " " + value : value; break;
                default: vars[name] = value; break;
            }
        }
        return vars;
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> vars)
    {
        for (var i = 0; i < 5 && value.Contains("$(", StringComparison.Ordinal); i++)
            value = VarRefRegex().Replace(value, m => vars.TryGetValue(m.Groups["n"].Value, out var v) ? v : m.Value);
        return value;
    }

    /// <summary>Logical lines: comments removed, backslash continuations joined.</summary>
    internal static IEnumerable<string> Lines(string text)
    {
        var current = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimStart().StartsWith('#') ? "" : raw;
            if (line.EndsWith('\\')) { current += line[..^1] + " "; continue; }
            yield return current + line;
            current = "";
        }
        if (current.Length > 0) yield return current;
    }

    private static bool UsesParent(string text) => Regex.IsMatch(text, @"(^|[\s=(])\.\.(/|\s|$)", RegexOptions.Multiline);

    internal static Dictionary<string, string> ControlFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i > 0 && !char.IsWhiteSpace(line[0])) fields[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return fields;
    }

    private static string? Read(SourceIndex index, string rel)
    {
        try
        {
            var full = index.FullPath(rel);
            return new FileInfo(full).Length > 512 * 1024 ? null : File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Regex DefaultRegex(string variable) => new(@"(?m)^\s*" + Regex.Escape(variable) + @"\s*\?=\s*(?<v>\S+)");

    [GeneratedRegex(@"\$\(THEOS(?:_MAKE_PATH)?\)/(?:makefiles/)?[\w./]+\.mk")] private static partial Regex TheosIncludeRegex();
    [GeneratedRegex(@"^\s*SUBPROJECTS\s*[:?+]?=\s*(?<v>.+)$")] private static partial Regex SubprojectsRegex();
    [GeneratedRegex(@"^\s*ifeq\s*\(\s*\$\((?<var>[A-Za-z_][A-Za-z0-9_]*)\)\s*,\s*(?<val>[^)\s]*)\s*\)")] private static partial Regex IfeqRegex();
    [GeneratedRegex(@"^else\s+ifeq\s*\(\s*\$\((?<var>[A-Za-z_][A-Za-z0-9_]*)\)\s*,\s*(?<val>[^)\s]*)\s*\)")] private static partial Regex ElseIfeqRegex();
    [GeneratedRegex(@"^\s*THEOS_PACKAGE_SCHEME\s*[:?]?=\s*(?<v>\w+)")] private static partial Regex SchemeRegex();
    [GeneratedRegex(@"^\s*TARGET\s*[:?]?=\s*(?<v>\S+)")] private static partial Regex TargetRegex();
    [GeneratedRegex(@"\$\(error\s+(?<msg>[^)]*(?:\([^)]*\)[^)]*)*)\)")] private static partial Regex ErrorRegex();
    [GeneratedRegex(@"^\s*ARCHS\s*[:?+]?=\s*(?<v>.+)$")] private static partial Regex ArchsRegex();
    [GeneratedRegex(@"^\s*(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*(?<op>:=|\?=|\+=|=)\s*(?<v>.*)$")] private static partial Regex AssignRegex();
    [GeneratedRegex(@"\$\((?<n>[A-Za-z_][A-Za-z0-9_]*)\)")] private static partial Regex VarRefRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.+-]{0,63}$")] private static partial Regex NameRegex();
    [GeneratedRegex(@"^\d+(\.\d+){0,2}$")] private static partial Regex VersionRegex();
    [GeneratedRegex(@"^(arm64e?|armv7s?|x86_64)$")] private static partial Regex ArchRegex();
}
