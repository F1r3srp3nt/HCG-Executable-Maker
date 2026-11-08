using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// Finds Node.js installations without relying on the login shell, so a Finder launch sees the same Node as Terminal:
/// PATH, Homebrew, /usr/local, nvm, Volta, fnm, asdf, /opt/node*, Program Files. Every candidate is RUN
/// (<c>node --version</c>); a file that merely exists is never reported as ready. npm is located as the
/// npm-cli.js that ships with that Node (its version read from npm's package.json) and is run through that node.
/// </summary>
public sealed partial class NodeDiscovery(IProcessRunner runner)
{
    public sealed record Candidate(string Path, string Source);
    public sealed record Result(IReadOnlyList<NodeInstallation> Installations, IReadOnlyList<string> Rejected);

    public static IEnumerable<Candidate> Candidates(TargetPlatform platform, string? pathVariable, string home, Func<string, IEnumerable<string>>? listDirs = null)
    {
        listDirs ??= dir => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir) : [];
        var exe = platform == TargetPlatform.Windows ? "node.exe" : "node";
        foreach (var dir in (pathVariable ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return new Candidate(System.IO.Path.Combine(dir, exe), "PATH");
        if (platform == TargetPlatform.Windows)
        {
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
                if (pf is { Length: > 0 }) yield return new Candidate(System.IO.Path.Combine(pf, "nodejs", exe), "Program Files");
            if (Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData)
                foreach (var d in listDirs(System.IO.Path.Combine(appData, "nvm"))) yield return new Candidate(System.IO.Path.Combine(d, exe), "nvm-windows");
            yield break;
        }
        yield return new Candidate("/opt/homebrew/bin/node", "Homebrew");
        foreach (var d in listDirs("/opt/homebrew/opt").Where(d => System.IO.Path.GetFileName(d).StartsWith("node", StringComparison.Ordinal)))
            yield return new Candidate(System.IO.Path.Combine(d, "bin", "node"), "Homebrew");
        yield return new Candidate("/usr/local/bin/node", "/usr/local");
        foreach (var d in listDirs("/usr/local/opt").Where(d => System.IO.Path.GetFileName(d).StartsWith("node", StringComparison.Ordinal)))
            yield return new Candidate(System.IO.Path.Combine(d, "bin", "node"), "Homebrew");
        var nvmDir = Environment.GetEnvironmentVariable("NVM_DIR") is { Length: > 0 } nd ? nd : System.IO.Path.Combine(home, ".nvm");
        foreach (var d in listDirs(System.IO.Path.Combine(nvmDir, "versions", "node"))) yield return new Candidate(System.IO.Path.Combine(d, "bin", "node"), "nvm");
        foreach (var d in listDirs(System.IO.Path.Combine(home, ".volta", "tools", "image", "node"))) yield return new Candidate(System.IO.Path.Combine(d, "bin", "node"), "Volta");
        foreach (var root in new[] { System.IO.Path.Combine(home, ".local", "share", "fnm", "node-versions"), System.IO.Path.Combine(home, "Library", "Application Support", "fnm", "node-versions") })
            foreach (var d in listDirs(root)) yield return new Candidate(System.IO.Path.Combine(d, "installation", "bin", "node"), "fnm");
        foreach (var d in listDirs(System.IO.Path.Combine(home, ".asdf", "installs", "nodejs"))) yield return new Candidate(System.IO.Path.Combine(d, "bin", "node"), "asdf");
        foreach (var d in listDirs("/opt").Where(d => System.IO.Path.GetFileName(d).StartsWith("node", StringComparison.Ordinal)))
            yield return new Candidate(System.IO.Path.Combine(d, "bin", "node"), System.IO.Path.GetFileName(d));
        yield return new Candidate("/usr/bin/node", "system");
    }

    /// <summary>npm-cli.js that ships with this Node (&lt;prefix&gt;/lib/node_modules/npm on Unix, &lt;dir&gt;\node_modules\npm on Windows).</summary>
    public static string? FindNpmCli(string nodePath)
    {
        var bin = System.IO.Path.GetDirectoryName(nodePath)!;
        foreach (var npmDir in new[]
                 {
                     System.IO.Path.Combine(System.IO.Path.GetDirectoryName(bin) ?? bin, "lib", "node_modules", "npm"),
                     System.IO.Path.Combine(bin, "node_modules", "npm"),
                     // Homebrew's /opt/homebrew/bin/node → Cellar; npm lives under the Homebrew prefix.
                     "/opt/homebrew/lib/node_modules/npm", "/usr/local/lib/node_modules/npm",
                 })
        {
            var cli = System.IO.Path.Combine(npmDir, "bin", "npm-cli.js");
            if (File.Exists(cli)) return cli;
        }
        return null;
    }

    public static string? ReadNpmVersion(string npmCli)
    {
        try
        {
            var pkg = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(npmCli)!)!, "package.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public async Task<Result> DiscoverAsync(TargetPlatform platform, CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<NodeInstallation>();
        var rejected = new List<string>();
        foreach (var c in Candidates(platform, Environment.GetEnvironmentVariable("PATH"), home))
        {
            if (!File.Exists(c.Path)) continue;
            string real;
            try { real = PathGuard.GetRealPath(c.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { real = c.Path; }
            // Version-manager shims (Volta's bin/node, asdf shims) are not installations: their real targets are listed separately.
            if (!seen.Add(real) || real.Contains("/.volta/bin/", StringComparison.Ordinal) || real.Contains("/.asdf/shims/", StringComparison.Ordinal)) continue;
            try
            {
                var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec
                {
                    Executable = c.Path, Arguments = ["--version"], Timeout = TimeSpan.FromSeconds(15),
                    Environment = new Dictionary<string, string> { ["PATH"] = System.IO.Path.GetDirectoryName(c.Path)!, ["HOME"] = home },
                }, ct).ConfigureAwait(false);
                var m = NodeVersionRegex().Match(stdout);
                if (!r.Succeeded || !m.Success)
                {
                    rejected.Add($"{c.Path} ({c.Source}): {(r.Succeeded ? "printed no version" : $"exit code {r.ExitCode}: {(stderr + stdout).Trim().Split('\n')[0]}")}");
                    continue;
                }
                var npm = FindNpmCli(c.Path);
                found.Add(new NodeInstallation(c.Path, m.Groups[1].Value, c.Source, npm, npm is null ? null : ReadNpmVersion(npm)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                rejected.Add($"{c.Path} ({c.Source}): {ex.Message}");
            }
        }
        var ordered = found.OrderByDescending(n => ParseVersion(n.Version)).ToList();
        return new Result(ordered, rejected);
    }

    private static Version ParseVersion(string v) => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);

    public static IEnumerable<ToolchainInfo> Rows(Result result)
    {
        const string category = "Node.js / Electron";
        const string url = "https://nodejs.org/en/download";
        var hint = "Install Node.js (LTS) from " + url + " or with Homebrew/nvm/Volta, then press Refresh.";
        if (result.Installations.Count == 0)
        {
            yield return new ToolchainInfo
            {
                Id = ToolchainIds.Node, DisplayName = "Node.js", Category = category, SetupUrl = url,
                Status = result.Rejected.Count > 0 ? ToolchainStatus.ValidationFailed : ToolchainStatus.Missing,
                Detail = result.Rejected.Count > 0 ? "Found but not working: " + string.Join("; ", result.Rejected) : "Looked on PATH, Homebrew, /usr/local, nvm, Volta, fnm, asdf and /opt.",
                InstallHint = hint,
            };
            yield return new ToolchainInfo { Id = ToolchainIds.Npm, DisplayName = "npm", Category = category, Status = ToolchainStatus.Missing, InstallHint = "npm ships with Node.js.", SetupUrl = url };
            yield break;
        }
        var top = result.Installations[0];
        yield return new ToolchainInfo
        {
            Id = ToolchainIds.Node, DisplayName = "Node.js", Category = category, Status = ToolchainStatus.Installed, SetupUrl = url,
            Version = top.Version, ExecutablePath = top.NodePath, Source = top.Source,
            AdditionalVersions = result.Installations.Select(i => i.Version).ToList(),
            Detail = result.Installations.Count > 1
                ? "Installations: " + string.Join("; ", result.Installations.Select(i => $"{i.Version} {i.NodePath} ({i.Source})")) + ". Each project uses the newest one that satisfies its engines.node."
                : null,
        };
        yield return top.NpmCli is null
            ? new ToolchainInfo { Id = ToolchainIds.Npm, DisplayName = "npm", Category = category, Status = ToolchainStatus.Missing, Detail = $"npm-cli.js was not found next to {top.NodePath}.", InstallHint = "Reinstall Node.js; npm ships with it.", SetupUrl = url }
            : new ToolchainInfo { Id = ToolchainIds.Npm, DisplayName = "npm", Category = category, Status = top.NpmVersion is null ? ToolchainStatus.ValidationFailed : ToolchainStatus.Installed, Version = top.NpmVersion, ExecutablePath = top.NpmCli, Source = $"bundled with Node.js {top.Version}", SetupUrl = url, Detail = top.NpmVersion is null ? "npm's package.json could not be read." : null };
    }

    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)", RegexOptions.Multiline)]
    private static partial Regex NodeVersionRegex();
}
