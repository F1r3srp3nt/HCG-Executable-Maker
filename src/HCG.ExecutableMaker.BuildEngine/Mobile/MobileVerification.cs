using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>Diagnostics from Gradle, javac/kotlinc, clang/swiftc, xcodebuild, Flutter and MSBuild output.</summary>
public sealed partial class MobileOutputParser
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly MsBuildOutputParser _msbuild = new();

    public BuildMessage? Parse(string line)
    {
        BuildMessage? m = null;
        if (FileDiagnostic().Match(line) is { Success: true } f)
            m = new BuildMessage(Severity(f.Groups["sev"].Value), f.Groups["msg"].Value.Trim(), null, f.Groups["file"].Value, int.Parse(f.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture));
        else if (ProjectDiagnostic().Match(line) is { Success: true } pd)
            // xcodebuild's project-level diagnostics: "/…/App.xcodeproj: error: The iOS deployment target … (in target 'App' from project 'App')"
            m = new BuildMessage(Severity(pd.Groups["sev"].Value), pd.Groups["msg"].Value.Trim(), "xcodebuild", pd.Groups["file"].Value);
        else if (_msbuild.Parse(line) is { } ms) return ms;
        else if (KotlinDiagnostic().Match(line) is { Success: true } k)
            m = new BuildMessage(k.Groups["sev"].Value == "e" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, k.Groups["msg"].Value.Trim());
        else if (PlainDiagnostic().Match(line) is { Success: true } p)
            m = new BuildMessage(Severity(p.Groups["sev"].Value), p.Groups["msg"].Value.Trim());
        else if (line.StartsWith("FAILURE: ", StringComparison.Ordinal) || line.Contains("** BUILD FAILED **", StringComparison.Ordinal) || line.Contains("** ARCHIVE FAILED **", StringComparison.Ordinal) || line.Contains("** EXPORT FAILED **", StringComparison.Ordinal))
            m = new BuildMessage(DiagnosticSeverity.Error, line.Trim());
        if (m is null || m.Text.Length == 0) return null;
        return _seen.Add($"{m.Severity}|{m.File}|{m.Line}|{m.Text}") ? m : null;
    }

    private static DiagnosticSeverity Severity(string s) => s.StartsWith("err", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;

    // "/src/Main.java:12: error: ';' expected", "/src/App.swift:3:5: error: cannot find 'x' in scope"
    [GeneratedRegex(@"^(?<file>(?:[A-Za-z]:)?[^:\r\n]+\.\w+):(?<line>\d+):(?:\d+:)?\s*(?<sev>error|warning|fatal error):\s*(?<msg>.+)$")]
    private static partial Regex FileDiagnostic();

    // "/path/App.xcodeproj: error: …", "/path/Package.swift: warning: …", "App.xcworkspace: error: …" (no line number)
    [GeneratedRegex(@"^(?<file>(?:[A-Za-z]:)?[^\s:\r\n][^:\r\n]*\.(?:xcodeproj|xcworkspace|swift|plist|entitlements|xcconfig|storyboard|xib|xcassets|pbxproj|csproj|props|targets)): (?<sev>error|warning|fatal error): (?<msg>.+)$")]
    private static partial Regex ProjectDiagnostic();

    // Kotlin: "e: file:///src/Main.kt:3:5 Unresolved reference: x", "w: ..."
    [GeneratedRegex(@"^(?<sev>[ew]): (?<msg>.+)$")]
    private static partial Regex KotlinDiagnostic();

    // xcodebuild / Flutter: "error: No profiles for 'com.x' were found", "Error: Gradle task assembleDebug failed"
    [GeneratedRegex(@"^\s*(?<sev>error|warning|Error|Warning|ERROR):\s+(?<msg>.+)$")]
    private static partial Regex PlainDiagnostic();
}

/// <summary>
/// Verifies a mobile artifact: static inspection (always) plus the official tools when they are installed
/// (apksigner, bundletool validate, codesign). Results never imply store approval.
/// </summary>
public sealed partial class MobileArtifactVerifier(IProcessRunner runner)
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(3);

    public async Task<(MobileArtifactReport Report, List<VerificationCheck> Checks)> VerifyAsync(string path, ToolchainReport? toolchains, CancellationToken ct)
    {
        var report = MobileArtifactInspector.Inspect(path, ct);
        var checks = new List<VerificationCheck>(report.Checks);
        if (report.Checks.All(c => c.Name != "Structure")) checks.Insert(0, Metadata(report));
        if (toolchains is null || report.Status == VerificationStatus.Failed) return (report, checks);

        var java = MobileArgs.InspectedJdk(toolchains)?.JavaExecutable;
        switch (report.Kind)
        {
            case MobileArtifactKind.Apk when report.Signing != MobileSigningState.Unsigned && java is not null
                                             && toolchains.Find(ToolchainIds.Apksigner)?.ExecutablePath is { } apksigner:
                var jar = Path.Combine(Path.GetDirectoryName(apksigner)!, "lib", "apksigner.jar");
                if (File.Exists(jar))
                    checks.Add(await ToolCheckAsync("apksigner verify", java, ["-jar", jar, "verify", "--print-certs", "--verbose", path], ct,
                        output => ApksignerSchemes().Matches(output).Select(m => m.Groups[1].Value).ToList() is { Count: > 0 } s ? "Verified: " + string.Join(", ", s) : "Verified").ConfigureAwait(false));
                break;
            case MobileArtifactKind.Aab when java is not null && toolchains.Find(ToolchainIds.Bundletool) is { IsAvailable: true, ExecutablePath: { } bundletool }:
                checks.Add(await ToolCheckAsync("bundletool validate", java, ["-jar", bundletool, "validate", "--bundle=" + path], ct, _ => "Bundle is valid").ConfigureAwait(false));
                break;
            case MobileArtifactKind.IosApp or MobileArtifactKind.XcArchive when OperatingSystem.IsMacOS()
                                             && report.Signing != MobileSigningState.Unsigned && File.Exists("/usr/bin/codesign"):
                var app = report.Kind == MobileArtifactKind.IosApp ? path : FindArchivedApp(path);
                if (app is not null)
                    checks.Add(await ToolCheckAsync("codesign --verify", "/usr/bin/codesign", ["--verify", "--deep", "--strict", "--verbose=2", app], ct, _ => "Code signature is valid").ConfigureAwait(false));
                break;
        }
        return (report, checks);
    }

    public static VerificationCheck Metadata(MobileArtifactReport r)
    {
        var parts = new List<string>();
        if (r.PackageId is not null) parts.Add(r.PackageId);
        if (r.VersionName is not null || r.VersionCode is not null) parts.Add($"version {r.VersionName} ({r.VersionCode})");
        if (r.MinimumOs is not null) parts.Add((r.Platform == "android" ? "minSdk " : "min iOS ") + r.MinimumOs);
        if (r.TargetSdk is not null) parts.Add("targetSdk " + r.TargetSdk);
        if (r.Architectures.Count > 0) parts.Add(string.Join("/", r.Architectures));
        parts.Add("signing: " + r.Signing);
        if (r.Platform is not null && r.Platform != "android") parts.Add(r.Platform);
        parts.Add(r.IsDistributable ? "distribution-signed" : "not distributable");
        return new VerificationCheck("Metadata", VerificationStatus.Passed, string.Join(" · ", parts));
    }

    private static string? FindArchivedApp(string archive)
    {
        var apps = Path.Combine(archive, "Products", "Applications");
        return Directory.Exists(apps) ? Directory.EnumerateDirectories(apps, "*.app").FirstOrDefault() : null;
    }

    private async Task<VerificationCheck> ToolCheckAsync(string name, string exe, List<string> args, CancellationToken ct, Func<string, string> success)
    {
        var (result, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec
        {
            Executable = exe, Arguments = args, Timeout = ToolTimeout,
            Environment = Security.BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment,
        }, ct).ConfigureAwait(false);
        if (result.Succeeded) return new VerificationCheck(name, VerificationStatus.Passed, success(stdout + stderr));
        var detail = string.Join(" ", (stderr + "\n" + stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4));
        return new VerificationCheck(name, VerificationStatus.Failed, result.TimedOut ? "Timed out." : $"Exit code {result.ExitCode}: {detail}");
    }

    [GeneratedRegex(@"Verified using (v[\d.]+) scheme \([^)]*\): true")]
    private static partial Regex ApksignerSchemes();
}
