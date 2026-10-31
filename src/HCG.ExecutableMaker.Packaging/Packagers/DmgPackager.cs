using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// Creates a compressed UDZO disk image with hdiutil. Requires a macOS host; the catalog never offers it elsewhere.
/// NOTE: implemented but not exercised by automated tests in the Linux development environment.
/// </summary>
public sealed class DmgPackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.MacDmg;

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

            var verify = await runner.RunAsync(new ProcessSpec { Executable = hdiutil, Arguments = ["verify", dmg], Timeout = TimeSpan.FromMinutes(5) },
                (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
            IReadOnlyList<VerificationCheck> checks =
            [
                verify.Succeeded
                    ? new VerificationCheck("hdiutil verify", VerificationStatus.Passed, "Checksum verified by hdiutil.")
                    : new VerificationCheck("hdiutil verify", VerificationStatus.Failed, $"hdiutil verify exited with {verify.ExitCode}."),
                new VerificationCheck("Code signature", VerificationStatus.PassedWithWarnings, "Disk image is not signed or notarized."),
            ];
            return [new PackagedOutput(Format, ArtifactKind.File, dmg, checks)];
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}
