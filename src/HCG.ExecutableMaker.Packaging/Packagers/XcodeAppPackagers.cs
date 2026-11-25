using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>How a macOS app is signed, in words that keep ad hoc, development and Developer ID apart.</summary>
public static class MacSignatureDescription
{
    public enum Kind { Unsigned, AdHoc, Development, DeveloperId, Other }

    public static Kind Classify(SignatureInfo? info) => info switch
    {
        null => Kind.Unsigned,
        { AdHoc: true } => Kind.AdHoc,
        { Leaf: { } leaf } when leaf.StartsWith("Developer ID Application", StringComparison.Ordinal) => Kind.DeveloperId,
        { Leaf: { } leaf } when leaf.StartsWith("Apple Development", StringComparison.Ordinal) || leaf.StartsWith("Mac Developer", StringComparison.Ordinal) => Kind.Development,
        _ => Kind.Other,
    };

    public static string Describe(SignatureInfo? info) => Classify(info) switch
    {
        Kind.Unsigned => "Not signed.",
        Kind.AdHoc => "Ad hoc signature (no certificate, no team). It runs on the Mac that built it; on other Macs Gatekeeper blocks the first launch until the person allows it in System Settings › Privacy & Security › Open Anyway. Not Developer ID signed, not notarized.",
        Kind.Development => $"Development signature ({info!.Leaf}, team {info.TeamId}). Runs on Macs registered to that team; not a distribution signature, not notarized.",
        Kind.DeveloperId => $"Developer ID signature ({info!.Leaf}){(info.HardenedRuntime ? ", hardened runtime" : ", WITHOUT the hardened runtime (notarization requires it)")}{(info.SecureTimestamp ? ", secure timestamp" : "")}. Not notarized: notarization is a separate, explicit step.",
        _ => $"Signed by {info!.Leaf ?? "an unknown authority"}{(info.TeamId is { } t ? $" (team {t})" : "")}. Not notarized.",
    };
}

/// <summary>
/// The .app xcodebuild built for a native macOS Xcode project, kept exactly as the project built it (name, bundle ID,
/// version, icon, resources, entitlements and the project's own signature). It is copied with ditto and verified from
/// the bundle itself: identity, architectures of every executable, minimum macOS, icon, resources and the signature.
/// The signature Xcode applied is kept (an unsigned build is signed ad hoc, and reported so). Quarantine and Gatekeeper
/// settings are never touched.
/// </summary>
public sealed class XcodeAppBundlePackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.MacAppBundle;
    public bool Handles(BuildSettings settings) => settings.MacXcode is not null;

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS) throw new InvalidOperationException("macOS app bundles are copied and verified on a Mac.");
        var o = request.Settings.MacXcode!;
        var bundleName = request.ExecutableFileName.Split('/')[0];
        var source = Path.Combine(request.PublishDirectory, bundleName);
        if (!bundleName.EndsWith(".app", StringComparison.Ordinal) || !Directory.Exists(source))
            throw new InvalidOperationException($"The app xcodebuild built ({bundleName}) is missing from the build output.");
        var dest = Path.Combine(request.OutputRoot, bundleName);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        log.Info($"Copying {bundleName} as Xcode built it (ditto keeps symbolic links, permissions, extended attributes and the signature)");
        var copy = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = [source, dest], Timeout = TimeSpan.FromMinutes(5) }, (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!copy.Succeeded) throw new InvalidOperationException($"ditto could not copy {bundleName} (exit code {copy.ExitCode}).");

        var checks = new List<VerificationCheck>();
        var facts = MacAppFacts.Read(dest);
        checks.Add(facts.Problem is null
            ? new VerificationCheck("Bundle structure", VerificationStatus.Passed, $"Info.plist and Contents/MacOS/{facts.Executable} are present.")
            : new VerificationCheck("Bundle structure", VerificationStatus.Failed, facts.Problem));
        checks.AddRange(VerifyFacts(facts, o, request.Settings.Identity));
        if (facts.Executable is { } exe) checks.Add(Checks.ExecutablePermission(Path.Combine(dest, "Contents", "MacOS", exe), TargetPlatform.MacOS));

        var codesign = request.Toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath ?? "/usr/bin/codesign";
        var signer = new MacCodeSigner(runner, codesign);
        // The project's signing is kept: Xcode signed the app during the build as the project says (ad hoc, development
        // or Developer ID with the project's own entitlements). HCG does not re-sign Xcode-built apps.
        var before = await signer.DescribeAsync(dest, cancellationToken).ConfigureAwait(false);
        if (before is null)
        {
            // Apple silicon refuses to run unsigned code; an ad-hoc signature is the minimum, and it is reported as such.
            log.Info("The build is not signed (the project disables code signing). Signing stage: ad hoc (codesign --force --deep --sign -) so it can run on Apple silicon.");
            var sign = await runner.RunAsync(new ProcessSpec { Executable = codesign, Arguments = ["--force", "--deep", "--sign", "-", dest], Timeout = TimeSpan.FromMinutes(5) },
                (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!sign.Succeeded) throw new InvalidOperationException($"codesign could not sign {bundleName} ad hoc (exit code {sign.ExitCode}).");
        }
        else
        {
            log.Info($"Signing stage: kept the signature Xcode applied with the project's settings ({MacSignatureDescription.Classify(before)}).");
        }

        var after = await signer.DescribeAsync(dest, cancellationToken).ConfigureAwait(false);
        var (ok, detail) = await signer.VerifyStrictAsync(dest, cancellationToken).ConfigureAwait(false);
        var kind = MacSignatureDescription.Classify(after);
        checks.Add(!ok
            ? new VerificationCheck("Code signature", VerificationStatus.Failed, "codesign --verify --deep --strict failed: " + detail)
            : new VerificationCheck("Code signature", kind == MacSignatureDescription.Kind.DeveloperId ? VerificationStatus.Passed : VerificationStatus.PassedWithWarnings,
                "codesign --verify --deep --strict passed. " + MacSignatureDescription.Describe(after)));
        // Gatekeeper's verdict, reported, never changed: an ad-hoc or unnotarized app is expected to be rejected here.
        var gate = await signer.GatekeeperAsync(dest, "execute", cancellationToken).ConfigureAwait(false);
        checks.Add(gate.Status == VerificationStatus.Failed && kind != MacSignatureDescription.Kind.DeveloperId
            ? gate with { Status = VerificationStatus.PassedWithWarnings, Detail = gate.Detail + " Expected for an app that is not Developer ID signed and notarized; HCG does not remove quarantine or change Gatekeeper." }
            : gate);
        checks.Add(Checks.LaunchNotPerformed());
        return [new PackagedOutput(Format, ArtifactKind.Directory, dest, checks)];
    }

    /// <summary>The bundle is the project's app: its identity, version, architectures and minimum macOS are what was asked for.</summary>
    public static IEnumerable<VerificationCheck> VerifyFacts(MacAppFacts facts, MacXcodeBuildOptions o, AppIdentity identity)
    {
        if (facts.Problem is not null) yield break;
        var expectedId = o.BundleIdentifier is { } id && !id.Contains("$(", StringComparison.Ordinal) ? id : null;
        yield return expectedId is null || facts.BundleIdentifier == expectedId
            ? new VerificationCheck("App identity", VerificationStatus.Passed,
                $"{facts.Label} · {facts.BundleIdentifier} · version {facts.ShortVersion} ({facts.BuildVersion}) — the project's own identity (not rebranded).")
            : new VerificationCheck("App identity", VerificationStatus.Failed, $"Bundle ID is {facts.BundleIdentifier}; the project sets {expectedId}.");

        var expected = MachO.ExpectedSlices(o.Architecture.ToTarget()).Order(StringComparer.Ordinal).ToList();
        var wrong = facts.MachOFiles.Where(m => !expected.All(m.Value.Contains)).Select(m => $"{m.Key} ({string.Join(", ", m.Value)})").ToList();
        var main = facts.Architectures.Order(StringComparer.Ordinal).ToList();
        yield return main.SequenceEqual(expected) && wrong.Count == 0
            ? new VerificationCheck("Architectures", VerificationStatus.Passed,
                $"{o.Architecture.Label()}: {string.Join(" + ", main)} in Contents/MacOS/{facts.Executable}" + (facts.MachOFiles.Count > 1 ? $" and all {facts.MachOFiles.Count} Mach-O files in the bundle." : "."))
            : new VerificationCheck("Architectures", VerificationStatus.Failed,
                $"Expected {string.Join(" + ", expected)}; the executable has {(main.Count == 0 ? "none" : string.Join(" + ", main))}." + (wrong.Count > 0 ? " Missing in: " + string.Join("; ", wrong.Take(5)) : ""));

        var min = facts.MinimumSystemVersion;
        var linked = facts.LinkedMinimum;
        yield return min is not null && (o.DeploymentTarget is null || Same(min, o.DeploymentTarget)) && linked.All(l => Same(l, min))
            ? new VerificationCheck("Minimum macOS", VerificationStatus.Passed, $"macOS {min} (LSMinimumSystemVersion; the executable is linked for {string.Join(", ", linked)}).")
            : new VerificationCheck("Minimum macOS", VerificationStatus.Failed,
                $"LSMinimumSystemVersion {min ?? "missing"}, linked for {string.Join(", ", linked)}, project deployment target {o.DeploymentTarget ?? "not set"}.");

        yield return facts.HasIcon
            ? new VerificationCheck("App icon", VerificationStatus.Passed, facts.IconName is not null ? $"CFBundleIconName {facts.IconName} in the compiled asset catalog (Assets.car)." : $"{facts.IconFile} in Resources.")
            : new VerificationCheck("App icon", VerificationStatus.PassedWithWarnings, "The project defines no app icon that ended up in the bundle (macOS shows a generic icon).");
        yield return new VerificationCheck("Resources", facts.ResourceFiles > 0 ? VerificationStatus.Passed : VerificationStatus.PassedWithWarnings,
            $"{facts.ResourceFiles} file(s) in Contents/Resources, as Xcode built them.");
    }

    private static bool Same(string a, string b) => ProjectInspectionCompare(a, b) == 0;

    /// <summary>Numeric version comparison ("26" == "26.0").</summary>
    private static int ProjectInspectionCompare(string a, string b)
    {
        static int[] P(string v) => v.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var (x, y) = (P(a), P(b));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}

/// <summary>
/// The verified .app compressed with ditto (keeps the bundle, permissions, links and signature), then checked by
/// expanding it again: the expanded app is identical and its signature still verifies.
/// </summary>
public sealed class XcodeAppZipPackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.PortableZip;
    public bool Handles(BuildSettings settings) => settings.MacXcode is not null;

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS) throw new InvalidOperationException("Zipping a macOS app requires a Mac (ditto).");
        var bundleName = request.ExecutableFileName.Split('/')[0];
        var bundle = Path.Combine(request.OutputRoot, bundleName);
        if (!Directory.Exists(bundle)) throw new InvalidOperationException("The .app must be packaged and verified before its ZIP.");
        var zip = Path.Combine(request.OutputRoot, request.ArtifactBaseName + ".zip");
        if (File.Exists(zip)) File.Delete(zip);
        log.Info($"Creating {Path.GetFileName(zip)} with ditto -c -k --keepParent (the ZIP holds {bundleName} itself)");
        var r = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-c", "-k", "--keepParent", bundle, zip], Timeout = TimeSpan.FromMinutes(10) },
            (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<VerificationCheck>();
        if (!r.Succeeded || !File.Exists(zip))
        {
            checks.Add(new VerificationCheck("Archive created", VerificationStatus.Failed, $"ditto exited with code {r.ExitCode}."));
            return [new PackagedOutput(Format, ArtifactKind.File, zip, checks)];
        }
        checks.Add(new VerificationCheck("Archive created", VerificationStatus.Passed, $"{new FileInfo(zip).Length:N0} bytes."));

        var expand = Path.Combine(Path.GetTempPath(), "hcg-zipcheck-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var x = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-x", "-k", zip, expand], Timeout = TimeSpan.FromMinutes(10) }, (_, _) => { }, cancellationToken).ConfigureAwait(false);
            var expanded = Path.Combine(expand, bundleName);
            if (!x.Succeeded || !Directory.Exists(expanded))
                checks.Add(new VerificationCheck("Expands to the app", VerificationStatus.Failed, $"ditto -x -k did not produce {bundleName} (exit code {x.ExitCode})."));
            else
            {
                checks.Add(BundleComparison.Compare(bundle, expanded, "Expands to the app", "the expanded ZIP"));
                var codesign = request.Toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath ?? "/usr/bin/codesign";
                var (ok, detail) = await new MacCodeSigner(runner, codesign).VerifyStrictAsync(expanded, cancellationToken).ConfigureAwait(false);
                checks.Add(new VerificationCheck("Signature after expanding", ok ? VerificationStatus.Passed : VerificationStatus.Failed,
                    ok ? "codesign --verify --deep --strict passes on the expanded app." : "The expanded app's signature does not verify: " + detail));
            }
        }
        finally
        {
            try { if (Directory.Exists(expand)) Directory.Delete(expand, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return [new PackagedOutput(Format, ArtifactKind.File, zip, checks)];
    }
}

/// <summary>
/// The .xcarchive xcodebuild archive produced for the same scheme and configuration, delivered as a ZIP made with ditto
/// (keeps the bundle structure, the app's signature and the dSYM). Checked by expanding it again: the archive's
/// Info.plist and its Products/Applications app must be there.
/// </summary>
public sealed class XcodeArchivePackager(IProcessRunner runner) : IPackager
{
    public const string StagedSuffix = ".xcarchive";
    public PackagingFormat Format => PackagingFormat.MacXcodeArchive;
    public bool Handles(BuildSettings settings) => settings.MacXcode is not null;

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS) throw new InvalidOperationException("Xcode archives are made on a Mac.");
        var staged = Directory.EnumerateDirectories(request.PublishDirectory, "*" + StagedSuffix).FirstOrDefault()
                     ?? throw new InvalidOperationException("xcodebuild archive produced no .xcarchive in this build.");
        var zip = Path.Combine(request.OutputRoot, request.ArtifactBaseName + ".xcarchive.zip");
        if (File.Exists(zip)) File.Delete(zip);
        log.Info($"Creating {Path.GetFileName(zip)} from {Path.GetFileName(staged)} with ditto -c -k --keepParent");
        var r = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-c", "-k", "--keepParent", staged, zip], Timeout = TimeSpan.FromMinutes(15) },
            (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<VerificationCheck>();
        if (!r.Succeeded || !File.Exists(zip))
            return [new PackagedOutput(Format, ArtifactKind.File, zip, [new VerificationCheck("Archive created", VerificationStatus.Failed, $"ditto exited with code {r.ExitCode}.")])];
        checks.Add(new VerificationCheck("Archive created", VerificationStatus.Passed, $"{new FileInfo(zip).Length:N0} bytes."));
        var expand = Path.Combine(Path.GetTempPath(), "hcg-xcarchive-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var x = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-x", "-k", zip, expand], Timeout = TimeSpan.FromMinutes(15) }, (_, _) => { }, cancellationToken).ConfigureAwait(false);
            var root = Path.Combine(expand, Path.GetFileName(staged));
            var apps = Directory.Exists(Path.Combine(root, "Products", "Applications")) ? Directory.GetDirectories(Path.Combine(root, "Products", "Applications"), "*.app") : [];
            var dsyms = Directory.Exists(Path.Combine(root, "dSYMs")) ? Directory.GetDirectories(Path.Combine(root, "dSYMs"), "*.dSYM").Length : 0;
            checks.Add(!x.Succeeded || !File.Exists(Path.Combine(root, "Info.plist"))
                ? new VerificationCheck("Archive structure", VerificationStatus.Failed, "The expanded ZIP has no .xcarchive Info.plist.")
                : apps.Length == 0
                ? new VerificationCheck("Archive structure", VerificationStatus.Failed, "The archive holds no app in Products/Applications (is the target's SKIP_INSTALL set to YES?).")
                : new VerificationCheck("Archive structure", VerificationStatus.Passed, $"Info.plist, {string.Join(", ", apps.Select(Path.GetFileName))} in Products/Applications, {dsyms} dSYM(s)."));
        }
        finally
        {
            try { if (Directory.Exists(expand)) Directory.Delete(expand, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return [new PackagedOutput(Format, ArtifactKind.File, zip, checks)];
    }
}
