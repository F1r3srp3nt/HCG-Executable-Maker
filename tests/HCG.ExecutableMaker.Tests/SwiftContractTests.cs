using System.Text;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Swift;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Swift;
using Xunit.Abstractions;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Packaging contracts (AppPackaging.json, hcg-app-packaging/1): strict reading, confirmation, prebuild planning,
/// staging, Info.plist templates, helper placement and verification — and, on a Mac, a real build of an app whose
/// helper is compiled by a prebuild command.
/// </summary>
public class SwiftContractTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    // The contract HCG Server ships (macos/HCGServerApp/AppPackaging.json), kept identical to that file, with its
    // package two levels down. The real file has a top-level "description"; a test copy without it once hid that
    // HCG rejected it.
    private const string HcgServerContract = """
        {
          "schema": "hcg-app-packaging/1",
          "description": "Everything HCG Server.app needs beyond the SwiftPM product. scripts/build-macos-app.sh reads this file and is the reference implementation; see docs/MACOS-APP.md#packaging-contract.",
          "product": "HCGServer",
          "bundle": { "displayName": "HCG Server", "identifier": "com.hcg.server.app", "infoPlist": "Resources/Info.plist", "icon": "Resources/AppIcon.icns", "minimumMacOS": "13.0" },
          "prebuild": [ { "description": "Build the host agent (Go) as a universal helper", "requires": ["go >= 1.25"], "workingDirectory": "../..",
                          "command": ["scripts/build-agent-helper.sh", "--output", "${HELPERS_DIR}/hcg-agent"] } ],
          "helpers": [ { "name": "hcg-agent", "source": "${HELPERS_DIR}/hcg-agent", "destination": "Contents/Helpers/hcg-agent", "required": true,
                         "architectures": ["arm64", "x86_64"], "sign": "before-app", "verify": ["version"] } ],
          "resources": [ { "source": "Resources/Artwork/hcg-server-artwork.jpg", "destination": "Contents/Resources/hcg-server-artwork.jpg", "required": true } ]
        }
        """;

    private string Workspace()
    {
        var root = _t.Combine("ws");
        Directory.CreateDirectory(root);
        _t.Write("ws/macos/HCGServerApp/Package.swift", "// swift-tools-version:5.9\nimport PackageDescription\nlet package = Package(name: \"HCGServerApp\")\n");
        _t.Write("ws/macos/HCGServerApp/AppPackaging.json", HcgServerContract);
        return root;
    }

    private static SwiftPackagingContract Parse(string json, string root) =>
        SwiftPackagingContractReader.Parse(Encoding.UTF8.GetBytes(json), "macos/HCGServerApp/AppPackaging.json", root);

    // ───────────── Reading ─────────────

    [Fact]
    public void The_HCG_Server_contract_is_read_as_data()
    {
        var root = Workspace();
        var (c, error) = SwiftPackagingContractReader.Read(root, "macos/HCGServerApp");
        Assert.Null(error);
        Assert.NotNull(c);
        Assert.Equal("HCGServer", c.Product);
        Assert.Equal("macos/HCGServerApp/AppPackaging.json", c.Path);
        Assert.Equal(64, c.Sha256.Length);
        Assert.Equal("com.hcg.server.app", c.Bundle.Identifier);
        var step = Assert.Single(c.Prebuild);
        Assert.Equal("../..", step.WorkingDirectory);
        Assert.Equal(["scripts/build-agent-helper.sh", "--output", "${HELPERS_DIR}/hcg-agent"], step.Command);
        Assert.Equal(new SwiftToolRequirement("go", "1.25"), Assert.Single(step.Requires));
        var helper = Assert.Single(c.Helpers);
        Assert.Equal(("hcg-agent", "Contents/Helpers/hcg-agent"), (helper.Name, helper.Destination));
        Assert.Equal(["arm64", "x86_64"], helper.Architectures);
        Assert.Equal(["version"], helper.Verify);
        Assert.Equal("Contents/Resources/hcg-server-artwork.jpg", Assert.Single(c.Resources).Destination);
    }

    [Fact]
    public void A_package_without_a_contract_has_none_and_the_scanner_records_it()
    {
        var root = Workspace();
        Assert.Equal((null, null), SwiftPackagingContractReader.Read(root, ""));
        var info = SwiftPackageScanner.Scan(SourceIndex.Build(root), "macos/HCGServerApp/Package.swift");
        Assert.Equal("HCGServer", info.PackagingContract?.Product);
        Assert.Null(info.PackagingContractError);

        _t.Write("ws/macos/HCGServerApp/AppPackaging.json", "{ \"schema\": \"hcg-app-packaging/2\", \"product\": \"HCGServer\" }");
        var broken = SwiftPackageScanner.Scan(SourceIndex.Build(root), "macos/HCGServerApp/Package.swift");
        Assert.Null(broken.PackagingContract);
        Assert.Contains("hcg-app-packaging/2", broken.PackagingContractError);
    }

    [Theory]
    [InlineData("\"schema\": \"hcg-app-packaging/1\"", "\"schema\": \"other\"", "not supported")]
    [InlineData("\"product\": \"HCGServer\",", "\"product\": \"HCGServer\", \"extra\": 1,", "unknown key \"extra\"")]
    [InlineData("\"command\": [\"scripts/build-agent-helper.sh\", \"--output\", \"${HELPERS_DIR}/hcg-agent\"]", "\"command\": \"scripts/build-agent-helper.sh --output x\"", "must be a list")]
    [InlineData("\"destination\": \"Contents/Helpers/hcg-agent\"", "\"destination\": \"Contents/MacOS/hcg-agent\"", "Contents/MacOS holds only")]
    [InlineData("\"workingDirectory\": \"../..\"", "\"workingDirectory\": \"../../..\"", "outside the project")]
    [InlineData("\"workingDirectory\": \"../..\"", "\"workingDirectory\": \"/usr/local\"", "relative path")]
    [InlineData("\"--output\", \"${HELPERS_DIR}/hcg-agent\"", "\"--output\", \"${HOME}/hcg-agent\"", "variable or substitution")]
    [InlineData("\"--output\", \"${HELPERS_DIR}/hcg-agent\"", "\"--output\", \"${HELPERS_DIR}/other\"", "no prebuild command writes it")]
    [InlineData("\"architectures\": [\"arm64\", \"x86_64\"]", "\"architectures\": [\"arm64\", \"ppc\"]", "\"arm64\" or \"x86_64\"")]
    [InlineData("\"verify\": [\"version\"]", "\"verify\": [\"launch\"]", "not a known check")]
    [InlineData("\"sign\": \"before-app\"", "\"sign\": \"after-app\"", "not supported")]
    [InlineData("\"destination\": \"Contents/Resources/hcg-server-artwork.jpg\"", "\"destination\": \"Contents/Resources/../Info.plist\"", "under Contents/Resources")]
    [InlineData("[\"go >= 1.25\"]", "[\"go 1.25\"]", "tool name")]
    [InlineData("\"icon\": \"Resources/AppIcon.icns\"", "\"icon\": \"Resources/AppIcon.png\"", ".icns")]
    public void Contracts_HCG_cannot_honour_are_rejected_with_the_reason(string from, string to, string reason)
    {
        var root = Workspace();
        Assert.Contains(from, HcgServerContract);
        var ex = Assert.Throws<SwiftPackagingContractReader.ContractException>(() => Parse(HcgServerContract.Replace(from, to), root));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void A_top_level_description_is_accepted_as_text_only()
    {
        var root = Workspace();
        Assert.Equal("HCGServer", Parse(HcgServerContract, root).Product); // the real file has one
        var json = "{ \"schema\": \"hcg-app-packaging/1\", \"product\": \"HCGServer\", \"description\": 7 }";
        Assert.Contains("description must be a string", Assert.Throws<SwiftPackagingContractReader.ContractException>(() => Parse(json, root)).Message);
    }

    [Fact]
    public void Invalid_JSON_and_oversized_files_are_reported_not_ignored()
    {
        var root = Workspace();
        _t.Write("ws/macos/HCGServerApp/AppPackaging.json", "{ \"schema\": ");
        Assert.Contains("not valid JSON", SwiftPackagingContractReader.Read(root, "macos/HCGServerApp").Error);
        _t.Write("ws/macos/HCGServerApp/AppPackaging.json", new string(' ', SwiftPackagingContractReader.MaxBytes + 1));
        Assert.Contains("larger than", SwiftPackagingContractReader.Read(root, "macos/HCGServerApp").Error);
    }

    // ───────────── Confirmation and validation ─────────────

    private static BuildSettings Settings(SwiftPackagingContract? contract, bool confirmed = true, SwiftAppKind kind = SwiftAppKind.GuiApp,
        TargetArchitecture arch = TargetArchitecture.Universal, string version = "0.1.0", params PackagingFormat[] formats) => new()
    {
        ProjectRelativePath = "macos/HCGServerApp/Package.swift", Platform = TargetPlatform.MacOS, Architecture = arch, OutputDirectory = "/tmp/o",
        Identity = new AppIdentity { ApplicationName = "HCG Server", Version = version, ApplicationId = "com.hcg.server.app" },
        PackagingFormats = formats.Length == 0 ? [PackagingFormat.MacAppBundle] : formats,
        Swift = new SwiftBuildOptions
        {
            PackageDirectory = "macos/HCGServerApp", Product = "HCGServer", Kind = kind, MinimumMacOS = "13.0",
            PackagingContract = contract, PackagingContractConfirmed = confirmed,
        },
    };

    [Fact]
    public void Prebuild_commands_need_explicit_confirmation_and_unsupported_combinations_are_refused()
    {
        var c = Parse(HcgServerContract, Workspace());
        SwiftContractPlanner.Validate(Settings(c));
        SwiftContractPlanner.Validate(Settings(null, confirmed: false));
        Assert.Equal(SwiftContractPlanner.DeclinedMessage, Assert.Throws<BuildValidationException>(() => SwiftContractPlanner.Validate(Settings(c, confirmed: false))).Message);
        Assert.Contains("Deliver HCGServer as a macOS app", Assert.Throws<BuildValidationException>(() => SwiftContractPlanner.Validate(Settings(c, kind: SwiftAppKind.CommandLine))).Message);
        Assert.Contains("Mac App Store", Assert.Throws<BuildValidationException>(() => SwiftContractPlanner.Validate(Settings(c, formats: [PackagingFormat.MacAppBundle, PackagingFormat.MacAppStore]))).Message);

        var armOnly = c with { Helpers = [c.Helpers[0] with { Architectures = ["arm64"] }] };
        SwiftContractPlanner.Validate(Settings(armOnly, arch: TargetArchitecture.Arm64));
        Assert.Contains("only promises hcg-agent for arm64", Assert.Throws<BuildValidationException>(() => SwiftContractPlanner.Validate(Settings(armOnly))).Message);
    }

    [Fact]
    public void A_contract_changed_after_confirmation_stops_the_build()
    {
        var root = Workspace();
        var (c, _) = SwiftPackagingContractReader.Read(root, "macos/HCGServerApp");
        var swift = Settings(c).Swift!;
        Assert.NotNull(SwiftContractPlanner.Reload(root, swift).Contract);

        _t.Write("ws/macos/HCGServerApp/AppPackaging.json", HcgServerContract.Replace("--output", "--out"));
        var (changed, error) = SwiftContractPlanner.Reload(root, swift);
        Assert.Null(changed);
        Assert.Contains("changed after its commands were confirmed", error);

        File.Delete(_t.Combine("ws", "macos", "HCGServerApp", "AppPackaging.json"));
        Assert.Contains("was removed", SwiftContractPlanner.Reload(root, swift).Error);
    }

    // ───────────── Tools and prebuild planning ─────────────

    private sealed class Locator(Dictionary<string, string> tools) : IExecutableLocator
    {
        public string? Find(string name, IEnumerable<string>? extraCandidates = null) => tools.GetValueOrDefault(name);
    }

    [Fact]
    public async Task Required_tools_are_located_and_their_versions_checked()
    {
        var c = Parse(HcgServerContract, Workspace());
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        Task<(int, string)> Go(string output) => Task.FromResult((0, output));

        var (_, missing) = await SwiftContractPlanner.ResolveToolsAsync(c, new Locator([]), (_, _) => Go(""), env, CancellationToken.None);
        Assert.Contains("go is required", missing);

        var (_, old) = await SwiftContractPlanner.ResolveToolsAsync(c, new Locator(new() { ["go"] = "/opt/go/bin/go" }),
            (_, _) => Go("go version go1.22.5 darwin/arm64"), env, CancellationToken.None);
        Assert.Contains("needs go >= 1.25", old);
        Assert.Contains("1.22.5", old);

        var calls = new List<IReadOnlyList<string>>();
        var (tools, error) = await SwiftContractPlanner.ResolveToolsAsync(c, new Locator(new() { ["go"] = "/opt/go/bin/go" }),
            (spec, _) => { calls.Add(spec.Arguments); return Go("go version go1.25.4 darwin/arm64"); }, env, CancellationToken.None);
        Assert.Null(error);
        Assert.Equal(new SwiftContractPlanner.ResolvedTool("go", "/opt/go/bin/go", "1.25.4"), Assert.Single(tools));
        Assert.Equal(["version"], Assert.Single(calls));
    }

    [UnixFact]
    public void Prebuild_steps_are_argument_vectors_with_the_tools_on_PATH()
    {
        var root = Workspace();
        var c = Parse(HcgServerContract, root);
        var tools = new[] { new SwiftContractPlanner.ResolvedTool("go", "/opt/go/bin/go", "1.25.4") };
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/Users/test" };

        // The script must exist and be executable.
        Assert.Contains("does not exist", Assert.Throws<BuildValidationException>(() => SwiftContractPlanner.PlanPrebuild(c, root, "/tmp/helpers", tools, env, new Locator([]))).Message);
        var script = _t.Write("ws/scripts/build-agent-helper.sh", "#!/bin/sh\nexit 0\n");
        Assert.Contains("chmod +x", Assert.Throws<BuildValidationException>(() => SwiftContractPlanner.PlanPrebuild(c, root, "/tmp/helpers", tools, env, new Locator([]))).Message);
        FileOps.MarkExecutable(script);

        var step = Assert.Single(SwiftContractPlanner.PlanPrebuild(c, root, "/tmp/hcg build/helpers", tools, env, new Locator([])));
        Assert.Equal(BuildStage.Preparing, step.Stage);
        Assert.Equal("Prebuild: Build the host agent (Go) as a universal helper", step.Title);
        Assert.Equal(Path.GetFullPath(script), step.Process.Executable);
        Assert.Equal(["--output", "/tmp/hcg build/helpers/hcg-agent"], step.Process.Arguments);
        Assert.Equal(Path.GetFullPath(root), step.Process.WorkingDirectory);
        Assert.Equal("/opt/go/bin:/usr/bin:/bin", step.Process.Environment!["PATH"]);
        Assert.Equal("/Users/test", step.Process.Environment["HOME"]);
    }

    // ───────────── Staging ─────────────

    [UnixFact]
    public void Staging_mirrors_bundle_paths_and_names_the_step_when_a_helper_is_missing()
    {
        var root = Workspace();
        var c = Parse(HcgServerContract, root);
        var helpers = _t.Combine("build", "helpers");
        Directory.CreateDirectory(helpers);
        var publish = _t.Combine("build", "publish");
        Directory.CreateDirectory(publish);
        _t.Write("ws/macos/HCGServerApp/Resources/Artwork/hcg-server-artwork.jpg", "jpg");
        _t.Write("ws/macos/HCGServerApp/Resources/AppIcon.icns", "icns");
        _t.Write("ws/macos/HCGServerApp/Resources/Info.plist", InfoPlist.Create([("CFBundleExecutable", "HCGServer")]));

        var ex = Assert.Throws<BuildValidationException>(() => SwiftContractPlanner.Stage(c, root, helpers, publish));
        Assert.Contains("“Build the host agent (Go) as a universal helper” finished, but the helper hcg-agent", ex.Message);

        File.WriteAllBytes(Path.Combine(helpers, "hcg-agent"), SwiftTests.FatMachO(0x000D0000));
        var notes = SwiftContractPlanner.Stage(c, root, helpers, publish);
        var staged = Path.Combine(publish, SwiftContractLayout.StagingFolder);
        var agent = Path.Combine(staged, "Contents", "Helpers", "hcg-agent");
        Assert.True(File.Exists(agent));
        Assert.True((File.GetUnixFileMode(agent) & UnixFileMode.UserExecute) != 0);
        Assert.True(File.Exists(Path.Combine(staged, "Contents", "Resources", "hcg-server-artwork.jpg")));
        Assert.True(File.Exists(Path.Combine(staged, "Contents", "Resources", "AppIcon.icns")));
        Assert.True(File.Exists(Path.Combine(staged, SwiftContractLayout.InfoPlistTemplateFile)));
        Assert.Contains("Helper hcg-agent → Contents/Helpers/hcg-agent", notes);
        Assert.False(Directory.EnumerateDirectories(publish, "*.bundle").Any(), "the staging folder is not a SwiftPM resource bundle");
    }

    // ───────────── Info.plist template and verification ─────────────

    [Fact]
    public void The_Info_plist_template_keeps_its_own_keys_and_takes_HCGs_values()
    {
        var template = _t.Write("Info.plist", """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleExecutable</key><string>HCGServer</string>
              <!-- set by the build -->
              <key>CFBundleShortVersionString</key><string>0.0.0</string>
              <key>NSPrincipalClass</key><string>NSApplication</string>
              <key>NSSupportsSuddenTermination</key><false/>
              <key>NSBonjourServices</key>
              <array>
                <string>_hcg-agent._tcp</string>
              </array>
            </dict>
            </plist>
            """);
        var merged = _t.Write("merged.plist", InfoPlist.Merge(template, [("CFBundleShortVersionString", "0.1.0"), ("CFBundleIdentifier", "com.hcg.server.app"), ("NSHighResolutionCapable", true)]));
        var read = InfoPlist.Read(merged);
        Assert.Equal("0.1.0", read["CFBundleShortVersionString"]);
        Assert.Equal("com.hcg.server.app", read["CFBundleIdentifier"]);
        Assert.Equal("NSApplication", read["NSPrincipalClass"]);
        Assert.Equal("false", read["NSSupportsSuddenTermination"]);
        Assert.Equal("true", read["NSHighResolutionCapable"]);
        var text = File.ReadAllText(merged);
        Assert.Contains("<string>_hcg-agent._tcp</string>", text);
        Assert.DoesNotContain("set by the build", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "<key>CFBundleShortVersionString</key>"));
    }

    [UnixFact]
    public void Helpers_are_verified_for_presence_permission_slices_and_location()
    {
        var c = Parse(HcgServerContract, Workspace());
        var app = _t.Combine("HCG Server.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        _t.Write("HCG Server.app/Contents/Resources/hcg-server-artwork.jpg", "jpg");
        IReadOnlyList<VerificationCheck> Check() => SwiftBundleVerifier.VerifyContractStructure(app, c, TargetArchitecture.Universal);

        Assert.Contains(Check(), x => x.Name == "Helper hcg-agent" && x.Status == VerificationStatus.Failed && x.Detail.Contains("is missing"));

        var helper = Path.Combine(app, "Contents", "Helpers", "hcg-agent");
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.WriteAllBytes(helper, SwiftTests.ThinMachO(0x0100000C, 0x000D0000));
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var failed = Assert.Single(Check(), x => x.Name == "Helper hcg-agent");
        Assert.Equal(VerificationStatus.Failed, failed.Status);
        Assert.Contains("not executable", failed.Detail);
        Assert.Contains("no x86_64 slice", failed.Detail);

        File.WriteAllBytes(helper, SwiftTests.FatMachO(0x000D0000));
        FileOps.MarkExecutable(helper);
        Assert.All(Check(), x => Assert.Equal(VerificationStatus.Passed, x.Status));

        File.WriteAllBytes(Path.Combine(app, "Contents", "MacOS", "hcg-agent"), [0]);
        Assert.Contains(Check(), x => x.Name == "Helper location" && x.Status == VerificationStatus.Failed);
    }

    // ───────────── Real build (macOS with Xcode or the Command Line Tools) ─────────────

    private sealed class Observer(ITestOutputHelper output) : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) => output.WriteLine($"== {stage}: {description}");
        public void OnLog(BuildLogLine line) => output.WriteLine($"[{line.Kind}] {line.Text}");
    }

    /// <summary>
    /// A Swift package whose AppPackaging.json compiles a universal C helper in a prebuild step. HCG must run that
    /// step, place the helper in Contents/Helpers, sign it before the app, and run its "version" check.
    /// </summary>
    [MacToolsFact]
    public async Task Builds_an_app_whose_helper_comes_from_a_prebuild_command()
    {
        var paths = new AppPaths(_t.Combine("app"));
        var db = new SqliteDatabase(paths);
        var runner = new ProcessRunner();
        var toolchains = new ToolchainService(new ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System));
        var project = new ProjectRecord { Id = Guid.NewGuid(), Name = "Probe", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/orig", WorkspacePath = _t.Combine("ws-real"), ImportedAt = DateTimeOffset.UtcNow };
        new SqliteProjectRepository(db).UpsertAsync(project).GetAwaiter().GetResult();
        var src = project.SourceDirectory;
        void W(string rel, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(src, rel))!); File.WriteAllText(Path.Combine(src, rel), text); }
        W("Package.swift", "// swift-tools-version:5.9\nimport PackageDescription\nlet package = Package(name: \"Probe\", platforms: [.macOS(.v13)],\n  products: [.executable(name: \"Probe\", targets: [\"Probe\"])], targets: [.executableTarget(name: \"Probe\")])\n");
        W("Sources/Probe/main.swift", "print(\"probe\")\n");
        W("helper/probe-helper.c", "#include <stdio.h>\nint main(int c, char **v) { printf(\"probe-helper 2.3.4 (test)\\n\"); return 0; }\n");
        W("scripts/make-helper.sh", "#!/bin/sh\nset -e\nmkdir -p \"$(dirname \"$2\")\"\nexec clang -arch arm64 -arch x86_64 -mmacosx-version-min=13.0 -o \"$2\" helper/probe-helper.c\n");
        FileOps.MarkExecutable(Path.Combine(src, "scripts", "make-helper.sh"));
        W("Resources/notes.txt", "bundled\n");
        W("Resources/Info.plist", InfoPlist.Create([("CFBundleExecutable", "Probe"), ("NSPrincipalClass", "NSApplication"), ("CFBundleShortVersionString", "0.0.0")]));
        W("AppPackaging.json", """
            { "schema": "hcg-app-packaging/1", "product": "Probe",
              "bundle": { "displayName": "Probe", "identifier": "com.example.probe", "infoPlist": "Resources/Info.plist" },
              "prebuild": [ { "description": "Compile the probe helper", "requires": ["clang"], "command": ["scripts/make-helper.sh", "--output", "${HELPERS_DIR}/probe-helper"] } ],
              "helpers": [ { "name": "probe-helper", "source": "${HELPERS_DIR}/probe-helper", "destination": "Contents/Helpers/probe-helper",
                             "architectures": ["arm64", "x86_64"], "verify": ["version"] } ],
              "resources": [ { "source": "Resources/notes.txt", "destination": "Contents/Resources/notes.txt" } ] }
            """);
        var (contract, error) = SwiftPackagingContractReader.Read(src, "");
        Assert.Null(error);

        var build = new BuildService(new LocalProcessBuildWorker(runner), toolchains, new SqliteBuildRepository(db),
            new PackagingRegistry([new SwiftAppBundlePackager(runner), new SwiftAppZipPackager(runner)]), paths, TimeProvider.System);
        var settings = new BuildSettings
        {
            ProjectRelativePath = "Package.swift", Platform = TargetPlatform.MacOS, Architecture = TargetArchitecture.Universal,
            OutputDirectory = _t.Combine("out"), Timeout = TimeSpan.FromMinutes(20),
            Identity = new AppIdentity { ApplicationName = "Probe", Version = "2.3.4", ApplicationId = "com.example.probe" },
            PackagingFormats = [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip],
            Swift = new SwiftBuildOptions { PackageDirectory = "", Product = "Probe", Kind = SwiftAppKind.GuiApp, MinimumMacOS = "13.0", PackagingContract = contract, PackagingContractConfirmed = true },
        };
        var record = await build.RunAsync(new BuildRequest { Project = project, Settings = settings, TrustAcknowledged = true }, new Observer(output), CancellationToken.None);
        Assert.True(record.Status == BuildStatus.Succeeded, record.FailureReason);
        var bundle = Assert.Single(record.Artifacts, a => a.Format == PackagingFormat.MacAppBundle).Path;
        Assert.Equal(["Probe"], Directory.EnumerateFileSystemEntries(Path.Combine(bundle, "Contents", "MacOS")).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(bundle, "Contents", "Helpers", "probe-helper")));
        Assert.Equal("bundled\n", File.ReadAllText(Path.Combine(bundle, "Contents", "Resources", "notes.txt")));
        Assert.Equal("NSApplication", InfoPlist.Read(Path.Combine(bundle, "Contents", "Info.plist"))["NSPrincipalClass"]);
        var checks = record.Artifacts.SelectMany(a => a.Checks).ToList();
        Assert.Contains(checks, c => c.Name == "Helper probe-helper" && c.Status == VerificationStatus.Passed);
        Assert.Contains(checks, c => c.Name == "Helper probe-helper signature" && c.Status == VerificationStatus.Passed);
        Assert.Contains(checks, c => c.Name == "Helper probe-helper version" && c.Status == VerificationStatus.Passed && c.Detail.StartsWith("probe-helper 2.3.4", StringComparison.Ordinal));
        Assert.DoesNotContain(checks, c => c.Status == VerificationStatus.Failed);

        // The same build with a different app version fails loudly at the version check.
        var mismatch = await build.RunAsync(new BuildRequest { Project = project, Settings = settings with { Identity = settings.Identity with { Version = "1.0.0" } }, TrustAcknowledged = true, AllowOverwriteOutput = true },
            new Observer(output), CancellationToken.None);
        // A failed verification removes the output and records the failed checks as build errors.
        Assert.Equal(BuildStatus.Failed, mismatch.Status);
        Assert.Empty(mismatch.Artifacts);
        Assert.Contains(mismatch.Messages, m => m.Severity == DiagnosticSeverity.Error && m.Text.Contains("Helper probe-helper version") && m.Text.Contains("Set the app version to 2.3.4"));
    }
}

/// <summary>Runs only on a Mac with swift, clang and codesign (Xcode or the Command Line Tools).</summary>
public sealed class MacToolsFactAttribute : FactAttribute
{
    public MacToolsFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS.";
        else if (new[] { "/usr/bin/swift", "/usr/bin/clang", "/usr/bin/codesign" }.Any(p => !File.Exists(p))) Skip = "Requires Xcode or the Command Line Tools.";
        else if (Environment.GetEnvironmentVariable("HCG_SKIP_INTEGRATION") == "1") Skip = "HCG_SKIP_INTEGRATION=1";
    }
}
