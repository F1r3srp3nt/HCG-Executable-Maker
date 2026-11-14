using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Mobile;

public enum MobileArtifactKind
{
    Apk,
    Aab,
    ApkSet,
    Ipa,
    XcArchive,
    IosApp,
}

public enum MobileSigningState
{
    Unsigned,
    /// <summary>Android debug certificate (CN=Android Debug) — installable for testing, never distributable.</summary>
    DebugSigned,
    /// <summary>Signed with a non-debug key (release/upload key).</summary>
    ReleaseSigned,
    /// <summary>iOS: ad-hoc code signature only (no Apple certificate).</summary>
    AdHocSigned,
    /// <summary>iOS: signed with a development profile (get-task-allow).</summary>
    DevelopmentSigned,
    /// <summary>iOS: signed with a distribution profile (ad hoc, enterprise or App Store).</summary>
    DistributionSigned,
    /// <summary>Signed, but the kind could not be determined statically.</summary>
    Signed,
}

/// <summary>Static inspection result for a mobile artifact. Inspection never executes the artifact.</summary>
public sealed record MobileArtifactReport
{
    public required MobileArtifactKind Kind { get; init; }
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public string? PackageId { get; init; }
    public string? VersionName { get; init; }
    public string? VersionCode { get; init; }
    /// <summary>Android minSdk or iOS MinimumOSVersion.</summary>
    public string? MinimumOs { get; init; }
    /// <summary>iOS: the minimum OS of each slice of the app's executable (LC_BUILD_VERSION / LC_VERSION_MIN_IPHONEOS); empty when not read.</summary>
    public IReadOnlyList<string> BinaryMinimumOs { get; init; } = [];
    public string? TargetSdk { get; init; }
    public IReadOnlyList<string> Architectures { get; init; } = [];
    public MobileSigningState Signing { get; init; }
    public IReadOnlyList<string> SigningSchemes { get; init; } = [];
    public string? Signer { get; init; }
    /// <summary>iphoneos / iphonesimulator for iOS artifacts.</summary>
    public string? Platform { get; init; }
    public bool IsDistributable { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<VerificationCheck> Checks { get; init; } = [];
    public IReadOnlyList<string> Modules { get; init; } = [];
    public VerificationStatus Status => Verification.Checks.Combine(Checks);
}

/// <summary>
/// Inspects .apk, .aab, .apks, .ipa, .xcarchive and .app artifacts without executing or extracting them. Integrity and
/// structure checks are local; package-store approval is never implied.
/// </summary>
public static class MobileArtifactInspector
{
    public static MobileArtifactKind? KindOf(string path)
    {
        var p = path.TrimEnd('/', '\\');
        return System.IO.Path.GetExtension(p).ToLowerInvariant() switch
        {
            ".apk" => MobileArtifactKind.Apk,
            ".aab" => MobileArtifactKind.Aab,
            ".apks" => MobileArtifactKind.ApkSet,
            ".ipa" => MobileArtifactKind.Ipa,
            ".xcarchive" => MobileArtifactKind.XcArchive,
            ".app" => MobileArtifactKind.IosApp,
            _ => null,
        };
    }

    public static MobileArtifactReport Inspect(string path, CancellationToken ct = default)
    {
        var kind = KindOf(path) ?? throw new ArgumentException($"Not a recognized mobile artifact: {path}");
        try
        {
            return kind switch
            {
                MobileArtifactKind.Apk => InspectApk(path, ct),
                MobileArtifactKind.Aab => InspectAab(path, ct),
                MobileArtifactKind.ApkSet => InspectApkSet(path, ct),
                MobileArtifactKind.Ipa => InspectIpa(path, ct),
                MobileArtifactKind.XcArchive => InspectXcArchive(path),
                _ => InspectIosApp(path),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
        {
            return new MobileArtifactReport
            {
                Kind = kind, Path = path, SizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
                Checks = [new VerificationCheck("Structure", VerificationStatus.Failed, $"The artifact is corrupt or not a valid {kind}: {ex.Message}")],
            };
        }
    }

    // ───────────── Android ─────────────

    private static MobileArtifactReport InspectApk(string path, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        using var zip = OpenZip(fs);
        var checks = new List<VerificationCheck> { ZipIntegrity.Check(zip, ct) };
        var manifestBytes = ZipIntegrity.ReadEntry(zip, "AndroidManifest.xml")
                            ?? throw new InvalidDataException("AndroidManifest.xml is missing (not an APK).");
        var manifest = AndroidBinaryXml.Parse(manifestBytes);
        var root = manifest.FirstOrDefault(e => e.Name == "manifest") ?? throw new InvalidDataException("Manifest root element missing.");
        var sdk = manifest.FirstOrDefault(e => e.Name == "uses-sdk");
        var app = manifest.FirstOrDefault(e => e.Name == "application");
        checks.Add(new VerificationCheck("Manifest", VerificationStatus.Passed, $"{root.Attributes.GetValueOrDefault("package")} {root.Attributes.GetValueOrDefault("versionName")}"));

        var dex = zip.Entries.Count(e => e.FullName.StartsWith("classes", StringComparison.Ordinal) && e.FullName.EndsWith(".dex", StringComparison.Ordinal));
        checks.Add(dex > 0
            ? new VerificationCheck("Code", VerificationStatus.Passed, $"{dex} dex file(s)")
            : new VerificationCheck("Code", VerificationStatus.Failed, "No classes.dex — the APK contains no executable code."));
        var abis = Abis(zip, "lib/");

        fs.Position = 0;
        var sig = ApkSignature.Read(fs, zip.Entries.Select(e => e.FullName));
        var cert = sig.Certificate ?? V1Certificate(zip);
        var debuggable = app?.Attributes.GetValueOrDefault("debuggable") == "true";
        var state = !sig.IsSigned ? MobileSigningState.Unsigned
            : cert?.Subject.Contains("CN=Android Debug", StringComparison.Ordinal) == true ? MobileSigningState.DebugSigned
            : cert is not null ? MobileSigningState.ReleaseSigned : MobileSigningState.Signed;
        checks.Add(state switch
        {
            MobileSigningState.Unsigned => new VerificationCheck("Signature", VerificationStatus.PassedWithWarnings, "Unsigned — cannot be installed until it is signed (and zipaligned)."),
            MobileSigningState.DebugSigned => new VerificationCheck("Signature", VerificationStatus.PassedWithWarnings, $"Debug-signed ({string.Join(", ", sig.Schemes)}). Installable for testing; not accepted by Google Play."),
            _ => new VerificationCheck("Signature", VerificationStatus.Passed, $"Signed ({string.Join(", ", sig.Schemes)}) by {cert?.Subject ?? "unknown signer"}. Run apksigner to verify cryptographically."),
        });
        if (debuggable) checks.Add(new VerificationCheck("Debuggable", VerificationStatus.PassedWithWarnings, "android:debuggable=true — a debug build."));

        return new MobileArtifactReport
        {
            Kind = MobileArtifactKind.Apk, Path = path, SizeBytes = fs.Length,
            PackageId = root.Attributes.GetValueOrDefault("package"),
            VersionName = root.Attributes.GetValueOrDefault("versionName"),
            VersionCode = root.Attributes.GetValueOrDefault("versionCode"),
            MinimumOs = sdk?.Attributes.GetValueOrDefault("minSdkVersion"),
            TargetSdk = sdk?.Attributes.GetValueOrDefault("targetSdkVersion"),
            Architectures = abis.Count > 0 ? abis : ["any (no native code)"],
            Signing = state, SigningSchemes = sig.Schemes, Signer = cert?.Subject,
            Platform = "android",
            IsDistributable = state == MobileSigningState.ReleaseSigned && !debuggable,
            Checks = checks,
            Notes = state == MobileSigningState.Unsigned ? ["Sign with apksigner (after zipalign) before installing."] : [],
        };
    }

    private static MobileArtifactReport InspectAab(string path, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        using var zip = OpenZip(fs);
        var checks = new List<VerificationCheck> { ZipIntegrity.Check(zip, ct) };
        var manifestBytes = ZipIntegrity.ReadEntry(zip, "base/manifest/AndroidManifest.xml")
                            ?? throw new InvalidDataException("base/manifest/AndroidManifest.xml is missing (not an App Bundle).");
        var manifest = ProtoXml.Parse(manifestBytes);
        var root = manifest.FirstOrDefault(e => e.Name == "manifest") ?? throw new InvalidDataException("Manifest root element missing.");
        var sdk = manifest.FirstOrDefault(e => e.Name == "uses-sdk");
        checks.Add(zip.GetEntry("BundleConfig.pb") is not null
            ? new VerificationCheck("Bundle config", VerificationStatus.Passed, "BundleConfig.pb present")
            : new VerificationCheck("Bundle config", VerificationStatus.Failed, "BundleConfig.pb is missing; bundletool cannot process this bundle."));
        var modules = zip.Entries.Select(e => e.FullName).Where(n => n.EndsWith("/manifest/AndroidManifest.xml", StringComparison.Ordinal))
            .Select(n => n[..n.IndexOf('/')]).Distinct().Order(StringComparer.Ordinal).ToList();
        var hasDex = zip.Entries.Any(e => e.FullName.StartsWith("base/dex/", StringComparison.Ordinal) && e.FullName.EndsWith(".dex", StringComparison.Ordinal));
        checks.Add(hasDex
            ? new VerificationCheck("Code", VerificationStatus.Passed, $"base module dex present; modules: {string.Join(", ", modules)}")
            : new VerificationCheck("Code", VerificationStatus.Failed, "base/dex/ contains no dex files."));
        var abis = modules.SelectMany(m => Abis(zip, m + "/lib/")).Distinct().ToList();
        var cert = V1Certificate(zip);
        var signed = cert is not null;
        var state = !signed ? MobileSigningState.Unsigned : cert!.Subject.Contains("CN=Android Debug", StringComparison.Ordinal) ? MobileSigningState.DebugSigned : MobileSigningState.ReleaseSigned;
        checks.Add(state switch
        {
            MobileSigningState.Unsigned => new VerificationCheck("Signature", VerificationStatus.PassedWithWarnings, "Unsigned bundle. Google Play requires the bundle to be signed with your upload key."),
            MobileSigningState.DebugSigned => new VerificationCheck("Signature", VerificationStatus.PassedWithWarnings, "Signed with the Android debug key — Google Play rejects debug-signed bundles."),
            _ => new VerificationCheck("Signature", VerificationStatus.Passed, $"JAR-signed by {cert!.Subject} (upload key)."),
        });
        return new MobileArtifactReport
        {
            Kind = MobileArtifactKind.Aab, Path = path, SizeBytes = fs.Length,
            PackageId = root.Attributes.GetValueOrDefault("package"),
            VersionName = root.Attributes.GetValueOrDefault("versionName"),
            VersionCode = root.Attributes.GetValueOrDefault("versionCode"),
            MinimumOs = sdk?.Attributes.GetValueOrDefault("minSdkVersion"),
            TargetSdk = sdk?.Attributes.GetValueOrDefault("targetSdkVersion"),
            Architectures = abis.Count > 0 ? abis : ["any (no native code)"],
            Signing = state, SigningSchemes = signed ? ["JAR (v1)"] : [], Signer = cert?.Subject,
            Platform = "android", Modules = modules,
            IsDistributable = state == MobileSigningState.ReleaseSigned,
            Notes = ["An App Bundle is a publishing format; devices install APKs generated from it (by Google Play or bundletool)."],
            Checks = checks,
        };
    }

    private static MobileArtifactReport InspectApkSet(string path, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        using var zip = OpenZip(fs);
        var checks = new List<VerificationCheck> { ZipIntegrity.Check(zip, ct) };
        var apks = zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.Ordinal)).Select(e => e.FullName).ToList();
        checks.Add(apks.Count > 0
            ? new VerificationCheck("APK set", VerificationStatus.Passed, $"{apks.Count} APK(s){(zip.GetEntry("toc.pb") is not null ? ", table of contents present" : "")}")
            : new VerificationCheck("APK set", VerificationStatus.Failed, "The APK set contains no APKs."));
        var universal = apks.Contains("universal.apk");
        return new MobileArtifactReport
        {
            Kind = MobileArtifactKind.ApkSet, Path = path, SizeBytes = fs.Length, Platform = "android", Checks = checks,
            Modules = apks,
            Notes = [universal ? "Contains universal.apk, installable on any compatible device once signed." : "Split APKs: install with `bundletool install-apks` or `adb install-multiple`."],
        };
    }

    private static List<string> Abis(ZipArchive zip, string prefix) =>
        zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".so", StringComparison.Ordinal))
            .Select(n => n[prefix.Length..].Split('/')[0]).Distinct().Order(StringComparer.Ordinal).ToList();

    private static System.Security.Cryptography.X509Certificates.X509Certificate2? V1Certificate(ZipArchive zip)
    {
        var block = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
            (e.FullName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)));
        return block is null ? null : ApkSignature.FromPkcs7(ZipIntegrity.ReadEntry(zip, block.FullName, 1024 * 1024) ?? []);
    }

    // ───────────── iOS ─────────────

    private static MobileArtifactReport InspectIpa(string path, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        using var zip = OpenZip(fs);
        var checks = new List<VerificationCheck> { ZipIntegrity.Check(zip, ct) };
        var apps = zip.Entries.Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n.StartsWith("Payload/", StringComparison.Ordinal) && n.Count(c => c == '/') >= 2)
            .Select(n => n.Split('/')[1]).Where(n => n.EndsWith(".app", StringComparison.Ordinal)).Distinct().ToList();
        if (apps.Count != 1) throw new InvalidDataException(apps.Count == 0 ? "Payload/<App>.app is missing (not an IPA)." : "The IPA contains more than one application bundle.");
        var appDir = $"Payload/{apps[0]}/";
        checks.Add(new VerificationCheck("Payload", VerificationStatus.Passed, appDir));
        var plist = PlistReader.ParseDictionary(ZipIntegrity.ReadEntry(zip, appDir + "Info.plist") ?? throw new InvalidDataException("Info.plist is missing from the app bundle."));
        var exeName = plist.GetValueOrDefault("CFBundleExecutable") as string;
        var exeEntry = exeName is null ? null : zip.GetEntry(appDir + exeName);
        var archs = exeEntry is null ? [] : MachOArchitectures(ReadHead(exeEntry));
        checks.Add(exeEntry is null
            ? new VerificationCheck("Executable", VerificationStatus.Failed, $"CFBundleExecutable '{exeName}' not found in the bundle.")
            : archs.Count > 0 ? new VerificationCheck("Executable", VerificationStatus.Passed, $"Mach-O {string.Join(", ", archs)}")
            : new VerificationCheck("Executable", VerificationStatus.Failed, "The bundle executable is not a Mach-O binary."));
        var hasCodeResources = zip.GetEntry(appDir + "_CodeSignature/CodeResources") is not null;
        var profile = ParseProvisioningProfile(ZipIntegrity.ReadEntry(zip, appDir + "embedded.mobileprovision", 4 * 1024 * 1024));
        return IosReport(MobileArtifactKind.Ipa, path, fs.Length, plist, archs, hasCodeResources, profile, checks);
    }

    private static MobileArtifactReport InspectXcArchive(string path)
    {
        var info = System.IO.Path.Combine(path, "Info.plist");
        if (!File.Exists(info)) throw new InvalidDataException("Info.plist is missing (not an Xcode archive).");
        var archive = PlistReader.ParseDictionary(File.ReadAllBytes(info));
        var props = archive.GetValueOrDefault("ApplicationProperties") as Dictionary<string, object>
                    ?? throw new InvalidDataException("The archive has no ApplicationProperties (not an application archive).");
        var appRel = props.GetValueOrDefault("ApplicationPath") as string ?? throw new InvalidDataException("ApplicationPath missing.");
        var app = System.IO.Path.GetFullPath(System.IO.Path.Combine(path, "Products", appRel));
        if (!HCG.ExecutableMaker.Core.PathGuard.IsWithin(path, app) || !Directory.Exists(app))
            throw new InvalidDataException($"Products/{appRel} is missing from the archive.");
        var inner = InspectIosApp(app);
        var identity = props.GetValueOrDefault("SigningIdentity") as string;
        var checks = new List<VerificationCheck> { new("Archive", VerificationStatus.Passed, $"{System.IO.Path.GetFileName(path)} → Products/{appRel}") };
        checks.AddRange(inner.Checks);
        if (string.IsNullOrEmpty(identity) && inner.Signing is MobileSigningState.Unsigned or MobileSigningState.AdHocSigned)
            checks.Add(new VerificationCheck("Exportability", VerificationStatus.PassedWithWarnings, "Archive is not signed with an Apple certificate; it cannot be exported to a distributable IPA."));
        return inner with
        {
            Kind = MobileArtifactKind.XcArchive, Path = path, SizeBytes = DirectorySize(path),
            Signer = string.IsNullOrEmpty(identity) ? inner.Signer : identity,
            Checks = checks,
        };
    }

    private static MobileArtifactReport InspectIosApp(string path)
    {
        var plistPath = System.IO.Path.Combine(path, "Info.plist");
        if (!File.Exists(plistPath)) throw new InvalidDataException("Info.plist is missing (not an application bundle).");
        var plist = PlistReader.ParseDictionary(File.ReadAllBytes(plistPath));
        var exe = plist.GetValueOrDefault("CFBundleExecutable") as string;
        var exePath = exe is null ? null : System.IO.Path.Combine(path, exe);
        var archs = exePath is not null && File.Exists(exePath) ? MachOArchitectures(ReadHead(exePath)) : [];
        var checks = new List<VerificationCheck>
        {
            exePath is null || !File.Exists(exePath)
                ? new VerificationCheck("Executable", VerificationStatus.Failed, $"CFBundleExecutable '{exe}' not found.")
                : archs.Count > 0 ? new VerificationCheck("Executable", VerificationStatus.Passed, $"Mach-O {string.Join(", ", archs)}")
                : new VerificationCheck("Executable", VerificationStatus.Failed, "The bundle executable is not a Mach-O binary."),
        };
        var hasCodeResources = File.Exists(System.IO.Path.Combine(path, "_CodeSignature", "CodeResources"));
        var profilePath = System.IO.Path.Combine(path, "embedded.mobileprovision");
        var profile = ParseProvisioningProfile(File.Exists(profilePath) ? File.ReadAllBytes(profilePath) : null);
        IReadOnlyList<string> minos = [];
        if (exePath is not null && File.Exists(exePath))
        {
            try { minos = HCG.ExecutableMaker.Packaging.Verification.AppleMachO.Read(exePath).Select(sl => sl.MinimumOS).OfType<string>().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException) { }
        }
        return IosReport(MobileArtifactKind.IosApp, path, DirectorySize(path), plist, archs, hasCodeResources, profile, checks) with { BinaryMinimumOs = minos };
    }

    public sealed record ProvisioningProfile(string? Name, string? TeamName, string? TeamId, DateTime? Expiration, bool HasDevices, bool AllDevices, bool GetTaskAllow);

    /// <summary>Extracts the XML plist embedded in a CMS-signed .mobileprovision (signature is not validated here).</summary>
    public static ProvisioningProfile? ParseProvisioningProfile(byte[]? data)
    {
        if (data is null) return null;
        var text = Encoding.Latin1.GetString(data);
        var start = text.IndexOf("<?xml", StringComparison.Ordinal);
        var end = text.IndexOf("</plist>", StringComparison.Ordinal);
        if (start < 0 || end < start) return null;
        try
        {
            var plist = PlistReader.ParseDictionary(Encoding.UTF8.GetBytes(text[start..(end + 8)]));
            var ent = plist.GetValueOrDefault("Entitlements") as Dictionary<string, object>;
            return new ProvisioningProfile(
                plist.GetValueOrDefault("Name") as string,
                plist.GetValueOrDefault("TeamName") as string,
                (plist.GetValueOrDefault("TeamIdentifier") as List<object>)?.FirstOrDefault() as string,
                plist.GetValueOrDefault("ExpirationDate") as DateTime?,
                plist.ContainsKey("ProvisionedDevices"),
                plist.GetValueOrDefault("ProvisionsAllDevices") is true,
                ent?.GetValueOrDefault("get-task-allow") is true);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { return null; }
    }

    private static MobileArtifactReport IosReport(MobileArtifactKind kind, string path, long size, Dictionary<string, object> plist,
        IReadOnlyList<string> archs, bool hasCodeResources, ProvisioningProfile? profile, List<VerificationCheck> checks)
    {
        var platform = plist.GetValueOrDefault("DTPlatformName") as string;
        var supported = (plist.GetValueOrDefault("CFBundleSupportedPlatforms") as List<object>)?.OfType<string>().ToList() ?? [];
        platform ??= supported.FirstOrDefault();
        var simulator = platform == "iphonesimulator";
        var bundleId = plist.GetValueOrDefault("CFBundleIdentifier") as string;
        checks.Add(bundleId is null
            ? new VerificationCheck("Info.plist", VerificationStatus.Failed, "CFBundleIdentifier is missing.")
            : new VerificationCheck("Info.plist", VerificationStatus.Passed, $"{bundleId} {plist.GetValueOrDefault("CFBundleShortVersionString")} ({plist.GetValueOrDefault("CFBundleVersion")})"));
        if (simulator)
            checks.Add(new VerificationCheck("Platform", VerificationStatus.PassedWithWarnings, "iOS Simulator build — runs only in the Simulator; NOT installable on devices and NOT distributable."));
        else if (platform is not null)
            checks.Add(new VerificationCheck("Platform", VerificationStatus.Passed, $"Device build ({platform})."));

        var state = profile is not null
            ? profile.GetTaskAllow ? MobileSigningState.DevelopmentSigned : MobileSigningState.DistributionSigned
            : hasCodeResources ? MobileSigningState.AdHocSigned : MobileSigningState.Unsigned;
        var distribution = profile is null ? null
            : profile.AllDevices ? "enterprise" : profile.HasDevices ? (profile.GetTaskAllow ? "development" : "ad hoc / release testing") : "App Store Connect";
        checks.Add(state switch
        {
            MobileSigningState.Unsigned => new VerificationCheck("Code signature", simulator ? VerificationStatus.PassedWithWarnings : VerificationStatus.PassedWithWarnings,
                "Not code-signed. iOS devices refuse to run unsigned apps."),
            MobileSigningState.AdHocSigned => new VerificationCheck("Code signature", VerificationStatus.PassedWithWarnings,
                simulator ? "Ad-hoc signed for the Simulator (normal for Simulator builds)." : "Code-signed without a provisioning profile — not installable on devices."),
            _ => new VerificationCheck("Code signature", VerificationStatus.Passed,
                $"Signed with provisioning profile '{profile!.Name}' ({distribution}) for team {profile.TeamName ?? profile.TeamId}" +
                (profile.Expiration is { } exp ? $", expires {exp:yyyy-MM-dd}" : "") + ". Run codesign --verify on a Mac to verify cryptographically."),
        });
        if (profile?.Expiration is { } expiry && expiry < DateTime.UtcNow)
            checks.Add(new VerificationCheck("Provisioning", VerificationStatus.Failed, $"The embedded provisioning profile expired on {expiry:yyyy-MM-dd}."));

        return new MobileArtifactReport
        {
            Kind = kind, Path = path, SizeBytes = size,
            PackageId = bundleId,
            VersionName = plist.GetValueOrDefault("CFBundleShortVersionString") as string,
            VersionCode = plist.GetValueOrDefault("CFBundleVersion")?.ToString(),
            MinimumOs = plist.GetValueOrDefault("MinimumOSVersion") as string,
            Architectures = archs,
            Signing = state,
            Signer = profile is null ? null : $"{profile.TeamName} ({profile.TeamId})",
            SigningSchemes = distribution is null ? [] : [distribution],
            Platform = platform,
            IsDistributable = !simulator && state is MobileSigningState.DistributionSigned,
            Checks = checks,
            Notes = simulator ? ["Simulator builds cannot be converted into device builds; build an archive instead."] : [],
        };
    }

    /// <summary>Architectures of a thin or universal (fat) Mach-O binary.</summary>
    public static IReadOnlyList<string> MachOArchitectures(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8) return [];
        static string Cpu(uint cpu) => cpu switch { 0x0100000C => "arm64", 0x01000007 => "x86_64", 0x0000000C => "armv7", 0x00000007 => "i386", _ => $"cpu 0x{cpu:x}" };
        var magicLe = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (magicLe is 0xFEEDFACF or 0xFEEDFACE) return [Cpu(BinaryPrimitives.ReadUInt32LittleEndian(head[4..]))];
        var magicBe = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (magicBe is 0xCAFEBABE or 0xCAFEBABF)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            var entry = magicBe == 0xCAFEBABE ? 20 : 32;
            if (count is 0 or > 16 || 8 + count * entry > head.Length) return [];
            var archs = new List<string>((int)count);
            for (var i = 0; i < (int)count; i++) archs.Add(Cpu(BinaryPrimitives.ReadUInt32BigEndian(head[(8 + i * entry)..])));
            return archs;
        }
        return [];
    }

    private static byte[] ReadHead(string file)
    {
        using var s = File.OpenRead(file);
        var buffer = new byte[Math.Min(4096, s.Length)];
        s.ReadExactly(buffer);
        return buffer;
    }

    private static byte[] ReadHead(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        var buffer = new byte[(int)Math.Min(4096, entry.Length)];
        s.ReadExactly(buffer);
        return buffer;
    }

    private static ZipArchive OpenZip(Stream s)
    {
        try { return new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex) { throw new InvalidDataException("Not a valid ZIP container: " + ex.Message, ex); }
    }

    private static long DirectorySize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
}
