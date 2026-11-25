using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Infrastructure.Processes;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using HCG.ExecutableMaker.Packaging.Theos;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Complex .NET (Mono AOT) / Avalonia iOS bundles through Unleashify, from the HCG Mail Unleashed 0.1.0~beta1 audit:
/// an unsigned main executable with a dotted name, SkiaSharp and HarfBuzzSharp frameworks that arrive ad hoc signed by
/// their NuGet build (with their own Info.plist and _CodeSignature/CodeResources), managed assemblies, AOT data files and
/// runtime configuration. Signatures here are real CodeDirectories (AdHocSigner), so integrity is actually verified.
/// Policy under test (unchanged): ldid signs only what is unsigned; signatures that come with the build are kept.
/// </summary>
public sealed class DotNetBundleSigningTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private const string Exe = "HCGMail.iOS";
    private const string BundleId = "com.hakmicapitalgroup.hcgmail.unleashed";
    private static readonly string[] Frameworks = ["libSkiaSharp", "libHarfBuzzSharp"];

    private static string FrameworkPlist(string name) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict>
        <key>CFBundleIdentifier</key><string>com.microsoft.{name}</string>
        <key>CFBundleExecutable</key><string>{name}</string>
        <key>CFBundlePackageType</key><string>FMWK</string>
        <key>MinimumOSVersion</key><string>11.0</string>
        </dict></plist>
        """;

    /// <summary>A bundle shaped like HCG Mail Unleashed's Release build (EnableCodeSigning=false).</summary>
    private string MailLikeApp(Action<string>? tweak = null, uint mainFlags = 0, bool mainPresigned = false)
    {
        var app = _t.Combine("build", Exe + ".app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Info.plist"), MachOFixture.InfoPlist(BundleId, Exe, "16.0", "0.1.0", name: "HCG Mail Unleashed"));
        var main = MachOFixture.Build(minos: "16.0", signed: true, codeDirectory: false, rpaths: ["@executable_path/Frameworks"],
            dylibs: ["/usr/lib/libSystem.B.dylib", "/System/Library/Frameworks/UIKit.framework/UIKit", "/System/Library/Frameworks/Metal.framework/Metal",
                     "@rpath/libHarfBuzzSharp.framework/libHarfBuzzSharp", "@rpath/libSkiaSharp.framework/libSkiaSharp"]);
        if (mainPresigned) main = AdHocSigner.Sign(main, Exe, flags: mainFlags, mainBinary: true, teamId: "TEAM000001");
        File.WriteAllBytes(Path.Combine(app, Exe), main);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(app, Exe), (UnixFileMode)0b111_101_101);
        foreach (var fw in Frameworks)
        {
            var dir = Path.Combine(app, "Frameworks", fw + ".framework");
            Directory.CreateDirectory(Path.Combine(dir, "_CodeSignature"));
            var plist = Encoding.UTF8.GetBytes(FrameworkPlist(fw));
            var resources = Encoding.UTF8.GetBytes($"<plist><dict><key>files</key><dict><key>Info.plist</key><data>{Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(plist))}</data></dict></dict></plist>");
            File.WriteAllBytes(Path.Combine(dir, "Info.plist"), plist);
            File.WriteAllBytes(Path.Combine(dir, "_CodeSignature", "CodeResources"), resources);
            var bin = MachOFixture.Build(MachOFixture.Dylib, minos: "11.0", signed: true, codeDirectory: false, rpaths: ["@executable_path/Frameworks", "@loader_path/Frameworks"],
                dylibs: ["/usr/lib/libc++.1.dylib", "/usr/lib/libSystem.B.dylib"]);
            File.WriteAllBytes(Path.Combine(dir, fw), AdHocSigner.Sign(bin, "com.microsoft." + fw, infoPlist: plist, codeResources: resources));
        }
        foreach (var asm in new[] { "HCGMail.iOS", "HCG.Mail.Core", "Avalonia", "Avalonia.iOS", "SkiaSharp", "HarfBuzzSharp", "MailKit", "System.Private.CoreLib" })
        {
            File.WriteAllBytes(Path.Combine(app, asm + ".dll"), Encoding.ASCII.GetBytes("MZ managed assembly " + asm));
            File.WriteAllBytes(Path.Combine(app, asm + ".aotdata.arm64"), Encoding.ASCII.GetBytes("MONO_AOT_DATA " + asm));
        }
        File.WriteAllBytes(Path.Combine(app, "runtimeconfig.bin"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(app, "archived-expanded-entitlements.xcent"), "<plist><dict/></plist>");
        File.WriteAllBytes(Path.Combine(app, "Assets.car"), new byte[512]);
        File.WriteAllText(Path.Combine(app, "PrivacyInfo.xcprivacy"), "<plist><dict/></plist>");
        File.WriteAllBytes(Path.Combine(app, "AppIcon60x60@3x.png"), PngFixtures.CgBI(180, 180));
        tweak?.Invoke(app);
        return app;
    }

    /// <summary>A fake ldid that signs in place like the real one (-S[entitlements] file): ldid-style, real hashes.</summary>
    private static RecordingProcessRunner Ldid() => new(spec =>
    {
        var file = spec.Arguments[^1];
        var s = spec.Arguments.First(a => a.StartsWith("-S", StringComparison.Ordinal));
        var ents = s.Length > 2 ? File.ReadAllText(s[2..]) : null;
        File.WriteAllBytes(file, AdHocSigner.Sign(File.ReadAllBytes(file), Path.GetFileName(file), ldidStyle: true, entitlementsXml: ents, mainBinary: Path.GetFileName(file) == Exe));
        return (0, "", "");
    });

    private async Task<(RootlessAppPackager.Result Pkg, RootlessAppDebValidator.Result Val)> Package(string app, RecordingProcessRunner? ldid = null)
    {
        var ents = _t.Write("Entitlements.Unleashed.ldid.plist", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>keychain-access-groups</key><array><string>{BundleId}</string></array></dict></plist>");
        var spec = new RootlessAppPackageSpec(BundleId, "0.1.0~beta1", "HCG Mail Unleashed", RootlessAppPackager.HcgMaintainer, "Private email.\nCandidate beta.", EntitlementsFile: ents,
            Timestamp: new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
        var bundle = IosAppBundle.Open(app, _t.Combine("work-" + Guid.NewGuid().ToString("N")[..6]));
        var pkg = await RootlessAppPackager.PackageAsync(bundle, spec, _t.Combine("out-" + Guid.NewGuid().ToString("N")[..6]), "/usr/local/bin/ldid", ldid ?? Ldid(), null, CancellationToken.None);
        var val = await RootlessAppDebValidator.ValidateAsync(pkg.DebPath, new(BundleId, "0.1.0~beta1", BundleId, "17.4"), CancellationToken.None);
        return (pkg, val);
    }

    private static Dictionary<string, byte[]> Payload(string deb)
    {
        var bytes = File.ReadAllBytes(deb);
        using var ms = new MemoryStream(bytes);
        var data = TheosDebVerifier.ReadAr(ms).Single(m => m.Name == "data.tar.gz");
        var files = new Dictionary<string, byte[]>();
        using var tar = new TarReader(new GZipStream(new MemoryStream(bytes, (int)data.Offset, (int)data.Size), CompressionMode.Decompress));
        while (tar.GetNextEntry() is { } e)
            if (e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && e.DataStream is { } s)
            { using var o = new MemoryStream(); s.CopyTo(o); files[e.Name.TrimStart('.', '/')] = o.ToArray(); }
        return files;
    }

    private static VerificationCheck Check(RootlessAppDebValidator.Result v, string name) => v.Checks.Single(c => c.Name == name);

    [Fact]
    public async Task Only_the_unsigned_main_executable_is_ldid_signed_and_the_frameworks_keep_their_own_verified_signatures()
    {
        var app = MailLikeApp();
        var before = Frameworks.ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(app, "Frameworks", f + ".framework", f)));
        var (pkg, v) = await Package(app);
        Assert.True(v.Passed, v.Report);
        Assert.Equal([Exe], pkg.SignedFiles);                                             // nothing already signed is re-signed
        var payload = Payload(pkg.DebPath);
        var root = $"var/jb/Applications/{Exe}.app/";
        foreach (var fw in Frameworks)
        {
            Assert.Equal(before[fw], payload[$"{root}Frameworks/{fw}.framework/{fw}"]);     // byte for byte as the build shipped them
            Assert.Equal(File.ReadAllBytes(Path.Combine(app, "Frameworks", fw + ".framework", "_CodeSignature", "CodeResources")), payload[$"{root}Frameworks/{fw}.framework/_CodeSignature/CodeResources"]);
        }
        foreach (var f in Directory.EnumerateFiles(app).Where(f => f.EndsWith(".dll") || f.EndsWith(".aotdata.arm64") || f.EndsWith("runtimeconfig.bin")))
            Assert.Equal(File.ReadAllBytes(f), payload[root + Path.GetFileName(f)]);       // managed code and AOT data untouched
        var integrity = Check(v, "Signature integrity");
        Assert.Equal(VerificationStatus.Passed, integrity.Status);
        Assert.Contains("3 signature(s) verified page by page", integrity.Detail);
        Assert.Contains($"{Exe} (ad hoc, ldid-style)", integrity.Detail);
        Assert.Contains("Frameworks/libSkiaSharp.framework/libSkiaSharp (ad hoc)", integrity.Detail);
        Assert.Contains("identifier com.microsoft.libSkiaSharp", v.Report);
        Assert.Contains("Info.plist matches", v.Report);
        Assert.Contains("CodeResources matches", v.Report);
        Assert.Contains("Entitlements matches", v.Report);                                 // the main binary's ldid entitlements are bound
        Assert.Equal(VerificationStatus.Passed, Check(v, "Dynamic libraries").Status);      // @rpath frameworks resolve
        Assert.Contains($"/var/jb/Applications/{Exe}.app", Check(v, "App registration").Detail);
    }

    [Fact]
    public async Task A_nested_binary_changed_after_it_was_signed_is_refused()
    {
        var app = MailLikeApp(a =>
        {
            var fw = Path.Combine(a, "Frameworks", "libSkiaSharp.framework", "libSkiaSharp");
            var b = File.ReadAllBytes(fw);
            var sigOff = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(FindSigCmd(b) + 8));
            b[sigOff - 8] ^= 0x5A;                                                           // a code byte, after signing
            File.WriteAllBytes(fw, b);
        });
        var (_, v) = await Package(app);
        Assert.False(v.Passed);
        var integrity = Check(v, "Signature integrity");
        Assert.Equal(VerificationStatus.Failed, integrity.Status);
        Assert.Contains("libSkiaSharp.framework/libSkiaSharp (arm64): 1 of 1 page hashes do not match the file — it was changed after it was signed", integrity.Detail);
    }

    [Fact]
    public async Task A_framework_resource_changed_after_signing_is_reported_without_failing_the_package()
    {
        var app = MailLikeApp(a => File.AppendAllText(Path.Combine(a, "Frameworks", "libHarfBuzzSharp.framework", "Info.plist"), "\n<!-- edited -->"));
        var (_, v) = await Package(app);
        Assert.True(v.Passed, v.Report);
        var integrity = Check(v, "Signature integrity");
        Assert.Equal(VerificationStatus.PassedWithWarnings, integrity.Status);
        Assert.Contains("libHarfBuzzSharp (arm64): the Info.plist it was signed with has changed", integrity.Detail);
    }

    [Fact]
    public async Task Library_validation_in_the_main_executable_refuses_frameworks_signed_by_anyone_else()
    {
        // A main executable that arrives already signed with library validation (as a distribution build would).
        var app = MailLikeApp(mainPresigned: true, mainFlags: AdHocSigner.LibraryValidation | AdHocSigner.Runtime);
        var (pkg, v) = await Package(app);
        Assert.Empty(pkg.SignedFiles);                                                       // kept as built: nothing unsigned
        var integrity = Check(v, "Signature integrity");
        Assert.Equal(VerificationStatus.Failed, integrity.Status);
        Assert.Contains($"{Exe} enforces library validation, and Frameworks/libSkiaSharp.framework/libSkiaSharp is ad hoc: dyld refuses to load it", integrity.Detail);
    }

    [Fact]
    public async Task A_missing_framework_is_refused_before_it_reaches_a_device()
    {
        var app = MailLikeApp(a => Directory.Delete(Path.Combine(a, "Frameworks", "libHarfBuzzSharp.framework"), recursive: true));
        var (_, v) = await Package(app);
        var libs = Check(v, "Dynamic libraries");
        Assert.Equal(VerificationStatus.Failed, libs.Status);
        Assert.Contains($"{Exe} loads @rpath/libHarfBuzzSharp.framework/libHarfBuzzSharp, which is not in the package", libs.Detail);
    }

    [Fact]
    public async Task The_handoff_names_the_dotted_executable_and_install_path_correctly()
    {
        var app = MailLikeApp();
        var spec = new RootlessAppPackageSpec(BundleId, "0.1.0~beta1", "HCG Mail Unleashed", RootlessAppPackager.HcgMaintainer, "Private email.\nCandidate beta.");
        var o = await new RootlessAppWorkflow(Ldid(), TimeProvider.System).RunAsync(new RootlessAppWorkflow.Request(app, _t.Combine("wf"), spec,
            new PackageProvenance("F1r3srp3nt/HCG-Mail", new string('b', 40), false),
            new CandidateTexts("Private email.", [], [], "undecided"), "/usr/local/bin/ldid", "17.4"), null, CancellationToken.None);
        Assert.True(o.Succeeded, o.Failure);
        Assert.True(o.Lifecycle!.Passed, string.Join("\n", o.Lifecycle.Checks.Select(c => $"{c.Status} {c.Name}: {c.Detail}")));
        var md = File.ReadAllText(Path.Combine(o.Handoff!.Folder, "DEVICE-TEST.md"));
        Assert.Contains($"Installs only `/var/jb/Applications/{Exe}.app`", md);
        Assert.Contains("entries starting with `HCGMail.iOS`", md);
        Assert.Contains("-e hcgmail", md);
        Assert.Contains("grep '^HCGMail.iOS' | head -1", md);                              // the launch-crash evidence names this executable
        Assert.Contains("\"namespace\" : \"CODESIGNING\"", md);
        Assert.Null(PngIcon.ValidateStandard(File.ReadAllBytes(Path.Combine(o.Handoff.Folder, "icon.png"))));   // the build's CgBI icon, converted
    }

    /// <summary>
    /// HCG Repo's submission contract: submission.json claims the method (ldid), exactly the files Maker signed (the main
    /// executable, not the frameworks that arrived signed) and each binary's exact entitlements; the package.toml draft
    /// records the same entitlements under [review.entitlements]. HCG Repo's own parser reads both back identically.
    /// </summary>
    [Fact]
    public async Task The_submission_claims_only_what_Maker_signed_and_the_exact_entitlements()
    {
        var app = MailLikeApp();
        var ents = _t.Write("Entitlements.Unleashed.ldid.plist", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>keychain-access-groups</key><array><string>{BundleId}</string></array></dict></plist>");
        var spec = new RootlessAppPackageSpec(BundleId, "0.1.0~beta1", "HCG Mail Unleashed", RootlessAppPackager.HcgMaintainer, "Private email.\nCandidate beta.", EntitlementsFile: ents);
        var o = await new RootlessAppWorkflow(Ldid(), TimeProvider.System).RunAsync(new RootlessAppWorkflow.Request(app, _t.Combine("wf-sign"), spec,
            new PackageProvenance("F1r3srp3nt/HCG-Mail", new string('b', 40), false),
            new CandidateTexts("Private email.", [], [], "undecided"), "/usr/local/bin/ldid", "17.4"), null, CancellationToken.None);
        Assert.True(o.Succeeded, o.Failure);
        const string main = $"var/jb/Applications/{Exe}.app/{Exe}";
        var m = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(o.Handoff!.Folder, SubmissionManifest.FileName)))!;
        Assert.Empty(SubmissionManifest.Validate(m.AsObject()));
        var signing = m["signing"]!;
        Assert.Equal("ldid", signing["method"]!.GetValue<string>());
        Assert.Equal([main], signing["signedFiles"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal([main], signing["entitlements"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(BundleId, signing["entitlements"]![main]!["keychain-access-groups"]![0]!.GetValue<string>());
        var toml = File.ReadAllText(Path.Combine(o.Handoff.Folder, "package.toml"));
        // The same lines HCG Repo's own reviewed Mail package.toml carries.
        Assert.Contains($"[review.entitlements.\"{main}\"]\n\"keychain-access-groups\" = [\"{BundleId}\"]", toml.Replace("\r", ""));
        if (new HCG.ExecutableMaker.Core.ExecutableLocator().Find("python3") is { } python)
        {
            var script = "import json,sys,tomllib; t = tomllib.load(open(sys.argv[1],'rb')); m = json.load(open(sys.argv[2])); " +
                         "assert t['review']['entitlements'] == m['signing']['entitlements'], (t['review'], m['signing']); print('ok')";
            var (r, stdout, stderr) = await new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner().CaptureAsync(new HCG.ExecutableMaker.Core.ProcessSpec { Executable = python, Arguments = ["-I", "-c", script, Path.Combine(o.Handoff.Folder, "package.toml"), Path.Combine(o.Handoff.Folder, SubmissionManifest.FileName)] }, CancellationToken.None);
            if (!stderr.Contains("No module named 'tomllib'")) { Assert.True(r.Succeeded, stderr); Assert.Equal("ok", stdout.Trim()); }
        }
    }

    /// <summary>
    /// The published HCG Mail Unleashed 0.1.0~beta1 (HCG_MAIL_UNLEASHED_DEB, read only): every signature verifies, the
    /// frameworks are SkiaSharp's own ad hoc signatures and the main executable is ldid-signed.
    /// </summary>
    [Fact]
    public async Task The_published_HCG_Mail_Unleashed_beta1_signatures_all_verify()
    {
        if (Environment.GetEnvironmentVariable("HCG_MAIL_UNLEASHED_DEB") is not { } deb || !File.Exists(deb)) return;
        var v = await RootlessAppDebValidator.ValidateAsync(deb, new(BundleId, "0.1.0~beta1", BundleId, "17.4"), CancellationToken.None);
        Assert.Equal("ac4b31b10dbdf9f060b6e02274c3123c4e95045b8ba3b458f77f99387563099a", v.Facts.Sha256);
        Assert.True(v.Passed, v.Report);
        var integrity = Check(v, "Signature integrity");
        Assert.Equal(VerificationStatus.Passed, integrity.Status);
        Assert.Contains("HCGMail.iOS (ad hoc, ldid-style)", integrity.Detail);
        Assert.Contains("cdhash 77ceb18c026ec4d96cb3601ad05fa978d020b969", v.Report);
        Assert.Contains("cdhash af0fc3349d6e1d97dee1cd5ad041292e37c558c9", v.Report);   // libSkiaSharp, as SkiaSharp's NuGet ships it
        Assert.Contains("17459/17459 pages verified", v.Report);
    }

    private static int FindSigCmd(byte[] m)
    {
        var n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(16));
        var off = 32;
        for (var i = 0; i < n; i++)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(off)) == 0x1D) return off;
            off += (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(off + 4));
        }
        throw new InvalidOperationException();
    }
}
