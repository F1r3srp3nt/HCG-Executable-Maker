using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Domain;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// .NET for iOS archives and IPAs at exact paths (HCG Mail iOS, Maker run 46074732453 in HCG-Mail): the SDK's
/// <c>ArchiveDir</c> is the .xcarchive itself, so HCG names it and then checks that exact folder and IPA.
/// </summary>
public sealed class DotNetIosArchiveTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private string Archive(string path, int apps = 1, bool infoPlist = true, bool appInfoPlist = true)
    {
        Directory.CreateDirectory(path);
        if (infoPlist) File.WriteAllText(Path.Combine(path, "Info.plist"), "<plist version=\"1.0\"><dict/></plist>");
        for (var i = 0; i < apps; i++)
        {
            var app = Path.Combine(path, "Products", "Applications", i == 0 ? "HCGMail.iOS.app" : $"Other{i}.app");
            Directory.CreateDirectory(app);
            if (appInfoPlist) File.WriteAllText(Path.Combine(app, "Info.plist"), "<plist version=\"1.0\"><dict/></plist>");
        }
        return path;
    }

    [Fact]
    public void A_complete_archive_yields_its_one_app()
    {
        var archive = Archive(_t.Combine("archives", "HCGMail.iOS.xcarchive"));
        var (app, problem) = DotNetIosPlanner.InspectArchive(archive);
        Assert.Null(problem);
        Assert.Equal(Path.Combine(archive, "Products", "Applications", "HCGMail.iOS.app"), app);
    }

    [Fact]
    public void The_old_layout_archive_written_into_the_folder_itself_is_named_as_the_cause()
    {
        // What Maker's former -p:ArchiveDir=<work>/archives produced: Info.plist and Products/ directly in archives/.
        Archive(_t.Combine("archives"));
        var (app, problem) = DotNetIosPlanner.InspectArchive(_t.Combine("archives", "HCGMail.iOS.xcarchive"));
        Assert.Null(app);
        Assert.Contains("did not create the archive", problem);
        Assert.Contains("uses ArchiveDir as the .xcarchive", problem);
        Assert.Contains("Xcode's Archives folder", DotNetIosPlanner.InspectArchive(_t.Combine("elsewhere", "X.xcarchive")).Problem);
    }

    [Theory]
    [InlineData("missing", "did not create the archive")]
    [InlineData("no-info-plist", "has no Info.plist, so it is not a complete Xcode archive")]
    [InlineData("no-app", "has no app in Products/Applications")]
    [InlineData("two-apps", "has 2 apps in Products/Applications")]
    [InlineData("app-without-plist", "HCGMail.iOS.app has no Info.plist")]
    public void An_incomplete_archive_is_refused_with_the_exact_reason(string scenario, string expected)
    {
        var path = _t.Combine("archives", "HCGMail.iOS.xcarchive");
        _ = scenario switch
        {
            "no-info-plist" => Archive(path, infoPlist: false),
            "no-app" => Archive(path, apps: 0),
            "two-apps" => Archive(path, apps: 2),
            "app-without-plist" => Archive(path, appInfoPlist: false),
            _ => path,
        };
        var (app, problem) = DotNetIosPlanner.InspectArchive(path);
        Assert.Null(app);
        Assert.Contains(expected, problem);
    }

    private string XcodeArchive(string folder, string name, string bundleId, DateTime written)
    {
        var path = Path.Combine(folder, "2026-11-28", name + ".xcarchive");
        Directory.CreateDirectory(Path.Combine(path, "Products", "Applications", "HelloDotNetIos.app"));
        var info = Path.Combine(path, "Info.plist");
        File.WriteAllText(info, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>ApplicationProperties</key><dict><key>CFBundleIdentifier</key><string>{bundleId}</string></dict></dict></plist>");
        File.SetLastWriteTimeUtc(info, written);
        return path;
    }

    [Fact]
    public void Workloads_before_27_write_to_Xcodes_Archives_folder_and_only_this_builds_archive_is_taken_from_there()
    {
        // Xamarin.Shared.targets 26.2 does not pass ArchiveDir to its Archive task (27.2 does): the archive goes to
        // ~/Library/Developer/Xcode/Archives/<date>/<Project> <date>.xcarchive (CI run 46124360927, workload 26.2.10233).
        var folder = DotNetIosPlanner.XcodeArchivesFolder(_t.Combine("home"));
        Assert.Equal(_t.Combine("home", "Library", "Developer", "Xcode", "Archives"), folder);
        var started = new DateTime(2026, 11, 28, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal((null, null), DotNetIosPlanner.FindArchiveInXcodeFolder(folder, "com.example.hello", started)); // no folder at all

        XcodeArchive(folder, "HelloDotNetIos 11-27-26 9.00 AM", "com.example.hello", started.AddDays(-1));        // older: another build's
        XcodeArchive(folder, "Other 11-28-26 8.01 AM", "com.example.other", started.AddMinutes(1));               // another app
        var mine = XcodeArchive(folder, "HelloDotNetIos 11-28-26 8.01 AM", "com.example.hello", started.AddMinutes(1));
        Assert.Equal((mine, (string?)null), DotNetIosPlanner.FindArchiveInXcodeFolder(folder, "com.example.hello", started));

        XcodeArchive(folder, "HelloDotNetIos 11-28-26 8.02 AM", "com.example.hello", started.AddMinutes(2));
        var (none, ambiguous) = DotNetIosPlanner.FindArchiveInXcodeFolder(folder, "com.example.hello", started);
        Assert.Null(none);
        Assert.Contains("2 new archives for com.example.hello", ambiguous);
        Assert.Contains("does not guess", ambiguous);
    }

    private string Ipa(params string[] entries)
    {
        var path = _t.Combine("ipa-" + Guid.NewGuid().ToString("N")[..8], "HCGMail.iOS.ipa");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in entries) { using var s = zip.CreateEntry(e).Open(); s.Write(Encoding.UTF8.GetBytes("x")); }
        return path;
    }

    [Fact]
    public void The_ipa_must_hold_the_archived_app_with_its_executable()
    {
        Assert.Null(DotNetIosPlanner.InspectIpa(Ipa("Payload/HCGMail.iOS.app/Info.plist", "Payload/HCGMail.iOS.app/HCGMail.iOS"), "HCGMail.iOS.app", "HCGMail.iOS"));
        Assert.Contains("no Payload/HCGMail.iOS.app/HCGMail.iOS", DotNetIosPlanner.InspectIpa(Ipa("Payload/HCGMail.iOS.app/Info.plist"), "HCGMail.iOS.app", "HCGMail.iOS"));
        Assert.Contains("no Payload/HCGMail.iOS.app/Info.plist", DotNetIosPlanner.InspectIpa(Ipa("Payload/Other.app/Info.plist"), "HCGMail.iOS.app", null));
        Assert.Contains("did not create the IPA", DotNetIosPlanner.InspectIpa(_t.Combine("none.ipa"), "HCGMail.iOS.app", null));
        File.WriteAllText(_t.Combine("bad.ipa"), "not a zip");
        Assert.Contains("is not a valid IPA", DotNetIosPlanner.InspectIpa(_t.Combine("bad.ipa"), "HCGMail.iOS.app", null));
    }

    private MobilePlanInput Input(string? assemblyName, string projectPath = "iOS/src/HCG.Mail.iOS/HCG.Mail.iOS.csproj") => new()
    {
        Options = new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosIpa },
        Analysis = new MobileAnalysis
        {
            Framework = MobileFramework.DotNetIos,
            DotNetIos = new DotNetIosProjectInfo { ProjectPath = projectPath, TargetFramework = "net10.0-ios", AssemblyName = assemblyName },
        },
        SourceDirectory = _t.Combine("src"), WorkDirectory = _t.Combine("work"), Toolchains = null!, Environment = new Dictionary<string, string>(), ToolHomeDirectory = _t.Combine("th"),
    };

    [Theory]
    [InlineData("HCGMail.iOS", "HCGMail.iOS")]
    [InlineData(null, "HCG.Mail.iOS")]
    [InlineData("$(MSBuildProjectName)", "HCG.Mail.iOS")]
    [InlineData("My App;x=1", "My_App_x_1")]
    public void The_archive_and_ipa_are_named_after_the_assembly_with_safe_characters(string? assembly, string expected)
    {
        var input = Input(assembly);
        Assert.Equal(expected, DotNetIosPlanner.ProductName(input));
        Assert.Equal(Path.Combine(_t.Combine("work"), "archives", expected + ".xcarchive"), DotNetIosPlanner.ArchivePath(input));
        Assert.Equal(Path.Combine(_t.Combine("work"), "ipa", expected + ".ipa"), DotNetIosPlanner.IpaPath(input));
    }
}
