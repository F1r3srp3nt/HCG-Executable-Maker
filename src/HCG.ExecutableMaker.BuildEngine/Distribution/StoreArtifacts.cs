using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Theos;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

/// <summary>A file proposed for Ship to Store, with what HCG Executable Maker detected about it.</summary>
public sealed record StoreArtifactCandidate
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    /// <summary>windows · macos · ios · ipados · linux (null when the file is not a supported type).</summary>
    public string? Platform { get; init; }
    /// <summary>exe · msi · msix · zip · dmg · pkg · ipa · deb · rpm · appimage.</summary>
    public string? Format { get; init; }
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public IReadOnlyList<string> Architectures { get; init; } = [];
    public string? MinimumOs { get; init; }
    public StorePackage? Package { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public StoreSigningReport Signing { get; init; } = new("not-checked", null, null);
    public bool IsSupported => Platform is not null && Format is not null && Problems.Count == 0;
    public string Label => Platform is null ? "Not supported" : $"{StoreArtifacts.PlatformName(Platform)} · {Format}";
}

/// <summary>
/// Detects the platform and format of release files (Windows EXE/MSI/MSIX/ZIP, macOS app ZIP/DMG/PKG, iOS/iPadOS IPA,
/// Linux DEB/RPM/AppImage, jailbreak DEB), computes SHA-256, reads package metadata, and runs the signature and
/// notarisation checks this computer can run. It never uploads anything; <see cref="HcgStoreClient"/> does.
/// </summary>
public static class StoreArtifacts
{
    public static string PlatformName(string platform) => platform switch
    {
        "windows" => "Windows", "macos" => "macOS", "ios" => "iOS", "ipados" => "iPadOS", "linux" => "Linux", _ => platform,
    };

    public static async Task<StoreArtifactCandidate> DetectAsync(string path, CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path);
        if (Directory.Exists(path))
            return new StoreArtifactCandidate
            {
                Path = path, FileName = name,
                Problems = [name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    ? "A macOS .app is a folder: package it as a ZIP first (Ship to Store offers this on a Mac with ditto, which keeps its signature)."
                    : "Folders cannot be shipped; choose a file."],
            };
        if (!File.Exists(path)) return new StoreArtifactCandidate { Path = path, FileName = name, Problems = ["The file does not exist."] };
        var size = new FileInfo(path).Length;
        var sha = await Sha256Async(path, ct).ConfigureAwait(false);
        var c = new StoreArtifactCandidate { Path = path, FileName = name, SizeBytes = size, Sha256 = sha };
        var lower = name.ToLowerInvariant();
        var problems = new List<string>();
        var notes = new List<string>();
        byte[] head;
        await using (var s = File.OpenRead(path)) { head = new byte[Math.Min(64, size)]; await s.ReadExactlyAsync(head, ct).ConfigureAwait(false); }
        bool Magic(string ascii, int at = 0) => head.Length >= at + ascii.Length && Encoding.Latin1.GetString(head, at, ascii.Length) == ascii;
        string? platform = null, format = null;
        IReadOnlyList<string> arch = [];
        string? minOs = null;
        StorePackage? package = null;

        if (lower.EndsWith(".exe")) { (platform, format) = ("windows", "exe"); if (!Magic("MZ")) problems.Add("Not a Windows executable (no MZ header)."); }
        else if (lower.EndsWith(".msi")) { (platform, format) = ("windows", "msi"); if (!(head.Length >= 8 && Convert.ToHexStringLower(head, 0, 8) == "d0cf11e0a1b11ae1")) problems.Add("Not a Windows Installer package."); }
        else if (lower.EndsWith(".msix")) { (platform, format) = ("windows", "msix"); Zip(path, problems, z => { if (z.GetEntry("AppxManifest.xml") is null) problems.Add("An MSIX package must contain AppxManifest.xml."); }); }
        else if (lower.EndsWith(".dmg")) { (platform, format) = ("macos", "dmg"); if (!Trailer(path, "koly")) problems.Add("Not a macOS disk image (no UDIF trailer)."); }
        else if (lower.EndsWith(".pkg")) { (platform, format) = ("macos", "pkg"); if (!Magic("xar!")) problems.Add("Not a macOS installer package (no xar header)."); }
        else if (lower.EndsWith(".rpm")) { (platform, format) = ("linux", "rpm"); if (!(head.Length >= 4 && Convert.ToHexStringLower(head, 0, 4) == "edabeedb")) problems.Add("Not an RPM package."); }
        else if (lower.EndsWith(".appimage")) { (platform, format) = ("linux", "appimage"); if (!Magic("\u007fELF") || !Magic("AI\u0002", 8)) problems.Add("Not a type-2 AppImage."); }
        else if (lower.EndsWith(".ipa"))
        {
            format = "ipa";
            platform = "ios";
            Zip(path, problems, z =>
            {
                var plist = z.Entries.FirstOrDefault(e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^Payload/[^/]+\.app/Info\.plist$"));
                if (plist is null) { problems.Add("An IPA must contain Payload/<name>.app/Info.plist."); return; }
                try
                {
                    using var ms = new MemoryStream();
                    using (var es = plist.Open()) es.CopyTo(ms);
                    var info = PlistReader.ParseDictionary(ms.ToArray());
                    minOs = info.GetValueOrDefault("MinimumOSVersion") as string;
                    // UIDeviceFamily: 1 = iPhone, 2 = iPad. iPad-only builds are iPadOS releases.
                    var families = (info.GetValueOrDefault("UIDeviceFamily") as IEnumerable<object>)?.Select(o => Convert.ToInt64(o)).ToList() ?? [];
                    if (families.Count > 0 && families.All(f => f == 2)) platform = "ipados";
                    else if (families.Contains(2)) notes.Add("Universal app (iPhone and iPad): shipped as iOS; it also runs on iPadOS.");
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidCastException or OverflowException) { notes.Add("Info.plist could not be read: " + ex.Message); }
            });
        }
        else if (lower.EndsWith(".deb"))
        {
            format = "deb";
            try
            {
                var control = ReadDebControl(path);
                if (control is null) notes.Add("The control file is not gzip-compressed; HCG Executable Maker reads it with dpkg-deb when available.");
                else
                {
                    package = new StorePackage(control.GetValueOrDefault("Package") ?? "", control.GetValueOrDefault("Version") ?? "", control.GetValueOrDefault("Architecture") ?? "");
                    arch = [package.Architecture];
                }
            }
            catch (InvalidDataException ex) { problems.Add(ex.Message); }
            // iphoneos-arm / iphoneos-arm64: jailbreak packages for iOS (HCG Repo); everything else: Linux.
            platform = package?.Architecture.StartsWith("iphoneos", StringComparison.Ordinal) == true ? "ios" : "linux";
        }
        else if (lower.EndsWith(".zip"))
        {
            format = "zip";
            Zip(path, problems, z =>
            {
                var apps = z.Entries.Select(e => System.Text.RegularExpressions.Regex.Match(e.FullName, @"^([^/]+\.app)/").Groups[1].Value).Where(v => v.Length > 0).Distinct().ToList();
                platform = apps.Count > 0 ? "macos" : "windows";
                if (apps.Count > 0 && !z.Entries.Any(e => e.FullName.EndsWith(".app/Contents/Info.plist", StringComparison.Ordinal))) problems.Add("The .app bundle has no Contents/Info.plist.");
                if (apps.Count == 0 && !z.Entries.Any(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                    notes.Add("No .app or .exe inside: shipped as a Windows ZIP. Check that this is what you meant.");
            });
        }
        else problems.Add("Unsupported file type. Supported: .exe .msi .msix .zip (Windows), .zip with a .app .dmg .pkg (macOS), .ipa (iOS/iPadOS), .deb .rpm .AppImage (Linux), .deb for jailbroken iOS.");
        return c with { Platform = platform, Format = format, Architectures = arch, MinimumOs = minOs, Package = package, Problems = problems, Notes = notes };
    }

    private static void Zip(string path, List<string> problems, Action<ZipArchive> inspect)
    {
        try
        {
            using var z = ZipFile.OpenRead(path);
            foreach (var e in z.Entries)
            {
                var n = e.FullName;
                if (n.StartsWith('/') || n.Contains('\\') || n.Split('/').Contains("..") || (n.Length > 1 && n[1] == ':')) { problems.Add($"Unsafe archive entry \"{n}\"."); break; }
                if (((e.ExternalAttributes >> 16) & 0xF000) == 0xA000 && !n.Contains(".app/", StringComparison.Ordinal) && !n.Contains(".framework/", StringComparison.Ordinal))
                {
                    problems.Add($"Archive entry \"{n}\" is a symbolic link outside an app or framework bundle.");
                    break;
                }
            }
            inspect(z);
        }
        catch (InvalidDataException ex) { problems.Add("Not a valid ZIP archive: " + ex.Message); }
    }

    private static bool Trailer(string path, string ascii)
    {
        using var s = File.OpenRead(path);
        if (s.Length < 512) return false;
        s.Position = s.Length - 512;
        var b = new byte[4];
        s.ReadExactly(b);
        return Encoding.Latin1.GetString(b) == ascii;
    }

    /// <summary>Control fields of a .deb whose control.tar is gzip; null when another compression is used.</summary>
    public static IReadOnlyDictionary<string, string>? ReadDebControl(string path)
    {
        using var s = File.OpenRead(path);
        var members = TheosDebVerifier.ReadAr(s);
        var control = members.FirstOrDefault(m => m.Name.StartsWith("control.tar", StringComparison.Ordinal))
                      ?? throw new InvalidDataException("The package has no control.tar member.");
        if (control.Name != "control.tar.gz") return null;
        s.Position = control.Offset;
        var bytes = new byte[control.Size];
        s.ReadExactly(bytes);
        using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new System.Formats.Tar.TarReader(gz);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.Name is not ("./control" or "control") || entry.DataStream is null) continue;
            using var sr = new StreamReader(entry.DataStream);
            return sr.ReadToEnd().Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && !p[0].StartsWith(' '))
                .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
        }
        throw new InvalidDataException("control.tar.gz has no control file.");
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Signature and notarisation checks this computer can run, reported to the Store as the uploader's own result.
    /// macOS: codesign, spctl, stapler, pkgutil. Windows: Authenticode (Get-AuthenticodeSignature). Elsewhere and for
    /// formats without a platform tool here: "not-checked", with the reason — never "unsigned" by assumption.
    /// </summary>
    public static async Task<StoreSigningReport> CheckSigningAsync(StoreArtifactCandidate c, IProcessRunner runner, CancellationToken ct = default)
    {
        if (!c.IsSupported) return new("not-checked", null, null);
        var lines = new List<string>();
        async Task<(int Code, string Out)> Run(string exe, params string[] args)
        {
            if (!File.Exists(exe)) return (-1, $"{exe} not found");
            var (r, so, se) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = args, Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
            var text = (so + "\n" + se).Trim();
            lines.Add($"$ {System.IO.Path.GetFileName(exe)} {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))} → exit {r.ExitCode}{(text.Length > 0 ? ": " + string.Join(" | ", text.Split('\n').Take(6)) : "")}");
            return (r.ExitCode, text);
        }
        if (OperatingSystem.IsMacOS() && c.Format is "pkg" or "dmg" or "zip" or "ipa" && c.Platform is "macos" or "ios" or "ipados")
        {
            string target = c.Path;
            string? temp = null;
            try
            {
                if (c.Format is "zip" or "ipa")
                {
                    temp = Directory.CreateTempSubdirectory("hcg-ship-").FullName;
                    if ((await Run("/usr/bin/ditto", "-x", "-k", c.Path, temp)).Code != 0) return new("not-checked", string.Join("\n", lines), "ditto");
                    target = Directory.EnumerateDirectories(c.Format == "ipa" ? System.IO.Path.Combine(temp, "Payload") : temp, "*.app").FirstOrDefault() ?? temp;
                }
                if (c.Format == "pkg")
                {
                    var sig = await Run("/usr/sbin/pkgutil", "--check-signature", target);
                    if (sig.Code != 0 || sig.Out.Contains("Status: no signature", StringComparison.Ordinal)) return new("unsigned", string.Join("\n", lines), "pkgutil");
                }
                else
                {
                    var cs = await Run("/usr/bin/codesign", "--verify", "--deep", "--strict", "--verbose=2", target);
                    if (cs.Code != 0) return new(cs.Out.Contains("not signed", StringComparison.OrdinalIgnoreCase) ? "unsigned" : "invalid", string.Join("\n", lines), "codesign");
                }
                if (c.Platform != "macos") return new("signed", string.Join("\n", lines), "codesign");
                var assess = c.Format switch
                {
                    "pkg" => await Run("/usr/sbin/spctl", "--assess", "--type", "install", "--verbose", target),
                    "dmg" => await Run("/usr/sbin/spctl", "--assess", "--type", "open", "--context", "context:primary-signature", "--verbose", target),
                    _ => await Run("/usr/sbin/spctl", "--assess", "--type", "exec", "--verbose", target),
                };
                var staple = await Run("/usr/bin/xcrun", "stapler", "validate", target);
                var notarized = assess.Code == 0 && (assess.Out.Contains("Notarized", StringComparison.OrdinalIgnoreCase) || staple.Code == 0);
                return new(notarized ? "notarized" : "signed", string.Join("\n", lines), "codesign/spctl/stapler");
            }
            finally { if (temp is not null) try { Directory.Delete(temp, true); } catch (IOException) { } }
        }
        if (OperatingSystem.IsWindows() && c.Format is "exe" or "msi" or "msix")
        {
            var ps = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var r = await Run(ps, "-NoProfile", "-NonInteractive", "-Command", $"(Get-AuthenticodeSignature -LiteralPath '{c.Path.Replace("'", "''")}').Status");
            var status = r.Out.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            return new(status == "Valid" ? "signed" : status == "NotSigned" ? "unsigned" : r.Code == -1 ? "not-checked" : "invalid", string.Join("\n", lines), "Get-AuthenticodeSignature");
        }
        return new("not-checked", $"{PlatformName(c.Platform!)} {c.Format} signatures cannot be checked on {HostInfo.CurrentPlatform}; HCG Store records the file as not checked.", null);
    }

    /// <summary>On a Mac: packages a .app folder as a ZIP with ditto (keeps signatures, symlinks and extended attributes).</summary>
    public static async Task<string> ZipAppAsync(string appFolder, string outputDirectory, IProcessRunner runner, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("A .app is packaged for the Store with ditto on a Mac, which keeps its code signature.");
        var zip = System.IO.Path.Combine(outputDirectory, System.IO.Path.GetFileNameWithoutExtension(appFolder).Replace(' ', '-') + ".zip");
        var (r, _, err) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-c", "-k", "--keepParent", appFolder, zip], Timeout = TimeSpan.FromMinutes(10) }, ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException("ditto could not package the app: " + err.Trim());
        return zip;
    }
}
