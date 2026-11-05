using System.Diagnostics;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

public sealed class MacProjectPortTests : IDisposable
{
    private readonly TempDir _temp = new();
    private const string Project = "HelloIOS.xcodeproj";
    private readonly string _projectFile;

    public MacProjectPortTests()
    {
        var fixture = Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "HelloIOS");
        foreach (var path in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories))
            _temp.Write(Path.GetRelativePath(fixture, path), File.ReadAllText(path));
        _projectFile = _temp.Combine(Project, "project.pbxproj");
        File.WriteAllText(_projectFile, File.ReadAllText(_projectFile)
            .Replace("SDKROOT = iphoneos;", "SDKROOT = macosx;")
            .Replace("IPHONEOS_DEPLOYMENT_TARGET = 16.0;", "MACOSX_DEPLOYMENT_TARGET = 13.0;"));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ConvertsSettingsAndRetainsTargetMembershipAndScheme()
    {
        var before = File.ReadAllText(_projectFile);
        _temp.Write("MacOnly.swift", "import AppKit");
        var port = MacProjectPort.PrepareCopy(_temp.Path, Project, "com.hakmicapital.helloios");
        Assert.Contains("MacOnly.swift", port.ReviewFiles);
        var after = File.ReadAllText(port.ProjectFile);
        Assert.DoesNotContain("MACOSX_DEPLOYMENT_TARGET", after);
        Assert.Contains("HelloIOSApp.swift in Sources", after);
        Assert.NotEqual(before, after);
        var info = XcodeProjectAnalyzer.Analyze(SourceIndex.Build(_temp.Path), Project, out var diagnostics);
        Assert.Equal("16.0", info.AppTargets.Single().DeploymentTarget);
        Assert.Equal("HelloIOS", info.Schemes.Single().Name);
        Assert.Empty(diagnostics);
        Assert.Throws<InvalidDataException>(() => MacProjectPort.PrepareCopy(_temp.Path, Project));
        Assert.Equal(after, File.ReadAllText(_projectFile));
    }

    [Fact]
    public void MismatchedBundleIsRefusedBeforeEditing()
    {
        var before = File.ReadAllText(_projectFile);
        Assert.Throws<InvalidDataException>(() => MacProjectPort.PrepareCopy(_temp.Path, Project, "other.app"));
        Assert.Equal(before, File.ReadAllText(_projectFile));
    }

    [RequiresXcodeFact]
    public async Task MacRunnerBuildsThePortForAnIosSimulator()
    {
        MacProjectPort.PrepareCopy(_temp.Path, Project);
        var start = new ProcessStartInfo("/usr/bin/xcodebuild") { UseShellExecute = false };
        foreach (var arg in new[] { "-project", _temp.Combine(Project), "-scheme", "HelloIOS", "-sdk", "iphonesimulator",
                     "-destination", "generic/platform=iOS Simulator", "-derivedDataPath", _temp.Combine("Build"), "build" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(_temp.Combine("Build", "Build", "Products", "Debug-iphonesimulator", "HelloIOS.app", "HelloIOS")));
    }
}


public sealed class RequiresXcodeFactAttribute : FactAttribute
{
    public RequiresXcodeFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/xcodebuild"))
            Skip = "Requires macOS and Xcode.";
    }
}
