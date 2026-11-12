using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Node;

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

public enum FileChangeKind { Add, Modify, Remove }

/// <summary>One file the conversion writes in the desktop project (never in the imported source).</summary>
public sealed record ConversionFileChange(string RelativePath, FileChangeKind Kind, string Reason, string? Before, string After)
{
    /// <summary>Binary content copied from this file (the icon) instead of <see cref="After"/>.</summary>
    public string? CopyFrom { get; init; }
    /// <summary>What the preview shows instead of the content (values that should not be displayed or logged).</summary>
    public string? Preview { get; init; }
}

public enum DesktopConversionMode
{
    /// <summary>A browser app: HCG generates the Electron main process, packaging and scripts.</summary>
    WrapBrowserApp,
    /// <summary>An Electron app with its own main process and no packaging: HCG adds packaging and keeps the main process.</summary>
    AddPackaging,
}

/// <summary>Where the desktop project came from (recorded in hcg-desktop/conversion.json).</summary>
public sealed record DesktopConversionSource(Guid ProjectId, string ProjectName, string Location, string WebDirectory);

public sealed record DesktopConversionOptions(string AppId, string ProductName)
{
    /// <summary>A square PNG (at least 512×512) or an .icns file; null = Electron's default icon.</summary>
    public string? IconPath { get; init; }
    /// <summary>electron-builder mac targets written to the configuration (HCG's build passes its own choice on the command line).</summary>
    public IReadOnlyList<string> MacTargets { get; init; } = ["zip", "dmg"];
    public IReadOnlyList<string> MacArchs { get; init; } = ["arm64"];
    /// <summary>Developer ID Application identity (its name without the "Developer ID Application: " prefix); null = ad-hoc signature.</summary>
    public string? MacIdentity { get; init; }
    /// <summary>Copy the public VITE_ settings from the project's .env files into the desktop build (never names that look like credentials).</summary>
    public bool UsePublicEnvironment { get; init; } = true;
    public DesktopConversionSource? Source { get; init; }
}

public sealed record DesktopConversionPlan
{
    public required string ProjectDirectory { get; init; }
    public required string ProjectName { get; init; }
    public required DesktopConversionOptions Options { get; init; }
    public required IReadOnlyList<ConversionFinding> Findings { get; init; }
    public required IReadOnlyList<ConversionFileChange> Changes { get; init; }
    public DesktopConversionMode Mode { get; init; } = DesktopConversionMode.WrapBrowserApp;
    public DesktopEligibility? Eligibility { get; init; }
    /// <summary>The project's own production build script (e.g. "build", which runs vite build).</summary>
    public string? FrontendBuildScript { get; init; }
    public string OutputDirectory { get; init; } = "dist";
    public string BasePath { get; init; } = "/";
    /// <summary>The record of an earlier conversion found in the folder: its files are replaced, not duplicated.</summary>
    public DesktopConversionRecord? PreviousRecord { get; init; }
    /// <summary>The monorepo lockfile that seeds this project's own lockfile (relative to the workspace source).</summary>
    public string? SeedLockFile { get; init; }
    /// <summary>Public VITE_ settings copied into the desktop build (names only), and the ones left out because they look like credentials.</summary>
    public IReadOnlyList<string> PublicEnvironment { get; init; } = [];
    public IReadOnlyList<string> OmittedEnvironment { get; init; } = [];
    public IReadOnlyList<string> RemoteHosts { get; init; } = [];
    public string? IconSummary { get; init; }
    public bool CanApply => Findings.All(f => f.Level != ConversionFindingLevel.Blocker);
    public IEnumerable<ConversionFinding> Blockers => Findings.Where(f => f.Level == ConversionFindingLevel.Blocker);
}

public sealed record DesktopConversionResult(bool Success, string WorkspaceDirectory, IReadOnlyList<string> Log, string? Error)
{
    public DesktopConversionRecord? Record { get; init; }
}

public sealed record DesktopConversionRecordFile(string Path, string Sha256);

/// <summary>hcg-desktop/conversion.json: what HCG generated, from what, with which options. Repeated conversions read it.</summary>
public sealed record DesktopConversionRecord
{
    public int Format { get; init; } = 1;
    public string Generator { get; init; } = "HCG Executable Maker";
    public string GeneratorVersion { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DesktopConversionMode Mode { get; init; }
    public DesktopConversionSource? Source { get; init; }
    /// <summary>SHA-256 over the copied source files (path + content), so a later run can tell whether the web app changed.</summary>
    public string? SourceFingerprint { get; init; }
    public string AppId { get; init; } = "";
    public string ProductName { get; init; } = "";
    public IReadOnlyList<string> MacTargets { get; init; } = [];
    public IReadOnlyList<string> MacArchs { get; init; } = [];
    public string? MacIdentity { get; init; }
    public string? Icon { get; init; }
    public string ElectronVersion { get; init; } = "";
    public string ElectronBuilderVersion { get; init; } = "";
    public IReadOnlyList<string> PublicEnvironment { get; init; } = [];
    /// <summary>Every file HCG wrote, with its SHA-256 as written (edited files are never overwritten).</summary>
    public IReadOnlyList<DesktopConversionRecordFile> Files { get; init; } = [];
    /// <summary>package.json exactly as it was before HCG changed it.</summary>
    public string PackageJsonBefore { get; init; } = "";
}

/// <summary>
/// "Create desktop application" for Vite + React web apps. Inspection is static: files are read as text, nothing in the
/// project runs. Applying writes a separate desktop project (the imported source is never changed) containing:
/// <list type="bullet">
/// <item>an Electron main process that serves the production build from the app bundle over a private <c>app://</c>
/// scheme (absolute asset paths, lazy-loaded chunks and history routes work; unknown routes fall back to index.html, like
/// the web host's rewrite; reloads and deep links resolve the same way), with context isolation, the renderer sandbox and
/// webSecurity on, Node integration off, a content security policy, permission requests denied, and navigation or new
/// windows outside the app opened in the user's browser only for https and mailto links;</item>
/// <item>electron-builder configuration (macOS .app with zip and dmg, ad-hoc signed unless a Developer ID identity is
/// chosen) and package.json scripts that run the project's own production build first, then check that the output and
/// every asset index.html references exist and that nothing loads the development server;</item>
/// <item>package-lock.json resolved with npm install --package-lock-only --ignore-scripts (no project or package script
/// runs), starting from the project's own lockfile or its monorepo's;</item>
/// <item>hcg-desktop/conversion.json and hcg-desktop/README.md: the record of every generated file.</item>
/// </list>
/// A folder that already holds HCG's earlier output is converted again cleanly: the recorded files are replaced and
/// package.json is rebuilt from the recorded original, so nothing accumulates. Files edited after generation are not
/// overwritten. An Electron app with a main process but no packaging keeps its main process (only packaging is added).
/// </summary>
public static partial class WebDesktopConversion
{
    public const string ElectronVersion = "45.3.0";
    public const string ElectronBuilderVersion = "26.18.0";
    /// <summary>Electron 45's package requires this Node.js to install (its engines field).</summary>
    public const string ElectronNodeRequirement = ">=22.12.0";
    public const string ShellDirectory = "hcg-desktop";
    public const string MainEntry = ShellDirectory + "/main.cjs";
    public const string VerifyScript = ShellDirectory + "/verify-build.cjs";
    public const string RecordFile = NodeProjectScanner.HcgDesktopRecordPath;
    public const string ReadmeFile = ShellDirectory + "/README.md";
    public const string EntitlementsFile = ShellDirectory + "/entitlements.mac.plist";
    public const string EnvironmentFile = ".env.production.local";
    public const string BuildScriptName = "desktop:build";
    public const string PackageScriptName = "desktop:dist:mac";
    public const string LinuxPackageScriptName = "desktop:dist:linux";
    private const string WrittenByNpm = "(written by npm)";

    private static readonly HashSet<string> CopyExcludedDirectories = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", ".hg", ".svn", "release", ".vite", ".turbo", ".cache", "coverage", "playwright-report", "test-results", ".DS_Store",
    };
    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts", ".vue", ".svelte"];
    private static readonly (string Package, string Label)[] RedirectAuth =
    [
        ("@supabase/supabase-js", "Supabase Auth"), ("firebase", "Firebase Authentication"), ("@auth0/auth0-react", "Auth0"), ("@auth0/auth0-spa-js", "Auth0"),
        ("oidc-client-ts", "OpenID Connect (oidc-client-ts)"), ("oidc-client", "OpenID Connect (oidc-client)"), ("@azure/msal-browser", "Microsoft MSAL"),
        ("@clerk/clerk-react", "Clerk"), ("next-auth", "NextAuth"), ("aws-amplify", "AWS Amplify Auth"),
    ];
    /// <summary>Vite's production-mode .env files, in the order it loads them (later files win).</summary>
    private static readonly string[] ProductionEnvFiles = [".env", ".env.local", ".env.production", ".env.production.local"];
    /// <summary>Hosts that are documentation or schema links, not services the app calls.</summary>
    private static readonly HashSet<string> NonServiceHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.w3.org", "w3.org", "react.dev", "reactjs.org", "vite.dev", "vitejs.dev", "developer.mozilla.org", "schema.org", "json-schema.org", "example.com", "www.example.com",
    };

    // ------------------------------------------------------------------ plan

    /// <summary>Reads the web project (as text only) and proposes the desktop project.</summary>
    public static DesktopConversionPlan Plan(string sourceRoot, NodeProjectInfo project, DesktopConversionOptions? options = null)
    {
        var dir = ProjectRoot(sourceRoot, project.Directory);
        var findings = new List<ConversionFinding>();

        // ---- An earlier conversion in this folder: plan from the package.json it started from ----
        DesktopConversionRecord? previous = null;
        string packageJsonText;
        var currentPackageJson = File.ReadAllText(Path.Combine(dir, "package.json"));
        if (File.Exists(Path.Combine(dir, RecordFile.Replace('/', Path.DirectorySeparatorChar))))
        {
            previous = ReadRecord(Path.Combine(dir, RecordFile.Replace('/', Path.DirectorySeparatorChar)), out var recordProblem);
            if (previous is null)
                return Blocked(project, options, "Project", $"{RecordFile} exists but could not be read ({recordProblem}). Remove the hcg-desktop folder or build the project as an Electron app.");
            foreach (var f in previous.Files.Where(f => f.Path != "package-lock.json"))
            {
                var full = Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full) && Sha256File(full) != f.Sha256)
                    findings.Add(new(ConversionFindingLevel.Blocker, "Project", $"{f.Path} was changed after HCG generated it. HCG does not overwrite your edits: undo the change, or build this folder as the Electron app it already is.", f.Path));
            }
            packageJsonText = previous.PackageJsonBefore;
            findings.Add(new(ConversionFindingLevel.Info, "Project", $"HCG generated a desktop wrapper here on {previous.CreatedAt:yyyy-MM-dd}. Converting again replaces the {previous.Files.Count} recorded files and rebuilds package.json from its recorded original, so nothing is duplicated.", RecordFile));
        }
        else packageJsonText = currentPackageJson;

        JsonObject pkg;
        try { pkg = JsonNode.Parse(packageJsonText, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException ex) { return Blocked(project, options, "package.json", $"package.json could not be read: {ex.Message}"); }
        var deps = Dependencies(pkg);
        var scripts = pkg["scripts"] as JsonObject ?? [];

        // ---- What kind of project this is (the same rules the inspector shows) ----
        var eligibility = previous is not null
            ? new DesktopEligibility(previous.Mode == DesktopConversionMode.AddPackaging ? DesktopConversionCategory.ElectronWithoutPackaging : DesktopConversionCategory.BrowserApp, "Regenerating HCG's earlier desktop wrapper.", [])
            : project.DesktopEligibility ?? DesktopEligibilityRules.Classify(project, []);
        var mode = eligibility.Category == DesktopConversionCategory.ElectronWithoutPackaging ? DesktopConversionMode.AddPackaging : DesktopConversionMode.WrapBrowserApp;
        if (!eligibility.CanConvert)
        {
            findings.Add(new(ConversionFindingLevel.Blocker, "Project", eligibility.Summary, project.ManifestPath));
            foreach (var r in eligibility.Reasons) findings.Add(new(ConversionFindingLevel.Blocker, "Project", r, project.ManifestPath));
        }
        if (previous is null)
        {
            foreach (var conflict in new[] { ShellDirectory, "electron-builder.yml", "electron-builder.yaml", "electron-builder.json", "electron-builder.json5", "electron-builder.config.js", "electron-builder.config.cjs" }
                         .Where(c => File.Exists(Path.Combine(dir, c)) || Directory.Exists(Path.Combine(dir, c))))
                findings.Add(new(ConversionFindingLevel.Blocker, "Project", $"{conflict} already exists in the project and was not generated by HCG. HCG does not overwrite it: remove it, or build the project as an Electron app.", conflict));
            foreach (var name in new[] { BuildScriptName, PackageScriptName, LinuxPackageScriptName }.Where(n => scripts.ContainsKey(n)))
                findings.Add(new(ConversionFindingLevel.Blocker, "Scripts", $"package.json already has a \"{name}\" script. HCG does not replace scripts it did not write; rename it first.", project.ManifestPath));
            if (pkg["build"] is not null)
                findings.Add(new(ConversionFindingLevel.Blocker, "Project", "package.json already has a \"build\" section (electron-builder configuration). Build the project as an Electron app instead.", project.ManifestPath));
        }

        // ---- Production build ----
        var buildScript = scripts.Select(s => (s.Key, Value: s.Value?.GetValue<string>() ?? ""))
            .OrderBy(s => s.Key == "build" ? 0 : 1).ThenBy(s => s.Key, StringComparer.Ordinal).FirstOrDefault(s => DesktopEligibilityRules.ViteBuildRegex().IsMatch(s.Value)).Key;
        if (buildScript is null)
        {
            if (!findings.Any(f => f.Message.Contains("vite build", StringComparison.Ordinal)))
                findings.Add(new(ConversionFindingLevel.Blocker, "Build", "No package.json script runs \"vite build\". The desktop app needs the production build; add a build script (\"build\": \"vite build\").", project.ManifestPath));
        }
        else
            findings.Add(new(ConversionFindingLevel.Info, "Build", $"The desktop build runs the project's own production build first: npm run {buildScript} ({scripts[buildScript]}). The website's own build, scripts and deployment configuration stay as they are.", project.ManifestPath));

        var viteConfig = ViteConfig(dir);
        var viteText = viteConfig is null ? "" : File.ReadAllText(Path.Combine(dir, viteConfig));
        var outDir = "dist";
        if (OutDirRegex().Match(viteText) is { Success: true } od) outDir = od.Groups["v"].Value.Trim('/');
        else if (viteText.Contains("outDir", StringComparison.Ordinal))
            findings.Add(new(ConversionFindingLevel.Info, "Build", $"{viteConfig} computes build.outDir; HCG uses the default (dist) and the desktop build stops if dist/index.html is missing.", viteConfig));
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
        else if (viteText.Contains("base:", StringComparison.Ordinal) || viteText.Contains("base,", StringComparison.Ordinal))
            findings.Add(new(ConversionFindingLevel.Info, "Asset paths", $"{viteConfig} computes base; HCG assumes \"/\" (the default when its variables are unset). Absolute asset paths such as /assets/… are served from the app bundle.", viteConfig));
        if (mode == DesktopConversionMode.WrapBrowserApp)
            findings.Add(new(ConversionFindingLevel.Info, "Loading",
                $"The window loads app://bundle{basePath}, a private scheme served from the app's own files ({outDir}/): no web server, development server or network. Lazy-loaded chunks and assets resolve like on the web; paths without a file extension are answered with index.html (like the web host's rewrite), so deep links and reloads keep working. Paths outside {outDir}/ are refused."));
        if (ProxyRegex().Match(viteText) is { Success: true } px)
            findings.Add(new(ConversionFindingLevel.Warning, "Backend", $"{viteConfig} configures server.proxy, which forwards requests to a backend only while the Vite development server runs. The desktop app has no development server, so those requests will fail.", viteConfig, LineOf(viteText, px.Index)));

        // ---- Source scan (text only) ----
        var devServers = DesktopEligibilityRules.ServerFrameworks.Where(s => deps.ContainsKey(s.Package)).Select(s => s.Label).ToList();
        if (devServers.Count > 0 && eligibility.CanConvert)
            findings.Add(new(ConversionFindingLevel.Warning, "Backend", $"The project includes server code ({string.Join(", ", devServers)}, development only). Only the browser build is packaged; no server runs inside the desktop app.", project.ManifestPath));
        var routers = new SortedSet<string>(StringComparer.Ordinal);
        var envVars = new SortedSet<string>(StringComparer.Ordinal);
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var authEvidence = new List<(string File, int Line, string Text)>();
        var lazyChunks = 0;
        var usesHcgConnect = false;
        foreach (var (rel, text) in SourceFiles(dir, outDir))
        {
            foreach (Match m in RelativeApiRegex().Matches(text))
                findings.Add(new(ConversionFindingLevel.Warning, "Backend",
                    $"Requests to {m.Groups["p"].Value} go to the page's own server. In the desktop app the page comes from the app bundle, so there is no server behind {m.Groups["p"].Value}; point it at an HTTPS API instead.", rel, LineOf(text, m.Index)));
            foreach (Match m in LocalhostRegex().Matches(text))
                findings.Add(new(ConversionFindingLevel.Warning, "Backend", $"{m.Value} is a server on the developer's computer; it will not exist where the app is installed.", rel, LineOf(text, m.Index)));
            foreach (Match m in ServiceWorkerRegex().Matches(text))
                findings.Add(new(ConversionFindingLevel.Warning, "Offline", "A service worker is registered. Service workers are not enabled for the desktop app's app:// scheme; the app works without its offline cache.", rel, LineOf(text, m.Index)));
            foreach (Match m in AuthCallRegex().Matches(text)) authEvidence.Add((rel, LineOf(text, m.Index), m.Value));
            foreach (Match m in RouterRegex().Matches(text)) routers.Add(m.Value);
            foreach (Match m in EnvRegex().Matches(text)) envVars.Add(m.Groups["n"].Value);
            foreach (Match m in HttpsHostRegex().Matches(text))
                if (!NonServiceHosts.Contains(m.Groups["h"].Value) && !m.Groups["h"].Value.Contains('<') && m.Groups["h"].Value.Contains('.')) hosts.Add(m.Groups["h"].Value);
            lazyChunks += LazyImportRegex().Matches(text).Count;
            if (text.Contains("VITE_HCG_CONNECT_", StringComparison.Ordinal)) usesHcgConnect = true;
        }
        var authPackages = RedirectAuth.Where(a => deps.ContainsKey(a.Package)).Select(a => a.Label).Distinct().ToList();
        if (authPackages.Count > 0 || authEvidence.Count > 0)
            findings.Add(new(ConversionFindingLevel.Warning, "Sign-in",
                $"Browser sign-in {(authPackages.Count > 0 ? "(" + string.Join(", ", authPackages) + ") " : "")}redirects back to the page's own web address. The desktop app's address is app://bundle, which identity providers do not accept as a redirect URI, so this sign-in will not work in the desktop app until the provider registers a native (desktop) client for it." +
                (authEvidence.Count > 0 ? " Found: " + string.Join("; ", authEvidence.Take(6).Select(a => $"{a.Text} ({a.File}:{a.Line})")) + "." : ""),
                authEvidence.FirstOrDefault().File, authEvidence.Count > 0 ? authEvidence[0].Line : null));
        if (usesHcgConnect)
            findings.Add(new(ConversionFindingLevel.Warning, "Sign-in",
                "The app is prepared for HCG Connect sign-in (VITE_HCG_CONNECT_…). Its redirect URI must be on the origin serving the web build, which a desktop app does not have. Desktop sign-in needs a separate public desktop client registered centrally by HCG, with a loopback redirect (http://127.0.0.1:<port>/…) and PKCE, and the system browser — the way HCG Executable Maker itself signs in. Until HCG registers that client, sign-in stays unavailable in the desktop app. HCG does not register clients, add a login service or put a client secret in the app."));
        if (routers.Any(r => r.Contains("Browser", StringComparison.Ordinal)))
            findings.Add(new(ConversionFindingLevel.Info, "Routing", mode == DesktopConversionMode.WrapBrowserApp
                ? $"History routing ({string.Join(", ", routers)}): every path without a file extension is answered with index.html, so navigation, deep links and reloads work as on the web."
                : $"History routing ({string.Join(", ", routers)}) is used: check that your main process serves index.html for app routes (with loadFile, a reload on a deep route fails)."));
        else if (routers.Count > 0)
            findings.Add(new(ConversionFindingLevel.Info, "Routing", $"Hash routing ({string.Join(", ", routers)}) works unchanged."));
        if (lazyChunks > 0)
            findings.Add(new(ConversionFindingLevel.Info, "Loading", $"{lazyChunks} lazy import(s) (code splitting): the chunks are part of the build and are served from the app like every other file."));
        if (hosts.Count > 0)
            findings.Add(new(ConversionFindingLevel.Info, "Network", $"The code refers to these HTTPS hosts: {string.Join(", ", hosts.Take(12))}{(hosts.Count > 12 ? $" and {hosts.Count - 12} more" : "")}. Remote services stay remote: the app needs a network connection for them and shows its own errors when they cannot be reached; HCG bundles no server and no server credentials."));

        // ---- Build-time settings ----
        var (publicEnv, omittedEnv, envSources, envText) = PublicEnvironment(dir, options?.UsePublicEnvironment ?? true);
        if (envVars.Count > 0)
            findings.Add(new(ConversionFindingLevel.Info, "Configuration", $"Build-time variables are fixed when the desktop app is built: {string.Join(", ", envVars)}." +
                (publicEnv.Count > 0 ? $" Taken from {string.Join(", ", envSources)}: {string.Join(", ", publicEnv)} (public by design: every VITE_ value is embedded in the web build as well)." : " No .env file sets them, so the app's defaults apply.")));
        if (omittedEnv.Count > 0)
            findings.Add(new(ConversionFindingLevel.Warning, "Configuration", $"Not copied into the desktop build because the names look like credentials: {string.Join(", ", omittedEnv)}. Everything in the app can be read by its users; server credentials never belong in it.", envSources.FirstOrDefault()));
        foreach (var hosting in new[] { "vercel.json", "netlify.toml", "_headers", "public/_headers", "public/_redirects", "firebase.json" }.Where(h => File.Exists(Path.Combine(dir, h))))
            findings.Add(new(ConversionFindingLevel.Info, "Hosting", $"{hosting} configures the web host (rewrites, headers). It still applies to the website; in the desktop app, routing and the security policy come from the generated main process.", hosting));

        // ---- Node.js ----
        findings.Add(new(ConversionFindingLevel.Info, "Node.js", $"Electron {ElectronVersion} and electron-builder {ElectronBuilderVersion} are added (electron requires Node.js {ElectronNodeRequirement} to install)" +
            (project.EnginesNode is { } en ? $"; the project requires Node.js {en}." : ".") + " The installed app includes its own runtime: users do not need Node.js."));

        // ---- Monorepo lockfile ----
        string? seed = null;
        var hasOwnLock = File.Exists(Path.Combine(dir, "package-lock.json")) || File.Exists(Path.Combine(dir, "npm-shrinkwrap.json"));
        if (!hasOwnLock && project.WorkspaceLockFile is { } wl && wl.EndsWith("package-lock.json", StringComparison.Ordinal) && previous is null)
        {
            seed = wl;
            findings.Add(new(ConversionFindingLevel.Info, "Dependencies", $"This app is part of a monorepo whose versions are pinned in {wl}. The desktop project gets its own package-lock.json, built from those pinned versions (npm keeps every version that still satisfies package.json)."));
        }
        else if (!hasOwnLock && previous is null)
            findings.Add(new(ConversionFindingLevel.Warning, "Dependencies", "The project has no package-lock.json: npm resolves the versions now and the desktop project pins them in a new lockfile. Commit a lockfile in the web project for builds that match the website exactly."));

        // ---- Identity, icon, signing ----
        var opts = options ?? DefaultOptions(project, dir);
        if (!AppIdRegex().IsMatch(opts.AppId))
            findings.Add(new(ConversionFindingLevel.Blocker, "Identity", $"\"{opts.AppId}\" is not a valid app id (reverse-DNS, e.g. com.example.app).", null));
        if (opts.ProductName.Trim().Length == 0 || opts.ProductName.IndexOfAny(['/', '\\', ':', '"']) >= 0 || opts.ProductName.Length > 80)
            findings.Add(new(ConversionFindingLevel.Blocker, "Identity", $"\"{opts.ProductName}\" cannot be used as the app name (it must not be empty, longer than 80 characters or contain / \\ : \").", null));
        if (opts.MacTargets.Any(t => t is not ("zip" or "dmg" or "dir")) || opts.MacArchs.Any(a => a is not ("arm64" or "x64" or "universal")) || opts.MacArchs.Count == 0)
            findings.Add(new(ConversionFindingLevel.Blocker, "Targets", "macOS targets must be among zip, dmg and dir, and architectures among arm64, x64 and universal.", null));
        if (opts.MacIdentity is { } identity && (identity.Length == 0 || identity.Contains('"') || identity.StartsWith("Developer ID Application:", StringComparison.Ordinal) || identity.StartsWith('-')))
            findings.Add(new(ConversionFindingLevel.Blocker, "Signing", $"\"{identity}\" is not a signing identity name HCG can write (use the certificate's name without the \"Developer ID Application: \" prefix).", null));
        string? iconSummary = null;
        string? iconTarget = null;
        if (opts.IconPath is { } icon)
        {
            var check = DesktopIcon.Check(icon);
            if (check.Problem is not null) findings.Add(new(ConversionFindingLevel.Blocker, "Icon", check.Problem, icon));
            else
            {
                iconTarget = ShellDirectory + "/icon" + (check.IsIcns ? ".icns" : ".png");
                iconSummary = check.Summary;
                findings.Add(new(ConversionFindingLevel.Info, "Icon", $"App icon: {Path.GetFileName(icon)} ({check.Summary}).{(check.IsIcns ? " The Linux build uses Electron's default icon (.icns is macOS only)." : "")}"));
            }
        }
        else findings.Add(new(ConversionFindingLevel.Info, "Icon", "No icon chosen: the app uses Electron's default icon. Choose a square PNG of at least 512×512 (1024×1024 is best) or an .icns file."));
        findings.Add(new(ConversionFindingLevel.Info, "Signing", opts.MacIdentity is null
            ? "Signing: ad-hoc signature only. The app runs on the Mac that built it; it is NOT signed for distribution and NOT notarized, so other Macs show a Gatekeeper warning. Developer ID signing and notarization are separate, explicit steps."
            : $"Signing: electron-builder signs with \"Developer ID Application: {opts.MacIdentity}\" (hardened runtime, the JIT entitlement V8 needs). Notarization is a separate step HCG does not run during the build."));

        // ---- Security of an existing main process (kept, not rewritten) ----
        if (mode == DesktopConversionMode.AddPackaging && project.MainEntry is { } existingMain && File.Exists(Path.Combine(dir, existingMain.TrimStart('.', '/'))))
        {
            var mainText = File.ReadAllText(Path.Combine(dir, existingMain.TrimStart('.', '/')));
            foreach (var (pattern, message) in new (Regex, string)[]
                     {
                         (InsecurePreferenceRegex(), "turns off a renderer protection ({0}). HCG keeps your main process as it is: set contextIsolation and sandbox to true and nodeIntegration and webSecurity accordingly before distributing the app."),
                         (DevServerLoadRegex(), "loads a development server ({0}). The packaged app must load the production build instead."),
                     })
                foreach (Match m in pattern.Matches(mainText))
                    findings.Add(new(ConversionFindingLevel.Warning, "Main process", $"{existingMain} " + string.Format(System.Globalization.CultureInfo.InvariantCulture, message, m.Value), existingMain, LineOf(mainText, m.Index)));
            findings.Add(new(ConversionFindingLevel.Info, "Main process", $"Your main process {existingMain} is kept unchanged; HCG adds packaging only. Runtime dependencies stay in \"dependencies\" because your main process may load them."));
        }
        else if (mode == DesktopConversionMode.WrapBrowserApp)
            findings.Add(new(ConversionFindingLevel.Info, "Security", "The window runs with context isolation, the renderer sandbox and webSecurity on, Node.js integration off and no preload script (the page needs no native access). A content security policy limits scripts to the app itself; permission requests (camera, microphone, …) are denied; links to other sites open in the user's browser only when they are https or mailto, every other navigation and new window is refused."));

        // ---- Changes ----
        var changes = new List<ConversionFileChange>();
        if (findings.All(f => f.Level != ConversionFindingLevel.Blocker))
        {
            if (mode == DesktopConversionMode.WrapBrowserApp)
                changes.Add(new(MainEntry, FileChangeKind.Add, "Electron main process: serves the production build from the app bundle and opens the window with the security settings above.", null, MainScript(opts, outDir, basePath)));
            changes.Add(new(VerifyScript, FileChangeKind.Add, "Runs after the production build: stops the desktop build when index.html or an asset it references is missing, or when the build still loads the development server.", null, VerifyBuildScript()));
            changes.Add(new("electron-builder.yml", FileChangeKind.Add, $"electron-builder configuration: app id, name, {(mode == DesktopConversionMode.WrapBrowserApp ? "the files packaged (the production build and the shell only), " : "")}icon, macOS {string.Join(" and ", opts.MacTargets)} for {string.Join(", ", opts.MacArchs)} ({(opts.MacIdentity is null ? "ad-hoc signed" : "Developer ID signed")}), publishing off.",
                null, BuilderConfig(opts, outDir, iconTarget, mode)));
            if (iconTarget is not null)
                changes.Add(new(iconTarget, FileChangeKind.Add, $"The app icon ({iconSummary}).", null, "") { CopyFrom = opts.IconPath, Preview = $"Copied from {opts.IconPath}" });
            if (opts.MacIdentity is not null)
                changes.Add(new(EntitlementsFile, FileChangeKind.Add, "Hardened-runtime entitlements for Developer ID signing (JIT for V8 only).", null, Entitlements()));
            if (publicEnv.Count > 0)
                changes.Add(new(EnvironmentFile, FileChangeKind.Add, $"The public VITE_ settings the web build uses ({string.Join(", ", publicEnv)}), so the desktop build matches it.", null, envText)
                    { Preview = string.Join("\n", publicEnv.Select(n => $"{n}=… (value from {string.Join(", ", envSources)})")) });
            changes.Add(new("package.json", FileChangeKind.Modify,
                mode == DesktopConversionMode.WrapBrowserApp
                    ? $"Electron entry (\"main\"), the desktop scripts, electron {ElectronVersion} and electron-builder {ElectronBuilderVersion}. Runtime dependencies move to devDependencies: Vite bundles them into {outDir}/, so the packaged app ships no node_modules."
                    : $"The desktop scripts and electron-builder {ElectronBuilderVersion}; \"main\", electron and your dependencies are kept.",
                currentPackageJson, PackageJson(pkg, buildScript!, outDir, basePath, mode)));
            changes.Add(new("package-lock.json", hasOwnLock || previous is not null ? FileChangeKind.Modify : FileChangeKind.Add,
                seed is not null ? $"Created from the monorepo lockfile {seed}, then completed with npm install --package-lock-only --ignore-scripts (no project or package script runs)."
                                 : "Updated for the new dependencies with npm install --package-lock-only --ignore-scripts when the desktop project is created (no project or package script runs).",
                null, WrittenByNpm));
            changes.Add(new(ReadmeFile, FileChangeKind.Add, "What HCG generated and why (for whoever maintains the desktop project).", null, Readme(opts, mode, outDir, buildScript!)));
            changes.Add(new(RecordFile, FileChangeKind.Add, "The record of this conversion: source, options and every generated file with its SHA-256 (written last).", null, "(written when the files above are written)"));
            if (previous is not null)
            {
                var now = changes.Select(c => c.RelativePath).ToHashSet(StringComparer.Ordinal);
                foreach (var stale in previous.Files.Where(f => !now.Contains(f.Path) && f.Path != RecordFile))
                    changes.Add(new(stale.Path, FileChangeKind.Remove, "Generated by the earlier conversion and no longer needed with these options.", null, ""));
            }
        }
        return new DesktopConversionPlan
        {
            ProjectDirectory = project.Directory, ProjectName = project.DisplayName, Options = opts, Findings = findings, Changes = changes, Mode = mode, Eligibility = eligibility,
            FrontendBuildScript = buildScript, OutputDirectory = outDir, BasePath = basePath, PreviousRecord = previous, SeedLockFile = seed,
            PublicEnvironment = publicEnv, OmittedEnvironment = omittedEnv, RemoteHosts = hosts.ToList(), IconSummary = iconSummary,
        };
    }

    public static DesktopConversionOptions DefaultOptions(NodeProjectInfo project, string? projectDir = null)
    {
        var title = projectDir is not null && File.Exists(Path.Combine(projectDir, "index.html")) && TitleRegex().Match(File.ReadAllText(Path.Combine(projectDir, "index.html"))) is { Success: true } t
            ? System.Net.WebUtility.HtmlDecode(t.Groups["t"].Value.Trim()) : null;
        var name = project.ProductName ?? (title is { Length: > 0 and < 60 } ? title.Split(" — ")[0].Split(" | ")[0].Trim() : project.Name);
        var slug = Regex.Replace(project.Name.ToLowerInvariant().Replace("@", "").Replace('/', '-'), "[^a-z0-9-]", "-").Trim('-');
        if (slug.Length == 0) slug = "app";
        var hcg = (project.Name + " " + name).Contains("hcg", StringComparison.OrdinalIgnoreCase);
        return new DesktopConversionOptions(hcg ? $"com.hakmicapitalgroup.{slug}" : $"app.desktop.{slug}", name)
        {
            IconPath = projectDir is null ? null : DesktopIcon.FindInProject(projectDir),
        };
    }

    private static DesktopConversionPlan Blocked(NodeProjectInfo project, DesktopConversionOptions? options, string area, string message) => new()
    {
        ProjectDirectory = project.Directory, ProjectName = project.DisplayName, Options = options ?? new DesktopConversionOptions("app.desktop.app", project.DisplayName),
        Findings = [new(ConversionFindingLevel.Blocker, area, message, project.ManifestPath)], Changes = [],
    };

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Writes the desktop project into an empty <paramref name="destination"/>: a copy of the web project (without
    /// node_modules, build output, VCS data or .env files), with HCG's earlier output (if any) removed, plus the planned
    /// changes; then resolves package-lock.json without running any script and checks that the chosen Node.js satisfies
    /// what the resolved build tools require. Writes the record last.
    /// </summary>
    public static async Task<DesktopConversionResult> ApplyAsync(DesktopConversionPlan plan, string sourceRoot, string destination, NodeInstallation node,
        IProcessRunner runner, IReadOnlyDictionary<string, string> environment, Action<string>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        void Log(string line) { log.Add(line); progress?.Invoke(line); }
        if (!plan.CanApply) return new(false, destination, log, "The plan has blockers: " + string.Join(" ", plan.Blockers.Select(b => b.Message)));
        if (node.NpmCli is null) return new(false, destination, log, $"npm was not found next to Node.js {node.Version} ({node.NodePath}).");
        if (NodeVersionRange.Check(node.Version, ElectronNodeRequirement) == NodeVersionRange.Result.NotSatisfied)
            return new(false, destination, log, $"Electron {ElectronVersion} requires Node.js {ElectronNodeRequirement} to install; the selected Node.js is {node.Version}. Install a newer Node.js (Toolchains page), then try again.");
        var source = ProjectRoot(sourceRoot, plan.ProjectDirectory);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            return new(false, destination, log, $"{destination} is not empty.");
        Directory.CreateDirectory(destination);
        Log($"Copying {plan.ProjectName} into the desktop project {destination} (the imported source is not changed)…");
        var (copied, fingerprint) = CopyProject(source, destination, plan.OutputDirectory, ct);
        Log($"Copied {copied:N0} files (node_modules, {plan.OutputDirectory}/, release/, VCS data and .env files left out). Source fingerprint {fingerprint[..16]}…");

        if (plan.PreviousRecord is { } previous)
        {
            foreach (var f in previous.Files.Where(f => f.Path is not "package-lock.json"))
            {
                var full = Path.Combine(destination, f.Path.Replace('/', Path.DirectorySeparatorChar));
                if (PathGuard.IsWithin(destination, full) && File.Exists(full)) File.Delete(full);
            }
            Log($"Removed the {previous.Files.Count} files of the earlier conversion ({previous.CreatedAt:yyyy-MM-dd}) before writing the new ones.");
        }

        var written = new List<DesktopConversionRecordFile>();
        foreach (var change in plan.Changes.Where(c => c.After != WrittenByNpm && c.RelativePath != RecordFile))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(destination, change.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathGuard.IsWithin(destination, path)) return new(false, destination, log, $"{change.RelativePath} is outside the desktop project.");
            if (change.Kind == FileChangeKind.Remove)
            {
                if (File.Exists(path)) File.Delete(path);
                Log($"Removed {change.RelativePath}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (change.CopyFrom is { } from) File.Copy(from, path, overwrite: true);
            else await File.WriteAllTextAsync(path, change.After, new UTF8Encoding(false), ct).ConfigureAwait(false);
            written.Add(new(change.RelativePath, Sha256File(path)));
            Log($"{(change.Kind == FileChangeKind.Add ? "Added" : "Updated")} {change.RelativePath}");
        }

        if (plan.SeedLockFile is { } seed)
        {
            var seedPath = Path.Combine(sourceRoot, seed.Replace('/', Path.DirectorySeparatorChar));
            var seeded = SeedLockfile(await File.ReadAllTextAsync(seedPath, ct).ConfigureAwait(false), plan.ProjectDirectory,
                plan.Changes.First(c => c.RelativePath == "package.json").After);
            await File.WriteAllTextAsync(Path.Combine(destination, "package-lock.json"), seeded, new UTF8Encoding(false), ct).ConfigureAwait(false);
            Log($"Started package-lock.json from the versions pinned in {seed}.");
        }
        Log($"Resolving package-lock.json with npm {node.NpmVersion} (npm install --package-lock-only --ignore-scripts)…");
        var result = await runner.RunAsync(new ProcessSpec
        {
            Executable = node.NodePath,
            Arguments = [node.NpmCli, "install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund"],
            WorkingDirectory = destination,
            Environment = ElectronBuildPlanner.Environment(environment, node),
            Timeout = TimeSpan.FromMinutes(10),
        }, (_, line) => { if (line.Length > 0) Log("  " + Security.SecretRedactor.Redact(line)); }, ct).ConfigureAwait(false);
        if (result.Cancelled || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (!result.Succeeded)
            return new(false, destination, log, result.TimedOut ? "npm did not finish within 10 minutes." :
                $"npm could not resolve the desktop dependencies (exit code {result.ExitCode}). Check the network connection and the project's package.json; nothing was built.");
        var lockPath = Path.Combine(destination, "package-lock.json");
        if (!File.Exists(lockPath)) return new(false, destination, log, "npm finished but wrote no package-lock.json.");

        // The build tools as resolved: does the chosen Node.js satisfy them? (npm only warns; the build would fail later.)
        var engines = LockfileEngines(await File.ReadAllTextAsync(lockPath, ct).ConfigureAwait(false), ["vite", "electron", "electron-builder"]);
        var unmet = engines.Where(e => NodeVersionRange.Check(node.Version, e.Value.Range) == NodeVersionRange.Result.NotSatisfied).ToList();
        foreach (var (name, (version, range)) in engines) Log($"  {name} {version} requires Node.js {range}");
        if (unmet.Count > 0)
            return new(false, destination, log, $"Node.js {node.Version} does not satisfy {string.Join(", ", unmet.Select(u => $"{u.Key} {u.Value.Version} (needs {u.Value.Range})"))}. Install a matching Node.js (Toolchains page); nothing was built.");
        written.Add(new("package-lock.json", Sha256File(lockPath)));

        var record = new DesktopConversionRecord
        {
            GeneratorVersion = HostInfo.ProductVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Mode = plan.Mode,
            Source = plan.Options.Source,
            SourceFingerprint = fingerprint,
            AppId = plan.Options.AppId,
            ProductName = plan.Options.ProductName,
            MacTargets = plan.Options.MacTargets,
            MacArchs = plan.Options.MacArchs,
            MacIdentity = plan.Options.MacIdentity,
            Icon = plan.Options.IconPath is null ? null : Path.GetFileName(plan.Options.IconPath),
            ElectronVersion = ElectronVersion,
            ElectronBuilderVersion = ElectronBuilderVersion,
            PublicEnvironment = plan.PublicEnvironment,
            Files = written,
            PackageJsonBefore = plan.PreviousRecord?.PackageJsonBefore ?? plan.Changes.First(c => c.RelativePath == "package.json").Before ?? "",
        };
        await File.WriteAllTextAsync(Path.Combine(destination, RecordFile.Replace('/', Path.DirectorySeparatorChar)), SerializeRecord(record), new UTF8Encoding(false), ct).ConfigureAwait(false);
        Log($"Wrote {RecordFile}: {written.Count} generated files recorded.");
        Log("The desktop project is ready. Its build runs: install (npm ci), compile the web app (npm run desktop:build), package (electron-builder), verify.");
        return new(true, destination, log, null) { Record = record };
    }

    // ------------------------------------------------------------------ record

    private static readonly JsonSerializerOptions RecordJson = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SerializeRecord(DesktopConversionRecord record) => JsonSerializer.Serialize(record, RecordJson) + "\n";

    public static DesktopConversionRecord? ReadRecord(string path, out string? problem)
    {
        problem = null;
        try
        {
            var r = JsonSerializer.Deserialize<DesktopConversionRecord>(File.ReadAllText(path), RecordJson);
            if (r is null || r.Format != 1 || r.PackageJsonBefore.Length == 0) { problem = "unknown format"; return null; }
            if (r.Files.Any(f => f.Path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(f.Path))) { problem = "a recorded path is outside the project"; return null; }
            return r;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { problem = ex.Message; return null; }
    }

    // ------------------------------------------------------------------ lockfile

    /// <summary>
    /// A standalone package-lock.json for one member of an npm workspace, from the monorepo's lockfile: the hoisted
    /// packages, with the member's own nested packages taking their place, and without the other members or workspace
    /// links. npm install --package-lock-only then drops what the member does not use and keeps every pinned version that
    /// still satisfies package.json.
    /// </summary>
    public static string SeedLockfile(string rootLockText, string memberDirectory, string memberPackageJson)
    {
        var root = JsonNode.Parse(rootLockText) as JsonObject ?? throw new BuildValidationException("The monorepo lockfile is not a JSON object.");
        var packages = root["packages"] as JsonObject ?? throw new BuildValidationException("The monorepo lockfile has no \"packages\" section (lockfileVersion 2 or later is needed).");
        var member = JsonNode.Parse(memberPackageJson) as JsonObject ?? new JsonObject();
        var prefix = memberDirectory.TrimEnd('/') + "/node_modules/";
        var result = new JsonObject();
        var self = new JsonObject();
        foreach (var key in new[] { "name", "version", "license", "dependencies", "devDependencies", "optionalDependencies", "peerDependencies", "engines" })
            if (member[key] is { } v) self[key] = v.DeepClone();
        result[""] = self;
        foreach (var (key, value) in packages)
        {
            if (value is not JsonObject entry || entry["link"]?.GetValue<bool>() == true) continue;
            if (key.StartsWith("node_modules/", StringComparison.Ordinal) && !result.ContainsKey(key)) result[key] = entry.DeepClone();
        }
        foreach (var (key, value) in packages)
            if (key.StartsWith(prefix, StringComparison.Ordinal) && value is JsonObject entry && entry["link"]?.GetValue<bool>() != true)
                result["node_modules/" + key[prefix.Length..]] = entry.DeepClone();
        var lockJson = new JsonObject
        {
            ["name"] = member["name"]?.DeepClone(),
            ["version"] = member["version"]?.DeepClone(),
            ["lockfileVersion"] = 3,
            ["requires"] = true,
            ["packages"] = result,
        };
        return lockJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>engines.node of the named packages as resolved in a package-lock.json (top-level installs only).</summary>
    public static IReadOnlyDictionary<string, (string Version, string Range)> LockfileEngines(string lockText, IEnumerable<string> names)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (JsonNode.Parse(lockText)?["packages"] is not JsonObject packages) return result;
        foreach (var name in names)
            if (packages["node_modules/" + name] is JsonObject e && e["engines"]?["node"]?.GetValue<string>() is { } range)
                result[name] = (e["version"]?.GetValue<string>() ?? "?", range);
        return result;
    }

    // ------------------------------------------------------------------ environment

    /// <summary>
    /// The VITE_ settings a production web build of this project would embed (Vite's production-mode .env files, later
    /// files winning), without names that look like credentials. Only VITE_ names: anything else is server-side.
    /// </summary>
    public static (IReadOnlyList<string> Kept, IReadOnlyList<string> Omitted, IReadOnlyList<string> Sources, string FileText) PublicEnvironment(string projectDir, bool enabled)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new List<string>();
        var omitted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in ProductionEnvFiles)
        {
            var full = Path.Combine(projectDir, file);
            if (!File.Exists(full)) continue;
            var any = false;
            foreach (var raw in File.ReadLines(full))
            {
                var m = DotEnvLineRegex().Match(raw);
                if (!m.Success) continue;
                var name = m.Groups["k"].Value;
                if (!name.StartsWith("VITE_", StringComparison.Ordinal)) continue;
                if (CredentialNameRegex().IsMatch(name)) { omitted.Add(name); continue; }
                var value = m.Groups["v"].Value.Trim();
                if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
                else if (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0) value = value[..hash].TrimEnd();
                values[name] = value;
                any = true;
            }
            if (any) sources.Add(file);
        }
        if (!enabled) return ([], omitted.ToList(), sources, "");
        var kept = values.Keys.Order(StringComparer.Ordinal).ToList();
        var text = new StringBuilder("# Generated by HCG Executable Maker: the public VITE_ settings of the web build (" + string.Join(", ", sources) + ").\n# Every VITE_ value is embedded in the app and readable by its users. Never put credentials here.\n");
        foreach (var k in kept) text.Append(k).Append('=').Append(JsonSerializer.Serialize(values[k])).Append('\n');
        return (kept, omitted.ToList(), sources, text.ToString());
    }

    // ------------------------------------------------------------------ copy

    private static (int Count, string Fingerprint) CopyProject(string source, string destination, string outDir, CancellationToken ct)
    {
        var outDirFull = Path.GetFullPath(Path.Combine(source, outDir));
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
            CopyEntry(entry, destination, "", outDirFull, hashes, ct);
        var all = Encoding.UTF8.GetBytes(string.Join("\n", hashes.Select(h => h.Key + " " + h.Value)));
        return (hashes.Count, Convert.ToHexStringLower(SHA256.HashData(all)));
    }

    private static void CopyEntry(FileSystemInfo entry, string destDir, string rel, string outDirFull, IDictionary<string, string> hashes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entry.LinkTarget is not null) return; // symbolic links are never followed or copied
        var target = Path.Combine(destDir, entry.Name);
        var relative = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
        if (entry is DirectoryInfo d)
        {
            if (CopyExcludedDirectories.Contains(d.Name) || string.Equals(Path.GetFullPath(d.FullName), outDirFull, StringComparison.Ordinal)) return;
            Directory.CreateDirectory(target);
            foreach (var e in d.EnumerateFileSystemInfos()) CopyEntry(e, target, relative, outDirFull, hashes, ct);
            return;
        }
        if (IsEnvFile(entry.Name) || entry.Name == ".DS_Store") return;
        File.Copy(entry.FullName, target);
        hashes[relative] = Sha256File(target);
    }

    // ------------------------------------------------------------------ generated files

    /// <summary>The content security policy the shell sends with every page of the app.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; font-src 'self' data:; " +
        "media-src 'self' blob: https:; connect-src 'self' https: wss:; frame-src https:; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; form-action 'self' https:";

    private static string MainScript(DesktopConversionOptions o, string outDir, string basePath) => $$"""
        // Generated by HCG Executable Maker: the desktop shell for {{Js(o.ProductName)}}.
        // It shows the production build ({{outDir}}/) from inside the app over a private app:// scheme. There is no web
        // server, development server or network for the app's own files, and no Node.js access for the page: the window
        // uses context isolation, the renderer sandbox and webSecurity, and has no Node integration and no preload script.
        // Review and change it like any other project file (HCG will then not overwrite it; see hcg-desktop/README.md).
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
        // Sent with every page: scripts only from the app itself; remote services over HTTPS stay reachable.
        const CSP = {{Js(ContentSecurityPolicy)}};

        // A private, standard and secure scheme: absolute paths (/assets/…), lazy-loaded chunks and history routes resolve inside the app.
        protocol.registerSchemesAsPrivileged([
          { scheme: SCHEME, privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true } },
        ]);

        function resolveFile(urlPath) {
          let p;
          try { p = decodeURIComponent(urlPath); } catch { return null; }
          if (p.includes('\0')) return null;
          if (BASE !== '/' && p.startsWith(BASE)) p = '/' + p.slice(BASE.length);
          const file = path.normalize(path.join(ROOT, p));
          if (file !== ROOT && !file.startsWith(ROOT + path.sep)) return null; // never outside the build
          try { if (fs.statSync(file).isFile()) return file; } catch { /* not a file */ }
          // Paths without a file extension are app routes: answer with index.html, as the web host's rewrite does.
          return path.extname(p) === '' ? path.join(ROOT, 'index.html') : null;
        }

        function isAppUrl(url) {
          try { const u = new URL(url); return u.protocol === `${SCHEME}:` && u.host === HOST; } catch { return false; }
        }

        // Only https and mailto links leave the app, and they open in the user's browser or mail program.
        function openOutside(url) {
          let u;
          try { u = new URL(url); } catch { return; }
          if (u.protocol === 'https:' || u.protocol === 'mailto:') shell.openExternal(u.toString());
          else console.warn(`[desktop] refused to open ${u.protocol} link`);
        }

        function guard(contents) {
          contents.setWindowOpenHandler(({ url }) => { if (!isAppUrl(url)) openOutside(url); return { action: 'deny' }; });
          contents.on('will-navigate', (event, url) => { if (!isAppUrl(url)) { event.preventDefault(); openOutside(url); } });
          contents.on('will-redirect', (event, url) => { if (!isAppUrl(url)) { event.preventDefault(); } });
          contents.on('will-attach-webview', (event) => event.preventDefault());
        }

        function errorPage(title, detail) {
          const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
          return 'data:text/html;charset=utf-8,' + encodeURIComponent(`<!doctype html><meta charset="utf-8"><title>${esc(PRODUCT)}</title>` +
            `<body style="font:15px system-ui;margin:48px;color:#222"><h1 style="font-size:20px">${esc(title)}</h1><p>${esc(detail)}</p></body>`);
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
              nodeIntegrationInSubFrames: false,
              webSecurity: true,
              allowRunningInsecureContent: false,
            },
          });
          win.once('ready-to-show', () => win.show());
          win.webContents.on('did-fail-load', (_event, code, description, url, isMainFrame) => {
            if (!isMainFrame || code === -3 /* aborted by a navigation */) return;
            console.error(`[desktop] ${url} failed to load: ${description} (${code})`);
            win.loadURL(errorPage(`${PRODUCT} could not open its start page`, `${description} (${code}). The app's files may be damaged: reinstall the app.`));
          });
          win.loadURL(ORIGIN + BASE);
          return win;
        }

        if (!app.requestSingleInstanceLock()) {
          app.quit();
        } else {
          app.on('second-instance', () => {
            const [win] = BrowserWindow.getAllWindows();
            if (win) { if (win.isMinimized()) win.restore(); win.focus(); }
          });

          app.on('web-contents-created', (_event, contents) => guard(contents));

          app.whenReady().then(() => {
            protocol.handle(SCHEME, async (request) => {
              const url = new URL(request.url);
              if (url.host !== HOST) return new Response('Not found', { status: 404 });
              const file = resolveFile(url.pathname);
              if (!file) return new Response('Not found', { status: 404 });
              const response = await net.fetch(pathToFileURL(file).toString());
              if (!file.endsWith('.html')) return response;
              const headers = new Headers(response.headers);
              headers.set('Content-Security-Policy', CSP);
              headers.set('X-Content-Type-Options', 'nosniff');
              return new Response(response.body, { status: response.status, headers });
            });
            session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
            session.defaultSession.setPermissionCheckHandler(() => false);
            // Remote services stay remote: failures are logged with the host (the page shows its own message).
            session.defaultSession.webRequest.onErrorOccurred({ urls: ['https://*/*', 'wss://*/*'] }, (details) => {
              console.warn(`[desktop] ${new URL(details.url).host}: ${details.error}`);
            });
            createWindow();
            app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
          });

          app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
        }

        """;

    private static string VerifyBuildScript() => """
        // Generated by HCG Executable Maker. Runs after the production build (npm run desktop:build) and stops the desktop
        // build when the output is missing, references assets that do not exist, or still depends on the development server.
        'use strict';
        const fs = require('node:fs');
        const path = require('node:path');

        const dir = path.resolve(__dirname, '..', process.argv[2] || 'dist');
        const base = process.argv[3] || '/';
        const rel = (p) => path.relative(process.cwd(), p) || '.';
        const index = path.join(dir, 'index.html');
        if (!fs.existsSync(index)) {
          console.error(`HCG desktop: the production build did not produce ${rel(index)}.`);
          process.exit(1);
        }
        const html = fs.readFileSync(index, 'utf8');
        const devServer = ['/@vite/client', '/@react-refresh', 'localhost:5173', '127.0.0.1:5173'].filter((s) => html.includes(s));
        if (devServer.length > 0) {
          console.error(`HCG desktop: ${rel(index)} still loads the development server (${devServer.join(', ')}).`);
          process.exit(1);
        }
        // Every script, stylesheet, preload and icon index.html references must be part of the build.
        const missing = [];
        let count = 0;
        for (const m of html.matchAll(/<(?:script|link)\b[^>]*?\s(?:src|href)=["']([^"'#?]+)/gi)) {
          const ref = m[1];
          if (/^[a-z][a-z0-9+.-]*:/i.test(ref) || ref.startsWith('//')) continue; // remote (https://…, data:)
          let p = ref.startsWith(base) ? ref.slice(base.length) : ref.replace(/^\.?\//, '');
          count++;
          if (!fs.existsSync(path.join(dir, decodeURIComponent(p)))) missing.push(ref);
        }
        if (missing.length > 0) {
          console.error(`HCG desktop: index.html references files that are not in the build: ${missing.join(', ')}`);
          process.exit(1);
        }
        let files = 0;
        const walk = (d) => { for (const e of fs.readdirSync(d, { withFileTypes: true })) e.isDirectory() ? walk(path.join(d, e.name)) : files++; };
        walk(dir);
        console.log(`HCG desktop: production build ready in ${rel(dir)}/ (${files} files; index.html and its ${count} referenced assets present).`);

        """;

    private static string BuilderConfig(DesktopConversionOptions o, string outDir, string? icon, DesktopConversionMode mode)
    {
        var sb = new StringBuilder();
        sb.Append("# Generated by HCG Executable Maker for the desktop app (see hcg-desktop/README.md).\n");
        sb.Append($"appId: {Yaml(o.AppId)}\nproductName: {Yaml(o.ProductName)}\n");
        sb.Append($"directories:\n  output: release\n  buildResources: {ShellDirectory}\n");
        if (mode == DesktopConversionMode.WrapBrowserApp)
            sb.Append($"files:\n  - {Yaml(outDir + "/**/*")}\n  - {Yaml(MainEntry)}\n  - package.json\n");
        sb.Append("asar: true\npublish: null\n");
        sb.Append("mac:\n  category: public.app-category.productivity\n  target:\n");
        foreach (var t in o.MacTargets) sb.Append($"    - target: {t}\n      arch: [{string.Join(", ", o.MacArchs)}]\n");
        if (icon is not null) sb.Append($"  icon: {Yaml(icon)}\n");
        if (o.MacIdentity is { } id)
            sb.Append($"  identity: {Yaml(id)}\n  hardenedRuntime: true\n  gatekeeperAssess: false\n  entitlements: {Yaml(EntitlementsFile)}\n  entitlementsInherit: {Yaml(EntitlementsFile)}\n  notarize: false\n");
        else
            sb.Append("  # Ad-hoc signature: runs on this Mac only. It is not signed for distribution and not notarized.\n  identity: \"-\"\n");
        sb.Append("dmg:\n  writeUpdateInfo: false\n");
        sb.Append("linux:\n  target:\n    - dir\n");
        if (icon is not null && icon.EndsWith(".png", StringComparison.Ordinal)) sb.Append($"  icon: {Yaml(icon)}\n");
        return sb.ToString();
    }

    private static string Entitlements() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <!-- Generated by HCG Executable Maker: the hardened runtime exceptions an Electron app needs (V8's JIT). Nothing else. -->
        <plist version="1.0">
        <dict>
          <key>com.apple.security.cs.allow-jit</key>
          <true/>
        </dict>
        </plist>

        """;

    private static string PackageJson(JsonObject original, string buildScript, string outDir, string basePath, DesktopConversionMode mode)
    {
        var pkg = (JsonObject)original.DeepClone();
        var scripts = pkg["scripts"] as JsonObject ?? new JsonObject();
        pkg["scripts"] = scripts;
        scripts[BuildScriptName] = $"npm run {buildScript} && node {VerifyScript} {outDir} {basePath}";
        // The targets, architectures and signing come from electron-builder.yml; HCG's build passes its own choice explicitly.
        scripts[PackageScriptName] = $"npm run {BuildScriptName} && electron-builder --mac --publish never";
        scripts[LinuxPackageScriptName] = $"npm run {BuildScriptName} && electron-builder --linux dir --publish never";
        var dev = pkg["devDependencies"] as JsonObject ?? new JsonObject();
        if (mode == DesktopConversionMode.WrapBrowserApp)
        {
            pkg["main"] = MainEntry;
            if (pkg["dependencies"] is JsonObject runtime)
            {
                foreach (var (name, value) in runtime.ToList())
                    if (!dev.ContainsKey(name)) dev[name] = value?.DeepClone();
                pkg.Remove("dependencies");
            }
            dev["electron"] = ElectronVersion;
        }
        dev["electron-builder"] = ElectronBuilderVersion;
        pkg["devDependencies"] = new JsonObject(dev.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => KeyValuePair.Create(d.Key, d.Value?.DeepClone())));
        return pkg.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private static string Readme(DesktopConversionOptions o, DesktopConversionMode mode, string outDir, string buildScript) => $"""
        # {o.ProductName} — desktop project

        Generated by HCG Executable Maker from the web app{(o.Source is { } s ? $" in `{(s.WebDirectory.Length == 0 ? "(root)" : s.WebDirectory)}` of “{s.ProjectName}”" : "")}.
        The website itself is unchanged: its own `npm run {buildScript}`, scripts and deployment files are kept as they were.

        | File | Purpose |
        | --- | --- |
        {(mode == DesktopConversionMode.WrapBrowserApp ? $"| `{MainEntry}` | Electron main process: serves `{outDir}/` over `app://bundle` (assets, lazy chunks, history routes, reloads), secured window, CSP, link handling. |\n" : "")}| `{VerifyScript}` | Runs after the web build; stops the desktop build if `{outDir}/index.html` or an asset it references is missing, or the build loads the dev server. |
        | `electron-builder.yml` | App id `{o.AppId}`, name, icon, macOS {string.Join(" + ", o.MacTargets)} for {string.Join(", ", o.MacArchs)}, {(o.MacIdentity is null ? "ad-hoc signature" : "Developer ID signature")}. |
        | `package.json` | `desktop:build`, `desktop:dist:mac`, `desktop:dist:linux` scripts; electron-builder{(mode == DesktopConversionMode.WrapBrowserApp ? ", electron and the `main` entry; runtime dependencies moved to devDependencies (Vite bundles them)" : "")}. |
        | `package-lock.json` | Resolved with `npm install --package-lock-only --ignore-scripts`. |
        | `{RecordFile}` | Source, options and the SHA-256 of every generated file. |

        Build: `npm ci && npm run {PackageScriptName}` (or build it in HCG Executable Maker).
        Signing: {(o.MacIdentity is null ? "ad-hoc only — the app runs on the Mac that built it; it is not signed for distribution and not notarized." : $"Developer ID Application: {o.MacIdentity}; notarize separately (xcrun notarytool).")}

        Converting the web app again in HCG replaces these files. A generated file you edit is never overwritten:
        HCG stops and names it instead.

        """;

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

    /// <summary>.env files are never copied as they are (they may hold server secrets); examples and templates are.</summary>
    private static bool IsEnvFile(string name) =>
        name.StartsWith(".env", StringComparison.Ordinal) && !name.EndsWith(".example", StringComparison.Ordinal)
        && !name.EndsWith(".sample", StringComparison.Ordinal) && !name.EndsWith(".template", StringComparison.Ordinal);

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

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
    [GeneratedRegex(@"['""`]https://(?<h>[a-zA-Z0-9.-]+)(?::\d+)?[/'""`]")] private static partial Regex HttpsHostRegex();
    [GeneratedRegex(@"\b(?:lazy|import)\s*\(\s*(?:\(\s*\)\s*=>\s*import\s*\()?\s*['""`]\.{1,2}/")] private static partial Regex LazyImportRegex();
    [GeneratedRegex(@"^\s*(?:export\s+)?(?<k>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<v>.*)$")] private static partial Regex DotEnvLineRegex();
    [GeneratedRegex(@"SECRET|TOKEN|PASSWORD|PASSWD|PRIVATE|SERVICE_ROLE|CREDENTIAL|API_KEY|ACCESS_KEY", RegexOptions.IgnoreCase)] private static partial Regex CredentialNameRegex();
    [GeneratedRegex(@"(?:contextIsolation|sandbox|webSecurity)\s*:\s*false|nodeIntegration\s*:\s*true")] private static partial Regex InsecurePreferenceRegex();
    [GeneratedRegex(@"loadURL\(\s*['""`]https?://(?:localhost|127\.0\.0\.1)[^'""`]*['""`]")] private static partial Regex DevServerLoadRegex();
}
