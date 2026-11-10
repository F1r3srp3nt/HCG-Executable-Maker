using System.Text.Json;
using HCG.ExecutableMaker.BuildEngine.Electron;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Node;

namespace HCG.ExecutableMaker.Tests;

/// <summary>"Prepare desktop app": Vite + React web project → separate Electron workspace (static analysis, plan, apply).</summary>
public class WebDesktopConversionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private string Fixture()
    {
        var dir = _t.Combine("web");
        CopyDir(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "WebViteReact"), dir);
        File.WriteAllText(Path.Combine(dir, ".env.local"), "VITE_SUPABASE_URL=https://real.example\n"); // a real value: never copied
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "react"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "react", "index.js"), "");
        Directory.CreateDirectory(Path.Combine(dir, "dist"));
        File.WriteAllText(Path.Combine(dir, "dist", "index.html"), "<old build>");
        return dir;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
    }

    private static NodeProjectInfo Project(string dir) => Assert.Single(NodeProjectScanner.Scan(SourceIndex.Build(dir)).Projects);

    [Fact]
    public void A_vite_react_web_app_gets_a_plan_with_precise_findings()
    {
        var dir = Fixture();
        var p = Project(dir);
        Assert.Equal(NodeProjectKind.WebApp, p.Kind);
        var plan = WebDesktopConversion.Plan(dir, p);
        Assert.True(plan.CanApply, string.Join(" | ", plan.Blockers.Select(b => b.Message)));
        Assert.Equal("Fixture Web Shop", plan.Options.ProductName); // from index.html <title>
        Assert.Equal("app.desktop.fixture-web-shop", plan.Options.AppId);
        Assert.Equal("build", plan.FrontendBuildScript);

        var api = Assert.Single(plan.Findings, f => f.Area == "Backend" && f.Message.Contains("/api/orders"));
        Assert.Equal(ConversionFindingLevel.Warning, api.Level);
        Assert.Equal("src/main.tsx:8", api.Location);
        Assert.Contains(plan.Findings, f => f.Message.Contains("server.proxy") && f.Location == "vite.config.ts:9");
        Assert.Contains(plan.Findings, f => f.Area == "Asset paths" && f.Message.Contains("loads app://bundle/."));
        Assert.Equal("/", plan.BasePath);
        var auth = Assert.Single(plan.Findings, f => f.Area == "Sign-in");
        Assert.Contains("Supabase Auth", auth.Message);
        Assert.Contains("signInWithOAuth (src/main.tsx:13)", auth.Message);
        Assert.Contains(plan.Findings, f => f.Area == "Configuration" && f.Message.Contains(".env.local") && f.Level == ConversionFindingLevel.Warning);
        Assert.Contains(plan.Findings, f => f.Area == "Configuration" && f.Message.Contains("VITE_SUPABASE_ANON_KEY, VITE_SUPABASE_URL"));
        Assert.Contains(plan.Findings, f => f.Area == "Routing" && f.Message.Contains("BrowserRouter"));
        Assert.Equal(["hcg-desktop/main.cjs", "hcg-desktop/verify-build.cjs", "electron-builder.yml", "package.json", "package-lock.json"], plan.Changes.Select(c => c.RelativePath));
    }

    [Fact]
    public void The_generated_shell_serves_the_build_from_the_app_and_keeps_the_page_isolated()
    {
        var dir = Fixture();
        var plan = WebDesktopConversion.Plan(dir, Project(dir));
        var main = plan.Changes.Single(c => c.RelativePath == "hcg-desktop/main.cjs").After;
        foreach (var setting in new[] { "contextIsolation: true", "sandbox: true", "nodeIntegration: false", "webSecurity: true", "nodeIntegrationInWorker: false" })
            Assert.Contains(setting, main);
        Assert.Contains("win.loadURL(ORIGIN + BASE)", main);
        Assert.Contains("const ROOT = path.join(__dirname, '..', \"dist\")", main);
        Assert.Contains("path.extname(p) === '' ? path.join(ROOT, 'index.html')", main); // history routes
        Assert.Contains("callback(false)", main); // permission requests denied
        Assert.DoesNotContain("localhost", main);
        Assert.DoesNotContain("5173", main);
        Assert.DoesNotContain("preload", main);

        var builder = plan.Changes.Single(c => c.RelativePath == "electron-builder.yml").After;
        Assert.Contains("appId: \"app.desktop.fixture-web-shop\"", builder);
        Assert.Contains("  - \"dist/**/*\"", builder);
        Assert.Contains("    - dmg", builder);

        using var pkg = JsonDocument.Parse(plan.Changes.Single(c => c.RelativePath == "package.json").After);
        var r = pkg.RootElement;
        Assert.Equal("hcg-desktop/main.cjs", r.GetProperty("main").GetString());
        Assert.Equal("npm run build && node hcg-desktop/verify-build.cjs dist", r.GetProperty("scripts").GetProperty("desktop:build").GetString());
        Assert.Equal("npm run desktop:build && electron-builder --mac zip dmg --arm64", r.GetProperty("scripts").GetProperty("desktop:dist:mac").GetString());
        Assert.Equal("npm run desktop:build && electron-builder --linux dir", r.GetProperty("scripts").GetProperty("desktop:dist:linux").GetString());
        Assert.False(r.TryGetProperty("dependencies", out _)); // bundled by Vite: the packaged app ships no node_modules
        var dev = r.GetProperty("devDependencies");
        Assert.Equal(WebDesktopConversion.ElectronVersion, dev.GetProperty("electron").GetString());
        Assert.Equal(WebDesktopConversion.ElectronBuilderVersion, dev.GetProperty("electron-builder").GetString());
        Assert.Equal("^19.0.0", dev.GetProperty("react").GetString());
        Assert.Equal("vite", r.GetProperty("scripts").GetProperty("dev").GetString()); // the project's own scripts are kept
    }

    [Theory]
    [InlineData("next", "Next.js renders pages on a server")]
    [InlineData("pnpm", "uses pnpm")]
    [InlineData("builder-config", "electron-builder.yml already exists")]
    [InlineData("no-build", "No package.json script runs \"vite build\"")]
    [InlineData("cdn-base", "sets base to \"https://cdn.example.com/\"")]
    [InlineData("vue", "This project has Vite and no React")]
    [InlineData("server-api", "the project's own backend would have to run separately")]
    [InlineData("local-dep", "a package outside this project")]
    public void Unsupported_projects_are_refused_with_the_reason(string variant, string reason)
    {
        var dir = Fixture();
        var pkgPath = Path.Combine(dir, "package.json");
        var pkg = File.ReadAllText(pkgPath);
        switch (variant)
        {
            case "next": pkg = pkg.Replace("\"react\": \"^19.0.0\"", "\"next\": \"^15.0.0\", \"react\": \"^19.0.0\""); break;
            case "pnpm": File.WriteAllText(Path.Combine(dir, "pnpm-lock.yaml"), "lockfileVersion: '9.0'\n"); break;
            case "builder-config": File.WriteAllText(Path.Combine(dir, "electron-builder.yml"), "appId: x\n"); break;
            case "no-build": pkg = pkg.Replace("\"build\": \"tsc -b && vite build\",", ""); break;
            case "cdn-base": File.WriteAllText(Path.Combine(dir, "vite.config.ts"), File.ReadAllText(Path.Combine(dir, "vite.config.ts")).Replace("base: \"/\"", "base: \"https://cdn.example.com/\"")); break;
            case "vue": pkg = pkg.Replace("\"react\": \"^19.0.0\",", "\"vue\": \"^3.5.0\",").Replace("\"react-dom\": \"^19.0.0\",", ""); break;
            case "server-api": pkg = pkg.Replace("\"react\": \"^19.0.0\"", "\"express\": \"^5.0.0\", \"react\": \"^19.0.0\""); break;
            case "local-dep": pkg = pkg.Replace("\"react\": \"^19.0.0\"", "\"shared-ui\": \"file:../shared-ui\", \"react\": \"^19.0.0\""); break;
        }
        File.WriteAllText(pkgPath, pkg);
        var plan = WebDesktopConversion.Plan(dir, Project(dir));
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Blockers, b => b.Message.Contains(reason));
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void An_existing_electron_app_is_built_as_it_is_and_not_converted()
    {
        var dir = Fixture();
        File.WriteAllText(Path.Combine(dir, "package.json"), File.ReadAllText(Path.Combine(dir, "package.json")).Replace("\"vite\": \"^7.0.0\"", "\"vite\": \"^7.0.0\", \"electron\": \"45.2.3\""));
        var p = Project(dir);
        Assert.Equal(NodeProjectKind.ElectronDesktop, p.Kind);
        Assert.Contains(WebDesktopConversion.Plan(dir, p).Blockers, b => b.Message.Contains("already an Electron app"));
    }

    [Fact]
    public void Invalid_identity_is_refused()
    {
        var dir = Fixture();
        var plan = WebDesktopConversion.Plan(dir, Project(dir), new DesktopConversionOptions("not an id", "Shop"));
        Assert.Contains(plan.Blockers, b => b.Area == "Identity");
    }

    private sealed class LockfileRunner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            File.WriteAllText(Path.Combine(spec.WorkingDirectory!, "package-lock.json"), "{\"lockfileVersion\":3,\"packages\":{}}");
            onOutput?.Invoke(OutputStream.StdOut, "up to date, audited 0 packages");
            return Task.FromResult(new ProcessResult(0, TimeSpan.Zero, false, false));
        }
    }

    [Fact]
    public async Task Applying_writes_a_separate_workspace_that_is_a_buildable_electron_project_and_leaves_the_source_alone()
    {
        var dir = Fixture();
        var before = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
        var plan = WebDesktopConversion.Plan(dir, Project(dir));
        var dest = _t.Combine("desktop");
        var runner = new LockfileRunner();
        var node = new NodeInstallation("/usr/bin/node", "22.12.0", "PATH", "/usr/lib/node_modules/npm/bin/npm-cli.js", "10.9.0");
        var result = await WebDesktopConversion.ApplyAsync(plan, dir, dest, node, runner, new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, null, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        // The imported source is unchanged.
        Assert.Equal(before.Keys.Order(), Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order());
        Assert.All(before, kv => Assert.Equal(kv.Value, File.ReadAllText(kv.Key)));

        // The workspace: project files + generated files; no node_modules, old build or real .env values.
        Assert.True(File.Exists(Path.Combine(dest, "src", "main.tsx")));
        Assert.True(File.Exists(Path.Combine(dest, ".env.example")));
        Assert.False(File.Exists(Path.Combine(dest, ".env.local")));
        Assert.False(Directory.Exists(Path.Combine(dest, "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(dest, "dist")));
        Assert.True(File.Exists(Path.Combine(dest, "hcg-desktop", "main.cjs")));

        // Lockfile updated without running any script.
        var npm = Assert.Single(runner.Calls);
        Assert.Equal(["/usr/lib/node_modules/npm/bin/npm-cli.js", "install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund"], npm.Arguments);
        Assert.Equal(dest, npm.WorkingDirectory);

        // HCG now recognizes it as a ready Electron app with the desktop packaging script.
        var electron = Project(dest);
        Assert.Equal(NodeProjectKind.ElectronDesktop, electron.Kind);
        Assert.Empty(electron.MissingForDesktop);
        Assert.Equal("hcg-desktop/main.cjs", electron.MainEntry);
        Assert.True(electron.MainEntryExists);
        Assert.Equal(ElectronPackagerKind.ElectronBuilder, electron.Packager);
        Assert.Equal("app.desktop.fixture-web-shop", electron.BuilderConfig!.AppId);
        Assert.Equal(["desktop:dist:linux", "desktop:dist:mac"], electron.PackagingScripts.Select(s => s.Name).Order());
        foreach (var platform in new[] { "mac", "linux" })
        {
            // Whatever host builds it, the chosen script runs the production build before electron-builder.
            var script = ElectronBuildDefaults.ChooseScript(electron, platform)!;
            Assert.Null(script.Problem);
            Assert.Equal("desktop:build", Assert.Single(script.CompileSteps).NpmScript);
        }
    }

    [Fact]
    public async Task Applying_refuses_a_non_empty_destination_and_a_plan_with_blockers()
    {
        var dir = Fixture();
        var node = new NodeInstallation("/usr/bin/node", "22.12.0", "PATH", "/npm-cli.js", "10.9.0");
        var dest = _t.Combine("taken");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "x"), "");
        var ok = WebDesktopConversion.Plan(dir, Project(dir));
        Assert.False((await WebDesktopConversion.ApplyAsync(ok, dir, dest, node, new LockfileRunner(), new Dictionary<string, string>(), null, CancellationToken.None)).Success);
        var blocked = WebDesktopConversion.Plan(dir, Project(dir), new DesktopConversionOptions("bad id", "x"));
        Assert.Contains("blockers", (await WebDesktopConversion.ApplyAsync(blocked, dir, _t.Combine("other"), node, new LockfileRunner(), new Dictionary<string, string>(), null, CancellationToken.None)).Error);
    }
}

/// <summary>Runs only when HCG_WEB_PROJECT points at a Vite + React web project (e.g. the HCG Store's web-hcg-store).</summary>
public sealed class WebProjectFactAttribute : FactAttribute
{
    public WebProjectFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HCG_WEB_PROJECT") is not { Length: > 0 } dir || !Directory.Exists(dir))
            Skip = "Set HCG_WEB_PROJECT to a Vite + React project (and HCG_WEB_DESKTOP_OUT to an empty folder) to prepare a real desktop workspace with npm.";
    }
}

public class WebDesktopConversionRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>REAL npm (lockfile only, no scripts): the web project becomes a desktop workspace HCG recognizes as Electron.</summary>
    [WebProjectFact]
    public async Task A_real_web_project_becomes_a_desktop_workspace()
    {
        var source = Path.GetFullPath(Environment.GetEnvironmentVariable("HCG_WEB_PROJECT")!);
        var dest = Path.GetFullPath(Environment.GetEnvironmentVariable("HCG_WEB_DESKTOP_OUT") is { Length: > 0 } o ? o : Path.Combine(Path.GetTempPath(), "hcg-desktop-" + Guid.NewGuid().ToString("N")[..8]));
        var project = Assert.Single(NodeProjectScanner.Scan(SourceIndex.Build(source)).Projects);
        var plan = WebDesktopConversion.Plan(source, project);
        output.WriteLine($"Plan for {plan.ProjectName}: app id {plan.Options.AppId}, build script {plan.FrontendBuildScript}, output {plan.OutputDirectory}/, base {plan.BasePath}");
        foreach (var f in plan.Findings) output.WriteLine($"[{f.Level}] {f.Area}: {f.Message}{(f.Location.Length > 0 ? " (" + f.Location + ")" : "")}");
        foreach (var c in plan.Changes) output.WriteLine($"{c.Kind} {c.RelativePath}: {c.Reason}");
        Assert.True(plan.CanApply);
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "hcg-web-desktop-" + Guid.NewGuid().ToString("N")[..8]));
        var report = await new HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector(new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner(),
            new ExecutableLocator(), paths, TimeProvider.System).DetectAsync(CancellationToken.None);
        var (node, detail) = NodeCapabilities.SelectNode(report, project.EnginesNode);
        Assert.True(node is not null, detail);
        var env = HCG.ExecutableMaker.Security.BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment;
        var result = await WebDesktopConversion.ApplyAsync(plan, source, dest, node!, new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner(), env, output.WriteLine, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        var electron = Assert.Single(NodeProjectScanner.Scan(SourceIndex.Build(dest)).Projects);
        Assert.Equal(NodeProjectKind.ElectronDesktop, electron.Kind);
        Assert.Empty(electron.MissingForDesktop);
        output.WriteLine("Desktop workspace: " + dest);
        if (Environment.GetEnvironmentVariable("HCG_WEB_DESKTOP_RESULT") is { Length: > 0 } file) File.WriteAllText(file, dest);
    }
}
