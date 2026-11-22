using HCG.ExecutableMaker.BuildEngine.Doctor;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// HCG Mail (F1r3srp3nt/HCG-Mail 13d93a8) is the regression case: its iOS head resolves the shared projects through
/// HcgSharedDir (three conditional assignments and $([MSBuild]::NormalizeDirectory(…))) and imports guarded by
/// HcgSharedRootFound. Maker's static reader resolved none of it, so it reported four "missing" references even when
/// the whole repository was imported.
/// </summary>
public sealed class BuildDoctorTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string FixtureDir(params string[] parts) => Path.Combine([Fixtures.RepoRoot, "tests", "Fixtures", .. parts]);

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>The current HCG Mail build files, as a repository checkout whose path contains spaces.</summary>
    private string MailRepo()
    {
        var dir = _t.Combine("Repos", "HCG Mail");
        if (!Directory.Exists(dir)) { CopyDir(FixtureDir("DotNetIos", "HCG Mail current"), dir); Directory.CreateDirectory(Path.Combine(dir, ".git")); }
        return dir;
    }

    private const string Ios = "iOS App Store Submission";

    private static ProjectInspectionService Service() => new(
        new IProjectInspector[] { new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System) }
            .Concat(HCG.ExecutableMaker.ProjectInspection.Ecosystems.EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    // ───────────── MSBuild evaluation ─────────────

    [Fact]
    public void HcgSharedDir_is_evaluated_like_MSBuild_conditions_then_NormalizeDirectory_then_the_guarded_imports()
    {
        var repo = MailRepo();
        var mobile = Path.Combine(repo, Ios, "src", "HCG.Mail.Mobile", "HCG.Mail.Mobile.csproj");
        var f = DotNetProjectInspector.LoadProject(repo, mobile)!;
        var shared = Path.Combine(repo, "Windows-executable-Application") + Path.DirectorySeparatorChar;
        Assert.Equal(shared, f.Get("HcgSharedDir"));
        Assert.Equal("true", f.Get("HcgSharedRootFound"));
        Assert.Equal(
            ["../../../Windows-executable-Application/src/HCG.Mail.Core/HCG.Mail.Core.csproj", "../../../Windows-executable-Application/src/HCG.Mail.Email/HCG.Mail.Email.csproj",
             "../../../Windows-executable-Application/src/HCG.Mail.Infrastructure/HCG.Mail.Infrastructure.csproj", "../../../Windows-executable-Application/src/HCG.Mail.Presentation/HCG.Mail.Presentation.csproj"],
            f.ProjectReferences);
        Assert.All(f.ProjectReferences, r => Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(mobile)!, r)), r));
        Assert.Equal("$(HcgSharedSource)HCG.Mail.Core/HCG.Mail.Core.csproj", f.RawProjectReferences[0].Raw);

        var head = DotNetProjectInspector.LoadProject(repo, Path.Combine(repo, Ios, "src", "HCG.Mail.iOS", "HCG.Mail.iOS.csproj"))!;
        Assert.Equal("com.hakmicapitalgroup.hcgmail", head.Get("ApplicationId"));   // from the shared Directory.Build.props, behind HcgSharedRootFound
        Assert.Equal("26.3", head.Get("HcgRequiredXcode"));
        Assert.Contains("HCG_GOOGLE_CLIENT_SECRET", head.EnvironmentInputs);         // named, never read
    }

    [Fact]
    public void The_environment_is_never_read_even_when_HCG_SHARED_ROOT_is_set()
    {
        var repo = MailRepo();
        var mobile = Path.Combine(repo, Ios, "src", "HCG.Mail.Mobile", "HCG.Mail.Mobile.csproj");
        Environment.SetEnvironmentVariable("HCG_SHARED_ROOT", "/nonexistent/elsewhere/");
        try { Assert.Equal(Path.Combine(repo, "Windows-executable-Application") + Path.DirectorySeparatorChar, DotNetProjectInspector.LoadProject(repo, mobile)!.Get("HcgSharedDir")); }
        finally { Environment.SetEnvironmentVariable("HCG_SHARED_ROOT", null); }
    }

    [Theory]
    [InlineData("'' == ''", true)]
    [InlineData("'a' != 'A'", false)]
    [InlineData("'true' == 'true' And ('x' == 'y' Or 'b' == 'b')", true)]
    [InlineData("!('a' == 'a')", false)]
    [InlineData("'1' >= '2'", null)]
    [InlineData("HasTrailingSlash('x')", null)]
    public void Conditions_are_decided_or_reported_as_undecided(string condition, bool? expected) =>
        Assert.Equal(expected, MsBuildCondition.Evaluate(condition));

    [Fact]
    public async Task The_full_repository_import_resolves_the_shared_projects_and_reads_the_toolchain_pins()
    {
        var a = await Service().InspectAsync(MailRepo(), null, CancellationToken.None);
        var dn = a.Mobile!.DotNetIos!;
        Assert.Empty(dn.MissingReferences);
        Assert.Empty(dn.OutsideWorkspace);
        Assert.Contains("Windows-executable-Application/src/HCG.Mail.Core/HCG.Mail.Core.csproj", dn.ProjectReferences);
        Assert.Equal("0.1.0", dn.DisplayVersion);
        Assert.Equal("26.2.10233/10.0.100", dn.WorkloadPin);
        Assert.Equal($"{Ios}/toolchain.props", dn.WorkloadPinFile);
        Assert.False(dn.WorkloadPinIsRollbackFile);
        Assert.Equal("26.3", dn.RequiredXcode);
    }

    // ───────────── Build Doctor ─────────────

    private static ProjectRecord Project(string source, string workspace, ProjectSourceKind kind, string location)
    {
        Directory.CreateDirectory(workspace);
        if (!Directory.Exists(Path.Combine(workspace, "source"))) CopyDir(source, Path.Combine(workspace, "source"));
        return new ProjectRecord { Id = Guid.NewGuid(), Name = "HCG Mail", SourceKind = kind, SourceLocation = location, WorkspacePath = workspace, ImportedAt = DateTimeOffset.UtcNow };
    }

    private static ToolchainReport Report(bool mac, string? workload = "26.2.10233/10.0.100", string xcode = "26.3")
    {
        var rows = new List<ToolchainInfo> { new() { Id = ToolchainIds.DotNetSdk, DisplayName = ".NET SDK", Category = "Build", Status = ToolchainStatus.Installed, Version = "10.0.114", ExecutablePath = "/usr/local/share/dotnet/dotnet" } };
        if (workload is not null) rows.Add(new() { Id = ToolchainIds.DotNetIosWorkload, DisplayName = ".NET iOS workload", Category = "Mobile", Status = ToolchainStatus.Installed, Version = workload });
        var host = mac ? new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx") : new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux");
        return new ToolchainReport(host, rows, null, null, DateTimeOffset.UtcNow)
        {
            Xcode = mac ? new XcodeStatus { State = XcodeState.Ready, VerifiedVersion = xcode, Diagnosis = "Ready", Sdks = ["iphoneos 26.2", "iphonesimulator 26.2"] } : null,
        };
    }

    private static async Task<DoctorReport> Doctor(ProjectRecord p, ToolchainReport r, MobileOutput output = MobileOutput.IosSimulatorApp)
    {
        var a = await Service().InspectAsync(p.SourceDirectory, null, CancellationToken.None);
        return BuildDoctor.Diagnose(new DoctorInput(p, a.Mobile!, output, null, r));
    }

    [Fact]
    public async Task Imported_whole_on_a_Mac_HCG_Mail_has_no_reference_or_property_errors()
    {
        var p = Project(MailRepo(), _t.Combine("ws-full"), ProjectSourceKind.Directory, MailRepo());
        var d = await Doctor(p, Report(mac: true));
        Assert.DoesNotContain(d.Findings, f => f.Category is DoctorCategory.ProjectReferences or DoctorCategory.MsBuildProperties or DoctorCategory.ImportScope);
        Assert.True(d.ReadyToBuild, string.Join("\n", d.Findings.Where(f => f.Severity == DoctorSeverity.Error).Select(f => f.Problem)));
        Assert.Contains(d.Findings, f => f.Id == "env:HCG_GOOGLE_CLIENT_SECRET" && f.Severity == DoctorSeverity.Info);
        Assert.Contains(d.Facts, x => x.Contains("reference $(HcgSharedSource)HCG.Mail.Core/HCG.Mail.Core.csproj → Windows-executable-Application/src/HCG.Mail.Core/HCG.Mail.Core.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Imported_as_one_folder_the_shared_projects_are_outside_and_the_repair_imports_the_repository_root()
    {
        var original = Path.Combine(MailRepo(), Ios);
        var p = Project(original, _t.Combine("ws-partial"), ProjectSourceKind.Directory, original);
        var d = await Doctor(p, Report(mac: true));
        var scope = d.Findings.Where(f => f.Category == DoctorCategory.ImportScope).ToList();
        Assert.Equal(4, scope.Count(f => f.Severity == DoctorSeverity.Error));                       // Core, Email, Infrastructure, Presentation
        Assert.Contains(scope, f => f.Problem.StartsWith("hcg-logo.png (AvaloniaResource", StringComparison.Ordinal) && f.Severity == DoctorSeverity.Warning);
        var repair = scope[0].Repair!;
        Assert.Equal((RepairKind.ImportRepository, RepairMode.RequiresApproval), (repair.Kind, repair.Mode));
        Assert.Equal(MailRepo(), repair.Target);                                                       // the .git root, not just the common parent
        Assert.Contains("resolves to ../Windows-executable-Application/src/HCG.Mail.Core/HCG.Mail.Core.csproj", scope.First(f => f.Problem.StartsWith("HCG.Mail.Core", StringComparison.Ordinal)).RootCause);
        Assert.Contains("5 projects or files outside the imported folder", string.Join("; ", BuildDoctor.Summary(d)), StringComparison.Ordinal);
        Assert.Equal("Configuration required", BuildDoctor.Readiness(d));
    }

    [Fact]
    public async Task From_a_ZIP_of_one_folder_the_repair_is_manual_and_nothing_is_fabricated()
    {
        var p = Project(Path.Combine(MailRepo(), Ios), _t.Combine("ws-zip"), ProjectSourceKind.ZipArchive, _t.Combine("ios.zip"));
        var d = await Doctor(p, Report(mac: true));
        var f = d.Findings.First(x => x.Category == DoctorCategory.ImportScope);
        Assert.Equal((RepairKind.Instructions, RepairMode.Manual), (f.Repair!.Kind, f.Repair.Mode));
        Assert.False(Directory.Exists(Path.Combine(p.WorkspacePath, "Windows-executable-Application")));
    }

    [Fact]
    public async Task On_Linux_the_mac_requirement_is_one_finding_not_one_per_tool()
    {
        var p = Project(MailRepo(), _t.Combine("ws-linux"), ProjectSourceKind.Directory, MailRepo());
        var d = await Doctor(p, Report(mac: false));
        Assert.Single(d.Findings, f => f.Category == DoctorCategory.Host);
        Assert.Equal("Needs a Mac with Xcode", BuildDoctor.Readiness(d));
    }

    [Fact]
    public async Task A_missing_workload_and_a_different_Xcode_are_explained_with_the_pinned_command()
    {
        var p = Project(MailRepo(), _t.Combine("ws-tools"), ProjectSourceKind.Directory, MailRepo());
        var d = await Doctor(p, Report(mac: true, workload: null, xcode: "26.2"), MobileOutput.IosArchive);
        var w = d.Findings.Single(f => f.Category == DoctorCategory.Workload);
        Assert.Equal((RepairKind.InstallWorkload, RepairMode.RequiresApproval), (w.Repair!.Kind, w.Repair.Mode));
        Assert.Contains("26.2.10233/10.0.100", w.Repair.Command);
        var x = d.Findings.Single(f => f.Id == "xcode:pin");
        Assert.Contains("Installing the workload alone does not change that", x.RootCause);

        var args = DoctorRepairs.WorkloadArguments(p, (await Service().InspectAsync(p.SourceDirectory, null, CancellationToken.None)).Mobile!.DotNetIos!);
        Assert.Equal(["workload", "install", "ios", "--from-rollback-file"], args.Take(4));
        Assert.Equal("{\"microsoft.net.sdk.ios\":\"26.2.10233/10.0.100\"}", File.ReadAllText(args[4]).Trim());
        Assert.StartsWith(Path.Combine(p.WorkspacePath, "doctor"), args[4]);                          // in the workspace, not the source
    }

    [Fact]
    public async Task A_broken_relative_reference_is_repaired_only_after_approval_with_backup_verification_and_rollback()
    {
        var src = _t.Combine("broken");
        _t.Write("broken/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0-ios</TargetFramework><OutputType>Exe</OutputType><ApplicationId>com.example.app</ApplicationId></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Libs/Core.csproj" /></ItemGroup>
            </Project>
            """);
        _t.Write("broken/Shared/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var p = Project(src, _t.Combine("ws-broken"), ProjectSourceKind.Directory, src);
        var d = await Doctor(p, Report(mac: true));
        var f = d.Findings.Single(x => x.Category == DoctorCategory.ProjectReferences);
        Assert.Contains("the only Core.csproj in the workspace is Shared/Core/Core.csproj", f.RootCause);
        var repair = f.Repair!;
        Assert.Equal((RepairKind.EditFiles, RepairMode.RequiresApproval), (repair.Kind, repair.Mode));
        var preview = DoctorRepairs.Preview(repair);
        Assert.Contains("- " + "  <ItemGroup><ProjectReference Include=\"../Libs/Core.csproj\" /></ItemGroup>", preview);
        Assert.Contains("+ " + "  <ItemGroup><ProjectReference Include=\"../Shared/Core/Core.csproj\" /></ItemGroup>", preview);

        var appFile = Path.Combine(p.SourceDirectory, "App", "App.csproj");
        var before = File.ReadAllText(appFile);
        var record = await new DoctorRepairs(new ProcessRunner(), TimeProvider.System).ApplyFileChangesAsync(p, repair, CancellationToken.None);
        Assert.Contains("../Shared/Core/Core.csproj", File.ReadAllText(appFile));
        Assert.Equal(before, File.ReadAllText(Path.Combine(record.BackupFolder!, "files", "App", "App.csproj")));
        Assert.True(File.Exists(Path.Combine(record.BackupFolder!, "repair.json")));
        Assert.DoesNotContain((await Doctor(p, Report(mac: true))).Findings, x => x.Category == DoctorCategory.ProjectReferences);   // verified

        await DoctorRepairs.RollbackAsync(p, record, CancellationToken.None);
        Assert.Equal(before, File.ReadAllText(appFile));
    }

    [Fact]
    public async Task A_repair_is_refused_when_the_file_changed_after_review_and_nothing_outside_the_source_is_touched()
    {
        var src = _t.Combine("b2");
        _t.Write("b2/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-ios</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include=\"../X/Lib.csproj\" /></ItemGroup></Project>");
        _t.Write("b2/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var p = Project(src, _t.Combine("ws-b2"), ProjectSourceKind.Directory, src);
        var repair = (await Doctor(p, Report(mac: true))).Findings.Single(x => x.Category == DoctorCategory.ProjectReferences).Repair!;
        File.AppendAllText(Path.Combine(p.SourceDirectory, "App", "App.csproj"), "\n<!-- edited -->");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new DoctorRepairs(new ProcessRunner(), TimeProvider.System).ApplyFileChangesAsync(p, repair, CancellationToken.None));
        Assert.Contains("changed since the repair was proposed", ex.Message);

        var escape = repair with { Changes = [new DoctorFileChange("../../outside.txt", "", "x")] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DoctorRepairs(new ProcessRunner(), TimeProvider.System).ApplyFileChangesAsync(p, escape, CancellationToken.None));
    }

    [Fact]
    public void MSBuild_evaluation_output_is_parsed_into_properties_and_existing_references()
    {
        var src = _t.Combine("eval");
        _t.Write("eval/Lib/Lib.csproj", "<Project />");
        string Esc(string x) => x.Replace("\\", "\\\\");
        var lib = Esc(Path.Combine(src, "Lib", "Lib.csproj"));
        var gone = Esc(Path.Combine(src, "Gone", "Gone.csproj"));
        var json = "{\"Properties\":{\"TargetFramework\":\"net10.0-ios\",\"ApplicationId\":\"com.example.app\",\"RuntimeIdentifier\":\"\"}," +
                   "\"Items\":{\"ProjectReference\":[{\"Identity\":\"../Lib/Lib.csproj\",\"FullPath\":\"" + lib + "\"}," +
                   "{\"Identity\":\"../Gone/Gone.csproj\",\"FullPath\":\"" + gone + "\"}]}}";
        var e = MsBuildEvaluation.Parse("App/App.csproj", src, true, json);
        Assert.Equal("com.example.app", e.Properties["ApplicationId"]);
        Assert.False(e.Properties.ContainsKey("RuntimeIdentifier"));
        Assert.Equal([("Lib/Lib.csproj", true), ("Gone/Gone.csproj", false)], e.References);
    }
}
