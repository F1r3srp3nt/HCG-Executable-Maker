using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// Builds a standard macOS application bundle:
/// <c>Name.app/Contents/{Info.plist, PkgInfo, MacOS/&lt;publish output&gt;, Resources/&lt;icon&gt;.icns}</c>.
/// The bundle is unsigned beyond the ad-hoc signature the .NET SDK applies to the app host.
/// </summary>
public sealed class MacAppBundlePackager : IPackager
{
    public PackagingFormat Format => PackagingFormat.MacAppBundle;

    public Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (request.Settings.Platform != TargetPlatform.MacOS)
            throw new InvalidOperationException("A macOS application bundle can only be produced for a macOS target.");

        var identity = request.Settings.Identity;
        var bundleName = Core.PathGuard.SanitizeFileName(identity.ApplicationName, "Application") + ".app";
        var bundle = Path.Combine(request.OutputRoot, bundleName);
        var contents = Path.Combine(bundle, "Contents");
        var macOs = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");
        log.Info($"Assembling {bundleName}");
        Directory.CreateDirectory(resources);
        FileOps.CopyDirectory(request.PublishDirectory, macOs, cancellationToken);
        var exe = Path.Combine(macOs, request.ExecutableFileName);
        if (File.Exists(exe)) FileOps.MarkExecutable(exe);
        var createdump = Path.Combine(macOs, "createdump");
        if (File.Exists(createdump)) FileOps.MarkExecutable(createdump);

        string? iconFile = null;
        if (request.Icons?.IcnsPath is { } icns)
        {
            iconFile = "AppIcon.icns";
            File.Copy(icns, Path.Combine(resources, iconFile));
        }
        else
        {
            log.Warning("No icon selected: the bundle will use the generic macOS application icon.");
        }

        var version = InfoPlist.ToBundleVersion(identity.Version);
        var entries = new List<(string, object)>
        {
            ("CFBundleDevelopmentRegion", "en"),
            ("CFBundleDisplayName", identity.ApplicationName),
            ("CFBundleExecutable", request.ExecutableFileName),
            ("CFBundleIdentifier", identity.ApplicationId),
            ("CFBundleInfoDictionaryVersion", "6.0"),
            ("CFBundleName", identity.ApplicationName.Length > 15 ? identity.ApplicationName[..15] : identity.ApplicationName),
            ("CFBundlePackageType", "APPL"),
            ("CFBundleShortVersionString", version),
            ("CFBundleVersion", version),
            ("LSMinimumSystemVersion", request.Settings.Architecture == TargetArchitecture.Arm64 ? "11.0" : "10.15"),
            ("NSHighResolutionCapable", true),
            ("NSPrincipalClass", "NSApplication"),
        };
        if (iconFile is not null) entries.Insert(3, ("CFBundleIconFile", iconFile));
        if (!string.IsNullOrWhiteSpace(identity.Publisher))
            entries.Add(("NSHumanReadableCopyright", $"© {identity.Publisher}"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist.Create(entries));
        File.WriteAllText(Path.Combine(contents, "PkgInfo"), "APPL????");

        var outputs = new List<PackagedOutput> { new(Format, ArtifactKind.Directory, bundle, MacBundleVerifier.Verify(bundle, request.Settings.Architecture)) };

        if (request.Host.Platform != TargetPlatform.MacOS)
        {
            // Off-Mac hosts: also produce a ZIP that records Unix permissions for transfer to a Mac.
            var zipPath = Path.Combine(request.OutputRoot, request.ArtifactBaseName + ".app.zip");
            log.Info($"Creating transfer archive {Path.GetFileName(zipPath)} (preserves executable permissions)");
            var files = Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(request.OutputRoot, f).Replace('\\', '/') is var rel
                    ? (f, rel, rel == $"{bundleName}/Contents/MacOS/{request.ExecutableFileName}" || rel == $"{bundleName}/Contents/MacOS/createdump")
                    : default)
                .OrderBy(x => x.rel, StringComparer.Ordinal)
                .ToList();
            var dirs = Directory.EnumerateDirectories(bundle, "*", SearchOption.AllDirectories)
                .Select(d => Path.GetRelativePath(request.OutputRoot, d).Replace('\\', '/'))
                .Prepend(bundleName).OrderBy(d => d, StringComparer.Ordinal).ToList();
            UnixZip.Create(zipPath, files, dirs, cancellationToken);
            var zipChecks = PortableZipPackager.Verify(zipPath, $"{bundleName}/Contents/MacOS", files.Count, request);
            zipChecks.Add(new VerificationCheck("Transfer note", VerificationStatus.Passed,
                "Unzip on the Mac with Finder or `ditto -x -k`. Unsigned apps require right-click → Open on first launch."));
            outputs.Add(new PackagedOutput(Format, ArtifactKind.File, zipPath, zipChecks));
        }
        return (IReadOnlyList<PackagedOutput>)outputs;
    }, cancellationToken);
}

/// <summary>Structural verification of a .app bundle. Does not and cannot prove launchability off-Mac.</summary>
public static class MacBundleVerifier
{
    public static IReadOnlyList<VerificationCheck> Verify(string bundle, TargetArchitecture expectedArch)
    {
        var checks = new List<VerificationCheck>();
        var contents = Path.Combine(bundle, "Contents");
        var plistPath = Path.Combine(contents, "Info.plist");
        if (!bundle.EndsWith(".app", StringComparison.Ordinal) || !Directory.Exists(contents))
        {
            checks.Add(new VerificationCheck("Bundle structure", VerificationStatus.Failed, "Missing Contents directory or .app extension."));
            return checks;
        }
        Dictionary<string, string> plist;
        try { plist = InfoPlist.Read(plistPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
        {
            checks.Add(new VerificationCheck("Info.plist", VerificationStatus.Failed, $"Unreadable: {ex.Message}"));
            return checks;
        }
        string[] required = ["CFBundleExecutable", "CFBundleIdentifier", "CFBundleName", "CFBundlePackageType", "CFBundleShortVersionString", "CFBundleVersion"];
        var missing = required.Where(k => !plist.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        checks.Add(missing.Count == 0
            ? new VerificationCheck("Info.plist", VerificationStatus.Passed, $"{plist["CFBundleIdentifier"]} {plist["CFBundleShortVersionString"]}")
            : new VerificationCheck("Info.plist", VerificationStatus.Failed, "Missing keys: " + string.Join(", ", missing)));
        if (plist.GetValueOrDefault("CFBundlePackageType") is { } t && t != "APPL")
            checks.Add(new VerificationCheck("Package type", VerificationStatus.Failed, $"CFBundlePackageType is '{t}', expected APPL."));

        if (plist.TryGetValue("CFBundleExecutable", out var exeName))
        {
            var exe = Path.Combine(contents, "MacOS", exeName);
            if (!File.Exists(exe))
            {
                checks.Add(new VerificationCheck("Bundle executable", VerificationStatus.Failed, $"Contents/MacOS/{exeName} does not exist."));
            }
            else
            {
                checks.Add(BinaryInspector.CheckMatchesTarget(exe, TargetPlatform.MacOS, expectedArch));
                checks.Add(Checks.ExecutablePermission(exe, TargetPlatform.MacOS));
                var info = BinaryInspector.Inspect(exe);
                checks.Add(info.HasCodeSignature
                    ? new VerificationCheck("Code signature", VerificationStatus.PassedWithWarnings,
                        "App host carries the .NET SDK's ad-hoc signature only. NOT signed with a Developer ID and NOT notarized.")
                    : new VerificationCheck("Code signature", expectedArch == TargetArchitecture.Arm64 ? VerificationStatus.Failed : VerificationStatus.PassedWithWarnings,
                        expectedArch == TargetArchitecture.Arm64
                            ? "Unsigned Apple Silicon executable: macOS will refuse to run it."
                            : "Unsigned. NOT signed with a Developer ID and NOT notarized."));
            }
        }
        if (plist.TryGetValue("CFBundleIconFile", out var icon))
        {
            var iconPath = Path.Combine(contents, "Resources", icon.EndsWith(".icns", StringComparison.Ordinal) ? icon : icon + ".icns");
            checks.Add(File.Exists(iconPath)
                ? new VerificationCheck("Icon", VerificationStatus.Passed, icon)
                : new VerificationCheck("Icon", VerificationStatus.Failed, $"Referenced icon {icon} is missing."));
        }
        checks.Add(OperatingSystem.IsMacOS()
            ? Checks.LaunchNotPerformed()
            : new VerificationCheck("Launch test", VerificationStatus.NotVerifiable, "Launchability can only be confirmed on a Mac. Structure verified only."));
        return checks;
    }
}
