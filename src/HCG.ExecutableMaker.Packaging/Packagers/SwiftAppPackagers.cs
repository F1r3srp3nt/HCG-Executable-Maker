using System.IO.Compression;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// Builds a macOS .app from a SwiftPM executable product:
/// <c>Name.app/Contents/{Info.plist, PkgInfo, MacOS/&lt;product&gt;, Resources/{AppIcon.icns, *.bundle}}</c>, then
/// signs it ad hoc. Unlike <see cref="MacAppBundlePackager"/> (which wraps a .NET publish folder), nothing besides the
/// executable goes into Contents/MacOS, LSMinimumSystemVersion comes from the package / Mach-O deployment target rather
/// than a .NET default, and privacy usage descriptions are written from the build options.
/// </summary>
public sealed class SwiftAppBundlePackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.MacAppBundle;
    public bool Handles(BuildSettings settings) => settings.Swift is { Kind: SwiftAppKind.GuiApp };

    public static string BundleName(BuildSettings settings) => PathGuard.SanitizeFileName(settings.Identity.ApplicationName, "Application") + ".app";

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        var settings = request.Settings;
        var swift = settings.Swift ?? throw new InvalidOperationException("Swift build options are missing.");
        if (settings.Platform != TargetPlatform.MacOS || request.Host.Platform != TargetPlatform.MacOS)
            throw new InvalidOperationException("Swift .app bundles are produced on a Mac for macOS.");
        var codesign = request.Toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath
                       ?? throw new InvalidOperationException("codesign was not detected. Install the Command Line Tools: xcode-select --install");

        var distribution = settings.MacDistribution;
        if (distribution.SigningMode == MacSigningMode.DeveloperId)
        {
            if (string.IsNullOrEmpty(distribution.DeveloperIdApplicationSha1))
                throw new InvalidOperationException("Developer ID signing is selected but no Developer ID Application certificate was chosen.");
            if (!distribution.EntitlementsConfirmed)
                throw new InvalidOperationException("Developer ID signing needs the entitlements to be reviewed and confirmed first (Configure › Distribution).");
            if (Directory.EnumerateDirectories(request.PublishDirectory, "*.bundle").Any())
                throw new InvalidOperationException("Developer ID signing is incompatible with this project: SwiftPM resource bundles must sit at the .app root " +
                    "(where Bundle.module looks), outside the signed Contents folder, and Gatekeeper rejects such an app. Load resources from Bundle.main or build with Xcode.");
        }

        var identity = settings.Identity;
        var bundleName = BundleName(settings);
        var bundle = Path.Combine(request.OutputRoot, bundleName);
        var contents = Path.Combine(bundle, "Contents");
        var macOs = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");
        if (Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
        log.Info($"Assembling {bundleName} (executable {request.ExecutableFileName})");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);

        var sourceExe = Path.Combine(request.PublishDirectory, request.ExecutableFileName);
        if (!File.Exists(sourceExe)) throw new InvalidOperationException($"The Swift product executable {request.ExecutableFileName} is missing from the build output.");
        var exe = Path.Combine(macOs, request.ExecutableFileName);
        File.Copy(sourceExe, exe);
        FileOps.MarkExecutable(exe);

        // SwiftPM resource bundles are placed after signing (below): SwiftPM's generated Bundle.module accessor looks for
        // them at Bundle.main.bundleURL/<name>.bundle — the .app's root — and nowhere inside Contents.
        var resourceBundles = Directory.EnumerateDirectories(request.PublishDirectory, "*.bundle").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList();

        string? iconFile = null;
        if (request.Icons?.IcnsPath is { } icns)
        {
            iconFile = "AppIcon.icns";
            File.Copy(icns, Path.Combine(resources, iconFile));
        }
        else
        {
            log.Warning("No icon selected: the app will use the generic macOS application icon.");
        }

        // Deployment target: the option (validated ≥ the package's platforms entry), else what the linker recorded.
        var slices = MachO.ReadSlices(exe);
        var binaryMin = HighestMinimum(slices);
        var minimum = swift.MinimumMacOS ?? binaryMin;
        if (swift.MinimumMacOS is null)
            log.Info(binaryMin is null ? "No macOS deployment target found in the executable; LSMinimumSystemVersion is omitted."
                : $"LSMinimumSystemVersion {binaryMin} (deployment target recorded in the executable).");

        var entries = new List<(string, object)>
        {
            ("CFBundleDevelopmentRegion", "en"),
            ("CFBundleDisplayName", identity.ApplicationName),
            ("CFBundleExecutable", request.ExecutableFileName),
            ("CFBundleIdentifier", identity.ApplicationId),
            ("CFBundleInfoDictionaryVersion", "6.0"),
            ("CFBundleName", identity.ApplicationName.Length > 15 ? identity.ApplicationName[..15] : identity.ApplicationName),
            ("CFBundlePackageType", "APPL"),
            ("CFBundleShortVersionString", InfoPlist.ToBundleVersion(identity.Version)),
            ("CFBundleVersion", swift.BuildNumber),
            ("NSHighResolutionCapable", true),
        };
        if (iconFile is not null) entries.Add(("CFBundleIconFile", iconFile));
        if (minimum is not null) entries.Add(("LSMinimumSystemVersion", minimum));
        if (!string.IsNullOrWhiteSpace(swift.Category)) entries.Add(("LSApplicationCategoryType", swift.Category));
        if (!string.IsNullOrWhiteSpace(identity.Publisher)) entries.Add(("NSHumanReadableCopyright", $"© {identity.Publisher}"));
        foreach (var (key, text) in swift.UsageDescriptions.OrderBy(k => k.Key, StringComparer.Ordinal))
            entries.Add((key, text));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist.Create(entries));
        File.WriteAllText(Path.Combine(contents, "PkgInfo"), "APPL????");

        if (distribution.SigningMode == MacSigningMode.DeveloperId)
        {
            // Developer ID: hardened runtime + secure timestamp + the confirmed entitlements (no resource bundles; checked above).
            var signer = new MacCodeSigner(runner, codesign);
            log.Info($"Signing for direct distribution with Developer ID ({distribution.DeveloperIdApplicationSha1}), hardened runtime, secure timestamp, " +
                     $"entitlements: {(distribution.DeveloperIdEntitlements.Count == 0 ? "none" : string.Join(", ", distribution.DeveloperIdEntitlements))}");
            await signer.SignDeveloperIdAsync(bundle, distribution.DeveloperIdApplicationSha1!, distribution.DeveloperIdEntitlements, log, cancellationToken).ConfigureAwait(false);
            var devChecks = new List<VerificationCheck>(SwiftBundleVerifier.VerifyStructure(bundle, settings, []));
            devChecks.AddRange(await signer.VerifyDeveloperIdAsync(bundle,
                new SigningIdentity(distribution.DeveloperIdApplicationSha1!, "", AppleCertificateKind.DeveloperIdApplication, null),
                distribution.DeveloperIdEntitlements, cancellationToken).ConfigureAwait(false));
            devChecks.Add(await signer.GatekeeperAsync(bundle, "execute", cancellationToken).ConfigureAwait(false));
            devChecks.Add(new VerificationCheck("Notarization", VerificationStatus.NotVerifiable, "Not notarized yet. Notarization is a separate, explicit step (Build results › Notarize)."));
            devChecks.Add(Checks.LaunchNotPerformed());
            return [new PackagedOutput(Format, ArtifactKind.Directory, bundle, devChecks)];
        }

        // Ad-hoc signature over the whole bundle (seals Info.plist and resources). Not a Developer ID; not notarized.
        log.Info("Signing ad hoc: codesign --force --sign - " + bundleName);
        var sign = await runner.RunAsync(new ProcessSpec
        {
            Executable = codesign, Arguments = ["--force", "--sign", "-", "--timestamp=none", bundle], Timeout = TimeSpan.FromMinutes(5),
        }, (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!sign.Succeeded) throw new InvalidOperationException($"codesign failed with exit code {sign.ExitCode}.");

        foreach (var name in resourceBundles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileOps.CopyDirectory(Path.Combine(request.PublishDirectory, name), Path.Combine(bundle, name), cancellationToken);
            log.Info($"Resource bundle {name} → {bundleName}/{name} (where SwiftPM's Bundle.module looks: Bundle.main.bundleURL). " +
                     "It is outside the signed Contents folder: macOS code signing seals only Contents, and SwiftPM looks nowhere inside it.");
        }

        var checks = await SwiftBundleVerifier.VerifyAsync(runner, codesign, bundle, settings, resourceBundles, cancellationToken).ConfigureAwait(false);
        return [new PackagedOutput(Format, ArtifactKind.Directory, bundle, checks)];
    }

    internal static string? HighestMinimum(IEnumerable<MachO.Slice> slices) =>
        slices.Select(s => s.MinimumMacOS).OfType<string>()
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions)).FirstOrDefault();

    internal static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}

/// <summary>Verification of a Swift .app: structure, Info.plist, slices, deployment target, privacy keys and the signature.</summary>
public static class SwiftBundleVerifier
{
    public static async Task<IReadOnlyList<VerificationCheck>> VerifyAsync(IProcessRunner runner, string codesign, string bundle, BuildSettings settings,
        IReadOnlyList<string> expectedResourceBundles, CancellationToken cancellationToken)
    {
        var checks = new List<VerificationCheck>(VerifyStructure(bundle, settings, expectedResourceBundles));
        // With root-level resource bundles the whole-bundle check necessarily reports "unsealed contents present in the
        // bundle root"; the signature itself is verified on a copy without them so a real signing problem still fails.
        var target = bundle;
        string? temp = null;
        if (expectedResourceBundles.Count > 0)
        {
            temp = Path.Combine(Path.GetTempPath(), "hcg-sigcheck-" + Guid.NewGuid().ToString("N")[..10]);
            target = Path.Combine(temp, Path.GetFileName(bundle));
            var copy = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = [bundle, target], Timeout = TimeSpan.FromMinutes(5) },
                (_, _) => { }, cancellationToken).ConfigureAwait(false);
            if (copy.Succeeded) foreach (var name in expectedResourceBundles) { var d = Path.Combine(target, name); if (Directory.Exists(d)) Directory.Delete(d, true); }
            else target = bundle;
        }
        try
        {
            var output = new List<string>();
            var verify = await runner.RunAsync(new ProcessSpec
            {
                Executable = codesign, Arguments = ["--verify", "--strict", "--deep", "--verbose=2", target], Timeout = TimeSpan.FromMinutes(5),
            }, (_, l) => { lock (output) output.Add(l); }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var adHoc = "Ad-hoc signature only: NOT signed with a Developer ID and NOT notarized, so other Macs show a Gatekeeper warning (right-click → Open).";
            checks.Add(verify.Succeeded
                ? new VerificationCheck("Code signature", VerificationStatus.PassedWithWarnings,
                    expectedResourceBundles.Count == 0
                        ? "codesign --verify --strict passed. " + adHoc
                        : $"codesign --verify --strict passed for the signed Contents folder. The SwiftPM resource bundle(s) at the app root ({string.Join(", ", expectedResourceBundles)}) are outside the seal, so a whole-bundle check reports \"unsealed contents present in the bundle root\" and Gatekeeper will reject a downloaded copy. Local use works; for distribution, load resources from Bundle.main (Contents/Resources) or build with Xcode. " + adHoc)
                : new VerificationCheck("Code signature", VerificationStatus.Failed,
                    $"codesign --verify --strict failed (exit code {verify.ExitCode}): {string.Join(" ", output.TakeLast(3))}"));
        }
        finally
        {
            if (temp is not null) try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        checks.Add(Checks.LaunchNotPerformed());
        return checks;
    }

    /// <summary>Checks that need no tools (also usable off-Mac and in tests).</summary>
    public static IReadOnlyList<VerificationCheck> VerifyStructure(string bundle, BuildSettings settings, IReadOnlyList<string> expectedResourceBundles)
    {
        var swift = settings.Swift!;
        var checks = new List<VerificationCheck>();
        var contents = Path.Combine(bundle, "Contents");
        if (!bundle.EndsWith(".app", StringComparison.Ordinal) || !Directory.Exists(contents))
        {
            checks.Add(new VerificationCheck("Bundle structure", VerificationStatus.Failed, "Missing Contents directory or .app extension."));
            return checks;
        }
        Dictionary<string, string> plist;
        try { plist = InfoPlist.Read(Path.Combine(contents, "Info.plist")); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
        {
            checks.Add(new VerificationCheck("Info.plist", VerificationStatus.Failed, $"Unreadable: {ex.Message}"));
            return checks;
        }
        string[] required = ["CFBundleExecutable", "CFBundleIdentifier", "CFBundleName", "CFBundlePackageType", "CFBundleShortVersionString", "CFBundleVersion"];
        var missing = required.Where(k => !plist.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        checks.Add(missing.Count == 0
            ? new VerificationCheck("Info.plist", VerificationStatus.Passed,
                $"{plist["CFBundleIdentifier"]} {plist["CFBundleShortVersionString"]} ({plist["CFBundleVersion"]}), display name {plist.GetValueOrDefault("CFBundleDisplayName", plist["CFBundleName"])}")
            : new VerificationCheck("Info.plist", VerificationStatus.Failed, "Missing keys: " + string.Join(", ", missing)));
        if (plist.GetValueOrDefault("CFBundleExecutable") != swift.Product)
            checks.Add(new VerificationCheck("Bundle executable name", VerificationStatus.Failed, $"CFBundleExecutable is '{plist.GetValueOrDefault("CFBundleExecutable")}', expected the product '{swift.Product}'."));

        var exe = Path.Combine(contents, "MacOS", plist.GetValueOrDefault("CFBundleExecutable") ?? swift.Product);
        if (!File.Exists(exe))
        {
            checks.Add(new VerificationCheck("Bundle executable", VerificationStatus.Failed, $"Contents/MacOS/{Path.GetFileName(exe)} does not exist."));
            return checks;
        }
        var extra = Directory.EnumerateFileSystemEntries(Path.Combine(contents, "MacOS")).Select(Path.GetFileName).Where(n => n != Path.GetFileName(exe)).ToList();
        if (extra.Count > 0)
            checks.Add(new VerificationCheck("Contents/MacOS", VerificationStatus.Failed, "Unexpected files next to the executable (they break the signature): " + string.Join(", ", extra)));
        checks.Add(BinaryInspector.CheckMatchesTarget(exe, TargetPlatform.MacOS, settings.Architecture));
        checks.Add(Checks.ExecutablePermission(exe, TargetPlatform.MacOS));

        var slices = MachO.ReadSlices(exe);
        var binaryMin = SwiftAppBundlePackager.HighestMinimum(slices);
        var plistMin = plist.GetValueOrDefault("LSMinimumSystemVersion");
        if (binaryMin is null)
            checks.Add(new VerificationCheck("Deployment target", VerificationStatus.PassedWithWarnings, $"The executable records no macOS deployment target; LSMinimumSystemVersion is {plistMin ?? "not set"}."));
        else if (plistMin is null)
            checks.Add(new VerificationCheck("Deployment target", VerificationStatus.PassedWithWarnings, $"Executable built for macOS {binaryMin}+, but LSMinimumSystemVersion is not set."));
        else if (SwiftAppBundlePackager.CompareVersions(plistMin, binaryMin) < 0)
            checks.Add(new VerificationCheck("Deployment target", VerificationStatus.Failed,
                $"LSMinimumSystemVersion {plistMin} is lower than the executable's deployment target macOS {binaryMin}: the app would be offered on Macs where it cannot start."));
        else
            checks.Add(new VerificationCheck("Deployment target", VerificationStatus.Passed, $"LSMinimumSystemVersion {plistMin}; executable built for macOS {binaryMin}+."));

        foreach (var (key, _) in swift.UsageDescriptions)
            checks.Add(plist.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)
                ? new VerificationCheck($"Privacy: {key}", VerificationStatus.Passed, text.Length > 80 ? text[..80] + "…" : text)
                : new VerificationCheck($"Privacy: {key}", VerificationStatus.Failed, $"{key} is missing: macOS terminates the app when it requests that permission."));

        if (plist.TryGetValue("CFBundleIconFile", out var icon))
            checks.Add(File.Exists(Path.Combine(contents, "Resources", icon.EndsWith(".icns", StringComparison.Ordinal) ? icon : icon + ".icns"))
                ? new VerificationCheck("Icon", VerificationStatus.Passed, icon)
                : new VerificationCheck("Icon", VerificationStatus.Failed, $"Referenced icon {icon} is missing."));
        if (expectedResourceBundles.Count > 0)
        {
            var absent = expectedResourceBundles.Where(b => !Directory.Exists(Path.Combine(bundle, b)) || !Directory.EnumerateFileSystemEntries(Path.Combine(bundle, b)).Any()).ToList();
            checks.Add(absent.Count == 0
                ? new VerificationCheck("Resource bundles", VerificationStatus.Passed,
                    $"{string.Join(", ", expectedResourceBundles)} at the app root, where SwiftPM's generated Bundle.module accessor looks (Bundle.main.bundleURL).")
                : new VerificationCheck("Resource bundles", VerificationStatus.Failed, "Missing or empty: " + string.Join(", ", absent)));
        }
        return checks;
    }
}

/// <summary>
/// The Swift .app compressed with <c>ditto -c -k --keepParent</c> (Finder's "Compress" format: keeps permissions,
/// extended attributes and the signature), then extracted to a temporary folder and re-verified with codesign.
/// </summary>
public sealed class SwiftAppZipPackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.PortableZip;
    public bool Handles(BuildSettings settings) => settings.Swift is { Kind: SwiftAppKind.GuiApp };

    private const string Ditto = "/usr/bin/ditto";

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.MacOS) throw new InvalidOperationException("Zipping a Swift .app requires a macOS host (ditto).");
        var bundleName = SwiftAppBundlePackager.BundleName(request.Settings);
        var bundle = Path.Combine(request.OutputRoot, bundleName);
        if (!Directory.Exists(bundle)) throw new InvalidOperationException("The .app bundle must be created before its ZIP.");
        var zip = Path.Combine(request.OutputRoot, request.ArtifactBaseName + ".app.zip");
        if (File.Exists(zip)) File.Delete(zip);
        log.Info($"Creating {Path.GetFileName(zip)} with ditto");
        var create = await runner.RunAsync(new ProcessSpec
        {
            Executable = Ditto, Arguments = ["-c", "-k", "--sequesterRsrc", "--keepParent", bundle, zip], Timeout = TimeSpan.FromMinutes(10),
        }, (_, l) => log.Info(l), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!create.Succeeded || !File.Exists(zip)) throw new InvalidOperationException($"ditto failed with exit code {create.ExitCode}.");

        var checks = new List<VerificationCheck>();
        var exeEntryName = $"{bundleName}/Contents/MacOS/{request.ExecutableFileName}";
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var expected = Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Count();
            var files = archive.Entries.Count(e => !e.FullName.EndsWith('/') && !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal));
            checks.Add(files == expected
                ? new VerificationCheck("Archive integrity", VerificationStatus.Passed, $"{files} files")
                : new VerificationCheck("Archive integrity", VerificationStatus.Failed, $"Expected {expected} files, found {files}."));
            var exeEntry = archive.GetEntry(exeEntryName);
            checks.Add(exeEntry is null
                ? new VerificationCheck("Executable present", VerificationStatus.Failed, $"{exeEntryName} not found in the archive.")
                : (UnixZip.GetMode(exeEntry) & 0b001_000_000) != 0
                    ? new VerificationCheck("Executable permission", VerificationStatus.Passed, $"{exeEntryName} recorded as {Convert.ToString(UnixZip.GetMode(exeEntry), 8)}")
                    : new VerificationCheck("Executable permission", VerificationStatus.Failed, "Executable bit not recorded in the archive."));
        }
        catch (InvalidDataException ex)
        {
            checks.Add(new VerificationCheck("Archive integrity", VerificationStatus.Failed, ex.Message));
        }

        // Round trip: extract exactly as a recipient would and confirm the signature still verifies.
        var codesign = request.Toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath;
        var temp = Path.Combine(request.OutputRoot, ".zip-verify");
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            var extract = await runner.RunAsync(new ProcessSpec { Executable = Ditto, Arguments = ["-x", "-k", zip, temp], Timeout = TimeSpan.FromMinutes(10) },
                (_, _) => { }, cancellationToken).ConfigureAwait(false);
            if (!extract.Succeeded)
                checks.Add(new VerificationCheck("Extract round trip", VerificationStatus.Failed, $"ditto -x -k failed with exit code {extract.ExitCode}."));
            else if (codesign is null)
                checks.Add(new VerificationCheck("Extract round trip", VerificationStatus.NotVerifiable, "Extracted, but codesign is unavailable to re-verify the signature."));
            else
            {
                // Root-level SwiftPM resource bundles are outside the seal (see the .app's signature check): verify Contents.
                var extracted = Path.Combine(temp, bundleName);
                var rootBundles = Directory.EnumerateDirectories(extracted, "*.bundle").ToList();
                var bundlesIntact = rootBundles.All(b => Directory.EnumerateFileSystemEntries(b, "*", SearchOption.AllDirectories).Any());
                foreach (var b in rootBundles) Directory.Delete(b, true);
                var verify = await runner.RunAsync(new ProcessSpec
                {
                    Executable = codesign, Arguments = ["--verify", "--strict", "--deep", extracted], Timeout = TimeSpan.FromMinutes(5),
                }, (_, _) => { }, cancellationToken).ConfigureAwait(false);
                checks.Add(verify.Succeeded && bundlesIntact
                    ? new VerificationCheck("Extract round trip", VerificationStatus.Passed, "Extracted with ditto -x -k; the .app's signature still verifies (codesign --verify --strict)" +
                        (rootBundles.Count > 0 ? $" and its {rootBundles.Count} resource bundle(s) extracted intact." : "."))
                    : new VerificationCheck("Extract round trip", VerificationStatus.Failed, $"After extraction, codesign --verify failed (exit code {verify.ExitCode})."));
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return [new PackagedOutput(Format, ArtifactKind.File, zip, checks)];
    }
}
