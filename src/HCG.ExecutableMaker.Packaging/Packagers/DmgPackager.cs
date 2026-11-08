using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// Creates a compressed UDZO disk image with hdiutil. Requires a macOS host; the catalog never offers it elsewhere.
/// NOTE: implemented but not exercised by automated tests in the Linux development environment.
/// </summary>
public sealed class DmgPackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.MacDmg;
    public bool Handles(BuildSettings settings) => settings.Electron is null; // Electron: electron-builder's dmg (ElectronDmgPackager)

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS)
            throw new InvalidOperationException("DMG creation requires a macOS host.");
        var hdiutil = request.Toolchains.Find(ToolchainIds.Hdiutil)?.ExecutablePath
                      ?? throw new InvalidOperationException("hdiutil was not detected.");
        var bundleName = PathGuard.SanitizeFileName(request.Settings.Identity.ApplicationName, "Application") + ".app";
        var bundle = Path.Combine(request.OutputRoot, bundleName);
        if (!Directory.Exists(bundle))
            throw new InvalidOperationException("The .app bundle must be created before the DMG.");

        var staging = Path.Combine(request.OutputRoot, ".dmg-staging");
        Directory.CreateDirectory(staging);
        try
        {
            var dittoTarget = Path.Combine(staging, bundleName);
            var ditto = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = [bundle, dittoTarget] },
                (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
            if (!ditto.Succeeded) throw new InvalidOperationException($"ditto failed with exit code {ditto.ExitCode}.");
            File.CreateSymbolicLink(Path.Combine(staging, "Applications"), "/Applications");

            var dmg = Path.Combine(request.OutputRoot, request.ArtifactBaseName + ".dmg");
            // hdiutil intermittently fails with "Resource busy" (notably on CI Macs); retry a few times.
            ProcessResult? create = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (File.Exists(dmg)) File.Delete(dmg);
                create = await runner.RunAsync(new ProcessSpec
                {
                    Executable = hdiutil,
                    Arguments = ["create", "-volname", request.Settings.Identity.ApplicationName, "-srcfolder", staging, "-format", "UDZO", "-fs", "HFS+", dmg],
                    Timeout = TimeSpan.FromMinutes(10),
                }, (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
                if ((create.Succeeded && File.Exists(dmg)) || create.Cancelled || create.TimedOut) break;
                log.Warning($"hdiutil create failed (attempt {attempt} of 3, exit code {create.ExitCode}); retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (create is not { Succeeded: true } || !File.Exists(dmg))
                throw new InvalidOperationException($"hdiutil create failed with exit code {create?.ExitCode}.");

            var distribution = request.Settings.MacDistribution;
            var signer = new MacCodeSigner(runner, request.Toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath ?? "/usr/bin/codesign");
            if (distribution.SigningMode == MacSigningMode.DeveloperId && distribution.DeveloperIdApplicationSha1 is { } sha1)
                await signer.SignFileAsync(dmg, sha1, log, cancellationToken).ConfigureAwait(false);

            var verify = await runner.RunAsync(new ProcessSpec { Executable = hdiutil, Arguments = ["verify", dmg], Timeout = TimeSpan.FromMinutes(5) },
                (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
            var checks = new List<VerificationCheck>
            {
                verify.Succeeded
                    ? new VerificationCheck("hdiutil verify", VerificationStatus.Passed, "Checksum verified by hdiutil.")
                    : new VerificationCheck("hdiutil verify", VerificationStatus.Failed, $"hdiutil verify exited with {verify.ExitCode}."),
            };
            checks.Add(await MountCheckAsync(hdiutil, dmg, bundle, bundleName, cancellationToken).ConfigureAwait(false));
            if (distribution.SigningMode == MacSigningMode.DeveloperId)
            {
                var (ok, detail) = await signer.VerifyStrictAsync(dmg, cancellationToken).ConfigureAwait(false);
                checks.Add(ok
                    ? new VerificationCheck("Disk image signature", VerificationStatus.Passed, "Signed with the Developer ID Application certificate and a secure timestamp; codesign --verify passed.")
                    : new VerificationCheck("Disk image signature", VerificationStatus.Failed, "codesign --verify failed: " + detail));
                checks.Add(await signer.GatekeeperAsync(dmg, "open", cancellationToken).ConfigureAwait(false));
                checks.Add(new VerificationCheck("Notarization", VerificationStatus.NotVerifiable, "Not notarized yet. Notarization is a separate, explicit step (Build results › Notarize)."));
            }
            else
            {
                checks.Add(new VerificationCheck("Code signature", VerificationStatus.PassedWithWarnings, "Disk image is not signed or notarized (ad-hoc build)."));
            }
            return [new PackagedOutput(Format, ArtifactKind.File, dmg, checks)];
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Mounts the image read-only and compares the .app inside with the built one; checks the Applications link.</summary>
    private async Task<VerificationCheck> MountCheckAsync(string hdiutil, string dmg, string bundle, string bundleName, CancellationToken ct)
    {
        var mount = Path.Combine(Path.GetTempPath(), "hcg-dmg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(mount);
        var attached = false;
        try
        {
            var attach = await runner.RunAsync(new ProcessSpec
            {
                Executable = hdiutil, Arguments = ["attach", "-readonly", "-nobrowse", "-noautoopen", "-mountpoint", mount, dmg], Timeout = TimeSpan.FromMinutes(5),
            }, (_, _) => { }, ct).ConfigureAwait(false);
            if (!attach.Succeeded) return new VerificationCheck("Disk image contents", VerificationStatus.Failed, $"hdiutil attach failed with exit code {attach.ExitCode}.");
            attached = true;
            var link = new FileInfo(Path.Combine(mount, "Applications"));
            var compare = BundleComparison.Compare(bundle, Path.Combine(mount, bundleName), "Disk image contents", "the mounted disk image");
            return compare.Status == VerificationStatus.Passed && link.LinkTarget != "/Applications"
                ? compare with { Status = VerificationStatus.Failed, Detail = compare.Detail + " The Applications shortcut is missing." }
                : compare;
        }
        finally
        {
            if (attached)
                await runner.RunAsync(new ProcessSpec { Executable = hdiutil, Arguments = ["detach", mount, "-force"], Timeout = TimeSpan.FromMinutes(2) },
                    (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
            try { Directory.Delete(mount); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
