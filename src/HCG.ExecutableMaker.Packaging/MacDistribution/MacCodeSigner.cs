using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>What <c>codesign -d --verbose=4</c> reports about a signature.</summary>
public sealed record SignatureInfo(
    IReadOnlyList<string> Authorities,
    string? TeamId,
    string? Identifier,
    bool HardenedRuntime,
    bool SecureTimestamp,
    bool AdHoc)
{
    public string? Leaf => Authorities.FirstOrDefault();
}

/// <summary>
/// Signs and verifies macOS code with real certificates. Every call is an argument list (no shell); identities are
/// passed by SHA-1 so an ambiguous name can never pick the wrong certificate. HCG never reads private keys.
/// </summary>
public sealed class MacCodeSigner(IProcessRunner runner, string codesign = "/usr/bin/codesign")
{
    private static readonly TimeSpan SignTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Developer ID: hardened runtime, secure timestamp, confirmed entitlements (an empty set is valid).</summary>
    public Task SignDeveloperIdAsync(string path, string identitySha1, IReadOnlyList<string> entitlements, IPackagingLog log, CancellationToken ct) =>
        SignAsync(path, identitySha1, Entitlements.ToPlist(entitlements), runtime: true, log, ct);

    /// <summary>Mac App Store: Apple Distribution identity, sandbox entitlements including the application identifier.</summary>
    public Task SignAppStoreAsync(string path, string identitySha1, string entitlementsPlist, IPackagingLog log, CancellationToken ct) =>
        SignAsync(path, identitySha1, entitlementsPlist, runtime: false, log, ct);

    /// <summary>Signs a flat file (a DMG) with a Developer ID Application identity and a secure timestamp.</summary>
    public async Task SignFileAsync(string path, string identitySha1, IPackagingLog log, CancellationToken ct)
    {
        log.Info($"codesign --force --timestamp --sign {identitySha1} {Path.GetFileName(path)}");
        var r = await runner.RunAsync(new ProcessSpec { Executable = codesign, Arguments = ["--force", "--timestamp", "--sign", identitySha1, path], Timeout = SignTimeout },
            (_, l) => log.Info(l), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!r.Succeeded) throw new InvalidOperationException($"codesign could not sign {Path.GetFileName(path)} (exit code {r.ExitCode}). See the log above for codesign's reason.");
    }

    private async Task SignAsync(string path, string identitySha1, string entitlementsPlist, bool runtime, IPackagingLog log, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hcg-entitlements-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "entitlements.plist");
        await File.WriteAllTextAsync(file, entitlementsPlist, ct).ConfigureAwait(false);
        try
        {
            List<string> args = ["--force", "--timestamp"];
            if (runtime) args.AddRange(["--options", "runtime"]);
            args.AddRange(["--entitlements", file, "--sign", identitySha1, path]);
            log.Info("codesign " + string.Join(' ', args.Select(a => a == file ? "<entitlements.plist>" : a)));
            var r = await runner.RunAsync(new ProcessSpec { Executable = codesign, Arguments = args, Timeout = SignTimeout }, (_, l) => log.Info(l), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!r.Succeeded)
                throw new InvalidOperationException($"codesign failed with exit code {r.ExitCode}. Common causes: the certificate's private key is not in the login keychain, the keychain is locked, or Apple's timestamp server could not be reached. See the log above for codesign's own message.");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<(bool Ok, string Detail)> VerifyStrictAsync(string path, CancellationToken ct)
    {
        var (r, o, e) = await runner.CaptureAsync(new ProcessSpec { Executable = codesign, Arguments = ["--verify", "--strict", "--deep", "--verbose=2", path], Timeout = SignTimeout }, ct).ConfigureAwait(false);
        return (r.Succeeded, LastLines(o + e, 3));
    }

    public async Task<SignatureInfo?> DescribeAsync(string path, CancellationToken ct)
    {
        var (r, o, e) = await runner.CaptureAsync(new ProcessSpec { Executable = codesign, Arguments = ["-d", "--verbose=4", path], Timeout = SignTimeout }, ct).ConfigureAwait(false);
        return r.Succeeded ? ParseSignatureInfo(o + "\n" + e) : null;
    }

    /// <summary>The signed entitlements (key → value) of a bundle or binary; empty when it has none.</summary>
    public async Task<Dictionary<string, object>> ReadEntitlementsAsync(string path, CancellationToken ct)
    {
        var (r, o, _) = await runner.CaptureAsync(new ProcessSpec { Executable = codesign, Arguments = ["-d", "--entitlements", "-", "--xml", path], Timeout = SignTimeout }, ct).ConfigureAwait(false);
        var xml = o.Trim();
        if (!r.Succeeded || xml.Length == 0 || !xml.Contains("<plist", StringComparison.Ordinal)) return new(StringComparer.Ordinal);
        try { return PlistReader.ParseDictionary(System.Text.Encoding.UTF8.GetBytes(xml[xml.IndexOf('<')..])); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { return new(StringComparer.Ordinal); }
    }

    public static SignatureInfo ParseSignatureInfo(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? Value(string key) => lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        var team = Value("TeamIdentifier");
        var flags = lines.FirstOrDefault(l => l.StartsWith("CodeDirectory ", StringComparison.Ordinal)) ?? "";
        return new SignatureInfo(
            lines.Where(l => l.StartsWith("Authority=", StringComparison.Ordinal)).Select(l => l[10..]).ToList(),
            team is null or "not set" ? null : team,
            Value("Identifier"),
            flags.Contains("(runtime)", StringComparison.Ordinal) || flags.Contains(",runtime", StringComparison.Ordinal),
            Value("Timestamp") is not null,
            Value("Signature") == "adhoc");
    }

    /// <summary>
    /// Independent checks of a Developer ID signature: integrity, certificate type and team, hardened runtime, secure
    /// timestamp, no get-task-allow, and exactly the confirmed entitlements.
    /// </summary>
    public async Task<IReadOnlyList<VerificationCheck>> VerifyDeveloperIdAsync(string bundle, SigningIdentity expected, IReadOnlyList<string> confirmed, CancellationToken ct)
    {
        var checks = new List<VerificationCheck>();
        var (ok, detail) = await VerifyStrictAsync(bundle, ct).ConfigureAwait(false);
        checks.Add(ok
            ? new VerificationCheck("Code signature", VerificationStatus.Passed, "codesign --verify --strict --deep passed.")
            : new VerificationCheck("Code signature", VerificationStatus.Failed, "codesign --verify --strict --deep failed: " + detail));
        var info = await DescribeAsync(bundle, ct).ConfigureAwait(false);
        if (info is null)
        {
            checks.Add(new VerificationCheck("Signing certificate", VerificationStatus.Failed, "codesign -d could not read the signature."));
            return checks;
        }
        checks.Add(info.Leaf?.StartsWith("Developer ID Application:", StringComparison.Ordinal) == true && (expected.TeamId is null || info.TeamId == expected.TeamId)
            ? new VerificationCheck("Signing certificate", VerificationStatus.Passed, $"{info.Leaf} (team {info.TeamId})")
            : new VerificationCheck("Signing certificate", VerificationStatus.Failed, $"Expected a Developer ID Application certificate of team {expected.TeamId}; the signature is from {info.Leaf ?? "an ad-hoc signature"} (team {info.TeamId ?? "none"})."));
        checks.Add(info.HardenedRuntime
            ? new VerificationCheck("Hardened runtime", VerificationStatus.Passed, "Enabled (flags include runtime).")
            : new VerificationCheck("Hardened runtime", VerificationStatus.Failed, "Not enabled: the notary service rejects apps without the hardened runtime."));
        checks.Add(info.SecureTimestamp
            ? new VerificationCheck("Secure timestamp", VerificationStatus.Passed, "Signed with a secure timestamp from Apple.")
            : new VerificationCheck("Secure timestamp", VerificationStatus.Failed, "No secure timestamp: the notary service rejects it."));
        checks.Add(await EntitlementsCheckAsync(bundle, confirmed, requireSandbox: false, ct).ConfigureAwait(false));
        return checks;
    }

    public async Task<VerificationCheck> EntitlementsCheckAsync(string bundle, IReadOnlyList<string> confirmedBooleans, bool requireSandbox, CancellationToken ct)
    {
        var signed = await ReadEntitlementsAsync(bundle, ct).ConfigureAwait(false);
        if (signed.GetValueOrDefault(Entitlements.GetTaskAllow) is true)
            return new VerificationCheck("Entitlements", VerificationStatus.Failed, "com.apple.security.get-task-allow is true (a debugging entitlement): distribution signatures must not include it.");
        if (requireSandbox && signed.GetValueOrDefault(Entitlements.AppSandbox) is not true)
            return new VerificationCheck("Entitlements", VerificationStatus.Failed, "com.apple.security.app-sandbox is missing: Mac App Store apps must be sandboxed.");
        var booleans = signed.Where(kv => kv.Value is true).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var expected = confirmedBooleans.ToHashSet(StringComparer.Ordinal);
        if (requireSandbox) expected.Add(Entitlements.AppSandbox);
        var extra = booleans.Except(expected).Order().ToList();
        var missing = expected.Except(booleans).Order().ToList();
        return extra.Count == 0 && missing.Count == 0
            ? new VerificationCheck("Entitlements", VerificationStatus.Passed, expected.Count == 0 ? "None (none were needed)." : string.Join(", ", expected.Order()))
            : new VerificationCheck("Entitlements", VerificationStatus.Failed,
                $"Signed entitlements differ from the confirmed ones.{(extra.Count > 0 ? " Unexpected: " + string.Join(", ", extra) + "." : "")}{(missing.Count > 0 ? " Missing: " + string.Join(", ", missing) + "." : "")}");
    }

    /// <summary>Gatekeeper's verdict (spctl). Before notarization a Developer ID app is expected to be "Unnotarized".</summary>
    public async Task<VerificationCheck> GatekeeperAsync(string path, string type, CancellationToken ct, bool expectNotarized = false)
    {
        List<string> args = ["--assess", "--type", type, "-vv"];
        if (type == "open") args.AddRange(["--context", "context:primary-signature"]);
        args.Add(path);
        var (r, o, e) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/sbin/spctl", Arguments = args, Timeout = TimeSpan.FromMinutes(2) }, ct).ConfigureAwait(false);
        var text = (o + e).Trim();
        var source = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("source=", StringComparison.Ordinal)) ?? LastLines(text, 1);
        if (r.Succeeded) return new VerificationCheck("Gatekeeper", VerificationStatus.Passed, $"spctl accepted ({source}).");
        return !expectNotarized && text.Contains("Unnotarized", StringComparison.OrdinalIgnoreCase)
            ? new VerificationCheck("Gatekeeper", VerificationStatus.PassedWithWarnings, $"spctl: {source}. Expected before notarization: run Notarize (an explicit step) to get Gatekeeper acceptance on other Macs.")
            : new VerificationCheck("Gatekeeper", VerificationStatus.Failed, $"spctl rejected it: {source}");
    }

    public static string LastLines(string text, int n) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));
}
