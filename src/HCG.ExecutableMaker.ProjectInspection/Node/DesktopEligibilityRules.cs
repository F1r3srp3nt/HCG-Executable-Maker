using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Node;

/// <summary>
/// Decides, from package.json facts only (nothing runs), what "Create desktop application" can do with a Node.js project:
/// wrap a static Vite + React browser app, add packaging to an Electron app that has none, build an existing Electron
/// app as it is, or explain why the project needs a server or is not supported. Not every React app is a static app:
/// server rendering, a backend in the project, another package manager or a monorepo root are reported, not converted.
/// </summary>
public static partial class DesktopEligibilityRules
{
    /// <summary>Frameworks that render pages on a server (a desktop app has no server).</summary>
    public static readonly (string Package, string Label)[] ServerRendered =
    [
        ("next", "Next.js"), ("nuxt", "Nuxt"), ("@remix-run/react", "Remix"), ("@remix-run/node", "Remix"), ("@sveltejs/kit", "SvelteKit"),
        ("astro", "Astro"), ("@angular/ssr", "Angular SSR"), ("gatsby", "Gatsby"), ("@react-router/dev", "React Router framework mode"),
        ("@tanstack/react-start", "TanStack Start"), ("vike", "Vike"),
    ];

    /// <summary>Server frameworks: as runtime dependencies they mean the project has a backend.</summary>
    public static readonly (string Package, string Label)[] ServerFrameworks =
    [
        ("express", "Express"), ("fastify", "Fastify"), ("koa", "Koa"), ("hono", "Hono"), ("@nestjs/core", "NestJS"), ("@hapi/hapi", "hapi"), ("restify", "restify"),
    ];

    public static DesktopEligibility Classify(NodeProjectInfo p, IReadOnlyCollection<string> siblingPackageNames)
    {
        var deps = p.Dependencies;
        var hasVite = deps.ContainsKey("vite");
        var hasReact = deps.ContainsKey("react") && deps.ContainsKey("react-dom");
        var reasons = new List<string>();

        if (p.Kind == NodeProjectKind.ElectronDesktop)
        {
            if (p.HcgDesktopRecord is not null)
                return new(DesktopConversionCategory.ExistingElectron,
                    $"{p.DisplayName} is a desktop project HCG generated earlier ({p.HcgDesktopRecord}). It is built as it is; to regenerate it, open the original web project and choose Create desktop application (that updates this project instead of adding another).",
                    []);
            var noPackaging = p.Packager == ElectronPackagerKind.None && p.BuilderConfig is null && p.PackagingScripts.Count == 0;
            if (noPackaging && p.MainEntryExists && !p.MainEntryBuilt && p.PackageManager == NodePackageManager.Npm && ViteBuildScript(p) is not null && hasVite)
                return new(DesktopConversionCategory.ElectronWithoutPackaging,
                    $"{p.DisplayName} already has Electron and a main process ({p.MainEntry}) but no packaging. HCG adds electron-builder configuration and desktop build scripts and keeps your main process unchanged.",
                    [$"Main process: {p.MainEntry}", $"Production build: npm run {ViteBuildScript(p)}"]);
            reasons.AddRange(p.MissingForDesktop);
            return new(DesktopConversionCategory.ExistingElectron,
                $"{p.DisplayName} is an Electron app. HCG builds it with its own configuration and generates nothing for it.", reasons);
        }
        if (p.Kind is NodeProjectKind.NodeService or NodeProjectKind.Library)
            return new(DesktopConversionCategory.Unsupported,
                $"{p.DisplayName} is {(p.Kind == NodeProjectKind.NodeService ? "a Node.js server or command-line program" : "a library")}, not a browser app: it has no page to show in a desktop window.", []);

        // ---- Browser app ----
        var ssr = ServerRendered.Where(s => deps.ContainsKey(s.Package)).Select(s => s.Label).Distinct().ToList();
        if (ssr.Count > 0)
            return new(DesktopConversionCategory.ServerDependent,
                $"{p.DisplayName} uses {string.Join(", ", ssr)}, which renders pages on a server. A desktop app has no server, so it cannot be packaged as a static desktop app without additional setup (a static export, or hosting the server separately).",
                ssr.Select(s => $"{s} in package.json").ToList());
        var backend = ServerFrameworks.Where(s => p.RuntimeDependencyNames.Contains(s.Package)).Select(s => s.Label).ToList();
        if (backend.Count > 0)
            return new(DesktopConversionCategory.ServerDependent,
                $"{p.DisplayName} ships a backend ({string.Join(", ", backend)} under \"dependencies\"). Only the browser build can be packaged; the backend would have to be hosted separately and the app pointed at it over HTTPS.",
                backend.Select(b => $"{b} is a runtime dependency").ToList());

        if (p.DeclaresWorkspaces)
            reasons.Add("package.json declares \"workspaces\": this is a monorepo root. Choose the web app inside it (its own folder).");
        if (!hasVite || !hasReact)
            reasons.Add($"Create desktop application supports Vite + React apps in this version. This project has {(hasVite ? "Vite" : "no Vite")} and {(hasReact ? "React" : "no React (react and react-dom)")}" +
                        (p.Frameworks.Count > 0 ? $" (detected: {string.Join(", ", p.Frameworks)})." : "."));
        if (p.PackageManager != NodePackageManager.Npm)
            reasons.Add($"The project uses {p.PackageManager.ToString().ToLowerInvariant()} ({p.LockFile ?? p.PackageManagerField ?? "packageManager field"}). HCG installs with npm only in this version and would ignore that lockfile, so it does not convert the project.");
        if (p.WorkspaceRoot is not null && p.WorkspaceLockFile is { } wl && !wl.EndsWith("package-lock.json", StringComparison.Ordinal))
            reasons.Add($"The monorepo pins dependencies in {wl}, which npm cannot read.");
        foreach (var (name, spec) in deps.Where(d => d.Value.StartsWith("file:", StringComparison.Ordinal) || d.Value.StartsWith("link:", StringComparison.Ordinal) || d.Value.StartsWith("workspace:", StringComparison.Ordinal) || d.Value.StartsWith("portal:", StringComparison.Ordinal)))
            reasons.Add($"Dependency {name} is \"{spec}\", a package outside this folder. The desktop project contains this folder only, so it could not be installed.");
        foreach (var sibling in deps.Keys.Where(siblingPackageNames.Contains))
            reasons.Add($"Dependency {sibling} is another package of this monorepo. The desktop project contains this folder only, so it could not be installed.");
        var build = ViteBuildScript(p);
        if (hasVite && build is null)
            reasons.Add("No package.json script runs \"vite build\". The desktop app needs the production build: add one (\"build\": \"vite build\").");
        if (reasons.Count > 0)
            return new(DesktopConversionCategory.Unsupported, $"{p.DisplayName} cannot be converted automatically as it is.", reasons);

        return new(DesktopConversionCategory.BrowserApp,
            $"{p.DisplayName} is a Vite + React browser app. HCG can generate a separate desktop project that packages its production build (npm run {build}) in a secured Electron window.",
            [$"Production build: npm run {build} ({p.Scripts[build!]})", $"Package manager: npm ({p.LockFile ?? (p.WorkspaceLockFile is null ? "no lockfile" : "monorepo lockfile " + p.WorkspaceLockFile)})"]);
    }

    /// <summary>The script that runs the production build ("build" first), or null.</summary>
    public static string? ViteBuildScript(NodeProjectInfo p) =>
        p.Scripts.OrderBy(s => s.Key == "build" ? 0 : 1).ThenBy(s => s.Key, StringComparer.Ordinal).FirstOrDefault(s => ViteBuildRegex().IsMatch(s.Value)).Key;

    [GeneratedRegex(@"(^|[\s&;(|])vite\s+build\b")]
    public static partial Regex ViteBuildRegex();
}
