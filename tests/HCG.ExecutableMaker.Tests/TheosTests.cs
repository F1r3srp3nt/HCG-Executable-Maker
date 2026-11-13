using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Native;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Theos;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Native;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Theos (jailbreak packages): detection from HCG Nexus' real Makefiles, readiness, the build plan and .deb verification.</summary>
public class TheosTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string Fixture => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "Theos", "NexusLike");

    /// <summary>The fixture copied to a folder whose path contains spaces.</summary>
    private string Repo()
    {
        var dir = _t.Combine("HCG Nexus repo", "source");
        if (!Directory.Exists(dir)) CopyDir(Fixture, dir);
        return dir;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static NativeAnalysis Scan(string dir) => NativeProjectScanner.Scan(SourceIndex.Build(dir, CancellationToken.None));

    [Fact]
    public void Nexus_is_a_theos_tweak_with_a_preference_bundle_and_a_separately_packaged_preview_app()
    {
        var native = Scan(Repo());
        Assert.Equal(2, native.Projects.Count);
        Assert.All(native.Projects, p => Assert.Equal(NativeProjectKind.Theos, p.Kind));   // Theos before generic Make: never a Make project
        var root = native.Projects.Single(p => p.Directory == "");
        var t = root.Theos!;
        Assert.Null(root.UnsupportedReason);
        Assert.Equal(("com.hakmicapitalgroup.hcgnexus", "HCG Nexus", "0.1.0"), (t.PackageId, t.PackageName, t.PackageVersion));
        Assert.Equal("mobilesubstrate | ellekit, preferenceloader", t.Depends);
        Assert.Equal(["Prefs"], t.Subprojects);                                   // part of this package, not a separate project
        Assert.Collection(t.Instances,
            i => Assert.Equal((TheosInstanceType.Tweak, "HCGNexus", "HCGNexus.plist"), (i.Type, i.Name, i.FilterPlist)),
            i => Assert.Equal((TheosInstanceType.PreferenceBundle, "HCGNexusPrefs", "/Library/PreferenceBundles"), (i.Type, i.Name, i.InstallPath)));
        Assert.Equal(["arm64", "arm64e"], t.Architectures);
        Assert.Equal("HCG_ARCHS", t.ArchitectureVariable);
        Assert.True(t.UsesParentDirectory);                                         // Prefs uses ROOT := .. → the repository layout must be kept

        var preview = native.Projects.Single(p => p.Directory == "PreviewHarness");
        Assert.Equal((TheosInstanceType.Application, "HCGNexusPreview"), (preview.Theos!.Instances.Single().Type, preview.Theos.Instances.Single().Name));
        Assert.Equal("com.hakmicapitalgroup.hcgnexus.preview", preview.Theos.PackageId);
    }

    [Fact]
    public void The_configurations_are_the_ones_the_makefile_defines_with_their_own_sdk_and_deployment_target()
    {
        var t = Scan(Repo()).Projects.Single(p => p.Directory == "").Theos!;
        var rootless = t.Schemes.Single(s => s.SelectorValue == "rootless");
        Assert.Equal(("HCG_ENV", "rootless", "16.5", "15.0", true), (rootless.SelectorVariable, rootless.PackageScheme, rootless.SdkVersion, rootless.DeploymentTarget, rootless.IsDefault));
        Assert.Equal(("/var/jb", "iphoneos-arm64"), (rootless.InstallPrefix, rootless.DebArchitecture));
        var rootful = t.Schemes.Single(s => s.SelectorValue == "rootful");
        Assert.Equal(("rootful", "14.5", "14.0", false), (rootful.PackageScheme, rootful.SdkVersion, rootful.DeploymentTarget, rootful.IsDefault));
        Assert.Equal(("/", "iphoneos-arm"), (rootful.InstallPrefix, rootful.DebArchitecture));
        var roothide = t.Schemes.Single(s => s.SelectorValue == "roothide");
        Assert.Contains("roothide needs the roothide fork of Theos", roothide.ProjectError);  // the project's own refusal, verbatim
        Assert.Equal(SupportLevel.RequiresConfiguration, NativeCapabilities.EvaluateTheos(Scan(Repo()).Projects[0], roothide, Report()).Support);
    }

    private static ToolchainReport Report(params string[] missing)
    {
        ToolchainInfo Row(string id, string? path, params string[] extra) => missing.Contains(id)
            ? new ToolchainInfo { Id = id, DisplayName = id, Category = "Theos", Status = ToolchainStatus.Missing, Detail = $"{id} was not found.", InstallHint = $"Install {id}." }
            : new ToolchainInfo { Id = id, DisplayName = id, Category = "Theos", Status = ToolchainStatus.Installed, ExecutablePath = path, Version = "1", AdditionalVersions = extra };
        return new ToolchainReport(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"),
        [
            Row(ToolchainIds.Theos, "/opt/theos", "iPhoneOS16.5.sdk"), Row(ToolchainIds.TheosMake, "/opt/homebrew/bin/gmake"), Row(ToolchainIds.Ldid, "/opt/homebrew/bin/ldid"),
            Row(ToolchainIds.Perl, "/usr/bin/perl"), Row(ToolchainIds.TheosIosToolchain, "/Applications/Xcode.app/.../clang"),
        ], null, null, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Missing_dependencies_are_reported_one_by_one_including_the_sdk_a_configuration_needs()
    {
        var p = Scan(Repo()).Projects.Single(x => x.Directory == "");
        var rootless = p.Theos!.Schemes.Single(s => s.SelectorValue == "rootless");
        Assert.Equal(SupportLevel.Supported, NativeCapabilities.EvaluateTheos(p, rootless, Report()).Support);

        var missing = NativeCapabilities.EvaluateTheos(p, rootless, Report(ToolchainIds.Ldid, ToolchainIds.TheosMake));
        Assert.Equal(SupportLevel.MissingToolchain, missing.Support);
        Assert.Contains(missing.Remediation, r => r.StartsWith("ldid:", StringComparison.Ordinal));
        Assert.Contains(missing.Remediation, r => r.StartsWith("GNU make 4+:", StringComparison.Ordinal));
        Assert.Equal(2, missing.Remediation.Count);

        // rootful builds against iPhoneOS 14.5, which is not in $THEOS/sdks: that alone is named, with the fix.
        var rootful = NativeCapabilities.EvaluateTheos(p, p.Theos.Schemes.Single(s => s.SelectorValue == "rootful"), Report());
        Assert.Equal(SupportLevel.MissingToolchain, rootful.Support);
        Assert.Contains("iPhoneOS 14.5 SDK", Assert.Single(rootful.Remediation));
        Assert.Contains(rootful.Requirements, r => r is { DisplayName: "iPhoneOS 14.5 SDK", IsSatisfied: false });

        var windows = Report() with { Host = new HostEnvironment(TargetPlatform.Windows, TargetArchitecture.X64, "Windows", ".NET", "win") };
        Assert.Equal(SupportLevel.RequiresDifferentHost, NativeCapabilities.EvaluateTheos(p, rootless, windows).Support);
    }

    [Fact]
    public void Theos_and_its_sdks_are_found_in_the_standard_places()
    {
        var theos = _t.Combine("theos");
        Directory.CreateDirectory(Path.Combine(theos, "makefiles"));
        File.WriteAllText(Path.Combine(theos, "makefiles", "common.mk"), "");
        foreach (var v in new[] { "14.5", "16.5" }) Directory.CreateDirectory(Path.Combine(theos, "sdks", $"iPhoneOS{v}.sdk"));
        Assert.Equal(theos, HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector.FindTheos(null, _t.Path));
        Assert.Equal(theos, HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector.FindTheos(theos, "/nonexistent"));
        Assert.Null(HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector.FindTheos(_t.Combine("nothing"), "/nonexistent"));
        Assert.Equal(["16.5", "14.5"], HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector.TheosSdks(theos));
    }

    private static TheosSchemeChoice Choice(NativeProjectInfo p, string value) => p.Theos!.Schemes.Single(s => s.SelectorValue == value) is var s
        ? new TheosSchemeChoice
        {
            SelectorVariable = s.SelectorVariable, SelectorValue = s.SelectorValue, PackageScheme = s.PackageScheme, SdkVersion = s.SdkVersion, DeploymentTarget = s.DeploymentTarget,
            Architectures = p.Theos.Architectures, PackageId = p.Theos.PackageId, PackageVersion = p.Theos.PackageVersion, Instances = p.Theos.Instances,
        }
        : null!;

    private static BuildSettings Settings(string projectDir, TheosSchemeChoice choice) => new()
    {
        ProjectRelativePath = projectDir, Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = "/tmp",
        Identity = new AppIdentity { ApplicationName = "HCG Nexus", Version = "0.1.0", ApplicationId = "com.hakmicapitalgroup.hcgnexus" },
        Native = new NativeBuildOptions { ProjectDirectory = projectDir, Kind = NativeProjectKind.Theos, Target = "package", TheosScheme = choice },
        PackagingFormats = [PackagingFormat.JailbreakDeb],
    };

    [Fact]
    public void The_build_runs_make_package_in_a_space_free_copy_of_the_whole_repository_with_one_folder_per_configuration()
    {
        var repo = Repo();
        var p = Scan(repo).Projects.Single(x => x.Directory == "PreviewHarness");
        var host = new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux");
        var work = _t.Combine("HCG data", "builds", "b1", "native");                     // HCG's own folder has a space (like ~/Library/Application Support)
        var rootless = Settings("PreviewHarness", Choice(p, "rootless"));
        TheosBuildPlanner.Validate(rootless, host, repo);
        var prep = TheosBuildPlanner.Prepare(rootless, repo, work, copyWholeSource: true, CancellationToken.None);
        try
        {
            Assert.True(prep.IsTemporary);
            Assert.DoesNotContain(' ', prep.WorkRoot);
            Assert.True(File.Exists(Path.Combine(prep.ProjectDirectory, "Makefile")));
            Assert.True(File.Exists(Path.Combine(prep.ProjectDirectory, "..", "Sources", "Core", "HCGCore.m")));   // ../Sources still resolves
            var step = TheosBuildPlanner.Plan(rootless, prep, new TheosBuildPlanner.Tools("/opt/theos", "/usr/bin/make"), new Dictionary<string, string>());
            Assert.Equal("/usr/bin/make", step.Process.Executable);
            Assert.Equal(["-C", prep.ProjectDirectory, "package", "FINALPACKAGE=1", "THEOS_PACKAGE_DIR=" + prep.PackageDirectory, "messages=yes", "HCG_ENV=rootless"], step.Process.Arguments);
            Assert.DoesNotContain(step.Process.Arguments, a => a is "install" or "do" || a.StartsWith("THEOS_DEVICE", StringComparison.Ordinal));
            Assert.Equal("/opt/theos", step.Process.Environment!["THEOS"]);

            var rootful = TheosBuildPlanner.Prepare(Settings("PreviewHarness", Choice(p, "rootful")), repo, work, copyWholeSource: true, CancellationToken.None);
            Assert.NotEqual(prep.WorkRoot, rootful.WorkRoot);                                   // rootless and rootful never share a folder
            Assert.EndsWith("hcg-env-rootless-rootless", prep.WorkRoot);
            Assert.EndsWith("hcg-env-rootful-rootful", rootful.WorkRoot);
            Directory.Delete(Path.GetDirectoryName(rootful.WorkRoot)!, true);
        }
        finally { Directory.Delete(Path.GetDirectoryName(prep.WorkRoot)!, true); }

        Assert.Throws<BuildValidationException>(() => TheosBuildPlanner.Validate(rootless with { Platform = TargetPlatform.MacOS }, host, repo));   // not a desktop build
        Assert.Throws<BuildValidationException>(() => TheosBuildPlanner.Validate(Settings("PreviewHarness", Choice(p, "rootless") with { SelectorValue = "x; rm -rf /" }), host, repo));
        Assert.Throws<BuildValidationException>(() => TheosBuildPlanner.Validate(rootless, new HostEnvironment(TargetPlatform.Windows, TargetArchitecture.X64, "Windows", ".NET", "win"), repo));
    }

    // ------------------------------------------------------------------ .deb verification

    private const uint CpuArm64 = 0x0100000C;

    /// <summary>A thin arm64 Mach-O dylib header with LC_BUILD_VERSION (iOS, given minimum).</summary>
    private static byte[] Thin(uint subtype, uint minos = 0x000F0000, uint fileType = 6)
    {
        var b = new byte[32 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), CpuArm64);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), subtype);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), fileType);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 0x32);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), 2);        // PLATFORM_IOS
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), minos);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48), minos);
        return b;
    }

    private static byte[] Fat(params byte[][] slices)
    {
        var header = 8 + 20 * slices.Length;
        var offset = (header + 0x3FFF) & ~0x3FFF;
        var output = new MemoryStream();
        var h = new byte[header];
        BinaryPrimitives.WriteUInt32BigEndian(h, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)slices.Length);
        var offsets = new List<int>();
        for (var i = 0; i < slices.Length; i++)
        {
            offsets.Add(offset);
            var e = h.AsSpan(8 + 20 * i);
            BinaryPrimitives.WriteUInt32BigEndian(e, CpuArm64);
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], BinaryPrimitives.ReadUInt32LittleEndian(slices[i].AsSpan(8)));
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)slices[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], 14);
            offset = (offset + slices[i].Length + 0x3FFF) & ~0x3FFF;
        }
        output.Write(h);
        for (var i = 0; i < slices.Length; i++) { output.SetLength(offsets[i]); output.Position = offsets[i]; output.Write(slices[i]); }
        return output.ToArray();
    }

    private static byte[] NexusBinary(bool newAbi = true) => Fat(Thin(0), Thin(newAbi ? 0x80000002u | (0u << 24) : 2u));

    /// <summary>A Debian package like Theos' dm.pl writes: debian-binary, control.tar.gz, data.tar.lzma.</summary>
    private string Deb(string name, string control, IEnumerable<(string Path, byte[] Data)> files, string compression = "lzma", params (string Name, string Text)[] scripts)
    {
        byte[] Tar(IEnumerable<(string Path, byte[] Data)> entries)
        {
            using var ms = new MemoryStream();
            using (var w = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true))
            {
                w.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./"));     // dm.pl and dpkg-deb write the archive root first (CI run 74)
                foreach (var (path, data) in entries)
                    w.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./" + path) { DataStream = new MemoryStream(data), Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute });
            }
            return ms.ToArray();
        }
        byte[] Gzip(byte[] data) { using var ms = new MemoryStream(); using (var gz = new GZipStream(ms, CompressionLevel.Fastest)) gz.Write(data); return ms.ToArray(); }
        byte[] Compress(byte[] data)
        {
            if (compression == "gz") return Gzip(data);
            var tmp = _t.Combine(Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(tmp, data);
            using var p = Process.Start(new ProcessStartInfo("xz", ["--format=" + (compression == "lzma" ? "lzma" : "xz"), "-k", "-f", tmp]) { UseShellExecute = false, RedirectStandardInput = true })!;
            p.StandardInput.Close();
            if (!p.WaitForExit(60_000)) { p.Kill(entireProcessTree: true); throw new TimeoutException("xz did not finish within 60 s"); }
            Assert.Equal(0, p.ExitCode);
            return File.ReadAllBytes(tmp + (compression == "lzma" ? ".lzma" : ".xz"));
        }
        var controlTar = Gzip(Tar([("control", Encoding.UTF8.GetBytes(control)), .. scripts.Select(s => (s.Name, Encoding.UTF8.GetBytes(s.Text)))]));
        var dataTar = Compress(Tar(files));
        var path = _t.Combine(name);
        using var fs = File.Create(path);
        fs.Write("!<arch>\n"u8);
        void Member(string n, byte[] data)
        {
            var header = $"{n,-16}{"0",-12}{"0",-6}{"0",-6}{"100644",-8}{data.Length,-10}`\n";
            fs.Write(Encoding.ASCII.GetBytes(header));
            fs.Write(data);
            if (data.Length % 2 == 1) fs.WriteByte((byte)'\n');
        }
        Member("debian-binary", "2.0\n"u8.ToArray());
        Member("control.tar.gz", controlTar);
        Member(compression == "gz" ? "data.tar.gz" : "data.tar." + compression, dataTar);
        return path;
    }

    private static string Control(string arch) => $"Package: com.hakmicapitalgroup.hcgnexus\nName: HCG Nexus\nVersion: 0.1.0\nArchitecture: {arch}\nMaintainer: Hakmi Capital Group\nDepends: mobilesubstrate | ellekit, preferenceloader\n";

    private static IEnumerable<(string, byte[])> NexusPayload(string prefix, bool newAbi = true) =>
    [
        ($"{prefix}Library/MobileSubstrate/DynamicLibraries/HCGNexus.dylib", NexusBinary(newAbi)),
        ($"{prefix}Library/MobileSubstrate/DynamicLibraries/HCGNexus.plist", "{ Filter = { Bundles = ( \"com.apple.springboard\" ); }; }\n"u8.ToArray()),
        ($"{prefix}Library/PreferenceBundles/HCGNexusPrefs.bundle/HCGNexusPrefs", Fat(Thin(0, fileType: 8), Thin(0x80000002u, fileType: 8))),
        ($"{prefix}Library/PreferenceLoader/Preferences/HCGNexusPrefs.plist", "<plist/>"u8.ToArray()),
    ];

    private static bool HasXz => File.Exists("/usr/bin/xz") || File.Exists("/opt/homebrew/bin/xz") || File.Exists("/usr/local/bin/xz");

    [Fact]
    public async Task A_rootless_package_like_theos_builds_passes_every_check()
    {
        if (!HasXz) return; // building the lzma fixture needs xz (the verifier itself uses the system tar)
        var p = Scan(Repo()).Projects.Single(x => x.Directory == "");
        var deb = Deb("com.hakmicapitalgroup.hcgnexus_0.1.0_iphoneos-arm64.deb", Control("iphoneos-arm64"), NexusPayload("var/jb/"));
        var result = await TheosDebVerifier.VerifyAsync(deb, Choice(p, "rootless"), new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner(), CancellationToken.None);
        Assert.All(result.Checks.Where(c => c.Name != "Installation"), c => Assert.True(c.Status == VerificationStatus.Passed, $"{c.Name}: {c.Detail}"));
        Assert.Contains(result.Checks, c => c.Name == "Tweak HCGNexus" && c.Detail.Contains("/var/jb/Library/MobileSubstrate/DynamicLibraries/HCGNexus.dylib"));
        Assert.Contains(result.Checks, c => c.Name == "HCGNexus binary" && c.Detail.Contains("arm64, arm64e (ptrauth v0)") && c.Detail.Contains("iOS 15.0+"));
        Assert.Contains(result.Checks, c => c.Name == "HCGNexus filter" && c.Detail == "Injected into: com.apple.springboard");
        Assert.Contains(result.Checks, c => c.Name == "Package kind" && c.Detail.Contains("not an IPA"));
        Assert.Equal(VerificationStatus.NotVerifiable, result.Checks.Single(c => c.Name == "Installation").Status);
        Assert.Contains("/var/jb/Library/PreferenceBundles/HCGNexusPrefs.bundle/HCGNexusPrefs", result.Report);
    }

    [Fact]
    public async Task Wrong_layout_architecture_field_missing_files_old_arm64e_abi_and_scripts_are_each_reported()
    {
        if (!HasXz) return;
        var p = Scan(Repo()).Projects.Single(x => x.Directory == "");
        var runner = new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner();

        // A rootful-layout payload with the rootful Architecture field, verified as rootless.
        var rootfulDeb = Deb("a.deb", Control("iphoneos-arm"), NexusPayload(""));
        var r = await TheosDebVerifier.VerifyAsync(rootfulDeb, Choice(p, "rootless"), runner, CancellationToken.None);
        Assert.Equal(VerificationStatus.Failed, r.Checks.Single(c => c.Name == "Package architecture").Status);
        Assert.Contains("must say iphoneos-arm64", r.Checks.Single(c => c.Name == "Package architecture").Detail);
        Assert.Equal(VerificationStatus.Failed, r.Checks.Single(c => c.Name == "Install location").Status);
        Assert.Contains("outside /var/jb", r.Checks.Single(c => c.Name == "Install location").Detail);
        Assert.Contains("Missing from the payload: /var/jb/Library/MobileSubstrate/DynamicLibraries/HCGNexus.dylib", r.Checks.Single(c => c.Name == "Tweak HCGNexus").Detail);

        // Old (Linux-toolchain) arm64e ABI with an iOS 15 deployment target: fails, with the reason and the fix.
        var oldAbi = Deb("b.deb", Control("iphoneos-arm64"), NexusPayload("var/jb/", newAbi: false), "xz", ("postinst", "#!/bin/sh\nkillall SpringBoard\n"));
        r = await TheosDebVerifier.VerifyAsync(oldAbi, Choice(p, "rootless"), runner, CancellationToken.None);
        var binary = r.Checks.Single(c => c.Name == "HCGNexus binary");
        Assert.Equal(VerificationStatus.Failed, binary.Status);
        Assert.Contains("old (unversioned) pointer-authentication ABI", binary.Detail);
        Assert.Equal(VerificationStatus.PassedWithWarnings, r.Checks.Single(c => c.Name == "Maintainer scripts").Status);
        Assert.Contains("postinst run as root", r.Checks.Single(c => c.Name == "Maintainer scripts").Detail);
    }

    [Fact]
    public async Task A_gzip_payload_with_the_archive_root_entry_is_read_and_an_entry_outside_the_root_is_refused()
    {
        var p = Scan(Repo()).Projects.Single(x => x.Directory == "");
        var deb = Deb("gz.deb", Control("iphoneos-arm64"), NexusPayload("var/jb/"), "gz");
        var r = await TheosDebVerifier.VerifyAsync(deb, Choice(p, "rootless"), new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner(), CancellationToken.None);
        Assert.Equal(VerificationStatus.Passed, r.Checks.Single(c => c.Name == "Install location").Status);
        Assert.Contains("var/jb/Library/MobileSubstrate/DynamicLibraries/HCGNexus.dylib", r.Payload);

        using var evil = new MemoryStream();
        using (var w = new TarWriter(evil, TarEntryFormat.Gnu, leaveOpen: true))
            w.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "../escape") { DataStream = new MemoryStream([1]) });
        evil.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => TheosDebVerifier.ExtractTarAsync(evil, _t.Combine("x"), CancellationToken.None));
        Assert.False(File.Exists(_t.Combine("escape")));
    }

    [Fact]
    public async Task A_file_that_is_not_a_debian_package_is_rejected()
    {
        var p = Scan(Repo()).Projects.Single(x => x.Directory == "");
        var fake = _t.Write("fake.deb", "PK\u0003\u0004 an IPA renamed to .deb");
        var r = await TheosDebVerifier.VerifyAsync(fake, Choice(p, "rootless"), new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner(), CancellationToken.None);
        Assert.Equal(VerificationStatus.Failed, Assert.Single(r.Checks).Status);
        Assert.Contains("ar signature", r.Checks[0].Detail);
    }

    [Fact]
    public void Arm64e_slices_report_whether_they_carry_the_ios14_pointer_authentication_abi()
    {
        var path = _t.Combine("bin");
        File.WriteAllBytes(path, Fat(Thin(0), Thin(0x81000002u), Thin(2)));
        var slices = AppleMachO.Read(path);
        Assert.Equal(["arm64", "arm64e", "arm64e"], slices.Select(s => s.Architecture));
        Assert.Equal((true, 1), (slices[1].PtrAuthVersioned, slices[1].PtrAuthAbiVersion!.Value));
        Assert.False(slices[2].PtrAuthVersioned);
        Assert.All(slices, s => Assert.Equal(("iOS", "15.0", AppleMachO.FileType.Dylib), (s.Platform, s.MinimumOS, s.Type)));
    }

    [Fact]
    public void The_ios_clang_for_theos_is_found_in_the_developer_directory_without_starting_xcrun()
    {
        var dev = _t.Combine("Xcode.app", "Contents", "Developer");
        var clang = Path.Combine(dev, "Toolchains", "XcodeDefault.xctoolchain", "usr", "bin", "clang");
        Directory.CreateDirectory(Path.GetDirectoryName(clang)!);
        File.WriteAllText(clang, "");
        Assert.Contains("no iPhoneOS SDK", HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector.LocateXcodeIosClang(dev).Problem);
        Directory.CreateDirectory(Path.Combine(dev, "Platforms", "iPhoneOS.platform", "Developer", "SDKs", "iPhoneOS26.2.sdk"));
        Assert.Equal((clang, "iPhoneOS26.2.sdk"), HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector.LocateXcodeIosClang(dev) is var r ? (r.Clang, r.Sdk) : default);
        Assert.Contains("Command Line Tools", HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector.LocateXcodeIosClang("/Library/Developer/CommandLineTools").Problem);
        Assert.Null(HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector.LocateXcodeIosClang(null).Clang);
    }

    [Fact]
    public void Theos_errors_are_reported_with_their_message()
    {
        var parser = new NativeOutputParser();
        var m = parser.Parse("==> Error: The vendor/include and/or vendor/lib directories are missing. Please run `$THEOS/bin/update-theos`.");
        Assert.Equal((DiagnosticSeverity.Error, "Theos"), (m!.Severity, m.Code));
        Assert.StartsWith("The vendor/include", m.Text);
        var e = parser.Parse("Makefile:28: *** Set HCG_ENV=rootless or HCG_ENV=rootful after confirming your jailbreak layout (see docs/ENVIRONMENT.md).  Stop.");
        Assert.Equal(("Makefile", 28), (e!.File, e.Line));
    }
}
