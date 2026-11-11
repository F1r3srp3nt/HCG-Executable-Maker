using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>Capacitor apps: package.json with @capacitor/core plus capacitor.config.*; read as text, nothing runs.</summary>
public static partial class CapacitorAnalyzer
{
    private static readonly string[] Configs = ["capacitor.config.json", "capacitor.config.ts", "capacitor.config.js"];

    public static IReadOnlyList<string> FindRoots(SourceIndex index) =>
        index.Files.Where(f => Configs.Contains(SourceIndex.FileName(f)))
            .Select(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
            .Where(dir => index.Files.Contains(Join(dir, "package.json")) && DependsOnCapacitor(index, Join(dir, "package.json")))
            .Distinct().OrderBy(SourceIndex.Depth).ToList();

    public static CapacitorProjectInfo Analyze(SourceIndex index, string root)
    {
        var config = Configs.Select(c => Join(root, c)).First(index.Files.Contains);
        var configText = File.ReadAllText(index.FullPath(config));
        string? Value(string key) => JsonValueRegex(key).Match(configText) is { Success: true } m ? m.Groups["v"].Value : null;
        JsonObject? pkg = null;
        try { pkg = JsonNode.Parse(File.ReadAllText(index.FullPath(Join(root, "package.json"))), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject; }
        catch (JsonException) { }
        var scripts = pkg?["scripts"] as JsonObject;
        var build = scripts?.Select(s => s.Key).OrderBy(k => k == "build" ? 0 : 1).FirstOrDefault(k => k == "build" || scripts[k]?.ToString().Contains("vite build", StringComparison.Ordinal) == true);
        var version = (pkg?["dependencies"] as JsonObject)?["@capacitor/core"]?.ToString();
        return new CapacitorProjectInfo
        {
            Root = root, ConfigFile = config, AppId = Value("appId"), AppName = Value("appName"), WebDir = Value("webDir") ?? "dist", BuildScript = build,
            CapacitorVersion = version, HasLockFile = index.Files.Contains(Join(root, "package-lock.json")),
            HasAndroid = index.Files.Contains(Join(root, "android/settings.gradle")) || index.Files.Contains(Join(root, "android/settings.gradle.kts")),
            HasIos = index.Files.Any(f => f.StartsWith(Join(root, "ios/App/"), StringComparison.Ordinal) && f.Contains(".xcodeproj/", StringComparison.Ordinal)),
        };
    }

    private static bool DependsOnCapacitor(SourceIndex index, string packageJson)
    {
        try { return File.ReadAllText(index.FullPath(packageJson)).Contains("\"@capacitor/core\"", StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    public static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;

    private static Regex JsonValueRegex(string key) => new($@"['""]?{key}['""]?\s*:\s*['""](?<v>[^'""]+)['""]");
}
