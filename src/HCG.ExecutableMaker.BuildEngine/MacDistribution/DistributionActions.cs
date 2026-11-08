using System.Text.Json;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.BuildEngine.MacDistribution;

/// <summary>One independently reported step of an explicit distribution action.</summary>
public sealed record DistributionStep(string Name, VerificationStatus Status, string Detail);

/// <summary>The result of an explicit action on one artifact.</summary>
public sealed record DistributionActionResult(string Artifact, IReadOnlyList<DistributionStep> Steps)
{
    public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Status is VerificationStatus.Passed or VerificationStatus.PassedWithWarnings);
}

/// <summary>
/// Notarization and stapling, as an explicit, user-triggered operation on a finished Developer ID build:
/// <c>xcrun notarytool submit --keychain-profile … --wait</c> → on rejection the notary log is saved →
/// <c>xcrun stapler staple</c> + <c>stapler validate</c> → Gatekeeper assessment. Each container is submitted on its own
/// and each step is reported separately. Credentials are referenced by keychain profile name only.
/// </summary>
public sealed class NotarizationService(IProcessRunner runner, IBuildRepository builds, IAppPaths paths)
{
    private const string Xcrun = "/usr/bin/xcrun";

    /// <summary>Artifacts that can be notarized: a Developer ID .app, its DMG, and a Developer ID Installer-signed .pkg.</summary>
    public static IReadOnlyList<ArtifactRecord> Eligible(BuildRecord build)
    {
        if (build.Settings.MacDistribution.SigningMode != MacSigningMode.DeveloperId || build.Status != BuildStatus.Succeeded) return [];
        return build.Artifacts.Where(a => a.Format is PackagingFormat.MacAppBundle or PackagingFormat.MacDmg
                                          || (a.Format == PackagingFormat.MacPkg && build.Settings.MacDistribution.PkgInstallerSha1 is not null)).ToList();
    }

    /// <summary>Why a build cannot be notarized, or null when it can.</summary>
    public static string? Blocker(BuildRecord build, string? keychainProfile)
    {
        if (!OperatingSystem.IsMacOS()) return "Notarization runs on a Mac (xcrun notarytool).";
        if (build.Settings.MacDistribution.SigningMode != MacSigningMode.DeveloperId)
            return "This build is ad-hoc signed. The notary service accepts only Developer ID signed software with the hardened runtime: rebuild with Developer ID signing.";
        if (build.Status != BuildStatus.Succeeded) return "Only a successful, verified build can be notarized.";
        if (string.IsNullOrWhiteSpace(keychainProfile))
            return "Set up notarization credentials first (Settings › Apple credentials): store them in the keychain with xcrun notarytool store-credentials and enter the profile name.";
        return null;
    }

    public async Task<IReadOnlyList<DistributionActionResult>> NotarizeAsync(BuildRecord build, IReadOnlyCollection<Guid> artifactIds, string keychainProfile,
        Action<string> log, CancellationToken ct)
    {
        if (Blocker(build, keychainProfile) is { } blocker) throw new InvalidOperationException(blocker);
        var results = new List<DistributionActionResult>();
        var outputRoot = Path.GetDirectoryName(build.Artifacts[0].Path)!;
        foreach (var artifact in Eligible(build).Where(a => artifactIds.Contains(a.Id)).OrderBy(a => a.Format == PackagingFormat.MacAppBundle ? 0 : 1).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var steps = new List<DistributionStep>();
            string? temp = null;
            try
            {
                var submitPath = artifact.Path;
                if (artifact.Format == PackagingFormat.MacAppBundle)
                {
                    // An .app is submitted as a ZIP made with ditto (the notary service accepts UDIF, flat pkg and ZIP).
                    temp = Path.Combine(Path.GetTempPath(), "hcg-notary-" + Guid.NewGuid().ToString("N")[..10]);
                    Directory.CreateDirectory(temp);
                    submitPath = Path.Combine(temp, Path.GetFileNameWithoutExtension(artifact.Path) + ".zip");
                    var zip = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-c", "-k", "--keepParent", artifact.Path, submitPath], Timeout = TimeSpan.FromMinutes(10) },
                        (_, _) => { }, ct).ConfigureAwait(false);
                    if (!zip.Succeeded) { steps.Add(new("Submit", VerificationStatus.Failed, $"Could not zip the app for submission (ditto exit code {zip.ExitCode}).")); continue; }
                }
                log($"Submitting {Path.GetFileName(artifact.Path)} to Apple's notary service (this usually takes a few minutes)…");
                var (sr, so, se) = await runner.CaptureAsync(new ProcessSpec
                {
                    Executable = Xcrun,
                    Arguments = ["notarytool", "submit", submitPath, "--keychain-profile", keychainProfile, "--wait", "--timeout", "1h", "--output-format", "json"],
                    Timeout = TimeSpan.FromMinutes(75),
                }, ct).ConfigureAwait(false);
                var (id, status, message) = ParseSubmission(so);
                if (status is null)
                {
                    steps.Add(new("Submit", VerificationStatus.Failed, $"notarytool did not complete (exit code {sr.ExitCode}): {MacCodeSigner.LastLines(se + so, 3)} " +
                                                                       "Check the keychain profile name and your network connection."));
                    continue;
                }
                steps.Add(new("Submit", VerificationStatus.Passed, $"Submission {id} uploaded."));
                if (status != "Accepted")
                {
                    var logPath = Path.Combine(paths.LogsDirectory, $"notary-{id}.json");
                    var (lr, _, _) = await runner.CaptureAsync(new ProcessSpec
                    {
                        Executable = Xcrun, Arguments = ["notarytool", "log", id!, "--keychain-profile", keychainProfile, logPath], Timeout = TimeSpan.FromMinutes(5),
                    }, ct).ConfigureAwait(false);
                    steps.Add(new("Notarization", VerificationStatus.Failed, $"Apple's notary service answered {status}: {message}." +
                        (lr.Succeeded ? $" The notary log with the exact issues was saved to {logPath}." : " The notary log could not be downloaded.")));
                    continue;
                }
                steps.Add(new("Notarization", VerificationStatus.Passed, $"Accepted by Apple's notary service (submission {id})."));

                var (str, sto, ste) = await runner.CaptureAsync(new ProcessSpec { Executable = Xcrun, Arguments = ["stapler", "staple", artifact.Path], Timeout = TimeSpan.FromMinutes(10) }, ct).ConfigureAwait(false);
                steps.Add(str.Succeeded
                    ? new("Staple", VerificationStatus.Passed, "Ticket stapled, so Gatekeeper can verify it offline.")
                    : new("Staple", VerificationStatus.Failed, $"stapler staple failed: {MacCodeSigner.LastLines(sto + ste, 2)} (the ticket can take a moment to propagate; run Notarize again to retry stapling)."));
                var (vr, vo, ve) = await runner.CaptureAsync(new ProcessSpec { Executable = Xcrun, Arguments = ["stapler", "validate", artifact.Path], Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
                steps.Add(vr.Succeeded ? new("Staple validation", VerificationStatus.Passed, "stapler validate passed.")
                    : new("Staple validation", VerificationStatus.Failed, MacCodeSigner.LastLines(vo + ve, 2)));
                var type = artifact.Format switch { PackagingFormat.MacPkg => "install", PackagingFormat.MacDmg => "open", _ => "execute" };
                var gk = await new MacCodeSigner(runner).GatekeeperAsync(artifact.Path, type, ct, expectNotarized: true).ConfigureAwait(false);
                steps.Add(new("Gatekeeper", gk.Status, gk.Detail));

                if (artifact.Format == PackagingFormat.MacAppBundle && str.Succeeded)
                    await RezipStapledAppAsync(build, artifact, steps, log, ct).ConfigureAwait(false);
            }
            finally
            {
                if (temp is not null) try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
                results.Add(new DistributionActionResult(Path.GetFileName(artifact.Path), steps));
                await RecordAsync(build, artifact, steps, ct).ConfigureAwait(false);
            }
        }
        await WriteChecksumsAsync(build, outputRoot, ct).ConfigureAwait(false);
        await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
        return results;
    }

    /// <summary>A ZIP cannot be stapled: the stapled .app is zipped again so the ZIP artifact carries the ticket.</summary>
    private async Task RezipStapledAppAsync(BuildRecord build, ArtifactRecord app, List<DistributionStep> steps, Action<string> log, CancellationToken ct)
    {
        var zipArtifact = build.Artifacts.FirstOrDefault(a => a.Format == PackagingFormat.PortableZip && a.Path.EndsWith(".app.zip", StringComparison.Ordinal));
        if (zipArtifact is null) return;
        var fresh = zipArtifact.Path + ".tmp";
        var r = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-c", "-k", "--sequesterRsrc", "--keepParent", app.Path, fresh], Timeout = TimeSpan.FromMinutes(10) },
            (_, _) => { }, ct).ConfigureAwait(false);
        if (!r.Succeeded) { steps.Add(new("ZIP with stapled app", VerificationStatus.Failed, $"ditto exit code {r.ExitCode}; the previous ZIP (without ticket) was kept.")); return; }
        File.Move(fresh, zipArtifact.Path, overwrite: true);
        log($"Re-created {Path.GetFileName(zipArtifact.Path)} from the stapled app.");
        steps.Add(new("ZIP with stapled app", VerificationStatus.Passed, $"{Path.GetFileName(zipArtifact.Path)} re-created from the stapled .app."));
        await ReplaceAsync(build, zipArtifact, c => c.Where(x => x.Name != "Notarization").Append(new VerificationCheck("Notarization", VerificationStatus.Passed, "Contains the notarized, stapled .app.")), ct).ConfigureAwait(false);
    }

    private async Task RecordAsync(BuildRecord build, ArtifactRecord artifact, IReadOnlyList<DistributionStep> steps, CancellationToken ct)
    {
        var notarized = steps.Any(s => s.Name == "Notarization" && s.Status == VerificationStatus.Passed);
        var summary = string.Join(" · ", steps.Select(s => $"{s.Name}: {s.Status}"));
        await ReplaceAsync(build, artifact, checks => checks
            .Where(c => c.Name is not ("Notarization" or "Gatekeeper" or "Stapled ticket"))
            .Append(new VerificationCheck("Notarization", notarized ? VerificationStatus.Passed : VerificationStatus.Failed, summary))
            .Concat(steps.Where(s => s.Name is "Gatekeeper").Select(s => new VerificationCheck("Gatekeeper", s.Status, s.Detail)))
            .Concat(steps.Where(s => s.Name is "Staple").Select(s => new VerificationCheck("Stapled ticket", s.Status, s.Detail))), ct).ConfigureAwait(false);
    }

    /// <summary>Replaces an artifact record with refreshed checks, size and checksum (records are immutable).</summary>
    internal static async Task ReplaceAsync(BuildRecord build, ArtifactRecord artifact, Func<IEnumerable<VerificationCheck>, IEnumerable<VerificationCheck>> checks, CancellationToken ct)
    {
        string sha; long size;
        if (artifact.Kind == ArtifactKind.Directory)
        {
            var (digest, manifest, total) = await Checksums.Sha256DirectoryAsync(artifact.Path, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(artifact.Path)!, Path.GetFileName(artifact.Path) + ".manifest.sha256.txt"), manifest, ct).ConfigureAwait(false);
            (sha, size) = (digest, total);
        }
        else
        {
            sha = await Checksums.Sha256FileAsync(artifact.Path, ct).ConfigureAwait(false);
            size = new FileInfo(artifact.Path).Length;
        }
        var newChecks = checks(artifact.Checks).ToList();
        var index = build.Artifacts.FindIndex(a => a.Id == artifact.Id);
        build.Artifacts[index] = new ArtifactRecord
        {
            Id = artifact.Id, BuildId = artifact.BuildId, Format = artifact.Format, Kind = artifact.Kind, Path = artifact.Path,
            SizeBytes = size, Sha256 = sha, Verification = Checks.Combine(newChecks), Checks = newChecks, CreatedAt = artifact.CreatedAt,
        };
    }

    internal static async Task WriteChecksumsAsync(BuildRecord build, string outputRoot, CancellationToken ct)
    {
        var sums = string.Concat(build.Artifacts.Where(a => a.Kind == ArtifactKind.File).Select(a => $"{a.Sha256}  {Path.GetFileName(a.Path)}\n"));
        if (sums.Length > 0) await File.WriteAllTextAsync(Path.Combine(outputRoot, "SHA256SUMS.txt"), sums, ct).ConfigureAwait(false);
    }

    /// <summary>Parses <c>notarytool submit --output-format json</c>: id, status (Accepted / Invalid / Rejected / In Progress) and message.</summary>
    public static (string? Id, string? Status, string? Message) ParseSubmission(string json)
    {
        var start = json.IndexOf('{');
        if (start < 0) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            var root = doc.RootElement;
            string? Get(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("id"), Get("status"), Get("message"));
        }
        catch (JsonException) { return (null, null, null); }
    }
}

/// <summary>
/// Explicit test installation of an installer package into the current user's ~/Applications (no administrator
/// rights, nothing else on the Mac is touched), followed by checks, an optional launch, and removal. Refuses to run
/// when an app of that name is already installed there, so nothing of the user's is overwritten.
/// </summary>
public sealed class PkgTestInstaller(IProcessRunner runner)
{
    public async Task<DistributionActionResult> TestInstallAsync(ArtifactRecord pkg, string builtApp, bool launch, Action<string> log, CancellationToken ct)
    {
        var steps = new List<DistributionStep>();
        if (!OperatingSystem.IsMacOS()) return new(Path.GetFileName(pkg.Path), [new("Install", VerificationStatus.NotVerifiable, "Installing a .pkg requires macOS.")]);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = Path.Combine(home, "Applications", Path.GetFileName(builtApp));
        if (Directory.Exists(target))
            return new(Path.GetFileName(pkg.Path), [new("Install", VerificationStatus.NotVerifiable,
                $"{target} already exists. HCG does not overwrite installed apps; move it away first, then run Test install again.")]);
        var plist = Packaging.Packagers.InfoPlist.Read(Path.Combine(builtApp, "Contents", "Info.plist"));
        var id = plist.GetValueOrDefault("CFBundleIdentifier") ?? "";
        var exe = plist.GetValueOrDefault("CFBundleExecutable") ?? "";
        try
        {
            log($"installer -pkg {Path.GetFileName(pkg.Path)} -target CurrentUserHomeDirectory");
            var (ir, io, ie) = await runner.CaptureAsync(new ProcessSpec
            {
                Executable = "/usr/sbin/installer", Arguments = ["-pkg", pkg.Path, "-target", "CurrentUserHomeDirectory"], Timeout = TimeSpan.FromMinutes(10),
            }, ct).ConfigureAwait(false);
            if (!ir.Succeeded || !Directory.Exists(target))
            {
                steps.Add(new("Install", VerificationStatus.Failed, $"installer exited with code {ir.ExitCode}: {MacCodeSigner.LastLines(io + ie, 3)}"));
                return new(Path.GetFileName(pkg.Path), steps);
            }
            steps.Add(new("Install", VerificationStatus.Passed, $"Installed to {target}."));
            var cmp = BundleComparison.Compare(builtApp, target, "Installed app", "the installed app");
            steps.Add(new("Installed app", cmp.Status, cmp.Detail));
            var hasRootBundles = Directory.EnumerateDirectories(target, "*.bundle").Any();
            if (!hasRootBundles)
            {
                var (ok, detail) = await new MacCodeSigner(runner).VerifyStrictAsync(target, ct).ConfigureAwait(false);
                steps.Add(new("Installed signature", ok ? VerificationStatus.Passed : VerificationStatus.Failed, ok ? "codesign --verify --strict --deep passed on the installed app." : detail));
            }
            if (launch)
                steps.Add(await LaunchAsync(target, exe, ct).ConfigureAwait(false));
            else
                steps.Add(new("Launch", VerificationStatus.NotVerifiable, "Not launched (launching runs the project's code; choose Launch to include it)."));
        }
        finally
        {
            if (Directory.Exists(target))
            {
                try { Directory.Delete(target, recursive: true); steps.Add(new("Remove", VerificationStatus.Passed, $"Removed {target}.")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { steps.Add(new("Remove", VerificationStatus.Failed, $"Could not remove {target}: {ex.Message}")); }
                var (fr, _, fe) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/sbin/pkgutil", Arguments = ["--volume", home, "--forget", id], Timeout = TimeSpan.FromMinutes(1) }, CancellationToken.None).ConfigureAwait(false);
                steps.Add(new("Receipt", fr.Succeeded ? VerificationStatus.Passed : VerificationStatus.PassedWithWarnings,
                    fr.Succeeded ? $"Install receipt {id} forgotten." : $"The install receipt could not be removed: {fe.Trim()}"));
            }
        }
        return new(Path.GetFileName(pkg.Path), steps);
    }

    private async Task<DistributionStep> LaunchAsync(string app, string exe, CancellationToken ct)
    {
        var (or, _, oe) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/open", Arguments = ["-n", app], Timeout = TimeSpan.FromMinutes(1) }, ct).ConfigureAwait(false);
        if (!or.Succeeded) return new("Launch", VerificationStatus.Failed, $"open failed: {oe.Trim()}");
        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var exePath = Path.Combine(app, "Contents", "MacOS", exe);
        var (pr, po, _) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/pgrep", Arguments = ["-f", exePath], Timeout = TimeSpan.FromSeconds(20) }, ct).ConfigureAwait(false);
        var running = pr.Succeeded && po.Trim().Length > 0;
        if (running)
            await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/pkill", Arguments = ["-f", exePath], Timeout = TimeSpan.FromSeconds(20) }, CancellationToken.None).ConfigureAwait(false);
        return running
            ? new("Launch", VerificationStatus.Passed, "The installed app started and was still running after 5 seconds (then quit).")
            : new("Launch", VerificationStatus.Failed, "The installed app was not running 5 seconds after open (it crashed or was blocked; see Console.app).");
    }
}

/// <summary>
/// Explicit validation of a Mac App Store package with App Store Connect (<c>xcrun altool --validate-app</c>). Uses an
/// App Store Connect API key referenced by ID (the .p8 stays in ~/.appstoreconnect/private_keys). Never uploads.
/// </summary>
public sealed class AppStoreValidationService(IProcessRunner runner)
{
    public static string KeyFile(string keyId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".appstoreconnect", "private_keys", $"AuthKey_{keyId}.p8");

    public static string? Blocker(string? keyId, string? issuerId)
    {
        if (!OperatingSystem.IsMacOS()) return "Validation runs on a Mac (xcrun altool).";
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(issuerId))
            return "Set up App Store Connect API access first (Settings › Apple credentials): create a key in App Store Connect › Users and Access › Integrations, enter its Key ID and Issuer ID, and save the .p8 file as ~/.appstoreconnect/private_keys/AuthKey_<KeyID>.p8.";
        if (!File.Exists(KeyFile(keyId))) return $"The API key file {KeyFile(keyId)} was not found. Download the .p8 from App Store Connect and save it there.";
        return null;
    }

    public async Task<DistributionActionResult> ValidateAsync(ArtifactRecord pkg, string keyId, string issuerId, CancellationToken ct)
    {
        if (Blocker(keyId, issuerId) is { } b) return new(Path.GetFileName(pkg.Path), [new("Validation", VerificationStatus.NotVerifiable, b)]);
        var (fr, _, _) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/xcrun", Arguments = ["--find", "altool"], Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        if (!fr.Succeeded)
            return new(Path.GetFileName(pkg.Path), [new("Validation", VerificationStatus.NotVerifiable, "altool is not available with the selected Xcode. Validate and upload with Apple's Transporter app instead.")]);
        var (r, o, e) = await runner.CaptureAsync(new ProcessSpec
        {
            Executable = "/usr/bin/xcrun",
            Arguments = ["altool", "--validate-app", "-f", pkg.Path, "-t", "macos", "--apiKey", keyId, "--apiIssuer", issuerId, "--output-format", "json"],
            Timeout = TimeSpan.FromMinutes(30),
        }, ct).ConfigureAwait(false);
        return new(Path.GetFileName(pkg.Path), [r.Succeeded
            ? new("Validation", VerificationStatus.Passed, "App Store Connect validation passed. Upload and submission stay manual (Transporter, or altool --upload-app).")
            : new("Validation", VerificationStatus.Failed, $"App Store Connect reported problems (exit code {r.ExitCode}): {MacCodeSigner.LastLines(o + e, 6)}")]);
    }
}

/// <summary>Guided setup and checks for Apple credentials. HCG stores only names/IDs; secrets stay in the keychain or key file.</summary>
public sealed class AppleCredentialsService(IProcessRunner runner)
{
    /// <summary>The Terminal command that stores notarization credentials in the login keychain (notarytool asks for the app-specific password itself).</summary>
    public static string StoreCredentialsCommand(string profile, string? appleId, string? teamId) =>
        $"xcrun notarytool store-credentials \"{profile}\" --apple-id \"{(string.IsNullOrWhiteSpace(appleId) ? "you@example.com" : appleId)}\" --team-id \"{(string.IsNullOrWhiteSpace(teamId) ? "TEAMID" : teamId)}\"";

    /// <summary>Checks that the keychain profile works by listing recent submissions (contacts Apple; no upload).</summary>
    public async Task<(bool Ok, string Detail)> CheckNotaryProfileAsync(string profile, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return (false, "Checking notarization credentials requires macOS.");
        var (r, o, e) = await runner.CaptureAsync(new ProcessSpec
        {
            Executable = "/usr/bin/xcrun", Arguments = ["notarytool", "history", "--keychain-profile", profile, "--output-format", "json"], Timeout = TimeSpan.FromMinutes(2),
        }, ct).ConfigureAwait(false);
        return r.Succeeded
            ? (true, $"The keychain profile \"{profile}\" works (Apple's notary service answered).")
            : (false, $"The keychain profile \"{profile}\" did not work: {MacCodeSigner.LastLines(e + o, 2)} Store it again with the command shown.");
    }
}
