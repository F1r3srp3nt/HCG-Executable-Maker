using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.BuildEngine.Electron;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;

namespace HCG.ExecutableMaker.Porting.Web;

/// <summary>
/// Web-based mobile packaging: a Vite + React web app becomes an Android or iPhone/iPad app with Capacitor, an
/// established mobile runtime. The production build runs in the platform WebView from inside the app. What a phone
/// cannot provide (the project's server, browser sign-in redirects, desktop-only browser APIs, secrets) is classified
/// and listed; nothing is stubbed or silently removed.
/// </summary>
public sealed partial class WebToMobilePorter(MobileTarget target) : IPorter
{
    public const string CapacitorVersion = "8.5.3";
    public const string MobileCss = "public/hcg-mobile.css";
    public const string ConfigFile = "capacitor.config.json";

    public PortingPath Path { get; } = target == MobileTarget.Android ? PortingPath.WebToAndroid : PortingPath.WebToIOS;
    public MobileTarget Target { get; } = target;
    private string Platform => Target == MobileTarget.Android ? "android" : "ios";
    private string PlatformName => Target.DisplayName();

    public IReadOnlyList<string> Scope =>
    [
        $"Supported: web apps built with Vite and React, installed with npm, whose production build ({"vite build"}) is a static site.",
        $"Generated: a separate copy of the project with Capacitor {CapacitorVersion} ({Platform}), a capacitor.config.json, touch and safe-area styles and the viewport for phones. The native {PlatformName} project is created from Capacitor's own template in the Validate step.",
        "Reused unchanged: the app's components, routes, styles and assets. Requests to its own server, browser sign-in redirects, tokens in web storage and desktop-only browser APIs are classified, not faked.",
        "Not supported in this version: other frameworks (Vue, Svelte, Angular), server-rendered apps (Next.js, Nuxt, Remix, SvelteKit, Astro), Electron apps, pnpm/Yarn/Bun projects, monorepo workspaces.",
    ];

    public IReadOnlyDictionary<string, string> Pins => new Dictionary<string, string>
    {
        ["@capacitor/core"] = CapacitorVersion, ["@capacitor/cli"] = CapacitorVersion, [$"@capacitor/{Platform}"] = CapacitorVersion,
    };

    // ───────────── Sources ─────────────

    public IReadOnlyList<PortSource> FindSources(SourceIndex index)
    {
        var sources = new List<PortSource>();
        foreach (var manifest in index.Named("package.json").OrderBy(SourceIndex.Depth).ThenBy(f => f, StringComparer.Ordinal))
        {
            var dir = manifest.Contains('/') ? manifest[..manifest.LastIndexOf('/')] : "";
            if (dir.Split('/').Any(d => d is "test" or "tests" or "fixtures" or "e2e" or "examples")) continue;
            string text;
            try { text = File.ReadAllText(index.FullPath(manifest)); } catch (IOException) { continue; }
            var deps = DependencyNames(text);
            var web = deps.Overlaps(["vite", "react", "vue", "svelte", "@angular/core", "next", "nuxt", "astro", "@sveltejs/kit", "@remix-run/react", "electron", "solid-js", "preact"]);
            if (!web) continue;
            var capacitorProject = deps.Contains("@capacitor/core") || File.Exists(index.FullPath(PortOutput.Join(dir, ConfigFile))) || File.Exists(index.FullPath(PortOutput.Join(dir, "capacitor.config.ts")));
            var supported = deps.Contains("vite") && deps.Contains("react") && deps.Contains("react-dom") && !deps.Contains("electron");
            var kind = capacitorProject ? "Existing Capacitor app" : supported ? "Vite + React web app" : "Web or Node.js app";
            sources.Add(new PortSource
            {
                Path = Path, Key = (capacitorProject ? "capacitor:" : supported ? "web:" : "unsupported-web:") + dir, Kind = kind,
                DisplayName = (dir.Length == 0 ? "package.json" : dir + "/package.json"), Directory = dir, Target = manifest,
            });
        }
        return sources;
    }

    private static HashSet<string> DependencyNames(string packageJson)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(packageJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is JsonObject o)
                foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
                    if (o[section] is JsonObject d) foreach (var (k, _) in d) names.Add(k);
        }
        catch (JsonException) { }
        return names;
    }

    // ───────────── Assessment ─────────────

    public PortingAssessment Assess(SourceIndex index, PortSource source, ToolchainReport? toolchains)
    {
        var common = new PortingAssessment
        {
            Path = Path, Source = source, Capability = PortingCapability.NotSupported, Summary = "", Scope = Scope,
            Toolchains = Toolchains(toolchains), Signing = Signing, AssessedAt = DateTimeOffset.UtcNow, Pins = Pins,
        };
        var dir = index.FullPath(source.Directory);
        if (source.Key.StartsWith("capacitor:", StringComparison.Ordinal))
            return common with
            {
                Capability = Directory.Exists(System.IO.Path.Combine(dir, Platform)) ? PortingCapability.BuildExistingTarget : PortingCapability.NotSupported,
                Summary = Directory.Exists(System.IO.Path.Combine(dir, Platform))
                    ? $"{source.DisplayName} is already a Capacitor app with a {PlatformName} project. No conversion is needed: build it in Mobile Build Studio."
                    : $"{source.DisplayName} is already a Capacitor app without a {Platform}/ project. Add the platform with Capacitor's CLI (npx cap add {Platform}) in the project; HCG does not change an existing Capacitor setup.",
            };
        var facts = WebDesktopConversion.Scan(dir);
        if (source.Key.StartsWith("unsupported-web:", StringComparison.Ordinal))
            return common with { Summary = UnsupportedSummary(source, facts), Findings = UnsupportedFindings(source, facts) };

        var f = new List<PortingFinding>();
        void Add(FindingCategory c, FindingDisposition d, string title, string detail, string? file = null, int? line = null, string? guidance = null, string? evidence = null) =>
            f.Add(new PortingFinding { Category = c, Disposition = d, Title = title, Detail = detail, File = file is null ? null : PortOutput.Join(source.Directory, file), Line = line, Guidance = guidance, Evidence = evidence });
        void Hits(IReadOnlyList<WebSourceHit> hits, FindingCategory c, FindingDisposition d, string title, string detail, string? guidance = null)
        {
            foreach (var h in hits.Take(12)) Add(c, d, title, detail, h.File, h.Line, guidance, h.Text);
            if (hits.Count > 12) Add(c, d, title, $"{hits.Count - 12} more place(s) like the ones above.", null, null, guidance);
        }

        // ---- Project shape (blockers) ----
        if (facts.PackageJsonError is { } err) Add(FindingCategory.BuildSystem, FindingDisposition.Blocker, "package.json is unreadable", err, "package.json");
        if (facts.PackageManager != "npm")
            Add(FindingCategory.BuildSystem, FindingDisposition.Blocker, $"Uses {facts.PackageManager}", $"The project is installed with {facts.PackageManager} ({facts.LockFile ?? "packageManager field"}). HCG installs dependencies with npm only, which would ignore that lockfile.", facts.LockFile ?? "package.json",
                guidance: "Convert the project to npm (package-lock.json) or use the planned support for other package managers.");
        foreach (var local in facts.LocalDependencies)
            Add(FindingCategory.Dependency, FindingDisposition.Blocker, "Dependency outside the project", $"{local} points outside this project, so the separate mobile project could not install it.", "package.json");
        if (facts.DeclaresWorkspaces)
            Add(FindingCategory.BuildSystem, FindingDisposition.Blocker, "npm workspaces", "package.json declares npm workspaces. Import the web app's own folder, not the monorepo root.", "package.json");
        if (facts.BuildScript is null)
            Add(FindingCategory.BuildSystem, FindingDisposition.Blocker, "No production build script", "No package.json script runs \"vite build\". The mobile app packages the production build; add a script such as \"build\": \"vite build\".", "package.json");
        else
            Add(FindingCategory.BuildSystem, FindingDisposition.Compatible, "Production build", $"The mobile app packages the project's own production build: npm run {facts.BuildScript} ({facts.Scripts[facts.BuildScript]}), output {facts.OutDir}/.", "package.json");
        if (facts.OutDir.Length == 0 || facts.OutDir.Contains("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(facts.OutDir))
            Add(FindingCategory.BuildSystem, FindingDisposition.Blocker, "Build output outside the project", $"build.outDir \"{facts.OutDir}\" is outside the project.", facts.ViteConfig);
        if (facts.HttpBase is { } hb)
            Add(FindingCategory.FileSystem, FindingDisposition.Blocker, "Assets load from a web server", $"base is \"{hb.Text}\": the app would load its files from that server instead of from the app.", hb.File, hb.Line, "Use base \"/\" or \"./\" for the mobile build.");
        else if (facts.BasePath != "/")
            Add(FindingCategory.FileSystem, FindingDisposition.Blocker, "Sub-path base", $"base is \"{facts.BasePath}\". Capacitor serves the build at the root of the app's local origin, so absolute asset paths under {facts.BasePath} would not be found.", facts.ViteConfig,
                guidance: "Build the mobile app with base \"/\" (for example a separate Vite mode).");
        foreach (var existing in new[] { ConfigFile, "capacitor.config.ts", "capacitor.config.js", "android", "ios" }.Where(n => File.Exists(System.IO.Path.Combine(dir, n)) || Directory.Exists(System.IO.Path.Combine(dir, n))))
            Add(FindingCategory.BuildSystem, FindingDisposition.Blocker, $"{existing} already exists", $"{existing} exists in the project. HCG does not overwrite it; the mobile project would need it. Rename or remove it first.", existing);

        // ---- Secrets and identity boundaries ----
        foreach (var s in facts.SecretEnvVars.DistinctBy(s => s.Text))
            Add(FindingCategory.Permission, FindingDisposition.Blocker, $"Secret-looking build variable {s.Text}",
                $"{s.Text} is compiled into the app's code (every VITE_ variable is readable by anyone who installs the app). A server secret must never ship in a mobile app.", s.File, s.Line,
                "Keep the secret on a server and call that server from the app; use only public keys (anon/publishable keys) in the app.");
        if (facts.SecretEnvFiles.Count > 0)
            Add(FindingCategory.Permission, FindingDisposition.Compatible, "Environment files not copied",
                $"{string.Join(", ", facts.SecretEnvFiles)} may hold secrets and are not copied into the mobile project. Set the public build values you need there yourself.", facts.SecretEnvFiles[0]);

        // ---- Server and companion ----
        foreach (var server in facts.ServerFrameworks)
            Add(FindingCategory.Dependency, FindingDisposition.RequiresCompanion, $"Server code ({server})",
                $"The project contains server code ({server}). A phone does not run it: only the browser build is packaged. The server has to run elsewhere (hosted, or on a companion computer).", "package.json");
        Hits(facts.RelativeApiCalls, FindingCategory.PlatformApi, FindingDisposition.RequiresCompanion, "Request to the page's own server",
            "In the mobile app the page comes from the app itself, so there is no server behind this relative address. It needs a hosted HTTPS API (or a companion computer), configured as an absolute URL.",
            "Make the API base URL configurable (for example VITE_API_BASE_URL) and point it at the deployed HTTPS backend; allow the app's origin (https://localhost on Android, capacitor://localhost on iOS) in its CORS settings.");
        Hits(facts.LocalhostUrls, FindingCategory.PlatformApi, FindingDisposition.RequiresCompanion, "Address on the developer's computer",
            "This address is a server on the computer the app was written on; on a phone, localhost is the phone itself.", "Use the deployed HTTPS address instead.");
        if (facts.DevProxy is { } dp)
            Add(FindingCategory.PlatformApi, FindingDisposition.RequiresCompanion, "Development server proxy", "server.proxy forwards requests only while the Vite development server runs. The mobile app has no development server.", dp.File, dp.Line);

        // ---- Sign-in callbacks and secure storage ----
        if (facts.AuthPackages.Count > 0 || facts.RedirectSignIn.Count > 0)
        {
            var detail = $"Browser sign-in{(facts.AuthPackages.Count > 0 ? " (" + string.Join(", ", facts.AuthPackages) + ")" : "")} returns to the web page's own address. Inside the app that address is the app's local origin, which an identity provider does not accept as a redirect: the provider must register a mobile client with an app link or custom URL scheme, and the app must handle the callback (Capacitor App plugin, appUrlOpen) and open sign-in in the system browser. HCG does not invent this registration or fake the callback.";
            if (facts.RedirectSignIn.Count == 0) Add(FindingCategory.Lifecycle, FindingDisposition.NeedsReview, "Sign-in callback", detail, "package.json");
            else Hits(facts.RedirectSignIn, FindingCategory.Lifecycle, FindingDisposition.NeedsReview, "Sign-in callback", detail);
        }
        Hits(facts.TokenStorage, FindingCategory.Permission, FindingDisposition.NeedsReview, "Credential kept in web storage",
            "Web storage (localStorage/sessionStorage) is plain app data, not the device's secure store. Sessions and tokens belong in the iOS Keychain / Android Keystore.",
            "Store tokens through a secure-storage plugin backed by Keychain/Keystore, or keep only short-lived tokens in memory.");

        // ---- Device and browser APIs ----
        Hits(facts.ElectronApis, FindingCategory.PlatformApi, FindingDisposition.Blocker, "Electron API", "Electron's APIs exist only in Electron. The mobile WebView has no Electron, Node.js or IPC.",
            "Replace the call with a web API or a Capacitor plugin, or move the work to a companion service.");
        Hits(facts.HardwareApis, FindingCategory.PlatformApi, FindingDisposition.Blocker, "Desktop browser hardware API",
            "Web Bluetooth, WebUSB, Web Serial and WebHID are not available in the Android WebView or iOS WKWebView.", "Use a native Capacitor plugin for the device, or a companion computer.");
        Hits(facts.FileSystemAccess, FindingCategory.FileSystem, FindingDisposition.NeedsReview, "File System Access API",
            "showOpenFilePicker/showSaveFilePicker/showDirectoryPicker are not available in mobile WebViews.", "Use <input type=\"file\"> to open files and the Capacitor Filesystem and Share plugins to save them.");
        Hits(facts.Downloads, FindingCategory.FileSystem, FindingDisposition.NeedsReview, "File download",
            "WebViews do not save downloads to the phone's files the way a browser does.", "Write the file with the Capacitor Filesystem plugin and offer it with the Share plugin.");
        Hits(facts.Notifications, FindingCategory.Lifecycle, FindingDisposition.NeedsReview, "Web notifications",
            "The Web Notifications API is not available in mobile WebViews, and the app does not run in the background.", "Use Capacitor Local Notifications or push notifications (which need a server).");
        Hits(facts.Geolocation, FindingCategory.Permission, FindingDisposition.NeedsReview, "Location",
            Target == MobileTarget.Android
                ? "Location works in the WebView once the app declares it: add ACCESS_COARSE_LOCATION and ACCESS_FINE_LOCATION to android/app/src/main/AndroidManifest.xml."
                : "Location works in the WebView once the app explains it: add NSLocationWhenInUseUsageDescription to ios/App/App/Info.plist.");
        Hits(facts.CameraMicrophone, FindingCategory.Permission, FindingDisposition.NeedsReview, "Camera or microphone",
            Target == MobileTarget.Android
                ? "getUserMedia needs CAMERA and/or RECORD_AUDIO declared in AndroidManifest.xml (and MODIFY_AUDIO_SETTINGS for audio)."
                : "getUserMedia needs NSCameraUsageDescription and/or NSMicrophoneUsageDescription in Info.plist.");
        Hits(facts.ServiceWorkers, FindingCategory.Lifecycle, FindingDisposition.NeedsReview, "Service worker",
            "The app's files are already on the phone, so an offline cache is not needed. On iOS, service workers do not run for the app's local origin.", "Make the app work without its service worker (it is skipped where unavailable).");
        Hits(facts.WindowOpen, FindingCategory.UiAdaptation, FindingDisposition.NeedsReview, "New windows",
            "A phone has no extra windows: window.open either replaces the app's page or does nothing.", "Open external pages in the system browser (Capacitor Browser plugin) and show in-app content as a route or dialog.");
        Hits(facts.Clipboard, FindingCategory.PlatformApi, FindingDisposition.NeedsReview, "Clipboard", "navigator.clipboard works in the app's secure origin; reading the clipboard prompts the user on iOS.");

        // ---- Supported adaptations made by the generator ----
        Add(FindingCategory.UiAdaptation, FindingDisposition.AutoAdapted, "Phone viewport",
            facts.HasViewportMeta ? "index.html's viewport gets viewport-fit=cover so content can use the full screen around notches." : "index.html gets a viewport meta tag (device width, viewport-fit=cover); without it phones render a zoomed-out desktop page.",
            "index.html");
        Add(FindingCategory.UiAdaptation, FindingDisposition.AutoAdapted, "Touch and safe-area styles",
            $"{MobileCss} adds safe-area padding (notches, home indicator), 44-point minimum touch targets for buttons and form controls on touch screens, no tap delay, and no automatic text inflation. It is linked from index.html; review it with your design.",
            "index.html");
        if (facts.Routers.Any(r => r.Contains("Browser", StringComparison.Ordinal)))
            Add(FindingCategory.UiAdaptation, FindingDisposition.Compatible, "History routing", $"{string.Join(", ", facts.Routers)}: Capacitor answers unknown paths with index.html, so routes and reloads work unchanged.");
        else if (facts.Routers.Count > 0)
            Add(FindingCategory.UiAdaptation, FindingDisposition.Compatible, "Hash routing", $"{string.Join(", ", facts.Routers)} works unchanged.");
        if (!facts.HasHtmlLang && facts.IndexHtml is not null)
            Add(FindingCategory.UiAdaptation, FindingDisposition.NeedsReview, "Page language", "index.html has no lang attribute, so screen readers (VoiceOver, TalkBack) cannot pick the right voice.", "index.html",
                guidance: "Add lang=\"…\" to the <html> element.");
        if (facts.EnvVars.Count > 0)
            Add(FindingCategory.BuildSystem, FindingDisposition.Compatible, "Build-time configuration", $"Fixed when the mobile app is built: {string.Join(", ", facts.EnvVars)}.");
        if (facts.HostingFiles.Count > 0)
            Add(FindingCategory.BuildSystem, FindingDisposition.Compatible, "Web hosting configuration", $"{string.Join(", ", facts.HostingFiles)} configure a web host and do not apply inside the app.");
        Add(FindingCategory.Permission, FindingDisposition.AutoAdapted, "Network access", Target == MobileTarget.Android
            ? "Capacitor's Android template declares INTERNET and serves the app from https://localhost; plain-HTTP requests are blocked (allowMixedContent off)."
            : "Capacitor's iOS app serves the app from capacitor://localhost; App Transport Security requires HTTPS for network requests.");

        // ---- Reusable code ----
        var (files, lines) = Count(dir, facts.OutDir, "src");
        var (assets, _) = Count(dir, facts.OutDir, "public");
        var units = new List<ReusableUnit> { new("Web app source (src/)", "Reused unchanged in the WebView", files, lines, f.Count(x => x.Disposition == FindingDisposition.Blocker && x.File?.Contains("/src/") != false && x.Line is not null), 0) };
        if (assets > 0) units.Add(new ReusableUnit("Static assets (public/)", "Packaged unchanged", assets, 0, 0, 0));

        var blockers = f.Count(x => x.Disposition == FindingDisposition.Blocker);
        var companion = f.Count(x => x.Disposition == FindingDisposition.RequiresCompanion);
        var capability = blockers > 0 ? PortingCapability.BlockedNeedsPorting : PortingCapability.GenerateTarget;
        var name = facts.Title is { Length: > 0 and < 60 } t ? t : facts.Package?["productName"]?.GetValue<string>() ?? facts.Package?["name"]?.GetValue<string>() ?? "Web App";
        return common with
        {
            Capability = capability,
            Summary = $"{source.DisplayName}: Vite + React web app ({files} source file(s)). " + (capability == PortingCapability.GenerateTarget
                ? $"HCG can package it as {PlatformName} app with Capacitor {CapacitorVersion}. " +
                  (companion > 0 ? $"{companion} feature(s) need a hosted backend or companion computer; " : "") +
                  $"{f.Count(x => x.Disposition == FindingDisposition.NeedsReview)} item(s) need adaptation by hand. A generated project is not a verified mobile build: validate and build it next."
                : $"{blockers} blocker(s) must be resolved in the project first; nothing is generated until then."),
            Reusable = units,
            Findings = Order(f),
            ManualSteps = ManualSteps(facts),
            SuggestedAppName = name,
            SuggestedAppId = SuggestId(facts.Package?["name"]?.GetValue<string>() ?? name),
        };
    }

    private static List<PortingFinding> Order(IEnumerable<PortingFinding> f) =>
        f.OrderBy(x => x.Disposition switch
        {
            FindingDisposition.Blocker => 0, FindingDisposition.RequiresCompanion => 1, FindingDisposition.NeedsReview => 2, FindingDisposition.AutoAdapted => 3, _ => 4,
        }).ThenBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Line).ToList();

    private string UnsupportedSummary(PortSource source, WebProjectFacts facts)
    {
        string reason;
        if (facts.IsElectron) reason = "an Electron desktop app. Packaging its renderer for phones is planned: the renderer must build without Electron and each preload/IPC call needs a mobile replacement (see the support matrix).";
        else if (facts.SsrFrameworks.Count > 0) reason = $"rendered by a server ({string.Join(", ", facts.SsrFrameworks)}). Export a static build to make it eligible for web packaging, or keep the server and use companion mode (planned).";
        else if (facts.OtherUiFrameworks.Count > 0) reason = $"built with {string.Join(", ", facts.OtherUiFrameworks)}. Capacitor packaging for these frameworks is planned; this version supports Vite + React.";
        else if (!facts.HasVite) reason = "not a Vite app. This version packages Vite + React production builds.";
        else reason = "not a React app (react and react-dom). This version packages Vite + React.";
        return $"{source.DisplayName} is {reason}";
    }

    private List<PortingFinding> UnsupportedFindings(PortSource source, WebProjectFacts facts)
    {
        var list = new List<PortingFinding>();
        foreach (var h in facts.ElectronApis.Take(8))
            list.Add(new PortingFinding { Category = FindingCategory.PlatformApi, Disposition = FindingDisposition.Blocker, Title = "Electron API", Detail = "Exists only in Electron.", File = PortOutput.Join(source.Directory, h.File), Line = h.Line, Evidence = h.Text });
        foreach (var s in facts.ServerFrameworks)
            list.Add(new PortingFinding { Category = FindingCategory.Dependency, Disposition = FindingDisposition.RequiresCompanion, Title = $"Server code ({s})", Detail = "Runs on a computer or server, not on the phone.", File = source.Target });
        return list;
    }

    private IReadOnlyList<string> ManualSteps(WebProjectFacts facts)
    {
        var steps = new List<string>();
        if (facts.RelativeApiCalls.Count > 0 || facts.LocalhostUrls.Count > 0 || facts.DevProxy is not null || facts.ServerFrameworks.Count > 0)
            steps.Add("Deploy the backend the app calls over HTTPS (or run it on a companion computer), make its base URL configurable in the web app, and allow the app's origin (https://localhost on Android, capacitor://localhost on iOS) in its CORS policy.");
        if (facts.AuthPackages.Count > 0 || facts.RedirectSignIn.Count > 0)
            steps.Add("Register a mobile client with your identity provider (an app link or custom URL scheme as redirect URI; never reuse the web client), open sign-in in the system browser and handle the callback in the app (Capacitor App plugin, appUrlOpen). For HCG sign-in, HCG must issue a separate public mobile client.");
        if (facts.TokenStorage.Count > 0)
            steps.Add("Move tokens and sessions from web storage to the Keychain/Keystore through a secure-storage plugin.");
        if (facts.Geolocation.Count > 0 || facts.CameraMicrophone.Count > 0)
            steps.Add(Target == MobileTarget.Android
                ? "Declare the permissions the app uses (location, camera, microphone) in android/app/src/main/AndroidManifest.xml."
                : "Add the usage descriptions the app needs (NSLocationWhenInUseUsageDescription, NSCameraUsageDescription, NSMicrophoneUsageDescription) to ios/App/App/Info.plist.");
        if (facts.FileSystemAccess.Count > 0 || facts.Downloads.Count > 0)
            steps.Add("Replace desktop file pickers and downloads with <input type=\"file\"> and the Capacitor Filesystem/Share plugins.");
        if (facts.Notifications.Count > 0) steps.Add("Replace web notifications with Capacitor Local Notifications or push notifications.");
        steps.Add("Review the generated touch and safe-area styles (public/hcg-mobile.css) and test the layout at phone widths, in portrait and landscape, with VoiceOver/TalkBack.");
        steps.Add(Target == MobileTarget.Android
            ? "Replace Capacitor's default app icon and splash screen (android/app/src/main/res) with your own."
            : "Replace Capacitor's default app icon and splash screen (ios/App/App/Assets.xcassets) with your own.");
        steps.Add(Target == MobileTarget.Android
            ? "For release: create your own upload key and signing configuration; publishing to Google Play stays manual."
            : "For devices and release: choose your Apple Developer team and signing in Xcode; TestFlight and App Store submission stay manual.");
        return steps;
    }

    private static (int Files, int Lines) Count(string dir, string outDir, string sub)
    {
        var root = System.IO.Path.Combine(dir, sub);
        if (!Directory.Exists(root)) return (0, 0);
        int files = 0, lines = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (System.IO.Path.GetRelativePath(dir, file).Replace('\\', '/').StartsWith(outDir + "/", StringComparison.Ordinal)) continue;
            files++;
            if (sub == "src" && new FileInfo(file).Length < 1_000_000) lines += File.ReadLines(file).Count();
        }
        return (files, lines);
    }

    private static string SuggestId(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant().Replace("@", "").Replace('/', '.'), "[^a-z0-9.]", "").Trim('.');
        var parts = slug.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => char.IsDigit(p[0]) ? "app" + p : p).ToList();
        if (parts.Count == 0) parts.Add("app");
        return (name.Contains("hcg", StringComparison.OrdinalIgnoreCase) ? "com.hakmicapitalgroup." : "com.example.") + string.Join('.', parts);
    }

    private string[] Signing => Target == MobileTarget.Android
        ?
        [
            "Debug builds are signed automatically with the Android debug key (installable on emulators and on devices with USB debugging).",
            "Release builds need your own upload/signing key. HCG never creates release keys.",
            "Google Play upload and store listing stay manual.",
        ]
        :
        [
            "Simulator builds need no signing.",
            "Devices, TestFlight and the App Store need your Apple Developer team, certificate and provisioning profile. HCG never creates them.",
            "TestFlight upload and App Store submission stay manual.",
        ];

    private IReadOnlyList<ToolchainRequirement> Toolchains(ToolchainReport? r)
    {
        var list = new List<ToolchainRequirement>();
        var node = r?.NodeInstallations.FirstOrDefault();
        list.Add(new ToolchainRequirement(ToolchainIds.Node, "Node.js and npm (web build, Capacitor CLI)", "22", node is not null,
            node is not null ? $"Node.js {node.Version} ({node.NodePath})" : "Install Node.js 22 or newer (Capacitor 8 requires it)."));
        ToolchainRequirement T(string id, string name, string? min, string fallback)
        {
            var t = r?.Find(id);
            return new ToolchainRequirement(id, name, min, t?.IsAvailable == true, t?.IsAvailable == true ? (t.Version is { } v ? $"{name} {v}" : t.ExecutablePath) : t?.InstallHint ?? fallback);
        }
        if (Target == MobileTarget.Android)
        {
            list.Add(T(ToolchainIds.Jdk, "JDK 21 (Capacitor 8's Android build)", "21", "Install JDK 21."));
            list.Add(T(ToolchainIds.AndroidSdk, "Android SDK (API 36 platform)", "36", "Install the Android SDK."));
            list.Add(T(ToolchainIds.Adb, "adb (for emulator/device launch)", null, "SDK Manager → Platform-Tools."));
        }
        else
        {
            list.Add(T(ToolchainIds.Xcode, "Xcode (a Mac is required)", "16", "Install Xcode on a Mac."));
            list.Add(T(ToolchainIds.IosSdk, "iOS SDK and Simulator", null, "Install the iOS platform in Xcode › Settings › Components."));
        }
        return list;
    }

    // ───────────── Generation (pure: copies and writes files, runs nothing) ─────────────

    public (IReadOnlyList<string> Steps, string BuildRoot) Generate(SourceIndex index, PortingAssessment assessment, PortOptions options, PortOutput output)
    {
        if (assessment.Capability is PortingCapability.BuildExistingTarget or PortingCapability.NotSupported)
            throw new InvalidOperationException("Nothing to generate for this source: " + assessment.Summary);
        if (assessment.Capability == PortingCapability.BlockedNeedsPorting)
            throw new InvalidOperationException("This web app has blockers. Resolve them in the project first; web packaging has no draft mode (a draft would run with missing parts).");
        if (!AppIdRegex().IsMatch(options.AppId))
            throw new InvalidOperationException($"'{options.AppId}' is not a valid app id (reverse-DNS, letters and digits, e.g. com.example.notes).");
        if (string.IsNullOrWhiteSpace(options.AppName) || options.AppName.IndexOfAny(['/', '\\', ':', '"', '<', '>']) >= 0)
            throw new InvalidOperationException($"'{options.AppName}' cannot be used as the app name.");

        var dir = assessment.Source.Directory;
        var full = index.FullPath(dir);
        var facts = WebDesktopConversion.Scan(full);
        var steps = new List<string>();
        var copied = CopyProject(full, dir, facts.OutDir, output);
        steps.Add($"Copied the web project ({copied} files) into a separate folder. node_modules, {facts.OutDir}/, version-control data and .env files with values were left out; the original is not modified.");

        output.Modify("package.json", PortOutput.Join(dir, "package.json"), PackageJson(facts, Platform), $"Capacitor {CapacitorVersion} ({Platform}) and the mobile scripts.");
        steps.Add($"package.json: @capacitor/core, @capacitor/{Platform} and @capacitor/cli {CapacitorVersion} (exact versions); scripts mobile:build and mobile:sync.");

        output.Add(ConfigFile, CapacitorConfig(options, facts.OutDir), "Capacitor configuration: app id, name, the web build folder; no development-server URL.");
        steps.Add($"{ConfigFile}: appId {options.AppId}, appName {options.AppName}, webDir {facts.OutDir}. No server.url, so the app always uses its packaged files.");

        output.Add(MobileCss, MobileStyles, "Touch and safe-area styles for phones.");
        if (facts.IndexHtml is not null)
        {
            output.Modify("index.html", PortOutput.Join(dir, "index.html"), IndexHtml(facts.IndexHtml), "Phone viewport (viewport-fit=cover) and the touch/safe-area styles.");
            steps.Add("index.html: phone viewport with viewport-fit=cover, and a link to hcg-mobile.css.");
        }
        steps.Add($"Validate (next step, runs code after you confirm trust): npm install, npm run {facts.BuildScript}, then Capacitor's CLI creates the native {PlatformName} project ({Platform}/) from its template and copies the build into it.");
        steps.Add(Target == MobileTarget.Android
            ? "Build: Mobile Build Studio installs, builds the web app, syncs it into android/ and runs Gradle (debug APK)."
            : "Build (on a Mac): Mobile Build Studio installs, builds the web app, syncs it into ios/ and runs xcodebuild (Simulator app).");
        return (steps, Target == MobileTarget.Android ? "android" : "ios/App");
    }

    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", ".hg", ".svn", "release", ".vite", ".turbo", ".cache", "coverage", "playwright-report", "test-results", "HCG-PORT",
    };

    private static int CopyProject(string fullDir, string relDir, string outDir, PortOutput output)
    {
        var count = 0;
        var outFull = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullDir, outDir));
        void Walk(DirectoryInfo d)
        {
            foreach (var e in d.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                if (e.LinkTarget is not null) continue; // links are never followed or copied
                if (e is DirectoryInfo sub)
                {
                    if (Excluded.Contains(sub.Name) || string.Equals(System.IO.Path.GetFullPath(sub.FullName), outFull, StringComparison.Ordinal)) continue;
                    Walk(sub);
                }
                else if (!WebDesktopConversion.IsEnvFileWithValues(e.Name) && e.Name != ".DS_Store")
                {
                    var rel = System.IO.Path.GetRelativePath(fullDir, e.FullName).Replace('\\', '/');
                    output.CopyFile(PortOutput.Join(relDir, rel), rel);
                    count++;
                }
            }
        }
        Walk(new DirectoryInfo(fullDir));
        return count;
    }

    internal static string PackageJson(WebProjectFacts facts, string platform)
    {
        var pkg = facts.Package!.DeepClone().AsObject();
        JsonObject Section(string name) => pkg[name] as JsonObject ?? (JsonObject)(pkg[name] = new JsonObject());
        var deps = Section("dependencies");
        deps["@capacitor/core"] = CapacitorVersion;
        deps[$"@capacitor/{platform}"] = CapacitorVersion;
        Section("devDependencies")["@capacitor/cli"] = CapacitorVersion;
        var scripts = Section("scripts");
        scripts["mobile:build"] = $"npm run {facts.BuildScript}";
        scripts["mobile:sync"] = $"npm run {facts.BuildScript} && cap sync {platform}";
        foreach (var name in new[] { "dependencies", "devDependencies" })
            if (pkg[name] is JsonObject o)
                pkg[name] = new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        return pkg.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    internal static string CapacitorConfig(PortOptions o, string webDir) => new JsonObject
    {
        ["appId"] = o.AppId,
        ["appName"] = o.AppName,
        ["webDir"] = webDir,
        ["android"] = new JsonObject { ["allowMixedContent"] = false },
        ["ios"] = new JsonObject { ["contentInset"] = "never" },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    internal static string IndexHtml(string html)
    {
        var nl = html.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var result = ViewportTagRegex().Match(html) is { Success: true } m
            ? html[..m.Index] + (m.Value.Contains("viewport-fit", StringComparison.OrdinalIgnoreCase) ? m.Value : ViewportContentRegex().Replace(m.Value, c => c.Value[..^1] + ", viewport-fit=cover" + c.Value[^1..], 1)) + html[(m.Index + m.Length)..]
            : HeadRegex().Replace(html, h => h.Value + nl + "    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\" />", 1);
        var link = "<link rel=\"stylesheet\" href=\"/hcg-mobile.css\" />";
        return result.Contains(link, StringComparison.Ordinal) ? result : HeadCloseRegex().Replace(result, h => nl + "    " + link + h.Value, 1);
    }

    internal const string MobileStyles = """
        /* Added by HCG Executable Maker for the mobile app (web-based mobile packaging). Review it with your design. */
        html {
          -webkit-text-size-adjust: 100%;
          text-size-adjust: 100%;
        }
        body {
          /* Keep content clear of notches, rounded corners and the home indicator. */
          padding-top: env(safe-area-inset-top);
          padding-right: env(safe-area-inset-right);
          padding-bottom: env(safe-area-inset-bottom);
          padding-left: env(safe-area-inset-left);
          -webkit-tap-highlight-color: transparent;
        }
        a, button, input, select, textarea, label, [role="button"] {
          touch-action: manipulation; /* no double-tap zoom delay */
        }
        @media (pointer: coarse) {
          /* At least 44×44 points for controls you tap (Apple and Android accessibility guidance). */
          button, select, [role="button"], input:not([type="hidden"]):not([type="checkbox"]):not([type="radio"]) {
            min-height: 44px;
          }
          input[type="checkbox"], input[type="radio"] {
            min-width: 24px;
            min-height: 24px;
          }
          input, select, textarea {
            font-size: max(16px, 1em); /* iOS zooms into smaller text fields */
          }
        }
        :focus-visible {
          outline: 3px solid currentColor;
          outline-offset: 2px;
        }

        """;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$")] private static partial Regex AppIdRegex();
    [GeneratedRegex(@"<meta[^>]+name\s*=\s*['""]viewport['""][^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ViewportTagRegex();
    [GeneratedRegex(@"content\s*=\s*(['""])[^'""]*\1", RegexOptions.IgnoreCase)] private static partial Regex ViewportContentRegex();
    [GeneratedRegex(@"<head[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex HeadRegex();
    [GeneratedRegex(@"\s*</head>", RegexOptions.IgnoreCase)] private static partial Regex HeadCloseRegex();
}
