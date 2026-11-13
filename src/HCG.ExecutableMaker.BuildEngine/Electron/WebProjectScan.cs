using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using HCG.ExecutableMaker.ProjectInspection.Node;

namespace HCG.ExecutableMaker.BuildEngine.Electron;

/// <summary>A place in the web app's source where something was found.</summary>
public sealed record WebSourceHit(string File, int Line, string Text)
{
    public string Location => $"{File}:{Line}";
}

/// <summary>
/// Neutral facts about a web project, read as text (nothing runs): its manifest, build, asset paths, and what its own
/// source code uses. Desktop and mobile conversions each decide what the facts mean for their platform.
/// </summary>
public sealed record WebProjectFacts
{
    public required string Directory { get; init; }
    public required string PackageJsonText { get; init; }
    public JsonObject? Package { get; init; }
    public string? PackageJsonError { get; init; }
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Scripts { get; init; } = new Dictionary<string, string>();
    public bool HasVite { get; init; }
    public bool HasReact { get; init; }
    public bool IsElectron { get; init; }
    public IReadOnlyList<string> SsrFrameworks { get; init; } = [];
    public IReadOnlyList<string> ServerFrameworks { get; init; } = [];
    /// <summary>Non-React UI frameworks (Vue, Svelte, Angular, ...).</summary>
    public IReadOnlyList<string> OtherUiFrameworks { get; init; } = [];
    public IReadOnlyList<string> AuthPackages { get; init; } = [];
    /// <summary>"npm", "pnpm", "yarn" or "bun" (from the lockfile or packageManager).</summary>
    public string PackageManager { get; init; } = "npm";
    public string? LockFile { get; init; }
    public IReadOnlyList<string> LocalDependencies { get; init; } = [];
    public bool DeclaresWorkspaces { get; init; }
    public string? BuildScript { get; init; }
    public string? ViteConfig { get; init; }
    public string OutDir { get; init; } = "dist";
    public bool OutDirComputed { get; init; }
    public string BasePath { get; init; } = "/";
    public bool BaseComputed { get; init; }
    public WebSourceHit? HttpBase { get; init; }
    public WebSourceHit? DevProxy { get; init; }
    public IReadOnlyList<WebSourceHit> RelativeApiCalls { get; init; } = [];
    public IReadOnlyList<WebSourceHit> LocalhostUrls { get; init; } = [];
    public IReadOnlyList<WebSourceHit> ServiceWorkers { get; init; } = [];
    public IReadOnlyList<WebSourceHit> RedirectSignIn { get; init; } = [];
    public IReadOnlyList<WebSourceHit> ElectronApis { get; init; } = [];
    public IReadOnlyList<WebSourceHit> FileSystemAccess { get; init; } = [];
    public IReadOnlyList<WebSourceHit> Downloads { get; init; } = [];
    public IReadOnlyList<WebSourceHit> Notifications { get; init; } = [];
    public IReadOnlyList<WebSourceHit> Geolocation { get; init; } = [];
    public IReadOnlyList<WebSourceHit> CameraMicrophone { get; init; } = [];
    public IReadOnlyList<WebSourceHit> Clipboard { get; init; } = [];
    public IReadOnlyList<WebSourceHit> TokenStorage { get; init; } = [];
    public IReadOnlyList<WebSourceHit> WindowOpen { get; init; } = [];
    public IReadOnlyList<WebSourceHit> HardwareApis { get; init; } = [];
    public IReadOnlyList<string> Routers { get; init; } = [];
    public IReadOnlyList<string> EnvVars { get; init; } = [];
    /// <summary>Build-time variables whose names suggest a secret (every VITE_ value is readable inside the app).</summary>
    public IReadOnlyList<WebSourceHit> SecretEnvVars { get; init; } = [];
    public IReadOnlyList<string> SecretEnvFiles { get; init; } = [];
    public IReadOnlyList<string> HostingFiles { get; init; } = [];
    public string? IndexHtml { get; init; }
    public bool HasViewportMeta { get; init; }
    public bool HasHtmlLang { get; init; }
    public string? Title { get; init; }
    public int SourceFilesScanned { get; init; }
}

public static partial class WebDesktopConversion
{
    private static readonly string[] OtherUi = ["vue", "svelte", "@angular/core", "solid-js", "preact", "lit", "@builder.io/qwik"];

    /// <summary>Reads a web project's facts. Read-only: package.json, the Vite config, index.html and the app's source are read as text.</summary>
    public static WebProjectFacts Scan(string projectDir)
    {
        var dir = Path.GetFullPath(projectDir);
        var text = File.ReadAllText(Path.Combine(dir, "package.json"));
        JsonObject? pkg;
        try { pkg = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject; }
        catch (JsonException ex) { return new WebProjectFacts { Directory = dir, PackageJsonText = text, PackageJsonError = ex.Message }; }
        if (pkg is null) return new WebProjectFacts { Directory = dir, PackageJsonText = text, PackageJsonError = "package.json is not a JSON object." };

        var deps = Dependencies(pkg);
        var scripts = (pkg["scripts"] as JsonObject ?? []).Where(s => s.Value is JsonValue v && v.TryGetValue<string>(out _))
            .ToDictionary(s => s.Key, s => s.Value!.GetValue<string>(), StringComparer.Ordinal);
        var lockFile = new[] { "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock" }.FirstOrDefault(f => File.Exists(Path.Combine(dir, f)));
        var manager = lockFile switch
        {
            "pnpm-lock.yaml" => "pnpm", "yarn.lock" => "yarn", "bun.lockb" or "bun.lock" => "bun",
            _ => pkg["packageManager"]?.GetValue<string>() is { } pm && pm.Split('@')[0] is "pnpm" or "yarn" or "bun" ? pm.Split('@')[0] : "npm",
        };
        var viteConfig = ViteConfig(dir);
        var viteText = viteConfig is null ? "" : File.ReadAllText(Path.Combine(dir, viteConfig));
        var outDir = OutDirRegex().Match(viteText) is { Success: true } od ? od.Groups["v"].Value.Trim('/') : "dist";
        WebSourceHit? httpBase = null;
        var basePath = "/";
        if (BaseRegex().Match(viteText) is { Success: true } b)
        {
            var v = b.Groups["v"].Value;
            if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase) || v.StartsWith("//", StringComparison.Ordinal)) httpBase = new(viteConfig!, LineOf(viteText, b.Index), v);
            else basePath = v.Trim('.', '/').Length == 0 ? "/" : "/" + v.Trim('.', '/') + "/";
        }

        var hits = new Dictionary<string, List<WebSourceHit>>();
        void Add(string kind, string file, string body, Match m) =>
            (hits.TryGetValue(kind, out var l) ? l : hits[kind] = []).Add(new WebSourceHit(file, LineOf(body, m.Index), m.Value.Trim()));
        var routers = new SortedSet<string>(StringComparer.Ordinal);
        var envVars = new SortedSet<string>(StringComparer.Ordinal);
        var secretEnv = new List<WebSourceHit>();
        var scanned = 0;
        foreach (var (rel, body) in SourceFiles(dir, outDir))
        {
            scanned++;
            foreach (Match m in RelativeApiRegex().Matches(body)) Add("api", rel, body, m);
            foreach (Match m in LocalhostRegex().Matches(body)) Add("localhost", rel, body, m);
            foreach (Match m in ServiceWorkerRegex().Matches(body)) Add("sw", rel, body, m);
            foreach (Match m in AuthCallRegex().Matches(body)) Add("auth", rel, body, m);
            foreach (Match m in ElectronApiRegex().Matches(body)) Add("electron", rel, body, m);
            foreach (Match m in FileSystemAccessRegex().Matches(body)) Add("fs", rel, body, m);
            foreach (Match m in DownloadRegex().Matches(body)) Add("download", rel, body, m);
            foreach (Match m in NotificationRegex().Matches(body)) Add("notify", rel, body, m);
            foreach (Match m in GeolocationRegex().Matches(body)) Add("geo", rel, body, m);
            foreach (Match m in MediaRegex().Matches(body)) Add("media", rel, body, m);
            foreach (Match m in ClipboardRegex().Matches(body)) Add("clipboard", rel, body, m);
            foreach (Match m in TokenStorageRegex().Matches(body)) Add("token", rel, body, m);
            foreach (Match m in WindowOpenRegex().Matches(body)) Add("open", rel, body, m);
            foreach (Match m in HardwareRegex().Matches(body)) Add("hardware", rel, body, m);
            foreach (Match m in RouterRegex().Matches(body)) routers.Add(m.Value);
            foreach (Match m in EnvRegex().Matches(body))
            {
                envVars.Add(m.Groups["n"].Value);
                if (SecretNameRegex().IsMatch(m.Groups["n"].Value)) secretEnv.Add(new WebSourceHit(rel, LineOf(body, m.Index), m.Groups["n"].Value));
            }
        }
        IReadOnlyList<WebSourceHit> H(string kind) => hits.TryGetValue(kind, out var l) ? l : [];

        var indexPath = Path.Combine(dir, "index.html");
        var index = File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;
        return new WebProjectFacts
        {
            Directory = dir, PackageJsonText = text, Package = pkg, Dependencies = deps, Scripts = scripts,
            HasVite = deps.ContainsKey("vite") || viteConfig is not null,
            HasReact = deps.ContainsKey("react") && deps.ContainsKey("react-dom"),
            IsElectron = deps.ContainsKey("electron"),
            SsrFrameworks = DesktopEligibilityRules.ServerRendered.Where(s => deps.ContainsKey(s.Package)).Select(s => s.Label).Distinct().ToList(),
            ServerFrameworks = DesktopEligibilityRules.ServerFrameworks.Where(s => deps.ContainsKey(s.Package)).Select(s => s.Label).Distinct().ToList(),
            OtherUiFrameworks = OtherUi.Where(deps.ContainsKey).ToList(),
            AuthPackages = RedirectAuth.Where(a => deps.ContainsKey(a.Package)).Select(a => a.Label).Distinct().ToList(),
            PackageManager = manager, LockFile = lockFile,
            LocalDependencies = deps.Where(d => d.Value.StartsWith("file:", StringComparison.Ordinal) || d.Value.StartsWith("workspace:", StringComparison.Ordinal) || d.Value.StartsWith("link:", StringComparison.Ordinal))
                .Select(d => $"{d.Key} ({d.Value})").ToList(),
            DeclaresWorkspaces = pkg["workspaces"] is not null,
            BuildScript = scripts.OrderBy(s => s.Key == "build" ? 0 : 1).FirstOrDefault(s => DesktopEligibilityRules.ViteBuildRegex().IsMatch(s.Value)).Key,
            ViteConfig = viteConfig, OutDir = outDir,
            OutDirComputed = !OutDirRegex().IsMatch(viteText) && viteText.Contains("outDir", StringComparison.Ordinal),
            BasePath = basePath, HttpBase = httpBase,
            BaseComputed = !BaseRegex().IsMatch(viteText) && viteText.Contains("base:", StringComparison.Ordinal),
            DevProxy = ProxyRegex().Match(viteText) is { Success: true } px ? new WebSourceHit(viteConfig!, LineOf(viteText, px.Index), "server.proxy") : null,
            RelativeApiCalls = H("api"), LocalhostUrls = H("localhost"), ServiceWorkers = H("sw"), RedirectSignIn = H("auth"), ElectronApis = H("electron"),
            FileSystemAccess = H("fs"), Downloads = H("download"), Notifications = H("notify"), Geolocation = H("geo"), CameraMicrophone = H("media"),
            Clipboard = H("clipboard"), TokenStorage = H("token"), WindowOpen = H("open"), HardwareApis = H("hardware"),
            Routers = [.. routers], EnvVars = [.. envVars], SecretEnvVars = secretEnv,
            SecretEnvFiles = System.IO.Directory.EnumerateFiles(dir, ".env*").Select(Path.GetFileName).OfType<string>().Where(IsEnvFile).Order(StringComparer.Ordinal).ToList(),
            HostingFiles = new[] { "vercel.json", "netlify.toml", "_headers", "public/_headers", "public/_redirects", "firebase.json" }.Where(h => File.Exists(Path.Combine(dir, h))).ToList(),
            IndexHtml = index,
            HasViewportMeta = index is not null && ViewportRegex().IsMatch(index),
            HasHtmlLang = index is not null && HtmlLangRegex().IsMatch(index),
            Title = index is not null && TitleRegex().Match(index) is { Success: true } t ? System.Net.WebUtility.HtmlDecode(t.Groups["t"].Value.Trim()) : null,
            SourceFilesScanned = scanned,
        };
    }

    /// <summary>
    /// Copies a web project for a separate generated workspace: node_modules, the build output, VCS data, caches,
    /// symbolic links and .env files with values are left out. Returns the number of files copied.
    /// </summary>
    public static int CopyWebProject(string source, string destination, string outDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        return CopyProject(source, destination, outDir, ct).Count;
    }

    /// <summary>True for .env files that may hold real values (.env.example/.sample/.template are not).</summary>
    public static bool IsEnvFileWithValues(string fileName) => IsEnvFile(fileName);

    [GeneratedRegex(@"\bwindow\.electron\w*\b|\bipcRenderer\b|require\(\s*['""]electron['""]\s*\)|from\s+['""]electron['""]")] private static partial Regex ElectronApiRegex();
    [GeneratedRegex(@"\b(?:showOpenFilePicker|showSaveFilePicker|showDirectoryPicker)\s*\(")] private static partial Regex FileSystemAccessRegex();
    [GeneratedRegex(@"\.download\s*=|\bdownload=\{|URL\.createObjectURL\s*\(")] private static partial Regex DownloadRegex();
    [GeneratedRegex(@"new\s+Notification\s*\(|Notification\.requestPermission\s*\(")] private static partial Regex NotificationRegex();
    [GeneratedRegex(@"navigator\.geolocation\b")] private static partial Regex GeolocationRegex();
    [GeneratedRegex(@"\bgetUserMedia\s*\(|mediaDevices\.getDisplayMedia\s*\(")] private static partial Regex MediaRegex();
    [GeneratedRegex(@"navigator\.clipboard\.\w+")] private static partial Regex ClipboardRegex();
    [GeneratedRegex(@"(?:localStorage|sessionStorage)\.setItem\(\s*['""`][^'""`]*(?:token|session|auth|jwt|credential|password)[^'""`]*['""`]", RegexOptions.IgnoreCase)] private static partial Regex TokenStorageRegex();
    [GeneratedRegex(@"\bwindow\.open\s*\(")] private static partial Regex WindowOpenRegex();
    [GeneratedRegex(@"navigator\.(?:bluetooth|usb|serial|hid)\b")] private static partial Regex HardwareRegex();
    [GeneratedRegex(@"SECRET|SERVICE_ROLE|PRIVATE_KEY|PASSWORD|ADMIN_KEY")] private static partial Regex SecretNameRegex();
    [GeneratedRegex(@"<meta[^>]+name\s*=\s*['""]viewport['""]", RegexOptions.IgnoreCase)] private static partial Regex ViewportRegex();
    [GeneratedRegex(@"<html[^>]*\blang\s*=", RegexOptions.IgnoreCase)] private static partial Regex HtmlLangRegex();
}
