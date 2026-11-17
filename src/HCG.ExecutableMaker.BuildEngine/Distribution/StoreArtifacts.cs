using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Signing;
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
    /// <summary>installer · portable-app · device-build · simulator-build · jailbreak-package (null: unknown).</summary>
    public string? Kind { get; init; }
    /// <summary>Whether the file carries a signature structure (present/absent), found in the file itself; null when this
    /// format was not examined. Presence is not validity: <see cref="Signing"/> holds the platform tool's verdict.</summary>
    public string? SignatureStructure { get; init; }
    public IosBundleFacts? Ios { get; init; }
    public bool IsSupported => Platform is not null && Format is not null && Problems.Count == 0;
    public string Label => Platform is null ? (Kind == "simulator-build" ? "iOS Simulator build — not accepted" : "Not supported") : $"{StoreArtifacts.PlatformName(Platform)} · {Format}";
    public string KindName => StoreArtifacts.KindName(Kind);
    /// <summary>How this file installs. Never "ready" for unsigned, Simulator-only or jailbreak-only files.</summary>
    public StoreReadiness Readiness => StoreArtifacts.Readiness(this);
}

/// <summary>What an iOS app bundle says about itself: Simulator or device, and how it is provisioned.</summary>
/// <param name="Provisioning">development · ad-hoc · enterprise · app-store · none (no profile) · unreadable.</param>
/// <param name="ProfileVerified">The embedded profile's CMS signature is intact and chains to Apple on this computer.</param>
public sealed record IosBundleFacts(bool Simulator, string Provisioning, string? ProfileName, DateTimeOffset? ProfileExpires, bool ProfileVerified, string? BundleId);

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

    public static string KindName(string? kind) => kind switch
    {
        "installer" => "Installer",
        "portable-app" => "Portable app",
        "device-build" => "iOS device build",
        "simulator-build" => "iOS Simulator build",
        "jailbreak-package" => "Jailbreak package",
        _ => "Unknown",
    };

    public const string SimulatorRefusal = "iOS Simulator build: it runs only in the iOS Simulator on a Mac and cannot be installed on an iPhone or iPad, so HCG Store does not accept it. Build for a device and export an IPA (Mobile Build Studio › Archive, then Export IPA with a signing profile).";

    /// <summary>
    /// How a file installs, from its kind, the signature structure found in it, the platform tool's check and (iOS) its
    /// provisioning profile. Mirrors HCG Store's own rule (server/src/artifact-validation.ts installReadiness), which
    /// decides again from the uploaded bytes.
    /// </summary>
    public static StoreReadiness Readiness(StoreArtifactCandidate c)
    {
        static StoreReadiness R(string status, string label, bool ok = false) => new(status, label, ok);
        var reported = c.Signing.Status;
        switch (c.Kind)
        {
            case "simulator-build": return R("simulator-only", "iOS Simulator build: runs only in the Simulator on a Mac. Cannot be installed on an iPhone or iPad.");
            case "jailbreak-package": return R("jailbroken-devices-only", "Jailbreak package: installs only on jailbroken iPhones/iPads through a package manager (Sileo). Not for ordinary devices.");
            case "device-build":
                if (c.SignatureStructure == "absent" || reported == "unsigned") return R("unsigned", "Unsigned iOS device build: cannot be installed on an iPhone or iPad until it is signed with a provisioning profile.");
                if (reported == "invalid") return R("unsigned", "The signature is INVALID: not installable.");
                if (c.Ios?.ProfileExpires is { } exp && exp < DateTimeOffset.UtcNow) return R("profile-expired", $"Its provisioning profile expired on {exp:yyyy-MM-dd}: not installable until it is re-signed.");
                return c.Ios?.Provisioning switch
                {
                    "development" => R("registered-devices-only", "Development-signed: installs only on the devices registered in its provisioning profile."),
                    "ad-hoc" => R("registered-devices-only", "Ad hoc-signed: installs only on the devices listed in its provisioning profile."),
                    "enterprise" => R("enterprise-devices-only", "In-house (enterprise) signed: installs on devices that trust the organisation's certificate."),
                    "app-store" => R("app-store-profile", "Signed for App Store distribution: iPhones and iPads install it only through the App Store or TestFlight, which HCG never submits to."),
                    "none" => R("not-installable", "Signed without a provisioning profile: not installable on ordinary iPhones or iPads."),
                    _ => R("signature-not-verified", "A signature is present but its provisioning profile could not be read: installability unknown."),
                };
        }
        if (c.Platform is null) return R("not-supported", "Not a file HCG Store accepts.");
        if (c.Platform == "linux") return R("ready", "Linux package: no platform signature is required to install it.", true);
        if (c.SignatureStructure == "absent" || reported == "unsigned")
            return R("unsigned", c.Platform == "macos" ? "Unsigned: macOS Gatekeeper blocks it on other Macs." : "Unsigned: Windows SmartScreen warns before it runs; some organisations block unsigned software.");
        if (reported == "invalid") return R("unsigned", "The signature is INVALID.");
        if (c.Platform == "macos")
            return reported switch
            {
                "notarized" => R("ready", "Developer ID signed and notarized (checked on this computer).", true),
                "signed" => R("not-notarized", "Signed but not notarized: Gatekeeper warns or blocks it on other Macs."),
                _ => R("signature-not-verified", "Signature and notarisation not checked on this computer (needs a Mac)."),
            };
        return reported == "signed"
            ? R("ready", "Signed (Authenticode, checked on this computer).", true)
            : R("signature-not-verified", c.SignatureStructure == "present" ? "A signature is present but was not verified on this computer (needs Windows)." : "Signature not checked on this computer.");
    }

    public static async Task<StoreArtifactCandidate> DetectAsync(string path, CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path);
        if (Directory.Exists(path))
        {
            // Mobile Build Studio's Simulator output is an .app folder; an archive is an .xcarchive folder.
            var iosInfo = System.IO.Path.Combine(path, "Info.plist");
            if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(iosInfo))
            {
                var facts = IosFacts(File.ReadAllBytes(iosInfo), null);
                return new StoreArtifactCandidate
                {
                    Path = path, FileName = name, Kind = facts.Simulator ? "simulator-build" : "device-build", Ios = facts,
                    Problems = [facts.Simulator ? SimulatorRefusal : "An iOS app is shipped to HCG Store as an IPA: export one from the archive (Mobile Build Studio › Export IPA)."],
                };
            }
            return new StoreArtifactCandidate
            {
                Path = path, FileName = name,
                Problems = [name.EndsWith(".xcarchive", StringComparison.OrdinalIgnoreCase)
                    ? "An Xcode archive is not an installable file: export an IPA from it (Mobile Build Studio › Export IPA, with a signing profile)."
                    : name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    ? "A macOS .app is a folder: package it as a ZIP first (Ship to Store offers this on a Mac with ditto, which keeps its signature)."
                    : "Folders cannot be shipped; choose a file (for a portable folder, ship its ZIP)."],
            };
        }
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
        string? kind = null, structure = null;
        IosBundleFacts? ios = null;

        if (lower.EndsWith(".exe"))
        {
            (platform, format, kind) = ("windows", "exe", "installer");
            if (!Magic("MZ")) problems.Add("Not a Windows executable (no MZ header).");
            else { using var s = File.OpenRead(path); structure = PeSignature(s); }
        }
        else if (lower.EndsWith(".msi")) { (platform, format, kind) = ("windows", "msi", "installer"); if (!(head.Length >= 8 && Convert.ToHexStringLower(head, 0, 8) == "d0cf11e0a1b11ae1")) problems.Add("Not a Windows Installer package."); }
        else if (lower.EndsWith(".msix"))
        {
            (platform, format, kind) = ("windows", "msix", "installer");
            Zip(path, problems, z =>
            {
                if (z.GetEntry("AppxManifest.xml") is null) problems.Add("An MSIX package must contain AppxManifest.xml.");
                structure = z.GetEntry("AppxSignature.p7x") is null ? "absent" : "present";
            });
        }
        else if (lower.EndsWith(".dmg")) { (platform, format, kind) = ("macos", "dmg", "installer"); if (!Trailer(path, "koly")) problems.Add("Not a macOS disk image (no UDIF trailer)."); }
        else if (lower.EndsWith(".pkg")) { (platform, format, kind) = ("macos", "pkg", "installer"); if (!Magic("xar!")) problems.Add("Not a macOS installer package (no xar header)."); }
        else if (lower.EndsWith(".rpm")) { (platform, format, kind) = ("linux", "rpm", "installer"); if (!(head.Length >= 4 && Convert.ToHexStringLower(head, 0, 4) == "edabeedb")) problems.Add("Not an RPM package."); }
        else if (lower.EndsWith(".appimage")) { (platform, format, kind) = ("linux", "appimage", "portable-app"); if (!Magic("\u007fELF") || !Magic("AI\u0002", 8)) problems.Add("Not a type-2 AppImage."); }
        else if (lower.EndsWith(".ipa"))
        {
            format = "ipa";
            platform = "ios";
            kind = "device-build";
            Zip(path, problems, z =>
            {
                var plist = z.Entries.FirstOrDefault(e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^Payload/[^/]+\.app/Info\.plist$"));
                if (plist is null) { problems.Add("An IPA must contain Payload/<name>.app/Info.plist."); return; }
                var appDir = plist.FullName[..^"Info.plist".Length];
                structure = z.GetEntry(appDir + "_CodeSignature/CodeResources") is null ? "absent" : "present";
                try
                {
                    var plistBytes = Bytes(plist);
                    ios = IosFacts(plistBytes, z.GetEntry(appDir + "embedded.mobileprovision") is { } prof && prof.Length <= ProvisioningProfileReader.MaxBytes ? Bytes(prof) : null);
                    if (ios.Simulator) { kind = "simulator-build"; problems.Add(SimulatorRefusal); }
                    var info = PlistReader.ParseDictionary(plistBytes);
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
            kind = platform == "ios" ? "jailbreak-package" : "installer";
        }
        else if (lower.EndsWith(".zip"))
        {
            format = "zip";
            kind = "portable-app";
            Zip(path, problems, z =>
            {
                var apps = z.Entries.Select(e => System.Text.RegularExpressions.Regex.Match(e.FullName, @"^([^/]+\.app)/").Groups[1].Value).Where(v => v.Length > 0).Distinct().ToList();
                // An iOS .app keeps Info.plist at its top (a macOS .app has Contents/Info.plist): a zipped Simulator build.
                if (apps.Count > 0 && z.GetEntry(apps[0] + "/Info.plist") is { } iosPlist && z.GetEntry(apps[0] + "/Contents/Info.plist") is null)
                {
                    ios = IosFacts(Bytes(iosPlist), null);
                    kind = ios.Simulator ? "simulator-build" : "device-build";
                    problems.Add(ios.Simulator ? SimulatorRefusal : "An iOS app inside a ZIP: HCG Store takes iOS apps as IPA files (Payload/<name>.app). Export an IPA instead.");
                    return;
                }
                platform = apps.Count > 0 ? "macos" : "windows";
                if (apps.Count > 0) structure = z.GetEntry(apps[0] + "/Contents/_CodeSignature/CodeResources") is null ? "absent" : "present";
                else
                {
                    // A portable Windows app is judged by its own programs: the shallowest .exe files, not runtime helpers that
                    // ship already signed by their vendor (.NET's createdump.exe). Unsigned if any of them has no
                    // Authenticode certificate table; "present" only if all of them have one.
                    var exes = z.Entries.Where(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !RuntimeHelpers.Contains(System.IO.Path.GetFileName(e.FullName))).ToList();
                    if (exes.Count > 0)
                    {
                        var depth = exes.Min(e => e.FullName.Count(ch => ch == '/'));
                        var found = exes.Where(e => e.FullName.Count(ch => ch == '/') == depth).Take(20)
                            .Select(e => { using var es = e.Open(); using var ms = new MemoryStream(); es.CopyTo(ms); ms.Position = 0; return PeSignature(ms); }).ToList();
                        structure = found.Contains("absent") ? "absent" : found.All(f => f == "present") ? "present" : null;
                    }
                }
                if (apps.Count > 0 && !z.Entries.Any(e => e.FullName.EndsWith(".app/Contents/Info.plist", StringComparison.Ordinal))) problems.Add("The .app bundle has no Contents/Info.plist.");
                if (apps.Count == 0 && !z.Entries.Any(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    if (LinuxProgram(z) is { } elf)
                    {
                        // A Linux portable ZIP (no .exe, a Linux program inside) is not a Windows app, and HCG Store takes no
                        // Linux ZIPs: refuse it here rather than declare it as something it is not.
                        platform = "linux";
                        problems.Add($"A Linux app in a ZIP ({elf} is a Linux program). HCG Store takes Linux apps only as .deb, .rpm or .AppImage, not ZIP. Package it as one of those to ship it.");
                    }
                    else notes.Add("No .app or .exe inside: shipped as a Windows ZIP. Check that this is what you meant.");
                }
            });
        }
        else problems.Add(UnsupportedExplanation(lower));
        if (kind == "simulator-build") (platform, format) = (null, null);
        return c with { Platform = platform, Format = format, Architectures = arch, MinimumOs = minOs, Package = package, Problems = problems, Notes = notes, Kind = kind, SignatureStructure = structure, Ios = ios };
    }

    /// <summary>What HCG Store accepts, and what to do instead, for a file type it does not.</summary>
    public static string UnsupportedExplanation(string lowerName)
    {
        const string accepted = "HCG Store accepts: Windows .exe .msi .msix .zip · macOS app .zip .dmg .pkg · iOS/iPadOS .ipa (device builds) and jailbreak .deb · Linux .deb .rpm .AppImage.";
        var hint = System.IO.Path.GetExtension(lowerName) switch
        {
            ".apk" or ".aab" or ".apks" => "Android packages are not distributed through HCG Store yet.",
            ".xcarchive" => "An Xcode archive is not installable: export an IPA from it.",
            ".tar" or ".gz" or ".tgz" or ".xz" or ".7z" => "Archives other than ZIP are not accepted; Linux apps ship as .deb, .rpm or .AppImage.",
            ".app" => "A .app is a folder: package it as a ZIP (macOS) or export an IPA (iOS).",
            ".dll" or ".so" or ".dylib" => "Libraries are not shipped on their own; ship the app or installer that uses them.",
            _ => "This file type is not supported.",
        };
        return hint + " " + accepted;
    }

    private static byte[] Bytes(ZipArchiveEntry e)
    {
        using var es = e.Open();
        using var ms = new MemoryStream();
        es.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Simulator or device (Info.plist), and the provisioning profile's type (its CMS envelope is verified).</summary>
    public static IosBundleFacts IosFacts(byte[] infoPlist, byte[]? profile)
    {
        var simulator = false;
        string? bundleId = null;
        try
        {
            var info = PlistReader.ParseDictionary(infoPlist);
            bundleId = info.GetValueOrDefault("CFBundleIdentifier") as string;
            var platforms = (info.GetValueOrDefault("CFBundleSupportedPlatforms") as IEnumerable<object>)?.OfType<string>() ?? [];
            simulator = platforms.Any(p => p.EndsWith("Simulator", StringComparison.Ordinal))
                        || (info.GetValueOrDefault("DTPlatformName") as string)?.EndsWith("simulator", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidCastException or System.Xml.XmlException)
        {
            simulator = Encoding.Latin1.GetString(infoPlist).Contains("iPhoneSimulator", StringComparison.Ordinal);
        }
        if (profile is null) return new(simulator, "none", null, null, false, bundleId);
        var read = ProvisioningProfileReader.Read(profile, "embedded.mobileprovision");
        if (read.Profile is not { } p) return new(simulator, "unreadable", null, null, false, bundleId);
        var kind = p.Kind switch
        {
            AppleProfileKind.Development => "development",
            AppleProfileKind.AdHoc => "ad-hoc",
            AppleProfileKind.Enterprise => "enterprise",
            AppleProfileKind.AppStore => "app-store",
            _ => "unreadable",
        };
        return new(simulator, kind, p.Name, p.Expiration, p.Signature == ProfileSignatureStatus.VerifiedApple, bundleId);
    }

    private static readonly HashSet<string> RuntimeHelpers = new(StringComparer.OrdinalIgnoreCase) { "createdump.exe" };

    /// <summary>The shallowest Linux (ELF) program in a ZIP without a .exe or .app, if any: its entry name.</summary>
    private static string? LinuxProgram(ZipArchive z)
    {
        // Programs first: files without an extension (an app host, createdump) or shared libraries (.so), in any number
        // of entries, so hundreds of managed .dll files cannot hide them; a few other files after that. 4 bytes each.
        static bool Candidate(string name) => !System.IO.Path.GetFileName(name).Contains('.') || name.EndsWith(".so", StringComparison.Ordinal) || name.Contains(".so.", StringComparison.Ordinal);
        var all = z.Entries.Where(e => e.Length >= 4 && !e.FullName.EndsWith('/')).ToList();
        var files = all.Where(e => Candidate(e.FullName)).OrderBy(e => e.FullName.Count(ch => ch == '/')).Take(5000)
            .Concat(all.Where(e => !Candidate(e.FullName)).OrderBy(e => e.FullName.Count(ch => ch == '/')).Take(200));
        foreach (var e in files)
        {
            using var s = e.Open();
            Span<byte> magic = stackalloc byte[4];
            if (s.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 && magic.SequenceEqual("\u007FELF"u8)) return e.FullName;
        }
        return null;
    }

    /// <summary>Authenticode certificate table in a PE file's data directory: "present", "absent", or null if not a PE.</summary>
    public static string? PeSignature(Stream s)
    {
        var head = new byte[1024];
        var n = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (n < 64 || head[0] != 'M' || head[1] != 'Z') return null;
        var pe = BitConverter.ToInt32(head, 60);
        if (pe < 0 || pe + 24 + 240 > n || Encoding.Latin1.GetString(head, pe, 4) != "PE\0\0") return null;
        var magic = BitConverter.ToUInt16(head, pe + 24);
        var dir = pe + 24 + (magic == 0x20b ? 112 : 96) + 4 * 8; // data directory 4 = certificate table
        return BitConverter.ToUInt32(head, dir + 4) > 0 ? "present" : "absent";
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
