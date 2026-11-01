using System.Buffers.Binary;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Synthetic mobile artifacts built byte-for-byte in the test (binary XML, protobuf manifests, APK Signing Blocks,
/// PKCS#7, Mach-O, plists), so the inspector is exercised on every OS without the Android SDK or Xcode.
/// Real artifacts produced by Gradle/xcodebuild are verified in the CI mobile jobs.
/// </summary>
public class MobileArtifactTests
{
    // ───────────── Android APK ─────────────

    [Fact]
    public void Debug_signed_apk_is_reported_with_manifest_metadata_and_v2_scheme()
    {
        using var dir = new TempDir();
        var apk = MobileSamples.Apk(dir.Combine("app-debug.apk"), debuggable: true, signer: MobileSamples.Certificate("CN=Android Debug, O=Android, C=US"),
            abis: ["arm64-v8a", "x86_64"]);

        var r = MobileArtifactInspector.Inspect(apk);

        Assert.Equal(MobileArtifactKind.Apk, r.Kind);
        Assert.Equal("com.hakmicapital.hello", r.PackageId);
        Assert.Equal("1.2.0", r.VersionName);
        Assert.Equal("3", r.VersionCode);
        Assert.Equal("24", r.MinimumOs);
        Assert.Equal("35", r.TargetSdk);
        Assert.Equal(["arm64-v8a", "x86_64"], r.Architectures);
        Assert.Equal(MobileSigningState.DebugSigned, r.Signing);
        Assert.Contains("v2", r.SigningSchemes);
        Assert.False(r.IsDistributable);
        Assert.Contains(r.Checks, c => c.Name == "Debuggable");
        Assert.Contains(r.Checks, c => c.Name == "Archive integrity" && c.Status == VerificationStatus.Passed);
        Assert.Equal(VerificationStatus.PassedWithWarnings, r.Status);
    }

    [Fact]
    public void Release_signed_apk_with_v1_and_v2_is_distributable()
    {
        using var dir = new TempDir();
        var cert = MobileSamples.Certificate("CN=Hakmi Capital Group Release, O=Hakmi Capital Group");
        var apk = MobileSamples.Apk(dir.Combine("app-release.apk"), debuggable: false, signer: cert, v1: true);

        var r = MobileArtifactInspector.Inspect(apk);

        Assert.Equal(MobileSigningState.ReleaseSigned, r.Signing);
        Assert.Equal(["v1 (JAR)", "v2"], r.SigningSchemes);
        Assert.Contains("Hakmi Capital Group Release", r.Signer);
        Assert.True(r.IsDistributable);
        Assert.Equal(["any (no native code)"], r.Architectures);
        Assert.Equal(VerificationStatus.Passed, r.Status);
    }

    [Fact]
    public void Unsigned_apk_is_flagged_and_never_distributable()
    {
        using var dir = new TempDir();
        var r = MobileArtifactInspector.Inspect(MobileSamples.Apk(dir.Combine("app-release-unsigned.apk"), debuggable: false, signer: null));
        Assert.Equal(MobileSigningState.Unsigned, r.Signing);
        Assert.Empty(r.SigningSchemes);
        Assert.False(r.IsDistributable);
        Assert.Contains(r.Checks, c => c.Name == "Signature" && c.Status == VerificationStatus.PassedWithWarnings && c.Detail.Contains("Unsigned"));
    }

    [Fact]
    public void Apk_without_dex_fails_code_check()
    {
        using var dir = new TempDir();
        var r = MobileArtifactInspector.Inspect(MobileSamples.Apk(dir.Combine("nodex.apk"), debuggable: false, signer: null, includeDex: false));
        Assert.Contains(r.Checks, c => c.Name == "Code" && c.Status == VerificationStatus.Failed);
        Assert.Equal(VerificationStatus.Failed, r.Status);
    }

    [Fact]
    public void Corrupt_apk_reports_structure_failure_instead_of_throwing()
    {
        using var dir = new TempDir();
        var path = dir.Combine("broken.apk");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(4096));
        var r = MobileArtifactInspector.Inspect(path);
        Assert.Equal(VerificationStatus.Failed, r.Status);
        Assert.Contains(r.Checks, c => c.Name == "Structure");
    }

    [Fact]
    public void Zip_without_manifest_is_not_an_apk()
    {
        using var dir = new TempDir();
        var path = Zips.Create(dir.Combine("renamed.apk"), ("hello.txt", "renaming a file is not conversion", null));
        var r = MobileArtifactInspector.Inspect(path);
        Assert.Equal(VerificationStatus.Failed, r.Status);
        Assert.Contains("AndroidManifest.xml is missing", r.Checks.Single().Detail);
    }

    [Fact]
    public void Truncated_binary_manifest_is_rejected()
    {
        using var dir = new TempDir();
        var manifest = MobileSamples.BinaryManifest(debuggable: false);
        var path = dir.Combine("trunc.apk");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("AndroidManifest.xml").Open();
            s.Write(manifest, 0, manifest.Length / 2);
        }
        Assert.Equal(VerificationStatus.Failed, MobileArtifactInspector.Inspect(path).Status);
    }

    [Fact]
    public void Path_traversal_entry_fails_archive_integrity()
    {
        using var dir = new TempDir();
        var path = MobileSamples.Apk(dir.Combine("evil.apk"), debuggable: false, signer: null, extraEntries: [("../../evil.sh", "rm -rf ~")]);
        var r = MobileArtifactInspector.Inspect(path);
        Assert.Contains(r.Checks, c => c.Name == "Archive integrity" && c.Status == VerificationStatus.Failed && c.Detail.Contains("traversal"));
        Assert.False(File.Exists(Path.Combine(dir.Path, "..", "..", "evil.sh")));
    }

    [Fact]
    public void Crc_mismatch_is_detected()
    {
        using var dir = new TempDir();
        var path = dir.Combine("crc.apk");
        var payload = Encoding.ASCII.GetBytes("HCG-PAYLOAD-" + new string('x', 200));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var manifest = MobileSamples.BinaryManifest(debuggable: false);
            using (var s = zip.CreateEntry("AndroidManifest.xml").Open()) s.Write(manifest);
            using (var s = zip.CreateEntry("classes.dex", CompressionLevel.NoCompression).Open()) s.Write(payload);
        }
        var bytes = File.ReadAllBytes(path);
        var at = bytes.AsSpan().IndexOf("HCG-PAYLOAD-"u8);
        bytes[at + 20] ^= 0xFF; // flip a stored byte; the recorded CRC no longer matches
        File.WriteAllBytes(path, bytes);

        using var fs = File.OpenRead(path);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
        var check = ZipIntegrity.Check(archive);
        Assert.Equal(VerificationStatus.Failed, check.Status);
        Assert.Equal(VerificationStatus.Failed, MobileArtifactInspector.Inspect(path).Status);
    }

    [Fact]
    public void Binary_xml_decodes_stripped_attribute_names_through_resource_ids()
    {
        var elements = AndroidBinaryXml.Parse(MobileSamples.BinaryManifest(debuggable: true));
        var manifest = elements.Single(e => e.Name == "manifest");
        Assert.Equal("3", manifest.Attributes["versionCode"]); // name string is empty, resolved via resource id 0x0101021b
        Assert.Equal("true", elements.Single(e => e.Name == "application").Attributes["debuggable"]);
        Assert.Equal(1, elements.Single(e => e.Name == "uses-sdk").Depth);
    }

    [Fact]
    public void Binary_xml_rejects_non_axml_and_bad_chunks()
    {
        Assert.Throws<InvalidDataException>(() => AndroidBinaryXml.Parse("<manifest/>"u8.ToArray()));
        var data = MobileSamples.BinaryManifest(debuggable: false);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8 + 4), 0x7FFFFFF0); // string pool chunk size beyond the document
        Assert.Throws<InvalidDataException>(() => AndroidBinaryXml.Parse(data));
    }

    // ───────────── Android App Bundle / APK set ─────────────

    [Fact]
    public void Unsigned_aab_is_parsed_from_protobuf_manifest()
    {
        using var dir = new TempDir();
        var r = MobileArtifactInspector.Inspect(MobileSamples.Aab(dir.Combine("app-release.aab"), signer: null));
        Assert.Equal(MobileArtifactKind.Aab, r.Kind);
        Assert.Equal("com.hakmicapital.hello", r.PackageId);
        Assert.Equal("1.2.0", r.VersionName);
        Assert.Equal("3", r.VersionCode);
        Assert.Equal("24", r.MinimumOs);
        Assert.Equal(["base"], r.Modules);
        Assert.Equal(["arm64-v8a"], r.Architectures);
        Assert.Equal(MobileSigningState.Unsigned, r.Signing);
        Assert.False(r.IsDistributable);
        Assert.Contains(r.Notes, n => n.Contains("publishing format"));
    }

    [Fact]
    public void Aab_signed_with_debug_key_is_not_play_ready_and_upload_signed_aab_is()
    {
        using var dir = new TempDir();
        var debug = MobileArtifactInspector.Inspect(MobileSamples.Aab(dir.Combine("debug.aab"), MobileSamples.Certificate("CN=Android Debug, O=Android, C=US")));
        Assert.Equal(MobileSigningState.DebugSigned, debug.Signing);
        Assert.Contains(debug.Checks, c => c.Detail.Contains("rejects debug-signed"));
        var upload = MobileArtifactInspector.Inspect(MobileSamples.Aab(dir.Combine("upload.aab"), MobileSamples.Certificate("CN=HCG Upload")));
        Assert.Equal(MobileSigningState.ReleaseSigned, upload.Signing);
        Assert.True(upload.IsDistributable);
    }

    [Fact]
    public void Aab_without_bundle_config_fails()
    {
        using var dir = new TempDir();
        var r = MobileArtifactInspector.Inspect(MobileSamples.Aab(dir.Combine("nocfg.aab"), null, includeBundleConfig: false));
        Assert.Contains(r.Checks, c => c.Name == "Bundle config" && c.Status == VerificationStatus.Failed);
    }

    [Fact]
    public void Apk_set_lists_apks_and_identifies_universal_mode()
    {
        using var dir = new TempDir();
        var path = Zips.Create(dir.Combine("app.apks"), ("toc.pb", "x", null), ("universal.apk", "x", null));
        var r = MobileArtifactInspector.Inspect(path);
        Assert.Equal(["universal.apk"], r.Modules);
        Assert.Contains(r.Notes, n => n.Contains("universal.apk"));
        Assert.Equal(VerificationStatus.Passed, r.Status);
        var empty = MobileArtifactInspector.Inspect(Zips.Create(dir.Combine("empty.apks"), ("toc.pb", "x", null)));
        Assert.Equal(VerificationStatus.Failed, empty.Status);
    }

    [Fact]
    public void Pkcs7_reader_extracts_the_signer_certificate_and_rejects_garbage()
    {
        var cert = MobileSamples.Certificate("CN=PKCS7 Test");
        Assert.Equal(cert.Thumbprint, ApkSignature.FromPkcs7(MobileSamples.Pkcs7(cert))!.Thumbprint);
        Assert.Null(ApkSignature.FromPkcs7(RandomNumberGenerator.GetBytes(64)));
        Assert.Null(ApkSignature.FromPkcs7([]));
    }

    [RequiresJdkFact]
    public void Real_jarsigner_signature_is_read_from_an_app_bundle()
    {
        // Cross-checks the hand-written PKCS#7 path against the JDK's own signer (how AABs are signed).
        using var dir = new TempDir();
        var keystore = dir.Combine("test.keystore");
        JdkTools.Run("keytool", "-genkeypair", "-keystore", keystore, "-storepass", "hcgtest", "-keypass", "hcgtest", "-alias", "upload",
            "-keyalg", "RSA", "-keysize", "2048", "-validity", "2", "-dname", "CN=HCG Jarsigner Test, O=Hakmi Capital Group", "-noprompt");
        var aab = MobileSamples.Aab(dir.Combine("signed.aab"), signer: null);
        JdkTools.Run("jarsigner", "-keystore", keystore, "-storepass", "hcgtest", "-keypass", "hcgtest", aab, "upload");

        var r = MobileArtifactInspector.Inspect(aab);
        Assert.Equal(MobileSigningState.ReleaseSigned, r.Signing);
        Assert.Contains("CN=HCG Jarsigner Test", r.Signer);
        Assert.Contains(r.Checks, c => c.Name == "Archive integrity" && c.Status == VerificationStatus.Passed);
    }

    // ───────────── iOS ─────────────

    [Fact]
    public void Device_ipa_with_distribution_profile_is_reported_as_distribution_signed()
    {
        using var dir = new TempDir();
        var path = MobileSamples.Ipa(dir.Combine("Hello.ipa"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: true,
            profile: MobileSamples.Profile(getTaskAllow: false, devices: false, expires: DateTime.UtcNow.AddYears(1)));

        var r = MobileArtifactInspector.Inspect(path);

        Assert.Equal(MobileArtifactKind.Ipa, r.Kind);
        Assert.Equal("com.hakmicapital.helloios", r.PackageId);
        Assert.Equal("1.3.0", r.VersionName);
        Assert.Equal("7", r.VersionCode);
        Assert.Equal("16.0", r.MinimumOs);
        Assert.Equal(["arm64"], r.Architectures);
        Assert.Equal("iphoneos", r.Platform);
        Assert.Equal(MobileSigningState.DistributionSigned, r.Signing);
        Assert.Equal(["App Store Connect"], r.SigningSchemes);
        Assert.Contains("ABCDE12345", r.Signer);
        Assert.True(r.IsDistributable);
        Assert.Equal(VerificationStatus.Passed, r.Status);
    }

    [Fact]
    public void Ipa_with_development_profile_is_not_distributable_and_expired_profile_fails()
    {
        using var dir = new TempDir();
        var dev = MobileArtifactInspector.Inspect(MobileSamples.Ipa(dir.Combine("dev.ipa"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: true,
            profile: MobileSamples.Profile(getTaskAllow: true, devices: true, expires: DateTime.UtcNow.AddDays(30))));
        Assert.Equal(MobileSigningState.DevelopmentSigned, dev.Signing);
        Assert.Equal(["development"], dev.SigningSchemes);
        Assert.False(dev.IsDistributable);

        var expired = MobileArtifactInspector.Inspect(MobileSamples.Ipa(dir.Combine("old.ipa"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: true,
            profile: MobileSamples.Profile(getTaskAllow: false, devices: true, expires: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        Assert.Contains(expired.Checks, c => c.Name == "Provisioning" && c.Status == VerificationStatus.Failed);
    }

    [Fact]
    public void Unsigned_ipa_is_flagged()
    {
        using var dir = new TempDir();
        var r = MobileArtifactInspector.Inspect(MobileSamples.Ipa(dir.Combine("u.ipa"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: false, profile: null));
        Assert.Equal(MobileSigningState.Unsigned, r.Signing);
        Assert.False(r.IsDistributable);
        Assert.Equal(VerificationStatus.PassedWithWarnings, r.Status);
    }

    [Fact]
    public void Binary_plist_info_is_supported()
    {
        using var dir = new TempDir();
        var r = MobileArtifactInspector.Inspect(MobileSamples.Ipa(dir.Combine("b.ipa"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: false, profile: null, binaryPlist: true));
        Assert.Equal("com.hakmicapital.helloios", r.PackageId);
        Assert.Equal("1.3.0", r.VersionName);
        Assert.Equal("iphoneos", r.Platform);
    }

    [Fact]
    public void Ipa_without_payload_or_with_non_macho_executable_fails()
    {
        using var dir = new TempDir();
        var noPayload = MobileArtifactInspector.Inspect(Zips.Create(dir.Combine("x.ipa"), ("Hello.app/Info.plist", "<plist/>", null)));
        Assert.Contains("Payload", noPayload.Checks.Single().Detail);
        var script = MobileArtifactInspector.Inspect(MobileSamples.Ipa(dir.Combine("s.ipa"), "iphoneos", "#!/bin/sh\necho hi\n"u8.ToArray(), signed: false, profile: null));
        Assert.Contains(script.Checks, c => c.Name == "Executable" && c.Status == VerificationStatus.Failed);
    }

    [Fact]
    public void Simulator_app_and_unsigned_xcarchive_are_never_distributable()
    {
        using var dir = new TempDir();
        var app = MobileSamples.AppBundle(dir.Combine("Debug-iphonesimulator", "HelloIOS.app"), "iphonesimulator", MobileSamples.Fat(0x0100000C, 0x01000007), signed: true);
        var sim = MobileArtifactInspector.Inspect(app);
        Assert.Equal(MobileArtifactKind.IosApp, sim.Kind);
        Assert.Equal(["arm64", "x86_64"], sim.Architectures);
        Assert.Equal(MobileSigningState.AdHocSigned, sim.Signing);
        Assert.False(sim.IsDistributable);
        Assert.Contains(sim.Checks, c => c.Name == "Platform" && c.Detail.Contains("NOT distributable"));

        var archive = dir.Combine("HelloIOS.xcarchive");
        MobileSamples.AppBundle(Path.Combine(archive, "Products", "Applications", "HelloIOS.app"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: false);
        File.WriteAllText(Path.Combine(archive, "Info.plist"), MobileSamples.XmlPlist(new()
        {
            ["ApplicationProperties"] = new Dictionary<string, object> { ["ApplicationPath"] = "Applications/HelloIOS.app", ["CFBundleIdentifier"] = "com.hakmicapital.helloios" },
        }));
        var xc = MobileArtifactInspector.Inspect(archive);
        Assert.Equal(MobileArtifactKind.XcArchive, xc.Kind);
        Assert.Equal("com.hakmicapital.helloios", xc.PackageId);
        Assert.Contains(xc.Checks, c => c.Name == "Exportability" && c.Detail.Contains("cannot be exported"));
        Assert.False(xc.IsDistributable);
    }

    [Fact]
    public void Xcarchive_application_path_cannot_escape_the_archive()
    {
        using var dir = new TempDir();
        MobileSamples.AppBundle(dir.Combine("Outside.app"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: false);
        var archive = dir.Combine("Evil.xcarchive");
        Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(archive, "Info.plist"), MobileSamples.XmlPlist(new()
        {
            ["ApplicationProperties"] = new Dictionary<string, object> { ["ApplicationPath"] = "../../Outside.app" },
        }));
        var r = MobileArtifactInspector.Inspect(archive);
        Assert.Equal(VerificationStatus.Failed, r.Status);
    }

    [Fact]
    public void MachO_parser_handles_thin_fat_and_garbage()
    {
        Assert.Equal(["arm64"], MobileArtifactInspector.MachOArchitectures(MobileSamples.MachO(0x0100000C)));
        Assert.Equal(["x86_64"], MobileArtifactInspector.MachOArchitectures(MobileSamples.MachO(0x01000007)));
        Assert.Equal(["arm64", "x86_64"], MobileArtifactInspector.MachOArchitectures(MobileSamples.Fat(0x0100000C, 0x01000007)));
        Assert.Empty(MobileArtifactInspector.MachOArchitectures("\u007fELF\u0002\u0001\u0001\0\0\0\0\0"u8.ToArray()));
        var lying = MobileSamples.Fat(0x0100000C);
        BinaryPrimitives.WriteUInt32BigEndian(lying.AsSpan(4), 1000); // count beyond buffer
        Assert.Empty(MobileArtifactInspector.MachOArchitectures(lying));
    }

    [Fact]
    public void Provisioning_profile_parser_ignores_non_plist_data()
    {
        Assert.Null(MobileArtifactInspector.ParseProvisioningProfile(RandomNumberGenerator.GetBytes(256)));
        Assert.Null(MobileArtifactInspector.ParseProvisioningProfile(null));
        var p = MobileArtifactInspector.ParseProvisioningProfile(MobileSamples.Profile(getTaskAllow: false, devices: true, expires: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.NotNull(p);
        Assert.Equal("HCG Ad Hoc", p!.Name);
        Assert.Equal("ABCDE12345", p.TeamId);
        Assert.True(p.HasDevices);
        Assert.False(p.GetTaskAllow);
    }

    [Theory]
    [InlineData("a.apk", MobileArtifactKind.Apk)]
    [InlineData("a.AAB", MobileArtifactKind.Aab)]
    [InlineData("a.apks", MobileArtifactKind.ApkSet)]
    [InlineData("a.ipa", MobileArtifactKind.Ipa)]
    [InlineData("a.xcarchive/", MobileArtifactKind.XcArchive)]
    [InlineData("a.app", MobileArtifactKind.IosApp)]
    [InlineData("a.exe", null)]
    public void Artifact_kind_is_derived_from_the_container_name(string path, MobileArtifactKind? expected) =>
        Assert.Equal(expected, MobileArtifactInspector.KindOf(path));
}

/// <summary>Builders for synthetic mobile artifacts.</summary>
public static class MobileSamples
{
    public static X509Certificate2 Certificate(string subject)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        return X509CertificateLoader.LoadCertificate(cert.RawData);
    }

    // ── Android binary XML (AXML) ──

    public static byte[] BinaryManifest(bool debuggable)
    {
        // Strings with resource ids first (index == resource map index). Index 0 is deliberately empty (stripped name).
        string[] strings = ["", "versionName", "minSdkVersion", "targetSdkVersion", "debuggable", "manifest", "package", "com.hakmicapital.hello", "1.2.0", "uses-sdk", "application"];
        uint[] resIds = [0x0101021b, 0x0101021c, 0x0101020c, 0x01010270, 0x0101000f];
        int S(string s) => Array.IndexOf(strings, s);
        var body = new MemoryStream();
        body.Write(StringPool(strings));
        body.Write(Chunk(0x0180, 8, resIds.SelectMany(BitConverter.GetBytes).ToArray()));
        body.Write(Start(S("manifest"), [(0, Typed(0x10, 3)), (S("versionName"), Str(S("1.2.0"))), (S("package"), Str(S("com.hakmicapital.hello")))]));
        body.Write(Start(S("uses-sdk"), [(S("minSdkVersion"), Typed(0x10, 24)), (S("targetSdkVersion"), Typed(0x10, 35))]));
        body.Write(End(S("uses-sdk")));
        body.Write(Start(S("application"), [(S("debuggable"), Typed(0x12, debuggable ? 0xFFFFFFFF : 0))]));
        body.Write(End(S("application")));
        body.Write(End(S("manifest")));
        return Chunk(0x0003, 8, body.ToArray());

        static (uint Raw, byte Type, uint Data) Typed(byte type, uint data) => (0xFFFFFFFF, type, data);
        static (uint Raw, byte Type, uint Data) Str(int index) => ((uint)index, 0x03, (uint)index);
    }

    private static byte[] Chunk(ushort type, ushort headerSize, byte[] rest, byte[]? headerExtra = null)
    {
        var header = headerExtra ?? [];
        var size = 8 + header.Length + rest.Length;
        var b = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(b, type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)size);
        header.CopyTo(b, 8);
        rest.CopyTo(b, 8 + header.Length);
        return b;
    }

    private static byte[] StringPool(string[] strings)
    {
        var data = new MemoryStream();
        var offsets = new List<uint>();
        foreach (var s in strings)
        {
            offsets.Add((uint)data.Length);
            data.Write(BitConverter.GetBytes((ushort)s.Length));
            data.Write(Encoding.Unicode.GetBytes(s));
            data.Write([0, 0]);
        }
        while (data.Length % 4 != 0) data.WriteByte(0);
        var header = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0);                               // flags: UTF-16
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(28 + strings.Length * 4)); // strings start
        var rest = offsets.SelectMany(BitConverter.GetBytes).Concat(data.ToArray()).ToArray();
        return Chunk(0x0001, 28, rest, header);
    }

    private static byte[] Start(int name, (int Name, (uint Raw, byte Type, uint Data) Value)[] attrs)
    {
        var ext = new MemoryStream();
        var head = new byte[8]; // line number + comment
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), 0xFFFFFFFF);
        var e = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), (uint)name);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(8), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(10), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(12), (ushort)attrs.Length);
        ext.Write(e);
        foreach (var (attrName, (raw, type, data)) in attrs)
        {
            var a = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(a, 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)attrName);
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(8), raw);
            BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(12), 8);
            a[15] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(16), data);
            ext.Write(a);
        }
        return Chunk(0x0102, 16, ext.ToArray(), head);
    }

    private static byte[] End(int name)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), 0xFFFFFFFF);
        var e = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), (uint)name);
        return Chunk(0x0103, 16, e, head);
    }

    // ── APK ──

    public static string Apk(string path, bool debuggable, X509Certificate2? signer, bool v1 = false, string[]? abis = null,
        bool includeDex = true, (string Name, string Content)[]? extraEntries = null)
    {
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] content) { using var s = zip.CreateEntry(name).Open(); s.Write(content); }
            Add("AndroidManifest.xml", BinaryManifest(debuggable));
            if (includeDex) Add("classes.dex", "dex\n035\0"u8.ToArray());
            Add("resources.arsc", [2, 0, 12, 0]);
            foreach (var abi in abis ?? []) Add($"lib/{abi}/libhello.so", "\u007fELF"u8.ToArray());
            foreach (var (n, c) in extraEntries ?? []) Add(n, Encoding.UTF8.GetBytes(c));
            if (v1 && signer is not null)
            {
                Add("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n"u8.ToArray());
                Add("META-INF/CERT.SF", "Signature-Version: 1.0\r\n"u8.ToArray());
                Add("META-INF/CERT.RSA", Pkcs7(signer));
            }
        }
        if (signer is not null) InsertSigningBlock(path, signer);
        return path;
    }

    /// <summary>Inserts an APK Signing Block with a v2 entry (structure only) before the central directory.</summary>
    private static void InsertSigningBlock(string path, X509Certificate2 cert)
    {
        var zip = File.ReadAllBytes(path);
        var eocd = zip.AsSpan().LastIndexOf((ReadOnlySpan<byte>)[0x50, 0x4b, 0x05, 0x06]);
        var cdOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16));

        byte[] Lp(params byte[][] parts)
        {
            var body = parts.SelectMany(p => p).ToArray();
            return [.. BitConverter.GetBytes(body.Length), .. body];
        }
        var signedData = Lp(Lp(), Lp(Lp(cert.RawData)), Lp());                 // digests, certificates, attributes
        var value = Lp(Lp(signedData, Lp(), Lp(new byte[32])));               // signers → signer(signedData, signatures, publicKey)
        var pair = new MemoryStream();
        pair.Write(BitConverter.GetBytes((ulong)(4 + value.Length)));
        pair.Write(BitConverter.GetBytes(0x7109871au));
        pair.Write(value);
        var pairs = pair.ToArray();
        var blockSize = (ulong)(pairs.Length + 8 + 16);
        var block = new MemoryStream();
        block.Write(BitConverter.GetBytes(blockSize));
        block.Write(pairs);
        block.Write(BitConverter.GetBytes(blockSize));
        block.Write("APK Sig Block 42"u8);
        var blockBytes = block.ToArray();

        var result = new byte[zip.Length + blockBytes.Length];
        zip.AsSpan(0, cdOffset).CopyTo(result);
        blockBytes.CopyTo(result, cdOffset);
        zip.AsSpan(cdOffset).CopyTo(result.AsSpan(cdOffset + blockBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(eocd + blockBytes.Length + 16), (uint)(cdOffset + blockBytes.Length));
        File.WriteAllBytes(path, result);
    }

    /// <summary>Minimal PKCS#7 SignedData carrying one certificate (the shape of META-INF/*.RSA).</summary>
    public static byte[] Pkcs7(X509Certificate2 cert)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (w.PushSequence())
            {
                w.WriteInteger(1);
                using (w.PushSetOf()) { }
                using (w.PushSequence()) w.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, true))) w.WriteEncodedValue(cert.RawData);
                using (w.PushSetOf()) { }
            }
        }
        return w.Encode();
    }

    // ── AAB (protobuf manifest) ──

    public static string Aab(string path, X509Certificate2? signer, bool includeBundleConfig = true)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, byte[] content) { using var s = zip.CreateEntry(name).Open(); s.Write(content); }
        Add("base/manifest/AndroidManifest.xml", ProtoManifest());
        Add("base/dex/classes.dex", "dex\n035\0"u8.ToArray());
        Add("base/lib/arm64-v8a/libhello.so", "\u007fELF"u8.ToArray());
        Add("base/resources.pb", [0]);
        if (includeBundleConfig) Add("BundleConfig.pb", [0x0a, 0x00]);
        if (signer is not null)
        {
            Add("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n"u8.ToArray());
            Add("META-INF/KEY0.SF", "Signature-Version: 1.0\r\n"u8.ToArray());
            Add("META-INF/KEY0.RSA", Pkcs7(signer));
        }
        return path;
    }

    public static byte[] ProtoManifest()
    {
        byte[] Attr(string name, string? value, int? intValue)
        {
            var a = Field(2, Encoding.UTF8.GetBytes(name));
            if (value is not null) a = [.. a, .. Field(3, Encoding.UTF8.GetBytes(value))];
            if (intValue is { } i) a = [.. a, .. Field(6, Field(7, [0x30, .. Varint((ulong)i)]))]; // Item.prim.int_decimal_value (field 6, varint)
            return Field(4, a);
        }
        byte[] Element(string name, byte[][] attrs, params byte[][] children) =>
            Field(1, [.. Field(3, Encoding.UTF8.GetBytes(name)), .. attrs.SelectMany(a => a), .. children.SelectMany(c => Field(5, c))]);

        var usesSdk = Element("uses-sdk", [Attr("minSdkVersion", null, 24), Attr("targetSdkVersion", null, 35)]);
        return Element("manifest", [Attr("package", "com.hakmicapital.hello", null), Attr("versionCode", null, 3), Attr("versionName", "1.2.0", null)], usesSdk);
    }

    private static byte[] Field(int number, byte[] value) => [.. Varint((ulong)(number << 3 | 2)), .. Varint((ulong)value.Length), .. value];

    private static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        do { var b = (byte)(v & 0x7F); v >>= 7; bytes.Add(v != 0 ? (byte)(b | 0x80) : b); } while (v != 0);
        return [.. bytes];
    }

    // ── iOS ──

    public static byte[] MachO(uint cpu)
    {
        var b = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), cpu);
        return b;
    }

    public static byte[] Fat(params uint[] cpus)
    {
        var b = new byte[8 + cpus.Length * 20 + 64];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)cpus.Length);
        for (var i = 0; i < cpus.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + i * 20), cpus[i]);
        return b;
    }

    public static Dictionary<string, object> InfoPlist(string platform) => new()
    {
        ["CFBundleIdentifier"] = "com.hakmicapital.helloios",
        ["CFBundleExecutable"] = "HelloIOS",
        ["CFBundleShortVersionString"] = "1.3.0",
        ["CFBundleVersion"] = "7",
        ["MinimumOSVersion"] = "16.0",
        ["DTPlatformName"] = platform,
        ["CFBundleSupportedPlatforms"] = new List<object> { platform == "iphonesimulator" ? "iPhoneSimulator" : "iPhoneOS" },
    };

    public static string Ipa(string path, string platform, byte[] executable, bool signed, byte[]? profile, bool binaryPlist = false)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, byte[] content) { using var s = zip.CreateEntry(name).Open(); s.Write(content); }
        const string app = "Payload/HelloIOS.app/";
        var info = InfoPlist(platform);
        Add(app + "Info.plist", binaryPlist ? BinaryPlist(info) : Encoding.UTF8.GetBytes(XmlPlist(info)));
        Add(app + "HelloIOS", executable);
        if (signed) Add(app + "_CodeSignature/CodeResources", Encoding.UTF8.GetBytes(XmlPlist(new() { ["files"] = new Dictionary<string, object>() })));
        if (profile is not null) Add(app + "embedded.mobileprovision", profile);
        return path;
    }

    public static string AppBundle(string dir, string platform, byte[] executable, bool signed)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.plist"), XmlPlist(InfoPlist(platform)));
        File.WriteAllBytes(Path.Combine(dir, "HelloIOS"), executable);
        if (signed)
        {
            Directory.CreateDirectory(Path.Combine(dir, "_CodeSignature"));
            File.WriteAllText(Path.Combine(dir, "_CodeSignature", "CodeResources"), XmlPlist(new()));
        }
        return dir;
    }

    /// <summary>A provisioning-profile-shaped blob: binary CMS-like prefix, the XML plist, binary suffix.</summary>
    public static byte[] Profile(bool getTaskAllow, bool devices, DateTime expires)
    {
        var plist = new Dictionary<string, object>
        {
            ["Name"] = devices ? "HCG Ad Hoc" : "HCG App Store",
            ["TeamName"] = "Hakmi Capital Group",
            ["TeamIdentifier"] = new List<object> { "ABCDE12345" },
            ["ExpirationDate"] = expires,
            ["Entitlements"] = new Dictionary<string, object> { ["get-task-allow"] = getTaskAllow },
        };
        if (devices) plist["ProvisionedDevices"] = new List<object> { "00008110-000000000000001E" };
        return [0x30, 0x82, 0x10, 0x00, 0x06, 0x09, .. Encoding.UTF8.GetBytes(XmlPlist(plist)), 0xA0, 0x82, 0x00];
    }

    public static string XmlPlist(Dictionary<string, object> dict)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n");
        Write(dict);
        return sb.Append("</plist>\n").ToString();

        void Write(object o)
        {
            switch (o)
            {
                case Dictionary<string, object> d:
                    sb.Append("<dict>");
                    foreach (var (k, v) in d) { sb.Append("<key>").Append(System.Security.SecurityElement.Escape(k)).Append("</key>"); Write(v); }
                    sb.Append("</dict>\n");
                    break;
                case List<object> l: sb.Append("<array>"); l.ForEach(Write); sb.Append("</array>"); break;
                case bool b: sb.Append(b ? "<true/>" : "<false/>"); break;
                case DateTime dt: sb.Append("<date>").Append(dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("</date>"); break;
                case long or int: sb.Append("<integer>").Append(o).Append("</integer>"); break;
                default: sb.Append("<string>").Append(System.Security.SecurityElement.Escape(o.ToString())).Append("</string>"); break;
            }
        }
    }

    /// <summary>bplist00 writer for dictionaries/arrays of ASCII strings (enough for Info.plist tests).</summary>
    public static byte[] BinaryPlist(Dictionary<string, object> root)
    {
        var objects = new List<byte[]>();
        int Add(object o)
        {
            var index = objects.Count;
            objects.Add([]);
            switch (o)
            {
                case Dictionary<string, object> d:
                    var keys = d.Keys.Select(k => Add(k)).ToList();
                    var values = d.Values.Select(Add).ToList();
                    objects[index] = [.. Marker(0xD, d.Count), .. keys.Select(k => (byte)k), .. values.Select(v => (byte)v)];
                    break;
                case List<object> l:
                    var items = l.Select(Add).ToList();
                    objects[index] = [.. Marker(0xA, l.Count), .. items.Select(i => (byte)i)];
                    break;
                default:
                    var s = Encoding.ASCII.GetBytes(o.ToString()!);
                    objects[index] = [.. Marker(0x5, s.Length), .. s];
                    break;
            }
            return index;
        }
        static byte[] Marker(int type, int length) => length < 15 ? [(byte)(type << 4 | length)] : [(byte)(type << 4 | 0xF), 0x10, (byte)length];

        Add(root);
        if (objects.Count > 255) throw new InvalidOperationException("Test plist too large for 1-byte refs.");
        var ms = new MemoryStream();
        ms.Write("bplist00"u8);
        var offsets = new List<int>();
        foreach (var o in objects) { offsets.Add((int)ms.Length); ms.Write(o); }
        var tableOffset = ms.Length;
        foreach (var off in offsets) ms.Write([(byte)(off >> 8), (byte)off]);
        var trailer = new byte[32];
        trailer[6] = 2; // offset size
        trailer[7] = 1; // ref size
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)tableOffset);
        ms.Write(trailer);
        return ms.ToArray();
    }
}
