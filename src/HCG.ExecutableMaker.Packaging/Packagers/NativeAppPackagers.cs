using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// A macOS application bundle a C/C++ project produced itself (CMake MACOSX_BUNDLE, or a Makefile that builds Name.app).
/// HCG keeps it as the project built it — Info.plist, resources, frameworks — copies it with ditto (symbolic links and
/// permissions intact) and verifies it. Signing is a separate, reported stage: the build's distribution setting decides
/// (ad hoc by default; Developer ID with the hardened runtime when chosen). Notarization is never part of the build.
/// </summary>
public sealed class NativeAppBundlePackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.MacAppBundle;
    public bool Handles(BuildSettings settings) => settings.Native is { IsMacBundle: true };

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS) throw new InvalidOperationException("macOS app bundles are copied and signed on a Mac.");
        var bundleName = request.ExecutableFileName.Split('/')[0];
        var source = Path.Combine(request.PublishDirectory, bundleName);
        if (!bundleName.EndsWith(".app", StringComparison.Ordinal) || !Directory.Exists(source))
            throw new InvalidOperationException($"The project's app bundle ({bundleName}) is missing from the build output.");
        var dest = Path.Combine(request.OutputRoot, bundleName);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        log.Info($"Copying {bundleName} as the project built it (ditto keeps symbolic links and permissions)");
        var copy = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = [source, dest], Timeout = TimeSpan.FromMinutes(5) }, (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!copy.Succeeded) throw new InvalidOperationException($"ditto could not copy {bundleName} (exit code {copy.ExitCode}).");

        var checks = new List<VerificationCheck>();
        var exe = Path.Combine(request.OutputRoot, request.ExecutableFileName.Replace('/', Path.DirectorySeparatorChar));
        checks.Add(File.Exists(Path.Combine(dest, "Contents", "Info.plist"))
            ? new VerificationCheck("Bundle structure", VerificationStatus.Passed, $"Info.plist and Contents/MacOS/{Path.GetFileName(exe)} are present (kept as the project built them).")
            : new VerificationCheck("Bundle structure", VerificationStatus.Failed, "Contents/Info.plist is missing: the project's bundle is incomplete."));
        checks.Add(BinaryInspector.CheckMatchesTarget(exe, TargetPlatform.MacOS, request.Settings.Architecture));
        checks.Add(Checks.ExecutablePermission(exe, TargetPlatform.MacOS));

        // Signing: a separate stage, as the distribution setting says.
        var codesign = request.Toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath;
        if (codesign is null)
            checks.Add(new VerificationCheck("Code signature", VerificationStatus.NotVerifiable, "codesign was not detected (install the Command Line Tools); the bundle was left as the project built it."));
        else
        {
            var distribution = request.Settings.MacDistribution;
            if (distribution.SigningMode == MacSigningMode.DeveloperId && distribution.DeveloperIdApplicationSha1 is { Length: > 0 } sha1)
            {
                log.Info("Signing stage: Developer ID Application (hardened runtime, secure timestamp)");
                await new MacCodeSigner(runner, codesign).SignDeveloperIdAsync(dest, sha1, distribution.DeveloperIdEntitlements, log, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                log.Info("Signing stage: ad-hoc signature (codesign --force --deep --sign -). Not a distribution signature.");
                var sign = await runner.RunAsync(new ProcessSpec { Executable = codesign, Arguments = ["--force", "--deep", "--sign", "-", dest], Timeout = TimeSpan.FromMinutes(5) },
                    (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!sign.Succeeded) throw new InvalidOperationException($"codesign could not sign {bundleName} ad hoc (exit code {sign.ExitCode}).");
            }
            var (ok, detail) = await new MacCodeSigner(runner, codesign).VerifyStrictAsync(dest, cancellationToken).ConfigureAwait(false);
            checks.Add(new VerificationCheck("Code signature", ok ? (distribution.SigningMode == MacSigningMode.DeveloperId ? VerificationStatus.Passed : VerificationStatus.PassedWithWarnings) : VerificationStatus.Failed,
                ok ? distribution.SigningMode == MacSigningMode.DeveloperId ? "Developer ID signature verified (codesign --verify --deep --strict). Not notarized: notarization is a separate step."
                                                                          : "Ad-hoc signature verified: it runs on this Mac; other Macs show a Gatekeeper warning. Not signed for distribution, not notarized."
                   : "codesign --verify failed: " + detail));
        }
        checks.Add(Checks.LaunchNotPerformed());
        return [new PackagedOutput(Format, ArtifactKind.Directory, dest, checks)];
    }
}

/// <summary>The project's .app (as verified and signed by <see cref="NativeAppBundlePackager"/>) compressed with ditto for sharing.</summary>
public sealed class NativeAppZipPackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.PortableZip;
    public bool Handles(BuildSettings settings) => settings.Native is { IsMacBundle: true };

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS) throw new InvalidOperationException("Zipping a macOS app requires a Mac (ditto).");
        var bundleName = request.ExecutableFileName.Split('/')[0];
        var bundle = Path.Combine(request.OutputRoot, bundleName);
        if (!Directory.Exists(bundle)) bundle = Path.Combine(request.PublishDirectory, bundleName);
        var zip = Path.Combine(request.OutputRoot, request.ArtifactBaseName + ".zip");
        log.Info($"Creating {Path.GetFileName(zip)} with ditto -c -k --keepParent");
        var r = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-c", "-k", "--keepParent", bundle, zip], Timeout = TimeSpan.FromMinutes(10) },
            (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<VerificationCheck>
        {
            r.Succeeded && File.Exists(zip)
                ? new VerificationCheck("Archive created", VerificationStatus.Passed, $"{new FileInfo(zip).Length:N0} bytes (ditto keeps permissions, symbolic links and the code signature)")
                : new VerificationCheck("Archive created", VerificationStatus.Failed, $"ditto exited with code {r.ExitCode}."),
        };
        return [new PackagedOutput(Format, ArtifactKind.File, zip, checks)];
    }
}
