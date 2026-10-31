using System.IO.Compression;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Icons;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;
using SkiaSharp;

namespace HCG.ExecutableMaker.Tests;

public class BinaryInspectorTests
{
    [Fact]
    public void Detects_pe_x64()
    {
        var b = new byte[256];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
        "PE\0\0"u8.CopyTo(b.AsSpan(0x80));
        BitConverter.GetBytes((ushort)0x8664).CopyTo(b, 0x84);
        var info = BinaryInspector.Inspect(b);
        Assert.Equal(BinaryFormat.PortableExecutable, info.Format);
        Assert.Equal(TargetArchitecture.X64, info.Architecture);
        Assert.Equal(TargetPlatform.Windows, info.Platform);
    }

    [Fact]
    public void Detects_macho_arm64_with_signature()
    {
        var info = BinaryInspector.Inspect(BuildServiceTests.FakeMachOArm64());
        Assert.Equal(BinaryFormat.MachO, info.Format);
        Assert.Equal(TargetArchitecture.Arm64, info.Architecture);
        Assert.True(info.HasCodeSignature);
    }

    [Fact]
    public void Detects_elf_x64()
    {
        var b = new byte[64];
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F';
        BitConverter.GetBytes((ushort)62).CopyTo(b, 18);
        Assert.Equal(TargetArchitecture.X64, BinaryInspector.Inspect(b).Architecture);
        Assert.Equal(BinaryFormat.Elf, BinaryInspector.Inspect(b).Format);
    }

    [Fact]
    public void Unknown_data_is_unknown() => Assert.Equal(BinaryFormat.Unknown, BinaryInspector.Inspect(new byte[64]).Format);
}

public class ChecksumTests
{
    [Fact]
    public async Task Sha256_of_known_content()
    {
        using var t = new TempDir();
        var f = t.Write("a.txt", "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await Core.Checksums.Sha256FileAsync(f));
    }

    [Fact]
    public async Task Directory_digest_is_deterministic_and_content_sensitive()
    {
        using var t = new TempDir();
        t.Write("d/b.txt", "b");
        t.Write("d/sub/a.txt", "a");
        var (d1, manifest, total) = await Core.Checksums.Sha256DirectoryAsync(t.Combine("d"));
        var (d2, _, _) = await Core.Checksums.Sha256DirectoryAsync(t.Combine("d"));
        Assert.Equal(d1, d2);
        Assert.Equal(2, total);
        Assert.StartsWith("b.txt\t1\t", manifest);
        t.Write("d/b.txt", "B");
        Assert.NotEqual(d1, (await Core.Checksums.Sha256DirectoryAsync(t.Combine("d"))).Digest);
    }
}

public class ZipPackagingTests
{
    [Fact]
    public void Unix_zip_records_permissions_and_unix_made_by()
    {
        using var t = new TempDir();
        var exe = t.Write("App", "bin");
        var dll = t.Write("App.dll", "il");
        var zip = t.Combine("out.zip");
        UnixZip.Create(zip, [(exe, "root/App", true), (dll, "root/App.dll", false)], ["root"]);

        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal(0b111_101_101, UnixZip.GetMode(archive.GetEntry("root/App")!));
            Assert.Equal(0b110_100_100, UnixZip.GetMode(archive.GetEntry("root/App.dll")!));
        }
        // Every central directory record must declare Unix (3) as the "made by" host.
        var bytes = File.ReadAllBytes(zip);
        var cdCount = 0;
        for (var i = 0; i + 4 < bytes.Length; i++)
            if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) { Assert.Equal(3, bytes[i + 5]); cdCount++; }
        Assert.Equal(3, cdCount);
    }

    [Fact]
    public void EnsureUnixMadeBy_patches_archives_from_other_hosts()
    {
        using var t = new TempDir();
        var zip = Zips.Create(t.Combine("w.zip"), ("a.txt", "x", null));
        var bytes = File.ReadAllBytes(zip);
        for (var i = 0; i + 4 < bytes.Length; i++)
            if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) bytes[i + 5] = 0; // pretend: made on Windows
        File.WriteAllBytes(zip, bytes);
        UnixZip.EnsureUnixMadeBy(zip);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Single(archive.Entries);
    }
}

public class MacBundleTests
{
    private static string MakeBundle(TempDir t, bool withExe = true, bool signed = true)
    {
        var bundle = t.Combine("Out", "My App.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), InfoPlist.Create([
            ("CFBundleExecutable", "App"), ("CFBundleIdentifier", "com.example.app"), ("CFBundleName", "My App"),
            ("CFBundlePackageType", "APPL"), ("CFBundleShortVersionString", "1.0.0"), ("CFBundleVersion", "1.0.0"), ("NSHighResolutionCapable", true),
        ]));
        if (withExe)
        {
            var exe = Path.Combine(bundle, "Contents", "MacOS", "App");
            var bytes = BuildServiceTests.FakeMachOArm64();
            if (!signed) BitConverter.GetBytes(0x19u).CopyTo(bytes, 32);
            File.WriteAllBytes(exe, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        }
        return bundle;
    }

    [Fact]
    public void Valid_bundle_passes_structure_checks_but_launch_is_not_claimed()
    {
        using var t = new TempDir();
        var checks = MacBundleVerifier.Verify(MakeBundle(t), TargetArchitecture.Arm64);
        Assert.DoesNotContain(checks, c => c.Status == VerificationStatus.Failed);
        Assert.Contains(checks, c => c.Name == "Launch test" && c.Status == VerificationStatus.NotVerifiable);
        Assert.Contains(checks, c => c.Name == "Code signature" && c.Detail.Contains("NOT notarized"));
    }

    [Fact]
    public void Renamed_directory_is_not_a_valid_bundle()
    {
        using var t = new TempDir();
        var fake = t.Combine("Fake.app");
        Directory.CreateDirectory(fake);
        Assert.Contains(MacBundleVerifier.Verify(fake, TargetArchitecture.Arm64), c => c.Status == VerificationStatus.Failed);
    }

    [Fact]
    public void Missing_executable_fails()
    {
        using var t = new TempDir();
        Assert.Contains(MacBundleVerifier.Verify(MakeBundle(t, withExe: false), TargetArchitecture.Arm64),
            c => c.Name == "Bundle executable" && c.Status == VerificationStatus.Failed);
    }

    [Fact]
    public void Unsigned_arm64_executable_fails()
    {
        using var t = new TempDir();
        Assert.Contains(MacBundleVerifier.Verify(MakeBundle(t, signed: false), TargetArchitecture.Arm64),
            c => c.Name == "Code signature" && c.Status == VerificationStatus.Failed);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3.4-beta", "1.2.3")]
    [InlineData("garbage", "1.0.0")]
    public void Bundle_version_is_numeric(string input, string expected) => Assert.Equal(expected, InfoPlist.ToBundleVersion(input));

    [Fact]
    public void Plist_values_are_xml_escaped()
    {
        using var t = new TempDir();
        var p = t.Write("Info.plist", InfoPlist.Create([("CFBundleName", "A & <B>")]));
        Assert.Equal("A & <B>", InfoPlist.Read(p)["CFBundleName"]);
    }
}

public class PackagingCatalogTests
{
    private static PackagingContext Ctx(TargetPlatform target, TargetPlatform host = TargetPlatform.Linux, bool gui = true) =>
        new(target, new HostEnvironment(host, TargetArchitecture.X64, "test", ".NET", "m"), Fixtures.Toolchains("10.0.114"), gui);

    [Fact]
    public void Windows_installers_are_not_offered_as_implemented()
    {
        var o = PackagingCatalog.Evaluate(Ctx(TargetPlatform.Windows));
        Assert.True(o.Single(x => x.Format == PackagingFormat.PortableFolder).IsAvailable);
        Assert.True(o.Single(x => x.Format == PackagingFormat.PortableZip).IsAvailable);
        foreach (var f in new[] { PackagingFormat.WindowsInstallerExe, PackagingFormat.WindowsMsi, PackagingFormat.WindowsMsix })
        {
            var opt = o.Single(x => x.Format == f);
            Assert.Equal(PackagingAvailability.NotImplemented, opt.Availability);
            Assert.False(string.IsNullOrEmpty(opt.Reason));
        }
    }

    [Fact]
    public void Dmg_requires_macos_host()
    {
        var o = PackagingCatalog.Evaluate(Ctx(TargetPlatform.MacOS));
        Assert.Equal(PackagingAvailability.RequiresDifferentHost, o.Single(x => x.Format == PackagingFormat.MacDmg).Availability);
        Assert.True(o.Single(x => x.Format == PackagingFormat.MacAppBundle).IsAvailable);
        var onMacWithoutHdiutil = PackagingCatalog.Evaluate(Ctx(TargetPlatform.MacOS, TargetPlatform.MacOS));
        Assert.Equal(PackagingAvailability.MissingTool, onMacWithoutHdiutil.Single(x => x.Format == PackagingFormat.MacDmg).Availability);
    }

    [Fact]
    public void Console_apps_do_not_get_app_bundles()
    {
        var o = PackagingCatalog.Evaluate(Ctx(TargetPlatform.MacOS, gui: false));
        Assert.Equal(PackagingAvailability.NotApplicable, o.Single(x => x.Format == PackagingFormat.MacAppBundle).Availability);
    }

    [Fact]
    public void Android_is_entirely_unimplemented() =>
        Assert.All(PackagingCatalog.Evaluate(Ctx(TargetPlatform.Android)), o => Assert.Equal(PackagingAvailability.NotImplemented, o.Availability));

    [Fact]
    public void Dmg_pulls_in_app_bundle_first() =>
        Assert.Equal([PackagingFormat.MacAppBundle, PackagingFormat.MacDmg], PackagingCatalog.WithDependencies([PackagingFormat.MacDmg]));
}

public class IconTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private string Png(int w, int h, bool alpha = true)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, alpha ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(alpha ? SKColors.Transparent : SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Red };
            c.DrawRect(0, 0, w, h, paint);
        }
        var path = _t.Combine($"icon-{w}x{h}-{alpha}.png");
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void Inspects_png_and_warns_on_small_or_non_square()
    {
        var info = IconInspector.Inspect(Png(300, 200));
        Assert.Equal(IconFormat.Png, info.Format);
        Assert.Equal(300, info.Largest!.Width);
        Assert.Contains(info.Warnings, w => w.Contains("not square"));
        Assert.Contains(info.Warnings, w => w.Contains("recommended"));
        Assert.True(info.IsValid);
    }

    [Fact]
    public void Rejects_unknown_files() => Assert.False(IconInspector.Inspect(_t.Write("x.png", "not an image")).IsValid);

    [Fact]
    public void Generates_ico_and_icns_that_round_trip()
    {
        var src = Png(1024, 1024);
        var original = File.ReadAllBytes(src);
        var result = IconGenerator.Generate(src, _t.Combine("gen"), "AppIcon", ico: true, icns: true);

        Assert.Equal(original, File.ReadAllBytes(src)); // source untouched
        Assert.Equal(original, File.ReadAllBytes(result.SourceCopy));
        var ico = IconInspector.Inspect(result.IcoPath!);
        Assert.Equal(IconFormat.Ico, ico.Format);
        Assert.Equal(IconGenerator.IcoSizes, ico.Images.Select(i => i.Width).ToArray());
        Assert.True(ico.HasTransparency);
        var icns = IconInspector.Inspect(result.IcnsPath!);
        Assert.Equal(IconFormat.Icns, icns.Format);
        Assert.Equal(1024, icns.Largest!.Width);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Does_not_upscale_small_sources()
    {
        var result = IconGenerator.Generate(Png(64, 64), _t.Combine("gen"), "AppIcon", ico: true, icns: true);
        Assert.True(IconInspector.Inspect(result.IcoPath!).Images.Max(i => i.Width) <= 64);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Non_square_art_is_letterboxed_not_stretched()
    {
        using var src = new SKBitmap(200, 100);
        using (var c = new SKCanvas(src)) c.Clear(SKColors.Blue);
        var png = IconGenerator.RenderPng(src, 100);
        using var rendered = SKBitmap.Decode(png);
        Assert.Equal(0, rendered.GetPixel(50, 5).Alpha);    // transparent band above the art
        Assert.Equal(255, rendered.GetPixel(50, 50).Alpha); // art in the middle
    }
}
