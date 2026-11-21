using System.Xml.Linq;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// "macOS Xcode Project/": the Xcode project that builds the real .NET app (External Build System target running
/// Scripts/build-app.sh → tools/self-package) and runs it (the "Run HCG Executable Maker" launcher tool). Static checks
/// that run on every OS; the macOS CI job builds and launches it with xcodebuild.
/// </summary>
public class XcodeProjectFileTests
{
    private static string Dir => Path.Combine(Fixtures.RepoRoot, "macOS Xcode Project");
    private const string Project = "HCGExecutableMaker.xcodeproj";

    [Fact]
    public void The_project_file_parses_and_has_the_launcher_target_and_the_external_build_target()
    {
        var index = SourceIndex.Build(Dir);
        var ios = XcodeProjectAnalyzer.Analyze(index, Project, out var diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.Severity == Domain.DiagnosticSeverity.Error);
        var launcher = Assert.Single(ios.Targets);
        Assert.Equal("Run HCG Executable Maker", launcher.Name);
        Assert.Equal("com.apple.product-type.tool", launcher.ProductType);
        Assert.Equal(["Debug", "Release"], ios.Configurations);

        var root = (Dictionary<string, object>)OpenStepPlist.Parse(File.ReadAllText(Path.Combine(Dir, Project, "project.pbxproj")));
        var objects = ((Dictionary<string, object>)root["objects"]).Values.OfType<Dictionary<string, object>>().ToList();
        Assert.All(objects.Where(o => o.GetValueOrDefault("isa") as string == "XCBuildConfiguration" && ((Dictionary<string, object>)o["buildSettings"]).ContainsKey("SDKROOT")),
            c => Assert.Equal("macosx", ((Dictionary<string, object>)c["buildSettings"])["SDKROOT"]));
        var legacy = Assert.Single(objects, o => o.GetValueOrDefault("isa") as string == "PBXLegacyTarget");
        Assert.Equal("HCG Executable Maker (.NET)", legacy["name"]);
        Assert.Equal("/bin/bash", legacy["buildToolPath"]);
        Assert.Contains("Scripts/build-app.sh", (string)legacy["buildArgumentsString"], StringComparison.Ordinal);
        Assert.Equal("1", legacy["passBuildSettingsInEnvironment"]);
        // Every file the project references exists.
        foreach (var file in objects.Where(o => o.GetValueOrDefault("isa") as string == "PBXFileReference" && o.GetValueOrDefault("sourceTree") as string == "<group>"))
            Assert.True(Directory.EnumerateFiles(Dir, (string)file["path"], SearchOption.AllDirectories).Any(), $"{file["path"]} is referenced but missing");
    }

    [Fact]
    public void It_is_a_macOS_project_so_the_repository_stays_a_desktop_app_not_a_mobile_project()
    {
        var index = SourceIndex.Build(Fixtures.RepoRoot);
        Assert.True(XcodeProjectAnalyzer.IsMacOnlyProject(index, "macOS Xcode Project/" + Project));
        Assert.DoesNotContain("macOS Xcode Project/" + Project, XcodeProjectAnalyzer.FindMobileProjects(index));
    }

    [Theory]
    [InlineData("HCG Executable Maker.xcscheme", "Debug")]
    [InlineData("HCG Executable Maker (Release).xcscheme", "Release")]
    public void The_shared_schemes_build_both_targets_and_run_the_launcher_without_a_native_debugger(string file, string configuration)
    {
        var scheme = XDocument.Load(Path.Combine(Dir, Project, "xcshareddata", "xcschemes", file));
        var pbx = File.ReadAllText(Path.Combine(Dir, Project, "project.pbxproj"));
        var built = scheme.Descendants("BuildActionEntry").Select(e => e.Element("BuildableReference")!.Attribute("BlueprintName")!.Value).ToList();
        Assert.Equal(["HCG Executable Maker (.NET)", "Run HCG Executable Maker"], built);
        foreach (var r in scheme.Descendants("BuildableReference"))
            Assert.Contains(r.Attribute("BlueprintIdentifier")!.Value + " /* " + r.Attribute("BlueprintName")!.Value + " */ = {", pbx, StringComparison.Ordinal);
        var launch = scheme.Descendants("LaunchAction").Single();
        Assert.Equal(configuration, launch.Attribute("buildConfiguration")!.Value);
        // LLDB would stop on the signals the .NET runtime uses internally; managed debugging is documented in the README.
        Assert.Equal("", launch.Attribute("selectedDebuggerIdentifier")!.Value);
        Assert.Equal("Run HCG Executable Maker", launch.Descendants("BuildableReference").Single().Attribute("BlueprintName")!.Value);
    }

    [Fact]
    public void The_build_script_takes_the_version_from_Directory_Build_props_and_never_publishes()
    {
        var script = File.ReadAllText(Path.Combine(Dir, "Scripts", "build-app.sh"));
        Assert.Contains("Directory.Build.props", script, StringComparison.Ordinal);
        Assert.Contains("tools/self-package/self-package.cs", script, StringComparison.Ordinal);
        Assert.Contains("com.hakmicapital.executablemaker", script, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "git push", "git tag", "gh release", "notarytool", "altool", "ship-to-store" })
            Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        Assert.DoesNotContain("0.4.0", script, StringComparison.Ordinal); // no second copy of the version
    }

    [Fact]
    public void Every_dotnet_command_runs_without_Xcodes_build_settings_in_the_environment()
    {
        // Xcode passes its build settings as environment variables and MSBuild reads them as properties: TARGETNAME
        // became TargetName and broke the build on the first macOS CI run. Every dotnet call goes through clean_env.
        var script = File.ReadAllLines(Path.Combine(Dir, "Scripts", "build-app.sh"));
        Assert.Contains(script, l => l.Contains("clean_env=(env -i ", StringComparison.Ordinal));
        var calls = script.Where(l => l.Contains("\"$DOTNET\"", StringComparison.Ordinal) || l.Contains("/MacOS/$EXECUTABLE\" --version", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, l => Assert.Contains("\"${clean_env[@]}\"", l, StringComparison.Ordinal));
    }
}
