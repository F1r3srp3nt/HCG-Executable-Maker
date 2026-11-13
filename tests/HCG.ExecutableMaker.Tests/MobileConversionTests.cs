using System.Text.Json;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Porting;
using HCG.ExecutableMaker.Porting.Web;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Desktop → mobile conversion workspace: compatibility report (sources, four feature classes, per-target options),
/// the support matrix, web-based mobile packaging (Vite + React → Capacitor), source preservation, the migration report,
/// reproducibility, validation and failure reporting. Nothing here runs npm or a mobile build (a scripted runner stands in).
/// </summary>
public class MobileConversionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string Fixture(string name) => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", name);

    private static readonly IReadOnlyList<IPorter> Porters =
        [new HCG.ExecutableMaker.Porting.Apple.MacToIosPorter(), new HCG.ExecutableMaker.Porting.Android.LinuxToAndroidPorter(), new WebToMobilePorter(MobileTarget.Android), new WebToMobilePorter(MobileTarget.Ios)];

    private static CompatibilityReport Inspect(string root)
    {
        var index = SourceIndex.Build(root);
        var assessments = Porters.SelectMany(p => p.FindSources(index).Select(s => p.Assess(index, s, null))).ToList();
        return CompatibilityInspector.Build(index, assessments, SourceRevisionReader.Capture(root, index, TimeProvider.System), DateTimeOffset.UtcNow);
    }

    private string CopyFixture(string name)
    {
        var dest = _t.Combine("src-" + name);
        foreach (var file in Directory.EnumerateFiles(Fixture(name), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(Fixture(name), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return dest;
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllText);

    // ───────────── Support matrix ─────────────

    [Fact]
    public void The_matrix_is_honest_implemented_rows_have_a_converter_and_planned_rows_have_requirements()
    {
        var implementedPaths = Porters.Select(p => p.Path).ToHashSet();
        foreach (var row in ConversionCatalog.Matrix)
        {
            if (row.Status == SupportStatus.Implemented) Assert.True(row.Path is { } p && implementedPaths.Contains(p), $"{row.SourceFramework} → {row.Target}: implemented without a converter");
            else Assert.Null(row.Path);
            if (row.Status == SupportStatus.Planned) Assert.NotEmpty(row.Requirements);
            Assert.False(string.IsNullOrWhiteSpace(row.Summary));
        }
        foreach (var path in implementedPaths) Assert.NotNull(ConversionCatalog.For(path));
        // Both targets for every framework; HCG Server is a planned companion with its own contract requirement.
        foreach (var g in ConversionCatalog.Matrix.GroupBy(r => r.SourceFramework))
            Assert.Equal([MobileTarget.Ios, MobileTarget.Android], g.Select(r => r.Target).Order());
        var server = ConversionCatalog.For(ConversionCatalog.HcgServer).ToList();
        Assert.All(server, r => Assert.Equal((ConversionMode.MobileCompanion, SupportStatus.Planned), (r.Mode, r.Status)));
        Assert.Contains(server[0].Requirements, r => r.Contains("HCG Server agent API", StringComparison.Ordinal));
        Assert.Contains(server[0].Requirements, r => r.Contains("No server secrets", StringComparison.Ordinal));
    }

    // ───────────── Compatibility report ─────────────

    [Fact]
    public void A_vite_react_app_is_reusable_with_adaptations_for_both_targets()
    {
        var report = Inspect(Fixture("WebViteDesktop"));
        var source = Assert.Single(report.Sources);
        Assert.Equal(ConversionCatalog.ViteReact, source.Framework);
        Assert.Contains(report.Features, f => f.Class == FeatureClass.Reusable && f.Title.StartsWith("Web app source", StringComparison.Ordinal));
        Assert.Contains(report.Features, f => f.Class == FeatureClass.RequiresAdaptation && f.Title == "Touch and safe-area styles");
        Assert.Equal(0, report.Count(FeatureClass.Unsupported));
        Assert.Equal(0, report.Count(FeatureClass.RequiresBackendOrCompanion));
        foreach (var (target, path) in new[] { (MobileTarget.Android, PortingPath.WebToAndroid), (MobileTarget.Ios, PortingPath.WebToIOS) })
        {
            var o = report.Options.Single(x => x.Target == target);
            Assert.Equal((SupportStatus.Implemented, ConversionMode.WebMobilePackaging, path, PortingCapability.GenerateTarget), (o.Status, o.Mode, o.Path, o.Capability));
            Assert.True(o.CanGenerate);
        }
        Assert.False(report.ExecutableOnly);
        Assert.Empty(report.Blockers);
        Assert.NotNull(report.Revision);
    }

    [Fact]
    public void Server_calls_sign_in_and_tokens_are_classified_not_faked()
    {
        var dir = CopyFixture("WebViteReact");
        File.WriteAllText(Path.Combine(dir, "src", "session.ts"), "export const save = (t: string) => localStorage.setItem('auth_token', t);\nexport const pick = () => window.showOpenFilePicker();\n");
        var report = Inspect(dir);
        Assert.Contains(report.Features, f => f.Class == FeatureClass.RequiresBackendOrCompanion && f.Title == "Request to the page's own server" && f.Location == "src/main.tsx:8");
        Assert.Contains(report.Features, f => f.Class == FeatureClass.RequiresBackendOrCompanion && f.Title == "Development server proxy");
        Assert.Contains(report.Features, f => f.Class == FeatureClass.RequiresAdaptation && f.Title == "Sign-in callback" && f.Location == "src/main.tsx:13");
        Assert.Contains(report.Features, f => f.Class == FeatureClass.RequiresAdaptation && f.Title == "Credential kept in web storage" && f.Location == "src/session.ts:1");
        Assert.Contains(report.Features, f => f.Class == FeatureClass.RequiresAdaptation && f.Title == "File System Access API");
        var a = new WebToMobilePorter(MobileTarget.Android);
        var index = SourceIndex.Build(dir);
        var assessment = a.Assess(index, a.FindSources(index).Single(), null);
        Assert.Equal(PortingCapability.GenerateTarget, assessment.Capability);
        Assert.Contains(assessment.ManualSteps, s => s.Contains("identity provider", StringComparison.Ordinal) && s.Contains("never reuse the web client", StringComparison.Ordinal));
        Assert.Contains(assessment.ManualSteps, s => s.Contains("Deploy the backend", StringComparison.Ordinal));
        Assert.Contains(assessment.ManualSteps, s => s.Contains("Keychain/Keystore", StringComparison.Ordinal));
    }

    [Fact]
    public void A_server_secret_in_the_web_build_blocks_generation()
    {
        var dir = CopyFixture("WebViteDesktop");
        File.AppendAllText(Path.Combine(dir, "src", "main.jsx"), "\nconst admin = import.meta.env.VITE_SUPABASE_SERVICE_ROLE_KEY;\n");
        var porter = new WebToMobilePorter(MobileTarget.Android);
        var index = SourceIndex.Build(dir);
        var a = porter.Assess(index, porter.FindSources(index).Single(), null);
        Assert.Equal(PortingCapability.BlockedNeedsPorting, a.Capability);
        var blocker = Assert.Single(a.Blockers);
        Assert.Contains("VITE_SUPABASE_SERVICE_ROLE_KEY", blocker.Title, StringComparison.Ordinal);
        Assert.Equal(FeatureClass.Unsupported, blocker.Class);
        var ex = Assert.Throws<InvalidOperationException>(() => porter.Generate(index, a, new PortOptions { AppName = "Notes", AppId = "com.example.notes" }, new PortOutput(dir, _t.Combine("out"))));
        Assert.Contains("blockers", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Inspect(dir).Blockers, b => b.Contains("unsupported by the current converter", StringComparison.Ordinal));
    }

    [Fact]
    public void An_executable_alone_is_not_sufficient_source()
    {
        _t.Write("dist/Field Notes.exe", "MZ\u0090\u0000 not a real program");
        File.WriteAllBytes(_t.Combine("dist", "fieldnotes"), [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]);
        _t.Write("dist/Field Notes.app/Contents/MacOS/Field Notes", "binary");
        var report = Inspect(_t.Combine("dist"));
        Assert.True(report.ExecutableOnly);
        Assert.Empty(report.Sources);
        Assert.Empty(report.Options);
        Assert.Equal(3, report.Executables.Count);
        var blocker = Assert.Single(report.Blockers);
        Assert.Contains("A compiled executable cannot be converted", blocker, StringComparison.Ordinal);
        Assert.Contains("Import the application's source project", blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void Frameworks_without_a_converter_show_their_planned_requirements()
    {
        _t.Write("electron-app/package.json", """{ "name": "studio", "dependencies": { "react": "19.0.0", "react-dom": "19.0.0" }, "devDependencies": { "electron": "45.2.3", "vite": "7.0.0" } }""");
        _t.Write("electron-app/src/main.tsx", "window.electronAPI; import { ipcRenderer } from 'electron';\n");
        _t.Write("agent/package.json", """{ "name": "agent", "bin": { "agent": "cli.js" }, "dependencies": { "fastify": "5.0.0" } }""");
        _t.Write("desk/Desk.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWindowsForms>true</UseWindowsForms></PropertyGroup></Project>");
        var report = Inspect(_t.Path);

        var electron = report.Sources.Single(s => s.Framework == ConversionCatalog.Electron);
        Assert.All(report.Options.Where(o => o.SourceKey == electron.Key), o =>
        {
            Assert.Equal((SupportStatus.Planned, ConversionMode.WebMobilePackaging), (o.Status, o.Mode));
            Assert.Contains(o.Requirements, r => r.Contains("preload/IPC", StringComparison.Ordinal));
            Assert.False(o.CanGenerate);
        });
        Assert.Contains(report.Features, f => f.SourceKey == electron.Key && f.Class == FeatureClass.Unsupported && f.Title == "Electron API");

        var agent = report.Sources.Single(s => s.Framework == ConversionCatalog.NodeServer);
        Assert.Contains(report.Features, f => f.SourceKey == agent.Key && f.Class == FeatureClass.RequiresBackendOrCompanion);
        Assert.All(report.Options.Where(o => o.SourceKey == agent.Key), o => Assert.Equal((SupportStatus.Planned, ConversionMode.MobileCompanion), (o.Status, o.Mode)));

        var desk = report.Sources.Single(s => s.Framework == ConversionCatalog.DotNetWindows);
        Assert.Contains(report.Features, f => f.SourceKey == desk.Key && f.Class == FeatureClass.Unsupported && f.Title == "Windows-only UI");
        Assert.All(report.Options.Where(o => o.SourceKey == desk.Key), o => Assert.Equal(SupportStatus.NotPlanned, o.Status));
    }

    [Fact]
    public void A_c_program_is_implemented_for_Android_and_planned_for_iOS()
    {
        var report = Inspect(Fixture("LinuxPrimes"));
        var key = report.Sources.Single(s => s.Framework == ConversionCatalog.CmakeConsole).Key;
        Assert.Equal((SupportStatus.Implemented, PortingPath.LinuxToAndroid), report.Options.Where(o => o.SourceKey == key && o.Target == MobileTarget.Android).Select(o => (o.Status, o.Path!.Value)).Single());
        var ios = report.Options.Single(o => o.SourceKey == key && o.Target == MobileTarget.Ios);
        Assert.Equal(SupportStatus.Planned, ios.Status);
        Assert.Contains(ios.Requirements, r => r.Contains("console host", StringComparison.Ordinal));
    }

    // ───────────── Generation ─────────────

    private (PortingAssessment Assessment, SourceIndex Index, IPorter Porter) WebAssessment(string dir, MobileTarget target)
    {
        var porter = new WebToMobilePorter(target);
        var index = SourceIndex.Build(dir);
        return (porter.Assess(index, porter.FindSources(index).Single(), null), index, porter);
    }

    [Theory]
    [InlineData(MobileTarget.Android)]
    [InlineData(MobileTarget.Ios)]
    public void Generation_writes_a_separate_capacitor_project_and_preserves_the_source(MobileTarget target)
    {
        var dir = CopyFixture("WebViteDesktop");
        File.WriteAllText(Path.Combine(dir, ".env.local"), "VITE_API=https://secret.example\n");
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "react"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "react", "index.js"), "");
        var before = Snapshot(dir);
        var (a, index, porter) = WebAssessment(dir, target);
        var output = new PortOutput(dir, _t.Combine("out"));
        var (steps, buildRoot) = porter.Generate(index, a, new PortOptions { AppName = "Field Notes", AppId = "com.example.fieldnotes" }, output);

        Assert.Equal(before, Snapshot(dir)); // the source is untouched
        var outDir = output.Root;
        Assert.Equal(target == MobileTarget.Android ? "android" : "ios/App", buildRoot);
        Assert.True(File.Exists(Path.Combine(outDir, "src", "main.jsx")));
        Assert.False(File.Exists(Path.Combine(outDir, ".env.local")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "node_modules")));
        var platform = target == MobileTarget.Android ? "android" : "ios";

        var pkg = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "package.json"))).RootElement;
        Assert.Equal(WebToMobilePorter.CapacitorVersion, pkg.GetProperty("dependencies").GetProperty("@capacitor/core").GetString());
        Assert.Equal(WebToMobilePorter.CapacitorVersion, pkg.GetProperty("dependencies").GetProperty($"@capacitor/{platform}").GetString());
        Assert.Equal(WebToMobilePorter.CapacitorVersion, pkg.GetProperty("devDependencies").GetProperty("@capacitor/cli").GetString());
        Assert.Equal($"npm run build && cap sync {platform}", pkg.GetProperty("scripts").GetProperty("mobile:sync").GetString());

        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, WebToMobilePorter.ConfigFile))).RootElement;
        Assert.Equal("com.example.fieldnotes", config.GetProperty("appId").GetString());
        Assert.Equal("dist", config.GetProperty("webDir").GetString());
        Assert.False(config.TryGetProperty("server", out _)); // never a development-server URL

        var html = File.ReadAllText(Path.Combine(outDir, "index.html"));
        Assert.Contains("viewport-fit=cover", html, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/hcg-mobile.css\" />", html, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", File.ReadAllText(Path.Combine(outDir, WebToMobilePorter.MobileCss)), StringComparison.Ordinal);

        var changes = output.Changes;
        Assert.Contains(changes, c => c.Path == "package.json" && c.Kind == FileChangeKind.Modified && c.Diff.Contains("+    \"@capacitor/core\"", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Path == "index.html" && c.Kind == FileChangeKind.Modified);
        Assert.Contains(changes, c => c.Path == WebToMobilePorter.ConfigFile && c.Kind == FileChangeKind.Added);
        Assert.Contains(steps, s => s.StartsWith("Validate (next step, runs code after you confirm trust)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<head>\n    <title>x</title>\n  </head>", "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\" />")]
    [InlineData("<head>\n<meta name=\"viewport\" content=\"width=device-width\">\n</head>", "content=\"width=device-width, viewport-fit=cover\"")]
    public void The_viewport_is_added_or_extended_once(string head, string expected)
    {
        var html = WebToMobilePorter.IndexHtml($"<html>{head}<body></body></html>");
        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.Equal(html, WebToMobilePorter.IndexHtml(html));
    }

    [Fact]
    public void Invalid_identity_and_existing_capacitor_setup_are_refused()
    {
        var dir = CopyFixture("WebViteDesktop");
        var (a, index, porter) = WebAssessment(dir, MobileTarget.Android);
        Assert.Throws<InvalidOperationException>(() => porter.Generate(index, a, new PortOptions { AppName = "Notes", AppId = "not an id" }, new PortOutput(dir, _t.Combine("o1"))));
        Assert.Throws<InvalidOperationException>(() => new PortOutput(dir, Path.Combine(dir, "inside"))); // never inside the original
        File.WriteAllText(Path.Combine(dir, "capacitor.config.json"), "{}");
        var (again, index2, porter2) = WebAssessment(dir, MobileTarget.Android);
        // An existing Capacitor setup is never changed: the source is recognized as such, and nothing is generated.
        Assert.Equal(("Existing Capacitor app", PortingCapability.NotSupported), (again.Source.Kind, again.Capability));
        Assert.Contains("HCG does not change an existing Capacitor setup", again.Summary, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => porter2.Generate(index2, again, new PortOptions { AppName = "Notes", AppId = "com.example.notes" }, new PortOutput(dir, _t.Combine("o2"))));
    }

    // ───────────── Report, reproducibility, validation ─────────────

    private sealed class FixedToolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    /// <summary>Stands in for npm and Capacitor's CLI: writes what they would write; can fail one step.</summary>
    private sealed class ScriptedRunner(string? failOn = null) : IProcessRunner
    {
        public List<string> Commands { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
        {
            var line = string.Join(' ', spec.Arguments.Skip(1));
            Commands.Add(line);
            onOutput?.Invoke(OutputStream.StdOut, "ok " + line);
            if (failOn is not null && line.Contains(failOn, StringComparison.Ordinal)) return Task.FromResult(new ProcessResult(1, TimeSpan.Zero, false, false));
            var root = spec.WorkingDirectory!;
            if (line.StartsWith("run ", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Path.Combine(root, "dist"));
                File.WriteAllText(Path.Combine(root, "dist", "index.html"), "<!doctype html><script type=module src=/assets/index.js></script>");
            }
            if (line.StartsWith("add android", StringComparison.Ordinal))
            {
                var assets = Path.Combine(root, "android", "app", "src", "main", "assets");
                Directory.CreateDirectory(Path.Combine(assets, "public"));
                File.WriteAllText(Path.Combine(assets, "public", "index.html"), "<!doctype html>");
                File.WriteAllText(Path.Combine(assets, "capacitor.config.json"), "{\"appId\":\"com.example.fieldnotes\"}");
                File.WriteAllText(Path.Combine(root, "android", "gradlew"), "#!/bin/sh");
            }
            return Task.FromResult(new ProcessResult(0, TimeSpan.Zero, false, false));
        }
    }

    private static readonly NodeInstallation Node22 = new("/usr/bin/node", "22.12.0", "PATH", "/usr/lib/node_modules/npm/bin/npm-cli.js", "10.9.0");

    private (PortingService Service, ScriptedRunner Runner) Service(string? failOn = null, params NodeInstallation[] nodes)
    {
        var runner = new ScriptedRunner(failOn);
        var host = new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "test", ".NET", "test");
        var report = new ToolchainReport(host, [], null, null, DateTimeOffset.UtcNow) { NodeInstallations = nodes.Length == 0 ? [Node22] : nodes };
        return (new PortingService(new FixedToolchains(report), null!, null!, new DeviceLauncher(runner), runner, TimeProvider.System), runner);
    }

    private PortReport Generated(string dir)
    {
        var (a, index, porter) = WebAssessment(dir, MobileTarget.Android);
        var output = new PortOutput(dir, _t.Combine("out"));
        var options = new PortOptions { AppName = "Field Notes", AppId = "com.example.fieldnotes" };
        var (steps, root) = porter.Generate(index, a, options, output);
        var report = new PortReport
        {
            Id = Guid.NewGuid(), SourceProject = "WebViteDesktop", SourceDirectory = dir, OutputDirectory = output.Root, BuildRoot = root,
            Assessment = a, Plan = new MigrationPlan { Steps = steps, Changes = output.Changes }, Options = options, CreatedAt = DateTimeOffset.UtcNow,
            Source = SourceRevisionReader.Capture(dir, index, TimeProvider.System), GeneratorVersion = "test",
            Verification = [new VerificationEntry(VerificationLevel.Generated, LevelStatus.Passed, "ok")],
        };
        PortingService.Save(report);
        return report;
    }

    [Fact]
    public void The_migration_report_and_conversion_record_make_the_result_reproducible()
    {
        var dir = CopyFixture("WebViteReact");
        // A Git working copy: the commit and branch are recorded (read from .git as files).
        Directory.CreateDirectory(Path.Combine(dir, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(dir, ".git", "refs", "heads", "main"), new string('a', 40) + "\n");
        var report = Generated(dir);
        Assert.Equal(("git", new string('a', 40), "main"), (report.Source!.Vcs, report.Source.Commit, report.Source.Branch));

        var hcg = Path.Combine(report.OutputDirectory, PortingService.ReportFolder);
        var md = File.ReadAllText(Path.Combine(hcg, "MIGRATION.md"));
        Assert.Contains("Web-based mobile packaging", md, StringComparison.Ordinal);
        Assert.Contains("| Validated | Not performed |", md, StringComparison.Ordinal);
        Assert.Contains("| Compiled | Not performed |", md, StringComparison.Ordinal);
        Assert.Contains("### Requires a backend or companion computer", md, StringComparison.Ordinal);
        Assert.Contains("### Unsupported by the current converter (0)", md, StringComparison.Ordinal);
        Assert.Contains("## Unresolved (not generated, not faked)", md, StringComparison.Ordinal);
        Assert.Contains("Request to the page's own server", md, StringComparison.Ordinal);
        Assert.Contains("never reuse the web client", md, StringComparison.Ordinal);
        Assert.Contains("never done by HCG", md, StringComparison.Ordinal);

        var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(hcg, "conversion.json"))).RootElement;
        Assert.Equal(new string('a', 40), record.GetProperty("source").GetProperty("commit").GetString());
        Assert.Equal(report.Source.ContentSha256, record.GetProperty("source").GetProperty("contentSha256").GetString());
        Assert.Equal("WebMobilePackaging", record.GetProperty("conversion").GetProperty("mode").GetString());
        Assert.Equal("com.example.fieldnotes", record.GetProperty("conversion").GetProperty("appId").GetString());
        Assert.Equal(WebToMobilePorter.CapacitorVersion, record.GetProperty("pins").GetProperty("@capacitor/android").GetString());

        // The same files give the same fingerprint; any change gives another.
        var again = SourceRevisionReader.Fingerprint(SourceIndex.Build(dir), out _);
        Assert.Equal(report.Source.ContentSha256, again);
        File.AppendAllText(Path.Combine(dir, "src", "main.tsx"), "// changed\n");
        Assert.NotEqual(report.Source.ContentSha256, SourceRevisionReader.Fingerprint(SourceIndex.Build(dir), out _));
    }

    [Fact]
    public async Task Validation_without_trust_runs_nothing_and_says_what_is_left()
    {
        var report = Generated(CopyFixture("WebViteDesktop"));
        var (service, runner) = Service();
        var validated = await service.ValidateAsync(report, trustAcknowledged: false, null, CancellationToken.None);
        Assert.Empty(runner.Commands);
        var level = validated.Level(VerificationLevel.Validated);
        Assert.Equal(LevelStatus.NotPerformed, level.Status);
        Assert.Contains("confirm trust first", level.Detail, StringComparison.Ordinal);
        Assert.All(validated.ValidationChecks, c => Assert.True(c.Passed, c.Name + ": " + c.Detail));
        Assert.Contains(validated.ValidationChecks, c => c.Name == "Original project unchanged");
        Assert.Contains(validated.ValidationChecks, c => c.Name == "No environment files with values");
    }

    [Fact]
    public async Task Trusted_validation_builds_the_web_app_and_creates_the_native_project()
    {
        var report = Generated(CopyFixture("WebViteDesktop"));
        var (service, runner) = Service();
        var validated = await service.ValidateAsync(report, trustAcknowledged: true, null, CancellationToken.None);
        var level = validated.Level(VerificationLevel.Validated);
        Assert.True(level.Status == LevelStatus.Passed, level.Detail + "\n" + string.Join("\n", validated.ValidationChecks.Select(c => $"{c.Passed} {c.Name}: {c.Detail}")));
        Assert.Equal(["install --no-audit --no-fund", "run build", "add android"], runner.Commands.Select(c => c.Replace(Path.Combine(report.OutputDirectory, "node_modules", "@capacitor", "cli", "bin", "capacitor") + " ", "", StringComparison.Ordinal)));
        Assert.Contains("Not yet built for a device or simulator", level.Detail, StringComparison.Ordinal);
        Assert.Equal(LevelStatus.NotPerformed, validated.Level(VerificationLevel.Compiled).Status); // validation is not a build
        Assert.True(File.Exists(Path.Combine(report.OutputDirectory, PortingService.ReportFolder, "validate.log")));
        Assert.Equal(LevelStatus.Passed, PortingService.Load(report.OutputDirectory).Level(VerificationLevel.Validated).Status);
    }

    [Fact]
    public async Task Failures_are_reported_with_the_step_and_a_modified_source_is_detected()
    {
        var dir = CopyFixture("WebViteDesktop");
        var report = Generated(dir);
        var (failing, _) = Service(failOn: "run build");
        var failed = await failing.ValidateAsync(report, true, null, CancellationToken.None);
        Assert.Equal(LevelStatus.Failed, failed.Level(VerificationLevel.Validated).Status);
        Assert.Contains("The project's own web build failed (npm run build)", failed.Level(VerificationLevel.Validated).Detail, StringComparison.Ordinal);

        var (oldNode, _) = Service(null, Node22 with { Version = "20.11.0" });
        var noNode = await oldNode.ValidateAsync(report, true, null, CancellationToken.None);
        Assert.Equal(LevelStatus.NotPossibleHere, noNode.Level(VerificationLevel.Validated).Status);
        Assert.Contains("Node.js 22 or newer", noNode.Level(VerificationLevel.Validated).Detail, StringComparison.Ordinal);

        File.AppendAllText(Path.Combine(dir, "src", "main.jsx"), "// edited after conversion\n");
        var (service, runner) = Service();
        var changed = await service.ValidateAsync(report, true, null, CancellationToken.None);
        Assert.Equal(LevelStatus.Failed, changed.Level(VerificationLevel.Validated).Status);
        Assert.Contains(changed.ValidationChecks, c => c.Name == "Original project unchanged" && !c.Passed);
        Assert.Empty(runner.Commands); // nothing runs when the static checks fail
    }

    // ───────────── Capacitor in Mobile Build Studio ─────────────

    [Fact]
    public void A_generated_and_validated_project_is_recognized_as_a_capacitor_app()
    {
        _t.Write("app/package.json", """{ "name": "notes", "scripts": { "build": "vite build" }, "dependencies": { "@capacitor/core": "8.5.3" } }""");
        _t.Write("app/capacitor.config.json", """{ "appId": "com.example.notes", "appName": "Notes", "webDir": "dist" }""");
        _t.Write("app/android/settings.gradle", "include ':app'\n");
        _t.Write("app/android/app/build.gradle", "apply plugin: 'com.android.application'\nandroid { namespace 'com.example.notes'\n defaultConfig { applicationId 'com.example.notes' } }\n");
        var mobile = MobileProjectInspector.Analyze(SourceIndex.Build(_t.Path), []);
        Assert.Equal(MobileFramework.Capacitor, mobile.Framework);
        Assert.Equal([TargetPlatform.Android], mobile.Platforms);
        Assert.Equal(("app", "com.example.notes", "Notes", "dist", "build"), (mobile.Capacitor!.Root, mobile.Capacitor.AppId, mobile.Capacitor.AppName, mobile.Capacitor.WebDir, mobile.Capacitor.BuildScript));
        Assert.Equal("app/android", mobile.Android!.Root);
    }
}

public sealed class MobileConversionFactAttribute : FactAttribute
{
    public MobileConversionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HCG_MOBILE_CONVERSION_OUT") is not { Length: > 0 })
            Skip = "Set HCG_MOBILE_CONVERSION_OUT to a folder to run the real web → Android conversion (npm, Vite build, Capacitor CLI) of tests/Fixtures/WebViteDesktop.";
    }
}

/// <summary>REAL: generate, validate (npm install, the fixture's vite build, cap add android) and compile when the Android SDK is here.</summary>
public class MobileConversionRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [MobileConversionFact]
    public async Task The_fixture_becomes_a_real_capacitor_android_project()
    {
        var outParent = Path.GetFullPath(Environment.GetEnvironmentVariable("HCG_MOBILE_CONVERSION_OUT")!);
        Directory.CreateDirectory(outParent);
        var source = Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "WebViteDesktop");
        var before = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
        var porter = new WebToMobilePorter(MobileTarget.Android);
        var index = SourceIndex.Build(source);
        var paths = new AppPaths(Path.Combine(outParent, "hcg-data"));
        var runner = new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner();
        var toolchains = await new HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System).DetectAsync(CancellationToken.None);
        var assessment = porter.Assess(index, porter.FindSources(index).Single(), toolchains);
        foreach (var f in assessment.Findings) output.WriteLine($"[{f.Class.DisplayName()}] {f.Title} {f.Location}");
        var outDir = Path.Combine(outParent, "WebViteDesktop-android-mobile");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        var o = new PortOutput(source, outDir);
        var options = new PortOptions { AppName = "Field Notes", AppId = "com.example.fieldnotes" };
        var (steps, root) = porter.Generate(index, assessment, options, o);
        var report = new PortReport
        {
            Id = Guid.NewGuid(), SourceProject = "WebViteDesktop", SourceDirectory = source, OutputDirectory = o.Root, BuildRoot = root, Assessment = assessment,
            Plan = new MigrationPlan { Steps = steps, Changes = o.Changes }, Options = options, CreatedAt = DateTimeOffset.UtcNow,
            Source = SourceRevisionReader.Capture(source, index, TimeProvider.System), GeneratorVersion = HostInfo.ProductVersion,
            Verification = [new VerificationEntry(VerificationLevel.Generated, LevelStatus.Passed, $"{o.Changes.Count} files")],
        };
        PortingService.Save(report);
        var service = new PortingService(new Fixed(toolchains), null!, null!, new DeviceLauncher(runner), runner, TimeProvider.System);
        report = await service.ValidateAsync(report, trustAcknowledged: true, output.WriteLine, CancellationToken.None);
        foreach (var c in report.ValidationChecks) output.WriteLine($"{(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Detail}");
        var validated = report.Level(VerificationLevel.Validated);
        output.WriteLine($"Validated: {validated.Status} — {validated.Detail}");
        Assert.Equal(LevelStatus.Passed, validated.Status);
        Assert.True(File.Exists(Path.Combine(o.Root, "android", "app", "src", "main", "assets", "public", "index.html")));
        var compiled = (await service.CompileAsync(report, true, new NullObserver(), CancellationToken.None)).Report.Level(VerificationLevel.Compiled);
        output.WriteLine($"Compiled: {compiled.Status} — {compiled.Detail}");
        if (!toolchains.IsAvailable(ToolchainIds.AndroidSdk)) Assert.Equal(LevelStatus.NotPossibleHere, compiled.Status);
        Assert.All(before, kv => Assert.Equal(kv.Value, File.ReadAllText(kv.Key)));
        output.WriteLine("Generated mobile project: " + o.Root);
    }

    private sealed class Fixed(ToolchainReport r) : IToolchainService
    {
        public ToolchainReport? Latest => r;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(r);
    }

    private sealed class NullObserver : HCG.ExecutableMaker.BuildEngine.IBuildObserver
    {
        public void OnStage(HCG.ExecutableMaker.BuildEngine.BuildStage stage, string description) { }
        public void OnLog(HCG.ExecutableMaker.BuildEngine.BuildLogLine line) { }
    }
}
