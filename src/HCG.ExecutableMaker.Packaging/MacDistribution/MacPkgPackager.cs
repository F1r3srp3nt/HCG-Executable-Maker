using System.Security;
using System.Text;
using System.Xml.Linq;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>
/// macOS installer package (.pkg) that installs the built .app into /Applications (or ~/Applications for a
/// current-user install). Built with pkgbuild (component package, not relocatable, no scripts) and productbuild
/// (distribution with OS/architecture checks). Unsigned for local testing, or signed with a Developer ID Installer
/// certificate for distribution (then notarized as a separate, explicit step).
/// </summary>
public sealed class MacPkgPackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.MacPkg;

    public static string ArtifactName(PackagingRequest r) => r.ArtifactBaseName + ".pkg";

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS) throw new InvalidOperationException("Installer packages are built with pkgbuild/productbuild on a Mac.");
        var settings = request.Settings;
        var distribution = settings.MacDistribution;
        var signed = distribution.PkgInstallerSha1 is not null;
        if (signed && distribution.SigningMode != MacSigningMode.DeveloperId)
            throw new InvalidOperationException("A Developer ID Installer signature needs an app signed with Developer ID: an installer around an ad-hoc signed app cannot be notarized. " +
                                                "Choose Developer ID signing for the app, or build the package unsigned for local testing.");
        if (signed && string.IsNullOrEmpty(distribution.PkgInstallerName))
            throw new InvalidOperationException("The installer signing identity has no name; choose it again in Configure › Distribution.");

        var bundleName = PathGuard.SanitizeFileName(settings.Identity.ApplicationName, "Application") + ".app";
        var bundle = Path.Combine(request.OutputRoot, bundleName);
        if (!Directory.Exists(bundle)) throw new InvalidOperationException("The .app bundle must be created before the installer package.");
        var plist = InfoPlist.Read(Path.Combine(bundle, "Contents", "Info.plist"));
        var bundleId = plist.GetValueOrDefault("CFBundleIdentifier") ?? throw new InvalidOperationException("The .app has no CFBundleIdentifier.");
        var version = plist.GetValueOrDefault("CFBundleShortVersionString") ?? "1.0.0";
        var exe = Path.Combine(bundle, "Contents", "MacOS", plist.GetValueOrDefault("CFBundleExecutable") ?? "");
        var archs = File.Exists(exe) ? MachO.ReadSlices(exe).Select(s => s.Architecture).Distinct().ToList() : [];
        var minimumOs = plist.GetValueOrDefault("LSMinimumSystemVersion");

        var output = Path.Combine(request.OutputRoot, ArtifactName(request));
        if (File.Exists(output)) File.Delete(output);
        var staging = Path.Combine(Path.GetTempPath(), "hcg-pkg-" + Guid.NewGuid().ToString("N")[..10]);
        var root = Path.Combine(staging, "root");
        Directory.CreateDirectory(root);
        try
        {
            await RunAsync("/usr/bin/ditto", [bundle, Path.Combine(root, bundleName)], log, cancellationToken).ConfigureAwait(false);
            var componentPlist = Path.Combine(staging, "component.plist");
            await File.WriteAllTextAsync(componentPlist, ComponentPlist(bundleName), cancellationToken).ConfigureAwait(false);
            var componentPkg = Path.Combine(staging, "component.pkg");
            log.Info($"pkgbuild: {bundleName} → /Applications, identifier {bundleId}, version {version}, not relocatable, no scripts");
            await RunAsync("/usr/bin/pkgbuild",
                ["--root", root, "--component-plist", componentPlist, "--identifier", bundleId, "--version", version, "--install-location", "/Applications", componentPkg],
                log, cancellationToken).ConfigureAwait(false);

            var distributionXml = Path.Combine(staging, "distribution.xml");
            await File.WriteAllTextAsync(distributionXml, DistributionXml(settings.Identity.ApplicationName, bundleId, version, "component.pkg", archs, minimumOs), cancellationToken).ConfigureAwait(false);
            List<string> args = ["--distribution", distributionXml, "--package-path", staging];
            if (signed) args.AddRange(["--sign", distribution.PkgInstallerName!]);
            args.Add(output);
            log.Info(signed ? $"productbuild: signing with {distribution.PkgInstallerName}" : "productbuild: UNSIGNED package (local testing only; Gatekeeper blocks it on other Macs)");
            await RunAsync("/usr/bin/productbuild", args, log, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(output)) throw new InvalidOperationException("productbuild reported success but produced no package.");

            var checks = await PkgInspector.InspectAsync(runner, output, bundle, bundleId, version, signed ? PkgInspector.ExpectedSigner.DeveloperIdInstaller : PkgInspector.ExpectedSigner.None,
                cancellationToken).ConfigureAwait(false);
            return [new PackagedOutput(Format, ArtifactKind.File, output, checks)];
        }
        catch
        {
            // Never leave a half-made package behind.
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
            throw;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task RunAsync(string exe, IReadOnlyList<string> args, IPackagingLog log, CancellationToken ct)
    {
        var r = await runner.RunAsync(new ProcessSpec { Executable = exe, Arguments = [.. args], Timeout = TimeSpan.FromMinutes(15) }, (_, l) => log.Info(l), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!r.Succeeded) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed with exit code {r.ExitCode}. See the log above for its message.");
    }

    /// <summary>pkgbuild component property list: the app is installed exactly at /Applications/Name.app (never relocated to another copy).</summary>
    public static string ComponentPlist(string bundleName) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <array>
            <dict>
                <key>BundleHasStrictIdentifier</key>
                <true/>
                <key>BundleIsRelocatable</key>
                <false/>
                <key>BundleIsVersionChecked</key>
                <true/>
                <key>BundleOverwriteAction</key>
                <string>upgrade</string>
                <key>RootRelativeBundlePath</key>
                <string>{SecurityElement.Escape(bundleName)}</string>
            </dict>
        </array>
        </plist>

        """;

    /// <summary>
    /// productbuild distribution: no customization, no scripts, installable for all users (/Applications, needs an
    /// administrator) or the current user (~/Applications), restricted to the app's architectures and minimum macOS.
    /// </summary>
    public static string DistributionXml(string title, string id, string version, string componentFile, IReadOnlyList<string> architectures, string? minimumOs)
    {
        var archs = architectures.Select(a => a switch { "arm64" => "arm64", "x86_64" => "x86_64", _ => null }).OfType<string>().Distinct().ToList();
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<installer-gui-script minSpecVersion=\"2\">\n");
        sb.Append($"    <title>{SecurityElement.Escape(title)}</title>\n");
        sb.Append("    <options customize=\"never\" require-scripts=\"false\"")
          .Append(archs.Count > 0 ? $" hostArchitectures=\"{string.Join(',', archs)}\"" : "").Append("/>\n");
        sb.Append("    <domains enable_anywhere=\"false\" enable_currentUserHome=\"true\" enable_localSystem=\"true\"/>\n");
        if (!string.IsNullOrEmpty(minimumOs))
            sb.Append($"    <volume-check>\n        <allowed-os-versions>\n            <os-version min=\"{SecurityElement.Escape(minimumOs)}\"/>\n        </allowed-os-versions>\n    </volume-check>\n");
        var e = SecurityElement.Escape(id);
        sb.Append($"    <choices-outline>\n        <line choice=\"default\">\n            <line choice=\"{e}\"/>\n        </line>\n    </choices-outline>\n");
        sb.Append("    <choice id=\"default\"/>\n");
        sb.Append($"    <choice id=\"{e}\" visible=\"false\">\n        <pkg-ref id=\"{e}\"/>\n    </choice>\n");
        sb.Append($"    <pkg-ref id=\"{e}\" version=\"{SecurityElement.Escape(version)}\" onConclusion=\"none\">{SecurityElement.Escape(componentFile)}</pkg-ref>\n");
        sb.Append("</installer-gui-script>\n");
        return sb.ToString();
    }
}

/// <summary>Inspects a flat installer package without installing it: signature, structure, scripts and the payload.</summary>
public static class PkgInspector
{
    public enum ExpectedSigner { None, DeveloperIdInstaller, MacInstallerDistribution }

    public static async Task<IReadOnlyList<VerificationCheck>> InspectAsync(IProcessRunner runner, string pkg, string builtApp, string bundleId, string version,
        ExpectedSigner expected, CancellationToken ct)
    {
        var checks = new List<VerificationCheck>();
        var (sr, so, se) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/sbin/pkgutil", Arguments = ["--check-signature", pkg], Timeout = TimeSpan.FromMinutes(2) }, ct).ConfigureAwait(false);
        checks.Add(SignatureCheck(so + se, expected));

        var expanded = Path.Combine(Path.GetTempPath(), "hcg-pkgx-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var (er, _, ee) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/sbin/pkgutil", Arguments = ["--expand-full", pkg, expanded], Timeout = TimeSpan.FromMinutes(10) }, ct).ConfigureAwait(false);
            if (!er.Succeeded)
            {
                checks.Add(new VerificationCheck("Package structure", VerificationStatus.Failed, $"pkgutil --expand-full failed: {ee.Trim()}"));
                return checks;
            }
            checks.AddRange(InspectExpanded(expanded, builtApp, bundleId, version));
        }
        finally
        {
            try { Directory.Delete(expanded, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (expected == ExpectedSigner.DeveloperIdInstaller)
            checks.Add(await new MacCodeSigner(runner).GatekeeperAsync(pkg, "install", ct).ConfigureAwait(false));
        checks.Add(new VerificationCheck("Installation test", VerificationStatus.NotVerifiable,
            "Not installed during the build. Use Test install (an explicit step) to install, check and remove it."));
        return checks;
    }

    public static VerificationCheck SignatureCheck(string output, ExpectedSigner expected)
    {
        var text = output.Trim();
        var unsigned = text.Contains("no signature", StringComparison.OrdinalIgnoreCase);
        var signer = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("1. ", StringComparison.Ordinal))?[3..];
        return expected switch
        {
            ExpectedSigner.None when unsigned => new VerificationCheck("Installer signature", VerificationStatus.PassedWithWarnings,
                "Unsigned package: for local testing only. Gatekeeper blocks it on other Macs; choose a Developer ID Installer certificate for distribution."),
            ExpectedSigner.None => new VerificationCheck("Installer signature", VerificationStatus.Failed, "Expected an unsigned package, but pkgutil reports: " + MacCodeSigner.LastLines(text, 2)),
            ExpectedSigner.DeveloperIdInstaller when signer?.StartsWith("Developer ID Installer:", StringComparison.Ordinal) == true =>
                new VerificationCheck("Installer signature", VerificationStatus.Passed, $"Signed by {signer}."),
            ExpectedSigner.MacInstallerDistribution when signer is not null && (signer.StartsWith("3rd Party Mac Developer Installer:", StringComparison.Ordinal) || signer.StartsWith("Mac Installer Distribution:", StringComparison.Ordinal)) =>
                new VerificationCheck("Installer signature", VerificationStatus.Passed, $"Signed by {signer}."),
            _ => new VerificationCheck("Installer signature", VerificationStatus.Failed,
                unsigned ? "The package is not signed." : $"Signed by {signer ?? "an unexpected identity"}; expected {(expected == ExpectedSigner.DeveloperIdInstaller ? "a Developer ID Installer" : "a Mac Installer Distribution")} certificate."),
        };
    }

    /// <summary>Checks an expanded package (pkgutil --expand-full): Distribution, PackageInfo, no scripts, and the payload .app.</summary>
    public static IReadOnlyList<VerificationCheck> InspectExpanded(string expanded, string builtApp, string bundleId, string version)
    {
        var checks = new List<VerificationCheck>();
        var components = Directory.EnumerateDirectories(expanded, "*.pkg").ToList();
        if (!File.Exists(Path.Combine(expanded, "Distribution")) || components.Count != 1)
        {
            checks.Add(new VerificationCheck("Package structure", VerificationStatus.Failed, $"Expected a distribution with one component package; found {components.Count}."));
            return checks;
        }
        var component = components[0];
        var scripts = Directory.Exists(Path.Combine(component, "Scripts")) && Directory.EnumerateFileSystemEntries(Path.Combine(component, "Scripts")).Any();
        checks.Add(scripts
            ? new VerificationCheck("Installer scripts", VerificationStatus.Failed, "The package contains installer scripts; HCG's packages must not run scripts.")
            : new VerificationCheck("Installer scripts", VerificationStatus.Passed, "None: installing runs no code from the package."));
        try
        {
            var info = XDocument.Load(Path.Combine(component, "PackageInfo")).Root!;
            var loc = (string?)info.Attribute("install-location");
            var id = (string?)info.Attribute("identifier");
            var ver = (string?)info.Attribute("version");
            var relocatable = info.Elements("relocate").Any(r => r.HasElements);
            var ok = loc == "/Applications" && id == bundleId && ver == version && !relocatable;
            checks.Add(ok
                ? new VerificationCheck("Package identity", VerificationStatus.Passed, $"{id} {ver} → {loc} (not relocatable).")
                : new VerificationCheck("Package identity", VerificationStatus.Failed, $"PackageInfo has identifier {id}, version {ver}, install location {loc}, relocatable {relocatable}; expected {bundleId} {version} → /Applications, not relocatable."));
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or NullReferenceException)
        {
            checks.Add(new VerificationCheck("Package identity", VerificationStatus.Failed, "PackageInfo is unreadable: " + ex.Message));
        }
        var payloadApp = Path.Combine(component, "Payload", Path.GetFileName(builtApp));
        checks.Add(BundleComparison.Compare(builtApp, payloadApp, "Package payload", "the package payload"));
        return checks;
    }
}
