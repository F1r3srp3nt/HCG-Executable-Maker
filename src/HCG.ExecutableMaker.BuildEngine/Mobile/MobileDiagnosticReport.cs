using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>
/// A plain-text report that establishes exactly what is running and what would be built: this Maker build (version,
/// commit, executable), HCG's selected Xcode and its supported iOS range, the imported copy of the project (where it came
/// from, Git revision or ZIP hash, whether the original changed since, hashes of the Xcode project and schemes), every
/// scheme with its targets' deployment targets, the saved scheme, and readiness per scheme × output with the precise
/// state. Optionally it reproduces outside Maker: xcodebuild run directly on the same copy with the same Xcode.
/// Read-only apart from the optional reproduction's own derived-data folder.
/// </summary>
public static class MobileDiagnosticReport
{
    private static readonly MobileOutput[] IosOutputs = [MobileOutput.IosSimulatorApp, MobileOutput.IosArchive, MobileOutput.IosIpa];

    public static string Create(ProjectRecord project, ToolchainReport? report, string? selectedScheme, IAppPaths paths, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        L("HCG Executable Maker — mobile diagnostic report");
        L($"Generated: {now:yyyy-MM-dd HH:mm:ss} UTC");
        L();
        L("== Running Maker");
        L($"Build: {HostInfo.BuildIdentity}");
        L($"Executable: {HostInfo.ExecutablePath}");
        L($"App bundle: {HostInfo.AppBundlePath ?? "(not running from an .app bundle)"}");
        L($"HCG data folder: {paths.Root}");
        L();
        L("== Selected Xcode (HCG's own selection; the system setting is not changed)");
        if (report?.Xcode is { } x)
        {
            L($"State: {x.State} — {x.Diagnosis}");
            L($"Developer directory: {x.ActiveDeveloperDirectory ?? "(none)"} (source: {x.Source})");
            L($"Version: Xcode {x.VerifiedVersion ?? "?"} ({x.VerifiedBuild ?? "?"})");
            L(x.IosDeploymentRange is { } r
                ? $"iPhoneOS SDK {r.SdkVersion}: deployment targets iOS {r.Minimum} to {r.Maximum} (recommended {r.Recommended ?? "?"})"
                : "iPhoneOS deployment range: not readable (no SDKSettings.json)");
            L($"SDKs: {string.Join(", ", x.Sdks)}");
        }
        else L(report is null ? "Toolchains not detected yet." : $"No Xcode diagnosis on this host ({report.Host.DisplayName}).");
        L();

        L($"== Project “{project.Name}” ({project.Id:N})");
        L($"Imported from: {project.SourceKind} · {project.SourceLocation}");
        var snap = SourceSnapshot.Inspect(project);
        L($"Imported copy (what HCG builds): {project.SourceDirectory}");
        L($"  taken {project.ImportedAt:yyyy-MM-dd HH:mm} UTC · {snap.CopyFiles} files · content SHA-256 {snap.CopySha256}");
        if (snap.OriginalCommit is { } c) L($"Original Git revision: {c}{(snap.OriginalBranch is { } b ? " (" + b + ")" : "")}");
        if (snap.OriginalSha256 is { } o) L($"Original {(project.SourceKind == ProjectSourceKind.ZipArchive ? "ZIP SHA-256" : "content SHA-256")}: {o}{(snap.OriginalFiles is { } of ? $" · {of} files" : "")}");
        if (snap.ImportedArchiveSha256 is { } ia) L($"ZIP preserved at import, SHA-256: {ia}");
        L($"Copy vs original: {snap.Describe()}");

        var m = project.Analysis?.Mobile;
        if (m?.Ios is not { } ios)
        {
            L(m is null ? "Not recognized as a mobile project." : $"Framework: {m.Framework.DisplayName()} (no Xcode project).");
            return sb.ToString();
        }
        var container = ios.WorkspacePath ?? ios.ProjectPath ?? "(none)";
        L($"Xcode container: {container}");
        foreach (var f in KeyFiles(project.SourceDirectory, ios)) L($"  {f}");
        L($"Configurations: {string.Join(", ", ios.Configurations)}");
        L("Schemes (app targets each one builds and archives):");
        foreach (var choice in MobileReadiness.SchemeChoices(ios, report))
        {
            var info = ios.Schemes.FirstOrDefault(s => s.Name == choice.Name);
            L($"  {choice.Label}");
            L($"      targets: {string.Join(", ", choice.AppTargets.Select(t => $"{t.Name} (iOS {t.DeploymentTarget ?? "?"}, {t.BundleIdentifier})"))}" +
              (info?.ArchiveConfiguration is { } ac ? $" · archive configuration {ac}" : "") + (info is { Shared: false } ? " · not shared" : ""));
        }
        var def = MobileReadiness.DefaultScheme(ios);
        L($"Selected scheme: {selectedScheme ?? "(none saved)"}{(selectedScheme is null ? $" → Maker builds the default {def}" : "")}");
        L();

        L("== Readiness (scheme × output; unsigned archive, project-default signing for IPA)");
        foreach (var scheme in ios.Schemes.Select(s => s.Name).DefaultIfEmpty(def).OfType<string>())
            foreach (var output in IosOutputs)
            {
                var opts = new MobileBuildOptions { Framework = m.Framework, Output = output, Scheme = scheme, Signing = output == MobileOutput.IosArchive ? MobileSigningMode.Unsigned : MobileSigningMode.ProjectDefault };
                var state = MobileBuildState.Classify(MobileReadiness.Evaluate(m, output, opts, report), output.DisplayName());
                L($"{(scheme == (selectedScheme ?? def) ? "*" : " ")} {scheme} · {output.DisplayName()}: [{state.Tag}] {Trim(state.Detail, 600)}");
            }
        L("  (* = the scheme Maker uses now. Every build also needs the per-build trust confirmation in section 5.)");
        L();

        L("== Reproduce outside Maker (Terminal, same copy, same Xcode)");
        var dev = report?.Xcode?.ActiveDeveloperDirectory;
        var flag = container.EndsWith(".xcworkspace", StringComparison.OrdinalIgnoreCase) ? "-workspace" : "-project";
        L($"export DEVELOPER_DIR={Q(dev ?? "/Applications/Xcode.app/Contents/Developer")}");
        L($"cd {Q(project.SourceDirectory)}");
        L($"xcodebuild -list {flag} {Q(container)}");
        foreach (var s in ios.Schemes)
            foreach (var cfg in ios.Configurations)
                L($"xcodebuild {flag} {Q(container)} -scheme {Q(s.Name)} -configuration {cfg} -destination 'generic/platform=iOS Simulator' -showBuildSettings | grep -E '^Build settings for|^ *IPHONEOS_DEPLOYMENT_TARGET ='");
        var buildScheme = selectedScheme ?? def;
        L($"xcodebuild build {flag} {Q(container)} -scheme {Q(buildScheme ?? "")} -configuration Debug -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' -derivedDataPath /tmp/hcg-repro CODE_SIGNING_ALLOWED=NO");
        L($"Or let Maker run these and append the results: “HCG Executable Maker” --diagnose-mobile --project {project.Id:N} --reproduce");
        return sb.ToString();
    }

    /// <summary>Hashes of the files that decide schemes and deployment targets, to compare against a known source revision.</summary>
    private static IEnumerable<string> KeyFiles(string source, IosProjectInfo ios)
    {
        var files = new List<string>();
        if (ios.ProjectPath is { } p) files.Add(p + "/project.pbxproj");
        files.AddRange(ios.Schemes.Select(s => s.File).Where(f => f.Length > 0));
        foreach (var rel in files.Distinct())
        {
            var full = Path.Combine(source, rel);
            yield return File.Exists(full) ? $"{rel}: SHA-256 {SourceSnapshot.FileSha256(full)}" : $"{rel}: missing";
        }
    }

    /// <summary>
    /// Runs xcodebuild directly (not through Maker's build engine) on the imported copy with HCG's selected Xcode: scheme
    /// list, the deployment target every target of every scheme resolves to (Simulator and device, each configuration),
    /// then a Simulator build of the scheme Maker would use. Returns the commands and their results as text.
    /// </summary>
    public static async Task<string> ReproduceAsync(ProjectRecord project, ToolchainReport report, string? selectedScheme, IProcessRunner runner, string workDirectory, CancellationToken ct)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        L("== Outside Maker: xcodebuild run directly on the imported copy");
        if (report.Xcode is not { ActiveDeveloperDirectory: { } dev } x || report.Host.Platform != TargetPlatform.MacOS)
        {
            L("Not possible here: needs macOS with an Xcode selected for HCG.");
            return sb.ToString();
        }
        if (project.Analysis?.Mobile?.Ios is not { } ios) { L("No Xcode project."); return sb.ToString(); }
        var container = ios.WorkspacePath ?? ios.ProjectPath!;
        var flag = container.EndsWith(".xcworkspace", StringComparison.OrdinalIgnoreCase) ? "-workspace" : "-project";
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.Ordinal);
        env["DEVELOPER_DIR"] = dev;
        var xcodebuild = File.Exists("/usr/bin/xcodebuild") ? "/usr/bin/xcodebuild" : Path.Combine(dev, "usr", "bin", "xcodebuild");
        L($"DEVELOPER_DIR={dev} · Xcode {x.VerifiedVersion} · iPhoneOS range {x.IosDeploymentRange?.Minimum ?? "?"}–{x.IosDeploymentRange?.Maximum ?? "?"}");
        L($"Working directory: {project.SourceDirectory}");

        async Task<(int Exit, List<string> Lines)> Run(TimeSpan timeout, params string[] args)
        {
            var lines = new List<string>();
            var spec = new ProcessSpec { Executable = xcodebuild, Arguments = args, WorkingDirectory = project.SourceDirectory, Environment = env, Timeout = timeout };
            L("$ " + spec.DisplayCommand);
            var r = await runner.RunAsync(spec, (_, line) => { lock (lines) lines.Add(line); }, ct).ConfigureAwait(false);
            if (r.TimedOut) lines.Add($"(timed out after {timeout.TotalMinutes:0} min)");
            return (r.TimedOut ? -1 : r.ExitCode, lines);
        }

        var (listExit, list) = await Run(TimeSpan.FromMinutes(3), "-list", flag, container).ConfigureAwait(false);
        foreach (var l in list.SkipWhile(l => !l.Contains("Targets:", StringComparison.Ordinal))) L("  " + l.Trim());
        L($"  exit {listExit}");

        var min = IosDeploymentRange.Parse(x.IosDeploymentRange?.Minimum);
        foreach (var s in ios.Schemes)
            foreach (var cfg in ios.Configurations)
                foreach (var dest in new[] { "generic/platform=iOS Simulator", "generic/platform=iOS" })
                {
                    var (exit, lines) = await Run(TimeSpan.FromMinutes(3), flag, container, "-scheme", s.Name, "-configuration", cfg, "-destination", dest, "-showBuildSettings").ConfigureAwait(false);
                    var target = "";
                    var resolved = new List<string>();
                    foreach (var l in lines)
                    {
                        if (l.StartsWith("Build settings for action", StringComparison.Ordinal)) target = l.Split(" target ").LastOrDefault()?.TrimEnd(':') ?? "";
                        else if (l.TrimStart().StartsWith("IPHONEOS_DEPLOYMENT_TARGET = ", StringComparison.Ordinal))
                        {
                            var v = l.Split('=', 2)[1].Trim();
                            var below = min is not null && IosDeploymentRange.Parse(v) is { } pv && pv < min;
                            resolved.Add($"{target} {v}{(below ? " ← below this Xcode's minimum" : "")}");
                        }
                    }
                    L($"  {s.Name} · {cfg} · {(dest.Contains("Simulator") ? "Simulator" : "device")}: {(resolved.Count == 0 ? $"no settings (exit {exit}) {Trim(string.Join(" | ", lines.Where(IsError)), 300)}" : string.Join("; ", resolved.Distinct()))}");
                }

        var scheme = selectedScheme ?? MobileReadiness.DefaultScheme(ios) ?? "";
        var derived = Path.Combine(workDirectory, "derived-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        var (buildExit, buildLines) = await Run(TimeSpan.FromMinutes(30), "build", flag, container, "-scheme", scheme, "-configuration", "Debug", "-sdk", "iphonesimulator",
            "-destination", "generic/platform=iOS Simulator", "-derivedDataPath", derived, "CODE_SIGNING_ALLOWED=NO", "COMPILER_INDEX_STORE_ENABLE=NO").ConfigureAwait(false);
        var firstError = buildLines.FirstOrDefault(IsError);
        L($"  Simulator build of scheme {scheme}: exit {buildExit} · {buildLines.LastOrDefault(l => l.Contains("** BUILD", StringComparison.Ordinal))?.Trim() ?? "no BUILD line"}");
        L($"  first error: {firstError?.Trim() ?? "(none)"}");
        foreach (var w in buildLines.Where(l => l.Contains("deployment target", StringComparison.OrdinalIgnoreCase)).Distinct().Take(5)) L("  deployment diagnostic: " + Trim(w.Trim(), 400));
        var log = Path.Combine(workDirectory, $"reproduce-{scheme}.log");
        await File.WriteAllLinesAsync(log, buildLines, ct).ConfigureAwait(false);
        L($"  full build log: {log}");
        try { Directory.Delete(derived, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return sb.ToString();
    }

    private static bool IsError(string l) => l.Contains("error:", StringComparison.Ordinal) || l.Contains("** BUILD FAILED", StringComparison.Ordinal);
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static string Q(string s) => s.Contains(' ') || s.Contains('\'') ? "'" + s.Replace("'", "'\\''") + "'" : s;
}
