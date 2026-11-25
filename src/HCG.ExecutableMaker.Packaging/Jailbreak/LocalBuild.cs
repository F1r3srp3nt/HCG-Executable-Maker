using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Theos;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// Provenance of a package built on this computer (no CI run, no artifact). Every field is measured here or typed by the
/// person building; nothing is invented. HCG Repo records these fields from a <c>kind = "local-file"</c> import request.
/// </summary>
/// <param name="BuiltBy">The Maker build that packaged it (e.g. "HCG Executable Maker 0.5.0 (commit …) · Unleashify, local").</param>
/// <param name="Toolchain">The Xcode that compiled the app, read from the app's own Info.plist (DTXcode, DTXcodeBuild, DTSDKName).</param>
/// <param name="Host">A label for the build computer. Default: the operating system and architecture only (no computer or user name).</param>
/// <param name="SourceVisibility">"private" or "public": whether the app's source repository is public. Recorded, never changed.</param>
/// <param name="Validation">One line summarizing Maker's validation and lifecycle results.</param>
public sealed record LocalBuildInfo(string BuiltBy, string? Toolchain, DateTimeOffset BuiltAt, string? Host, string SourceVisibility, string? Validation);

public static partial class LocalBuild
{

    /// <summary>"Xcode 27.2 (27C5040a) · iphoneos27.2" from the build's DTXcode/DTXcodeBuild/DTSDKName, or null when absent.</summary>
    public static string? ToolchainFrom(IosAppBundle app)
    {
        var info = app.BuildInfo;
        if (!info.TryGetValue("DTXcode", out var code) || !XcodeCode().IsMatch(code)) return null;
        var major = int.Parse(code[..^2], System.Globalization.CultureInfo.InvariantCulture);
        var minor = code[^2] - '0';
        var patch = code[^1] - '0';
        var text = $"Xcode {major}.{minor}{(patch > 0 ? "." + patch : "")}";
        if (info.TryGetValue("DTXcodeBuild", out var build)) text += $" ({build})";
        if (info.TryGetValue("DTSDKName", out var sdk)) text += $" · {sdk}";
        return text;
    }

    /// <summary>The operating system and architecture of this computer — deliberately no computer, user or network name.</summary>
    public static string DefaultHostLabel() => $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";


    /// <summary>Whether the local build is pinned well enough for HCG Repo: a named repository, a full commit and a clean source.</summary>
    public static bool IsPinned(PackageProvenance p) => p.Local is not null && p.Repository is { Length: > 0 } && p.Commit is { Length: 40 } c && c.All(Uri.IsHexDigit) && p.SourceDirty != true;

    /// <summary>
    /// The approval check: an owner approval counts only when it names this exact .deb's SHA-256. For a CI build without a
    /// checksum the earlier rule applies (the run pins the bytes). Returns null when the approval holds, or why it does not.
    /// </summary>
    public static string? ApprovalProblem(CandidateTexts texts, PackageProvenance p, string sha256)
    {
        if (texts.OwnerApproval is not { Length: > 0 }) return null;
        if (texts.ApprovedSha256 is { Length: > 0 } approved)
            return string.Equals(approved.Trim(), sha256, StringComparison.OrdinalIgnoreCase) ? null
                : $"the owner's approval names SHA-256 {approved.Trim()}, but this .deb is {sha256}. An approval covers one exact package; rebuilt bytes need a new approval";
        return p.Local is not null ? $"a locally built package is approved by its exact SHA-256: the approval does not name one (this .deb is {sha256})" : null;
    }

    [GeneratedRegex(@"^\d{3,4}$")] private static partial Regex XcodeCode();
    [GeneratedRegex(@"github\.com[:/](?<repo>[A-Za-z0-9-]+/[A-Za-z0-9._-]+?)(?:\.git)?/?$")] private static partial Regex GitHubRemote();

    /// <summary>OWNER/NAME from a GitHub remote URL; anything else (and any credentials in the URL) is dropped.</summary>
    public static string? RepositoryFromRemote(string? url) => url is null ? null : GitHubRemote().Match(url.Trim()) is { Success: true } m ? m.Groups["repo"].Value : null;
}

/// <summary>
/// A checkout of one exact commit of a trusted local git repository, made for a build: <c>git clone --no-checkout</c> of
/// the repository into a new folder, then a detached checkout of the commit. The repository itself is only read (no
/// worktree, no fetch, no change to its branches); the copy is verified to be at that commit with no changes.
/// </summary>
public static class PinnedSource
{
    public sealed record Result(bool Succeeded, string? Commit, string? Folder, string? Repository, IReadOnlyList<string> Branches, IReadOnlyList<string> Notes, string? Failure);

    public static async Task<Result> CheckoutAsync(IProcessRunner runner, string git, string repository, string commitish, string destination, CancellationToken ct)
    {
        var notes = new List<string>();
        async Task<(bool Ok, string Out, string Err)> Git(string? cwd, params string[] args)
        {
            var (r, o, e) = await runner.CaptureAsync(new ProcessSpec { Executable = git, Arguments = cwd is null ? args : ["-C", cwd, .. args], Timeout = TimeSpan.FromMinutes(10) }, ct).ConfigureAwait(false);
            return (r.Succeeded, o.Trim(), e.Trim());
        }
        Result Fail(string why) => new(false, null, null, null, [], notes, why);

        if (!Directory.Exists(repository)) return Fail($"{repository} does not exist.");
        if (string.IsNullOrWhiteSpace(commitish) || commitish.StartsWith('-')) return Fail("Name the commit to build (a full or abbreviated SHA, a tag or a branch).");
        var top = await Git(repository, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (!top.Ok) return Fail($"{repository} is not a git repository: {top.Err}");
        var resolved = await Git(repository, "rev-parse", "--verify", "--end-of-options", commitish.Trim() + "^{commit}").ConfigureAwait(false);
        if (!resolved.Ok || resolved.Out.Length != 40) return Fail($"The repository has no commit \"{commitish}\": {resolved.Err}");
        var sha = resolved.Out.ToLowerInvariant();
        if (!commitish.Trim().Equals(sha, StringComparison.OrdinalIgnoreCase)) notes.Add($"\"{commitish.Trim()}\" resolves to {sha}.");
        var branches = (await Git(repository, "branch", "--all", "--contains", sha, "--format=%(refname:short)").ConfigureAwait(false)).Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        notes.Add(branches.Count > 0 ? $"On branch(es): {string.Join(", ", branches)}." : "The commit is on no branch of this repository.");
        var remote = await Git(repository, "remote", "get-url", "origin").ConfigureAwait(false);
        var repoName = remote.Ok ? LocalBuild.RepositoryFromRemote(remote.Out) : null;

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) return Fail($"{destination} is not empty.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var clone = await Git(null, "clone", "--quiet", "--no-checkout", "--no-hardlinks", top.Out, destination).ConfigureAwait(false);
        if (!clone.Ok) return Fail("git clone failed: " + clone.Err);
        var checkout = await Git(destination, "-c", "advice.detachedHead=false", "checkout", "--quiet", "--detach", sha).ConfigureAwait(false);
        if (!checkout.Ok) return Fail($"git checkout {sha} failed: {checkout.Err}");
        var head = await Git(destination, "rev-parse", "HEAD").ConfigureAwait(false);
        var status = await Git(destination, "status", "--porcelain", "--untracked-files=all").ConfigureAwait(false);
        if (!head.Ok || !head.Out.Equals(sha, StringComparison.OrdinalIgnoreCase)) return Fail($"The checkout is at {head.Out}, not {sha}.");
        if (!status.Ok || status.Out.Length > 0) return Fail("The checkout has changes: " + status.Out);
        if (File.Exists(Path.Combine(destination, ".gitmodules")))
            notes.Add("The repository has git submodules; they were not fetched. Builds that need them fail until they are added.");
        // The copy's origin is the local repository; record the real repository name, never a URL with credentials.
        await Git(destination, "remote", "remove", "origin").ConfigureAwait(false);
        return new(true, sha, destination, repoName, branches, notes, null);
    }
}

/// <summary>
/// The package's install → configure → remove cycle, simulated in a throwaway folder without privileges: the payload is
/// unpacked as dpkg would place it (and must stay under <c>var/jb</c>), Maker's maintainer scripts run with a recording
/// stand-in for <c>uicache</c> (and once with a failing one, which must not fail the install), and removing the listed
/// files must leave nothing behind. Scripts that are not exactly Maker's are never run. Nothing outside the folder is touched.
/// </summary>
public static class RootlessLifecycleSimulator
{
    public sealed record Result(bool Passed, IReadOnlyList<VerificationCheck> Checks)
    {
        public string Summary => Passed ? $"lifecycle passed ({Checks.Count} checks)" : "lifecycle FAILED: " + string.Join("; ", Checks.Where(c => c.Status == VerificationStatus.Failed).Select(c => c.Name));
    }

    /// <param name="runner">Runs the scripts; by default a plain child process with exactly the stated environment.</param>
    public static async Task<Result> RunAsync(string debPath, CancellationToken ct, IProcessRunner? runner = null, string? shell = null)
    {
        var checks = new List<VerificationCheck>();
        var root = Path.Combine(Path.GetTempPath(), "hcg-lifecycle-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var bytes = await File.ReadAllBytesAsync(debPath, ct).ConfigureAwait(false);
            IReadOnlyList<TheosDebVerifier.ArMember> members;
            using (var ms = new MemoryStream(bytes)) members = TheosDebVerifier.ReadAr(ms);
            var control = members.FirstOrDefault(m => m.Name == "control.tar.gz");
            var data = members.FirstOrDefault(m => m.Name == "data.tar.gz");
            if (control is null || data is null)
            {
                checks.Add(new("Unpack", VerificationStatus.Failed, "The package has no control.tar.gz and data.tar.gz."));
                return new(false, checks);
            }
            var fs = Path.Combine(root, "fs");
            var info = Path.Combine(root, "control");
            Directory.CreateDirectory(fs);
            Directory.CreateDirectory(info);
            var installed = await ExtractAsync(bytes, data, fs, ct).ConfigureAwait(false);
            await ExtractAsync(bytes, control, info, ct).ConfigureAwait(false);

            // Install: every entry lands under var/jb (rootless), and the app folder is there.
            var outside = installed.Where(p => !(p + "/").StartsWith("var/jb/", StringComparison.Ordinal) && p is not "var" and not ".").ToList();
            var apps = installed.Where(p => p.StartsWith("var/jb/Applications/", StringComparison.Ordinal))
                .Select(p => p.Split('/')[3]).Where(n => n.EndsWith(".app", StringComparison.Ordinal)).Distinct().ToList();
            checks.Add(new("Install (unpack)", outside.Count == 0 && apps.Count == 1 ? VerificationStatus.Passed : VerificationStatus.Failed,
                outside.Count > 0 ? "Entries outside /var/jb: " + string.Join(", ", outside.Take(5))
                : apps.Count != 1 ? $"Expected one app under /var/jb/Applications, found {apps.Count}"
                : $"{installed.Count(p => File.Exists(Path.Combine(fs, p)))} files placed under /var/jb/Applications/{apps[0]} in a throwaway root"));
            if (apps.Count != 1) return new(false, checks);
            var appPath = $"/var/jb/Applications/{apps[0]}";

            // Scripts: only Maker's exact text is executed.
            var (wantInst, wantRm) = RootlessAppPackager.Scripts(apps[0]);
            string? Read(string n) => File.Exists(Path.Combine(info, n)) ? File.ReadAllText(Path.Combine(info, n)) : null;
            var postinst = Read("postinst");
            var postrm = Read("postrm");
            var others = new[] { "preinst", "prerm", "triggers" }.Where(n => Read(n) is not null).ToList();
            var ours = postinst == wantInst && postrm == wantRm && others.Count == 0;
            checks.Add(new("Maintainer scripts", ours ? VerificationStatus.Passed : VerificationStatus.Failed,
                ours ? "postinst and postrm are exactly Maker's (uicache only); no preinst, prerm or triggers"
                     : "The scripts are not exactly the ones Maker writes" + (others.Count > 0 ? $" (also: {string.Join(", ", others)})" : "") + "; they were not executed"));
            var sh = shell ?? (OperatingSystem.IsWindows() ? null : File.Exists("/bin/sh") ? "/bin/sh" : null);
            if (ours && sh is null)
                checks.Add(new("Scripts run", VerificationStatus.NotVerifiable, "No POSIX shell on this computer; the scripts were checked by text only."));
            else if (ours)
            {
                var stub = Path.Combine(root, "stub");
                Directory.CreateDirectory(stub);
                var log = Path.Combine(root, "uicache.log");
                async Task<(int Exit, string Calls, string Err)> RunScript(string script, string arg, bool uicacheFails)
                {
                    await File.WriteAllTextAsync(Path.Combine(stub, "uicache"), "#!/bin/sh\necho \"$*\" >> \"$HCG_UICACHE_LOG\"\n" + (uicacheFails ? "exit 1\n" : "exit 0\n"), ct).ConfigureAwait(false);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(stub, "uicache"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    File.WriteAllText(log, "");
                    var spec = new ProcessSpec
                    {
                        Executable = sh, Arguments = [Path.Combine(info, script), arg], WorkingDirectory = fs, Timeout = TimeSpan.FromSeconds(30),
                        Environment = new Dictionary<string, string> { ["PATH"] = $"{stub}:/usr/bin:/bin", ["HCG_UICACHE_LOG"] = log, ["HOME"] = root },
                    };
                    var exit = runner is null ? await DirectAsync(spec, ct).ConfigureAwait(false) : (await runner.CaptureAsync(spec, ct).ConfigureAwait(false)).Result.ExitCode;
                    return (exit, File.ReadAllText(log).Trim(), "");
                }
                var cfg = await RunScript("postinst", "configure", false).ConfigureAwait(false);
                checks.Add(new("postinst configure", cfg.Exit == 0 && cfg.Calls == $"-p {appPath}" ? VerificationStatus.Passed : VerificationStatus.Failed,
                    $"exit {cfg.Exit}; uicache called with \"{cfg.Calls}\" (expected \"-p {appPath}\")"));
                var cfgFail = await RunScript("postinst", "configure", true).ConfigureAwait(false);
                checks.Add(new("postinst when uicache fails", cfgFail.Exit == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                    cfgFail.Exit == 0 ? "The install still completes (exit 0) and prints how to register the app by hand" : $"exit {cfgFail.Exit}: a missing or failing uicache would break the install"));
                var upgrade = await RunScript("postrm", "upgrade", false).ConfigureAwait(false);
                checks.Add(new("postrm upgrade", upgrade.Exit == 0 && upgrade.Calls.Length == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                    upgrade.Calls.Length == 0 ? "An upgrade does not unregister the app" : $"An upgrade calls uicache \"{upgrade.Calls}\""));
                var rm = await RunScript("postrm", "remove", false).ConfigureAwait(false);
                checks.Add(new("postrm remove", rm.Exit == 0 && rm.Calls == $"-u {appPath}" ? VerificationStatus.Passed : VerificationStatus.Failed,
                    $"exit {rm.Exit}; uicache called with \"{rm.Calls}\" (expected \"-u {appPath}\")"));
            }

            // Remove: delete what the package listed (files, then emptied folders) — nothing may be left behind.
            foreach (var p in installed.OrderByDescending(p => p.Count(ch => ch == '/')))
            {
                var full = Path.Combine(fs, p);
                if (File.Exists(full) || new FileInfo(full).LinkTarget is not null) File.Delete(full);
                else if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
            }
            var left = Directory.EnumerateFileSystemEntries(fs, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(fs, x).Replace('\\', '/')).ToList();
            checks.Add(new("Remove", left.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                left.Count == 0 ? "Removing the listed files leaves nothing behind" : "Left behind: " + string.Join(", ", left.Take(5))));
            return new(checks.All(c => c.Status != VerificationStatus.Failed), checks);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            checks.Add(new("Lifecycle", VerificationStatus.Failed, ex.Message));
            return new(false, checks);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<int> DirectAsync(ProcessSpec spec, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(spec.Executable) { WorkingDirectory = spec.WorkingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in spec.Environment ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new IOException($"Could not start {spec.Executable}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(spec.Timeout ?? TimeSpan.FromSeconds(30));
        var drain = Task.WhenAll(p.StandardOutput.ReadToEndAsync(timeout.Token), p.StandardError.ReadToEndAsync(timeout.Token));
        try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); await drain.ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return -1; }
        return p.ExitCode;
    }

    /// <summary>Unpacks a gzip tar member; returns the relative paths it created. Entries escaping the folder are refused.</summary>
    private static async Task<List<string>> ExtractAsync(byte[] deb, TheosDebVerifier.ArMember member, string dest, CancellationToken ct)
    {
        var paths = new List<string>();
        var full = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var gz = new GZipStream(new MemoryStream(deb, (int)member.Offset, (int)member.Size), CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } e)
        {
            var rel = e.Name.Replace('\\', '/').TrimStart('.', '/').TrimEnd('/');
            if (rel.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(dest, rel));
            if (!target.StartsWith(full, StringComparison.Ordinal) || rel.Split('/').Contains("..")) throw new InvalidDataException($"The entry {e.Name} escapes the package root.");
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.SymbolicLink:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    try { File.CreateSymbolicLink(target, e.LinkName); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await File.WriteAllTextAsync(target, "", ct).ConfigureAwait(false); } // e.g. Windows without symlink rights: placement is what is simulated
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var o = File.Create(target)) if (e.DataStream is { } s) await s.CopyToAsync(o, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported entry type {e.EntryType} for {e.Name}.");
            }
            paths.Add(rel);
        }
        return paths;
    }
}

/// <summary>
/// HCG Repo compatibility inspection: runs HCG Repo's own importer in dry-run mode on the exact bytes of a handoff, from
/// an HCG Repo checkout on this computer (copied to a throwaway folder first; the checkout is never written). The script
/// is the one CI runs (tools/unleashify/hcgrepo-dry-run.py). It runs HCG Repo's code, so the checkout must be trusted.
/// Nothing is imported, committed, pushed or published.
/// </summary>
public static class HcgRepoImportPreview
{
    public sealed record Result(bool Ran, int ExitCode, string Verdict, string Output, string? ReportJson);

    public static string Script()
    {
        using var s = typeof(HcgRepoImportPreview).Assembly.GetManifestResourceStream("hcgrepo-dry-run.py") ?? throw new InvalidOperationException("The import preview script is missing from this build.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static async Task<Result> RunAsync(IProcessRunner runner, string python, string hcgRepoCheckout, string handoffFolder, bool hypotheticalApproval, bool requireGenuineApproval,
        Action<string>? log, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(hcgRepoCheckout, "tools", "hcgrepo", "importer.py")))
            return new(false, 2, $"{hcgRepoCheckout} is not an HCG Repo checkout (no tools/hcgrepo/importer.py).", "", null);
        var dir = Path.Combine(Path.GetTempPath(), "hcg-import-preview-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "hcgrepo-dry-run.py");
            await File.WriteAllTextAsync(script, Script(), ct).ConfigureAwait(false);
            var report = Path.Combine(dir, "report.json");
            List<string> args = ["-I", script, "--repo", hcgRepoCheckout, "--handoff", handoffFolder, "--report", report];
            if (hypotheticalApproval) args.Add("--hypothetical-approval");
            if (requireGenuineApproval) args.Add("--require-genuine-approval");
            var output = new StringBuilder();
            var r = await runner.RunAsync(new ProcessSpec { Executable = python, Arguments = args, Timeout = TimeSpan.FromMinutes(10) },
                (_, line) => { lock (output) output.AppendLine(line); log?.Invoke(line); }, ct).ConfigureAwait(false);
            var text = output.ToString();
            var verdict = text.Split('\n').LastOrDefault(l => l.StartsWith("RESULT", StringComparison.Ordinal))?["RESULT".Length..].Trim()
                          ?? text.Split('\n').FirstOrDefault(l => l.StartsWith("REFUSED", StringComparison.Ordinal))?.Trim()
                          ?? $"The preview did not finish (exit {r.ExitCode}).";
            return new(true, r.ExitCode, verdict, text, File.Exists(report) ? await File.ReadAllTextAsync(report, ct).ConfigureAwait(false) : null);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
