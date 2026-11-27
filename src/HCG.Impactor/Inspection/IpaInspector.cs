using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Signing;
using HCG.Impactor.Model;

namespace HCG.Impactor.Inspection;

public sealed record IpaInspectionOptions
{
    /// <summary>Roots trusted for embedded provisioning profiles instead of the system store (tests only). Null = system store (Apple roots on a Mac).</summary>
    public X509Certificate2Collection? TrustedRoots { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Extract here and keep it (signing reuses it); when null a temporary folder is used and deleted.</summary>
    public string? ExtractTo { get; init; }
    public long MaxUncompressedBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public int MaxEntries { get; init; } = 400_000;
}

/// <summary>
/// HCG Impactor's IPA Inspector. Reads an .ipa (or an .app folder) without changing it and reports identity, versions,
/// minimum OS, architectures, nested bundles, signatures, entitlements, embedded profiles, certificates, expiry and
/// package integrity. Portable (Windows, Linux, macOS); Apple's own verification (codesign) is added on macOS by the
/// signing backend.
/// </summary>
public static class IpaInspector
{
    private static readonly string[] BundleExtensions = [".app", ".appex", ".framework", ".xpc"];

    public static IpaReport Inspect(string ipaPath, IpaInspectionOptions? options = null, CancellationToken ct = default)
    {
        options ??= new IpaInspectionOptions();
        var now = options.Time.GetUtcNow();
        var findings = new List<Finding>();
        var full = Path.GetFullPath(ipaPath);
        if (Directory.Exists(full) && full.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return InspectAppDirectory(full, full, options) with { InspectedAt = now };
        if (!File.Exists(full))
            return new IpaReport { SourcePath = full, InspectedAt = now, Findings = [Finding.Block("ipa.missing", "File not found", $"{full} does not exist.")] };
        if (full.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
            return new IpaReport
            {
                SourcePath = full, InspectedAt = now,
                Findings = [Finding.Block("ipa.is-deb", "Jailbreak package, not an IPA",
                    "This is a Debian package (.deb) for jailbroken devices. It is installed through a package manager (Sileo, Zebra) from a repository such as HCG Repo — not signed or installed as an IPA.",
                    "Use Mobile Build Studio › Unleashify and the HCG Repo workflow for .deb packages.")],
            };

        var size = new FileInfo(full).Length;
        string sha;
        using (var fs = File.OpenRead(full)) sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        var temp = options.ExtractTo is null;
        var root = options.ExtractTo ?? Path.Combine(Path.GetTempPath(), "hcg-impactor-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var extract = Extract(full, root, options, findings, ct);
            var baseReport = new IpaReport { SourcePath = full, SizeBytes = size, Sha256 = sha, InspectedAt = now, FileCount = extract.Files, UncompressedBytes = extract.Bytes };
            if (findings.HasBlockers()) return baseReport with { Findings = findings };

            var payload = Path.Combine(root, "Payload");
            var apps = Directory.Exists(payload) ? Directory.GetDirectories(payload, "*.app") : [];
            if (apps.Length == 0)
            {
                var hint = Directory.EnumerateDirectories(root, "*.xcarchive").Any() || Directory.EnumerateDirectories(root, "Products", SearchOption.AllDirectories).Any()
                    ? "This looks like a zipped Xcode archive. Export an IPA from it (Xcode › Organizer › Distribute App, or xcodebuild -exportArchive) and inspect that."
                    : Directory.EnumerateDirectories(root, "*.app").Any()
                        ? "The archive contains an .app at its top level, not inside Payload/. An IPA is a ZIP of Payload/<Name>.app."
                        : "An IPA is a ZIP containing Payload/<Name>.app.";
                findings.Add(Finding.Block("ipa.no-payload", "Not an iOS app package", "No Payload/<Name>.app folder was found in the archive.", hint));
                return baseReport with { Findings = findings };
            }
            if (apps.Length > 1)
                findings.Add(Finding.Block("ipa.multiple-apps", "More than one app in Payload", string.Join(", ", apps.Select(Path.GetFileName)) + " — an IPA contains exactly one app.",
                    "Export the IPA again from Xcode."));
            var app = InspectAppDirectory(apps[0], full, options);
            return app with
            {
                SizeBytes = size, Sha256 = sha, InspectedAt = now, FileCount = extract.Files, UncompressedBytes = extract.Bytes,
                AppPath = "Payload/" + Path.GetFileName(apps[0]),
                Findings = findings.Concat(app.Findings).ToList(),
            };
        }
        finally
        {
            if (temp) TryDelete(root);
        }
    }

    /// <summary>Inspects an extracted (or freshly signed) .app folder. <paramref name="sourcePath"/> is what the report names.</summary>
    public static IpaReport InspectAppDirectory(string appDir, string sourcePath, IpaInspectionOptions? options = null)
    {
        options ??= new IpaInspectionOptions();
        var now = options.Time.GetUtcNow();
        var findings = new List<Finding>();
        var app = ReadBundle(appDir, "", BundleKind.App, options, findings);
        var nested = new List<BundleInfo>();
        Walk(appDir, appDir, app.ExecutableName, nested, options, findings, depth: 0);

        var plist = ReadInfoPlist(appDir);
        var families = Families(plist);
        var caps = Capabilities(plist);
        var report = new IpaReport
        {
            SourcePath = sourcePath,
            InspectedAt = now,
            AppPath = Path.GetFileName(appDir),
            App = app,
            Nested = nested,
            Families = families,
            RequiredCapabilities = caps,
        };
        findings.AddRange(Evaluate(report, now));
        return report with { Findings = findings };
    }

    // ---------------------------------------------------------------- extraction

    private sealed record ExtractResult(int Files, long Bytes);

    private static ExtractResult Extract(string ipa, string root, IpaInspectionOptions options, List<Finding> findings, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var files = 0;
        long bytes = 0;
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(ipa); }
        catch (InvalidDataException)
        {
            findings.Add(Finding.Block("ipa.not-zip", "Not a valid IPA", "The file is not a ZIP archive (or its central directory is damaged).",
                "An .ipa is a ZIP file. Download or export it again; if it was renamed from another format, use the original."));
            return new(0, 0);
        }
        using (zip)
        {
            if (zip.Entries.Count > options.MaxEntries)
            {
                findings.Add(Finding.Block("ipa.too-many-entries", "Archive too large", $"The archive has {zip.Entries.Count:N0} entries (limit {options.MaxEntries:N0})."));
                return new(0, 0);
            }
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName;
                if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(p => p == ".."))
                {
                    findings.Add(Finding.Block("ipa.unsafe-path", "Unsafe path in the archive", $"\"{Trim(name)}\" would be written outside the package folder.",
                        "Do not install this file. A genuine IPA never contains such paths."));
                    return new(files, bytes);
                }
                if (name.StartsWith("__MACOSX/", StringComparison.Ordinal)) continue;
                var target = Path.GetFullPath(Path.Combine(root, name));
                if (!target.StartsWith(rootFull, StringComparison.Ordinal) && target + Path.DirectorySeparatorChar != rootFull)
                {
                    findings.Add(Finding.Block("ipa.unsafe-path", "Unsafe path in the archive", $"\"{Trim(name)}\" resolves outside the package folder."));
                    return new(files, bytes);
                }
                if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                bytes += entry.Length;
                if (bytes > options.MaxUncompressedBytes)
                {
                    findings.Add(Finding.Block("ipa.too-large", "Archive expands too far", $"The contents exceed {options.MaxUncompressedBytes / (1024 * 1024 * 1024)} GB when extracted."));
                    return new(files, bytes);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
                if ((unixMode & 0xF000) == 0xA000)
                {
                    string link;
                    using (var s = entry.Open()) using (var r = new StreamReader(s)) link = r.ReadToEnd();
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, link));
                    if (link.StartsWith('/') || !resolved.StartsWith(rootFull, StringComparison.Ordinal))
                    {
                        findings.Add(Finding.Block("ipa.unsafe-symlink", "Unsafe symbolic link", $"\"{Trim(name)}\" links outside the app (→ {Trim(link)})."));
                        return new(files, bytes);
                    }
                    try { File.CreateSymbolicLink(target, link); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        findings.Add(Finding.Warn("ipa.symlink-skipped", "Symbolic link not recreated", $"{Trim(name)} → {Trim(link)} ({ex.Message})."));
                    }
                    files++;
                    continue;
                }
                try
                {
                    using (var src = entry.Open())
                    using (var dst = File.Create(target))
                        src.CopyTo(dst);
                }
                catch (InvalidDataException ex)
                {
                    findings.Add(Finding.Block("ipa.damaged", "The package is damaged", $"{Trim(name)} could not be decompressed: {ex.Message}",
                        "Download or export the IPA again."));
                    return new(files, bytes);
                }
                if (!OperatingSystem.IsWindows() && (unixMode & 0x1FF) != 0)
                    try { File.SetUnixFileMode(target, (UnixFileMode)(unixMode & 0x1FF)); } catch (IOException) { }
                files++;
            }
        }
        return new(files, bytes);
    }

    // ---------------------------------------------------------------- bundles

    private static void Walk(string dir, string appRoot, string? appExecutable, List<BundleInfo> found, IpaInspectionOptions options, List<Finding> findings, int depth)
    {
        if (depth > 12) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).OrderBy(e => e, StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var executable = dir == appRoot ? appExecutable : null;
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name is "_CodeSignature" or "SC_Info") continue;
            var isLink = new FileInfo(entry).LinkTarget is not null;
            if (Directory.Exists(entry) && !isLink)
            {
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (BundleExtensions.Contains(ext) && Directory.Exists(entry))
                {
                    var rel = Path.GetRelativePath(appRoot, entry).Replace('\\', '/');
                    var kind = KindOf(rel, ext);
                    var bundle = ReadBundle(entry, rel, kind, options, findings);
                    // A framework folder without a binary (resources only) is sealed as data, not signed as code.
                    if (kind != BundleKind.Framework || bundle.Slices.Count > 0) found.Add(bundle);
                    Walk(entry, appRoot, null, found, options, findings, depth + 1);
                    // The bundle's own executable is handled by ReadBundle; skip it in the nested walk below.
                    continue;
                }
                Walk(entry, appRoot, null, found, options, findings, depth + 1);
                continue;
            }
            if (isLink || !File.Exists(entry)) continue;
            // Loose Mach-O files (Frameworks/libswift*.dylib, helper tools): signed individually.
            if (name == executable || IsBundleExecutable(entry)) continue;
            if (!MachOCodeSignature.IsMachO(entry)) continue;
            var relFile = Path.GetRelativePath(appRoot, entry).Replace('\\', '/');
            var (slices, sig) = MachOCodeSignature.Read(entry);
            found.Add(new BundleInfo { RelativePath = relFile, Kind = BundleKind.Dylib, Name = name, ExecutableName = name, Slices = slices, Signature = sig });
        }
    }

    /// <summary>True when <paramref name="file"/> is the main executable of the bundle folder it sits in.</summary>
    private static bool IsBundleExecutable(string file)
    {
        var dir = Path.GetDirectoryName(file)!;
        if (!BundleExtensions.Contains(Path.GetExtension(dir).ToLowerInvariant())) return false;
        var exe = ReadInfoPlist(dir)?.GetValueOrDefault("CFBundleExecutable") as string ?? Path.GetFileNameWithoutExtension(dir);
        return string.Equals(Path.GetFileName(file), exe, StringComparison.Ordinal);
    }

    private static BundleKind KindOf(string rel, string ext) => ext switch
    {
        ".appex" => BundleKind.AppExtension,
        ".framework" => BundleKind.Framework,
        ".app" when rel.StartsWith("Watch/", StringComparison.Ordinal) => BundleKind.WatchApp,
        ".app" when rel.StartsWith("AppClips/", StringComparison.Ordinal) => BundleKind.AppClip,
        _ => BundleKind.Other,
    };

    private static BundleInfo ReadBundle(string dir, string rel, BundleKind kind, IpaInspectionOptions options, List<Finding> findings)
    {
        var plist = ReadInfoPlist(dir);
        if (plist is null && kind != BundleKind.Framework)
            findings.Add(Finding.Block("bundle.no-info-plist", "Info.plist missing or unreadable", $"{Label(rel)} has no readable Info.plist.") with { Subject = Label(rel) });
        var exeName = plist?.GetValueOrDefault("CFBundleExecutable") as string ?? (kind == BundleKind.Framework ? Path.GetFileNameWithoutExtension(dir) : null);
        IReadOnlyList<BinarySlice> slices = [];
        var sig = CodeSignatureInfo.None;
        if (exeName is not null)
        {
            var exe = Path.Combine(dir, exeName);
            if (File.Exists(exe)) (slices, sig) = MachOCodeSignature.Read(exe);
            else if (kind != BundleKind.Framework)
                findings.Add(Finding.Block("bundle.no-executable", "Executable missing", $"{Label(rel)} names the executable \"{exeName}\", which is not in the bundle.") with { Subject = Label(rel) });
        }
        EmbeddedProfileInfo? profile = null;
        var profilePath = Path.Combine(dir, "embedded.mobileprovision");
        if (File.Exists(profilePath)) profile = ReadProfile(profilePath, options.TrustedRoots);
        var minOs = plist?.GetValueOrDefault("MinimumOSVersion") as string ?? slices.Select(s => s.MinimumOS).FirstOrDefault(m => m is not null);
        var ext = (plist?.GetValueOrDefault("NSExtension") as Dictionary<string, object>)?.GetValueOrDefault("NSExtensionPointIdentifier") as string
            ?? (plist?.GetValueOrDefault("EXAppExtensionAttributes") as Dictionary<string, object>)?.GetValueOrDefault("EXExtensionPointIdentifier") as string;
        return new BundleInfo
        {
            RelativePath = rel,
            Kind = kind,
            BundleIdentifier = plist?.GetValueOrDefault("CFBundleIdentifier") as string,
            Name = plist?.GetValueOrDefault("CFBundleDisplayName") as string ?? plist?.GetValueOrDefault("CFBundleName") as string ?? Path.GetFileNameWithoutExtension(dir),
            ShortVersion = plist?.GetValueOrDefault("CFBundleShortVersionString") as string,
            BuildNumber = plist?.GetValueOrDefault("CFBundleVersion") as string,
            ExecutableName = exeName,
            MinimumOSVersion = minOs,
            Slices = slices,
            Signature = sig,
            EmbeddedProfile = profile,
            Resources = SealedResources(dir, exeName),
            ExtensionPoint = ext,
        };
    }

    /// <summary>Reads and verifies an embedded or standalone provisioning profile, keeping the raw entitlement values.</summary>
    public static EmbeddedProfileInfo ReadProfile(string path, X509Certificate2Collection? trustedRoots)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(null, ex.Message); }
        var read = ProvisioningProfileReader.Read(data, path, trustedRoots);
        IReadOnlyDictionary<string, object> raw = new Dictionary<string, object>();
        try
        {
            var cms = new SignedCms();
            cms.Decode(data);
            if (PlistReader.ParseDictionary(cms.ContentInfo.Content).GetValueOrDefault("Entitlements") is Dictionary<string, object> ent) raw = ent;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or System.Xml.XmlException or FormatException) { }
        return new EmbeddedProfileInfo(read.Profile, read.Problem) { RawEntitlements = raw };
    }

    internal static Dictionary<string, object>? ReadInfoPlist(string bundleDir)
    {
        var path = Path.Combine(bundleDir, "Info.plist");
        if (!File.Exists(path)) return null;
        try { return PlistReader.ParseDictionary(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException or IOException) { return null; }
    }

    // ---------------------------------------------------------------- sealed resources

    /// <summary>
    /// Compares _CodeSignature/CodeResources (files2: SHA-256 "hash2", or SHA-1 "hash") with the files in the bundle.
    /// Nested code (entries with "cdhash") and symlinks are sealed by their own signatures and are not re-hashed here.
    /// </summary>
    internal static SealedResourcesInfo SealedResources(string bundleDir, string? executable)
    {
        var path = Path.Combine(bundleDir, "_CodeSignature", "CodeResources");
        if (!File.Exists(path)) return SealedResourcesInfo.Absent;
        Dictionary<string, object> files2;
        try { files2 = PlistReader.ParseDictionary(File.ReadAllBytes(path)).GetValueOrDefault("files2") as Dictionary<string, object> ?? []; }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException or IOException) { return new(true, 0, 0, ["_CodeSignature/CodeResources (unreadable)"], [], []); }

        var modified = new List<string>();
        var missing = new List<string>();
        var verified = 0;
        var nestedPrefixes = new List<string>();
        foreach (var (key, value) in files2)
        {
            byte[]? sha256 = null, sha1 = null;
            var optional = false;
            switch (value)
            {
                case byte[] b: sha1 = b; break;
                case Dictionary<string, object> d:
                    if (d.ContainsKey("cdhash") || d.ContainsKey("symlink")) { nestedPrefixes.Add(key.TrimEnd('/') + "/"); continue; }
                    sha256 = d.GetValueOrDefault("hash2") as byte[];
                    sha1 = d.GetValueOrDefault("hash") as byte[];
                    optional = d.GetValueOrDefault("optional") is true;
                    break;
                default: continue;
            }
            var file = Path.Combine(bundleDir, key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) { if (!optional) missing.Add(key); continue; }
            using var fs = File.OpenRead(file);
            var ok = sha256 is not null ? SHA256.HashData(fs).AsSpan().SequenceEqual(sha256)
                : sha1 is not null && SHA1.HashData(fs).AsSpan().SequenceEqual(sha1);
            if (ok) verified++; else modified.Add(key);
        }

        // Files the seal does not mention (outside nested code): resources added after signing.
        var unsealed = new List<string>();
        foreach (var f in Directory.EnumerateFiles(bundleDir, "*", SearchOption.AllDirectories).Take(200_000))
        {
            var rel = Path.GetRelativePath(bundleDir, f).Replace('\\', '/');
            if (rel.StartsWith("_CodeSignature/", StringComparison.Ordinal) || rel == executable || rel is "Info.plist" or "PkgInfo" || rel.StartsWith("SC_Info/", StringComparison.Ordinal)) continue;
            if (files2.ContainsKey(rel) || nestedPrefixes.Any(p => rel.StartsWith(p, StringComparison.Ordinal))) continue;
            if (BundleExtensions.Any(e => rel.Split('/').SkipLast(1).Any(seg => seg.EndsWith(e, StringComparison.OrdinalIgnoreCase)))) continue;
            unsealed.Add(rel);
            if (unsealed.Count >= 50) break;
        }
        return new SealedResourcesInfo(true, files2.Count, verified, modified, missing, unsealed);
    }

    // ---------------------------------------------------------------- evaluation

    private static DeviceFamilies Families(Dictionary<string, object>? plist)
    {
        var fam = DeviceFamilies.None;
        if (plist?.GetValueOrDefault("UIDeviceFamily") is List<object> list)
            foreach (var v in list)
                fam |= Convert.ToInt64(v is string s && long.TryParse(s, out var n) ? n : v is long l ? l : 0) switch
                {
                    1 => DeviceFamilies.IPhone, 2 => DeviceFamilies.IPad, 3 => DeviceFamilies.AppleTv, 4 => DeviceFamilies.Watch, 7 => DeviceFamilies.Vision, _ => DeviceFamilies.None,
                };
        // Apple's default when UIDeviceFamily is absent is iPhone (and iPad in compatibility mode).
        return fam == DeviceFamilies.None ? DeviceFamilies.IPhone : fam;
    }

    private static IReadOnlyList<string> Capabilities(Dictionary<string, object>? plist) => plist?.GetValueOrDefault("UIRequiredDeviceCapabilities") switch
    {
        List<object> l => l.OfType<string>().ToList(),
        Dictionary<string, object> d => d.Where(kv => kv.Value is true).Select(kv => kv.Key).ToList(),
        _ => [],
    };

    private static IEnumerable<Finding> Evaluate(IpaReport r, DateTimeOffset now)
    {
        var app = r.App!;
        if (string.IsNullOrWhiteSpace(app.BundleIdentifier))
            yield return Finding.Block("app.no-bundle-id", "No bundle identifier", "The app's Info.plist has no CFBundleIdentifier.", "Rebuild the app; Xcode sets this from PRODUCT_BUNDLE_IDENTIFIER.");
        if (app.MinimumOSVersion is null)
            yield return Finding.Warn("app.no-minimum-os", "Minimum iOS version unknown", "Info.plist has no MinimumOSVersion and the binary does not record one.");

        foreach (var b in r.AllBundles.Where(b => b.Slices.Count > 0))
        {
            var label = Label(b.RelativePath);
            if (b.Slices.Any(s => s.Platform == "iOS Simulator"))
                yield return Finding.Block("binary.simulator", "Built for the Simulator", $"{label} contains Simulator code ({string.Join(", ", b.Architectures)}), which cannot run on an iPhone or iPad.",
                    "Build for a device: Xcode › Product › Archive (or xcodebuild -sdk iphoneos) and export an IPA.") with { Subject = label };
            else if (b.Slices.Any(s => s.Platform is "macOS" or "Mac Catalyst"))
                yield return Finding.Block("binary.not-ios", "Not an iOS binary", $"{label} is built for {b.Slices.First(s => s.Platform is not null).Platform}.") with { Subject = label };
            if (!b.Slices.Any(s => s.Architecture is "arm64" or "arm64e"))
                yield return Finding.Block("binary.32-bit", "No 64-bit code", $"{label} contains only {string.Join(", ", b.Architectures)}. iOS 11 and later run 64-bit (arm64) apps only.") with { Subject = label };
            if (b.IsEncrypted)
                yield return Finding.Block("binary.encrypted", "Encrypted App Store binary",
                    $"{label} is encrypted with Apple's App Store DRM (FairPlay). A re-signed encrypted binary cannot start on any device, and HCG does not decrypt apps.",
                    "Install App Store apps from the App Store. For your own apps, export an IPA from your Xcode archive (Development or Ad Hoc).") with { Subject = label };
            if (b.Signature.Problem is { } p)
                yield return Finding.Warn("signature.malformed", "Code signature problem", $"{label}: {p}", "Re-signing replaces the signature.") with { Subject = label };
        }

        if (!app.Signature.Present)
            yield return Finding.Info("signature.unsigned", "Unsigned", "The app is not code-signed. Sign it in Signing Studio before installing.");
        else if (app.Signature.AdHoc)
            yield return Finding.Info("signature.adhoc", "Ad hoc signature", "The app has an ad hoc signature (no certificate). Devices require a signature from an Apple-issued certificate; sign it in Signing Studio.");
        else if (app.Signature.Signer is { } signer)
        {
            if (signer.NotAfter < now)
                yield return Finding.Warn("signature.certificate-expired", "Signing certificate expired",
                    $"Signed by {signer.CommonName}, which expired on {signer.NotAfter:yyyy-MM-dd}. Devices refuse to install it as is.", "Re-sign with a current certificate.");
        }

        foreach (var b in r.AllBundles.Where(b => b.EmbeddedProfile is not null))
        {
            var label = Label(b.RelativePath);
            var ep = b.EmbeddedProfile!;
            if (ep.Profile is null)
                yield return Finding.Warn("profile.embedded-unreadable", "Embedded profile unreadable", $"{label}: {ep.Problem}", "Re-signing replaces it.") with { Subject = label };
            else if (ep.Profile.Expiration is { } exp && exp < now)
                yield return Finding.Warn("profile.embedded-expired", "Embedded profile expired", $"{label}: \"{ep.Profile.Name}\" expired on {exp:yyyy-MM-dd}. The app will not launch until it is re-signed.",
                    "Re-sign with a current profile.") with { Subject = label };
        }

        foreach (var b in r.AllBundles.Where(b => b.Resources.Present && !b.Resources.Intact))
        {
            var label = Label(b.RelativePath);
            var changed = b.Resources.Modified.Concat(b.Resources.Missing).Take(5);
            yield return Finding.Warn("integrity.resources-changed", "Files changed after signing",
                $"{label}: {b.Resources.Modified.Count} modified and {b.Resources.Missing.Count} missing sealed file(s) ({string.Join(", ", changed)}). Installing it as is fails with \"A signed resource has been added, modified, or deleted\".",
                "Re-signing in Signing Studio seals the current contents. Only do that for files you changed on purpose.") with { Subject = label };
        }

        var appId = app.BundleIdentifier;
        if (appId is not null)
            foreach (var x in r.Extensions.Where(x => x.BundleIdentifier is not null && !x.BundleIdentifier.StartsWith(appId + ".", StringComparison.Ordinal)))
                yield return Finding.Warn("bundle.extension-prefix", "Extension identifier outside the app", $"{Label(x.RelativePath)} is {x.BundleIdentifier}; embedded targets must start with \"{appId}.\".",
                    "Keep the prefix when changing identifiers; devices reject mismatched extensions.") with { Subject = Label(x.RelativePath) };

        if (r.Families.HasFlag(DeviceFamilies.Vision) && !r.Families.HasFlag(DeviceFamilies.IPhone) && !r.Families.HasFlag(DeviceFamilies.IPad))
            yield return Finding.Block("app.not-iphone-ipad", "Not an iPhone/iPad app", "UIDeviceFamily lists neither iPhone nor iPad.");
    }

    internal static string Label(string rel) => rel.Length == 0 ? "The app" : rel;
    private static string Trim(string s) => s.Length > 160 ? s[..160] + "…" : s;

    internal static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
