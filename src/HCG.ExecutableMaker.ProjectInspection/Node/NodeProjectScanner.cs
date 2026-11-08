using System.Text.Json;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Node;

/// <summary>
/// Finds package.json projects in a workspace and classifies each one from its files only: Electron desktop app,
/// browser app, Node service/CLI or library. Never runs npm, project scripts or JavaScript configuration files.
/// </summary>
public static class NodeProjectScanner
{
    /// <summary>Folders that hold build output or vendored copies, never a project to build.</summary>
    private static readonly HashSet<string> OutputFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "dist", "release", "out", "build", ".next", ".nuxt", ".output", ".svelte-kit", ".vite", ".turbo", ".cache",
        "coverage", "desktop-dist", "dist-electron", "bower_components", "vendor", ".yarn", ".pnpm-store",
    };

    private static readonly (string Package, string Label)[] WebFrameworks =
    [
        ("vite", "Vite"), ("next", "Next.js"), ("react-scripts", "Create React App"), ("@angular/core", "Angular"),
        ("nuxt", "Nuxt"), ("@sveltejs/kit", "SvelteKit"), ("svelte", "Svelte"), ("vue", "Vue"), ("astro", "Astro"),
        ("@remix-run/react", "Remix"), ("parcel", "Parcel"), ("webpack", "webpack"), ("react-dom", "React"), ("solid-js", "Solid"),
    ];

    private static readonly (string Package, string Label)[] ServerFrameworks =
    [
        ("express", "Express"), ("fastify", "Fastify"), ("koa", "Koa"), ("@nestjs/core", "NestJS"), ("@hapi/hapi", "hapi"),
        ("hono", "Hono"), ("restify", "restify"),
    ];

    public static IReadOnlyList<string> FindManifests(SourceIndex index) =>
        index.Named("package.json")
            .Where(f => f.Split('/').SkipLast(1).All(seg => !OutputFolders.Contains(seg)))
            .OrderBy(SourceIndex.Depth).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();

    public static NodeAnalysis Scan(SourceIndex index)
    {
        var projects = new List<NodeProjectInfo>();
        foreach (var manifest in FindManifests(index))
        {
            var info = ScanOne(index, manifest);
            if (info is not null) projects.Add(info);
        }
        // Drop packages that live inside another project's electron-builder output folder (unpacked app copies).
        projects = projects.Where(p => !projects.Any(o => o != p && o.BuilderConfig is { } c &&
            p.Directory.StartsWith(Join(o.Directory, c.OutputDirectory) + "/", StringComparison.Ordinal))).ToList();
        return new NodeAnalysis { Projects = projects };
    }

    public static NodeProjectInfo? ScanOne(SourceIndex index, string manifestPath)
    {
        JsonDocument doc;
        try
        {
            var full = index.FullPath(manifestPath);
            if (new FileInfo(full).Length > 2_000_000) return null;
            doc = JsonDocument.Parse(File.ReadAllText(full), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var dir = manifestPath.Contains('/') ? manifestPath[..manifestPath.LastIndexOf('/')] : "";
            var projectDir = index.FullPath(dir.Length == 0 ? "." : dir);
            bool Exists(string rel) => File.Exists(Path.Combine(projectDir, rel.Replace('/', Path.DirectorySeparatorChar)));

            var deps = new Dictionary<string, (string Version, string Section)>(StringComparer.Ordinal);
            foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
                if (root.TryGetProperty(section, out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var p in d.EnumerateObject())
                        deps.TryAdd(p.Name, (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "", section));
            var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("scripts", out var sc) && sc.ValueKind == JsonValueKind.Object)
                foreach (var p in sc.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) scripts[p.Name] = p.Value.GetString() ?? "";

            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string? Engine(string name) => root.TryGetProperty("engines", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var evidence = new List<string>();
            var missing = new List<string>();
            var notes = new List<string>();
            var frameworks = new List<string>();

            // ---- Package manager and lockfile ----
            var (pm, lockFile) = DetectPackageManager(Exists, Str("packageManager"));

            // ---- Electron facts ----
            var electron = deps.TryGetValue("electron", out var e1) ? e1 : deps.TryGetValue("electron-nightly", out var e2) ? e2 : default;
            var hasElectron = electron.Version is not null;
            if (hasElectron) evidence.Add($"{Singular(electron.Section)} electron {electron.Version}");

            var packager = ElectronPackagerKind.None;
            string? packagerVersion = null;
            if (deps.TryGetValue("electron-builder", out var eb)) { packager = ElectronPackagerKind.ElectronBuilder; packagerVersion = eb.Version; }
            else if (deps.TryGetValue("@electron-forge/cli", out var ef)) { packager = ElectronPackagerKind.ElectronForge; packagerVersion = ef.Version; }
            else if (deps.TryGetValue("@electron/packager", out var ep) || deps.TryGetValue("electron-packager", out ep)) { packager = ElectronPackagerKind.ElectronPackager; packagerVersion = ep.Version; }

            var packagingScripts = ElectronPackagingScripts.Find(scripts);
            ElectronBuilderConfig? config = null;
            if (packager == ElectronPackagerKind.ElectronBuilder || packagingScripts.Count > 0 || ElectronBuilderConfigReader.ConfigFileNames.Any(Exists))
            {
                JsonElement? buildSection = root.TryGetProperty("build", out var b) ? b.Clone() : null;
                var explicitConfig = packagingScripts.Select(s => s.ConfigPath).FirstOrDefault(c => c is not null);
                var read = ElectronBuilderConfigReader.Read(projectDir, explicitConfig, buildSection);
                config = read.Config;
                if (read.Problem is not null) missing.Add(read.Problem);
                if (config is not null)
                {
                    evidence.Add($"electron-builder configuration: {config.Source}");
                    if (config.IsScript)
                        notes.Add($"{config.Source} is a script; HCG does not run it during inspection, so its settings (product name, targets) are confirmed by the build. HCG directs electron-builder's output into its own build folder.");
                }
                else if (packager == ElectronPackagerKind.ElectronBuilder && read.Problem is null)
                    notes.Add("electron-builder has no configuration file or package.json \"build\" section; its defaults apply (output folder dist, app id com.electron.<name>).");
            }

            var mainDeclared = Str("main");
            var main = config?.ExtraMetadataMain ?? mainDeclared ?? (hasElectron ? "index.js" : null);
            var mainExists = main is not null && Exists(main.TrimStart('.', '/'));
            var mainBuilt = false;

            // ---- Web and server signals ----
            foreach (var (pkg, label) in WebFrameworks) if (deps.ContainsKey(pkg)) frameworks.Add(label);
            var servers = ServerFrameworks.Where(s => deps.ContainsKey(s.Package)).Select(s => s.Label).ToList();
            frameworks.AddRange(servers);
            var webConfig = new[] { "vite.config.ts", "vite.config.js", "vite.config.mjs", "vite.config.mts", "next.config.js", "next.config.mjs", "next.config.ts", "angular.json", "nuxt.config.ts", "svelte.config.js", "webpack.config.js", "astro.config.mjs" }
                .FirstOrDefault(Exists);
            var isWeb = frameworks.Except(servers).Any() || webConfig is not null || Exists("index.html") || Exists("public/index.html");

            NodeProjectKind kind;
            if (hasElectron)
            {
                kind = NodeProjectKind.ElectronDesktop;
                // ---- What a desktop build still needs ----
                if (mainDeclared is null && config?.ExtraMetadataMain is null)
                {
                    if (mainExists) evidence.Add("main entry index.js (Electron's default)");
                    else missing.Add("package.json has no \"main\" field and there is no index.js: Electron needs \"main\" to point at the main-process script that creates the BrowserWindow.");
                }
                switch (packager)
                {
                    case ElectronPackagerKind.ElectronBuilder:
                        evidence.Add($"packager electron-builder {packagerVersion}");
                        break;
                    case ElectronPackagerKind.ElectronForge:
                        missing.Add($"Packaging is configured with Electron Forge (@electron-forge/cli {packagerVersion}). HCG recognizes Forge but runs only electron-builder in this version. Package with `npm run make`, or add electron-builder with an electron-builder.yml.");
                        break;
                    case ElectronPackagerKind.ElectronPackager:
                        missing.Add($"Packaging is configured with @electron/packager ({packagerVersion}). HCG recognizes it but runs only electron-builder in this version. Add electron-builder with an electron-builder.yml to build here.");
                        break;
                    default:
                        missing.Add("No supported packaging configuration: add electron-builder to devDependencies and describe the app in electron-builder.yml (or a \"build\" section in package.json), for example appId, productName, directories.output and a mac target.");
                        break;
                }
                var usable = packagingScripts.Where(s => s.Problem is null).ToList();
                if (packagingScripts.Count > 0 && usable.Count == 0) missing.Add(packagingScripts[0].Problem!);
                if (main is not null && !mainExists && (mainDeclared is not null || config?.ExtraMetadataMain is not null))
                {
                    var compile = usable.FirstOrDefault(s => s.CompileSteps.Count > 0);
                    if (compile is not null)
                    {
                        mainBuilt = true;
                        evidence.Add($"main entry {main} (built by {string.Join(" && ", compile.CompileSteps.Select(c => c.Display))})");
                    }
                    else missing.Add($"The main entry {main} does not exist and no packaging script builds it. Add a script that compiles it (e.g. \"desktop:build\") and run it before electron-builder (\"desktop:dist\": \"npm run desktop:build && electron-builder\").");
                }
                else if (mainExists) evidence.Add($"main entry {main}");
                if (pm != NodePackageManager.Npm)
                    missing.Add($"The project uses {pm.ToString().ToLowerInvariant()} ({lockFile ?? "packageManager field"}). HCG installs dependencies with npm only in this version, and a different package manager would ignore the project's lockfile.");
                if (lockFile is null)
                    notes.Add("No package-lock.json: dependencies are resolved with `npm install`, so versions may differ from the developer's machine. Commit a lockfile for reproducible builds.");
            }
            else if (isWeb)
            {
                kind = NodeProjectKind.WebApp;
                if (webConfig is not null) evidence.Add(webConfig);
                if (Exists("index.html")) evidence.Add("index.html");
                missing.AddRange(WebOnlyGaps(servers, scripts));
            }
            else if (servers.Count > 0 || root.TryGetProperty("bin", out _) || scripts.ContainsKey("start"))
            {
                kind = NodeProjectKind.NodeService;
                if (servers.Count > 0) evidence.Add("server framework " + string.Join(", ", servers));
                if (root.TryGetProperty("bin", out _)) evidence.Add("\"bin\" entry (command-line tool)");
                if (scripts.TryGetValue("start", out var start)) evidence.Add($"start script: {start}");
                missing.Add("This is a Node.js server or command-line program, not a desktop app: it has no window. HCG packages desktop apps built with Electron; a service is deployed or run with node instead.");
            }
            else
            {
                kind = NodeProjectKind.Library;
                evidence.Add("no application entry, web framework or server framework");
                missing.Add("This package has no application entry (no Electron dependency, web framework, server or start script), so there is nothing to package as an app.");
            }

            return new NodeProjectInfo
            {
                Directory = dir,
                ManifestPath = manifestPath,
                Name = Str("name") ?? (dir.Length == 0 ? new DirectoryInfo(index.Root).Name : dir[(dir.LastIndexOf('/') + 1)..]),
                Version = Str("version"),
                ProductName = config?.ProductName ?? Str("productName"),
                Description = Str("description"),
                Kind = kind,
                Evidence = evidence,
                Frameworks = frameworks.Distinct().ToList(),
                EnginesNode = Engine("node"),
                EnginesNpm = Engine("npm"),
                PackageManagerField = Str("packageManager"),
                PackageManager = pm,
                LockFile = lockFile,
                ElectronVersion = hasElectron ? electron.Version : null,
                MainEntry = main,
                MainEntryExists = mainExists,
                MainEntryBuilt = mainBuilt,
                Packager = packager,
                PackagerVersion = packagerVersion,
                BuilderConfig = config,
                Scripts = scripts,
                PackagingScripts = packagingScripts,
                MissingForDesktop = missing,
                Notes = notes,
            };
        }
    }

    /// <summary>Exactly what a browser-only project lacks before it can become a desktop app.</summary>
    private static IEnumerable<string> WebOnlyGaps(IReadOnlyList<string> servers, IReadOnlyDictionary<string, string> scripts)
    {
        yield return "No Electron dependency: a browser app needs a desktop shell. Add electron to devDependencies.";
        yield return "No Electron main-process entry: package.json \"main\" must point to a script that opens a BrowserWindow and loads the built app (loadFile of the production build, not the development server URL).";
        yield return "No desktop packaging configuration: add electron-builder to devDependencies and an electron-builder.yml (appId, productName, directories.output, files, and a mac target such as zip or dmg).";
        if (scripts.TryGetValue("dev", out var dev) && (dev.Contains("vite", StringComparison.Ordinal) || dev.Contains("next dev", StringComparison.Ordinal) || dev.Contains("webpack serve", StringComparison.Ordinal)))
            yield return $"The app currently runs from a development server (\"dev\": \"{dev}\"). A packaged app cannot depend on it: build the renderer (e.g. vite build) and load the output from disk.";
        if (servers.Count > 0)
            yield return $"The project includes a backend server ({string.Join(", ", servers)}). A desktop app must start it inside the Electron main process (or bundle it) because HCG does not run separate servers.";
    }

    private static (NodePackageManager Manager, string? LockFile) DetectPackageManager(Func<string, bool> exists, string? field)
    {
        string? lockFile = new[] { "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock" }.FirstOrDefault(exists);
        if (field is { Length: > 0 })
        {
            var name = field.Split('@')[0];
            var fromField = name switch { "yarn" => NodePackageManager.Yarn, "pnpm" => NodePackageManager.Pnpm, "bun" => NodePackageManager.Bun, _ => NodePackageManager.Npm };
            return (fromField, lockFile);
        }
        return lockFile switch
        {
            "yarn.lock" => (NodePackageManager.Yarn, lockFile),
            "pnpm-lock.yaml" => (NodePackageManager.Pnpm, lockFile),
            "bun.lockb" or "bun.lock" => (NodePackageManager.Bun, lockFile),
            _ => (NodePackageManager.Npm, lockFile),
        };
    }

    private static string Singular(string section) => section switch
    {
        "dependencies" => "dependency",
        "devDependencies" => "devDependency",
        "optionalDependencies" => "optional dependency",
        _ => "peer dependency",
    };

    public static string Join(string dir, string rel) => dir.Length == 0 ? rel.Replace('\\', '/').TrimEnd('/') : dir + "/" + rel.Replace('\\', '/').TrimEnd('/');
}
