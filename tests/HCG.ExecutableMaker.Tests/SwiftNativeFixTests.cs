using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Native Swift build fixes: configured identity preserved, resource placement, test prerequisites, build identity.</summary>
public class SwiftNativeFixTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string Script(string name) => File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "OsloScripts", name));

    [Fact]
    public void Distil_script_identity_is_read_without_running_it()
    {
        var h = PackagingScriptHints.Parse(Script("distil-build-macos.sh"), "scripts/build-macos.sh");
        Assert.Equal("Distil", h.Product);
        Assert.Equal("dev.tokenlab.prototype", h.BundleId);
        Assert.Equal("Distil", h.DisplayName);
        Assert.Equal("0.5.0", h.Version);
        Assert.Equal("5", h.BuildNumber);
        Assert.Equal("14.0", h.MinimumMacOS);
        Assert.Equal("public.app-category.developer-tools", h.Category);
        Assert.Equal(["DistilGitCommit", "DistilBuildDate"], h.ComputedCustomKeys.ToArray());
    }

    [Fact]
    public void Relay_script_identity_includes_its_privacy_text()
    {
        var h = PackagingScriptHints.Parse(Script("relay-build-app.sh"), "scripts/build-app.sh");
        Assert.Equal("Relay", h.Product);
        Assert.Equal("com.oslosprint.relay", h.BundleId);
        Assert.Equal("0.2.0", h.Version);
        Assert.Equal("2", h.BuildNumber);
        Assert.StartsWith("Relay records only while you hold the push-to-talk button", h.UsageDescriptions["NSMicrophoneUsageDescription"], StringComparison.Ordinal);
    }

    [Fact]
    public void Hints_are_matched_to_the_selected_product()
    {
        _t.Write("relay/Package.swift", "// swift-tools-version: 5.9\n");
        _t.Write("relay/scripts/build-app.sh", Script("relay-build-app.sh"));
        _t.Write("relay/scripts/check-prereqs.sh", "#!/bin/bash\nVERSION=\"9\"\n");
        var index = SourceIndex.Build(_t.Path);
        Assert.Equal("com.oslosprint.relay", PackagingScriptHints.Read(index, "relay", "Relay")!.BundleId);
        Assert.Null(PackagingScriptHints.Read(index, "relay", "relay-runner")); // that script packages a different product
    }

    private static BuildSettings Settings() => new()
    {
        ProjectRelativePath = "Package.swift", Platform = TargetPlatform.MacOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = "/tmp/o",
        Identity = new AppIdentity { ApplicationName = "Resource Probe", Version = "1.0.0", ApplicationId = "com.example.probe" },
        Swift = new SwiftBuildOptions { PackageDirectory = "", Product = "ResourceProbe", Kind = SwiftAppKind.GuiApp, MinimumMacOS = "14.0" },
    };

    [Fact]
    public void Resource_bundles_must_be_at_the_app_root_where_Bundle_module_looks()
    {
        var app = _t.Combine("Resource Probe.app");
        _t.Write("Resource Probe.app/Contents/Info.plist", InfoPlist.Create(
        [
            ("CFBundleExecutable", "ResourceProbe"), ("CFBundleIdentifier", "com.example.probe"), ("CFBundleName", "Resource Probe"),
            ("CFBundlePackageType", "APPL"), ("CFBundleShortVersionString", "1.0.0"), ("CFBundleVersion", "1"), ("LSMinimumSystemVersion", "14.0"),
        ]));
        Directory.CreateDirectory(_t.Combine("Resource Probe.app", "Contents", "MacOS"));
        File.WriteAllBytes(_t.Combine("Resource Probe.app", "Contents", "MacOS", "ResourceProbe"), SwiftTests.ThinMachO(0x0100000C, 0x000E0000));
        FileOps.MarkExecutable(_t.Combine("Resource Probe.app", "Contents", "MacOS", "ResourceProbe"));
        string[] bundles = ["ResourceProbe_ProbeKit.bundle", "ResourceProbe_ResourceProbeApp.bundle"];

        // Inside Contents/Resources (where Distil's own script would put them) Bundle.module cannot find them.
        foreach (var b in bundles) _t.Write($"Resource Probe.app/Contents/Resources/{b}/greeting.txt", "hi");
        Assert.Contains(SwiftBundleVerifier.VerifyStructure(app, Settings(), bundles), c => c.Name == "Resource bundles" && c.Status == VerificationStatus.Failed);

        foreach (var b in bundles) _t.Write($"Resource Probe.app/{b}/greeting.txt", "hi");
        var check = Assert.Single(SwiftBundleVerifier.VerifyStructure(app, Settings(), bundles), c => c.Name == "Resource bundles");
        Assert.Equal(VerificationStatus.Passed, check.Status);
        Assert.Contains("Bundle.main.bundleURL", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_XCTest_never_makes_a_package_unbuildable()
    {
        var pkg = new SwiftPackageInfo { Directory = "", ManifestPath = "Package.swift", ToolsVersion = "5.9", MinimumMacOSHint = "14.0" };
        var withoutXcTest = SwiftCapabilities.Evaluate(pkg, SwiftTests.MacReport());
        Assert.Equal(SupportLevel.Supported, withoutXcTest.Support);
        var row = Assert.Single(withoutXcTest.Requirements, r => r.ToolchainId == ToolchainIds.XcTest);
        Assert.False(row.IsSatisfied);
        Assert.Contains("only to run tests", row.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain(withoutXcTest.Remediation, r => r.Contains("XCTest", StringComparison.Ordinal));
    }

    [Fact]
    public void Running_build_identifies_itself()
    {
        Assert.False(string.IsNullOrWhiteSpace(HostInfo.ProductCommit));
        Assert.Matches(@"^HCG Executable Maker \d+\.\d+\.\d+ \(commit .+, built .+\)$", HostInfo.BuildIdentity);
        Assert.True(File.Exists(HostInfo.ExecutablePath));
        Assert.Null(HostInfo.AppBundlePath); // tests do not run from an .app
    }
}
