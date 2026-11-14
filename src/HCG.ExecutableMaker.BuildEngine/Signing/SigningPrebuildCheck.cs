using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Signing;

namespace HCG.ExecutableMaker.BuildEngine.Signing;

/// <summary>
/// Validates Apple signing before an iOS device build starts, from this Mac's keychain, the installed (verified)
/// profiles, the project's targets and their .entitlements files. Nothing is changed and Apple is not contacted.
/// </summary>
public sealed class SigningPrebuildCheck(AppleSigningAssetService assets, IProjectSigningStore store, TimeProvider time)
{
    private static readonly string[] EmbeddedProductTypes =
    [
        "com.apple.product-type.app-extension", "com.apple.product-type.extensionkit-extension", "com.apple.product-type.app-extension.messages",
        "com.apple.product-type.application.watchapp2", "com.apple.product-type.application.watchapp2-container",
        "com.apple.product-type.application.on-demand-install-capable", "com.apple.product-type.watchkit2-extension",
    ];

    /// <summary>Null when the build does not sign with an Apple identity (simulator, unsigned, project default).</summary>
    public async Task<SigningValidationReport?> CheckIosAsync(ProjectRecord project, MobileBuildOptions options, MobileAnalysis? analysis,
        string sourceDirectory, ToolchainReport toolchains, CancellationToken ct)
    {
        if (options.Signing is not (MobileSigningMode.AppleAutomatic or MobileSigningMode.AppleManual)) return null;
        if (options.Output is not (MobileOutput.IosArchive or MobileOutput.IosIpa)) return null;
        if (analysis?.Ios is not { } ios) return null;

        var saved = await store.LoadAsync(project.Id.ToString(), ct).ConfigureAwait(false);
        var inventory = await assets.ReadAsync(null, null, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var route = options.ExportMethod switch
        {
            IosExportMethod.AppStoreConnect => SigningRoute.IosAppStore,
            IosExportMethod.ReleaseTesting => SigningRoute.IosAdHoc,
            IosExportMethod.Enterprise => SigningRoute.IosEnterprise,
            _ => SigningRoute.LocalDevelopment,
        };
        var profile = options.ProvisioningProfile is { } named
            ? inventory.Profiles.FirstOrDefault(p => string.Equals(p.Uuid, named, StringComparison.OrdinalIgnoreCase) || p.Name == named)
            : null;
        var identity = saved?.IdentitySha1;
        var extra = new List<SigningCheck>();
        if (options.Signing == MobileSigningMode.AppleManual && identity is null && profile is not null)
        {
            // Xcode's manual signing picks an identity from the keychain that the profile includes; do the same.
            var inProfile = inventory.Certificates.Where(c => profile.CertificateSha1s.Contains(c.Sha1, StringComparer.OrdinalIgnoreCase)).ToList();
            identity = (inProfile.FirstOrDefault(c => c.Readiness(now) == SigningReadiness.Ready) ?? inProfile.FirstOrDefault())?.Sha1;
            if (identity is null)
                extra.Add(new("Signing certificate", SigningCheckCategory.Certificate, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                    $"None of the {profile.CertificateSha1s.Count} certificate(s) that {profile.Name} allows is in this Mac's keychain.",
                    "Import the .p12 of one of the profile's certificates, or regenerate the profile with a certificate you have."));
        }
        if (options.Signing == MobileSigningMode.AppleManual && options.ProvisioningProfile is not null && profile is null)
            extra.Add(new("Provisioning profile", SigningCheckCategory.Profile, SigningCheckScope.Local, SigningCheckOutcome.Failed,
                $"\"{options.ProvisioningProfile}\" is not installed on this Mac (or could not be verified).",
                "Download it in Xcode › Settings › Accounts › Download Manual Profiles, or import it on the Signing & Security page."));

        var targets = Targets(ios, sourceDirectory);
        var config = new ProjectSigningConfiguration
        {
            ProjectId = project.Id.ToString(),
            Platform = SigningPlatform.Ios,
            Mode = options.Signing == MobileSigningMode.AppleAutomatic ? ProjectSigningMode.XcodeAutomatic : ProjectSigningMode.Manual,
            Route = route,
            TeamId = options.DevelopmentTeam ?? saved?.TeamId,
            IdentitySha1 = identity,
            ProfileUuid = profile?.Uuid ?? saved?.ProfileUuid,
            Targets = targets.Select(t => saved?.Targets.FirstOrDefault(s => s.TargetName == t.Name) is { } s
                ? s with { BundleIdentifier = t.BundleIdentifier, IsEmbedded = t.IsEmbedded, Entitlements = t.Entitlements }
                : new TargetSigningConfiguration { TargetName = t.Name, BundleIdentifier = t.BundleIdentifier, IsEmbedded = t.IsEmbedded, Entitlements = t.Entitlements }).ToList(),
            IntendedDevices = saved?.IntendedDevices ?? [],
            UpdatedAt = now,
        };
        var xcode = toolchains.Find(ToolchainIds.Xcode);
        var report = SigningValidator.Validate(new SigningValidationInput
        {
            Configuration = config,
            Inventory = inventory,
            Targets = targets,
            Tools = new SigningToolFacts(OperatingSystem.IsMacOS(), File.Exists("/usr/bin/codesign"), File.Exists("/usr/bin/security"),
                xcode?.IsAvailable == true, xcode?.Version, File.Exists("/usr/bin/productbuild")),
            Now = now,
        });
        return extra.Count == 0 ? report : report with { Checks = [.. extra, .. report.Checks] };
    }

    /// <summary>The app targets and their embedded targets, with entitlements read from each target's .entitlements file.</summary>
    public static IReadOnlyList<SigningTargetFacts> Targets(IosProjectInfo ios, string sourceDirectory)
    {
        var files = EntitlementFiles(sourceDirectory);
        return ios.Targets
            .Where(t => t.IsApplication || EmbeddedProductTypes.Contains(t.ProductType))
            .OrderBy(t => t.IsApplication ? 0 : 1)
            .Select(t => new SigningTargetFacts(t.Name, t.BundleIdentifier, !t.IsApplication, ReadEntitlements(files, t.Name)))
            .ToList();
    }

    private static List<string> EntitlementFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.entitlements", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true })
                .Where(f => !f.Contains("/Pods/", StringComparison.Ordinal) && !f.Contains("/build/", StringComparison.Ordinal)).Take(200).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The target's entitlements file is "&lt;Target&gt;.entitlements" (Xcode's default name), wherever it is in the project.</summary>
    private static IReadOnlyDictionary<string, string> ReadEntitlements(List<string> files, string target)
    {
        var file = files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), target, StringComparison.Ordinal));
        if (file is null) return new Dictionary<string, string>();
        try
        {
            var plist = PlistReader.ParseDictionary(File.ReadAllBytes(file));
            return plist.ToDictionary(kv => kv.Key, kv => AppleCertificates.RenderValue(kv.Value), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException) { return new Dictionary<string, string>(); }
    }

    /// <summary>Log lines for the build log: outcome, scope (this Mac / needs Apple) and the remedy.</summary>
    public static IEnumerable<(BuildLogKind Kind, string Text)> Describe(SigningValidationReport report)
    {
        foreach (var c in report.Checks)
        {
            var kind = c.Outcome switch
            {
                SigningCheckOutcome.Failed => BuildLogKind.Error,
                SigningCheckOutcome.Warning => BuildLogKind.Warning,
                SigningCheckOutcome.Passed => BuildLogKind.Success,
                _ => BuildLogKind.Info,
            };
            var scope = c.Scope == SigningCheckScope.RequiresApple ? " [needs Apple — not checked by HCG]" : "";
            yield return (kind, $"Signing · {c.Title}{(c.Target is { } t ? $" ({t})" : "")}: {c.Outcome}{scope} — {c.Detail}{(c.Remedy is { } r && c.Outcome == SigningCheckOutcome.Failed ? " Fix: " + r : "")}");
        }
    }
}
