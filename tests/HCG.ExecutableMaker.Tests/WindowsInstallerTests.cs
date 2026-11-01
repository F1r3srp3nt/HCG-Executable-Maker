using System.Diagnostics;
using System.Text;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HCG.ExecutableMaker.Tests;

public class InnoSetupScriptTests
{
    private static BuildSettings Settings(string name = "Hello Avalonia", string version = "1.4.2", string publisher = "Hakmi Capital Group",
        TargetArchitecture arch = TargetArchitecture.X64, WindowsInstallerOptions? options = null) => new()
    {
        ProjectRelativePath = "App/App.csproj", Platform = TargetPlatform.Windows, Architecture = arch, OutputDirectory = "C:/out",
        Identity = new AppIdentity { ApplicationName = name, Version = version, Publisher = publisher, ApplicationId = "com.hakmicapital.hello" },
        PackagingFormats = [PackagingFormat.WindowsInstallerExe],
        WindowsInstaller = options ?? new WindowsInstallerOptions(),
    };

    private static string Script(BuildSettings s, string? icon = null, Version? iscc = null) => InnoSetupScript.Generate(new InnoSetupScript.Input
    {
        Settings = s, SourceDirectory = @"C:\hcg\builds\b1\publish", ExecutableFileName = "HelloAvalonia.exe",
        OutputDirectory = @"C:\out\Hello Avalonia-1.4.2-win-x64", OutputBaseFileName = "Hello Avalonia-1.4.2-win-x64-Setup",
        SetupIconPath = icon, CompilerVersion = iscc,
    });

    private static Dictionary<string, string> SetupSection(string script) =>
        script.Split('\n').Select(l => l.TrimEnd('\r')).SkipWhile(l => l != "[Setup]").Skip(1).TakeWhile(l => l.Length > 0)
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    [Fact]
    public void Generates_identity_directories_shortcuts_and_uninstall_settings()
    {
        var script = Script(Settings(), icon: @"C:\hcg\builds\b1\icons\AppIcon.ico");
        var setup = SetupSection(script);

        Assert.Equal("{{" + InnoSetupScript.AppId("com.hakmicapital.hello").ToString("D").ToUpperInvariant() + "}", setup["AppId"]);
        Assert.Equal("Hello Avalonia", setup["AppName"]);
        Assert.Equal("1.4.2", setup["AppVersion"]);
        Assert.Equal("Hakmi Capital Group", setup["AppPublisher"]);
        Assert.Equal("1.4.2.0", setup["VersionInfoVersion"]);
        Assert.Equal(@"{autopf}\Hello Avalonia", setup["DefaultDirName"]);
        Assert.Equal(@"{app}\HelloAvalonia.exe", setup["UninstallDisplayIcon"]);
        Assert.Equal(@"C:\hcg\builds\b1\icons\AppIcon.ico", setup["SetupIconFile"]);
        Assert.Equal("lowest", setup["PrivilegesRequired"]); // per-user by default: no administrator rights
        Assert.Equal("x64compatible", setup["ArchitecturesInstallIn64BitMode"]);
        Assert.Contains(@"Source: ""C:\hcg\builds\b1\publish\*""; DestDir: ""{app}""; Flags: ignoreversion recursesubdirs createallsubdirs", script);
        Assert.Contains(@"Name: ""{autoprograms}\Hello Avalonia""; Filename: ""{app}\HelloAvalonia.exe""", script);
        Assert.Contains(@"Name: ""{autodesktop}\Hello Avalonia""; Filename: ""{app}\HelloAvalonia.exe""; WorkingDir: ""{app}""; Tasks: desktopicon", script);
        Assert.Contains(@"Name: ""desktopicon""; Description: ""Create a &desktop shortcut""; GroupDescription: ""Additional shortcuts:""; Flags: unchecked", script);
        Assert.Contains(@"Flags: nowait postinstall skipifsilent", script);
        Assert.Contains(@"Type: dirifempty; Name: ""{app}""", script);
        Assert.DoesNotContain("[Code]", script);
    }

    [Fact]
    public void Options_change_scope_folder_and_shortcut_tasks()
    {
        var script = Script(Settings(options: new WindowsInstallerOptions
        {
            Scope = InstallScope.AllUsers, InstallFolderName = "HCG Hello", OfferDesktopShortcut = false, OfferLaunchAfterInstall = false,
        }));
        var setup = SetupSection(script);
        Assert.Equal("admin", setup["PrivilegesRequired"]);
        Assert.Equal(@"{autopf}\HCG Hello", setup["DefaultDirName"]);
        Assert.DoesNotContain("[Tasks]", script);
        Assert.DoesNotContain("{autodesktop}", script);
        Assert.DoesNotContain("[Run]", script);

        var checkedDesktop = Script(Settings(options: new WindowsInstallerOptions { DesktopShortcutCheckedByDefault = true }));
        Assert.DoesNotContain("Flags: unchecked", checkedDesktop);
    }

    [Fact]
    public void Untrusted_text_cannot_inject_directives_constants_or_parameters()
    {
        var hostile = "Evil {app} \"; Flags: x\r\nPrivilegesRequired=admin\n[Code]";
        var script = Script(Settings(name: hostile, publisher: "Pub{lisher}\nOutputDir=C:\\Windows", version: "2.0\nAppId=x"));
        var lines = script.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        // No injected line: every directive/section line is one HCG generated.
        Assert.Single(lines, l => l.StartsWith("PrivilegesRequired=", StringComparison.Ordinal));
        Assert.Single(lines, l => l.StartsWith("OutputDir=", StringComparison.Ordinal));
        Assert.Single(lines, l => l.StartsWith("AppId=", StringComparison.Ordinal));
        Assert.DoesNotContain("[Code]", lines);
        var setup = SetupSection(script);
        Assert.Equal("Evil {{app} \"; Flags: xPrivilegesRequired=admin[Code]", setup["AppName"]);
        Assert.Equal("Pub{{lisher}OutputDir=C:\\Windows", setup["AppPublisher"]);
        // Inside quoted parameters, quotes are doubled and braces escaped; file-name characters are sanitized.
        Assert.Contains("Description: \"Launch Evil {{app} \"\"; Flags: xPrivilegesRequired=admin[Code]\"", script);
        Assert.Contains(@"Name: ""{autoprograms}\Evil {{app}", script);
    }

    [Fact]
    public void App_id_is_stable_per_application_and_distinct_between_applications()
    {
        Assert.Equal(InnoSetupScript.AppId("com.hakmicapital.hello"), InnoSetupScript.AppId(" COM.hakmicapital.HELLO "));
        Assert.NotEqual(InnoSetupScript.AppId("com.hakmicapital.hello"), InnoSetupScript.AppId("com.hakmicapital.other"));
        Assert.Equal('5', InnoSetupScript.AppId("x").ToString("D")[14]);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3.0")]
    [InlineData("1.2.3.4.5", "1.2.3.4")]
    [InlineData("2.0-beta.1", "2.0.0.0")]
    [InlineData("v1", null)]
    public void Numeric_file_version_is_derived_when_possible(string version, string? expected) =>
        Assert.Equal(expected, InnoSetupScript.NumericVersion(version));

    [Fact]
    public void Arm64_and_older_compilers_use_matching_architecture_identifiers()
    {
        Assert.Equal("arm64", SetupSection(Script(Settings(arch: TargetArchitecture.Arm64)))["ArchitecturesAllowed"]);
        Assert.Equal("x64", SetupSection(Script(Settings(), iscc: new Version(6, 2, 2)))["ArchitecturesAllowed"]);
    }

    [Fact]
    public void Rejects_non_windows_targets_and_invalid_executable_names()
    {
        Assert.Throws<InvalidOperationException>(() => Script(Settings() with { Platform = TargetPlatform.Linux }));
        Assert.Throws<InvalidOperationException>(() => InnoSetupScript.Generate(new InnoSetupScript.Input
        {
            Settings = Settings(), SourceDirectory = "C:/p", ExecutableFileName = "..\\evil.exe", OutputDirectory = "C:/o", OutputBaseFileName = "x",
        }));
    }
}

public class WindowsInstallerPackagingTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static ToolchainReport Report(TargetPlatform host, bool iscc) => new(
        new HostEnvironment(host, TargetArchitecture.X64, "test", ".NET", "m"),
        [new ToolchainInfo { Id = ToolchainIds.InnoSetup, DisplayName = "Inno Setup", Category = "Windows", Status = iscc ? ToolchainStatus.Installed : ToolchainStatus.Missing, ExecutablePath = iscc ? @"C:\Inno\ISCC.exe" : null, Version = "6.4.3" }],
        null, null, DateTimeOffset.UtcNow);

    private static PackagingOption Option(TargetPlatform host, bool iscc) =>
        PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.Windows, Report(host, iscc).Host, Report(host, iscc), true))
            .Single(o => o.Format == PackagingFormat.WindowsInstallerExe);

    [Fact]
    public void Installer_is_available_only_on_windows_with_inno_setup()
    {
        Assert.Equal(PackagingAvailability.Available, Option(TargetPlatform.Windows, iscc: true).Availability);
        var missing = Option(TargetPlatform.Windows, iscc: false);
        Assert.Equal(PackagingAvailability.MissingTool, missing.Availability);
        Assert.Contains("Inno Setup 6", missing.Reason);
        foreach (var host in new[] { TargetPlatform.MacOS, TargetPlatform.Linux })
        {
            var other = Option(host, iscc: true);
            Assert.Equal(PackagingAvailability.RequiresDifferentHost, other.Availability);
            Assert.Contains("runs only on Windows", other.Reason);
        }
        // Portable formats are unaffected; MSI stays a separate, not-yet-implemented milestone.
        var all = PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.Windows, Report(TargetPlatform.Windows, true).Host, Report(TargetPlatform.Windows, true), true));
        Assert.Equal(PackagingAvailability.Available, all.Single(o => o.Format == PackagingFormat.PortableFolder).Availability);
        Assert.Equal(PackagingAvailability.Available, all.Single(o => o.Format == PackagingFormat.PortableZip).Availability);
        Assert.Equal(PackagingAvailability.NotImplemented, all.Single(o => o.Format == PackagingFormat.WindowsMsi).Availability);
    }

    private PackagingRequest Request(TargetPlatform host, bool iscc)
    {
        var publish = _t.Combine("publish");
        Directory.CreateDirectory(publish);
        File.WriteAllBytes(Path.Combine(publish, "App.exe"), [0x4D, 0x5A]);
        return new PackagingRequest
        {
            Settings = new BuildSettings
            {
                ProjectRelativePath = "a", Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64, OutputDirectory = _t.Combine("out"),
                Identity = new AppIdentity { ApplicationName = "App", Version = "1.0.0", ApplicationId = "com.example.app" },
            },
            PublishDirectory = publish, ExecutableFileName = "App.exe", OutputRoot = _t.Combine("out"), ArtifactBaseName = "App-1.0.0-win-x64",
            Host = Report(host, iscc).Host, Toolchains = Report(host, iscc),
        };
    }

    private sealed class NullLog : IPackagingLog { public void Info(string m) { } public void Warning(string m) { } }

    [Fact]
    public async Task Packager_refuses_non_windows_hosts_and_missing_compiler_without_running_anything()
    {
        var runner = new RecordingRunner();
        var packager = new InnoSetupPackager(runner);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => packager.PackageAsync(Request(TargetPlatform.MacOS, true), new NullLog(), CancellationToken.None));
        Assert.Contains("runs only on Windows", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => packager.PackageAsync(Request(TargetPlatform.Windows, false), new NullLog(), CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Compiler_errors_and_missing_output_are_failures()
    {
        Directory.CreateDirectory(_t.Combine("out"));
        var failing = new RecordingRunner(exitCode: 2);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new InnoSetupPackager(failing).PackageAsync(Request(TargetPlatform.Windows, true), new NullLog(), CancellationToken.None));
        Assert.Contains("script error", ex.Message);
        var spec = Assert.Single(failing.Calls);
        Assert.Equal(@"C:\Inno\ISCC.exe", spec.Executable);
        Assert.Equal("/Qp", spec.Arguments[0]);
        Assert.EndsWith("App-1.0.0-win-x64-Setup.iss", spec.Arguments[1]);
        Assert.NotNull(spec.Environment);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(spec.Arguments[1]).Take(3)); // UTF-8 BOM: Inno reads Unicode scripts

        var silent = new RecordingRunner(exitCode: 0);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new InnoSetupPackager(silent).PackageAsync(Request(TargetPlatform.Windows, true), new NullLog(), CancellationToken.None));
        Assert.Contains("was not produced", ex.Message);
    }

    [Fact]
    public void Verification_checks_pe_payload_marker_and_signature()
    {
        var request = Request(TargetPlatform.Windows, true);
        var exe = _t.Combine("Setup.exe");
        var pe = new byte[300_000];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        "PE\0\0"u8.CopyTo(pe.AsSpan(0x80));
        BitConverter.GetBytes((ushort)0x14C).CopyTo(pe, 0x84);
        BitConverter.GetBytes((ushort)0x10B).CopyTo(pe, 0x80 + 24); // PE32 optional header
        "Inno Setup Setup Data (6.4.3)"u8.CopyTo(pe.AsSpan(250_000));
        File.WriteAllBytes(exe, pe);
        var checks = InnoSetupPackager.Verify(exe, request);
        Assert.Contains(checks, c => c.Name == "Executable format" && c.Status == VerificationStatus.Passed);
        Assert.Contains(checks, c => c.Name == "Payload" && c.Status == VerificationStatus.Passed);
        Assert.Contains(checks, c => c.Name == "Inno Setup data" && c.Status == VerificationStatus.Passed);
        Assert.Contains(checks, c => c.Name == "Code signature" && c.Detail.StartsWith("Not code-signed"));
        Assert.Contains(checks, c => c.Name == "Install test" && c.Status == VerificationStatus.NotVerifiable);

        File.WriteAllBytes(exe, "not an exe"u8.ToArray());
        checks = InnoSetupPackager.Verify(exe, request);
        Assert.Contains(checks, c => c.Name == "Executable format" && c.Status == VerificationStatus.Failed);
        Assert.Contains(checks, c => c.Name == "Payload" && c.Status == VerificationStatus.Failed);
    }

    [Fact]
    public void Marker_search_spans_buffer_boundaries()
    {
        var file = _t.Combine("big.bin");
        var data = new byte[(1 << 20) + 100];
        "Inno Setup Setup Data"u8.CopyTo(data.AsSpan((1 << 20) - 10));
        File.WriteAllBytes(file, data);
        Assert.True(InnoSetupPackager.ContainsMarker(file));
        File.WriteAllBytes(file, new byte[5000]);
        Assert.False(InnoSetupPackager.ContainsMarker(file));
    }

    private sealed class RecordingRunner(int exitCode = 0) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            onOutput?.Invoke(OutputStream.StdOut, "Compiler output");
            return Task.FromResult(new ProcessResult(exitCode, TimeSpan.Zero, false, false));
        }
    }
}

/// <summary>Runs only on Windows with Inno Setup and HCG_INSTALLER_TESTS=1, because it installs into the current user profile.</summary>
public sealed class RequiresWindowsInstallerFactAttribute : FactAttribute
{
    public RequiresWindowsInstallerFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows installers are built and installed only on Windows.";
        else if (Environment.GetEnvironmentVariable("HCG_INSTALLER_TESTS") != "1") Skip = "Set HCG_INSTALLER_TESTS=1 to install/uninstall into this user profile.";
        else if (Fixtures.FindDotNet() is null) Skip = "No .NET SDK found.";
    }
}

/// <summary>
/// The real thing: build the Avalonia fixture for win-x64 with a Setup.exe, install it silently for the current user,
/// verify files, shortcuts and the uninstall registration, launch the installed app, uninstall, and confirm cleanup.
/// </summary>
public class WindowsInstallerEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private sealed class Observer(ITestOutputHelper output) : IBuildObserver
    {
        public void OnLog(BuildLogLine line) { if (line.Kind is not BuildLogKind.Output) output.WriteLine(line.Text); }
    }

    [RequiresWindowsInstallerFact]
    public async Task Builds_installs_launches_and_uninstalls_a_real_setup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new AppPaths(_t.Combine("app"));
        var db = new SqliteDatabase(paths);
        var runner = new ProcessRunner();
        var toolchains = new ToolchainService(new ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System));
        var report = await toolchains.GetAsync();
        var inno = report.Find(ToolchainIds.InnoSetup);
        Assert.True(inno?.IsAvailable == true, "Inno Setup 6 must be installed for this test (ISCC.exe not found).");
        output.WriteLine($"Inno Setup {inno!.Version} at {inno.ExecutablePath}");

        var projects = new ProjectService(new ProjectImporter(paths, TimeProvider.System),
            new ProjectInspectionService([new DotNetProjectInspector(TimeProvider.System)], TimeProvider.System),
            new SqliteProjectRepository(db), new SqliteSettingsStore(db), toolchains, paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
        var project = (await projects.ImportDirectoryAsync(Fixtures.HelloAvalonia, null, CancellationToken.None)).Project;
        var builds = new BuildService(new LocalProcessBuildWorker(runner), toolchains, new SqliteBuildRepository(db),
            new PackagingRegistry([new PortableFolderPackager(), new PortableZipPackager(), new InnoSetupPackager(runner)]), paths, TimeProvider.System);

        var appName = "HCG Installer Test " + Guid.NewGuid().ToString("N")[..6];
        var settings = new BuildSettings
        {
            ProjectRelativePath = project.Analysis!.PrimaryProjectPath!, Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64,
            OutputDirectory = _t.Combine("out"), Timeout = TimeSpan.FromMinutes(20),
            Identity = new AppIdentity
            {
                ApplicationName = appName, Version = "1.4.2", Publisher = "Hakmi Capital Group", ApplicationId = "com.hakmicapital.tests." + appName.Replace(' ', '-').ToLowerInvariant(),
                IconSourcePath = Path.Combine(project.SourceDirectory, "Assets", "icon.png"),
            },
            PackagingFormats = [PackagingFormat.PortableZip, PackagingFormat.WindowsInstallerExe],
            WindowsInstaller = new WindowsInstallerOptions { OfferDesktopShortcut = true },
        };
        var build = await builds.RunAsync(new BuildRequest { Project = project, Settings = settings, TrustAcknowledged = true, AssemblyName = "HelloAvalonia" }, new Observer(output), CancellationToken.None);
        foreach (var a in build.Artifacts)
        {
            output.WriteLine($"{a.Format} {a.Path} {a.SizeBytes:N0} bytes sha256={a.Sha256} {a.Verification}");
            foreach (var c in a.Checks) output.WriteLine($"   - {c.Name}: {c.Status} — {c.Detail}");
        }
        Assert.True(build.Status == BuildStatus.Succeeded, build.FailureReason);
        Assert.Equal([PackagingFormat.PortableZip, PackagingFormat.WindowsInstallerExe], build.Artifacts.Select(a => a.Format));
        var setup = build.Artifacts.Single(a => a.Format == PackagingFormat.WindowsInstallerExe);
        Assert.EndsWith("-Setup.exe", setup.Path);
        Assert.Equal(64, setup.Sha256.Length);
        Assert.Contains(setup.Sha256, File.ReadAllText(Path.Combine(Path.GetDirectoryName(setup.Path)!, "SHA256SUMS.txt")));
        Assert.Contains(setup.Checks, c => c.Name == "Inno Setup data" && c.Status == VerificationStatus.Passed);

        var installDir = _t.Combine("Installed App");
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), appName + ".lnk");
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), appName + ".lnk");
        var uninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{{{InnoSetupScript.AppId(settings.Identity.ApplicationId).ToString("D").ToUpperInvariant()}}}_is1";
        try
        {
            // Install (silent, current user, with the optional desktop shortcut).
            var log = _t.Combine("install.log");
            var exit = await Run(setup.Path, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER", "/TASKS=desktopicon", $"/DIR={installDir}", $"/LOG={log}");
            if (exit != 0 && File.Exists(log)) output.WriteLine(File.ReadAllText(log));
            Assert.Equal(0, exit);

            // Files: everything from the publish folder plus Inno's uninstaller.
            var publishDir = Path.Combine(paths.BuildsDirectory, build.Id.ToString("N"), "publish");
            var expected = Directory.EnumerateFiles(publishDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(publishDir, f)).ToList();
            foreach (var rel in expected) Assert.True(File.Exists(Path.Combine(installDir, rel)), $"Missing installed file {rel}");
            Assert.True(File.Exists(Path.Combine(installDir, "unins000.exe")), "Uninstaller missing.");
            output.WriteLine($"Installed {expected.Count} application files to {installDir}");

            // Shortcuts and uninstall registration.
            Assert.True(File.Exists(startMenu), $"Start Menu shortcut missing: {startMenu}");
            Assert.True(File.Exists(desktop), $"Desktop shortcut missing: {desktop}");
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(uninstallKey))
            {
                Assert.NotNull(key);
                Assert.Equal(appName, key!.GetValue("DisplayName"));
                Assert.Equal("1.4.2", key.GetValue("DisplayVersion"));
                Assert.Equal("Hakmi Capital Group", key.GetValue("Publisher"));
                Assert.Contains("unins000.exe", (string)key.GetValue("UninstallString")!);
            }

            // Launch the installed application and confirm it stays up.
            using (var app = Process.Start(new ProcessStartInfo(Path.Combine(installDir, "HelloAvalonia.exe")) { WorkingDirectory = installDir, UseShellExecute = false })!)
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                Assert.False(app.HasExited, $"The installed application exited immediately with code {(app.HasExited ? app.ExitCode : 0)}.");
                output.WriteLine($"Installed app running as PID {app.Id}; closing it.");
                app.Kill(entireProcessTree: true);
                await app.WaitForExitAsync();
            }

            // Uninstall and confirm cleanup.
            Assert.Equal(0, await Run(Path.Combine(installDir, "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"));
            await WaitUntil(() => !Directory.Exists(installDir) && !File.Exists(startMenu), TimeSpan.FromSeconds(60));
            Assert.False(Directory.Exists(installDir), "Install directory remained after uninstall.");
            Assert.False(File.Exists(startMenu), "Start Menu shortcut remained after uninstall.");
            Assert.False(File.Exists(desktop), "Desktop shortcut remained after uninstall.");
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(uninstallKey))
                Assert.Null(key);
            output.WriteLine("Uninstall removed the files, both shortcuts and the uninstall registration.");
        }
        finally
        {
            var uninstaller = Path.Combine(installDir, "unins000.exe");
            if (File.Exists(uninstaller)) await Run(uninstaller, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART");
            foreach (var f in new[] { startMenu, desktop }) if (File.Exists(f)) File.Delete(f);
        }
    }

    private static async Task<int> Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await p.WaitForExitAsync(cts.Token);
        return p.ExitCode;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout) await Task.Delay(500);
    }
}
