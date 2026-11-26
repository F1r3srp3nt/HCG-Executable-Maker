using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Synthetic Mach-O files with real headers and load commands (no code), for the rootless packaging tests.</summary>
internal static class MachOFixture
{
    public const int Execute = 2, Dylib = 6;

    public static byte[] Build(int fileType = Execute, string minos = "14.7", uint platform = 2, string[]? dylibs = null, string[]? rpaths = null,
        bool signed = true, string? entitlementsXml = null, bool encrypted = false, uint cpu = 0x0100000C, bool codeDirectory = true)
    {
        var cmds = new List<byte[]>();
        byte[] Cmd(uint cmd, int size, Action<byte[]> fill) { var b = new byte[size]; BinaryPrimitives.WriteUInt32LittleEndian(b, cmd); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)size); fill(b); return b; }
        static int Pad8(int n) => (n + 7) & ~7;
        uint Ver(string v) { var p = v.Split('.').Select(int.Parse).Concat([0, 0]).ToArray(); return (uint)((p[0] << 16) | (p[1] << 8) | p[2]); }
        cmds.Add(Cmd(0x32, 24, b => { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), platform); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), Ver(minos)); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), Ver("17.0")); }));
        foreach (var d in dylibs ?? ["/usr/lib/libSystem.B.dylib", "/System/Library/Frameworks/UIKit.framework/UIKit"])
        {
            var name = Encoding.UTF8.GetBytes(d);
            cmds.Add(Cmd(0xC, Pad8(24 + name.Length + 1), b => { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 24); name.CopyTo(b, 24); }));
        }
        foreach (var r in rpaths ?? [])
        {
            var name = Encoding.UTF8.GetBytes(r);
            cmds.Add(Cmd(0x8000001C, Pad8(12 + name.Length + 1), b => { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 12); name.CopyTo(b, 12); }));
        }
        if (encrypted) cmds.Add(Cmd(0x2C, 24, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 1)));
        byte[]? signature = null;
        if (signed)
        {
            // A superblob with a CodeDirectory (slot 0, as ldid writes it; the hashes are not real) and optional entitlements (slot 5).
            var blobs = new List<(uint Slot, byte[] Data)>();
            if (codeDirectory)
            {
                var cd = new byte[44];
                BinaryPrimitives.WriteUInt32BigEndian(cd, 0xFADE0C02); BinaryPrimitives.WriteUInt32BigEndian(cd.AsSpan(4), 44); BinaryPrimitives.WriteUInt32BigEndian(cd.AsSpan(8), 0x20001);
                blobs.Add((0, cd));
            }
            var ent = entitlementsXml is null ? [] : Encoding.UTF8.GetBytes(entitlementsXml);
            if (ent.Length > 0)
            {
                var entBlob = new byte[8 + ent.Length];
                BinaryPrimitives.WriteUInt32BigEndian(entBlob, 0xFADE7171); BinaryPrimitives.WriteUInt32BigEndian(entBlob.AsSpan(4), (uint)entBlob.Length); ent.CopyTo(entBlob, 8);
                blobs.Add((5, entBlob));
            }
            var header = 12 + blobs.Count * 8;
            signature = new byte[header + blobs.Sum(b => b.Data.Length)];
            BinaryPrimitives.WriteUInt32BigEndian(signature, 0xFADE0CC0);
            BinaryPrimitives.WriteUInt32BigEndian(signature.AsSpan(4), (uint)signature.Length);
            BinaryPrimitives.WriteUInt32BigEndian(signature.AsSpan(8), (uint)blobs.Count);
            var at = header;
            for (var i = 0; i < blobs.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(signature.AsSpan(12 + i * 8), blobs[i].Slot);
                BinaryPrimitives.WriteUInt32BigEndian(signature.AsSpan(16 + i * 8), (uint)at);
                blobs[i].Data.CopyTo(signature, at); at += blobs[i].Data.Length;
            }
            cmds.Add(Cmd(0x1D, 16, _ => { }));
        }
        var sizeofcmds = cmds.Sum(c => c.Length);
        var body = 32 + sizeofcmds + 64;
        var file = new byte[body + (signature?.Length ?? 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), cpu);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)fileType);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)cmds.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), (uint)sizeofcmds);
        var pos = 32;
        foreach (var c in cmds)
        {
            if (signature is not null && BinaryPrimitives.ReadUInt32LittleEndian(c) == 0x1D)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(8), (uint)body);
                BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(12), (uint)signature.Length);
            }
            c.CopyTo(file, pos); pos += c.Length;
        }
        signature?.CopyTo(file, body);
        return file;
    }

    public static string InfoPlist(string bundleId = "com.hcg.sentinel", string exe = "HCGSentinel", string minos = "14.7", string version = "1.4.0", string? platform = "iPhoneOS", string name = "HCG Sentinel") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>CFBundleIdentifier</key><string>{bundleId}</string>
        <key>CFBundleExecutable</key><string>{exe}</string>
        <key>CFBundleName</key><string>{name}</string>
        <key>CFBundleShortVersionString</key><string>{version}</string>
        <key>CFBundleVersion</key><string>42</string>
        <key>MinimumOSVersion</key><string>{minos}</string>
        <key>UIDeviceFamily</key><array><integer>1</integer><integer>2</integer></array>
        <key>CFBundleSupportedPlatforms</key><array><string>{platform}</string></array>
        <key>DTXcode</key><string>1640</string>
        <key>DTSDKName</key><string>iphoneos18.4</string>
        </dict></plist>
        """;

    /// <summary>An app like Xcode's device build: main executable, an embedded framework loaded through @rpath, an icon.</summary>
    public static string App(string dir, bool signed = true, string minos = "14.7", string[]? extraDylibs = null, bool framework = true, uint platform = 2, string? entitlementsXml = null, string? plistPlatform = "iPhoneOS",
        string exe = "HCGSentinel", string bundleId = "com.hcg.sentinel", string name = "HCG Sentinel", byte[]? icon = null)
    {
        var app = Path.Combine(dir, exe + ".app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Info.plist"), InfoPlist(bundleId, exe, minos, platform: plistPlatform, name: name));
        var dylibs = new List<string> { "/usr/lib/libSystem.B.dylib", "/System/Library/Frameworks/UIKit.framework/UIKit" };
        if (framework) dylibs.Add("@rpath/SentinelCore.framework/SentinelCore");
        dylibs.AddRange(extraDylibs ?? []);
        File.WriteAllBytes(Path.Combine(app, exe), Build(minos: minos, platform: platform, dylibs: [.. dylibs], rpaths: ["@executable_path/Frameworks"], signed: signed, entitlementsXml: entitlementsXml));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(app, exe), (UnixFileMode)0b111_101_101);
        if (framework)
        {
            var fw = Path.Combine(app, "Frameworks", "SentinelCore.framework");
            Directory.CreateDirectory(fw);
            File.WriteAllBytes(Path.Combine(fw, "SentinelCore"), Build(Dylib, minos: minos, platform: platform, signed: signed));
            File.WriteAllText(Path.Combine(fw, "Info.plist"), InfoPlist("com.hcg.sentinel.core", "SentinelCore", minos));
        }
        File.WriteAllBytes(Path.Combine(app, "AppIcon60x60@3x.png"), icon ?? PngFixtures.Standard(180));
        File.WriteAllBytes(Path.Combine(app, "Assets.car"), new byte[300]);
        return app;
    }

    /// <summary>A stand-in for ldid -S: rewrites the file as the same Mach-O with a code signature.</summary>
    public static RecordingProcessRunner FakeLdid(string minos = "14.7") => new(spec =>
    {
        var file = spec.Arguments[^1];
        var isFramework = file.Contains(".framework/", StringComparison.Ordinal);
        File.WriteAllBytes(file, isFramework ? Build(Dylib, minos: minos)
            : Build(minos: minos, dylibs: ["/usr/lib/libSystem.B.dylib", "/System/Library/Frameworks/UIKit.framework/UIKit", "@rpath/SentinelCore.framework/SentinelCore"], rpaths: ["@executable_path/Frameworks"]));
        return (0, "", "");
    });
}

public class RootlessAppDebTests
{
    private readonly TempDir _t = new();

    private static RootlessAppPackageSpec Spec(string version = "1.4.0~beta1", string? entitlements = null) => new(
        "com.hakmicapitalgroup.hcgsentinel", version, "HCG Sentinel", RootlessAppPackager.HcgMaintainer,
        "Toolkit for authorised testing of your own devices.\nBeta packaging of the standard app.", EntitlementsFile: entitlements,
        Timestamp: new DateTimeOffset(2026, 11, 15, 18, 0, 0, TimeSpan.Zero));

    private async Task<(RootlessAppPackager.Result Pkg, IosAppBundle App)> Package(string? appDir = null, RootlessAppPackageSpec? spec = null, IProcessRunner? runner = null, string? ldid = null)
    {
        var app = IosAppBundle.Open(appDir ?? MachOFixture.App(_t.Combine("build")), _t.Path);
        var pkg = await RootlessAppPackager.PackageAsync(app, spec ?? Spec(), _t.Combine("out"), ldid, runner ?? MachOFixture.FakeLdid(), null, CancellationToken.None);
        return (pkg, app);
    }

    [Fact]
    public async Task A_signed_device_app_becomes_a_rootless_gzip_deb_that_validates()
    {
        var (pkg, app) = await Package();
        Assert.Equal("com.hcg.sentinel", app.BundleId);
        Assert.Equal("14.7", app.MinimumOS);
        Assert.Empty(pkg.SignedFiles); // already signed: signatures kept as built
        var v = await RootlessAppDebValidator.ValidateAsync(pkg.DebPath, new("com.hakmicapitalgroup.hcgsentinel", "1.4.0~beta1", "com.hcg.sentinel", "16.5"), CancellationToken.None);
        Assert.True(v.Passed, v.Report);
        Assert.Equal("/var/jb/Applications/HCGSentinel.app", v.Facts.AppPath);
        Assert.Contains("Depends: firmware (>= 14.7), uikittools", pkg.ControlText);
        Assert.Contains("Architecture: iphoneos-arm64", pkg.ControlText);
        Assert.Equal(VerificationStatus.Passed, v.Checks.Single(c => c.Name == "Minimum iOS vs iOS 16.5").Status);
        Assert.Contains("does not", v.Checks.Single(c => c.Name == "Minimum iOS vs iOS 16.5").Detail.Replace("not a device test", "does not"));
        Assert.Equal(VerificationStatus.Passed, v.Checks.Single(c => c.Name == "Dynamic libraries").Status);
        Assert.Equal(VerificationStatus.Passed, v.Checks.Single(c => c.Name == "Rootless install paths").Status);
        Assert.All(v.Checks.Where(c => c.Name is not "Installation on a device" and not "Signature integrity"), c => Assert.Equal(VerificationStatus.Passed, c.Status));
        // This fixture's signatures are stubs without page hashes: nothing to verify, which is reported as such (real ones: DotNetBundleSigningTests).
        Assert.Equal(VerificationStatus.NotVerifiable, v.Checks.Single(c => c.Name == "Signature integrity").Status);
        Assert.Equal(VerificationStatus.NotVerifiable, v.Checks.Single(c => c.Name == "Installation on a device").Status);
        Assert.Equal(["postinst", "postrm"], v.Facts.Scripts.Keys.Order().ToArray());
        Assert.DoesNotContain("respring", v.Facts.Scripts["postinst"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_input_gives_the_same_bytes()
    {
        var a = await Package();
        var first = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(a.Pkg.DebPath)));
        File.Delete(a.Pkg.DebPath);
        var b = await RootlessAppPackager.PackageAsync(a.App, Spec(), _t.Combine("out"), null, MachOFixture.FakeLdid(), null, CancellationToken.None);
        Assert.Equal(first, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(b.DebPath))));
    }

    [Fact]
    public async Task Debians_own_dpkg_deb_reads_and_extracts_the_package()
    {
        if (!File.Exists("/usr/bin/dpkg-deb")) return; // Linux CI and this container have it
        var (pkg, _) = await Package();
        static (int Code, string Out) Run(params string[] args)
        {
            var psi = new ProcessStartInfo("/usr/bin/dpkg-deb") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o);
        }
        var info = Run("--info", pkg.DebPath);
        Assert.True(info.Code == 0, info.Out);
        Assert.Contains("Package: com.hakmicapitalgroup.hcgsentinel", info.Out);
        Assert.Contains("postinst", info.Out);
        var list = Run("--contents", pkg.DebPath);
        Assert.True(list.Code == 0, list.Out);
        Assert.Contains("root/wheel", list.Out);
        Assert.Contains("./var/jb/Applications/HCGSentinel.app/HCGSentinel", list.Out);
        Assert.Contains("-rwxr-xr-x root/wheel", list.Out.Split('\n').Single(l => l.EndsWith("HCGSentinel.app/HCGSentinel", StringComparison.Ordinal)));
        var x = _t.Combine("dpkg-x");
        var extract = Run("--extract", pkg.DebPath, x);
        Assert.True(extract.Code == 0, extract.Out);
        Assert.True(File.Exists(Path.Combine(x, "var", "jb", "Applications", "HCGSentinel.app", "Frameworks", "SentinelCore.framework", "SentinelCore")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_t.Combine("build"), "HCGSentinel.app", "Assets.car")), File.ReadAllBytes(Path.Combine(x, "var", "jb", "Applications", "HCGSentinel.app", "Assets.car")));
    }

    [Fact]
    public async Task Real_dpkg_installs_upgrades_and_removes_the_package_cleanly_in_a_sandbox()
    {
        if (!File.Exists("/usr/bin/dpkg")) return; // Linux CI and this container have dpkg; not a device test
        var (v1, _) = await Package();
        var v2App = MachOFixture.App(_t.Combine("v2"));
        Directory.Delete(Path.Combine(v2App, "Frameworks"), recursive: true);   // the next build drops a framework…
        File.WriteAllBytes(Path.Combine(v2App, "HCGSentinel"), MachOFixture.Build(dylibs: ["/usr/lib/libSystem.B.dylib"]));
        File.WriteAllText(Path.Combine(v2App, "NewResource.json"), "{}");       // …and adds a resource
        var v2 = await RootlessAppPackager.PackageAsync(IosAppBundle.Open(v2App, _t.Path), Spec("1.4.1~beta1"), _t.Combine("out2"), null, MachOFixture.FakeLdid(), null, CancellationToken.None);

        var root = _t.Combine("dpkg", "root"); var admin = _t.Combine("dpkg", "admin"); var bin = _t.Combine("dpkg", "bin"); var log = _t.Combine("dpkg", "uicache.log");
        foreach (var d in new[] { root, Path.Combine(admin, "updates"), Path.Combine(admin, "info"), bin }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(admin, "status"), ""); File.WriteAllText(Path.Combine(admin, "available"), "");
        File.WriteAllText(Path.Combine(bin, "uicache"), "#!/bin/sh\necho \"uicache $*\" >> \"$UICACHE_LOG\"\n");
        File.SetUnixFileMode(Path.Combine(bin, "uicache"), (UnixFileMode)0b111_101_101);
        int Dpkg(params string[] args)
        {
            var psi = new ProcessStartInfo("/usr/bin/dpkg") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--root=/", $"--instdir={root}", $"--admindir={admin}", "--force-script-chrootless", "--force-architecture", "--force-depends", "--force-not-root" }.Concat(args)) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");
            psi.Environment["UICACHE_LOG"] = log;
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            return p.ExitCode;
        }
        string[] Files() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => "/" + Path.GetRelativePath(root, f)).Order().ToArray();
        var app = "/var/jb/Applications/HCGSentinel.app";

        Assert.Equal(0, Dpkg("-i", v1.DebPath));
        Assert.Contains($"{app}/Frameworks/SentinelCore.framework/SentinelCore", Files());
        Assert.Equal(0, Dpkg("-i", v2.DebPath));
        Assert.DoesNotContain(Files(), f => f.Contains("SentinelCore", StringComparison.Ordinal));   // upgrade removes what the new build no longer has
        Assert.Contains($"{app}/NewResource.json", Files());
        Assert.Equal(0, Dpkg("-r", "com.hakmicapitalgroup.hcgsentinel"));
        Assert.Empty(Files());                                                                          // nothing left behind
        Assert.Equal(0, Dpkg("-P", "com.hakmicapitalgroup.hcgsentinel"));
        Assert.Equal([$"uicache -p {app}", $"uicache -p {app}", $"uicache -u {app}", $"uicache -u {app}"], File.ReadAllLines(log));
    }

    [Fact]
    public async Task Unsigned_build_output_is_pseudo_signed_with_ldid_nested_code_first_and_without_ldid_it_stops_with_the_prerequisite()
    {
        var appDir = MachOFixture.App(_t.Combine("unsigned"), signed: false);
        var runner = MachOFixture.FakeLdid();
        var (pkg, _) = await Package(appDir, runner: runner, ldid: "/usr/local/bin/ldid");
        Assert.Equal(["Frameworks/SentinelCore.framework/SentinelCore", "HCGSentinel"], pkg.SignedFiles);
        Assert.All(runner.Calls, c => Assert.Equal("-S", c.Arguments[0])); // no entitlements added
        var v = await RootlessAppDebValidator.ValidateAsync(pkg.DebPath, new(), CancellationToken.None);
        Assert.Equal(VerificationStatus.Passed, v.Checks.Single(c => c.Name == "Code signatures").Status);
        // The build output itself is not changed.
        Assert.False(MachOLinkage.Read(Path.Combine(appDir, "HCGSentinel"))[0].HasCodeSignature);

        var wf = await new RootlessAppWorkflow(runner, TimeProvider.System).RunAsync(Request(appDir, ldid: null), null, CancellationToken.None);
        Assert.Equal((RootlessAppWorkflow.Stage.Inspect, false), (wf.ReachedStage, wf.Succeeded));
        Assert.Contains("Missing prerequisite: ldid", wf.Failure);
    }

    private RootlessAppWorkflow.Request Request(string source, string? ldid = "/usr/local/bin/ldid", string? device = "16.5", PackageProvenance? prov = null, string access = "public", string? approval = null) =>
        new(source, _t.Combine("wf-" + Guid.NewGuid().ToString("N")[..6]), Spec(), prov ?? new("F1r3srp3nt/HCG-Sentinel", new string('a', 40), false),
            new("Toolkit for authorised testing of your own devices.", ["First rootless candidate of the standard app."], ["Sentinel's privileged helper is not part of this package."], access, "iPhone 13 Pro, iOS 16.5, Dopamine (rootless)", approval),
            ldid, device);

    [Fact]
    public async Task Simulator_builds_encrypted_App_Store_binaries_and_non_iOS_binaries_are_refused_before_packaging()
    {
        var sim = IosAppBundle.Open(MachOFixture.App(_t.Combine("sim"), platform: 7, plistPlatform: "iPhoneSimulator"), _t.Path);
        Assert.Contains(RootlessAppPackager.CheckSpec(Spec(), sim), p => p.Contains("iOS Simulator"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RootlessAppPackager.PackageAsync(sim, Spec(), _t.Combine("o"), null, MachOFixture.FakeLdid(), null, CancellationToken.None));
        Assert.Contains("Simulator", ex.Message);

        var macDir = _t.Combine("mac", "HCGSentinel.app");
        MachOFixture.App(_t.Combine("mac"), platform: 1);
        Assert.Contains(RootlessAppPackager.CheckSpec(Spec(), IosAppBundle.Open(macDir, _t.Path)), p => p.Contains("macOS"));

        var enc = MachOFixture.App(_t.Combine("enc"));
        File.WriteAllBytes(Path.Combine(enc, "HCGSentinel"), MachOFixture.Build(encrypted: true));
        Assert.Contains(RootlessAppPackager.CheckSpec(Spec(), IosAppBundle.Open(enc, _t.Path)), p => p.Contains("encrypted"));
    }

    [Fact]
    public async Task A_binary_that_needs_a_newer_iOS_than_the_test_device_fails_the_compatibility_check()
    {
        var (pkg, _) = await Package(MachOFixture.App(_t.Combine("ios17"), minos: "17.0"));
        var v = await RootlessAppDebValidator.ValidateAsync(pkg.DebPath, new(DeviceIosVersion: "16.5"), CancellationToken.None);
        var c = v.Checks.Single(c => c.Name == "Minimum iOS vs iOS 16.5");
        Assert.Equal(VerificationStatus.Failed, c.Status);
        Assert.Contains("needs iOS 17.0", c.Detail);
        Assert.Contains("Changing the number alone does not fix this", c.Detail);
        Assert.Contains("firmware (>= 17.0)", pkg.ControlText);
    }

    [Fact]
    public async Task Libraries_that_do_not_resolve_or_point_at_rootful_jailbreak_paths_fail()
    {
        var missing = MachOFixture.App(_t.Combine("missing"), extraDylibs: ["@rpath/Missing.framework/Missing"]);
        var (p1, _) = await Package(missing);
        var v1 = await RootlessAppDebValidator.ValidateAsync(p1.DebPath, new(), CancellationToken.None);
        Assert.Contains("@rpath/Missing.framework/Missing", v1.Checks.Single(c => c.Name == "Dynamic libraries").Detail);
        Assert.False(v1.Passed);

        var rootful = MachOFixture.App(_t.Combine("rootful"), extraDylibs: ["/Library/Frameworks/CydiaSubstrate.framework/CydiaSubstrate"]);
        File.Delete(p1.DebPath);
        var (p2, _) = await Package(rootful);
        var v2 = await RootlessAppDebValidator.ValidateAsync(p2.DebPath, new(), CancellationToken.None);
        Assert.Contains("rootful jailbreak location", v2.Checks.Single(c => c.Name == "Dynamic libraries").Detail);
    }

    [Fact]
    public async Task A_signature_command_without_a_CodeDirectory_is_unsigned_and_is_signed_with_ldid()
    {
        var app = MachOFixture.App(_t.Combine("nocd"));
        File.WriteAllBytes(Path.Combine(app, "HCGSentinel"), MachOFixture.Build(codeDirectory: false,
            dylibs: ["/usr/lib/libSystem.B.dylib", "@rpath/SentinelCore.framework/SentinelCore"], rpaths: ["@executable_path/Frameworks"]));
        Assert.False(MachOLinkage.Read(Path.Combine(app, "HCGSentinel"))[0].HasCodeSignature);
        var (pkg, _) = await Package(app, ldid: "/usr/local/bin/ldid");
        Assert.Equal(["HCGSentinel"], pkg.SignedFiles);
    }

    [Fact]
    public async Task Swift_libraries_found_through_an_rpath_into_the_OS_count_as_system_libraries()
    {
        var app = MachOFixture.App(_t.Combine("swift"));
        File.WriteAllBytes(Path.Combine(app, "HCGSentinel"), MachOFixture.Build(
            dylibs: ["/usr/lib/libSystem.B.dylib", "@rpath/libswift_Concurrency.dylib", "@rpath/SentinelCore.framework/SentinelCore"],
            rpaths: ["/usr/lib/swift", "@executable_path/Frameworks"]));
        var (pkg, _) = await Package(app);
        var v = await RootlessAppDebValidator.ValidateAsync(pkg.DebPath, new(), CancellationToken.None);
        var libs = v.Checks.Single(c => c.Name == "Dynamic libraries");
        Assert.Equal(VerificationStatus.Passed, libs.Status);
        Assert.True(libs.Detail.Contains("3 system libraries"), libs.Detail); // libSystem, UIKit (framework) and libswift_Concurrency via /usr/lib/swift
    }

    [Fact]
    public async Task An_embedded_Swift_runtime_library_resolves_its_rpath_through_the_main_executable()
    {
        // CI run 44063361930 (HCGSentinel, iOS 14.7): Xcode embeds libswift_Concurrency.dylib, which loads
        // @rpath/libswiftCore.dylib and has no LC_RPATH of its own; dyld finds it through the main executable's
        // /usr/lib/swift rpath. That is a system library, not a missing one.
        var app = MachOFixture.App(_t.Combine("concurrency"));
        File.WriteAllBytes(Path.Combine(app, "HCGSentinel"), MachOFixture.Build(
            dylibs: ["/usr/lib/libSystem.B.dylib", "@rpath/libswift_Concurrency.dylib", "@rpath/SentinelCore.framework/SentinelCore"],
            rpaths: ["/usr/lib/swift", "@executable_path/Frameworks"]));
        File.WriteAllBytes(Path.Combine(app, "Frameworks", "libswift_Concurrency.dylib"), MachOFixture.Build(MachOFixture.Dylib,
            dylibs: ["/usr/lib/libSystem.B.dylib", "@rpath/libswiftCore.dylib"]));
        var (pkg, _) = await Package(app);
        var v = await RootlessAppDebValidator.ValidateAsync(pkg.DebPath, new(), CancellationToken.None);
        var libs = v.Checks.Single(c => c.Name == "Dynamic libraries");
        Assert.True(libs.Status == VerificationStatus.Passed, libs.Detail);

        // Without the main executable's OS rpath the same library is reported as missing.
        File.WriteAllBytes(Path.Combine(app, "HCGSentinel"), MachOFixture.Build(
            dylibs: ["/usr/lib/libSystem.B.dylib", "@rpath/libswift_Concurrency.dylib", "@rpath/SentinelCore.framework/SentinelCore"],
            rpaths: ["@executable_path/Frameworks"]));
        var (pkg2, _) = await Package(app);
        var v2 = await RootlessAppDebValidator.ValidateAsync(pkg2.DebPath, new(), CancellationToken.None);
        Assert.Contains("libswiftCore.dylib", v2.Checks.Single(c => c.Name == "Dynamic libraries").Detail);
    }

    [Fact]
    public async Task Privileged_entitlements_are_refused_whether_embedded_or_requested_and_ordinary_ones_are_listed()
    {
        const string privileged = """<?xml version="1.0"?><plist version="1.0"><dict><key>platform-application</key><true/><key>com.apple.private.security.no-sandbox</key><true/></dict></plist>""";
        var (pkg, _) = await Package(MachOFixture.App(_t.Combine("priv"), entitlementsXml: privileged));
        var v = await RootlessAppDebValidator.ValidateAsync(pkg.DebPath, new(), CancellationToken.None);
        var ent = v.Checks.Single(c => c.Name == "Entitlements");
        Assert.Equal(VerificationStatus.Failed, ent.Status);
        Assert.Contains("platform-application", ent.Detail);

        var file = _t.Write("app.entitlements", privileged);
        var app = IosAppBundle.Open(MachOFixture.App(_t.Combine("req")), _t.Path);
        Assert.Contains(RootlessAppPackager.CheckSpec(Spec(entitlements: file), app), p => p.Contains("privileged entitlements"));

        const string ordinary = """<?xml version="1.0"?><plist version="1.0"><dict><key>application-identifier</key><string>ABCDE12345.com.hcg.sentinel</string></dict></plist>""";
        File.Delete(pkg.DebPath);
        var (ok, _) = await Package(MachOFixture.App(_t.Combine("ord"), entitlementsXml: ordinary));
        var v2 = await RootlessAppDebValidator.ValidateAsync(ok.DebPath, new(), CancellationToken.None);
        Assert.Equal(VerificationStatus.Passed, v2.Checks.Single(c => c.Name == "Entitlements").Status);
        Assert.Contains("application-identifier", v2.Report);
    }

    [Fact]
    public async Task Published_versions_are_immutable_and_a_new_build_needs_a_higher_version()
    {
        var (pkg, _) = await Package();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pkg.DebPath)));
        async Task<VerificationCheck> Check(params (string, string)[] published) =>
            (await RootlessAppDebValidator.ValidateAsync(pkg.DebPath, new(PublishedVersions: published), CancellationToken.None)).Checks.Single(c => c.Name == "Version");
        Assert.Equal(VerificationStatus.Failed, (await Check(("1.4.0~beta1", new string('0', 64)))).Status);   // same version, other bytes
        Assert.Equal(VerificationStatus.Passed, (await Check(("1.4.0~beta1", sha))).Status);                   // the same build again
        Assert.Equal(VerificationStatus.Failed, (await Check(("1.4.0", "x"))).Status);                          // ~beta1 sorts below 1.4.0
        Assert.Equal(VerificationStatus.Passed, (await Check(("1.3.9", "x"), ("1.4.0~alpha2", "y"))).Status);
    }

    [Theory]
    [InlineData("1.0.0~beta1", "1.0.0~beta2", -1)]
    [InlineData("1.0.0~beta2", "1.0.0~rc1", -1)]
    [InlineData("1.0.0~rc1", "1.0.0", -1)]
    [InlineData("1.0.0", "1.0.0-1", -1)]
    [InlineData("1.0.0-1", "1.0.1", -1)]
    [InlineData("0.10", "0.9", 1)]
    [InlineData("1:0.1", "9.9", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0a", "1.0", 1)]
    [InlineData("1.0~", "1.0", -1)]
    public void Debian_version_order_matches_dpkg(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(DebianVersion.Compare(a, b)));

    [Fact]
    public async Task Corrupt_gzip_truncated_payloads_and_lzma_members_are_refused()
    {
        var (pkg, _) = await Package();
        var bytes = File.ReadAllBytes(pkg.DebPath);
        // Flip bytes in the middle of data.tar.gz (the last member).
        var broken = (byte[])bytes.Clone();
        for (var i = broken.Length - 200; i < broken.Length - 150; i++) broken[i] ^= 0x5A;
        var path = _t.Combine("broken.deb");
        File.WriteAllBytes(path, broken);
        var v = await RootlessAppDebValidator.ValidateAsync(path, new(), CancellationToken.None);
        Assert.False(v.Passed);
        Assert.Contains(v.Checks, c => c.Status == VerificationStatus.Failed && c.Name is "gzip members" or "Complete extraction");

        var lzma = _t.Combine("lzma.deb");
        File.WriteAllBytes(lzma, Encoding.ASCII.GetBytes("!<arch>\ndebian-binary   0           0     0     100644  4         `\n2.0\ncontrol.tar.gz  0           0     0     100644  2         `\nxxdata.tar.lzma   0           0     0     100644  2         `\nyy"));
        var lv = await RootlessAppDebValidator.ValidateAsync(lzma, new(), CancellationToken.None);
        Assert.Contains("xz, lzma and zstd members are refused", lv.Checks.Single(c => c.Name == "Debian archive").Detail);
    }

    [Fact]
    public async Task An_ipa_is_opened_by_extracting_its_app_never_by_renaming()
    {
        var appDir = MachOFixture.App(_t.Combine("ipa-src", "Payload"));
        var ipa = _t.Combine("HCGSentinel.ipa");
        ZipFile.CreateFromDirectory(_t.Combine("ipa-src"), ipa);
        var app = IosAppBundle.Open(ipa, _t.Combine("work"));
        Assert.Equal(("ipa", "com.hcg.sentinel"), (app.SourceKind, app.BundleId));
        Assert.StartsWith(_t.Combine("work"), app.AppPath);
        Assert.NotNull(app.Main);
    }

    [Fact]
    public async Task The_workflow_exports_a_candidate_handoff_with_provenance_and_only_a_pinned_source_is_importable()
    {
        var runner = MachOFixture.FakeLdid();
        var appDir = MachOFixture.App(_t.Combine("h"));
        var steps = new List<RootlessAppWorkflow.Stage>();
        var pinned = await new RootlessAppWorkflow(runner, TimeProvider.System).RunAsync(
            Request(appDir, prov: new("F1r3srp3nt/HCG-Sentinel", new string('b', 40), false, 44012345678, "hcg-sentinel-rootless-deb", BuiltWith: "Xcode 16.4"), access: "connect",
                approval: "approved by the owner on 21 Nov 2026: 'test approval'"),
            (s, _) => steps.Add(s), CancellationToken.None);
        Assert.True(pinned.Succeeded, pinned.Failure + "\n" + string.Join("\n", pinned.Log));
        Assert.Equal([RootlessAppWorkflow.Stage.Inspect, RootlessAppWorkflow.Stage.Package, RootlessAppWorkflow.Stage.Validate, RootlessAppWorkflow.Stage.Handoff], steps.Distinct().ToArray());
        var h = pinned.Handoff!;
        Assert.True(h.ImportRequestComplete);
        foreach (var f in new[] { "SHA256SUMS", "provenance.json", "control.txt", "VALIDATION.txt", "CHANGELOG.md", "KNOWN-ISSUES.md", "package.toml", "icon.png", "import-request.toml", "DEVICE-TEST.md", "README.md" })
            Assert.True(File.Exists(Path.Combine(h.Folder, f)), f);
        var req = File.ReadAllText(Path.Combine(h.Folder, "import-request.toml"));
        Assert.Contains("run_id = 44012345678", req);
        Assert.Contains("channel = \"candidate\"", req);
        Assert.Contains($"sha256 = \"{pinned.Validation!.Facts.Sha256}\"", req);
        var toml = File.ReadAllText(Path.Combine(h.Folder, "package.toml"));
        Assert.Contains("level = \"connect\"", toml);                         // production access unchanged
        Assert.Contains("exception_versions = [\"1.4.0~beta1\"]", toml);       // free only for this candidate version
        Assert.Contains("approved by the owner on 21 Nov 2026: 'test approval'", toml);
        Assert.DoesNotContain(h.Missing, m => m.StartsWith("owner approval", StringComparison.Ordinal));
        Assert.Contains("still require their own HCG sign-in", toml);
        Assert.Contains("\"device_tested\": false", File.ReadAllText(Path.Combine(h.Folder, "provenance.json")));

        // Built by another repository's run (Executable Maker's CI building Sentinel): the request names the run's
        // repository and the commit that run built; the app's source commit stays in provenance.json and a comment.
        var cross = await new RootlessAppWorkflow(runner, TimeProvider.System).RunAsync(
            Request(MachOFixture.App(_t.Combine("h3")), prov: new("F1r3srp3nt/HCG-Sentinel", new string('c', 40), false, 77, "hcg-sentinel-rootless-deb",
                CiRepository: "F1r3srp3nt/HCG-Executable-Maker", CiCommit: new string('d', 40))), null, CancellationToken.None);
        var crossReq = File.ReadAllText(Path.Combine(cross.Handoff!.Folder, "import-request.toml"));
        Assert.Contains("repository = \"F1r3srp3nt/HCG-Executable-Maker\"", crossReq);
        Assert.Contains($"commit = \"{new string('d', 40)}\"", crossReq);
        Assert.Contains($"# App source: F1r3srp3nt/HCG-Sentinel commit {new string('c', 40)}", crossReq);
        Assert.Contains(new string('c', 40), File.ReadAllText(Path.Combine(cross.Handoff.Folder, "provenance.json")));
        Assert.Contains("Not yet device-tested", File.ReadAllText(Path.Combine(h.Folder, "KNOWN-ISSUES.md")));
        var deviceTest = File.ReadAllText(Path.Combine(h.Folder, "DEVICE-TEST.md"));
        Assert.Contains("uicache -p /var/jb/Applications/HCGSentinel.app", deviceTest);
        Assert.Contains("## 1 · Detect existing HCG Sentinel installations", deviceTest);       // sideloaded / packaged / other builds
        Assert.Contains("uicache -l | grep -i -e com.hcg.sentinel -e sentinel", deviceTest);
        Assert.Contains("dpkg -l > /var/mobile/hcgsentinel-test-before-dpkg.txt", deviceTest);     // snapshot for rollback
        Assert.Contains($"must print `{pinned.Validation!.Facts.Sha256}`", deviceTest);         // the exact file is checked before install
        Assert.Contains("## 7 · Rollback and recovery", deviceTest);

        // Without the owner's approval of this version, the exception stays commented out — never invented — and is listed as missing.
        var crossToml = File.ReadAllText(Path.Combine(cross.Handoff.Folder, "package.toml"));
        Assert.Contains("level = \"public\"", crossToml);
        Assert.Contains("# NOT IMPORTABLE YET: no owner approval of 1.4.0~beta1", crossToml);
        Assert.Contains("# exception_versions = [\"1.4.0~beta1\"]", crossToml);
        Assert.DoesNotContain("\nexception", crossToml);
        Assert.Contains(cross.Handoff.Missing, m => m.StartsWith("owner approval", StringComparison.Ordinal));
        var undecided = await new RootlessAppWorkflow(runner, TimeProvider.System).RunAsync(Request(MachOFixture.App(_t.Combine("h4")), access: "undecided"), null, CancellationToken.None);
        Assert.Contains("level = \"connect\"", File.ReadAllText(Path.Combine(undecided.Handoff!.Folder, "package.toml")));

        var local = await new RootlessAppWorkflow(runner, TimeProvider.System).RunAsync(Request(MachOFixture.App(_t.Combine("h2"))), null, CancellationToken.None);
        Assert.False(local.Handoff!.ImportRequestComplete);
        Assert.True(File.Exists(Path.Combine(local.Handoff.Folder, "import-request.INCOMPLETE.toml")));
        Assert.Contains(local.Handoff.Missing, m => m.StartsWith("import-request.toml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_package_requests_are_refused_with_the_reason()
    {
        var app = IosAppBundle.Open(MachOFixture.App(_t.Combine("bad")), _t.Path);
        var problems = RootlessAppPackager.CheckSpec(Spec() with { PackageId = "Com.HCG Sentinel", Version = "beta", Maintainer = " " }, app);
        Assert.Contains(problems, p => p.Contains("not a valid Debian package name"));
        Assert.Contains(problems, p => p.Contains("not a valid Debian version"));
        Assert.Contains(problems, p => p.Contains("maintainer is required"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => { IosAppBundle.Open(_t.Write("x.txt", "x"), _t.Path); await Task.CompletedTask; });
    }
}
