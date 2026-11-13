using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static NodeInstallation Node22 => new("/usr/bin/node", "22.14.0", "PATH", "/usr/lib/node_modules/npm/bin/npm-cli.js", "10.9.2");

    private static void Edit(string file, string from, string to)
    {
        var text = File.ReadAllText(file);
        Assert.Contains(from, text);
        File.WriteAllText(file, text.Replace(from, to));
    }

    /// <summary>A PNG header of the given size (enough for the icon checks, which read the header only).</summary>
    private static void WritePng(string path, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ihdr = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0, 0, 0, 0, 0 };
        File.WriteAllBytes(path, ihdr);
    }

    // ------------------------------------------------------------------ inspection

    [Fact]
    public void A_nested_vite_react_app_is_found_and_offered_for_conversion_instead_of_nothing_runnable()
    {
        // Like the HCG Store repository: no package.json at the root, the web app in web-hcg-store/.
        var root = _t.Combine("store");
        CopyDir(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "WebViteDesktop"), Path.Combine(root, "web-hcg-store"));
        File.WriteAllText(Path.Combine(root, "README.md"), "# Store\n");
        var index = SourceIndex.Build(root);
        var p = Assert.Single(NodeProjectScanner.Scan(index).Projects);
        Assert.Equal("web-hcg-store", p.Directory);
        Assert.Equal(NodeProjectKind.WebApp, p.Kind);
        Assert.Equal(DesktopConversionCategory.BrowserApp, p.DesktopEligibility!.Category);
        Assert.True(p.DesktopEligibility.CanConvert);

        var linux = new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux");
        var analysis = new NodeProjectInspector(TimeProvider.System).Inspect(index, new ToolchainReport(linux, [], null, null, DateTimeOffset.UtcNow));
        Assert.Contains("desktop conversion available", analysis.ApplicationType);
        Assert.Contains("Create desktop application", analysis.SupportSummary);
        Assert.Empty(analysis.MissingDependencies); // no hand-made Electron checklist for a convertible app
        Assert.Equal([PackagingFormat.PortableFolder], analysis.RecommendedFormats);
        var detected = Assert.Single(analysis.Projects);
        Assert.True(detected.IsDesktopConversionCandidate);
        Assert.False(detected.IsRunnableApplication);
        Assert.Equal("Browser app — can become a desktop app", detected.ApplicationType);
    }

    [Fact]
    public void Outputs_are_not_recommended_when_nothing_can_produce_them()
    {
        var dir = Fixture();
        Edit(Path.Combine(dir, "package.json"), "\"react\": \"^19.0.0\"", "\"next\": \"^15.0.0\", \"react\": \"^19.0.0\"");
        var linux = new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux");
        var analysis = new NodeProjectInspector(TimeProvider.System).Inspect(SourceIndex.Build(dir), new ToolchainReport(linux, [], null, null, DateTimeOffset.UtcNow));
        Assert.Empty(analysis.RecommendedFormats);
        Assert.Equal("Needs a server — additional setup required", analysis.ApplicationType);
        Assert.False(Assert.Single(analysis.Projects).IsDesktopConversionCandidate);
    }

    [Theory]
    [InlineData("plain", DesktopConversionCategory.BrowserApp, "Vite + React browser app")]
    [InlineData("next", DesktopConversionCategory.ServerDependent, "Next.js")]
    [InlineData("react-router-framework", DesktopConversionCategory.ServerDependent, "React Router framework mode")]
    [InlineData("express-runtime", DesktopConversionCategory.ServerDependent, "ships a backend (Express")]
    [InlineData("express-dev", DesktopConversionCategory.BrowserApp, "Vite + React")]
    [InlineData("vue", DesktopConversionCategory.Unsupported, "This project has Vite and no React")]
    [InlineData("pnpm", DesktopConversionCategory.Unsupported, "uses pnpm")]
    [InlineData("no-build", DesktopConversionCategory.Unsupported, "No package.json script runs \"vite build\"")]
    [InlineData("monorepo-root", DesktopConversionCategory.Unsupported, "this is a monorepo root")]
    [InlineData("local-dep", DesktopConversionCategory.Unsupported, "a package outside this folder")]
    [InlineData("electron-builder", DesktopConversionCategory.ExistingElectron, "is an Electron app")]
    [InlineData("electron-no-packaging", DesktopConversionCategory.ElectronWithoutPackaging, "keeps your main process")]
    public void Projects_are_classified_from_their_files(string variant, DesktopConversionCategory expected, string reason)
    {
        var dir = Fixture();
        var pkg = Path.Combine(dir, "package.json");
        switch (variant)
        {
            case "next": Edit(pkg, "\"react\": \"^19.0.0\"", "\"next\": \"^15.0.0\", \"react\": \"^19.0.0\""); break;
            case "react-router-framework": Edit(pkg, "\"typescript\"", "\"@react-router/dev\": \"^7.0.0\", \"typescript\""); break;
            case "express-runtime": Edit(pkg, "\"react\": \"^19.0.0\"", "\"express\": \"^5.0.0\", \"react\": \"^19.0.0\""); break;
            case "express-dev": Edit(pkg, "\"typescript\"", "\"express\": \"^5.0.0\", \"typescript\""); break;
            case "vue": Edit(pkg, "\"react\": \"^19.0.0\",", "\"vue\": \"^3.5.0\","); Edit(pkg, "\"react-dom\": \"^19.0.0\",", ""); break;
            case "pnpm": File.WriteAllText(Path.Combine(dir, "pnpm-lock.yaml"), "lockfileVersion: '9.0'\n"); break;
            case "no-build": Edit(pkg, "\"build\": \"tsc -b && vite build\",", ""); break;
            case "monorepo-root": Edit(pkg, "\"type\": \"module\",", "\"type\": \"module\", \"workspaces\": [\"packages/*\"],"); break;
            case "local-dep": Edit(pkg, "\"react\": \"^19.0.0\"", "\"shared-ui\": \"file:../shared-ui\", \"react\": \"^19.0.0\""); break;
            case "electron-builder":
                Edit(pkg, "\"vite\": \"^7.0.0\"", "\"vite\": \"^7.0.0\", \"electron\": \"45.3.0\", \"electron-builder\": \"26.18.0\"");
                Edit(pkg, "\"type\": \"module\",", "\"type\": \"module\", \"main\": \"electron/main.cjs\",");
                _t.Write("web/electron/main.cjs", "require('electron');");
                File.WriteAllText(Path.Combine(dir, "electron-builder.yml"), "appId: com.example.shop\n");
                break;
            case "electron-no-packaging":
                Edit(pkg, "\"vite\": \"^7.0.0\"", "\"vite\": \"^7.0.0\", \"electron\": \"45.3.0\"");
                Edit(pkg, "\"type\": \"module\",", "\"type\": \"module\", \"main\": \"electron/main.cjs\",");
                _t.Write("web/electron/main.cjs", "const { BrowserWindow } = require('electron');\nnew BrowserWindow({ webPreferences: { nodeIntegration: true } }).loadFile('dist/index.html');\n");
                break;
        }
        var e = Project(dir).DesktopEligibility!;
        Assert.Equal(expected, e.Category);
        Assert.Contains(reason, e.Summary + " " + string.Join(" ", e.Reasons));
    }

    [Fact]
    public void A_member_of_an_npm_workspace_is_eligible_and_its_lockfile_starts_from_the_monorepo_versions()
    {
        var root = _t.Combine("mono");
        _t.Write("mono/package.json", """{ "name": "mono", "private": true, "workspaces": ["apps/*", "packages/*"] }""");
        _t.Write("mono/package-lock.json", """
            { "name": "mono", "lockfileVersion": 3, "requires": true, "packages": {
              "": { "name": "mono", "workspaces": ["apps/*"] },
              "apps/web": { "name": "web", "version": "1.0.0" },
              "node_modules/web": { "resolved": "apps/web", "link": true },
              "node_modules/react": { "version": "19.1.10", "resolved": "https://registry.npmjs.org/react/-/react-19.1.10.tgz", "integrity": "sha512-a" },
              "node_modules/vite": { "version": "7.3.8", "dev": true, "engines": { "node": "^20.19.0 || >=22.12.0" } },
              "apps/web/node_modules/react-dom": { "version": "19.1.10", "integrity": "sha512-b" },
              "apps/admin/node_modules/lodash": { "version": "4.17.21" }
            } }
            """);
        CopyDir(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "WebViteDesktop"), Path.Combine(root, "apps", "web"));
        var p = NodeProjectScanner.Scan(SourceIndex.Build(root)).Projects.Single(x => x.Directory == "apps/web");
        Assert.Equal("", p.WorkspaceRoot);
        Assert.Equal("package-lock.json", p.WorkspaceLockFile);
        Assert.Equal(DesktopConversionCategory.BrowserApp, p.DesktopEligibility!.Category);
        var plan = WebDesktopConversion.Plan(root, p);
        Assert.True(plan.CanApply, string.Join(" | ", plan.Blockers.Select(b => b.Message)));
        Assert.Equal("package-lock.json", plan.SeedLockFile);

        var seeded = JsonNode.Parse(WebDesktopConversion.SeedLockfile(File.ReadAllText(Path.Combine(root, "package-lock.json")), "apps/web",
            plan.Changes.Single(c => c.RelativePath == "package.json").After))!["packages"]!.AsObject();
        Assert.Equal("fixture-field-notes", seeded[""]!["name"]!.GetValue<string>());
        Assert.Equal("19.1.10", seeded["node_modules/react"]!["version"]!.GetValue<string>());   // hoisted pin kept
        Assert.Equal("sha512-b", seeded["node_modules/react-dom"]!["integrity"]!.GetValue<string>()); // the member's own nested pin
        Assert.False(seeded.ContainsKey("node_modules/web"));     // workspace links dropped
        Assert.False(seeded.ContainsKey("node_modules/lodash"));  // other members' packages dropped
        Assert.False(seeded.ContainsKey("apps/web"));
    }

    [Fact]
    public void A_member_that_depends_on_a_sibling_package_or_a_pnpm_workspace_is_not_converted()
    {
        var root = _t.Combine("mono2");
        _t.Write("mono2/pnpm-lock.yaml", "lockfileVersion: '9.0'\n");
        _t.Write("mono2/pnpm-workspace.yaml", "packages:\n  - 'apps/*'\n  - 'packages/*'\n");
        _t.Write("mono2/packages/ui/package.json", """{ "name": "@mono/ui", "version": "1.0.0" }""");
        CopyDir(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "WebViteDesktop"), Path.Combine(root, "apps", "web"));
        Edit(Path.Combine(root, "apps", "web", "package.json"), "\"react\": \"19.1.10\",", "\"@mono/ui\": \"1.0.0\", \"react\": \"19.1.10\",");
        var p = NodeProjectScanner.Scan(SourceIndex.Build(root)).Projects.Single(x => x.Directory == "apps/web");
        Assert.Equal(NodePackageManager.Pnpm, p.PackageManager);
        Assert.Equal(DesktopConversionCategory.Unsupported, p.DesktopEligibility!.Category);
        Assert.Contains(p.DesktopEligibility.Reasons, r => r.Contains("uses pnpm"));
        Assert.Contains(p.DesktopEligibility.Reasons, r => r.Contains("@mono/ui is another package of this monorepo"));
    }

    // ------------------------------------------------------------------ plan

    [Fact]
    public void A_vite_react_web_app_gets_a_plan_with_precise_findings()
    {
        var dir = Fixture();
        var p = Project(dir);
        Assert.Equal(NodeProjectKind.WebApp, p.Kind);
        var plan = WebDesktopConversion.Plan(dir, p);
        Assert.True(plan.CanApply, string.Join(" | ", plan.Blockers.Select(b => b.Message)));
        Assert.Equal(DesktopConversionMode.WrapBrowserApp, plan.Mode);
        Assert.Equal("Fixture Web Shop", plan.Options.ProductName); // from index.html <title>
        Assert.Equal("app.desktop.fixture-web-shop", plan.Options.AppId);
        Assert.Equal("build", plan.FrontendBuildScript);

        var api = Assert.Single(plan.Findings, f => f.Area == "Backend" && f.Message.Contains("/api/orders"));
        Assert.Equal(ConversionFindingLevel.Warning, api.Level);
        Assert.Equal("src/main.tsx:8", api.Location);
        Assert.Contains(plan.Findings, f => f.Message.Contains("server.proxy") && f.Location == "vite.config.ts:9");
        Assert.Contains(plan.Findings, f => f.Area == "Loading" && f.Message.Contains("loads app://bundle/,") && f.Message.Contains("deep links and reloads"));
        Assert.Equal("/", plan.BasePath);
        var auth = Assert.Single(plan.Findings, f => f.Area == "Sign-in");
        Assert.Contains("Supabase Auth", auth.Message);
        Assert.Contains("signInWithOAuth (src/main.tsx:13)", auth.Message);
        // .env.local sets a public VITE_ value the production build would embed: it is used (not the file itself).
        Assert.Equal(["VITE_SUPABASE_URL"], plan.PublicEnvironment);
        Assert.Contains(plan.Findings, f => f.Area == "Configuration" && f.Message.Contains("VITE_SUPABASE_ANON_KEY, VITE_SUPABASE_URL"));
        Assert.Contains(plan.Findings, f => f.Area == "Routing" && f.Message.Contains("BrowserRouter"));
        Assert.Contains(plan.Findings, f => f.Area == "Signing" && f.Message.Contains("NOT signed for distribution and NOT notarized"));
        Assert.Contains(plan.Findings, f => f.Area == "Icon" && f.Message.Contains("Electron's default icon"));
        Assert.Equal(["hcg-desktop/main.cjs", "hcg-desktop/verify-build.cjs", "electron-builder.yml", ".env.production.local", "package.json", "package-lock.json", "hcg-desktop/README.md", "hcg-desktop/conversion.json"],
            plan.Changes.Select(c => c.RelativePath));
        var env = plan.Changes.Single(c => c.RelativePath == ".env.production.local");
        Assert.Contains("VITE_SUPABASE_URL=\"https://real.example\"", env.After);
        Assert.DoesNotContain("real.example", env.Preview); // values are not displayed in the preview
    }

    [Fact]
    public void Credential_like_settings_and_server_variables_never_reach_the_desktop_build()
    {
        var dir = Fixture();
        File.WriteAllText(Path.Combine(dir, ".env.production"), "VITE_API_URL=https://api.example.com # comment\nVITE_STRIPE_SECRET_KEY=sk_live_x\nVITE_SERVICE_ROLE=abc\nDATABASE_URL=postgres://u:p@h/db\nexport VITE_TITLE='Shop'\n");
        var plan = WebDesktopConversion.Plan(dir, Project(dir));
        Assert.Equal(["VITE_API_URL", "VITE_SUPABASE_URL", "VITE_TITLE"], plan.PublicEnvironment);
        Assert.Equal(["VITE_SERVICE_ROLE", "VITE_STRIPE_SECRET_KEY"], plan.OmittedEnvironment);
        var env = plan.Changes.Single(c => c.RelativePath == ".env.production.local").After;
        Assert.Contains("VITE_API_URL=\"https://api.example.com\"", env);
        Assert.Contains("VITE_TITLE=\"Shop\"", env);
        Assert.DoesNotContain("sk_live", env);
        Assert.DoesNotContain("DATABASE_URL", env);
        Assert.Contains(plan.Findings, f => f.Level == ConversionFindingLevel.Warning && f.Message.Contains("VITE_SERVICE_ROLE, VITE_STRIPE_SECRET_KEY"));

        var off = WebDesktopConversion.Plan(dir, Project(dir), WebDesktopConversion.DefaultOptions(Project(dir), dir) with { UsePublicEnvironment = false });
        Assert.DoesNotContain(off.Changes, c => c.RelativePath == ".env.production.local");
    }

    [Fact]
    public void The_generated_shell_serves_the_build_from_the_app_and_keeps_the_page_isolated()
    {
        var dir = Fixture();
        var plan = WebDesktopConversion.Plan(dir, Project(dir));
        var main = plan.Changes.Single(c => c.RelativePath == "hcg-desktop/main.cjs").After;
        foreach (var setting in new[] { "contextIsolation: true", "sandbox: true", "nodeIntegration: false", "webSecurity: true", "nodeIntegrationInWorker: false", "nodeIntegrationInSubFrames: false" })
            Assert.Contains(setting, main);
        Assert.Contains("win.loadURL(ORIGIN + BASE)", main);
        Assert.Contains("const ROOT = path.join(__dirname, '..', \"dist\")", main);
        Assert.Contains("path.extname(p) === '' ? path.join(ROOT, 'index.html')", main); // history routes and reloads
        Assert.Contains("callback(false)", main);                                       // permission requests denied
        Assert.Contains("setPermissionCheckHandler(() => false)", main);
        Assert.Contains("headers.set('Content-Security-Policy', CSP)", main);
        Assert.Contains("script-src 'self'", main);
        Assert.Contains("contents.on('will-redirect'", main);
        Assert.Contains("u.protocol === 'https:' || u.protocol === 'mailto:'", main);  // only https/mailto leave the app
        Assert.Contains("requestSingleInstanceLock", main);
        Assert.Contains("did-fail-load", main);
        Assert.DoesNotContain("localhost", main);
        Assert.DoesNotContain("5173", main);
        Assert.DoesNotContain("preload:", main); // no preload script: the page gets no native API

        var builder = plan.Changes.Single(c => c.RelativePath == "electron-builder.yml").After;
        Assert.Contains("appId: \"app.desktop.fixture-web-shop\"", builder);
        Assert.Contains("  - \"dist/**/*\"", builder);
        Assert.Contains("    - target: dmg\n      arch: [arm64]", builder);
        Assert.Contains("identity: \"-\"", builder);
        Assert.Contains("publish: null", builder);

        using var pkg = JsonDocument.Parse(plan.Changes.Single(c => c.RelativePath == "package.json").After);
        var r = pkg.RootElement;
        Assert.Equal("hcg-desktop/main.cjs", r.GetProperty("main").GetString());
        Assert.Equal("npm run build && node hcg-desktop/verify-build.cjs dist /", r.GetProperty("scripts").GetProperty("desktop:build").GetString());
        Assert.Equal("npm run desktop:build && electron-builder --mac --publish never", r.GetProperty("scripts").GetProperty("desktop:dist:mac").GetString());
        Assert.Equal("npm run desktop:build && electron-builder --linux dir --publish never", r.GetProperty("scripts").GetProperty("desktop:dist:linux").GetString());
        Assert.False(r.TryGetProperty("dependencies", out _)); // bundled by Vite: the packaged app ships no node_modules
        var dev = r.GetProperty("devDependencies");
        Assert.Equal(WebDesktopConversion.ElectronVersion, dev.GetProperty("electron").GetString());
        Assert.Equal(WebDesktopConversion.ElectronBuilderVersion, dev.GetProperty("electron-builder").GetString());
        Assert.Equal("^19.0.0", dev.GetProperty("react").GetString());
        Assert.Equal("vite", r.GetProperty("scripts").GetProperty("dev").GetString()); // the project's own scripts are kept
        Assert.Equal("tsc -b && vite build", r.GetProperty("scripts").GetProperty("build").GetString());
    }

    [Fact]
    public void Icon_targets_and_signing_choices_are_written_to_the_configuration()
    {
        var dir = Fixture();
        WritePng(Path.Combine(dir, "public", "brand", "icon-512.png"), 512, 512);
        WritePng(Path.Combine(dir, "public", "brand", "favicon-32.png"), 32, 32);
        var defaults = WebDesktopConversion.DefaultOptions(Project(dir), dir);
        Assert.EndsWith(Path.Combine("brand", "icon-512.png"), defaults.IconPath);

        var plan = WebDesktopConversion.Plan(dir, Project(dir), defaults with { MacTargets = ["zip"], MacArchs = ["x64"], MacIdentity = "Example Ltd (ABCDE12345)" });
        Assert.True(plan.CanApply, string.Join(" | ", plan.Blockers.Select(b => b.Message)));
        var builder = plan.Changes.Single(c => c.RelativePath == "electron-builder.yml").After;
        Assert.Contains("    - target: zip\n      arch: [x64]", builder);
        Assert.DoesNotContain("target: dmg", builder);
        Assert.Contains("icon: \"hcg-desktop/icon.png\"", builder);
        Assert.Contains("identity: \"Example Ltd (ABCDE12345)\"\n  hardenedRuntime: true", builder);
        Assert.Contains("notarize: false", builder);
        var icon = plan.Changes.Single(c => c.RelativePath == "hcg-desktop/icon.png");
        Assert.Equal(defaults.IconPath, icon.CopyFrom);
        Assert.Contains("com.apple.security.cs.allow-jit", plan.Changes.Single(c => c.RelativePath == "hcg-desktop/entitlements.mac.plist").After);
        Assert.Contains(plan.Findings, f => f.Area == "Signing" && f.Message.Contains("Notarization is a separate step"));
    }

    [Theory]
    [InlineData(256, 256, "need at least 512×512")]
    [InlineData(1024, 512, "must be square")]
    public void Unusable_icons_are_refused_with_the_reason(int w, int h, string reason)
    {
        var dir = Fixture();
        var icon = _t.Combine($"icon-{w}x{h}.png");
        WritePng(icon, w, h);
        var plan = WebDesktopConversion.Plan(dir, Project(dir), WebDesktopConversion.DefaultOptions(Project(dir), dir) with { IconPath = icon });
        Assert.Contains(plan.Blockers, b => b.Area == "Icon" && b.Message.Contains(reason));
        Assert.Null(DesktopIcon.FindInProject(Path.GetDirectoryName(icon)!));
    }

    [Theory]
    [InlineData("next", "Next.js")]
    [InlineData("pnpm", "uses pnpm")]
    [InlineData("builder-config", "electron-builder.yml already exists")]
    [InlineData("no-build", "No package.json script runs \"vite build\"")]
    [InlineData("cdn-base", "sets base to \"https://cdn.example.com/\"")]
    [InlineData("vue", "This project has Vite and no React")]
    [InlineData("server-api", "ships a backend")]
    [InlineData("local-dep", "a package outside this folder")]
    public void Unsupported_projects_are_refused_with_the_reason(string variant, string reason)
    {
        var dir = Fixture();
        var pkgPath = Path.Combine(dir, "package.json");
        switch (variant)
        {
            case "next": Edit(pkgPath, "\"react\": \"^19.0.0\"", "\"next\": \"^15.0.0\", \"react\": \"^19.0.0\""); break;
            case "pnpm": File.WriteAllText(Path.Combine(dir, "pnpm-lock.yaml"), "lockfileVersion: '9.0'\n"); break;
            case "builder-config": File.WriteAllText(Path.Combine(dir, "electron-builder.yml"), "appId: x\n"); break;
            case "no-build": Edit(pkgPath, "\"build\": \"tsc -b && vite build\",", ""); break;
            case "cdn-base": Edit(Path.Combine(dir, "vite.config.ts"), "base: \"/\"", "base: \"https://cdn.example.com/\""); break;
            case "vue": Edit(pkgPath, "\"react\": \"^19.0.0\",", "\"vue\": \"^3.5.0\","); Edit(pkgPath, "\"react-dom\": \"^19.0.0\",", ""); break;
            case "server-api": Edit(pkgPath, "\"react\": \"^19.0.0\"", "\"express\": \"^5.0.0\", \"react\": \"^19.0.0\""); break;
            case "local-dep": Edit(pkgPath, "\"react\": \"^19.0.0\"", "\"shared-ui\": \"file:../shared-ui\", \"react\": \"^19.0.0\""); break;
        }
        var plan = WebDesktopConversion.Plan(dir, Project(dir));
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Blockers, b => b.Message.Contains(reason));
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void An_existing_electron_app_is_built_as_it_is_and_nothing_is_generated()
    {
        var dir = Fixture();
        Edit(Path.Combine(dir, "package.json"), "\"vite\": \"^7.0.0\"", "\"vite\": \"^7.0.0\", \"electron\": \"45.3.0\", \"electron-builder\": \"26.18.0\"");
        Edit(Path.Combine(dir, "package.json"), "\"type\": \"module\",", "\"type\": \"module\", \"main\": \"electron/main.cjs\",");
        _t.Write("web/electron/main.cjs", "require('electron');");
        File.WriteAllText(Path.Combine(dir, "electron-builder.yml"), "appId: com.example.shop\n");
        var p = Project(dir);
        Assert.Equal(NodeProjectKind.ElectronDesktop, p.Kind);
        Assert.Equal(DesktopConversionCategory.ExistingElectron, p.DesktopEligibility!.Category);
        var plan = WebDesktopConversion.Plan(dir, p);
        Assert.Contains(plan.Blockers, b => b.Message.Contains("is an Electron app"));
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void An_electron_app_without_packaging_keeps_its_main_process_and_gets_packaging_only()
    {
        var dir = Fixture();
        Edit(Path.Combine(dir, "package.json"), "\"vite\": \"^7.0.0\"", "\"vite\": \"^7.0.0\", \"electron\": \"45.3.0\"");
        Edit(Path.Combine(dir, "package.json"), "\"type\": \"module\",", "\"type\": \"module\", \"main\": \"electron/main.cjs\",");
        _t.Write("web/electron/main.cjs", "const { BrowserWindow } = require('electron');\nnew BrowserWindow({ webPreferences: { nodeIntegration: true } }).loadFile('dist/index.html');\n");
        var plan = WebDesktopConversion.Plan(dir, Project(dir));
        Assert.True(plan.CanApply, string.Join(" | ", plan.Blockers.Select(b => b.Message)));
        Assert.Equal(DesktopConversionMode.AddPackaging, plan.Mode);
        Assert.DoesNotContain(plan.Changes, c => c.RelativePath == "hcg-desktop/main.cjs");
        Assert.Contains(plan.Findings, f => f.Area == "Main process" && f.Level == ConversionFindingLevel.Warning && f.Message.Contains("nodeIntegration: true") && f.Location == "electron/main.cjs:2");
        using var pkg = JsonDocument.Parse(plan.Changes.Single(c => c.RelativePath == "package.json").After);
        Assert.Equal("electron/main.cjs", pkg.RootElement.GetProperty("main").GetString());          // kept
        Assert.Equal("^19.0.0", pkg.RootElement.GetProperty("dependencies").GetProperty("react").GetString()); // not moved
        Assert.Equal("45.3.0", pkg.RootElement.GetProperty("devDependencies").GetProperty("electron").GetString());
        Assert.DoesNotContain("files:", plan.Changes.Single(c => c.RelativePath == "electron-builder.yml").After);
    }

    [Fact]
    public void Invalid_identity_and_targets_are_refused()
    {
        var dir = Fixture();
        Assert.Contains(WebDesktopConversion.Plan(dir, Project(dir), new DesktopConversionOptions("not an id", "Shop")).Blockers, b => b.Area == "Identity");
        Assert.Contains(WebDesktopConversion.Plan(dir, Project(dir), new DesktopConversionOptions("com.example.shop", "Shop") { MacTargets = ["pkg"] }).Blockers, b => b.Area == "Targets");
        Assert.Contains(WebDesktopConversion.Plan(dir, Project(dir), new DesktopConversionOptions("com.example.shop", "Shop") { MacIdentity = "Developer ID Application: X" }).Blockers, b => b.Area == "Signing");
    }

    // ------------------------------------------------------------------ apply

    private sealed class LockfileRunner(string lockfile = "{\"lockfileVersion\":3,\"packages\":{}}", string output = "up to date, audited 0 packages") : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            Seeded = File.Exists(Path.Combine(spec.WorkingDirectory!, "package-lock.json")) ? File.ReadAllText(Path.Combine(spec.WorkingDirectory!, "package-lock.json")) : null;
            File.WriteAllText(Path.Combine(spec.WorkingDirectory!, "package-lock.json"), lockfile);
            onOutput?.Invoke(OutputStream.StdOut, output);
            return Task.FromResult(new ProcessResult(0, TimeSpan.Zero, false, false));
        }
        public string? Seeded { get; private set; }
    }

    private static Task<DesktopConversionResult> Apply(DesktopConversionPlan plan, string root, string dest, IProcessRunner? runner = null, NodeInstallation? node = null) =>
        WebDesktopConversion.ApplyAsync(plan, root, dest, node ?? Node22, runner ?? new LockfileRunner(), new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, null, CancellationToken.None);

    [Fact]
    public async Task Applying_writes_a_separate_desktop_project_that_is_a_buildable_electron_project_and_leaves_the_source_alone()
    {
        var dir = Fixture();
        var before = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
        var plan = WebDesktopConversion.Plan(dir, Project(dir), WebDesktopConversion.DefaultOptions(Project(dir), dir) with
        {
            Source = new DesktopConversionSource(Guid.NewGuid(), "Shop repo", "/src/shop", ""),
        });
        var dest = _t.Combine("desktop");
        var runner = new LockfileRunner();
        var result = await Apply(plan, dir, dest, runner);
        Assert.True(result.Success, result.Error);

        // The imported source is unchanged.
        Assert.Equal(before.Keys.Order(), Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order());
        Assert.All(before, kv => Assert.Equal(kv.Value, File.ReadAllText(kv.Key)));

        // The desktop project: project files + generated files; no node_modules, old build or .env files.
        Assert.True(File.Exists(Path.Combine(dest, "src", "main.tsx")));
        Assert.True(File.Exists(Path.Combine(dest, ".env.example")));
        Assert.False(File.Exists(Path.Combine(dest, ".env.local")));
        Assert.False(Directory.Exists(Path.Combine(dest, "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(dest, "dist")));
        Assert.True(File.Exists(Path.Combine(dest, "hcg-desktop", "main.cjs")));
        Assert.True(File.Exists(Path.Combine(dest, "hcg-desktop", "README.md")));

        // Lockfile updated without running any script.
        var npm = Assert.Single(runner.Calls);
        Assert.Equal(["/usr/lib/node_modules/npm/bin/npm-cli.js", "install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund"], npm.Arguments);
        Assert.Equal(dest, npm.WorkingDirectory);

        // The record lists every generated file with its hash, and the original package.json.
        var record = WebDesktopConversion.ReadRecord(Path.Combine(dest, "hcg-desktop", "conversion.json"), out var problem);
        Assert.NotNull(record);
        Assert.Null(problem);
        Assert.Equal("Shop repo", record.Source!.ProjectName);
        Assert.Equal(before[Path.Combine(dir, "package.json")], record.PackageJsonBefore);
        Assert.Equal(64, record.SourceFingerprint!.Length);
        foreach (var f in record.Files) Assert.Equal(WebDesktopConversion.Sha256File(Path.Combine(dest, f.Path)), f.Sha256);
        Assert.Contains(record.Files, f => f.Path == "package-lock.json");

        // HCG now recognizes it as a ready Electron app (generated by HCG) with the desktop packaging script.
        var electron = Project(dest);
        Assert.Equal(NodeProjectKind.ElectronDesktop, electron.Kind);
        Assert.Equal(DesktopConversionCategory.ExistingElectron, electron.DesktopEligibility!.Category);
        Assert.Equal("hcg-desktop/conversion.json", electron.HcgDesktopRecord);
        Assert.Empty(electron.MissingForDesktop);
        Assert.Equal("hcg-desktop/main.cjs", electron.MainEntry);
        Assert.True(electron.MainEntryExists);
        Assert.Equal(ElectronPackagerKind.ElectronBuilder, electron.Packager);
        Assert.Equal("app.desktop.fixture-web-shop", electron.BuilderConfig!.AppId);
        Assert.Equal("-", electron.BuilderConfig.MacIdentity);
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
    public async Task Converting_again_replaces_the_generated_files_instead_of_accumulating_them()
    {
        var dir = Fixture();
        var first = _t.Combine("first");
        var withId = WebDesktopConversion.DefaultOptions(Project(dir), dir) with { MacIdentity = "Example Ltd (ABCDE12345)" };
        Assert.True((await Apply(WebDesktopConversion.Plan(dir, Project(dir), withId), dir, first)).Success);
        Assert.True(File.Exists(Path.Combine(first, "hcg-desktop", "entitlements.mac.plist")));

        // The generated project (e.g. committed back into a repository) is converted again with other options.
        var again = WebDesktopConversion.Plan(first, Project(first), withId with { MacIdentity = null, ProductName = "Shop Desktop" });
        Assert.True(again.CanApply, string.Join(" | ", again.Blockers.Select(b => b.Message)));
        Assert.NotNull(again.PreviousRecord);
        Assert.Equal(DesktopConversionMode.WrapBrowserApp, again.Mode);
        Assert.Contains(again.Changes, c => c.RelativePath == "hcg-desktop/entitlements.mac.plist" && c.Kind == WorkspaceChangeKind.Remove);
        var second = _t.Combine("second");
        var r2 = await Apply(again, first, second);
        Assert.True(r2.Success, r2.Error);

        // package.json is rebuilt from the recorded original: no doubled scripts, no "main" left over, same dependencies.
        Assert.Equal(File.ReadAllText(Path.Combine(first, "package.json")), File.ReadAllText(Path.Combine(second, "package.json")));
        Assert.False(File.Exists(Path.Combine(second, "hcg-desktop", "entitlements.mac.plist")));
        Assert.Equal(Directory.GetFiles(Path.Combine(first, "hcg-desktop")).Length - 1, Directory.GetFiles(Path.Combine(second, "hcg-desktop")).Length);
        Assert.Contains("productName: \"Shop Desktop\"", File.ReadAllText(Path.Combine(second, "electron-builder.yml")));
        Assert.Contains("identity: \"-\"", File.ReadAllText(Path.Combine(second, "electron-builder.yml")));
        var record2 = WebDesktopConversion.ReadRecord(Path.Combine(second, "hcg-desktop", "conversion.json"), out _)!;
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "package.json")), record2.PackageJsonBefore);

        // A third conversion with the same options produces the same files (idempotent).
        var third = _t.Combine("third");
        Assert.True((await Apply(WebDesktopConversion.Plan(second, Project(second), withId with { MacIdentity = null, ProductName = "Shop Desktop" }), second, third)).Success);
        foreach (var f in new[] { "package.json", "electron-builder.yml", "hcg-desktop/main.cjs", "hcg-desktop/verify-build.cjs", "hcg-desktop/README.md", ".env.production.local" })
            Assert.Equal(File.ReadAllText(Path.Combine(second, f)), File.ReadAllText(Path.Combine(third, f)));
    }

    [Fact]
    public async Task A_generated_file_edited_afterwards_is_never_overwritten()
    {
        var dir = Fixture();
        var first = _t.Combine("first");
        Assert.True((await Apply(WebDesktopConversion.Plan(dir, Project(dir)), dir, first)).Success);
        File.AppendAllText(Path.Combine(first, "hcg-desktop", "main.cjs"), "\n// my change\n");
        var again = WebDesktopConversion.Plan(first, Project(first));
        Assert.False(again.CanApply);
        Assert.Contains(again.Blockers, b => b.Location == "hcg-desktop/main.cjs" && b.Message.Contains("does not overwrite your edits"));
        Assert.Empty(again.Changes);
    }

    [Fact]
    public async Task The_resolved_build_tools_must_accept_the_chosen_node_and_npm_output_is_redacted()
    {
        var dir = Fixture();
        var plan = WebDesktopConversion.Plan(dir, Project(dir));
        var tooOld = await Apply(plan, dir, _t.Combine("old"), node: Node22 with { Version = "20.18.0" });
        Assert.False(tooOld.Success);
        Assert.Contains("requires Node.js >=22.12.0", tooOld.Error);

        var lockfile = """{ "lockfileVersion": 3, "packages": { "node_modules/vite": { "version": "8.0.0", "engines": { "node": ">=24.0.0" } }, "node_modules/electron": { "version": "45.3.0", "engines": { "node": ">= 22.12.0" } } } }""";
        var log = new List<string>();
        var r = await WebDesktopConversion.ApplyAsync(plan, dir, _t.Combine("new"), Node22,
            new LockfileRunner(lockfile, "npm http fetch GET 200 https://user:s3cr3tpassw0rd@registry.example.com/react Authorization: Bearer abcdefghijklmnop1234567890"),
            new Dictionary<string, string>(), log.Add, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("vite 8.0.0 (needs >=24.0.0)", r.Error);
        Assert.DoesNotContain("electron", r.Error!.Split("satisfy")[1].Split('.')[0]);
        Assert.DoesNotContain(log, l => l.Contains("s3cr3tpassw0rd") || l.Contains("abcdefghijklmnop1234567890"));
        Assert.Contains(log, l => l.Contains("[REDACTED]"));
    }

    [Fact]
    public async Task A_monorepo_member_gets_a_lockfile_seeded_from_the_monorepo()
    {
        var root = _t.Combine("mono");
        _t.Write("mono/package.json", """{ "name": "mono", "private": true, "workspaces": ["apps/*"] }""");
        _t.Write("mono/package-lock.json", """{ "lockfileVersion": 3, "packages": { "": {}, "node_modules/react": { "version": "19.1.10" } } }""");
        CopyDir(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "WebViteDesktop"), Path.Combine(root, "apps", "web"));
        var p = NodeProjectScanner.Scan(SourceIndex.Build(root)).Projects.Single(x => x.Directory == "apps/web");
        var runner = new LockfileRunner();
        var r = await Apply(WebDesktopConversion.Plan(root, p), root, _t.Combine("desk"), runner);
        Assert.True(r.Success, r.Error);
        Assert.Contains("\"node_modules/react\"", runner.Seeded); // npm started from the monorepo's pins
    }

    [Fact]
    public async Task Applying_refuses_a_non_empty_destination_and_a_plan_with_blockers()
    {
        var dir = Fixture();
        var dest = _t.Combine("taken");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "x"), "");
        var ok = WebDesktopConversion.Plan(dir, Project(dir));
        Assert.False((await Apply(ok, dir, dest)).Success);
        var blocked = WebDesktopConversion.Plan(dir, Project(dir), new DesktopConversionOptions("bad id", "x"));
        Assert.Contains("blockers", (await Apply(blocked, dir, _t.Combine("other"))).Error);
    }

    [Fact]
    public void Lockfile_engines_are_read_for_the_build_tools()
    {
        var e = WebDesktopConversion.LockfileEngines("""{ "packages": { "node_modules/vite": { "version": "7.3.8", "engines": { "node": "^20.19.0 || >=22.12.0" } }, "node_modules/x": {} } }""", ["vite", "electron"]);
        Assert.Equal(("7.3.8", "^20.19.0 || >=22.12.0"), e["vite"]);
        Assert.False(e.ContainsKey("electron"));
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
