using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.BuildEngine.MacXcode;

/// <summary>
/// Structured diagnostics for an xcodebuild run, read from its complete log. It reports:
/// <list type="bullet">
/// <item>compiler errors and warnings with file, line and column, and the target that compiled them;</item>
/// <item>the targets and commands xcodebuild lists as failed;</item>
/// <item>Swift package resolution errors, signing errors, linker errors and test failures, each named as such;</item>
/// <item>xcodebuild's own result lines and the duration.</item>
/// </list>
/// The report (<c>hcg.build-diagnostics</c> v1, JSON, plus a Markdown copy for people and development agents) never calls
/// a failed build successful: the status comes from the build, and the result lines are quoted as xcodebuild wrote them.
/// </summary>
public static partial class XcodeDiagnostics
{
    public const string Format = "hcg.build-diagnostics";
    public const int FormatVersion = 1;
    public const string JsonFileName = "xcode-diagnostics.json";
    public const string MarkdownFileName = "XCODE-DIAGNOSTICS.md";

    /// <param name="Category">compiler · linker · dependency · signing · project · test · other.</param>
    public sealed record Issue(string Severity, string Category, string Message, string? File, int? Line, int? Column, string? Target, string? Project);
    public sealed record FailedCommand(string Command, string? Target, string? Project);
    public sealed record TestFailure(string Test, string Message, string? File, int? Line);

    public sealed record Report(
        string Status, IReadOnlyList<Issue> Issues, IReadOnlyList<FailedCommand> FailedCommands, IReadOnlyList<string> FailedTargets,
        IReadOnlyList<TestFailure> TestFailures, IReadOnlyList<string> ResultLines, TimeSpan? Duration)
    {
        public int Errors => Issues.Count(i => i.Severity == "error");
        public int Warnings => Issues.Count(i => i.Severity == "warning");
        public IEnumerable<Issue> ByCategory(string category) => Issues.Where(i => i.Category == category);
    }

    /// <param name="lines">The build log, line by line (HCG's build.log or xcodebuild's raw output).</param>
    /// <param name="status">The build's own status (Succeeded, Failed, Cancelled, TimedOut). The report never upgrades it.</param>
    /// <param name="sourceRoots">Absolute folders whose prefix is removed from file paths (the build copy), so files read relative to the project.</param>
    public static Report Analyze(IEnumerable<string> lines, string status, TimeSpan? duration, IReadOnlyList<string>? sourceRoots = null)
    {
        var issues = new List<Issue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failedCommands = new List<FailedCommand>();
        var tests = new List<TestFailure>();
        var results = new List<string>();
        string? target = null, project = null;
        var inFailedList = false;

        string Rel(string path)
        {
            foreach (var root in sourceRoots ?? [])
            {
                var r = root.TrimEnd('/') + "/";
                if (path.StartsWith(r, StringComparison.Ordinal)) return path[r.Length..];
            }
            return path;
        }

        void Add(string severity, string category, string message, string? file = null, int? line = null, int? col = null)
        {
            var key = $"{severity}|{file}|{line}|{col}|{message}";
            if (seen.Add(key)) issues.Add(new Issue(severity, category, message.Trim(), file is null ? null : Rel(file), line, col, target, project));
        }

        foreach (var raw in lines)
        {
            var line = StripLogPrefix(raw);
            if (line.Length == 0) continue;

            if (InTarget().Match(line) is { Success: true } it) { target = it.Groups["t"].Value; project = it.Groups["p"].Value; }
            else if (OldTarget().Match(line) is { Success: true } ot) { target = ot.Groups["t"].Value; project = ot.Groups["p"].Value; }

            if (line.StartsWith("The following build commands failed:", StringComparison.Ordinal)) { inFailedList = true; continue; }
            if (inFailedList)
            {
                if (FailureCount().IsMatch(line)) { inFailedList = false; continue; }
                var cmd = line.Trim();
                var m = InTarget().Match(cmd);
                failedCommands.Add(new FailedCommand(m.Success ? cmd[..m.Index].TrimEnd() : cmd, m.Success ? m.Groups["t"].Value : null, m.Success ? m.Groups["p"].Value : null));
                continue;
            }

            if (ResultLine().Match(line) is { Success: true } rl) { results.Add(rl.Value.Trim()); continue; }

            if (TestCaseFailed().Match(line) is { Success: true } tc)
            {
                tests.Add(new TestFailure(tc.Groups["test"].Value, "failed" + (tc.Groups["time"].Success ? $" ({tc.Groups["time"].Value} s)" : ""), null, null));
                continue;
            }
            if (SwiftTestingIssue().Match(line) is { Success: true } st)
            {
                tests.Add(new TestFailure(st.Groups["test"].Value, st.Groups["msg"].Value, st.Groups["file"].Value, int.Parse(st.Groups["line"].Value)));
                Add("error", "test", $"{st.Groups["test"].Value}: {st.Groups["msg"].Value}", st.Groups["file"].Value, int.Parse(st.Groups["line"].Value));
                continue;
            }

            if (FileDiagnostic().Match(line) is { Success: true } fd)
            {
                var sev = fd.Groups["sev"].Value;
                if (sev == "note") continue;
                var msg = fd.Groups["msg"].Value;
                var file = fd.Groups["file"].Value;
                var ln = int.Parse(fd.Groups["line"].Value);
                int? col = fd.Groups["col"].Success ? int.Parse(fd.Groups["col"].Value) : null;
                if (XcTestAssertion().Match(msg) is { Success: true } xa)
                {
                    tests.Add(new TestFailure(xa.Groups["test"].Value, xa.Groups["msg"].Value, Rel(file), ln));
                    Add("error", "test", $"{xa.Groups["test"].Value}: {xa.Groups["msg"].Value}", file, ln, col);
                }
                else Add(sev, Classify(msg, isCompiler: true), msg, file, ln, col);
                continue;
            }

            // A project-level problem names the project or file without a line: "/…/App.xcodeproj: error: Signing for …".
            if (PathDiagnostic().Match(line) is { Success: true } pd)
            {
                var msg = InTarget().Replace(pd.Groups["msg"].Value, "").TrimEnd();
                Add(pd.Groups["sev"].Value, Classify(msg, isCompiler: false), msg, pd.Groups["file"].Value);
                continue;
            }
            if (ToolError().Match(line) is { Success: true } te)
            {
                var msg = te.Groups["msg"].Value;
                Add(te.Groups["sev"].Value, Classify(msg, isCompiler: false, tool: te.Groups["tool"].Value), msg);
                continue;
            }
            if (Undefined().IsMatch(line)) Add("error", "linker", line.Trim());
        }

        var failedTargets = failedCommands.Select(c => c.Target).OfType<string>()
            .Concat(issues.Where(i => i.Severity == "error" && i.Target is not null).Select(i => i.Target!))
            .Distinct(StringComparer.Ordinal).ToList();
        return new Report(status, issues, failedCommands, failedTargets, tests, results, duration);
    }

    /// <summary>The category an error belongs to: what a person or agent fixes first.</summary>
    public static string Classify(string message, bool isCompiler, string? tool = null)
    {
        var m = message;
        if (SigningText().IsMatch(m)) return "signing";
        if (DependencyText().IsMatch(m)) return "dependency";
        if (tool is "ld" || m.StartsWith("linker command failed", StringComparison.Ordinal) || m.Contains("Undefined symbol", StringComparison.Ordinal)) return "linker";
        if (ProjectText().IsMatch(m)) return "project";
        return isCompiler ? "compiler" : "other";
    }

    /// <summary>HCG's build.log prefixes each line ("[19:00:00]   " for tool output, "[19:00:00] ERROR " for HCG's own); xcodebuild's raw output has none.</summary>
    private static string StripLogPrefix(string raw)
    {
        var m = LogPrefix().Match(raw);
        return (m.Success ? raw[m.Length..] : raw).TrimEnd('\r');
    }

    // ------------------------------------------------------------------ report files

    public sealed record Context(string Project, string Scheme, string Configuration, string Architecture, string? Xcode, DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt, string LogFile, IReadOnlyList<string> ResultBundles, string? FailureReason);

    public static JsonObject ToJson(Report r, Context c) => new()
    {
        ["format"] = Format,
        ["formatVersion"] = FormatVersion,
        ["tool"] = "xcodebuild",
        ["project"] = c.Project, ["scheme"] = c.Scheme, ["configuration"] = c.Configuration, ["architecture"] = c.Architecture, ["xcode"] = c.Xcode,
        ["status"] = r.Status,
        ["failureReason"] = c.FailureReason,
        ["startedAt"] = c.StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["endedAt"] = c.EndedAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["durationSeconds"] = r.Duration is { } d ? Math.Round(d.TotalSeconds, 1) : null,
        ["xcodebuildResults"] = new JsonArray(r.ResultLines.Select(x => (JsonNode?)x).ToArray()),
        ["counts"] = new JsonObject { ["errors"] = r.Errors, ["warnings"] = r.Warnings, ["testFailures"] = r.TestFailures.Count },
        ["failedTargets"] = new JsonArray(r.FailedTargets.Select(x => (JsonNode?)x).ToArray()),
        ["failedCommands"] = new JsonArray(r.FailedCommands.Select(x => (JsonNode?)new JsonObject { ["command"] = x.Command, ["target"] = x.Target, ["project"] = x.Project }).ToArray()),
        ["issues"] = new JsonArray(r.Issues.Select(i => (JsonNode?)new JsonObject
        {
            ["severity"] = i.Severity, ["category"] = i.Category, ["message"] = i.Message, ["file"] = i.File, ["line"] = i.Line, ["column"] = i.Column,
            ["target"] = i.Target, ["project"] = i.Project,
        }).ToArray()),
        ["testFailures"] = new JsonArray(r.TestFailures.Select(t => (JsonNode?)new JsonObject { ["test"] = t.Test, ["message"] = t.Message, ["file"] = t.File, ["line"] = t.Line }).ToArray()),
        ["logFile"] = c.LogFile,
        ["resultBundles"] = new JsonArray(c.ResultBundles.Select(x => (JsonNode?)x).ToArray()),
    };

    /// <summary>A Markdown report for a person or a development agent: what failed, where, and the source lines around each error.</summary>
    /// <param name="sourceRoot">Where the files named in the report can be read (the build copy), for excerpts; null for none.</param>
    public static string ToMarkdown(Report r, Context c, string? sourceRoot)
    {
        var sb = new StringBuilder();
        var verdict = r.Status == "Succeeded" ? "SUCCEEDED" : r.Status.ToUpperInvariant();
        sb.AppendLine($"# Xcode build diagnostics — {c.Scheme} ({c.Configuration}, {c.Architecture})");
        sb.AppendLine();
        sb.AppendLine($"- **Status:** {verdict}{(c.FailureReason is { } fr && r.Status != "Succeeded" ? $" — {fr}" : "")}");
        sb.AppendLine($"- **Project:** `{c.Project}` · scheme `{c.Scheme}` · Xcode {c.Xcode ?? "unknown"}");
        sb.AppendLine($"- **Duration:** {(r.Duration is { } d ? $"{d.TotalSeconds:F1} s" : "unknown")} (started {c.StartedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC)");
        sb.AppendLine($"- **Counts:** {r.Errors} error(s), {r.Warnings} warning(s), {r.TestFailures.Count} test failure(s)");
        if (r.ResultLines.Count > 0) sb.AppendLine($"- **xcodebuild said:** {string.Join(" · ", r.ResultLines.Select(x => $"`{x}`"))}");
        if (r.FailedTargets.Count > 0) sb.AppendLine($"- **Failed targets:** {string.Join(", ", r.FailedTargets.Select(t => $"`{t}`"))}");
        sb.AppendLine($"- **Complete log:** `{c.LogFile}`");
        foreach (var b in c.ResultBundles) sb.AppendLine($"- **Result bundle (open in Xcode):** `{b}`");
        sb.AppendLine();

        foreach (var (category, title) in new[] { ("dependency", "Dependency resolution errors"), ("signing", "Signing errors"), ("project", "Project and scheme errors"),
                     ("compiler", "Compiler errors"), ("linker", "Linker errors"), ("test", "Test failures"), ("other", "Other errors") })
        {
            var errors = r.Issues.Where(i => i.Category == category && i.Severity == "error").ToList();
            if (errors.Count == 0) continue;
            sb.AppendLine($"## {title} ({errors.Count})");
            sb.AppendLine();
            foreach (var group in errors.GroupBy(e => e.File ?? ""))
            {
                if (group.Key.Length > 0) sb.AppendLine($"### `{group.Key}`").AppendLine();
                foreach (var e in group)
                {
                    sb.AppendLine($"- {(e.Line is { } l ? $"line {l}{(e.Column is { } col ? $", column {col}" : "")}: " : "")}{e.Message}{(e.Target is { } t ? $" _(target `{t}`)_" : "")}");
                    if (sourceRoot is not null && e.File is { } f && e.Line is { } ln && Excerpt(sourceRoot, f, ln) is { } ex)
                        sb.AppendLine().AppendLine("  ```swift").Append(ex).AppendLine("  ```");
                }
                sb.AppendLine();
            }
        }
        if (r.FailedCommands.Count > 0)
        {
            sb.AppendLine("## Commands xcodebuild reported as failed").AppendLine();
            foreach (var fc in r.FailedCommands) sb.AppendLine($"- `{fc.Command}`{(fc.Target is { } t ? $" (target `{t}`, project `{fc.Project}`)" : "")}");
            sb.AppendLine();
        }
        var warnings = r.Issues.Where(i => i.Severity == "warning").ToList();
        if (warnings.Count > 0)
        {
            sb.AppendLine($"## Warnings ({warnings.Count})").AppendLine();
            foreach (var w in warnings.Take(200))
                sb.AppendLine($"- {(w.File is { } f ? $"`{f}`{(w.Line is { } l ? $":{l}" : "")} " : "")}{w.Message}");
            if (warnings.Count > 200) sb.AppendLine($"- … {warnings.Count - 200} more in `{JsonFileName}`");
            sb.AppendLine();
        }
        if (r.Status != "Succeeded" && r.Errors == 0 && r.FailedCommands.Count == 0)
            sb.AppendLine("No compiler, dependency or signing error was recognised in the log. Read the complete log above: the build did not succeed.").AppendLine();
        return sb.ToString();
    }

    private static string? Excerpt(string root, string relative, int line)
    {
        var path = Path.Combine(root, relative);
        if (!File.Exists(path)) return null;
        try
        {
            var lines = File.ReadLines(path).Take(line + 2).ToList();
            if (line < 1 || line > lines.Count) return null;
            var sb = new StringBuilder();
            for (var i = Math.Max(1, line - 2); i <= Math.Min(lines.Count, line + 2); i++)
                sb.AppendLine($"  {(i == line ? ">" : " ")}{i,5} | {lines[i - 1]}");
            return sb.ToString();
        }
        catch (IOException) { return null; }
    }

    /// <summary>Writes xcode-diagnostics.json and XCODE-DIAGNOSTICS.md into <paramref name="folder"/>; returns both paths.</summary>
    public static IReadOnlyList<string> Write(string folder, Report r, Context c, string? sourceRoot)
    {
        Directory.CreateDirectory(folder);
        var json = Path.Combine(folder, JsonFileName);
        var md = Path.Combine(folder, MarkdownFileName);
        // Reports are handed to people and agents: authentication data is redacted as in the build log.
        File.WriteAllText(json, HCG.ExecutableMaker.Security.SecretRedactor.Redact(ToJson(r, c).ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) + "\n");
        File.WriteAllText(md, HCG.ExecutableMaker.Security.SecretRedactor.Redact(ToMarkdown(r, c, sourceRoot)));
        return [json, md];
    }

    // ------------------------------------------------------------------ patterns

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\] (?:  |[A-Z]+ )")] private static partial Regex LogPrefix();
    [GeneratedRegex(@"\(in target '(?<t>[^']+)' from project '(?<p>[^']+)'\)")] private static partial Regex InTarget();
    [GeneratedRegex(@"^=== BUILD TARGET (?<t>.+?) OF PROJECT (?<p>.+?) WITH")] private static partial Regex OldTarget();
    [GeneratedRegex(@"^\(\d+ failures?\)$")] private static partial Regex FailureCount();
    [GeneratedRegex(@"\*\* (?:BUILD|TEST|ARCHIVE|CLEAN|ANALYZE|EXPORT|TEST BUILD|TEST EXECUTE) (?:SUCCEEDED|FAILED|INTERRUPTED) \*\*.*$")] private static partial Regex ResultLine();
    [GeneratedRegex(@"^(?<file>/[^:\n]+?):(?<line>\d+):(?:(?<col>\d+):)? (?<sev>error|warning|note): (?<msg>.+)$")] private static partial Regex FileDiagnostic();
    [GeneratedRegex(@"^(?<file>/[^:\n]+?): (?<sev>error|warning): (?<msg>.+)$")] private static partial Regex PathDiagnostic();
    [GeneratedRegex(@"^(?:(?<tool>xcodebuild|ld|clang|swift-frontend|swift|codesign|ibtool|actool|PhaseScriptExecution|SwiftPM|xcrun): )?(?<sev>error|warning): (?<msg>.+)$")] private static partial Regex ToolError();
    [GeneratedRegex(@"^Undefined symbols? for architecture")] private static partial Regex Undefined();
    [GeneratedRegex(@"^Test [Cc]ase '(?<test>[^']+)' failed(?: \((?<time>[\d.]+) seconds\))?")] private static partial Regex TestCaseFailed();
    [GeneratedRegex(@"^\s*-\[(?<test>[^\]]+)\] : (?<msg>.+)$")] private static partial Regex XcTestAssertion();
    [GeneratedRegex(@"^[✘x] Test (?<test>.+?) recorded an issue at (?<file>[^:]+):(?<line>\d+):\d+: (?<msg>.+)$")] private static partial Regex SwiftTestingIssue();
    [GeneratedRegex(@"(?i)requires a development team|no signing certificate|no profiles? for|provisioning profile|code ?sign(?:ing)? |codesign|errSec\w+|signing certificate|resource fork, Finder information|is not signed|team ID|keychain")] private static partial Regex SigningText();
    [GeneratedRegex(@"(?i)resolve package|package resolution|dependencies could not be resolved|could not find (?:package|module)|missing package product|no such module|failed to clone|unable to (?:resolve|fetch)|package\.resolved|repository not found|swift package")] private static partial Regex DependencyText();
    [GeneratedRegex(@"(?i)does not contain a scheme|scheme .* is not configured|is not currently configured for the|unable to find a destination|the workspace .* does not|project .* cannot be opened|no such (?:project|workspace)")] private static partial Regex ProjectText();
}
