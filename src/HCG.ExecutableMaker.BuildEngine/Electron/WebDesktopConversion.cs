using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Electron;

public enum ConversionFindingLevel
{
    /// <summary>The project cannot be prepared as a desktop app (nothing is generated).</summary>
    Blocker,
    /// <summary>The desktop app can be built, but this part of the web app will not work in it as it does on the web.</summary>
    Warning,
    /// <summary>How HCG handles something (routing, build-time variables, …).</summary>
    Info,
}

public sealed record ConversionFinding(ConversionFindingLevel Level, string Area, string Message, string? Path = null, int? Line = null)
{
    public string Location => Path is null ? "" : Line is { } l ? $"{Path}:{l}" : Path;
}

public enum FileChangeKind { Add, Modify }

/// <summary>One file the conversion writes in the desktop workspace (never in the imported source).</summary>
public sealed record ConversionFileChange(string RelativePath, FileChangeKind Kind, string Reason, string? Before, string After);

public sealed record DesktopConversionOptions(string AppId, string ProductName);

public sealed record DesktopConversionPlan
{
    public required string ProjectDirectory { get; init; }
    public required string ProjectName { get; init; }
    public required DesktopConversionOptions Options { get; init; }
    public required IReadOnlyList<ConversionFinding> Findings { get; init; }
    public required IReadOnlyList<ConversionFileChange> Changes { get; init; }
    /// <summary>The project's own production build script (e.g. "build", which runs vite build).</summary>
    public string? FrontendBuildScript { get; init; }
    public string OutputDirectory { get; init; } = "dist";
    public string BasePath { get; init; } = "/";
    public bool CanApply => Findings.All(f => f.Level != ConversionFindingLevel.Blocker);
    public IEnumerable<ConversionFinding> Blockers => Findings.Where(f => f.Level == ConversionFindingLevel.Blocker);
}

public sealed record DesktopConversionResult(bool Success, string WorkspaceDirectory, IReadOnlyList<string> Log, string? Error);

/// <summary>
/// "Prepare desktop app" for Vite + React web apps. Inspection is static: files are read as text, nothing in the project
/// runs. Applying writes a separate desktop workspace (the imported source is never changed) containing:
/// <list type="bullet">
/// <item>an Electron main process that serves the production build from the app bundle over a private <c>app://</c>
/// scheme (absolute asset paths and history routes work; unknown routes fall back to index.html, like the web host's
/// rewrite), with context isolation, the renderer sandbox and webSecurity on, Node integration off, permission requests
/// denied and navigation outside the app opened in the user's browser;</item>
/// <item>electron-builder configuration (macOS zip + dmg, ad-hoc signed) and package.json scripts that run the project's
/// own production build first, then check that the output loads without a development server;</item>
/// <item>an updated package-lock.json (npm install --package-lock-only --ignore-scripts: no project or package scripts run).</item>
/// </list>
/// The result is a normal Electron project that the existing electron-builder pipeline builds.
/// </summary>
public static partial class WebDesktopConversion
{
    public const string ElectronVersion = "45.2.3";
    public const string ElectronBuilderVersion = "26.18.0";
    public const string ShellDirectory = "hcg-desktop";
    public const string MainEntry = ShellDirectory + "/main.cjs";
    public const string VerifyScript = ShellDirectory + "/verify-build.cjs";
    public const string BuildScriptName = "desktop:build";
    public const string PackageScriptName = "desktop:dist:mac";
    public const string LinuxPackageScriptName = "desktop:dist:linux";

    private static readonly HashSet<string> CopyExcludedDirectories = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", ".hg", ".svn", "release", ".vite", ".turbo", ".cache", "coverage", "playwright-report", "test-results", ".DS_Store",
    };
    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts", ".vue", ".svelte"];
    private static readonly (string Package, string Label)[] SsrFrameworks =
    [
        ("next", "Next.js"), ("nuxt", "Nuxt"), ("@remix-run/react", "Remix"), ("@sveltejs/kit", "SvelteKit"), ("astro", "Astro"),
        ("@angular/ssr", "Angular SSR"), ("gatsby", "Gatsby"),
    ];
    private static readonly (string Package, string Label)[] ServerFrameworks =
    [
        ("express", "Express"), ("fastify", "Fastify"), ("koa", "Koa"), ("hono", "Hono"), ("@nestjs/core", "NestJS"), ("@hapi/hapi", "hapi"),
    ];
    private static readonly (string Package, string Label)[] RedirectAuth =
    [
        ("@supabase/supabase-js", "Supabase Auth"), ("firebase", "Firebase Authentication"), ("@auth0/auth0-react", "Auth0"), ("@auth0/auth0-spa-js", "Auth0"),
        ("oidc-client-ts", "OpenID Connect (oidc-client-ts)"), ("oidc-client", "OpenID Connect (oidc-client)"), ("@azure/msal-browser", "Microsoft MSAL"),
        ("@clerk/clerk-react", "Clerk"), ("next-auth", "NextAuth"), ("aws-amplify", "AWS Amplify Auth"),
    ];

    // ------------------------------------------------------------------ plan

    /// <summary>Reads the web project (as text only) and proposes the desktop workspace.</summary>
    public static DesktopConversionPlan Plan(string sourceRoot, NodeProjectInfo project, DesktopConversionOptions? options = null)
    {
        var dir = ProjectRoot(sourceRoot, project.Directory);
        var findings = new List<ConversionFinding>();
        var packageJsonText = File.ReadAllText(Path.Combine(dir, "package.json"));
        JsonObject pkg;
        try { pkg = JsonNode.Parse(packageJsonText, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException ex)
        {
            return Blocked(project, options, "package.json", $"package.json could not be read: {ex.Message}");
        }
        var deps = Dependencies(pkg);
        var scripts = pkg["scripts"] as JsonObject ?? [];

        // ---- What kind of project this is ----
        if (project.Kind == NodeProjectKind.ElectronDesktop)
            findings.Add(new(ConversionFindingLevel.Blocker, "Project", "This project is already an Electron app. Build it as it is; Prepare desktop app is for browser-only web apps.", project.ManifestPath));
        var hasVite = deps.ContainsKey("vite") || ViteConfig(dir) is not null;
        var hasReact = deps.ContainsKey("react") && deps.ContainsKey("react-dom");
        foreach (var (pkgName, label) in SsrFrameworks.Where(s => deps.ContainsKey(s.Package)))
            findings.Add(new(ConversionFindingLevel.Blocker, "Framework", $"{label} renders pages on a server. Prepare desktop app packages static browser builds only (Vite + React); a server-rendered app needs its server, which a desktop app does not run.", project.ManifestPath));
        if (!hasVite || !hasReact)
            findings.Add(new(ConversionFindingLevel.Blocker, "Framework",
                $"Prepare desktop app supports Vite + React web apps. This project has {(hasVite ? "Vite" : "no Vite")} and {(hasReact ? "React" : "no React (react and react-dom)")}" +
                (project.Frameworks.Count > 0 ? $" (detected: {string.Join(", ", project.Frameworks)})." : "."), project.ManifestPath));
        if (project.PackageManager != NodePackageManager.Npm)
            findings.Add(new(ConversionFindingLevel.Blocker, "Package manager", $"The project uses {project.PackageManager.ToString().ToLowerInvariant()} ({project.LockFile ?? "packageManager field"}). HCG installs dependencies with npm only, which would ignore that lockfile.", project.ManifestPath));
        foreach (var (name, spec) in deps.Where(d => d.Value.StartsWith("file:", StringComparison.Ordinal) || d.Value.StartsWith("workspace:", StringComparison.Ordinal) || d.Value.StartsWith("link:", StringComparison.Ordinal)))
            findings.Add(new(ConversionFindingLevel.Blocker, "Dependencies", $"Dependency {name} is \"{spec}\", a package outside this project. The desktop workspace contains this project only, so it could not be installed.", project.ManifestPath));
        if (pkg["workspaces"] is not null)
            findings.Add(new(ConversionFindingLevel.Blocker, "Dependencies", "package.json declares npm workspaces. Import the web app's own folder, not the monorepo root.", project.ManifestPath));
        foreach (var conflict in new[] { ShellDirectory, "electron-builder.yml", "electron-builder.yaml", "electron-builder.json", "electron-builder.json5" }.Where(c => File.Exists(Path.Combine(dir, c)) || Directory.Exists(Path.Combine(dir, c))))
            findings.Add(new(ConversionFindingLevel.Blocker, "Project", $"{conflict} already exists in the project. HCG does not overwrite it; remove it or build the project as an Electron app.", conflict));
        foreach (var name in new[] { BuildScriptName, PackageScriptName, LinuxPackageScriptName }.Where(n => scripts.ContainsKey(n)))
            findings.Add(new(ConversionFindingLevel.Blocker, "Scripts", $"package.json already has a \"{name}\" script. HCG does not replace scripts; rename it first.", project.ManifestPath));
        if (pkg["build"] is not null)
            findings.Add(new(ConversionFindingLevel.Blocker, "Project", "package.json already has a \"build\" section (electron-builder configuration). Build the project as an Electron app instead.", project.ManifestPath));

        // ---- Production build ----
        var buildScript = scripts.Select(s => (s.Key, Value: s.Value?.GetValue<string>() ?? ""))
            .OrderBy(s => s.Key == "build" ? 0 : 1).FirstOrDefault(s => ViteBuildRegex().IsMatch(s.Value)).Key;
        if (buildScript is null)
            findings.Add(new(ConversionFindingLevel.Blocker, "Build", "No package.json script runs \"vite build\". The desktop app needs the production build; add a build script (\"build\": \"vite build\").", project.ManifestPath));
        else
            findings.Add(new(ConversionFindingLevel.Info, "Build", $"The desktop build runs the project's own production build first: npm run {buildScript} ({scripts[buildScript]}).", project.ManifestPath));

        var viteConfig = ViteConfig(dir);
        var viteText = viteConfig is null ? "" : File.ReadAllText(Path.Combine(dir, viteConfig));
        var outDir = "dist";
        if (OutDirRegex().Match(viteText) is { Success: true } od) outDir = od.Groups["v"].Value.Trim('/');
        else if (viteText.Contains("outDir", StringComparison.Ordinal))
            findings.Add(new(ConversionFindingLevel.Info, "Build", $"{viteConfig} computes build.outDir; HCG uses the default (dist) and checks after the build that dist/index.html exists.", viteConfig));
        if (outDir.Length == 0 || outDir.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(outDir))
            findings.Add(new(ConversionFindingLevel.Blocker, "Build", $"build.outDir \"{outDir}\" is outside the project.", viteConfig));
        var basePath = "/";
        if (BaseRegex().Match(viteText) is { Success: true } b)
        {
            var v = b.Groups["v"].Value;
            if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase) || v.StartsWith("//", StringComparison.Ordinal))
                findings.Add(new(ConversionFindingLevel.Blocker, "Asset paths", $"{viteConfig} sets base to \"{v}\": the app loads its files from that server, not from the app bundle. Use a path base (\"/\" or \"./\").", viteConfig, LineOf(viteText, b.Index)));
            else basePath = v.Trim('.', '/').Length == 0 ? "/" : "/" + v.Trim('.', '/') + "/";
        }
        else if (viteText.Contains("base:", StringComparison.Ordinal))
            findings.Add(new(ConversionFindingLevel.Info, "Asset paths", $"{viteConfig} computes base; HCG assumes \"/\" (the default). Absolute asset paths such as /assets/… are served from the app bundle.", viteConfig));
        findings.Add(new(ConversionFindingLevel.Info, "Asset paths",
            $"The window loads app://bundle{basePath}. Files in {outDir}/ are served from inside the app (no web server, no network); paths outside {outDir}/ are refused.", null));
        if (ProxyRegex().Match(viteText) is { Success: true } px)
            findings.Add(new(ConversionFindingLevel.Warning, "Backend", $"{viteConfig} configures server.proxy, which forwards requests to a backend only while the Vite development server runs. The desktop app has no development server, so those requests will fail.", viteConfig, LineOf(viteText, px.Index)));

        // ---- Source scan (text only) ----
        var servers = ServerFrameworks.Where(s => deps.ContainsKey(s.Package)).Select(s => s.Label).ToList();
        if (servers.Count > 0)
            findings.Add(new(ConversionFindingLevel.Warning, "Backend", $"The project includes server code ({string.Join(", ", servers)}). Only the browser build is packaged; the server does not run inside the desktop app.", project.ManifestPath));
        var routers = new SortedSet<string>(StringComparer.Ordinal);
        var envVars = new SortedSet<string>(StringComparer.Ordinal);
        var authEvidence = new List<(string File, int Line, string Text)>();
        foreach (var (rel, text) in SourceFiles(dir, outDir))
        {
            foreach (Match m in RelativeApiRegex().Matches(text))
                findings.Add(new(servers.Count > 0 ? ConversionFindingLevel.Blocker : ConversionFindingLevel.Warning, "Backend",
                    $"Requests to {m.Groups["p"].Value} go to the page's own server. In the desktop app the page comes from the app bundle, so there is no server behind {m.Groups["p"].Value}" +
                    (servers.Count > 0 ? " (the project's own backend would have to run separately)." : "; point it at an HTTPS API instead."), rel, LineOf(text, m.Index)));
            foreach (Match m in LocalhostRegex().Matches(text))
                findings.Add(new(ConversionFindingLevel.Warning, "Backend", $"{m.Value} is a server on the developer's computer; it will not exist where the app is installed.", rel, LineOf(text, m.Index)));
            foreach (Match m in ServiceWorkerRegex().Matches(text))
                findings.Add(new(ConversionFindingLevel.Warning, "Offline", "A service worker is registered. Service workers are not enabled for the desktop app's app:// scheme; the app works without its offline cache.", rel, LineOf(text, m.Index)));
            foreach (Match m in AuthCallRegex().Matches(text)) authEvidence.Add((rel, LineOf(text, m.Index), m.Value));
            foreach (Match m in RouterRegex().Matches(text)) routers.Add(m.Value);
            foreach (Match m in EnvRegex().Matches(text)) envVars.Add(m.Groups["n"].Value);
        }
        var authPackages = RedirectAuth.Where(a => deps.ContainsKey(a.Package)).Select(a => a.Label).Distinct().ToList();
        if (authPackages.Count > 0 || authEvidence.Count > 0)
            findings.Add(new(ConversionFindingLevel.Warning, "Sign-in",
                $"Browser sign-in {(authPackages.Count > 0 ? "(" + string.Join(", ", authPackages) + ") " : "")}redirects back to the page's own web address. The desktop app's address is app://bundle, which identity providers do not accept as a redirect URI, so this sign-in will not work in the desktop app until the provider registers a native (desktop) client for it." +
                (authEvidence.Count > 0 ? " Found: " + string.Join("; ", authEvidence.Take(6).Select(a => $"{a.Text} ({a.File}:{a.Line})")) + "." : ""),
                authEvidence.FirstOrDefault().File, authEvidence.Count > 0 ? authEvidence[0].Line : null));
        if (routers.Any(r => r.Contains("Browser", StringComparison.Ordinal)))
            findings.Add(new(ConversionFindingLevel.Info, "Routing", $"History routing ({string.Join(", ", routers)}): every path without a file extension is answered with index.html, as the web host's rewrite does, so deep links and reloads work.", null));
        else if (routers.Count > 0)
            findings.Add(new(ConversionFindingLevel.Info, "Routing", $"Hash routing ({string.Join(", ", routers)}) works unchanged.", null));
        if (envVars.Count > 0)
            findings.Add(new(ConversionFindingLevel.Info, "Configuration", $"Build-time variables are fixed when the desktop app is built: {string.Join(", ", envVars)}. Set them in the desktop workspace before building if the defaults are not right.", null));
        var envFiles = Directory.EnumerateFiles(dir, ".env*").Select(Path.GetFileName).OfType<string>().Where(IsSecretEnvFile).ToList();
        if (envFiles.Count > 0)
            findings.Add(new(ConversionFindingLevel.Warning, "Configuration", $"{string.Join(", ", envFiles)} {(envFiles.Count == 1 ? "is" : "are")} not copied into the desktop workspace (it may hold secrets, and every VITE_ value is visible inside the app). Copy the values you need into the workspace yourself.", envFiles[0]));
        foreach (var hosting in new[] { "vercel.json", "netlify.toml", "_headers", "public/_headers", "public/_redirects", "firebase.json" }.Where(h => File.Exists(Path.Combine(dir, h))))
            findings.Add(new(ConversionFindingLevel.Info, "Hosting", $"{hosting} configures a web host (rewrites, headers). It does not apply to the desktop app; routing is handled by the app's protocol handler, and the window is isolated as described below.", hosting));
        findings.Add(new(ConversionFindingLevel.Info, "Security", "The window runs with context isolation, the renderer sandbox and webSecurity on, Node.js integration off, permission requests (camera, microphone, …) denied, and links to other sites opened in the user's browser.", null));

        // ---- Changes ----
        var opts = options ?? DefaultOptions(project, dir);
        if (!AppIdRegex().IsMatch(opts.AppId))
            findings.Add(new(ConversionFindingLevel.Blocker, "Identity", $"\"{opts.AppId}\" is not a valid app id (reverse-DNS, e.g. com.example.app).", null));
        if (opts.ProductName.Trim().Length == 0 || opts.ProductName.IndexOfAny(['/', '\\', ':', '"']) >= 0)
            findings.Add(new(ConversionFindingLevel.Blocker, "Identity", $"\"{opts.ProductName}\" cannot be used as the app name (it must not be empty or contain / \\ : \").", null));
        var changes = new List<ConversionFileChange>();
        if (findings.All(f => f.Level != ConversionFindingLevel.Blocker))
        {
            changes.Add(new(MainEntry, FileChangeKind.Add, "Electron main process: serves the production build from the app bundle and opens the window with the security settings above.",
                null, MainScript(opts, outDir, basePath)));
            changes.Add(new(VerifyScript, FileChangeKind.Add, "Run after the production build: fails the desktop build when index.html is missing or still loads the development server.",
                null, VerifyBuildScript()));
            changes.Add(new("electron-builder.yml", FileChangeKind.Add, "electron-builder configuration: app id, name, the files packaged (the production build and the shell only), macOS zip and dmg (ad-hoc signed).",
                null, BuilderConfig(opts, outDir)));
            changes.Add(new("package.json", FileChangeKind.Modify,
                $"Electron entry (\"main\"), the desktop scripts, electron {ElectronVersion} and electron-builder {ElectronBuilderVersion}. Runtime dependencies move to devDependencies: Vite bundles them into {outDir}/, so the packaged app ships no node_modules.",
                packageJsonText, PackageJson(pkg, buildScript!, outDir)));
            changes.Add(new("package-lock.json", project.LockFile is null ? FileChangeKind.Add : FileChangeKind.Modify,
                "Updated for the new dependencies with npm install --package-lock-only --ignore-scripts when the workspace is created (no project or package scripts run).",
                null, "(written by npm)"));
        }
        return new DesktopConversionPlan
        {
            ProjectDirectory = project.Directory, ProjectName = project.DisplayName, Options = opts, Findings = findings, Changes = changes,
            FrontendBuildScript = buildScript, OutputDirectory = outDir, BasePath = basePath,
        };
    }

    public static DesktopConversionOptions DefaultOptions(NodeProjectInfo project, string? projectDir = null)
    {
        var title = projectDir is not null && File.Exists(Path.Combine(projectDir, "index.html")) && TitleRegex().Match(File.ReadAllText(Path.Combine(projectDir, "index.html"))) is { Success: true } t
            ? System.Net.WebUtility.HtmlDecode(t.Groups["t"].Value.Trim()) : null;
        var name = project.ProductName ?? (title is { Length: > 0 and < 60 } ? title : project.Name);
        var slug = Regex.Replace(project.Name.ToLowerInvariant().Replace("@", "").Replace('/', '-'), "[^a-z0-9-]", "-").Trim('-');
        if (slug.Length == 0) slug = "app";
        var hcg = (project.Name + " " + name).Contains("hcg", StringComparison.OrdinalIgnoreCase);
        return new DesktopConversionOptions(hcg ? $"com.hakmicapitalgroup.{slug}" : $"app.desktop.{slug}", name);
    }

    private static DesktopConversionPlan Blocked(NodeProjectInfo project, DesktopConversionOptions? options, string area, string message) => new()
    {
        ProjectDirectory = project.Directory, ProjectName = project.DisplayName, Options = options ?? new DesktopConversionOptions("app.desktop.app", project.DisplayName),
        Findings = [new(ConversionFindingLevel.Blocker, area, message, project.ManifestPath)], Changes = [],
    };

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Writes the desktop workspace: a copy of the web project (without node_modules, build output, VCS data or real .env
    /// files) plus the planned changes, then updates package-lock.json without running any script.
    /// </summary>
    public static async Task<DesktopConversionResult> ApplyAsync(DesktopConversionPlan plan, string sourceRoot, string destination, NodeInstallation node,
        IProcessRunner runner, IReadOnlyDictionary<string, string> environment, Action<string>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        void Log(string line) { log.Add(line); progress?.Invoke(line); }
        if (!plan.CanApply) return new(false, destination, log, "The plan has blockers: " + string.Join(" ", plan.Blockers.Select(b => b.Message)));
        if (node.NpmCli is null) return new(false, destination, log, $"npm was not found next to Node.js {node.Version} ({node.NodePath}).");
        var source = ProjectRoot(sourceRoot, plan.ProjectDirectory);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            return new(false, destination, log, $"{destination} is not empty.");
        Directory.CreateDirectory(destination);
        Log($"Copying {plan.ProjectName} into the desktop workspace {destination} (the imported source is not changed)…");
        var copied = CopyProject(source, destination, plan.OutputDirectory, ct);
        Log($"Copied {copied:N0} files (node_modules, {plan.OutputDirectory}/, release/, VCS data and .env files with real values left out).");
        foreach (var change in plan.Changes.Where(c => c.After != "(written by npm)"))
        {
            var path = Path.GetFullPath(Path.Combine(destination, change.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathGuard.IsWithin(destination, path)) return new(false, destination, log, $"{change.RelativePath} is outside the workspace.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, change.After, new UTF8Encoding(false), ct).ConfigureAwait(false);
            Log($"{(change.Kind == FileChangeKind.Add ? "Added" : "Updated")} {change.RelativePath}");
        }
        Log($"Updating package-lock.json with npm {node.NpmVersion} (npm install --package-lock-only --ignore-scripts)…");
        var result = await runner.RunAsync(new ProcessSpec
        {
            Executable = node.NodePath,
            Arguments = [node.NpmCli, "install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund"],
            WorkingDirectory = destination,
            Environment = ElectronBuildPlanner.Environment(environment, node),
            Timeout = TimeSpan.FromMinutes(10),
        }, (_, line) => { if (line.Length > 0) Log("  " + line); }, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return new(false, destination, log, result.TimedOut ? "npm did not finish within 10 minutes." : result.Cancelled ? "Cancelled."
                : $"npm could not resolve the desktop dependencies (exit code {result.ExitCode}). Check the network connection and the project's package.json; nothing was built.");
        if (!File.Exists(Path.Combine(destination, "package-lock.json")))
            return new(false, destination, log, "npm finished but wrote no package-lock.json.");
        Log("The desktop workspace is ready. Build it as an Electron app: install (npm ci), compile (npm run desktop:build), package (electron-builder).");
        return new(true, destination, log, null);
    }

    private static int CopyProject(string source, string destination, string outDir, CancellationToken ct)
    {
        var count = 0;
        var outDirFull = Path.GetFullPath(Path.Combine(source, outDir));
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
            count += CopyEntry(entry, destination, outDirFull, ct);
        return count;
    }

    private static int CopyEntry(FileSystemInfo entry, string destDir, string outDirFull, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entry.LinkTarget is not null) return 0; // symbolic links are never followed or copied
        var target = Path.Combine(destDir, entry.Name);
        if (entry is DirectoryInfo d)
        {
            if (CopyExcludedDirectories.Contains(d.Name) || string.Equals(Path.GetFullPath(d.FullName), outDirFull, StringComparison.Ordinal)) return 0;
            Directory.CreateDirectory(target);
            return d.EnumerateFileSystemInfos().Sum(e => CopyEntry(e, target, outDirFull, ct));
        }
        if (IsSecretEnvFile(entry.Name) || entry.Name == ".DS_Store") return 0;
        File.Copy(entry.FullName, target);
        return 1;
    }

    // ------------------------------------------------------------------ generated files

    private static string MainScript(DesktopConversionOptions o, string outDir, string basePath) => $$"""
        // Generated by HCG Executable Maker: the desktop shell for {{Js(o.ProductName)}}.
        // It shows the production build ({{outDir}}/) from inside the app. There is no web server, no development server and no
        // Node.js access for the page: the window uses context isolation, the renderer sandbox and webSecurity, and has no
        // Node integration. Review and change it like any other project file.
        'use strict';
        const { app, BrowserWindow, protocol, net, shell, session } = require('electron');
        const path = require('node:path');
        const fs = require('node:fs');
        const { pathToFileURL } = require('node:url');

        const PRODUCT = {{Js(o.ProductName)}};
        const SCHEME = 'app';
        const HOST = 'bundle';
        const ORIGIN = `${SCHEME}://${HOST}`;
        const BASE = {{Js(basePath)}};
        const ROOT = path.join(__dirname, '..', {{Js(outDir)}});

        // A private, standard and secure scheme: absolute paths (/assets/…) and history routes resolve inside the app.
        protocol.registerSchemesAsPrivileged([
          { scheme: SCHEME, privileges: { standard: true, secure: true, supportFetchAPI: true } },
        ]);

        function resolveFile(urlPath) {
          let p;
          try { p = decodeURIComponent(urlPath); } catch { return null; }
          if (BASE !== '/' && p.startsWith(BASE)) p = '/' + p.slice(BASE.length);
          const file = path.normalize(path.join(ROOT, p));
          if (file !== ROOT && !file.startsWith(ROOT + path.sep)) return null; // never outside the build
          try { if (fs.statSync(file).isFile()) return file; } catch { /* not a file */ }
          // Paths without a file extension are app routes: answer with index.html, as the web host's rewrite does.
          return path.extname(p) === '' ? path.join(ROOT, 'index.html') : null;
        }

        function openOutside(url) {
          if (/^https:\/\//i.test(url) || /^mailto:/i.test(url)) shell.openExternal(url);
        }

        function createWindow() {
          const win = new BrowserWindow({
            width: 1280,
            height: 860,
            minWidth: 480,
            minHeight: 360,
            title: PRODUCT,
            show: false,
            webPreferences: {
              contextIsolation: true,
              sandbox: true,
              nodeIntegration: false,
              nodeIntegrationInWorker: false,
              webSecurity: true,
              allowRunningInsecureContent: false,
            },
          });
          win.once('ready-to-show', () => win.show());
          // The window never leaves the app: other sites open in the user's browser.
          win.webContents.setWindowOpenHandler(({ url }) => { openOutside(url); return { action: 'deny' }; });
          win.webContents.on('will-navigate', (event, url) => {
            if (!url.startsWith(ORIGIN + '/')) { event.preventDefault(); openOutside(url); }
          });
          win.loadURL(ORIGIN + BASE);
          return win;
        }

        app.whenReady().then(() => {
          protocol.handle(SCHEME, (request) => {
            const url = new URL(request.url);
            if (url.host !== HOST) return new Response('Not found', { status: 404 });
            const file = resolveFile(url.pathname);
            if (!file) return new Response('Not found', { status: 404 });
            return net.fetch(pathToFileURL(file).toString());
          });
          session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
          createWindow();
          app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
        });

        app.on('web-contents-created', (_event, contents) => {
          contents.on('will-attach-webview', (event) => event.preventDefault());
        });

        app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });

        """;

    private static string VerifyBuildScript() => """
        // Generated by HCG Executable Maker. Runs after the production build (npm run desktop:build) and stops the desktop
        // build when the output is missing or still depends on the development server.
        'use strict';
        const fs = require('node:fs');
        const path = require('node:path');

        const dir = path.resolve(__dirname, '..', process.argv[2] || 'dist');
        const index = path.join(dir, 'index.html');
        if (!fs.existsSync(index)) {
          console.error(`HCG desktop: the production build did not produce ${path.relative(process.cwd(), index)}.`);
          process.exit(1);
        }
        const html = fs.readFileSync(index, 'utf8');
        const devServer = ['/@vite/client', '/@react-refresh', 'localhost:5173', '127.0.0.1:5173'].filter((s) => html.includes(s));
        if (devServer.length > 0) {
          console.error(`HCG desktop: ${path.relative(process.cwd(), index)} still loads the development server (${devServer.join(', ')}).`);
          process.exit(1);
        }
        console.log(`HCG desktop: production build ready in ${path.relative(process.cwd(), dir) || '.'}/ (index.html, ${fs.readdirSync(dir).length} entries).`);

        """;

    private static string BuilderConfig(DesktopConversionOptions o, string outDir) => $"""
        # Generated by HCG Executable Maker for the desktop app. Only the production build and the shell are packaged.
        appId: {Yaml(o.AppId)}
        productName: {Yaml(o.ProductName)}
        directories:
          output: release
        files:
          - {Yaml(outDir + "/**/*")}
          - {Yaml(ShellDirectory + "/main.cjs")}
          - package.json
        asar: true
        mac:
          category: public.app-category.productivity
          target:
            - zip
            - dmg
          # Ad-hoc signature: runs on this Mac; other Macs need Developer ID signing and notarization.
          identity: "-"
        linux:
          target:
            - dir

        """;

    private static string PackageJson(JsonObject original, string buildScript, string outDir)
    {
        var pkg = (JsonObject)original.DeepClone();
        pkg["main"] = MainEntry;
        var scripts = pkg["scripts"] as JsonObject ?? new JsonObject();
        pkg["scripts"] = scripts;
        scripts[BuildScriptName] = $"npm run {buildScript} && node {VerifyScript} {outDir}";
        scripts[PackageScriptName] = $"npm run {BuildScriptName} && electron-builder --mac zip dmg --arm64";
        // Every host follows a script that runs the production build first (HCG picks the script for its platform).
        scripts[LinuxPackageScriptName] = $"npm run {BuildScriptName} && electron-builder --linux dir";
        var dev = pkg["devDependencies"] as JsonObject ?? new JsonObject();
        if (pkg["dependencies"] is JsonObject runtime)
        {
            foreach (var (name, value) in runtime.ToList())
                if (!dev.ContainsKey(name)) dev[name] = value?.DeepClone();
            pkg.Remove("dependencies");
        }
        dev["electron"] = ElectronVersion;
        dev["electron-builder"] = ElectronBuilderVersion;
        pkg["devDependencies"] = new JsonObject(dev.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => KeyValuePair.Create(d.Key, d.Value?.DeepClone())));
        return pkg.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    // ------------------------------------------------------------------ helpers

    private static string ProjectRoot(string sourceRoot, string relative)
    {
        var dir = Path.GetFullPath(Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsWithin(Path.GetFullPath(sourceRoot), dir) && dir != Path.GetFullPath(sourceRoot))
            throw new BuildValidationException("The web project is outside the workspace.");
        return dir;
    }

    private static Dictionary<string, string> Dependencies(JsonObject pkg)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
            if (pkg[section] is JsonObject o)
                foreach (var (k, v) in o)
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) all.TryAdd(k, s);
        return all;
    }

    private static string? ViteConfig(string dir) =>
        new[] { "vite.config.ts", "vite.config.mts", "vite.config.js", "vite.config.mjs", "vite.config.cjs" }.FirstOrDefault(f => File.Exists(Path.Combine(dir, f)));

    private static bool IsSecretEnvFile(string name) =>
        name.StartsWith(".env", StringComparison.Ordinal) && !name.EndsWith(".example", StringComparison.Ordinal)
        && !name.EndsWith(".sample", StringComparison.Ordinal) && !name.EndsWith(".template", StringComparison.Ordinal);

    /// <summary>
    /// The app's own code: src/ (Vite's convention), else app/ and lib/. Tool configuration (Vite, Playwright, test
    /// runners), scripts and tests do not run in the window, so they are not scanned.
    /// </summary>
    private static IEnumerable<(string Relative, string Text)> SourceFiles(string dir, string outDir)
    {
        var skip = new HashSet<string>(CopyExcludedDirectories, StringComparer.Ordinal) { outDir, "tests", "test", "e2e", "__tests__", "__mocks__", "fixtures" };
        var roots = Directory.Exists(Path.Combine(dir, "src")) ? ["src"] : new[] { "app", "lib" }.Where(r => Directory.Exists(Path.Combine(dir, r))).ToArray();
        var count = 0;
        foreach (var file in roots.SelectMany(r => Directory.EnumerateFiles(Path.Combine(dir, r), "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (rel.Split('/').SkipLast(1).Any(skip.Contains)) continue;
            if (!SourceExtensions.Contains(Path.GetExtension(file)) || rel.Contains(".test.", StringComparison.Ordinal) || rel.Contains(".spec.", StringComparison.Ordinal)
                || rel.StartsWith("vite.config", StringComparison.Ordinal)) continue;
            var info = new FileInfo(file);
            if (info.Length > 1_000_000 || info.LinkTarget is not null) continue;
            if (++count > 5000) yield break;
            yield return (rel, File.ReadAllText(file));
        }
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;
    private static string Js(string s) => JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static string Yaml(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [GeneratedRegex(@"(^|[\s&;(])vite\s+build\b")] private static partial Regex ViteBuildRegex();
    [GeneratedRegex(@"outDir\s*:\s*['""`](?<v>[^'""`]+)['""`]")] private static partial Regex OutDirRegex();
    [GeneratedRegex(@"(?<![\w.])base\s*:\s*['""`](?<v>[^'""`]*)['""`]")] private static partial Regex BaseRegex();
    [GeneratedRegex(@"\bproxy\s*:\s*\{")] private static partial Regex ProxyRegex();
    [GeneratedRegex(@"(?:fetch|axios(?:\.\w+)?|\.(?:get|post|put|patch|delete))\(\s*['""`](?<p>/(?:api|graphql|trpc|auth|rest)\b[^'""`]*)")] private static partial Regex RelativeApiRegex();
    [GeneratedRegex(@"https?://(?:localhost|127\.0\.0\.1)(?::\d+)?[^\s'""`)]*")] private static partial Regex LocalhostRegex();
    [GeneratedRegex(@"serviceWorker\s*\.\s*register\s*\(")] private static partial Regex ServiceWorkerRegex();
    [GeneratedRegex(@"\b(?:signInWithOAuth|signInWithRedirect|signInWithPopup|loginWithRedirect|loginWithPopup|acquireTokenRedirect|signinRedirect)\b")] private static partial Regex AuthCallRegex();
    [GeneratedRegex(@"\b(?:BrowserRouter|createBrowserRouter|HashRouter|createHashRouter|MemoryRouter)\b")] private static partial Regex RouterRegex();
    [GeneratedRegex(@"import\.meta\.env\.(?<n>VITE_[A-Z0-9_]+)")] private static partial Regex EnvRegex();
    [GeneratedRegex(@"<title>(?<t>[^<]*)</title>", RegexOptions.IgnoreCase)] private static partial Regex TitleRegex();
    [GeneratedRegex(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")] private static partial Regex AppIdRegex();
}
