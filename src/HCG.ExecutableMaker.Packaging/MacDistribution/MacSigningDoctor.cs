using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Packagers;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>
/// Signing Doctor for a built native macOS app: what it is signed with and whether macOS will run it, read from the app
/// itself. It reports:
/// <list type="bullet">
/// <item>bundle identifier, signing identity and team, the signature kind;</item>
/// <item>entitlements, App Sandbox, hardened runtime and secure timestamp;</item>
/// <item>every embedded framework, helper, plug-in and XPC service with its own signer, and team mismatches;</item>
/// <item>the strict codesign verification, Gatekeeper's verdict and the notarization ticket.</item>
/// </list>
/// Read-only: it never signs, notarizes, staples or changes the project or the app. Signing and notarization are separate
/// steps that need the person's explicit approval.
/// </summary>
public sealed class MacSigningDoctor(IProcessRunner runner, string codesign = "/usr/bin/codesign")
{
    public enum Kind { Unsigned, AdHoc, Development, DeveloperId, AppStore, Other }

    public sealed record NestedCode(string Path, Kind Kind, string? Authority, string? TeamId, bool HardenedRuntime, bool Valid, string Detail);

    public sealed record Report
    {
        public required string App { get; init; }
        public string? BundleIdentifier { get; init; }
        public string? SigningIdentifier { get; init; }
        public Kind SignatureKind { get; init; }
        public string? Authority { get; init; }
        public IReadOnlyList<string> AuthorityChain { get; init; } = [];
        public string? TeamId { get; init; }
        public bool HardenedRuntime { get; init; }
        public bool SecureTimestamp { get; init; }
        public IReadOnlyDictionary<string, object> Entitlements { get; init; } = new Dictionary<string, object>();
        public bool AppSandbox => Entitlements.TryGetValue("com.apple.security.app-sandbox", out var v) && v is true;
        public IReadOnlyList<NestedCode> Nested { get; init; } = [];
        public bool SignatureValid { get; init; }
        public string VerifyDetail { get; init; } = "";
        /// <summary>spctl's verdict: "accepted (Notarized Developer ID)", "rejected (Unnotarized Developer ID)", "rejected (no usable signature)", …</summary>
        public string Gatekeeper { get; init; } = "";
        /// <summary>"notarized, ticket stapled" · "no stapled ticket" · "not checked".</summary>
        public string Notarization { get; init; } = "not checked";
        public bool Notarized => Notarization.StartsWith("notarized", StringComparison.Ordinal) || Gatekeeper.Contains("Notarized Developer ID", StringComparison.Ordinal);
        public IReadOnlyList<string> Findings { get; init; } = [];

        /// <summary>One word for people and records: unsigned, ad-hoc, development, developer-id, developer-id-notarized, app-store, other.</summary>
        public string Status => SignatureKind switch
        {
            Kind.Unsigned => "unsigned",
            Kind.AdHoc => "ad-hoc",
            Kind.Development => "development",
            Kind.DeveloperId => Notarized ? "developer-id-notarized" : "developer-id",
            Kind.AppStore => "app-store",
            _ => "other",
        };
    }

    public static Kind Classify(SignatureInfo? info) => info switch
    {
        null => Kind.Unsigned,
        { AdHoc: true } => Kind.AdHoc,
        { Leaf: { } l } when l.StartsWith("Developer ID Application", StringComparison.Ordinal) => Kind.DeveloperId,
        { Leaf: { } l } when l.StartsWith("Apple Development", StringComparison.Ordinal) || l.StartsWith("Mac Developer", StringComparison.Ordinal) => Kind.Development,
        { Leaf: { } l } when l.StartsWith("Apple Distribution", StringComparison.Ordinal) || l.StartsWith("3rd Party Mac Developer Application", StringComparison.Ordinal) || l.StartsWith("Apple Mac OS Application Signing", StringComparison.Ordinal) => Kind.AppStore,
        _ => Kind.Other,
    };

    public static string Describe(Kind k) => k switch
    {
        Kind.Unsigned => "not signed",
        Kind.AdHoc => "ad hoc (no certificate, no team: runs on the Mac that built it; Gatekeeper blocks it elsewhere)",
        Kind.Development => "development (Apple Development: Macs registered to the team; not for distribution)",
        Kind.DeveloperId => "Developer ID (distribution outside the Mac App Store)",
        Kind.AppStore => "Mac App Store distribution",
        _ => "signed by another authority",
    };

    /// <summary>Code inside a macOS app that carries its own signature: frameworks, dylibs, helpers, plug-ins, XPC services, login items.</summary>
    public static IReadOnlyList<string> NestedCodePaths(string app)
    {
        var contents = Path.Combine(app, "Contents");
        var found = new List<string>();
        void Dirs(string sub, string pattern)
        {
            var d = Path.Combine(contents, sub);
            if (Directory.Exists(d)) found.AddRange(Directory.EnumerateDirectories(d, pattern, SearchOption.TopDirectoryOnly));
        }
        Dirs("Frameworks", "*.framework");
        Dirs("PlugIns", "*.appex");
        Dirs("PlugIns", "*.plugin");
        Dirs("XPCServices", "*.xpc");
        Dirs("Library/LoginItems", "*.app");
        Dirs("Helpers", "*.app");
        Dirs("Library/SystemExtensions", "*.systemextension");
        foreach (var sub in new[] { "Frameworks", "Helpers" })
        {
            var d = Path.Combine(contents, sub);
            if (Directory.Exists(d)) found.AddRange(Directory.EnumerateFiles(d, "*.dylib", SearchOption.TopDirectoryOnly));
        }
        var macos = Path.Combine(contents, "MacOS");
        if (Directory.Exists(macos))
        {
            var main = InfoPlist.Read(Path.Combine(contents, "Info.plist")).GetValueOrDefault("CFBundleExecutable");
            found.AddRange(Directory.EnumerateFiles(macos).Where(f => Path.GetFileName(f) != main && new FileInfo(f).LinkTarget is null));
        }
        return found.Select(f => Path.GetRelativePath(app, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
    }

    public async Task<Report> InspectAsync(string app, CancellationToken ct)
    {
        var signer = new MacCodeSigner(runner, codesign);
        var findings = new List<string>();
        var plist = InfoPlist.Read(Path.Combine(app, "Contents", "Info.plist"));
        var info = await signer.DescribeAsync(app, ct).ConfigureAwait(false);
        var kind = Classify(info);
        var (valid, verify) = info is null ? (false, "The app carries no code signature.") : await signer.VerifyStrictAsync(app, ct).ConfigureAwait(false);
        var entitlements = info is null ? new Dictionary<string, object>() : await signer.ReadEntitlementsAsync(app, ct).ConfigureAwait(false);

        var nested = new List<NestedCode>();
        foreach (var rel in NestedCodePaths(app))
        {
            var path = Path.Combine(app, rel);
            var n = await signer.DescribeAsync(path, ct).ConfigureAwait(false);
            var nk = Classify(n);
            var (nv, nd) = n is null ? (false, "not signed") : await signer.VerifyStrictAsync(path, ct).ConfigureAwait(false);
            nested.Add(new NestedCode(rel, nk, n?.Leaf, n?.TeamId, n?.HardenedRuntime == true, nv, nd));
            if (n is null) findings.Add($"{rel} is not signed: codesign --verify --deep fails for the whole app, and macOS refuses to load it under the hardened runtime.");
            else if (info?.TeamId is { } team && n.TeamId is { } nt && nt != team && !n.AdHoc)
                findings.Add($"{rel} is signed by team {nt}, the app by team {team}: with library validation (hardened runtime) dyld refuses to load it unless the app has com.apple.security.cs.disable-library-validation.");
            else if (kind is Kind.DeveloperId && nk is Kind.AdHoc or Kind.Development)
                findings.Add($"{rel} is signed {Describe(nk)} inside a Developer ID app: notarization rejects it. Re-sign it with the same Developer ID identity (Xcode does this with \"Code Sign On Copy\").");
        }

        if (info is not null && !valid) findings.Add("codesign --verify --deep --strict fails: " + verify);
        if (kind is Kind.DeveloperId && !info!.HardenedRuntime) findings.Add("Developer ID without the hardened runtime: notarization requires ENABLE_HARDENED_RUNTIME = YES.");
        if (kind is Kind.DeveloperId && !info!.SecureTimestamp) findings.Add("Developer ID without a secure timestamp: notarization requires codesign --timestamp (Xcode adds it for Release archives).");
        if (entitlements.ContainsKey("com.apple.security.get-task-allow"))
            findings.Add("com.apple.security.get-task-allow is set (a debug entitlement): notarization rejects it. Build the Release configuration or archive.");
        foreach (var key in entitlements.Keys.Where(k => k.StartsWith("com.apple.security.cs.", StringComparison.Ordinal)))
            findings.Add($"Hardened-runtime exception {key} is granted; keep it only if the app needs it.");

        var gate = await RunAsync("/usr/sbin/spctl", ["-a", "-vv", "-t", "exec", app], ct).ConfigureAwait(false);
        var staple = await RunAsync("/usr/bin/xcrun", ["stapler", "validate", app], ct).ConfigureAwait(false);
        return new Report
        {
            App = app,
            BundleIdentifier = plist.GetValueOrDefault("CFBundleIdentifier"),
            SigningIdentifier = info?.Identifier,
            SignatureKind = kind,
            Authority = info?.Leaf,
            AuthorityChain = info?.Authorities ?? [],
            TeamId = info?.TeamId,
            HardenedRuntime = info?.HardenedRuntime == true,
            SecureTimestamp = info?.SecureTimestamp == true,
            Entitlements = entitlements,
            Nested = nested,
            SignatureValid = valid,
            VerifyDetail = verify,
            Gatekeeper = ParseGatekeeper(gate.Ok, gate.Text),
            Notarization = ParseStapler(staple.Ok, staple.Text),
            Findings = findings,
        };
    }

    /// <summary>spctl -a -vv output → "accepted (Notarized Developer ID)" / "rejected (Unnotarized Developer ID)" / "rejected (…)".</summary>
    public static string ParseGatekeeper(bool accepted, string text)
    {
        var source = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("source=", StringComparison.Ordinal))?["source=".Length..];
        var reason = source ?? text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("rejected", StringComparison.Ordinal) || l.Contains("no usable signature", StringComparison.Ordinal))
                     ?? text.Trim();
        return $"{(accepted ? "accepted" : "rejected")} ({(reason.Length == 0 ? "no detail" : reason)})";
    }

    /// <summary>xcrun stapler validate output → "notarized, ticket stapled" or why not.</summary>
    public static string ParseStapler(bool ok, string text) =>
        ok && text.Contains("worked", StringComparison.OrdinalIgnoreCase) ? "notarized, ticket stapled"
        : text.Contains("does not have a ticket stapled", StringComparison.OrdinalIgnoreCase) ? "no stapled notarization ticket"
        : text.Trim().Length == 0 ? "not checked" : "no stapled notarization ticket (" + text.Trim().Split('\n')[^1] + ")";

    private async Task<(bool Ok, string Text)> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!File.Exists(exe)) return (false, "");
        var (r, o, e) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = args, Timeout = TimeSpan.FromMinutes(2) }, ct).ConfigureAwait(false);
        return (r.Succeeded, (o + "\n" + e).Trim());
    }

    /// <summary>The report as text: what a person reads before signing, notarizing or shipping.</summary>
    public static string ToText(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Signing Doctor — {Path.GetFileName(r.App)}");
        sb.AppendLine($"  Bundle identifier   {r.BundleIdentifier ?? "(none)"}{(r.SigningIdentifier is { } si && si != r.BundleIdentifier ? $" (signature identifier {si})" : "")}");
        sb.AppendLine($"  Signature           {r.Status} — {Describe(r.SignatureKind)}");
        if (r.Authority is not null) sb.AppendLine($"  Signing identity    {r.Authority}{(r.AuthorityChain.Count > 1 ? $" (chain: {string.Join(" → ", r.AuthorityChain)})" : "")}");
        sb.AppendLine($"  Developer team      {r.TeamId ?? "none"}");
        sb.AppendLine($"  Hardened runtime    {(r.HardenedRuntime ? "on" : "off")} · secure timestamp {(r.SecureTimestamp ? "yes" : "no")}");
        sb.AppendLine($"  App Sandbox         {(r.AppSandbox ? "on" : "off")}");
        sb.AppendLine($"  Entitlements        {(r.Entitlements.Count == 0 ? "none" : string.Join(", ", r.Entitlements.Select(kv => $"{kv.Key}={Value(kv.Value)}")))}");
        sb.AppendLine($"  Verification        {(r.SignatureValid ? "codesign --verify --deep --strict passes" : "FAILS: " + r.VerifyDetail)}");
        sb.AppendLine($"  Gatekeeper          {r.Gatekeeper}");
        sb.AppendLine($"  Notarization        {r.Notarization}");
        sb.AppendLine($"  Embedded code       {(r.Nested.Count == 0 ? "none" : r.Nested.Count + " item(s)")}");
        foreach (var n in r.Nested) sb.AppendLine($"    {n.Path}: {Describe(n.Kind)}{(n.TeamId is { } t ? $", team {t}" : "")}{(n.Valid ? "" : " — " + n.Detail)}");
        if (r.Findings.Count > 0)
        {
            sb.AppendLine("  Findings:");
            foreach (var f in r.Findings) sb.AppendLine("    - " + f);
        }
        sb.AppendLine("  Nothing was signed, notarized or changed. Signing and notarization are separate steps that need your approval.");
        return sb.ToString();
    }

    private static string Value(object v) => v switch
    {
        bool b => b ? "true" : "false",
        IEnumerable<object> list => "[" + string.Join(", ", list.Select(Value)) + "]",
        _ => v.ToString() ?? "",
    };
}
