using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.Account.Support;

/// <summary>One file the user could send, and why it is or is not included by default.</summary>
public sealed class PlannedFile
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required long SizeBytes { get; init; }
    /// <summary>Why the file is excluded by default (null = included by default).</summary>
    public string? ExclusionReason { get; init; }
    /// <summary>Likely secrets found by the scan (kinds and line numbers). A flagged file is excluded by default.</summary>
    public IReadOnlyList<SecretRedactor.Finding> SecretFindings { get; init; } = [];
    /// <summary>Credentials/signing material: can never be included, even by choice.</summary>
    public bool NeverSend { get; init; }
    public bool Included { get; set; }
    public bool IsFlagged => SecretFindings.Count > 0;
    public string Explanation => NeverSend
        ? ExclusionReason ?? "Never sent"
        : IsFlagged
            ? "May contain secrets: " + string.Join(", ", SecretFindings.Take(4).Select(f => $"{f.Kind} (line {f.Line})")) + (SecretFindings.Count > 4 ? $" and {SecretFindings.Count - 4} more" : "")
            : ExclusionReason ?? "Included";
}

public sealed record AttachmentPlan(string Root, IReadOnlyList<PlannedFile> Files, IReadOnlyList<string> SkippedDirectories, bool Truncated)
{
    public IEnumerable<PlannedFile> Included => Files.Where(f => f.Included);
    public long IncludedBytes => Included.Sum(f => f.SizeBytes);
}

/// <summary>
/// Plans which project files a support request may carry. Excluded by default: credentials and signing material (never
/// sendable), dependency folders, build outputs, VCS metadata, very large and binary data files, and any text file the
/// secret scan flags. The scan is pattern-based: it finds common credential formats and never guarantees that no secret
/// remains, which is why the user reviews the list before anything is sent. Symbolic links are never followed or sent.
/// </summary>
public static class AttachmentPlanner
{
    public const long MaxScannedFileBytes = 2 * 1024 * 1024;
    public const long LargeFileBytes = 20 * 1024 * 1024;
    public const int MaxFiles = 20_000;

    /// <summary>Dependency, build-output, cache and VCS folders: excluded (not listed file by file).</summary>
    public static readonly IReadOnlySet<string> SkippedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bower_components", "jspm_packages", "vendor", "Pods", "Carthage", ".gradle", "build",
        "dist", "out", "release", "bin", "obj", "target", "DerivedData", ".build", ".swiftpm", ".venv", "venv", "env", "__pycache__",
        ".idea", ".vs", ".vscode", ".next", ".nuxt", ".output", ".svelte-kit", "coverage", "packages", "TestResults", ".terraform",
        ".dart_tool", ".pub-cache", ".cxx", ".externalNativeBuild", "xcuserdata", ".turbo", ".cache", "desktop-dist", "dist-electron",
    };

    private static readonly string[] SecretExtensions =
    [
        ".pem", ".key", ".p12", ".pfx", ".keystore", ".jks", ".mobileprovision", ".provisionprofile", ".p8", ".ppk", ".kdbx",
        ".gpg", ".asc", ".cer", ".crt", ".der", ".csr", ".keychain", ".keychain-db",
    ];

    private static readonly string[] SecretFileNames =
    [
        ".npmrc", ".pypirc", ".netrc", ".git-credentials", ".dockercfg", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
        "local.properties", "keystore.properties", "signing.properties", "google-services.json", "GoogleService-Info.plist",
        "credentials", "credentials.json", "secrets.json", "secrets.yaml", "secrets.yml", "service-account.json", ".htpasswd",
        "hcg-connect.json",
    ];

    private static readonly string[] DataExtensions = [".sqlite", ".sqlite3", ".db", ".mdb", ".realm", ".log", ".dmp", ".hprof"];

    public static AttachmentPlan Plan(string root, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        var files = new List<PlannedFile>();
        var skipped = new List<string>();
        var truncated = false;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                var rel = Path.GetRelativePath(root, e.FullName).Replace('\\', '/');
                if (e.LinkTarget is not null || e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    files.Add(new PlannedFile { RelativePath = rel, FullPath = e.FullName, SizeBytes = 0, ExclusionReason = "Symbolic link (never followed or sent)", NeverSend = true });
                    continue;
                }
                if (e is DirectoryInfo)
                {
                    if (SkippedDirectoryNames.Contains(e.Name)) skipped.Add(rel + "/");
                    else stack.Push(e.FullName);
                    continue;
                }
                var f = (FileInfo)e;
                files.Add(Classify(rel, f));
                if (files.Count >= MaxFiles) { truncated = true; break; }
            }
        }
        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return new AttachmentPlan(root, files, skipped, truncated);
    }

    public static PlannedFile Classify(string rel, FileInfo f)
    {
        var name = f.Name;
        var ext = f.Extension.ToLowerInvariant();
        string? never = null;
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".example", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)))
            never = "Environment file (.env): usually holds credentials";
        else if (SecretExtensions.Contains(ext))
            never = "Key, certificate or signing material";
        else if (SecretFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            never = "Credentials or signing configuration file";
        if (never is not null)
            return new PlannedFile { RelativePath = rel, FullPath = f.FullName, SizeBytes = f.Length, ExclusionReason = never, NeverSend = true };

        string? reason = null;
        if (DataExtensions.Contains(ext)) reason = "Database, log or dump file (may hold personal data)";
        else if (f.Length > LargeFileBytes) reason = $"Large file ({f.Length / (1024 * 1024)} MB)";

        IReadOnlyList<SecretRedactor.Finding> findings = [];
        if (f.Length <= MaxScannedFileBytes && reason is null)
        {
            try
            {
                var bytes = File.ReadAllBytes(f.FullName);
                if (!LooksBinary(bytes)) findings = SecretRedactor.Scan(Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reason = "Could not be read"; }
        }
        var file = new PlannedFile { RelativePath = rel, FullPath = f.FullName, SizeBytes = f.Length, ExclusionReason = reason, SecretFindings = findings };
        file.Included = reason is null && findings.Count == 0;
        return file;
    }

    private static bool LooksBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0);

    public sealed record Archive(string Path, long SizeBytes, string Sha256, int Entries);

    /// <summary>
    /// Writes the included files to a ZIP (entries under <c>project/</c>, sorted, regular files only) in
    /// <paramref name="outputDirectory"/>. The original project is never modified.
    /// </summary>
    public static Archive BuildArchive(AttachmentPlan plan, string outputDirectory, string fileName, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = System.IO.Path.Combine(outputDirectory, fileName);
        if (File.Exists(path)) File.Delete(path);
        var count = 0;
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var file in plan.Included.Where(f => !f.NeverSend))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file.FullPath);
                if (info.LinkTarget is not null || !info.Exists) continue; // changed since planning: never follow
                var entry = zip.CreateEntry("project/" + file.RelativePath, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = File.OpenRead(file.FullPath);
                using var output = entry.Open();
                input.CopyTo(output);
                count++;
            }
        }
        var info2 = new FileInfo(path);
        string sha;
        using (var s = File.OpenRead(path)) sha = Convert.ToHexStringLower(SHA256.HashData(s));
        return new Archive(path, info2.Length, sha, count);
    }
}
