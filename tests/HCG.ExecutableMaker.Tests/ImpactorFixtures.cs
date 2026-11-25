using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HCG.Impactor.Devices;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// SYNTHETIC iOS app packages for HCG Impactor's automated tests. The Mach-O headers, embedded code signatures
/// (CodeDirectory + entitlements + CMS), sealed resources and provisioning profiles are built byte-for-byte in the
/// formats Apple uses, signed by the synthetic test root — they exercise HCG's parsing and rules, not Apple's trust.
/// Physical-device validation is separate (docs/IMPACTOR.md › Physical device validation).
/// </summary>
public static class ImpactorFixtures
{
    public const string AppId = "com.example.app";
    public const string DeviceUdid = "00008140-001A2B3C4D5E001E";
    public const uint PlatformIos = 2, PlatformSimulator = 7;

    public sealed record MachOSpec
    {
        public uint CpuType { get; init; } = 0x0100000C; // arm64
        public uint CpuSubtype { get; init; }
        public uint Platform { get; init; } = PlatformIos;
        public string MinOs { get; init; } = "16.0";
        public bool Encrypted { get; init; }
        public byte[]? Signature { get; init; }
    }

    public static byte[] MachO(MachOSpec s)
    {
        var cmds = new List<byte[]>();
        var buildVersion = new byte[24];
        W(buildVersion, 0, 0x32); W(buildVersion, 4, 24); W(buildVersion, 8, s.Platform); W(buildVersion, 12, Version(s.MinOs)); W(buildVersion, 16, Version("18.0"));
        cmds.Add(buildVersion);
        var enc = new byte[24];
        W(enc, 0, 0x2C); W(enc, 4, 24); W(enc, 8, 0x4000); W(enc, 12, 0x1000); W(enc, 16, s.Encrypted ? 1u : 0u);
        cmds.Add(enc);
        const int sigOffset = 0x4000;
        if (s.Signature is not null)
        {
            var cs = new byte[16];
            W(cs, 0, 0x1D); W(cs, 4, 16); W(cs, 8, sigOffset); W(cs, 12, (uint)s.Signature.Length);
            cmds.Add(cs);
        }
        var sizeofcmds = cmds.Sum(c => c.Length);
        var header = new byte[32];
        W(header, 0, 0xFEEDFACF); W(header, 4, s.CpuType); W(header, 8, s.CpuSubtype); W(header, 12, 2); W(header, 16, (uint)cmds.Count); W(header, 20, (uint)sizeofcmds);
        var file = new byte[s.Signature is null ? 0x4000 : sigOffset + s.Signature.Length];
        header.CopyTo(file, 0);
        var pos = 32;
        foreach (var c in cmds) { c.CopyTo(file, pos); pos += c.Length; }
        s.Signature?.CopyTo(file, sigOffset);
        return file;
    }

    /// <summary>An embedded signature SuperBlob: CodeDirectory (identifier, team), optional entitlements, optional CMS by <paramref name="signer"/>.</summary>
    public static byte[] Signature(string identifier, string? team, X509Certificate2? signer, string? entitlementsXml, DateTimeOffset? signingTime = null)
    {
        var cd = CodeDirectory(identifier, team, adhoc: signer is null);
        var blobs = new List<(uint Type, byte[] Data)> { (0, cd) };
        if (entitlementsXml is not null) blobs.Add((5, Blob(0xFADE7171, Encoding.UTF8.GetBytes(entitlementsXml))));
        if (signer is not null)
        {
            var cms = new SignedCms(new ContentInfo(cd), detached: true);
            var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signer) { IncludeOption = X509IncludeOption.WholeChain };
            cmsSigner.SignedAttributes.Add(new Pkcs9SigningTime((signingTime ?? SigningFixtures.Now).UtcDateTime));
            cms.ComputeSignature(cmsSigner);
            blobs.Add((0x10000, Blob(0xFADE0B01, cms.Encode())));
        }
        var headerLen = 12 + blobs.Count * 8;
        var total = headerLen + blobs.Sum(b => b.Data.Length);
        var sb = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(sb, 0xFADE0CC0);
        BinaryPrimitives.WriteUInt32BigEndian(sb.AsSpan(4), (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(sb.AsSpan(8), (uint)blobs.Count);
        var off = headerLen;
        for (var i = 0; i < blobs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(sb.AsSpan(12 + i * 8), blobs[i].Type);
            BinaryPrimitives.WriteUInt32BigEndian(sb.AsSpan(16 + i * 8), (uint)off);
            blobs[i].Data.CopyTo(sb, off);
            off += blobs[i].Data.Length;
        }
        return sb;
    }

    private static byte[] CodeDirectory(string identifier, string? team, bool adhoc)
    {
        var id = Encoding.UTF8.GetBytes(identifier + "\0");
        var tm = team is null ? [] : Encoding.UTF8.GetBytes(team + "\0");
        const int fixedLen = 52;
        var len = fixedLen + id.Length + tm.Length;
        var cd = new byte[len];
        void Be(int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(cd.AsSpan(o), v);
        Be(0, 0xFADE0C02); Be(4, (uint)len); Be(8, 0x20400); Be(12, adhoc ? 0x2u : 0u); Be(16, (uint)len); Be(20, fixedLen);
        cd[36] = 32; cd[37] = 2; cd[39] = 12;
        Be(48, team is null ? 0u : (uint)(fixedLen + id.Length));
        id.CopyTo(cd, fixedLen);
        tm.CopyTo(cd, fixedLen + id.Length);
        return cd;
    }

    private static byte[] Blob(uint magic, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, magic);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)b.Length);
        payload.CopyTo(b, 8);
        return b;
    }

    public static string Entitlements(string team, string bundleId, bool getTaskAllow = true, params (string Key, string Xml)[] extra)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>");
        sb.Append($"<key>application-identifier</key><string>{team}.{bundleId}</string>");
        sb.Append($"<key>com.apple.developer.team-identifier</key><string>{team}</string>");
        sb.Append($"<key>get-task-allow</key>{(getTaskAllow ? "<true/>" : "<false/>")}");
        sb.Append($"<key>keychain-access-groups</key><array><string>{team}.{bundleId}</string></array>");
        foreach (var (k, x) in extra) sb.Append($"<key>{k}</key>{x}");
        return sb.Append("</dict></plist>").ToString();
    }

    public static string InfoPlist(string bundleId, string exe, string name, string version = "1.2.0", string build = "42", string minOs = "16.0", string? family = "<integer>1</integer><integer>2</integer>", string? extensionPoint = null, string packageType = "APPL")
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>");
        sb.Append($"<key>CFBundleIdentifier</key><string>{bundleId}</string><key>CFBundleExecutable</key><string>{exe}</string>");
        sb.Append($"<key>CFBundleName</key><string>{name}</string><key>CFBundleDisplayName</key><string>{name}</string>");
        sb.Append($"<key>CFBundleShortVersionString</key><string>{version}</string><key>CFBundleVersion</key><string>{build}</string>");
        sb.Append($"<key>CFBundlePackageType</key><string>{packageType}</string><key>MinimumOSVersion</key><string>{minOs}</string>");
        if (family is not null) sb.Append($"<key>UIDeviceFamily</key><array>{family}</array>");
        sb.Append("<key>UIRequiredDeviceCapabilities</key><array><string>arm64</string></array>");
        if (extensionPoint is not null) sb.Append($"<key>NSExtension</key><dict><key>NSExtensionPointIdentifier</key><string>{extensionPoint}</string></dict>");
        return sb.Append("</dict></plist>").ToString();
    }

    public sealed record IpaSpec
    {
        public string BundleId { get; init; } = AppId;
        public string Team { get; init; } = SigningFixtures.Team;
        public X509Certificate2? Signer { get; init; }
        public byte[]? EmbeddedProfile { get; init; }
        public MachOSpec Main { get; init; } = new();
        public bool WithExtension { get; init; } = true;
        public bool WithFramework { get; init; } = true;
        public string MinOs { get; init; } = "16.0";
        public string? Family { get; init; } = "<integer>1</integer><integer>2</integer>";
        /// <summary>Modify a sealed resource after "signing" (integrity check).</summary>
        public bool TamperResource { get; init; }
        /// <summary>Extra raw zip entries (name → bytes) — e.g. unsafe paths.</summary>
        public IReadOnlyDictionary<string, byte[]> RawEntries { get; init; } = new Dictionary<string, byte[]>();
        public bool OmitPayload { get; init; }
        public (string Key, string Xml)[] ExtraEntitlements { get; init; } = [];
    }

    /// <summary>Writes a synthetic IPA and returns its path.</summary>
    public static string Ipa(TempDir dir, IpaSpec s, string name = "Synthetic.ipa")
    {
        var path = dir.Combine(name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var app = s.OmitPayload ? "Synthetic.app/" : "Payload/Synthetic.app/";
        var files = new Dictionary<string, byte[]>();
        byte[] Exe(string id, MachOSpec spec, bool entitled) => MachO(spec with
        {
            Signature = s.Signer is null && spec.Signature is null ? null
                : spec.Signature ?? Signature(id, s.Team, s.Signer, entitled ? Entitlements(s.Team, id, true, s.ExtraEntitlements) : null),
        });

        files["Info.plist"] = Encoding.UTF8.GetBytes(InfoPlist(s.BundleId, "Synthetic", "Synthetic", minOs: s.MinOs, family: s.Family));
        files["Synthetic"] = Exe(s.BundleId, s.Main, true);
        files["Assets.car"] = Encoding.UTF8.GetBytes("synthetic asset catalog");
        files["en.lproj/Main.strings"] = Encoding.UTF8.GetBytes("\"Hello\" = \"Hello\";");
        if (s.EmbeddedProfile is not null) files["embedded.mobileprovision"] = s.EmbeddedProfile;
        if (s.WithExtension)
        {
            var extId = s.BundleId + ".share";
            files["PlugIns/Share.appex/Info.plist"] = Encoding.UTF8.GetBytes(InfoPlist(extId, "Share", "Share", minOs: s.MinOs, extensionPoint: "com.apple.share-services", packageType: "XPC!"));
            files["PlugIns/Share.appex/Share"] = Exe(extId, new MachOSpec { MinOs = s.MinOs }, true);
            if (s.EmbeddedProfile is not null) files["PlugIns/Share.appex/embedded.mobileprovision"] = s.EmbeddedProfile;
        }
        if (s.WithFramework)
        {
            files["Frameworks/Lib.framework/Info.plist"] = Encoding.UTF8.GetBytes(InfoPlist("com.example.lib", "Lib", "Lib", packageType: "FMWK", family: null));
            files["Frameworks/Lib.framework/Lib"] = Exe("com.example.lib", new MachOSpec { MinOs = s.MinOs }, false);
            files["Frameworks/libswiftSynthetic.dylib"] = Exe("libswiftSynthetic", new MachOSpec { MinOs = s.MinOs }, false);
        }
        // Seal the app's resources (CodeResources files2 with SHA-1 "hash" and SHA-256 "hash2"), like codesign does.
        if (s.Signer is not null)
        {
            var sealedFiles = files.Keys.Where(k => k is not ("Info.plist" or "Synthetic") && !k.StartsWith("PlugIns/", StringComparison.Ordinal) && !k.StartsWith("Frameworks/Lib.framework/", StringComparison.Ordinal)).ToList();
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>files2</key><dict>");
            foreach (var k in sealedFiles.OrderBy(k => k, StringComparer.Ordinal))
                sb.Append($"<key>{k}</key><dict><key>hash</key><data>{Convert.ToBase64String(SHA1.HashData(files[k]))}</data><key>hash2</key><data>{Convert.ToBase64String(SHA256.HashData(files[k]))}</data></dict>");
            if (s.WithExtension) sb.Append("<key>PlugIns/Share.appex</key><dict><key>cdhash</key><data>AAAA</data><key>requirement</key><string>synthetic</string></dict>");
            if (s.WithFramework) sb.Append("<key>Frameworks/Lib.framework</key><dict><key>cdhash</key><data>AAAA</data><key>requirement</key><string>synthetic</string></dict>");
            sb.Append("</dict></dict></plist>");
            files["_CodeSignature/CodeResources"] = Encoding.UTF8.GetBytes(sb.ToString());
            if (s.TamperResource) files["Assets.car"] = Encoding.UTF8.GetBytes("changed after signing");
        }
        foreach (var (k, v) in files)
        {
            var e = zip.CreateEntry(app + k);
            if (k is "Synthetic" || k.EndsWith("/Share", StringComparison.Ordinal) || k.EndsWith("/Lib", StringComparison.Ordinal) || k.EndsWith(".dylib", StringComparison.Ordinal))
                e.ExternalAttributes = unchecked((int)(0x81EDu << 16)); // regular file, 0755
            using var w = e.Open();
            w.Write(v);
        }
        foreach (var (k, v) in s.RawEntries)
        {
            using var w = zip.CreateEntry(k).Open();
            w.Write(v);
        }
        return path;
    }

    /// <summary>A profile for the synthetic app covering <paramref name="appId"/> (team-prefixed), with these certificates and devices.</summary>
    public static SigningFixtures.ProfileSpec Profile(string appId, X509Certificate2 cert, IReadOnlyList<string>? devices = null, bool dev = true, DateTimeOffset? expires = null, string name = "Synthetic Development") => new()
    {
        Name = name,
        AppId = SigningFixtures.Team + "." + appId,
        Certificates = [cert],
        Devices = devices ?? [DeviceUdid],
        GetTaskAllow = dev,
        Expires = expires ?? SigningFixtures.Now.AddMonths(6),
        Entitlements = new Dictionary<string, object> { ["keychain-access-groups"] = new[] { SigningFixtures.Team + ".*" } },
    };

    public static AppleDevice Device(string? udid = DeviceUdid, string os = "18.2", string cls = "iPhone", DeviceConnection connection = DeviceConnection.Usb,
        DeveloperModeState devMode = DeveloperModeState.Enabled, PairingState pairing = PairingState.Paired) => new()
    {
        Identifier = "5E1F2A3B-0000-4000-8000-00000000A11D",
        Udid = udid,
        Name = "Synthetic iPhone",
        ProductType = "iPhone17,1",
        MarketingName = "iPhone 16 Pro",
        DeviceClass = cls,
        Platform = "iOS",
        OsVersion = os,
        OsBuild = "22C152",
        CpuArchitecture = "arm64e",
        Connection = connection,
        Transport = connection == DeviceConnection.Usb ? "wired" : "localNetwork",
        Pairing = pairing,
        DeveloperMode = devMode,
        DeveloperServicesAvailable = true,
        TunnelState = "connected",
        Capabilities = [AppleDeviceCapabilities.InstallApp, AppleDeviceCapabilities.LaunchApp, AppleDeviceCapabilities.ListApps],
        Source = "synthetic",
    };

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);

    private static uint Version(string v)
    {
        var p = v.Split('.').Select(uint.Parse).Concat([0u, 0u]).ToArray();
        return (p[0] << 16) | (p[1] << 8) | p[2];
    }
}
