using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HCG.Impactor.Devices;
using HCG.Impactor.Model;
using HCG.Impactor.Signing;

namespace HCG.Impactor.Diagnostics;

/// <summary>Everything known about one Impactor session (inspect → plan → sign → install), for export.</summary>
public sealed record ImpactorSession
{
    public ImpactorCapabilities? Capabilities { get; init; }
    public IpaReport? Inspection { get; init; }
    public SigningPlan? Plan { get; init; }
    public SignResult? Signing { get; init; }
    public AppleDevice? Device { get; init; }
    public IReadOnlyList<Finding> DeviceFindings { get; init; } = [];
    public InstallResult? Install { get; init; }
    public LaunchResult? Launch { get; init; }
    public string? DevicectlVersion { get; init; }
    public string? XcodeVersion { get; init; }
}

/// <summary>
/// Exports an Impactor session as HCG's shared diagnostic format <c>hcg.diagnostic-report/1</c> (the format HCG Device
/// Link carries and HCG System Tester and Sentinel read) plus a readable Markdown report. Everything is redacted with
/// policy <c>hcg.redaction/1</c>: credentials, UDIDs, ECIDs, serial numbers, e-mail addresses and home-folder names.
/// </summary>
public static class ImpactorReport
{
    public const string Schema = "hcg.diagnostic-report/1";
    public const string RedactionPolicy = "hcg.redaction/1";

    public sealed record Origin(string App, string AppVersion, string InstallationId, string DeviceName, string? Module, string? ModuleVersion);
    public sealed record Redaction(string Policy, bool Applied);
    public sealed record Summary(string Status, string? HighestSeverity, int FindingCount);
    public sealed record ReportFinding(string Title, string? Severity, string? Confidence, string? Basis, string? Location, IReadOnlyList<string> Evidence, string? Explanation, string? Remediation);
    public sealed record Check(string Title, string Outcome, string? Detail);
    public sealed record Document(string Schema, string ReportId, string Kind, string Title, Origin Origin, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
        DateTimeOffset? SharedAt, Redaction Redaction, Summary Summary, IReadOnlyList<string> Scope, IReadOnlyList<ReportFinding> Findings,
        IReadOnlyList<Check> Checks, IReadOnlyList<string> Limitations, IReadOnlyList<string> NotTested);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static Document Build(ImpactorSession s, string appVersion, string installationId, DateTimeOffset now)
    {
        var findings = new List<ReportFinding>();
        var checks = new List<Check>();
        void AddFindings(IEnumerable<Finding> list, string basis)
        {
            foreach (var f in list.Where(f => f.Severity != FindingSeverity.Info))
                findings.Add(new ReportFinding(Cap(f.Title), f.Severity == FindingSeverity.Blocker ? "high" : "low", "high", basis, f.Subject,
                    [InstallDiagnostics.Redact(f.Detail)], null, f.Remedy));
        }
        if (s.Inspection is { } ipa)
        {
            checks.Add(new Check("IPA inspection", ipa.Findings.HasBlockers() ? "failed" : "passed",
                $"{ipa.App?.Name} {ipa.App?.ShortVersion} ({ipa.App?.BuildNumber}) · {ipa.App?.BundleIdentifier} · {ipa.AllBundles.Count()} bundle(s)"));
            AddFindings(ipa.Findings, "observed");
        }
        if (s.Plan is { } plan)
        {
            checks.Add(new Check("Signing plan", plan.IsReady ? "passed" : "failed",
                plan.Identity is null ? "No identity" : $"{plan.Identity.CommonName} · {plan.Method} · {plan.Assignments.Count(a => a.Profile is not null)} profile(s)"));
            AddFindings(plan.Findings, "observed");
        }
        if (s.Signing is { } sign)
        {
            checks.Add(new Check("Signing", sign.Succeeded ? "passed" : "failed", sign.Succeeded ? $"SHA-256 {sign.OutputSha256}" : sign.Failure?.Title));
            if (sign.Failure is { } f) findings.Add(FromDiagnosis(f, "Signing"));
        }
        if (s.Device is { } dev)
        {
            checks.Add(new Check("Device compatibility", s.DeviceFindings.HasBlockers() ? "failed" : "passed", $"{dev.Name} · {dev.DisplayModel} · iOS {dev.OsVersion} · {dev.Connection}"));
            AddFindings(s.DeviceFindings, "observed");
        }
        if (s.Install is { } inst)
        {
            checks.Add(new Check("Installation", inst.Succeeded ? "passed" : "failed", inst.Succeeded ? $"Installed {inst.InstalledBundleIdentifier} in {inst.Duration.TotalSeconds:0}s" : inst.Failure?.Title));
            if (inst.Failure is { } f) findings.Add(FromDiagnosis(f, "Installation"));
        }
        if (s.Launch is { } l)
        {
            checks.Add(new Check("Launch", l.Succeeded ? "passed" : "failed", l.Failure?.Title));
            if (l.Failure is { } f) findings.Add(FromDiagnosis(f, "Launch"));
        }
        var highest = findings.Select(f => f.Severity).Where(x => x is not null).OrderByDescending(Rank).FirstOrDefault();
        var status = checks.Any(c => c.Outcome == "failed") ? "issues-found" : findings.Count > 0 ? "warnings" : "ok";
        var limitations = new List<string>();
        if (s.Capabilities?.Limitation is { } lim) limitations.Add(lim);
        limitations.Add("HCG checks signing against Apple's rules locally; the device makes the final decision when the app is installed and launched.");
        var notTested = new List<string>();
        if (s.Install is null) notTested.Add("Installation on a device");
        if (s.Launch is null) notTested.Add("Launch on a device");
        var scope = new List<string>();
        if (s.Inspection?.App is { } app) scope.Add($"{app.Name} {app.ShortVersion} ({app.BundleIdentifier})");
        if (s.Device is { } d) scope.Add($"{d.Name} [{d.DisplayModel}, iOS {d.OsVersion}, {d.Connection}]");
        return new Document(Schema, Guid.NewGuid().ToString().ToLowerInvariant(), "impactor.deployment-diagnostics",
            Cap("HCG Impactor — " + (s.Inspection?.App?.Name ?? "IPA") + (s.Device is null ? "" : " on " + s.Device.Name)),
            new Origin("HCG Executable Maker", appVersion, installationId, Environment.MachineName.Length > 0 ? "This Mac" : "This computer", "impactor", appVersion),
            s.Inspection?.InspectedAt ?? now, now, null, new Redaction(RedactionPolicy, true), new Summary(status, highest, findings.Count),
            scope, findings, checks, limitations, notTested);
    }

    public static string ToJson(Document d) => JsonSerializer.Serialize(d, Json);

    public static string ToMarkdown(ImpactorSession s, Document d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {d.Title}").AppendLine();
        sb.AppendLine($"Report `{d.ReportId}` · {d.Schema} · redaction {d.Redaction.Policy} · {d.CreatedAt:yyyy-MM-dd HH:mm} UTC · status **{d.Summary.Status}**").AppendLine();
        if (s.Inspection is { } ipa && ipa.App is { } app)
        {
            sb.AppendLine("## IPA").AppendLine();
            sb.AppendLine($"| | |\n|---|---|\n| App | {app.Name} |\n| Version | {app.ShortVersion} ({app.BuildNumber}) |\n| Bundle identifier | {app.BundleIdentifier} |");
            sb.AppendLine($"| Minimum iOS | {app.MinimumOSVersion} |\n| Architectures | {string.Join(", ", app.Architectures)} |\n| Devices | {ipa.Families.Display()} |");
            sb.AppendLine($"| Signed by | {(app.Signature.Present ? app.Signature.AdHoc ? "ad hoc" : app.Signature.Signer?.CommonName : "unsigned")} |");
            sb.AppendLine($"| Embedded profile | {app.EmbeddedProfile?.Profile?.Name ?? "none"} |\n| SHA-256 | `{ipa.Sha256}` |").AppendLine();
            foreach (var b in ipa.Nested) sb.AppendLine($"- {b.Kind}: `{b.RelativePath}` {b.BundleIdentifier}");
            sb.AppendLine();
        }
        if (s.Plan is { } plan)
        {
            sb.AppendLine("## Signing plan").AppendLine();
            sb.AppendLine($"Identity: {plan.Identity?.CommonName} ({plan.Identity?.ShortSha1}) · method {plan.Method} · expires {plan.ExpiresAt:yyyy-MM-dd}").AppendLine();
            foreach (var a in plan.Assignments.Where(a => a.Profile is not null))
                sb.AppendLine($"- {a.Label}: {a.FinalBundleIdentifier} · profile \"{a.Profile!.Info.Name}\" · entitlements: {string.Join(", ", a.Entitlements?.Keys ?? [])}");
            sb.AppendLine();
        }
        sb.AppendLine("## Checks").AppendLine();
        foreach (var c in d.Checks) sb.AppendLine($"- **{c.Outcome}** — {c.Title}: {c.Detail}");
        sb.AppendLine().AppendLine("## Findings").AppendLine();
        if (d.Findings.Count == 0) sb.AppendLine("None.");
        foreach (var f in d.Findings) sb.AppendLine($"- [{f.Severity}] {f.Title}{(f.Location is null ? "" : $" ({f.Location})")}: {string.Join(" ", f.Evidence)}{(f.Remediation is null ? "" : $" → {f.Remediation}")}");
        void Raw(string title, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            sb.AppendLine().AppendLine($"## {title} (original output, redacted)").AppendLine().AppendLine("```").AppendLine(text.Length > 60_000 ? text[..60_000] + "\n… (truncated)" : text).AppendLine("```");
        }
        Raw("Signing", s.Signing is { } sg ? string.Join("\n", sg.Log) + "\n" + (sg.Failure?.RawRedacted ?? sg.Verification) : null);
        Raw("Installation", s.Install?.RawRedacted);
        Raw("Launch", s.Launch?.RawRedacted);
        sb.AppendLine().AppendLine("## Limitations").AppendLine();
        foreach (var l in d.Limitations) sb.AppendLine("- " + l);
        return InstallDiagnostics.Redact(sb.ToString());
    }

    /// <summary>Writes report.json and report.md into a new folder under <paramref name="parent"/>; returns the folder.</summary>
    public static string Export(ImpactorSession s, string parent, string appVersion, string installationId, DateTimeOffset now)
    {
        var d = Build(s, appVersion, installationId, now);
        var dir = Path.Combine(parent, $"hcg-impactor-{now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "report.json"), InstallDiagnostics.Redact(ToJson(d)));
        File.WriteAllText(Path.Combine(dir, "report.md"), ToMarkdown(s, d));
        return dir;
    }

    private static ReportFinding FromDiagnosis(Diagnosis f, string step) =>
        new(Cap($"{step}: {f.Title}"), f.Kind is DiagnosisKind.Cancelled ? "low" : "high", f.Kind == DiagnosisKind.Unknown ? "low" : "high", "observed", null,
            f.Evidence is null ? [] : [f.Evidence], f.Explanation, f.Remedy);

    private static int Rank(string? s) => s switch { "critical" => 4, "high" => 3, "medium" => 2, "low" => 1, _ => 0 };
    private static string Cap(string s) => s.Length > 200 ? s[..199] + "…" : s;
}
