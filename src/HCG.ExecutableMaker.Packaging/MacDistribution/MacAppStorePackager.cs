using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>
/// Prepares the Mac App Store submission package. A separate copy of the built .app gets the Mac App Store provisioning
/// profile (Contents/embedded.provisionprofile), the App Sandbox plus the confirmed, evidence-based entitlements and the
/// profile's application identifier, an Apple Distribution signature, and is wrapped by
/// <c>productbuild --component App.app /Applications --sign "3rd Party Mac Developer Installer…"</c>.
/// Validation (altool) and upload (Transporter / altool) are separate, explicit, manual steps.
/// </summary>
public sealed class MacAppStorePackager(IProcessRunner runner, TimeProvider time) : IPackager
{
    public PackagingFormat Format => PackagingFormat.MacAppStore;
    public bool Handles(BuildSettings settings) => settings.Swift is { Kind: SwiftAppKind.GuiApp };

    public static string ArtifactName(PackagingRequest r) => r.ArtifactBaseName + "-MacAppStore.pkg";

    /// <summary>Everything that must be true before anything is signed; each problem is a specific blocker with its fix.</summary>
    public static IReadOnlyList<string> Blockers(BuildSettings settings, MacProvisioningProfile? profile, DateTimeOffset now, bool hasRootResourceBundles)
    {
        var d = settings.MacDistribution;
        var list = new List<string>();
        if (settings.Swift is not { Kind: SwiftAppKind.GuiApp })
            list.Add("Only SwiftUI/AppKit Swift apps can be prepared for the Mac App Store in this version (.NET apps and command-line tools are incompatible).");
        if (hasRootResourceBundles)
            list.Add("SwiftPM resource bundles must sit at the .app root (where Bundle.module looks), outside the signed Contents folder; App Store validation rejects unsealed content. Load resources from Bundle.main or build with Xcode.");
        if (string.IsNullOrEmpty(d.AppStoreApplicationSha1))
            list.Add("Choose an Apple Distribution (or 3rd Party Mac Developer Application) certificate. Create one in Xcode › Settings › Accounts › Manage Certificates, or at developer.apple.com › Certificates.");
        if (string.IsNullOrEmpty(d.AppStoreInstallerName))
            list.Add("Choose a Mac Installer Distribution (3rd Party Mac Developer Installer) certificate. Create one at developer.apple.com › Certificates › Mac Installer Distribution.");
        if (profile is null)
            list.Add("Choose a Mac App Store provisioning profile for this bundle identifier (developer.apple.com › Profiles › Mac App Store Connect; Xcode downloads it to ~/Library/Developer/Xcode/UserData/Provisioning Profiles).");
        else
        {
            if (profile.Kind != MacProfileKind.MacAppStore)
                list.Add($"The profile \"{profile.Name}\" is a {profile.Kind} profile; the Mac App Store needs a Mac App Store distribution profile.");
            if (!profile.Matches(settings.Identity.ApplicationId))
                list.Add($"The profile's App ID {profile.ApplicationIdentifier} does not cover the bundle identifier {settings.Identity.ApplicationId}.");
            if (profile.IsExpired(now))
                list.Add($"The profile \"{profile.Name}\" expired on {profile.Expiration:yyyy-MM-dd}. Regenerate it at developer.apple.com and download it again.");
            if (d.AppStoreApplicationSha1 is { } sha && profile.CertificateSha1s.Count > 0 && !profile.CertificateSha1s.Contains(sha, StringComparer.OrdinalIgnoreCase))
                list.Add($"The profile \"{profile.Name}\" does not include the chosen distribution certificate. Regenerate the profile with that certificate.");
        }
        if (!d.EntitlementsConfirmed)
            list.Add("Review and confirm the App Sandbox entitlements (Configure › Distribution).");
        return list;
    }

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS) throw new InvalidOperationException("Mac App Store packages are prepared on a Mac.");
        var settings = request.Settings;
        var d = settings.MacDistribution;
        var profile = d.AppStoreProfilePath is null ? null : MacSigningInventoryReader.ReadProfile(d.AppStoreProfilePath);
        var rootBundles = Directory.EnumerateDirectories(request.PublishDirectory, "*.bundle").Any();
        var blockers = Blockers(settings, profile, time.GetUtcNow(), rootBundles);
        if (blockers.Count > 0) throw new InvalidOperationException("Mac App Store preparation is blocked: " + string.Join(" ", blockers));

        var bundleName = SwiftAppBundlePackager.BundleName(settings);
        var built = Path.Combine(request.OutputRoot, bundleName);
        if (!Directory.Exists(built)) throw new InvalidOperationException("The .app bundle must be created before the Mac App Store package.");
        var output = Path.Combine(request.OutputRoot, ArtifactName(request));
        if (File.Exists(output)) File.Delete(output);
        var staging = Path.Combine(Path.GetTempPath(), "hcg-mas-" + Guid.NewGuid().ToString("N")[..10]);
        var app = Path.Combine(staging, bundleName);
        Directory.CreateDirectory(staging);
        var codesign = request.Toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath ?? "/usr/bin/codesign";
        var signer = new MacCodeSigner(runner, codesign);
        try
        {
            // A separate copy: the .app artifact keeps its own (ad-hoc / Developer ID) signature.
            await RunAsync("/usr/bin/ditto", ["--noextattr", "--noqtn", built, app], log, cancellationToken).ConfigureAwait(false);
            // App Store / TestFlight apps must not carry com.apple.quarantine (or other stray attributes).
            await RunAsync("/usr/bin/xattr", ["-cr", app], log, cancellationToken).ConfigureAwait(false);
            File.Copy(profile!.Path, Path.Combine(app, "Contents", "embedded.provisionprofile"), overwrite: true);
            log.Info($"Embedded provisioning profile \"{profile.Name}\" ({profile.ApplicationIdentifier}, expires {profile.Expiration:yyyy-MM-dd})");

            var team = profile.TeamId!;
            var plist = Entitlements.ToPlist(
                [Entitlements.AppSandbox, .. d.AppStoreEntitlements],
                [(Entitlements.ApplicationIdentifier, $"{team}.{settings.Identity.ApplicationId}"), (Entitlements.TeamIdentifier, team)]);
            log.Info($"Signing for the Mac App Store ({d.AppStoreApplicationSha1}), App Sandbox entitlements: {string.Join(", ", new[] { Entitlements.AppSandbox }.Concat(d.AppStoreEntitlements))}");
            await signer.SignAppStoreAsync(app, d.AppStoreApplicationSha1!, plist, log, cancellationToken).ConfigureAwait(false);

            log.Info($"productbuild --component {bundleName} /Applications --sign \"{d.AppStoreInstallerName}\"");
            await RunAsync("/usr/bin/productbuild", ["--component", app, "/Applications", "--sign", d.AppStoreInstallerName!, output], log, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(output)) throw new InvalidOperationException("productbuild reported success but produced no package.");

            var checks = new List<VerificationCheck>();
            checks.AddRange(SwiftBundleVerifier.VerifyStructure(app, settings, []));
            var (ok, detail) = await signer.VerifyStrictAsync(app, cancellationToken).ConfigureAwait(false);
            checks.Add(ok ? new VerificationCheck("Code signature", VerificationStatus.Passed, "codesign --verify --strict --deep passed.")
                : new VerificationCheck("Code signature", VerificationStatus.Failed, "codesign --verify failed: " + detail));
            var info = await signer.DescribeAsync(app, cancellationToken).ConfigureAwait(false);
            checks.Add(info?.Leaf is { } leaf && (leaf.StartsWith("Apple Distribution:", StringComparison.Ordinal) || leaf.StartsWith("3rd Party Mac Developer Application:", StringComparison.Ordinal)) && info.TeamId == team
                ? new VerificationCheck("Signing certificate", VerificationStatus.Passed, $"{leaf} (team {info.TeamId})")
                : new VerificationCheck("Signing certificate", VerificationStatus.Failed, $"Expected an Apple Distribution certificate of team {team}; found {info?.Leaf ?? "no signature"}."));
            checks.Add(await signer.EntitlementsCheckAsync(app, [.. d.AppStoreEntitlements], requireSandbox: true, cancellationToken).ConfigureAwait(false));
            var signedEnt = await signer.ReadEntitlementsAsync(app, cancellationToken).ConfigureAwait(false);
            checks.Add(signedEnt.GetValueOrDefault(Entitlements.ApplicationIdentifier) as string == $"{team}.{settings.Identity.ApplicationId}"
                ? new VerificationCheck("Application identifier", VerificationStatus.Passed, $"{team}.{settings.Identity.ApplicationId}, covered by the profile {profile.ApplicationIdentifier}.")
                : new VerificationCheck("Application identifier", VerificationStatus.Failed, "The signed application identifier does not match the profile and bundle identifier."));
            checks.Add(new VerificationCheck("Provisioning profile", VerificationStatus.Passed,
                $"\"{profile.Name}\" (Mac App Store, team {team}, expires {profile.Expiration:yyyy-MM-dd}) embedded as Contents/embedded.provisionprofile."));
            var (xr, xo, _) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/xattr", Arguments = ["-r", app], Timeout = TimeSpan.FromMinutes(1) }, cancellationToken).ConfigureAwait(false);
            checks.Add(xr.Succeeded && !xo.Contains("com.apple.quarantine", StringComparison.Ordinal)
                ? new VerificationCheck("Quarantine attribute", VerificationStatus.Passed, "No com.apple.quarantine attribute in the app.")
                : new VerificationCheck("Quarantine attribute", VerificationStatus.Failed, "The app still carries com.apple.quarantine; App Store apps must not."));
            checks.AddRange((await PkgInspector.InspectAsync(runner, output, app, settings.Identity.ApplicationId, plistVersion(app), PkgInspector.ExpectedSigner.MacInstallerDistribution, cancellationToken)
                .ConfigureAwait(false)).Where(c => c.Name != "Installation test"));
            checks.Add(new VerificationCheck("App Store validation", VerificationStatus.NotVerifiable,
                "Not performed during the build. Use Validate with App Store Connect (an explicit step); upload and submission stay manual (Transporter or altool --upload-app)."));
            return [new PackagedOutput(Format, ArtifactKind.File, output, checks)];
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
            throw;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static string plistVersion(string app) => InfoPlist.Read(Path.Combine(app, "Contents", "Info.plist")).GetValueOrDefault("CFBundleShortVersionString") ?? "";
    }

    private async Task RunAsync(string exe, IReadOnlyList<string> args, IPackagingLog log, CancellationToken ct)
    {
        var r = await runner.RunAsync(new ProcessSpec { Executable = exe, Arguments = [.. args], Timeout = TimeSpan.FromMinutes(15) }, (_, l) => log.Info(l), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!r.Succeeded) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed with exit code {r.ExitCode}. See the log above for its message.");
    }
}
