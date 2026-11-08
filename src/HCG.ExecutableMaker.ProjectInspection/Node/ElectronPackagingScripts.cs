using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Node;

/// <summary>
/// Reads package.json scripts that call electron-builder ("node scripts/build.mjs &amp;&amp; electron-builder --mac --arm64")
/// and splits them into compile steps HCG can run without a shell plus the electron-builder flags. The scripts are
/// parsed as text; nothing is executed.
/// </summary>
public static partial class ElectronPackagingScripts
{
    public static IReadOnlyList<ElectronPackagingScript> Find(IReadOnlyDictionary<string, string> scripts) =>
        scripts.Where(s => InvokesElectronBuilder(s.Value))
            .Select(s => Parse(s.Key, s.Value, scripts))
            .OrderBy(s => s.Problem is null ? 0 : 1).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    public static bool InvokesElectronBuilder(string command) =>
        Segments(command).Any(seg => BuilderIndex(Tokenize(seg)) >= 0);

    public static ElectronPackagingScript Parse(string name, string command, IReadOnlyDictionary<string, string> scripts)
    {
        var segments = Segments(command);
        var builderAt = segments.FindIndex(seg => BuilderIndex(Tokenize(seg)) >= 0);
        var tokens = Tokenize(segments[builderAt]);
        var args = tokens.Skip(BuilderIndex(tokens) + 1).ToList();
        var platforms = new List<string>();
        var archs = new List<string>();
        var targets = new List<string>();
        string? config = null;
        string? problem = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--mac" or "-m" or "--macos" or "-o": platforms.Add("mac"); break;
                case "--linux" or "-l": platforms.Add("linux"); break;
                case "--win" or "-w" or "--windows": platforms.Add("win"); break;
                case "--x64" or "--arm64" or "--ia32" or "--armv7l" or "--universal": archs.Add(a[2..]); break;
                case "--dir": targets.Add("dir"); break;
                case "--config" or "-c" when i + 1 < args.Count && !args[i + 1].StartsWith('-'): config = args[++i]; break;
                default:
                    if (a.StartsWith("--config=", StringComparison.Ordinal)) config = a["--config=".Length..];
                    break;
            }
        }
        if (builderAt < segments.Count - 1)
            problem = $"The script runs commands after electron-builder ({string.Join(" && ", segments.Skip(builderAt + 1))}); HCG runs electron-builder as the last step only.";

        var steps = new List<ElectronCompileStep>();
        var before = segments.Take(builderAt).ToList();
        if (before.Count > 0 && problem is null)
        {
            // The whole prefix may itself be a named script ("desktop:build": "node scripts/build-desktop.mjs").
            var prefix = string.Join(" && ", before);
            var named = scripts.FirstOrDefault(s => s.Key != name && Normalize(s.Value) == Normalize(prefix)).Key;
            if (named is not null) steps.Add(new ElectronCompileStep { NpmScript = named });
            else
            {
                foreach (var seg in before)
                {
                    var step = ResolveSegment(seg, name, scripts);
                    if (step is null)
                    {
                        problem = $"HCG cannot run the compile command \"{seg}\" from script \"{name}\" without a shell. " +
                                  $"Move it into its own package.json script (for example \"desktop:build\") and call that script before electron-builder.";
                        steps.Clear();
                        break;
                    }
                    steps.Add(step);
                }
            }
        }
        return new ElectronPackagingScript
        {
            Name = name, Command = command, CompileSteps = steps,
            Platforms = platforms.Distinct().ToList(), Archs = archs.Distinct().ToList(), Targets = targets, ConfigPath = config, Problem = problem,
        };
    }

    /// <summary>"npm run x" / "yarn x" / "pnpm run x" / "node file args" → a step; anything else (pipes, env assignments, other programs) → null.</summary>
    private static ElectronCompileStep? ResolveSegment(string segment, string self, IReadOnlyDictionary<string, string> scripts)
    {
        if (ShellMetaRegex().IsMatch(segment)) return null;
        var t = Tokenize(segment);
        if (t.Count == 0) return null;
        string? script = t switch
        {
            ["npm", "run" or "run-script", var s] => s,
            ["yarn", "run", var s] => s,
            ["pnpm", "run", var s] => s,
            ["bun", "run", var s] => s,
            ["yarn", var s] when scripts.ContainsKey(s) => s,
            ["pnpm", var s] when scripts.ContainsKey(s) => s,
            _ => null,
        };
        if (script is not null) return script != self && scripts.ContainsKey(script) && ScriptNameRegex().IsMatch(script) ? new ElectronCompileStep { NpmScript = script } : null;
        var exact = scripts.FirstOrDefault(s => s.Key != self && Normalize(s.Value) == Normalize(segment)).Key;
        if (exact is not null) return new ElectronCompileStep { NpmScript = exact };
        if (t[0] == "node" && t.Count >= 2 && !t[1].StartsWith('-') && t.Skip(1).All(a => SafeArgRegex().IsMatch(a)))
            return new ElectronCompileStep { NodeArguments = t.Skip(1).ToList() };
        return null;
    }

    private static int BuilderIndex(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i].Replace('\\', '/');
            if (Path.GetFileName(t) is "electron-builder" or "electron-builder.cmd" || t.EndsWith("electron-builder/cli.js", StringComparison.Ordinal) || t.EndsWith("electron-builder/out/cli/cli.js", StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static List<string> Segments(string command) =>
        command.Split("&&").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static string Normalize(string s) => string.Join(' ', Tokenize(s));

    /// <summary>Whitespace split honouring single and double quotes (for reading only; never re-joined into a shell string).</summary>
    public static List<string> Tokenize(string s)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        var has = false;
        foreach (var c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else cur.Append(c); continue; }
            if (c is '"' or '\'') { quote = c; has = true; continue; }
            if (char.IsWhiteSpace(c)) { if (has || cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); has = false; } continue; }
            cur.Append(c);
        }
        if (has || cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    [GeneratedRegex(@"[|;<>`$]|\|\||&(?!&)|^\s*\w+=")]
    private static partial Regex ShellMetaRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._@/=:+,-]+$")]
    private static partial Regex SafeArgRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._:@/+-]+$")]
    public static partial Regex ScriptNameRegex();
}
