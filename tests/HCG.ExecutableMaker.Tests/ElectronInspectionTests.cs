using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.Node;

namespace HCG.ExecutableMaker.Tests;

public class ElectronInspectionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string FixtureDir(string name) => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "ElectronConfigs", name);

    private static ProjectInspectionService Service() => new(
        new IProjectInspector[] { new DotNetProjectInspector(TimeProvider.System) }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    private static ToolchainReport Report(TargetPlatform platform, params NodeInstallation[] nodes) => new(
        new HostEnvironment(platform, TargetArchitecture.Arm64, "test", ".NET", "test"),
        [], null, null, DateTimeOffset.UtcNow) { NodeInstallations = nodes };

    private static NodeInstallation Node(string version, string npm = "10.9.2") =>
        new($"/opt/node-{version}/bin/node", version, "PATH", $"/opt/node-{version}/lib/node_modules/npm/bin/npm-cli.js", npm);

    /// <summary>The Presentation Studio desktop conversion (Oslo-Sprint), with its real package.json and electron-builder.yml.</summary>
    private void WritePresentationStudioDesktop(string dir = "")
    {
        var prefix = dir.Length == 0 ? "" : dir + "/";
        _t.Write(prefix + "package.json", File.ReadAllText(Path.Combine(FixtureDir("presentation-studio-desktop"), "package.json")));
        _t.Write(prefix + "electron-builder.yml", File.ReadAllText(Path.Combine(FixtureDir("presentation-studio-desktop"), "electron-builder.yml")));
        _t.Write(prefix + "package-lock.json", "{}");
        _t.Write(prefix + "scripts/build-desktop.mjs", "// build");
        _t.Write(prefix + "desktop/main.ts", "// main");
        _t.Write(prefix + "index.html", "<!doctype html>");
        _t.Write(prefix + "vite.config.ts", "export default {}");
    }

    [Fact]
    public async Task Presentation_Studio_desktop_is_an_electron_builder_app_ready_on_a_mac()
    {
        WritePresentationStudioDesktop();
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.MacOS, Node("22.14.0")), CancellationToken.None);
        Assert.Equal("node", a.InspectorId);
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.Equal("Electron", a.Framework);
        var p = Assert.Single(a.Node!.Projects);
        Assert.Equal(NodeProjectKind.ElectronDesktop, p.Kind);
        Assert.Equal("^45.2.3", p.ElectronVersion);
        Assert.Equal(ElectronPackagerKind.ElectronBuilder, p.Packager);
        Assert.Equal("desktop-dist/main.mjs", p.MainEntry);
        Assert.False(p.MainEntryExists);
        Assert.True(p.MainEntryBuilt); // produced by desktop:build, checked after compilation
        Assert.Equal("package-lock.json", p.LockFile);
        Assert.Equal(">=22.12", p.EnginesNode);
        var c = p.BuilderConfig!;
        Assert.Equal("electron-builder.yml", c.Source);
        Assert.Equal("com.oslosprint.presentationstudio", c.AppId);
        Assert.Equal("Presentation Studio", c.ProductName);
        Assert.Equal("release", c.OutputDirectory);
        Assert.Equal(["zip", "dmg"], c.MacTargets);
        Assert.Equal(["arm64"], c.MacArchs);
        Assert.Equal("-", c.MacIdentity);
        Assert.Equal(["dir"], c.LinuxTargets);
        Assert.Equal("presentation-studio", c.LinuxExecutableName);
        Assert.Equal("desktop-dist/main.mjs", c.ExtraMetadataMain);
        Assert.Empty(p.MissingForDesktop);

        var mac = p.PackagingScripts.Single(s => s.Name == "desktop:dist:mac");
        Assert.Null(mac.Problem);
        Assert.Equal(["mac"], mac.Platforms);
        Assert.Equal(["arm64"], mac.Archs);
        Assert.Equal("desktop:build", Assert.Single(mac.CompileSteps).NpmScript);
        var pack = p.PackagingScripts.Single(s => s.Name == "desktop:pack");
        Assert.Equal(["dir"], pack.Targets);
        Assert.DoesNotContain(p.PackagingScripts, s => s.Name == "desktop:dist:mac-on-linux"); // a bash script, not an electron-builder call

        var project = Assert.Single(a.Projects);
        Assert.True(project.IsRunnableApplication);
        Assert.Equal([TargetPlatform.MacOS], project.CompatiblePlatforms);
        Assert.Equal("package.json", a.PrimaryProjectPath);
        Assert.Contains(PackagingFormat.MacAppBundle, a.RecommendedFormats);
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("No npm command, project script or JavaScript configuration was run"));
    }

    [Fact]
    public async Task Presentation_Studio_web_version_explains_exactly_what_is_missing()
    {
        _t.Write("package.json", File.ReadAllText(Path.Combine(FixtureDir("presentation-studio-web"), "package.json")));
        _t.Write("index.html", "<!doctype html>");
        _t.Write("vite.config.ts", "export default {}");
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.MacOS, Node("22.14.0")), CancellationToken.None);
        Assert.Equal("node", a.InspectorId);
        Assert.Equal(SupportLevel.RequiresConfiguration, a.Support);
        var p = Assert.Single(a.Node!.Projects);
        Assert.Equal(NodeProjectKind.WebApp, p.Kind);
        Assert.Contains("Vite", p.Frameworks);
        Assert.Contains("Express", p.Frameworks);
        Assert.Contains(p.MissingForDesktop, m => m.Contains("No Electron dependency"));
        Assert.Contains(p.MissingForDesktop, m => m.Contains("main-process entry"));
        Assert.Contains(p.MissingForDesktop, m => m.Contains("electron-builder"));
        Assert.Contains(p.MissingForDesktop, m => m.Contains("development server"));
        Assert.Contains(p.MissingForDesktop, m => m.Contains("backend server (Express)"));
        Assert.False(Assert.Single(a.Projects).IsRunnableApplication);
        Assert.DoesNotContain("planned for a later milestone", a.SupportSummary);
    }

    [Fact]
    public void Plain_service_and_library_are_not_desktop_apps()
    {
        _t.Write("svc/package.json", """{"name":"svc","main":"index.js","dependencies":{"fastify":"^5"},"scripts":{"start":"node index.js"}}""");
        _t.Write("lib/package.json", """{"name":"lib","version":"1.0.0"}""");
        var node = NodeProjectScanner.Scan(SourceIndex.Build(_t.Path));
        Assert.Equal(NodeProjectKind.NodeService, node.Projects.Single(p => p.Name == "svc").Kind);
        Assert.Equal(NodeProjectKind.Library, node.Projects.Single(p => p.Name == "lib").Kind);
        Assert.All(node.Projects, p => Assert.NotEmpty(p.MissingForDesktop));
    }

    [Fact]
    public async Task Nested_electron_app_in_a_repository_is_selectable_and_build_outputs_are_ignored()
    {
        WritePresentationStudioDesktop("apps/presentation-studio");
        _t.Write("tools/site/package.json", """{"name":"site","devDependencies":{"vite":"^8"}}""");
        // electron-builder output with an unpacked copy of the app's package.json must not be listed.
        _t.Write("apps/presentation-studio/release/linux-unpacked/resources/app/package.json", """{"name":"copy","devDependencies":{"electron":"45"}}""");
        _t.Write("apps/presentation-studio/dist/package.json", """{"name":"dist-copy"}""");
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.MacOS, Node("22.14.0")), CancellationToken.None);
        Assert.Equal("node", a.InspectorId);
        Assert.Equal(2, a.Node!.Projects.Count);
        var electron = a.Node.Projects.Single(p => p.Kind == NodeProjectKind.ElectronDesktop);
        Assert.Equal("apps/presentation-studio", electron.Directory);
        Assert.Equal("apps/presentation-studio/package.json", a.PrimaryProjectPath);
        Assert.Single(a.Projects, p => p.IsRunnableApplication);
    }

    [Fact]
    public async Task Electron_app_next_to_a_dotnet_solution_is_still_reported()
    {
        _t.Write("App.sln", "");
        _t.Write("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        WritePresentationStudioDesktop("desktop/studio");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal("dotnet", a.InspectorId);
        Assert.NotNull(a.Node);
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("Electron desktop app Presentation Studio found in desktop/studio/"));
    }

    [Fact]
    public void Forge_packager_and_pnpm_are_recognized_but_explained_as_not_implemented()
    {
        _t.Write("package.json", """{"name":"forge-app","main":"main.js","devDependencies":{"electron":"^45","@electron-forge/cli":"^7"},"packageManager":"pnpm@9.1.0"}""");
        _t.Write("main.js", "");
        _t.Write("pnpm-lock.yaml", "");
        var p = Assert.Single(NodeProjectScanner.Scan(SourceIndex.Build(_t.Path)).Projects);
        Assert.Equal(NodeProjectKind.ElectronDesktop, p.Kind);
        Assert.Equal(ElectronPackagerKind.ElectronForge, p.Packager);
        Assert.Equal(NodePackageManager.Pnpm, p.PackageManager);
        Assert.Contains(p.MissingForDesktop, m => m.Contains("Electron Forge") && m.Contains("runs only electron-builder"));
        Assert.Contains(p.MissingForDesktop, m => m.Contains("npm only"));
        Assert.Equal(SupportLevel.RequiresConfiguration, NodeCapabilities.Evaluate(p, Report(TargetPlatform.MacOS, Node("22.14.0"))).Support);
    }

    [Fact]
    public void Electron_without_main_or_packager_lists_each_gap()
    {
        _t.Write("package.json", """{"name":"bare","devDependencies":{"electron":"^45"}}""");
        var p = Assert.Single(NodeProjectScanner.Scan(SourceIndex.Build(_t.Path)).Projects);
        Assert.Contains(p.MissingForDesktop, m => m.Contains("no \"main\" field"));
        Assert.Contains(p.MissingForDesktop, m => m.Contains("No supported packaging configuration"));
    }

    [Fact]
    public void Missing_main_entry_without_a_compile_step_is_a_gap()
    {
        _t.Write("package.json", """{"name":"x","main":"out/main.js","devDependencies":{"electron":"^45","electron-builder":"^26"},"scripts":{"dist":"electron-builder --mac"}}""");
        var p = Assert.Single(NodeProjectScanner.Scan(SourceIndex.Build(_t.Path)).Projects);
        Assert.Contains(p.MissingForDesktop, m => m.Contains("out/main.js does not exist"));
    }

    [Fact]
    public void Engines_are_validated_against_installed_node_and_npm()
    {
        WritePresentationStudioDesktop();
        var p = Assert.Single(NodeProjectScanner.Scan(SourceIndex.Build(_t.Path)).Projects);
        var tooOld = NodeCapabilities.Evaluate(p, Report(TargetPlatform.MacOS, Node("20.18.0")));
        Assert.Equal(SupportLevel.MissingToolchain, tooOld.Support);
        Assert.Contains(tooOld.Remediation, r => r.Contains("requires Node.js >=22.12") && r.Contains("20.18.0"));
        // Several installations: the newest matching one is used (not just the first on PATH).
        var (chosen, _) = NodeCapabilities.SelectNode(Report(TargetPlatform.MacOS, Node("20.18.0"), Node("22.14.0")), ">=22.12");
        Assert.Equal("22.14.0", chosen!.Version);
        var none = NodeCapabilities.Evaluate(p, Report(TargetPlatform.MacOS));
        Assert.Contains(none.Remediation, r => r.Contains("Node.js was not found"));
    }

    [Fact]
    public void Only_implemented_host_combinations_are_offered()
    {
        WritePresentationStudioDesktop();
        var p = Assert.Single(NodeProjectScanner.Scan(SourceIndex.Build(_t.Path)).Projects);
        Assert.Equal(SupportLevel.RequiresDifferentHost, NodeCapabilities.Evaluate(p, Report(TargetPlatform.Windows, Node("22.14.0"))).Support);
        var mac = NodeCapabilities.Combinations(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "", "", ""));
        Assert.All(mac, c => Assert.Equal(TargetPlatform.MacOS, c.Platform));
        Assert.Contains(mac, c => c.Architecture == TargetArchitecture.Arm64 && c.Formats.SequenceEqual([PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg]));
        var linux = Assert.Single(NodeCapabilities.Combinations(new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "", "", "")));
        Assert.Equal([PackagingFormat.PortableFolder], linux.Formats);
    }

    [Theory]
    [InlineData("22.14.0", ">=22.12", NodeVersionRange.Result.Satisfied)]
    [InlineData("22.11.0", ">=22.12", NodeVersionRange.Result.NotSatisfied)]
    [InlineData("20.11.1", "^20.11.0", NodeVersionRange.Result.Satisfied)]
    [InlineData("21.0.0", "^20.11.0", NodeVersionRange.Result.NotSatisfied)]
    [InlineData("18.20.0", "18.x || 20.x", NodeVersionRange.Result.Satisfied)]
    [InlineData("19.0.0", "18.x || 20.x", NodeVersionRange.Result.NotSatisfied)]
    [InlineData("20.5.0", ">=18 <21", NodeVersionRange.Result.Satisfied)]
    [InlineData("21.0.0", ">=18 <21", NodeVersionRange.Result.NotSatisfied)]
    [InlineData("20.10.9", "~20.10", NodeVersionRange.Result.Satisfied)]
    [InlineData("20.11.0", "~20.10", NodeVersionRange.Result.NotSatisfied)]
    [InlineData("22.0.0", "20.11.0 - 22", NodeVersionRange.Result.Satisfied)]
    [InlineData("23.0.0", "20.11.0 - 22", NodeVersionRange.Result.NotSatisfied)]
    [InlineData("v22.12.0", ">= 22.12.0", NodeVersionRange.Result.Satisfied)]
    [InlineData("22.0.0", "*", NodeVersionRange.Result.Satisfied)]
    [InlineData("22.0.0", "lts/*", NodeVersionRange.Result.Unreadable)]
    public void Node_version_ranges(string version, string range, NodeVersionRange.Result expected) =>
        Assert.Equal(expected, NodeVersionRange.Check(version, range));

    [Fact]
    public void Yaml_subset_reads_electron_builder_shapes()
    {
        var tree = (Dictionary<string, object?>)MiniYaml.Parse("""
            # comment
            appId: com.example.app   # trailing comment
            productName: "My App"
            directories:
              output: out/release
            files:
              - dist/**
              - "!**/node_modules/**"
            mac:
              target:
                - target: zip
                  arch: [arm64, x64]
                - dmg
              identity: null
              extendInfo:
                Key: 'It''s here: yes'
            linux: { target: [dir], executableName: my-app }
            notes: |
              line one
              line two
            """)!;
        var c = ElectronBuilderConfigReader.FromTree("test", tree);
        Assert.Equal("com.example.app", c.AppId);
        Assert.Equal("My App", c.ProductName);
        Assert.Equal("out/release", c.OutputDirectory);
        Assert.Equal(["zip", "dmg"], c.MacTargets);
        Assert.Equal(["arm64", "x64"], c.MacArchs);
        Assert.Equal("null", c.MacIdentity); // explicitly disabled signing is distinguished from "not set"
        Assert.Equal(["dir"], c.LinuxTargets);
        Assert.Equal("my-app", c.LinuxExecutableName);
        var mac = (Dictionary<string, object?>)tree["mac"]!;
        Assert.Equal("It's here: yes", ((Dictionary<string, object?>)mac["extendInfo"]!)["Key"]);
        Assert.Equal("line one\nline two", tree["notes"]);
        Assert.Throws<FormatException>(() => MiniYaml.Parse("base: &b\n  x: 1\nother: *b"));
    }

    [Fact]
    public void Javascript_config_is_recognized_but_not_executed_and_package_json_build_is_read()
    {
        _t.Write("a/package.json", """{"name":"a","main":"main.js","devDependencies":{"electron":"^45","electron-builder":"^26"}}""");
        _t.Write("a/main.js", "");
        _t.Write("a/electron-builder.config.js", "throw new Error('must not run')");
        _t.Write("b/package.json", """{"name":"b","main":"main.js","devDependencies":{"electron":"^45","electron-builder":"^26"},"build":{"appId":"com.b","directories":{"output":"pkg"},"mac":{"target":"dmg"}}}""");
        _t.Write("b/main.js", "");
        var node = NodeProjectScanner.Scan(SourceIndex.Build(_t.Path));
        var a = node.Projects.Single(p => p.Name == "a");
        Assert.True(a.BuilderConfig!.IsScript);
        Assert.Contains(a.Notes, n => n.Contains("is a script"));
        Assert.Empty(a.MissingForDesktop);
        var b = node.Projects.Single(p => p.Name == "b").BuilderConfig!;
        Assert.Equal("package.json \"build\"", b.Source);
        Assert.Equal("pkg", b.OutputDirectory);
        Assert.Equal(["dmg"], b.MacTargets);
    }

    [Fact]
    public void Packaging_scripts_split_into_shell_free_compile_steps()
    {
        var scripts = new Dictionary<string, string>
        {
            ["build"] = "vite build",
            ["compile"] = "tsc -p tsconfig.main.json",
            ["dist"] = "npm run build && npm run compile && electron-builder --mac --arm64 --config builder.yml",
            ["direct"] = "node scripts/build.mjs --prod && npx electron-builder -m",
            ["piped"] = "NODE_ENV=production vite build && electron-builder",
            ["after"] = "electron-builder --dir && node notify.js",
        };
        var dist = ElectronPackagingScripts.Parse("dist", scripts["dist"], scripts);
        Assert.Null(dist.Problem);
        Assert.Equal(["build", "compile"], dist.CompileSteps.Select(s => s.NpmScript));
        Assert.Equal("builder.yml", dist.ConfigPath);
        var direct = ElectronPackagingScripts.Parse("direct", scripts["direct"], scripts);
        Assert.Equal(["scripts/build.mjs", "--prod"], Assert.Single(direct.CompileSteps).NodeArguments);
        Assert.Equal(["mac"], direct.Platforms);
        Assert.Contains("without a shell", ElectronPackagingScripts.Parse("piped", scripts["piped"], scripts).Problem);
        Assert.Contains("after electron-builder", ElectronPackagingScripts.Parse("after", scripts["after"], scripts).Problem);
    }
}
