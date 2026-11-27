using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.Packaging.Windows;

/// <summary>
/// Signing Doctor for Windows: the Authenticode state of a PE file, or of every program inside a portable ZIP or
/// folder. It is read the same way on every OS (<see cref="Authenticode"/>). On Windows, Windows' own verdict
/// (Get-AuthenticodeSignature, which asks WinVerifyTrust) is added next to it. Read only.
/// </summary>
public static class WindowsSigningDoctor
{
    public sealed record Report(string Target, IReadOnlyList<AuthenticodeReport> Files)
    {
        /// <summary>The overall state: unsigned if any program is unsigned, invalid if any signature is broken, else the weakest.</summary>
        public string Status => Files.Count == 0 ? "not-pe"
            : Files.Any(f => f.Status == "invalid") ? "invalid"
            : Files.Any(f => f.Status == "unsigned") ? (Files.All(f => f.Status == "unsigned") ? "unsigned" : "partly-signed")
            : Files.Any(f => f.Status == "signed-untrusted") ? "signed-untrusted" : "signed";

        public string StoreStatus => Status switch { "signed" => "signed", "unsigned" => "unsigned", "not-pe" => "not-checked", _ => "invalid" };
    }

    public static async Task<Report> InspectAsync(string target, IProcessRunner? runner, CancellationToken ct = default)
    {
        if (Directory.Exists(target))
            return new Report(target, await AllAsync(Programs(target), runner, ct).ConfigureAwait(false));
        if (target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var temp = Directory.CreateTempSubdirectory("hcg-authenticode-").FullName;
            try
            {
                using (var zip = ZipFile.OpenRead(target))
                    foreach (var e in zip.Entries.Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        var to = Path.GetFullPath(Path.Combine(temp, e.FullName));
                        if (!to.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;   // never outside the folder
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        e.ExtractToFile(to, overwrite: true);
                    }
                var reports = await AllAsync(Programs(temp), runner, ct).ConfigureAwait(false);
                return new Report(target, reports.Select(r => r with { Path = Path.GetRelativePath(temp, r.Path).Replace('\\', '/') }).ToList());
            }
            finally { try { Directory.Delete(temp, recursive: true); } catch (IOException) { } }
        }
        return new Report(target, [await OneAsync(target, runner, ct).ConfigureAwait(false)]);
    }

    /// <summary>The programs a person runs: every .exe (shallowest first), except the .NET runtime's createdump.exe.</summary>
    private static IEnumerable<string> Programs(string folder) =>
        Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).Equals("createdump.exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<AuthenticodeReport>> AllAsync(IEnumerable<string> files, IProcessRunner? runner, CancellationToken ct)
    {
        var list = new List<AuthenticodeReport>();
        foreach (var f in files) list.Add(await OneAsync(f, runner, ct).ConfigureAwait(false));
        return list;
    }

    private static async Task<AuthenticodeReport> OneAsync(string file, IProcessRunner? runner, CancellationToken ct)
    {
        var report = Authenticode.Inspect(file);
        if (runner is null || !OperatingSystem.IsWindows() || report.Status == "not-pe") return report;
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(ps)) return report;
        var (r, so, _) = await runner.CaptureAsync(new ProcessSpec
        {
            Executable = ps, Timeout = TimeSpan.FromMinutes(2),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", $"$s = Get-AuthenticodeSignature -LiteralPath '{file.Replace("'", "''")}'; \"$($s.Status)|$($s.StatusMessage)\""],
        }, ct).ConfigureAwait(false);
        var line = so.Trim().Split('\n').LastOrDefault()?.Trim();
        return r.ExitCode == 0 && line is { Length: > 0 } ? report with { WindowsVerdict = line.Replace('|', ':') } : report;
    }

    public static string ToText(Report r)
    {
        var sb = new StringBuilder();
        // Names only: this text goes to HCG Store as evidence, and a local path would say whose computer it was.
        static string Shown(string path) => Path.IsPathRooted(path) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : path;
        sb.AppendLine($"HCG Signing Doctor (Windows Authenticode) — {Shown(r.Target)}");
        sb.AppendLine($"Overall: {r.Status} (HCG Store: {r.StoreStatus})");
        foreach (var f in r.Files)
        {
            sb.AppendLine();
            sb.AppendLine($"{Shown(f.Path)} [{f.Machine ?? "?"}]: {f.Summary}");
            if (!f.Signed && f.Status != "invalid") continue;
            sb.AppendLine($"  digest {f.DigestAlgorithm}: {(f.DigestMatches == true ? "matches the file" : "DOES NOT MATCH the file")} · signature {(f.SignatureValid == true ? "verifies" : "DOES NOT verify")}");
            if (f.Signer is not null) sb.AppendLine($"  signer {f.Signer} · issuer {f.Issuer} · thumbprint {f.Thumbprint} · valid {f.NotBefore:yyyy-MM-dd} to {f.NotAfter:yyyy-MM-dd}");
            if (f.ChainTrusted is { } t) sb.AppendLine($"  chain: {(t ? "trusted" : "NOT trusted")} — {f.ChainDetail}");
            if (f.Timestamp is { } ts) sb.AppendLine($"  timestamp ({ts.Kind}): {ts.Time:yyyy-MM-dd HH:mm:ss}Z by {ts.Authority} — {(ts.Verified ? "verified" : ts.Problem)}");
            if (f.WindowsVerdict is { } w) sb.AppendLine($"  Windows (Get-AuthenticodeSignature): {w}");
            foreach (var p in f.Problems) sb.AppendLine($"  problem: {p}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Authenticode signing with signtool, on Windows. It never runs by itself: the caller passes the approval, which is
/// the SHA-256 of the exact file to sign (the file is refused if it changed since the approval). It uses the
/// certificate the person names by thumbprint, from their own certificate store. Maker holds no keys and creates or
/// imports no certificates. It always stamps with an RFC 3161 timestamp authority. The result is verified again
/// afterwards and reported only as Authenticode reads it.
/// </summary>
public static class AuthenticodeSigner
{
    public sealed record Request(string File, string SignTool, string Thumbprint, string TimestampUrl, string ApprovedSha256, string? Description = null);
    public sealed record Result(bool Succeeded, string Message, AuthenticodeReport? After, IReadOnlyList<string> Log);

    public static string Sha256(string file)
    {
        using var s = System.IO.File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public static string? Problem(Request q)
    {
        if (!OperatingSystem.IsWindows()) return "Authenticode signing uses signtool on Windows; nothing was signed on this computer.";
        if (!System.IO.File.Exists(q.SignTool)) return $"signtool was not found at {q.SignTool} (Windows SDK).";
        if (!System.Text.RegularExpressions.Regex.IsMatch(q.Thumbprint, "^[0-9A-Fa-f]{40}$")) return "The certificate thumbprint is the certificate's SHA-1 thumbprint: 40 hexadecimal characters.";
        if (!Uri.TryCreate(q.TimestampUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(u.UserInfo))
            return "The timestamp authority is an http(s) RFC 3161 address without credentials.";
        if (PeFile.Read(q.File) is null) return $"{q.File} is not a Windows PE file.";
        if (q.ApprovedSha256.Length < 12 || !Sha256(q.File).StartsWith(q.ApprovedSha256.ToLowerInvariant(), StringComparison.Ordinal))
            return $"The approval does not match this file: approve its SHA-256 ({Sha256(q.File)}), at least the first 12 characters.";
        return null;
    }

    public static async Task<Result> SignAsync(Request q, IProcessRunner runner, CancellationToken ct = default)
    {
        if (Problem(q) is { } problem) return new Result(false, problem, null, []);
        var log = new List<string>();
        List<string> args = ["sign", "/fd", "SHA256", "/sha1", q.Thumbprint.ToUpperInvariant(), "/tr", q.TimestampUrl, "/td", "SHA256"];
        if (q.Description is { Length: > 0 } d) args.AddRange(["/d", d]);
        args.Add(q.File);
        var r = await runner.RunAsync(new ProcessSpec { Executable = q.SignTool, Arguments = args, Timeout = TimeSpan.FromMinutes(5) },
            (_, line) => log.Add(line), ct).ConfigureAwait(false);
        var after = Authenticode.Inspect(q.File);
        if (r.ExitCode != 0) return new Result(false, $"signtool failed with exit code {r.ExitCode}: {string.Join(" | ", log.TakeLast(4))}", after, log);
        if (!after.Signed) return new Result(false, "signtool reported success, but the file does not carry an intact signature: " + after.Summary, after, log);
        if (after.Timestamp is not { Verified: true })
            return new Result(false, "The file is signed but has no verified timestamp, so the signature would lapse when the certificate expires: " + after.Summary, after, log);
        return new Result(true, after.Summary, after, log);
    }
}
