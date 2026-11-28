using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.BuildEngine.MacDistribution;

/// <summary>What to distribute and how: an already-built macOS app (a .app, or a ZIP holding exactly one) and the release it becomes.</summary>
public sealed record ExistingMacAppRequest
{
    /// <summary>The app as it was built elsewhere: a .app folder, or a .zip that holds exactly one .app at its top level. Never modified.</summary>
    public required string Input { get; init; }
    /// <summary>Optional checksum list (e.g. SHA256SUMS-signing-input.txt) that must name the input ZIP with its SHA-256.</summary>
    public string? ChecksumsFile { get; init; }
    /// <summary>Where the input came from, recorded as given (e.g. "HCG Mail CI run 46007763971, artifact 15201589154").</summary>
    public string? Origin { get; init; }
    /// <summary>The release version (SemVer, e.g. 1.0.0-beta.2). Info.plist's CFBundleShortVersionString must be its numeric part.</summary>
    public required string ReleaseVersion { get; init; }
    /// <summary>When set, Info.plist's CFBundleVersion must equal it.</summary>
    public string? ExpectedBuildNumber { get; init; }
    /// <summary>When set, Info.plist's CFBundleIdentifier must equal it.</summary>
    public string? ExpectedBundleIdentifier { get; init; }
    public required TargetArchitecture Architecture { get; init; }
    /// <summary>SHA-1 of the Developer ID Application identity in the login keychain.</summary>
    public required string DeveloperIdApplicationSha1 { get; init; }
    /// <summary>SHA-1 of the Developer ID Installer identity; required for a PKG.</summary>
    public string? InstallerSha1 { get; init; }
    /// <summary>Boolean entitlements for the app's own signature (e.g. com.apple.security.cs.allow-jit).</summary>
    public IReadOnlyList<string> Entitlements { get; init; } = [];
    /// <summary>The person reviewed the entitlements; nothing is signed without it.</summary>
    public bool EntitlementsConfirmed { get; init; }
    public bool Zip { get; init; } = true;
    public bool Pkg { get; init; } = true;
    public bool Dmg { get; init; }
    public required string OutputDirectory { get; init; }
    /// <summary>File name without extension (e.g. HCG-Mail-1.0.0-beta.2-macos-arm64); derived from the app when empty.</summary>
    public string? ArtifactBaseName { get; init; }
}

/// <summary>What Executable Maker reads from the input before anything is signed.</summary>
public sealed record ExistingMacAppInspection(
    string Input, string InputSha256, MacAppFacts Facts, IReadOnlyList<string> NonCodeInMacOS, IReadOnlyList<string> NestedCode,
    IReadOnlyList<VerificationCheck> Checks, string? SignatureAsReceived)
{
    public bool Usable => Checks.All(c => c.Status != VerificationStatus.Failed);
}

/// <summary>
/// Developer ID distribution of a macOS app that was built elsewhere (for example the unsigned signing input a CI run
/// qualified), without rebuilding it:
/// <list type="number">
/// <item><b>Sign</b> (<see cref="SignAsync"/>): the input is checked against its published SHA-256, copied into an HCG
/// workspace (the original is never modified), checked (identity, version, build, architectures, code only in
/// Contents/MacOS), signed inside out with the chosen Developer ID Application identity, the hardened runtime, a secure
/// timestamp and the confirmed entitlements, and verified (codesign, Signing Doctor, entitlements). Local only.</item>
/// <item><b>Notarize and package</b> (<see cref="NotarizeAndPackageAsync"/>, sends files to Apple): the app is notarized
/// and stapled; the ZIP, the PKG (Developer ID Installer) and the DMG are made from the stapled app; the PKG and DMG are
/// notarized and stapled; Gatekeeper assesses every file, the ZIP after extracting it as a customer would.</item>
/// <item><b>Release Passports</b> (<see cref="WritePassportsAsync"/>) for the final files.</item>
/// </list>
/// Each run is a build record, so Build History, Artifacts and Ship to Store (Add build) offer its files. Nothing is
/// uploaded to HCG Store or published.
/// </summary>
public sealed partial class ExistingMacAppDistribution(
    IProcessRunner runner, IBuildRepository builds, IProjectRepository projects, IAppPaths paths, TimeProvider time, Func<bool>? isMac = null)
{
    public const string ProviderId = "existing-mac-app";
    private bool IsMac => (isMac ?? OperatingSystem.IsMacOS)();
    private const string Ditto = "/usr/bin/ditto";
    private const string Xcrun = "/usr/bin/xcrun";
    private const string Hdiutil = "/usr/bin/hdiutil";

    // ───────────── Inspect ─────────────

    /// <summary>Reads the input without changing it: checksum, identity, version, architectures, Contents/MacOS, nested code, signature as received.</summary>
    public async Task<ExistingMacAppInspection> InspectAsync(string input, string? checksumsFile, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "hcg-mac-inspect-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var (app, sha, checks) = await StageInputAsync(input, checksumsFile, temp, ct).ConfigureAwait(false);
            if (app is null) return new(input, sha, new MacAppFacts { BundlePath = input, Problem = checks.LastOrDefault()?.Detail }, [], [], checks, null);
            var facts = MacAppFacts.Read(app);
            var nonCode = NonCodeInMacOS(app);
            string? asReceived = null;
            if (IsMac)
            {
                var report = await new MacSigningDoctor(runner).InspectAsync(app, ct).ConfigureAwait(false);
                asReceived = $"{report.Status} ({MacSigningDoctor.Describe(report.SignatureKind)})";
            }
            checks.Add(facts.Problem is { } p ? new("Bundle", VerificationStatus.Failed, p)
                : new("Bundle", VerificationStatus.Passed, $"{facts.Label}: {facts.BundleIdentifier} {facts.ShortVersion} ({facts.BuildVersion}), {string.Join("/", facts.Architectures)}, macOS {facts.MinimumSystemVersion ?? "?"}+"));
            checks.Add(CodeOnlyCheck(nonCode));
            if (JitCheck(app, []) is { } jit) checks.Add(jit with { Detail = jit.Detail.Replace("signed with", "to be signed with", StringComparison.Ordinal) });
            return new(input, sha, facts, nonCode, MacCodeSigner.NestedCode(app).Select(n => Path.GetRelativePath(app, n)).ToList(), checks, asReceived);
        }
        finally { TryDelete(temp); }
    }

    // ───────────── 1. Sign ─────────────

    /// <summary>Stage 1 (local): imports, checks, signs inside out and verifies. Returns the recorded build (Succeeded or Failed with the first reason).</summary>
    public async Task<BuildRecord> SignAsync(ExistingMacAppRequest request, Action<string> log, CancellationToken ct)
    {
        if (!IsMac) throw new InvalidOperationException("Developer ID signing runs on a Mac (codesign and the login keychain).");
        if (!request.EntitlementsConfirmed) throw new InvalidOperationException("Confirm the entitlements first: nothing is signed with entitlements nobody reviewed.");
        if (Entitlements_Invalid(request.Entitlements) is { } bad) throw new InvalidOperationException(bad);
        if (request.Pkg && request.InstallerSha1 is null)
            throw new InvalidOperationException("A PKG for distribution needs the Developer ID Installer identity. Choose it, or leave the PKG out.");
        if (!SemVer().IsMatch(request.ReleaseVersion)) throw new InvalidOperationException($"\"{request.ReleaseVersion}\" is not a release version (for example 1.0.0 or 1.0.0-beta.2).");

        // The identities, as the keychain holds them now.
        var (fr, fo, fe) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/security", Arguments = ["find-identity", "-v"], Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        if (!fr.Succeeded) throw new InvalidOperationException($"The login keychain could not be read (security find-identity exited with {fr.ExitCode}): {fe.Trim()}");
        var inventory = new MacSigningInventory { Identities = MacSigningInventoryReader.ParseIdentities(fo), DetectedAt = time.GetUtcNow() };
        var app = inventory.Identities.FirstOrDefault(i => string.Equals(i.Sha1, request.DeveloperIdApplicationSha1, StringComparison.OrdinalIgnoreCase));
        if (app is null || app.Kind != AppleCertificateKind.DeveloperIdApplication)
            throw new InvalidOperationException(app is null
                ? $"No signing identity with SHA-1 {request.DeveloperIdApplicationSha1} is in the login keychain. Import the Developer ID Application .p12 in Signing & Security › Apple Signing, then Refresh."
                : $"{app.Name} is not a Developer ID Application identity. Public distribution outside the Mac App Store needs Developer ID Application.");
        SigningIdentity? installer = null;
        if (request.InstallerSha1 is { } isha)
        {
            installer = inventory.Identities.FirstOrDefault(i => string.Equals(i.Sha1, isha, StringComparison.OrdinalIgnoreCase));
            if (installer is null || installer.Kind != AppleCertificateKind.DeveloperIdInstaller)
                throw new InvalidOperationException(installer is null
                    ? $"No identity with SHA-1 {isha} is in the login keychain. Import the Developer ID Installer .p12 in Signing & Security › Apple Signing."
                    : $"{installer.Name} is not a Developer ID Installer identity; packages signed with another certificate are rejected on customers' Macs.");
            if (app.TeamId is { } t1 && installer.TeamId is { } t2 && t1 != t2)
                throw new InvalidOperationException($"The Application identity is team {t1} and the Installer identity team {t2}: both must be the same team.");
        }

        var now = time.GetUtcNow();
        var staging = Path.Combine(Path.GetTempPath(), "hcg-mac-import-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var (stagedApp, inputSha, inputChecks) = await StageInputAsync(request.Input, request.ChecksumsFile, staging, ct).ConfigureAwait(false);
            if (stagedApp is null) throw new InvalidOperationException(inputChecks.Last().Detail);
            var facts = MacAppFacts.Read(stagedApp);
            if (facts.Problem is { } problem) throw new InvalidOperationException($"{Path.GetFileName(stagedApp)}: {problem}");
            var bundleName = Path.GetFileName(stagedApp);
            var bundleId = facts.BundleIdentifier!;

            // The project: one per bundle identifier, so every release of the app is grouped.
            var projectId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("hcg-existing-mac-app:" + bundleId)));
            var workspace = Path.Combine(paths.WorkspacesDirectory, projectId.ToString("N"));
            var source = Path.Combine(workspace, "source");
            Directory.CreateDirectory(source);
            var received = Path.Combine(source, bundleName);
            if (Directory.Exists(received)) Directory.Delete(received, recursive: true);
            await RunAsync(Ditto, [stagedApp, received], log, ct).ConfigureAwait(false);
            await projects.UpsertAsync(new ProjectRecord
            {
                Id = projectId, Name = $"{facts.Label} (macOS app built elsewhere)", SourceKind = ProjectSourceKind.Artifact,
                SourceLocation = Path.GetFullPath(request.Input), WorkspacePath = workspace, ImportedAt = now, LastOpenedAt = now,
            }, ct).ConfigureAwait(false);

            var baseName = string.IsNullOrWhiteSpace(request.ArtifactBaseName) ? DefaultBaseName(facts.Label, request.ReleaseVersion, request.Architecture) : PathGuard.SanitizeFileName(request.ArtifactBaseName.Trim(), "app");
            var job = UniqueFolder(Path.Combine(request.OutputDirectory, baseName));
            Directory.CreateDirectory(job);
            var logPath = Path.Combine(job, "distribution.log");
            var fileLog = new FileLog(logPath, log);

            var build = new BuildRecord
            {
                Id = Guid.NewGuid(), ProjectId = projectId, ProjectName = facts.Label, StartedAt = now, Status = BuildStatus.Running, LogPath = logPath, TrustAcknowledged = true,
                Settings = new BuildSettings
                {
                    ProjectRelativePath = bundleName, Platform = TargetPlatform.MacOS, Architecture = request.Architecture, OutputDirectory = job,
                    Identity = new AppIdentity { ApplicationName = facts.Label, Version = request.ReleaseVersion, ApplicationId = bundleId },
                    PackagingFormats = [PackagingFormat.MacAppBundle, .. request.Zip ? [PackagingFormat.PortableZip] : Array.Empty<PackagingFormat>(),
                        .. request.Pkg ? [PackagingFormat.MacPkg] : Array.Empty<PackagingFormat>(), .. request.Dmg ? [PackagingFormat.MacDmg] : Array.Empty<PackagingFormat>()],
                    MacDistribution = new MacDistributionOptions
                    {
                        SigningMode = MacSigningMode.DeveloperId, DeveloperIdApplicationSha1 = app.Sha1, PkgInstallerSha1 = installer?.Sha1, PkgInstallerName = installer?.Name,
                        DeveloperIdEntitlements = request.Entitlements, EntitlementsConfirmed = true,
                    },
                },
            };
            build.ToolchainVersions["build-provider"] = ProviderId;
            build.ToolchainVersions["input"] = Path.GetFullPath(request.Input);
            build.ToolchainVersions["input-sha256"] = inputSha;
            if (request.Origin is { Length: > 0 } origin) build.ToolchainVersions["input-origin"] = origin;
            build.ToolchainVersions["developer-id-application"] = $"{app.Name} ({app.Sha1})";
            if (installer is not null) build.ToolchainVersions["developer-id-installer"] = $"{installer.Name} ({installer.Sha1})";
            build.ToolchainVersions["artifact-base-name"] = baseName;
            build.ToolchainVersions["formats"] = string.Join(",", new[] { request.Zip ? "zip" : null, request.Pkg ? "pkg" : null, request.Dmg ? "dmg" : null }.OfType<string>());
            await builds.InsertAsync(build, ct).ConfigureAwait(false);

            var checks = new List<VerificationCheck>(inputChecks);
            try
            {
                fileLog.Info($"Input: {request.Input} (SHA-256 {inputSha}){(request.Origin is { Length: > 0 } o ? " — " + o : "")}");
                checks.AddRange(ReleaseChecks(facts, request));
                checks.Add(CodeOnlyCheck(NonCodeInMacOS(received)));
                if (checks.FirstOrDefault(c => c.Status == VerificationStatus.Failed) is { } stop)
                    throw new InvalidOperationException($"{stop.Name}: {stop.Detail}");

                if (JitCheck(received, request.Entitlements) is { } jit) checks.Add(jit);
                var signedApp = Path.Combine(job, bundleName);
                await RunAsync(Ditto, [received, signedApp], log, ct).ConfigureAwait(false);
                // Quarantine, Finder info and resource forks on the copy make codesign fail ("resource fork, Finder
                // information, or similar detritus not allowed"); the original keeps its attributes.
                await RunAsync("/usr/bin/xattr", ["-cr", signedApp], log, ct).ConfigureAwait(false);
                var signer = new MacCodeSigner(runner);
                fileLog.Info($"Signing {bundleName} with {app.Name} (hardened runtime, secure timestamp, entitlements: {(request.Entitlements.Count == 0 ? "none" : string.Join(", ", request.Entitlements))})");
                await signer.SignBundleInsideOutAsync(signedApp, app.Sha1, request.Entitlements, fileLog, ct).ConfigureAwait(false);
                checks.AddRange(await VerifySignedAppAsync(signedApp, app, request.Entitlements, ct).ConfigureAwait(false));
                checks.Add(await OriginalUnchangedAsync(request.Input, inputSha, ct).ConfigureAwait(false));

                build.Artifacts.Add(await ArtifactAsync(build, PackagingFormat.MacAppBundle, ArtifactKind.Directory, signedApp, checks, ct).ConfigureAwait(false));
                build.Status = checks.Any(c => c.Status == VerificationStatus.Failed) ? BuildStatus.Failed : BuildStatus.Succeeded;
                if (build.Status == BuildStatus.Failed)
                    build.FailureReason = string.Join(" ", checks.Where(c => c.Status == VerificationStatus.Failed).Select(c => $"{c.Name}: {c.Detail}"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                build.Status = ex is OperationCanceledException ? BuildStatus.Cancelled : BuildStatus.Failed;
                build.FailureReason = ex is OperationCanceledException ? "Cancelled." : ex.Message;
                fileLog.Info("Stopped: " + build.FailureReason);
            }
            foreach (var c in checks) fileLog.Info($"{c.Status}: {c.Name} — {c.Detail}");
            build.EndedAt = time.GetUtcNow();
            build.ErrorCount = checks.Count(c => c.Status == VerificationStatus.Failed);
            build.WarningCount = checks.Count(c => c.Status == VerificationStatus.PassedWithWarnings);
            await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
            return build;
        }
        finally { TryDelete(staging); }
    }

    // ───────────── 2. Notarize, staple, package ─────────────

    /// <summary>Why <see cref="NotarizeAndPackageAsync"/> cannot run for this build, or null.</summary>
    public static string? NotarizeBlocker(BuildRecord build, string? keychainProfile)
    {
        if (!IsExistingAppBuild(build)) return "This is not an existing-app distribution job.";
        if (build.Artifacts.FirstOrDefault(a => a.Format == PackagingFormat.MacAppBundle) is null) return "The signed app is missing from this job.";
        return NotarizationService.Blocker(build, keychainProfile);
    }

    public static bool IsExistingAppBuild(BuildRecord b) => b.ToolchainVersions.TryGetValue("build-provider", out var p) && p == ProviderId;

    /// <summary>
    /// Stage 2 (sends the app, then the PKG and DMG, to Apple's notary service with the keychain profile): notarize and
    /// staple the app; make the chosen files from the stapled app; notarize and staple the PKG and DMG; Gatekeeper on
    /// every file (the ZIP after extracting it). Stops at the first rejection, with the notary log saved.
    /// </summary>
    public async Task<IReadOnlyList<DistributionActionResult>> NotarizeAndPackageAsync(BuildRecord build, string keychainProfile, Action<string> log, CancellationToken ct)
    {
        if (NotarizeBlocker(build, keychainProfile) is { } blocker) throw new InvalidOperationException(blocker);
        var fileLog = new FileLog(build.LogPath ?? Path.Combine(build.Settings.OutputDirectory, "distribution.log"), log);
        var notary = new NotarizationService(runner, builds, paths);
        var appArtifact = build.Artifacts.First(a => a.Format == PackagingFormat.MacAppBundle);
        var results = new List<DistributionActionResult>();

        // The app first: the ZIP, PKG and DMG must contain the stapled app.
        var appResult = (await notary.NotarizeAsync(build, [appArtifact.Id], keychainProfile, fileLog.Info, ct).ConfigureAwait(false)).ToList();
        results.AddRange(appResult);
        if (!appResult.All(r => r.Succeeded))
        {
            fileLog.Info("The app was not notarized and stapled, so no distribution file was made. See the notary step above.");
            return results;
        }

        var app = build.Artifacts.First(a => a.Format == PackagingFormat.MacAppBundle).Path;
        var baseName = Tv(build, "artifact-base-name") ?? Path.GetFileNameWithoutExtension(app);
        var formats = (Tv(build, "formats") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var job = build.Settings.OutputDirectory;
        var d = build.Settings.MacDistribution;
        var made = new List<Guid>();
        // A second run replaces the files of the first; its records go with them (no duplicate artifacts).
        build.Artifacts.RemoveAll(a => a.Kind == ArtifactKind.File && a.Format is PackagingFormat.PortableZip or PackagingFormat.MacPkg or PackagingFormat.MacDmg);
        try
        {
            if (formats.Contains("zip"))
            {
                var zip = Path.Combine(job, baseName + ".zip");
                if (File.Exists(zip)) File.Delete(zip);
                await RunAsync(Ditto, ["-c", "-k", "--sequesterRsrc", "--keepParent", app, zip], fileLog.Info, ct).ConfigureAwait(false);
                var checks = new List<VerificationCheck> { new("Notarization", VerificationStatus.Passed, "Contains the notarized, stapled .app (a ZIP itself cannot be stapled).") };
                checks.AddRange(await DownloadCheckZipAsync(zip, ct).ConfigureAwait(false));
                build.Artifacts.Add(await ArtifactAsync(build, PackagingFormat.PortableZip, ArtifactKind.File, zip, checks, ct).ConfigureAwait(false));
                fileLog.Info($"ZIP: {zip}");
            }
            if (formats.Contains("pkg"))
            {
                var pkg = Path.Combine(job, baseName + ".pkg");
                var checks = await CreatePkgAsync(app, pkg, d.PkgInstallerName!, build.Settings.Identity.ApplicationName, fileLog, ct).ConfigureAwait(false);
                var rec = await ArtifactAsync(build, PackagingFormat.MacPkg, ArtifactKind.File, pkg, checks, ct).ConfigureAwait(false);
                build.Artifacts.Add(rec);
                made.Add(rec.Id);
            }
            if (formats.Contains("dmg"))
            {
                var dmg = Path.Combine(job, baseName + ".dmg");
                var checks = await CreateDmgAsync(app, dmg, build.Settings.Identity.ApplicationName, d.DeveloperIdApplicationSha1!, fileLog, ct).ConfigureAwait(false);
                var rec = await ArtifactAsync(build, PackagingFormat.MacDmg, ArtifactKind.File, dmg, checks, ct).ConfigureAwait(false);
                build.Artifacts.Add(rec);
                made.Add(rec.Id);
            }
        }
        catch (InvalidOperationException ex)
        {
            fileLog.Info("Packaging stopped: " + ex.Message);
            results.Add(new DistributionActionResult("packaging", [new DistributionStep("Package", VerificationStatus.Failed, ex.Message)]));
            await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
            return results;
        }
        await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
        if (made.Count > 0) results.AddRange(await notary.NotarizeAsync(build, made, keychainProfile, fileLog.Info, ct).ConfigureAwait(false));

        var original = await OriginalUnchangedAsync(build.ToolchainVersions["input"], build.ToolchainVersions["input-sha256"], ct).ConfigureAwait(false);
        results.Add(new DistributionActionResult("original input", [new DistributionStep(original.Name, original.Status, original.Detail)]));
        foreach (var r in results) foreach (var s in r.Steps) fileLog.Info($"{r.Artifact} · {s.Name}: {s.Status} — {s.Detail}");
        await NotarizationService.WriteChecksumsAsync(build, job, ct).ConfigureAwait(false);
        await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
        return results;
    }

    /// <summary>True when every distribution file of this job is notarized and Gatekeeper accepted it.</summary>
    public static bool ReadyForDistribution(BuildRecord build) =>
        IsExistingAppBuild(build) && build.Artifacts.Any(a => a.Kind == ArtifactKind.File)
        && build.Artifacts.Where(a => a.Kind == ArtifactKind.File).All(a =>
            a.Checks.Any(c => c.Name == "Notarization" && c.Status == VerificationStatus.Passed)
            && a.Checks.Where(c => c.Name == "Gatekeeper").All(c => c.Status == VerificationStatus.Passed)
            && a.Checks.Any(c => c.Name == "Gatekeeper"));

    // ───────────── 3. Release Passports ─────────────

    /// <summary>
    /// Release Passports for the job's distribution files, after notarization so they describe the final bytes. The
    /// source is the checkout's own commit (never guessed); with <paramref name="expectedCommit"/>, a checkout at another
    /// commit is refused.
    /// </summary>
    public async Task<IReadOnlyList<(string File, string? Passport, IReadOnlyList<string> Problems)>> WritePassportsAsync(
        BuildRecord build, string? sourceRepository, string? expectedCommit, CancellationToken ct)
    {
        if (!IsMac) throw new InvalidOperationException("Release Passports read the code signature with the macOS Signing Doctor, on a Mac.");
        var app = build.Artifacts.FirstOrDefault(a => a.Format == PackagingFormat.MacAppBundle)?.Path ?? throw new InvalidOperationException("The signed app is missing from this job.");
        var source = sourceRepository is { Length: > 0 } repo
            ? await SourceProvenance.ReadAsync(runner, MacArchivePassport.FindGit(), Path.GetFullPath(repo), ct).ConfigureAwait(false)
            : SourceProvenance.Unknown;
        if (expectedCommit is { Length: > 0 } want)
        {
            if (source.Commit is null) throw new InvalidOperationException($"The source checkout's commit is unknown ({source.Basis}), so it cannot be compared with {want}. Choose the checkout of that commit.");
            if (!source.Commit.StartsWith(want.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidOperationException($"The checkout is at {source.Commit[..12]}, not {want}: check out the commit the app was built from, then write the passports again.");
        }
        var input = Tv(build, "input") ?? "?";
        var inputSha = Tv(build, "input-sha256") ?? "?";
        var origin = Tv(build, "input-origin");
        var unchanged = await OriginalUnchangedAsync(input, inputSha, ct).ConfigureAwait(false);
        var output = new List<(string, string?, IReadOnlyList<string>)>();
        foreach (var a in build.Artifacts.Where(a => a.Kind == ArtifactKind.File && MacArchivePassport.KindOf(a.Path) is not null))
        {
            var notarized = a.Checks.FirstOrDefault(c => c.Name == "Notarization");
            var gatekeeper = a.Checks.FirstOrDefault(c => c.Name == "Gatekeeper");
            var r = await MacArchivePassport.WriteAsync(runner, new MacArchivePassport.Request
            {
                App = app, File = a.Path, Source = source, BuildNumber = MacAppFacts.Read(app).BuildVersion, ReleaseVersion = build.Settings.Identity.Version,
                Toolchain = $"HCG Executable Maker {HostInfo.ProductVersion}: Developer ID signing, notarization and packaging of an app built elsewhere (not rebuilt)",
                ExtraChecks =
                [
                    ("signing input", unchanged.Status == VerificationStatus.Passed ? "Passed" : "Failed",
                        $"Signed from {Path.GetFileName(input)}, SHA-256 {inputSha}{(origin is null ? "" : " (" + origin + ")")}; {unchanged.Detail}"),
                    ("notarization", notarized?.Status == VerificationStatus.Passed ? "Passed" : "Failed", notarized?.Detail ?? "not notarized"),
                    ("gatekeeper", gatekeeper?.Status == VerificationStatus.Passed ? "Passed" : "Failed", gatekeeper?.Detail ?? "not assessed"),
                ],
            }, ct).ConfigureAwait(false);
            output.Add((a.Path, r.Path, r.Problems));
        }
        build.ToolchainVersions["source-commit"] = source.Commit ?? "unknown";
        await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
        return output;
    }

    // ───────────── Steps ─────────────

    /// <summary>Copies (a .app) or extracts (a .zip) the input into <paramref name="temp"/>; the input itself is only read.</summary>
    private async Task<(string? App, string Sha256, List<VerificationCheck> Checks)> StageInputAsync(string input, string? checksumsFile, string temp, CancellationToken ct)
    {
        var checks = new List<VerificationCheck>();
        input = Path.GetFullPath(input);
        Directory.CreateDirectory(temp);
        if (Directory.Exists(input) && input.TrimEnd('/').EndsWith(".app", StringComparison.Ordinal))
        {
            var (digest, _, _) = await Checksums.Sha256DirectoryAsync(input, ct).ConfigureAwait(false);
            if (checksumsFile is not null)
                checks.Add(new("Published checksum", VerificationStatus.NotVerifiable, "A checksum list describes files; this input is a folder. Choose the ZIP the list names to compare it."));
            var copy = Path.Combine(temp, Path.GetFileName(input.TrimEnd('/')));
            if (IsMac) await RunAsync(Ditto, [input, copy], _ => { }, ct).ConfigureAwait(false);
            else CopyTree(input, copy); // inspection only: signing always runs on a Mac, with ditto
            checks.Add(new("Input", VerificationStatus.Passed, $"{Path.GetFileName(input.TrimEnd('/'))} (folder digest {digest[..16]}…), copied; the original is not modified."));
            return (copy, digest, checks);
        }
        if (!File.Exists(input) || !input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new("Input", VerificationStatus.Failed, $"{input}: choose a .app, or a .zip that holds one .app (for example the signing input of a CI run)."));
            return (null, "", checks);
        }
        var sha = await Checksums.Sha256FileAsync(input, ct).ConfigureAwait(false);
        if (checksumsFile is not null) checks.Add(PublishedChecksum(input, sha, checksumsFile));
        if (checks.Any(c => c.Status == VerificationStatus.Failed)) return (null, sha, checks);
        var extracted = Path.Combine(temp, "x");
        if (IsMac) await RunAsync(Ditto, ["-x", "-k", input, extracted], _ => { }, ct).ConfigureAwait(false); // keeps symbolic links, modes and extended attributes
        else System.IO.Compression.ZipFile.ExtractToDirectory(input, extracted); // inspection only
        var apps = Directory.Exists(extracted) ? Directory.GetDirectories(extracted, "*.app") : [];
        if (apps.Length != 1)
        {
            checks.Add(new("Input", VerificationStatus.Failed, apps.Length == 0
                ? $"{Path.GetFileName(input)} holds no .app at its top level (a ZIP made with ditto -c -k --keepParent has one)."
                : $"{Path.GetFileName(input)} holds {apps.Length} apps ({string.Join(", ", apps.Select(Path.GetFileName))}); choose a ZIP with exactly one."));
            return (null, sha, checks);
        }
        checks.Add(new("Input", VerificationStatus.Passed, $"{Path.GetFileName(input)}: SHA-256 {sha}; extracted {Path.GetFileName(apps[0])} (the ZIP is not modified)."));
        return (apps[0], sha, checks);
    }

    /// <summary>The input ZIP's SHA-256 against the line of a SHA256SUMS-style list that names it.</summary>
    public static VerificationCheck PublishedChecksum(string zip, string sha, string checksumsFile)
    {
        if (!File.Exists(checksumsFile)) return new("Published checksum", VerificationStatus.Failed, $"{checksumsFile} does not exist.");
        var name = Path.GetFileName(zip);
        foreach (var line in File.ReadLines(checksumsFile))
        {
            var m = ChecksumLine().Match(line.Trim());
            if (!m.Success || Path.GetFileName(m.Groups["name"].Value) != name) continue;
            return string.Equals(m.Groups["sha"].Value, sha, StringComparison.OrdinalIgnoreCase)
                ? new("Published checksum", VerificationStatus.Passed, $"{name} matches {Path.GetFileName(checksumsFile)} (SHA-256 {sha}).")
                : new("Published checksum", VerificationStatus.Failed, $"{name} is {sha}, but {Path.GetFileName(checksumsFile)} lists {m.Groups["sha"].Value.ToLowerInvariant()}: this is not the file that was published. Download it again; nothing was signed.");
        }
        return new("Published checksum", VerificationStatus.Failed, $"{Path.GetFileName(checksumsFile)} does not list {name}.");
    }

    /// <summary>Identity, version, build and architectures as the release says they must be.</summary>
    public static IReadOnlyList<VerificationCheck> ReleaseChecks(MacAppFacts facts, ExistingMacAppRequest r)
    {
        var checks = new List<VerificationCheck>();
        var numeric = NumericPart().Match(r.ReleaseVersion).Value;
        checks.Add(facts.ShortVersion is not null && NormalizeVersion(facts.ShortVersion) == NormalizeVersion(numeric)
            ? new("Version", VerificationStatus.Passed, $"CFBundleShortVersionString {facts.ShortVersion} is the numeric part of release {r.ReleaseVersion}.")
            : new("Version", VerificationStatus.Failed, $"CFBundleShortVersionString is {facts.ShortVersion ?? "missing"}, but release {r.ReleaseVersion} needs {numeric}. This app is another version; nothing was signed."));
        if (r.ExpectedBuildNumber is { Length: > 0 } build)
            checks.Add(facts.BuildVersion == build
                ? new("Build number", VerificationStatus.Passed, $"CFBundleVersion {facts.BuildVersion}.")
                : new("Build number", VerificationStatus.Failed, $"CFBundleVersion is {facts.BuildVersion ?? "missing"}, not {build}. Changing it would need a new signing input; nothing was signed."));
        if (r.ExpectedBundleIdentifier is { Length: > 0 } id)
            checks.Add(facts.BundleIdentifier == id
                ? new("Bundle identifier", VerificationStatus.Passed, id)
                : new("Bundle identifier", VerificationStatus.Failed, $"{facts.BundleIdentifier} is not {id}."));
        string[] need = r.Architecture switch { TargetArchitecture.Arm64 => ["arm64"], TargetArchitecture.X64 => ["x86_64"], _ => ["arm64", "x86_64"] };
        var missing = need.Where(a => !facts.Architectures.Contains(a)).ToList();
        checks.Add(missing.Count == 0
            ? new("Architectures", VerificationStatus.Passed, $"{string.Join(", ", facts.Architectures)} (needed: {string.Join(", ", need)}).")
            : new("Architectures", VerificationStatus.Failed, $"The main executable has {string.Join(", ", facts.Architectures)}; {string.Join(", ", missing)} is missing."));
        return checks;
    }

    /// <summary>Files in Contents/MacOS that are not Mach-O code.</summary>
    public static IReadOnlyList<string> NonCodeInMacOS(string app)
    {
        var macos = Path.Combine(app, "Contents", "MacOS");
        if (!Directory.Exists(macos)) return [];
        return Directory.EnumerateFiles(macos, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => !MacCodeSigner.IsMachOFile(f)).Select(f => Path.GetRelativePath(macos, f)).Order(StringComparer.Ordinal).ToList();
    }

    public static VerificationCheck CodeOnlyCheck(IReadOnlyList<string> nonCode) => nonCode.Count == 0
        ? new("Contents/MacOS", VerificationStatus.Passed, "Code only (every file is Mach-O), so each signature travels inside its file.")
        : new("Contents/MacOS", VerificationStatus.Failed,
            $"{nonCode.Count} file(s) there are not code ({string.Join(", ", nonCode.Take(5))}{(nonCode.Count > 5 ? ", …" : "")}). codesign can sign them only in extended attributes, which ZIP, PKG and downloads drop, so customers would see a damaged app. " +
            "The app has to be built with data files under Contents/Resources (for .NET: a single-file publish); that is a rebuild, which Executable Maker does not do here.");

    private async Task<IReadOnlyList<VerificationCheck>> VerifySignedAppAsync(string app, SigningIdentity identity, IReadOnlyList<string> entitlements, CancellationToken ct)
    {
        var signer = new MacCodeSigner(runner);
        var checks = new List<VerificationCheck>();
        var (ok, detail) = await signer.VerifyStrictAsync(app, ct).ConfigureAwait(false);
        checks.Add(ok ? new("Code signature", VerificationStatus.Passed, "codesign --verify --deep --strict passed: the whole bundle is sealed.")
                      : new("Code signature", VerificationStatus.Failed, "codesign --verify --deep --strict failed: " + detail));
        var report = await new MacSigningDoctor(runner).InspectAsync(app, ct).ConfigureAwait(false);
        checks.AddRange(SignatureChecks(report, identity));
        checks.Add(await signer.EntitlementsCheckAsync(app, entitlements, requireSandbox: false, ct).ConfigureAwait(false));
        checks.Add(await signer.GatekeeperAsync(app, "execute", ct).ConfigureAwait(false));
        return checks;
    }

    /// <summary>What the Signing Doctor must find after Developer ID signing: the identity's team on the app and on all nested code, runtime and timestamp.</summary>
    public static IReadOnlyList<VerificationCheck> SignatureChecks(MacSigningDoctor.Report report, SigningIdentity identity)
    {
        var checks = new List<VerificationCheck>
        {
            report.SignatureKind == MacSigningDoctor.Kind.DeveloperId && (identity.TeamId is null || report.TeamId == identity.TeamId)
                ? new("Developer ID", VerificationStatus.Passed, $"{report.Authority} (team {report.TeamId}).")
                : new("Developer ID", VerificationStatus.Failed, $"The app is signed as {MacSigningDoctor.Describe(report.SignatureKind)} by {report.Authority ?? "nobody"} (team {report.TeamId ?? "none"}), not by {identity.Name}."),
            report.HardenedRuntime && report.SecureTimestamp
                ? new("Hardened runtime and timestamp", VerificationStatus.Passed, "Both present, as Apple's notary service requires.")
                : new("Hardened runtime and timestamp", VerificationStatus.Failed, $"Hardened runtime {(report.HardenedRuntime ? "on" : "OFF")}, secure timestamp {(report.SecureTimestamp ? "present" : "MISSING")}: the notary service would reject it."),
        };
        var wrong = report.Nested.Where(n => n.Kind != MacSigningDoctor.Kind.DeveloperId || !n.HardenedRuntime || !n.Valid || (report.TeamId is { } team && n.TeamId != team)).ToList();
        checks.Add(wrong.Count == 0
            ? new("Nested code", VerificationStatus.Passed, report.Nested.Count == 0 ? "No nested code." : $"{report.Nested.Count} nested item(s), all Developer ID of the same team with the hardened runtime.")
            : new("Nested code", VerificationStatus.Failed, "Not signed like the app: " + string.Join("; ", wrong.Select(n => $"{n.Path} ({MacSigningDoctor.Describe(n.Kind)}, team {n.TeamId ?? "none"}, runtime {(n.HardenedRuntime ? "on" : "off")}{(n.Valid ? "" : ", invalid")})"))));
        return checks;
    }

    private async Task<VerificationCheck> OriginalUnchangedAsync(string input, string sha, CancellationToken ct)
    {
        string now;
        if (Directory.Exists(input)) now = (await Checksums.Sha256DirectoryAsync(input, ct).ConfigureAwait(false)).Digest;
        else if (File.Exists(input)) now = await Checksums.Sha256FileAsync(input, ct).ConfigureAwait(false);
        else return new("Original input", VerificationStatus.PassedWithWarnings, $"{input} is no longer there; it was not changed by Executable Maker, which works on a copy.");
        return now == sha
            ? new("Original input", VerificationStatus.Passed, $"Unchanged ({Path.GetFileName(input.TrimEnd('/'))} still has SHA-256 {sha[..16]}…).")
            : new("Original input", VerificationStatus.Failed, $"{input} changed since it was read ({sha[..16]}… → {now[..16]}…). Executable Maker works on a copy, so something else modified it.");
    }

    private async Task<IReadOnlyList<VerificationCheck>> DownloadCheckZipAsync(string zip, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "hcg-zip-check-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            await RunAsync(Ditto, ["-x", "-k", zip, temp], _ => { }, ct).ConfigureAwait(false);
            var app = Directory.GetDirectories(temp, "*.app").FirstOrDefault();
            if (app is null) return [new("ZIP contents", VerificationStatus.Failed, "The ZIP holds no .app.")];
            var (vr, vo, ve) = await runner.CaptureAsync(new ProcessSpec { Executable = Xcrun, Arguments = ["stapler", "validate", app], Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
            return
            [
                vr.Succeeded ? new("Stapled ticket", VerificationStatus.Passed, "The app extracted from the ZIP carries its ticket (stapler validate).")
                             : new("Stapled ticket", VerificationStatus.Failed, "The app extracted from the ZIP has no valid ticket: " + MacCodeSigner.LastLines(vo + ve, 2)),
                await new MacCodeSigner(runner).GatekeeperAsync(app, "execute", ct, expectNotarized: true).ConfigureAwait(false),
            ];
        }
        finally { TryDelete(temp); }
    }

    /// <summary>pkgbuild + productbuild (Developer ID Installer): installs exactly /Applications/&lt;App&gt;.app, no scripts, not relocatable.</summary>
    private async Task<IReadOnlyList<VerificationCheck>> CreatePkgAsync(string app, string output, string installerName, string title, FileLog log, CancellationToken ct)
    {
        var plist = InfoPlist.Read(Path.Combine(app, "Contents", "Info.plist"));
        var bundleId = plist.GetValueOrDefault("CFBundleIdentifier")!;
        var version = plist.GetValueOrDefault("CFBundleShortVersionString") ?? "1.0.0";
        var exe = Path.Combine(app, "Contents", "MacOS", plist.GetValueOrDefault("CFBundleExecutable") ?? "");
        var archs = File.Exists(exe) ? MachO.ReadSlices(exe).Select(s => s.Architecture).Distinct().ToList() : [];
        var bundleName = Path.GetFileName(app);
        if (File.Exists(output)) File.Delete(output);
        var staging = Path.Combine(Path.GetTempPath(), "hcg-pkg-" + Guid.NewGuid().ToString("N")[..10]);
        var root = Path.Combine(staging, "root");
        Directory.CreateDirectory(root);
        try
        {
            await RunAsync(Ditto, [app, Path.Combine(root, bundleName)], log.Info, ct).ConfigureAwait(false);
            var componentPlist = Path.Combine(staging, "component.plist");
            await File.WriteAllTextAsync(componentPlist, MacPkgPackager.ComponentPlist(bundleName), ct).ConfigureAwait(false);
            var component = Path.Combine(staging, "component.pkg");
            log.Info($"pkgbuild: {bundleName} → /Applications, identifier {bundleId}, version {version}, not relocatable, no scripts");
            await RunAsync("/usr/bin/pkgbuild", ["--root", root, "--component-plist", componentPlist, "--identifier", bundleId, "--version", version, "--install-location", "/Applications", component], log.Info, ct).ConfigureAwait(false);
            var distribution = Path.Combine(staging, "distribution.xml");
            await File.WriteAllTextAsync(distribution, MacPkgPackager.DistributionXml(title, bundleId, version, "component.pkg", archs, plist.GetValueOrDefault("LSMinimumSystemVersion")), ct).ConfigureAwait(false);
            log.Info($"productbuild: signing with {installerName}");
            await RunAsync("/usr/bin/productbuild", ["--distribution", distribution, "--package-path", staging, "--sign", installerName, "--timestamp", output], log.Info, ct).ConfigureAwait(false);
            if (!File.Exists(output)) throw new InvalidOperationException("productbuild reported success but produced no package.");
            return await PkgInspector.InspectAsync(runner, output, app, bundleId, version, PkgInspector.ExpectedSigner.DeveloperIdInstaller, ct).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
            throw;
        }
        finally { TryDelete(staging); }
    }

    /// <summary>A compressed disk image with the app and an Applications link, signed with the Developer ID Application identity.</summary>
    private async Task<IReadOnlyList<VerificationCheck>> CreateDmgAsync(string app, string output, string volumeName, string identitySha1, FileLog log, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetTempPath(), "hcg-dmg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(staging);
        try
        {
            await RunAsync(Ditto, [app, Path.Combine(staging, Path.GetFileName(app))], log.Info, ct).ConfigureAwait(false);
            File.CreateSymbolicLink(Path.Combine(staging, "Applications"), "/Applications");
            ProcessResult? create = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (File.Exists(output)) File.Delete(output);
                create = await runner.RunAsync(new ProcessSpec { Executable = Hdiutil, Arguments = ["create", "-volname", volumeName, "-srcfolder", staging, "-format", "UDZO", "-fs", "HFS+", output], Timeout = TimeSpan.FromMinutes(10) },
                    (_, l) => log.Info(l), ct).ConfigureAwait(false);
                if ((create.Succeeded && File.Exists(output)) || create.Cancelled || create.TimedOut) break;
                log.Info($"hdiutil create failed (attempt {attempt} of 3, exit code {create.ExitCode}); retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (create is not { Succeeded: true } || !File.Exists(output)) throw new InvalidOperationException($"hdiutil create failed with exit code {create?.ExitCode}.");
            var signer = new MacCodeSigner(runner);
            await signer.SignFileAsync(output, identitySha1, log, ct).ConfigureAwait(false);
            var verify = await runner.RunAsync(new ProcessSpec { Executable = Hdiutil, Arguments = ["verify", output], Timeout = TimeSpan.FromMinutes(5) }, (_, l) => log.Info(l), ct).ConfigureAwait(false);
            var (ok, detail) = await signer.VerifyStrictAsync(output, ct).ConfigureAwait(false);
            return
            [
                verify.Succeeded ? new("hdiutil verify", VerificationStatus.Passed, "Checksum verified by hdiutil.") : new("hdiutil verify", VerificationStatus.Failed, $"hdiutil verify exited with {verify.ExitCode}."),
                ok ? new("Disk image signature", VerificationStatus.Passed, "Developer ID Application signature with a secure timestamp; codesign --verify passed.")
                   : new("Disk image signature", VerificationStatus.Failed, "codesign --verify failed: " + detail),
            ];
        }
        finally { TryDelete(staging); }
    }

    private static async Task<ArtifactRecord> ArtifactAsync(BuildRecord build, PackagingFormat format, ArtifactKind kind, string path, IReadOnlyList<VerificationCheck> checks, CancellationToken ct)
    {
        string sha; long size;
        if (kind == ArtifactKind.Directory) { var (d, _, total) = await Checksums.Sha256DirectoryAsync(path, ct).ConfigureAwait(false); (sha, size) = (d, total); }
        else { sha = await Checksums.Sha256FileAsync(path, ct).ConfigureAwait(false); size = new FileInfo(path).Length; }
        return new ArtifactRecord
        {
            Id = Guid.NewGuid(), BuildId = build.Id, Format = format, Kind = kind, Path = path, SizeBytes = size, Sha256 = sha,
            Verification = Checks.Combine(checks), Checks = checks.ToList(), CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task RunAsync(string exe, IReadOnlyList<string> args, Action<string> log, CancellationToken ct)
    {
        var r = await runner.RunAsync(new ProcessSpec { Executable = exe, Arguments = [.. args], Timeout = TimeSpan.FromMinutes(15) }, (_, l) => log(l), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!r.Succeeded) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed with exit code {r.ExitCode}. See the log for its message.");
    }

    /// <summary>Default file name: Name-Version-macos-arch, with characters a URL or shell would trip over replaced.</summary>
    public static string DefaultBaseName(string label, string version, TargetArchitecture arch) =>
        PathGuard.SanitizeFileName($"{Regex.Replace(label.Trim(), @"\s+", "-")}-{version}-macos-{arch switch { TargetArchitecture.Arm64 => "arm64", TargetArchitecture.X64 => "x64", _ => "universal" }}", "app");

    private static string UniqueFolder(string folder)
    {
        if (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()) return folder;
        for (var n = 2; ; n++) if (!Directory.Exists($"{folder}-{n}")) return $"{folder}-{n}";
    }

    /// <summary>A problem with the entitlement keys, or null. Developer ID apps outside the Mac App Store take boolean keys in reverse-DNS form.</summary>
    public static string? Entitlements_Invalid(IReadOnlyList<string> keys)
    {
        var bad = keys.Where(k => !EntitlementKey().IsMatch(k)).ToList();
        if (bad.Count > 0) return $"Not an entitlement key: {string.Join(", ", bad)} (keys look like com.apple.security.cs.allow-jit).";
        // Refused before anything is signed (Apple's notary service rejects it, and it lets any process attach a debugger).
        if (keys.Any(k => k is "com.apple.security.get-task-allow" or "get-task-allow"))
            return "get-task-allow is a development entitlement: Apple's notary service rejects Developer ID apps that carry it. Remove it; nothing was signed.";
        return null;
    }

    /// <summary>A .NET app (CoreCLR next to the executable, or a single-file host that embeds System.Private.CoreLib).</summary>
    public static bool LooksLikeDotNet(string app)
    {
        var macos = Path.Combine(app, "Contents", "MacOS");
        if (!Directory.Exists(macos)) return false;
        if (Directory.EnumerateFiles(app, "*.dylib", SearchOption.AllDirectories).Any(f => Path.GetFileName(f) is "libcoreclr.dylib" or "libclrjit.dylib")) return true;
        if (MacAppFacts.Read(app).Executable is not { } exe || !File.Exists(Path.Combine(macos, exe))) return false;
        var needle = "System.Private.CoreLib"u8.ToArray();
        using var s = File.OpenRead(Path.Combine(macos, exe));
        var buffer = new byte[(1 << 20) + needle.Length];
        int carry = 0, read;
        while ((read = s.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var span = buffer.AsSpan(0, carry + read);
            if (span.IndexOf(needle) >= 0) return true;
            carry = Math.Min(needle.Length - 1, span.Length);
            span[^carry..].CopyTo(buffer);
        }
        return false;
    }

    /// <summary>Warns (never blocks) when a .NET app is about to be signed without allow-jit: CoreCLR's JIT needs it under the hardened runtime.</summary>
    public static VerificationCheck? JitCheck(string app, IReadOnlyList<string> entitlements) =>
        !LooksLikeDotNet(app) ? null
        : entitlements.Contains("com.apple.security.cs.allow-jit")
            ? new("JIT entitlement", VerificationStatus.Passed, "A .NET app, signed with com.apple.security.cs.allow-jit (CoreCLR compiles code at run time).")
            : new("JIT entitlement", VerificationStatus.PassedWithWarnings, "This is a .NET app: under the hardened runtime CoreCLR needs com.apple.security.cs.allow-jit, or it stops at start-up. Add it unless the app is ahead-of-time compiled.");

    /// <summary>"1.0" and "1.0.0" are the same version; numeric parts compare as numbers.</summary>
    public static string NormalizeVersion(string? numeric)
    {
        var parts = (numeric ?? "").Trim().Split('.').Select(p => int.TryParse(p, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : p).ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts);
    }

    /// <summary>The boolean-true keys of an .entitlements property list; anything else is reported, never signed silently.</summary>
    public static (IReadOnlyList<string> Keys, IReadOnlyList<string> Problems) ReadEntitlementsFile(string path)
    {
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
        System.Xml.Linq.XDocument doc;
        using (var reader = System.Xml.XmlReader.Create(path, settings)) doc = System.Xml.Linq.XDocument.Load(reader);
        var dict = doc.Root?.Element("dict");
        if (dict is null) return ([], ["The file has no top-level <dict>."]);
        var keys = new List<string>();
        var problems = new List<string>();
        var items = dict.Elements().ToList();
        for (var i = 0; i + 1 < items.Count; i += 2)
        {
            var key = items[i].Value;
            switch (items[i + 1].Name.LocalName)
            {
                case "true": keys.Add(key); break;
                case "false": break;
                default: problems.Add($"{key} is a <{items[i + 1].Name.LocalName}>; only true/false entitlements are supported for Developer ID here."); break;
            }
        }
        return (keys, problems);
    }

    private static string? Tv(BuildRecord b, string key) => b.ToolchainVersions.TryGetValue(key, out var v) ? v : null;

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Writes every line to the job's log file as well as to the caller.</summary>
    private sealed class FileLog(string path, Action<string> sink) : IPackagingLog
    {
        private readonly object _gate = new();
        public void Info(string message)
        {
            sink(message);
            lock (_gate) { try { File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}\n"); } catch (IOException) { } }
        }
        public void Warning(string message) => Info("WARNING: " + message);
    }

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")] private static partial Regex SemVer();
    [GeneratedRegex(@"^\d+(\.\d+){0,2}")] private static partial Regex NumericPart();
    [GeneratedRegex(@"^(?<sha>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$")] private static partial Regex ChecksumLine();
    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\.[A-Za-z0-9-]+)+$")] private static partial Regex EntitlementKey();
}
