using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.Account.Support;

/// <summary>
/// Builds the diagnostics a support request (or an AI explanation) sends: only what the selected action needs, each
/// value redacted (secrets masked, the user's home folder shown as ~) and listed so the user sees exactly what leaves
/// the computer. No source code is included here.
/// </summary>
public static class DiagnosticsCollector
{
    public const int MaxLogLines = 120;
    public const int MaxValueChars = 2000;

    public static IReadOnlyList<DiagnosticEntry> FromAnalysis(ProjectAnalysis a, HostEnvironment host, string appVersion)
    {
        var list = new List<DiagnosticEntry>
        {
            new("Environment", "HCG Executable Maker", appVersion),
            new("Environment", "Computer", $"{host.Platform.DisplayName()} {host.Architecture.DisplayName()} · {host.OsDescription}"),
            new("Project", "Detected by", a.InspectorId),
            new("Project", "Language", a.Language),
            new("Project", "Framework", a.Framework),
            new("Project", "Application type", a.ApplicationType),
            new("Project", "Support", a.Support.ToString()),
            new("Project", "Summary", a.SupportSummary),
        };
        foreach (var m in a.MissingDependencies.Take(20)) list.Add(new("Project", "Missing", m));
        foreach (var d in a.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Take(30))
            list.Add(new("Inspection", d.Severity.ToString(), d.Message + (d.Path is null ? "" : $" ({d.Path})")));
        foreach (var p in a.Projects.Take(10))
            list.Add(new("Projects", p.RelativePath, $"{p.ApplicationType}{(p.TargetFrameworks.Count > 0 ? " · " + string.Join(", ", p.TargetFrameworks) : "")}"));
        if (a.Node is { } node)
            foreach (var p in node.Projects.Take(10))
                list.Add(new("Node.js", p.DisplayDirectory, $"{p.Kind}{(p.ElectronVersion is { } e ? " · electron " + e : "")}{(p.MissingForDesktop.Count > 0 ? " · missing: " + string.Join(" | ", p.MissingForDesktop) : "")}"));
        return Clean(list);
    }

    public static IReadOnlyList<DiagnosticEntry> FromToolchains(ToolchainReport report, IEnumerable<string>? onlyIds = null)
    {
        var ids = onlyIds?.ToHashSet();
        return Clean(report.Toolchains.Where(t => ids is null || ids.Contains(t.Id))
            .Select(t => new DiagnosticEntry("Toolchains", t.DisplayName, $"{t.Status}{(t.Version is { } v ? " " + v : "")}{(t.Detail is { } d ? " · " + d : "")}")).ToList());
    }

    /// <summary>The failed build: settings summary, failure reason, error messages and the last log lines.</summary>
    public static IReadOnlyList<DiagnosticEntry> FromBuild(BuildRecord build, IReadOnlyList<string> logLines)
    {
        var s = build.Settings;
        var list = new List<DiagnosticEntry>
        {
            new("Build", "Status", build.Status.ToString()),
            new("Build", "Target", $"{s.Platform.DisplayName()} {s.Architecture.DisplayName()} · {string.Join(", ", s.PackagingFormats.Select(f => f.DisplayName()))}"),
            new("Build", "Failure", build.FailureReason ?? "—"),
        };
        foreach (var (k, v) in build.ToolchainVersions) list.Add(new("Build tools", k, v));
        foreach (var m in build.Messages.Where(m => m.Severity == DiagnosticSeverity.Error).Take(25))
            list.Add(new("Build errors", m.Code ?? "error", m.Text + (m.File is null ? "" : $" ({m.File}{(m.Line is { } l ? ":" + l : "")})")));
        var tail = logLines.TakeLast(MaxLogLines).ToList();
        if (tail.Count > 0) list.Add(new("Build log", $"last {tail.Count} lines", string.Join("\n", tail)));
        return Clean(list, maxChars: 16_000);
    }

    public static IReadOnlyList<DiagnosticEntry> Clean(IEnumerable<DiagnosticEntry> entries, int maxChars = MaxValueChars) =>
        entries.Select(e => e with { Label = Scrub(e.Label, 200), Value = Scrub(e.Value, maxChars) }).ToList();

    /// <summary>Secrets masked; the home folder (which contains the user name) replaced with ~; length capped.</summary>
    public static string Scrub(string text, int maxChars = MaxValueChars)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = SecretRedactor.Redact(text);
        if (home.Length > 3) result = result.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        return result.Length > maxChars ? result[..maxChars] + " …(truncated)" : result;
    }
}
