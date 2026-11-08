using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>
/// The real state of each macOS distribution capability on this Mac and for this project: Ready, Setup required
/// (with the exact guided setup), Incompatible with this project (with the reason), or Failed (with the actual error).
/// Nothing is offered as Ready unless its pipeline exists and its prerequisites are present.
/// </summary>
public static class MacDistributionCatalog
{
    public const string PkgDescription = "Installer package (.pkg) that installs the .app into Applications. Unsigned for local testing, or signed with a Developer ID Installer certificate for distribution. No installer scripts.";
    public const string AppStoreDescription = "Mac App Store submission package: sandboxed, signed with Apple Distribution, embedded provisioning profile, signed with Mac Installer Distribution. Validation and upload stay separate and manual.";

    public const string CertificatesHelp = "Create certificates in Xcode › Settings › Accounts › (your team) › Manage Certificates, or at developer.apple.com › Certificates, then press Refresh.";

    public static (PackagingAvailability, string?) Pkg(PackagingContext ctx, bool dotNet)
    {
        if (!ctx.IsGuiApplication) return (PackagingAvailability.NotApplicable, "The installer package installs .app bundles; console apps are distributed as portable folders/ZIPs.");
        if (ctx.Host.Platform != TargetPlatform.MacOS) return (PackagingAvailability.RequiresDifferentHost, "Installer packages are built with pkgbuild/productbuild, which exist only on macOS.");
        if (ctx.Toolchains is { } t && t.Find(ToolchainIds.Productbuild) is { IsAvailable: false })
            return (PackagingAvailability.MissingTool, "productbuild was not found in /usr/bin. It ships with macOS; reinstall the Command Line Tools: xcode-select --install");
        var d = ctx.Distribution;
        if (d?.PkgInstallerSha1 is { } sha)
        {
            if (ctx.Signing?.Find(sha) is null)
                return (PackagingAvailability.SetupRequired, "The chosen Developer ID Installer certificate is not in the keychain any more. Choose another one (or none for an unsigned test package). " + CertificatesHelp);
            if (d.SigningMode != MacSigningMode.DeveloperId)
                return (PackagingAvailability.SetupRequired, "A signed installer needs the app itself signed with Developer ID (Configure › Distribution › Signing). Otherwise build the package unsigned for local testing.");
            return (PackagingAvailability.Available, "Signed with " + ctx.Signing!.Find(sha)!.Name + ". Notarize it afterwards (explicit step) before distributing.");
        }
        var hasInstaller = ctx.Signing?.OfKind(AppleCertificateKind.DeveloperIdInstaller).Any() == true;
        return (PackagingAvailability.Available, dotNet
            ? "Unsigned: for local testing only (.NET apps are ad-hoc signed in this version, so a Developer ID Installer signature is not offered)."
            : hasInstaller
                ? "Unsigned: for local testing only. Choose your Developer ID Installer certificate in Configure › Distribution to sign it for distribution."
                : "Unsigned: for local testing only. Signed distribution needs a Developer ID Installer certificate (none found). " + CertificatesHelp);
    }

    /// <summary>Whether Developer ID signing of the app can be chosen (shown next to the signing-mode choice).</summary>
    public static (PackagingAvailability, string?) DeveloperId(PackagingContext ctx)
    {
        if (ctx.SwiftKind != SwiftAppKind.GuiApp)
            return (PackagingAvailability.NotApplicable, ctx.SwiftKind is null
                ? ".NET apps stay ad-hoc signed in this version: the hardened runtime needs .NET-specific exceptions HCG does not configure."
                : "Command-line tools are not signed for distribution in this version.");
        if (ctx.Host.Platform != TargetPlatform.MacOS) return (PackagingAvailability.RequiresDifferentHost, "Signing requires codesign on a Mac.");
        if (ctx.HasRootResourceBundles)
            return (PackagingAvailability.NotApplicable, "Incompatible with this project: its SwiftPM resource bundles must sit at the .app root (where Bundle.module looks), outside the signed Contents folder, and Gatekeeper rejects such an app after download. Load resources from Bundle.main or build with Xcode.");
        if (ctx.Signing?.Problem is { } p && ctx.Signing.Identities.Count == 0) return (PackagingAvailability.Failed, p);
        if (ctx.Signing?.OfKind(AppleCertificateKind.DeveloperIdApplication).Any() != true)
            return (PackagingAvailability.SetupRequired, "No Developer ID Application certificate was found in the keychain. It is created by the Account Holder of a paid Apple Developer Program team. " + CertificatesHelp);
        return (PackagingAvailability.Available, null);
    }

    public static (PackagingAvailability, string?) AppStore(PackagingContext ctx)
    {
        if (ctx.Host.Platform != TargetPlatform.MacOS) return (PackagingAvailability.RequiresDifferentHost, "Mac App Store packages are signed and built with codesign/productbuild on a Mac.");
        if (ctx.HasRootResourceBundles)
            return (PackagingAvailability.NotApplicable, "Incompatible with this project: SwiftPM resource bundles sit at the .app root (where Bundle.module looks), outside the signed Contents folder, which App Store validation rejects. Load resources from Bundle.main or build with Xcode.");
        var blockers = ctx.Evidence.Where(e => e.Kind == EntitlementEvidenceKind.SandboxBlocker).ToList();
        if (blockers.Count > 0)
            return (PackagingAvailability.NotApplicable, "Incompatible with the App Sandbox as written: " +
                string.Join(" ", blockers.Select(b => $"{b.Capability} ({b.Evidence}): {b.Explanation}")) + " HCG does not remove functionality; change the sources for a store build.");
        if (ctx.Signing?.Problem is { } problem && ctx.Signing.Identities.Count == 0) return (PackagingAvailability.Failed, problem);
        var setup = new List<string>();
        var signing = ctx.Signing ?? new MacSigningInventory();
        if (!signing.Identities.Any(i => i.SignsMacAppStoreApps))
            setup.Add("an Apple Distribution certificate");
        if (!signing.OfKind(AppleCertificateKind.MacInstallerDistribution).Any())
            setup.Add("a Mac Installer Distribution certificate (3rd Party Mac Developer Installer)");
        var matching = ctx.BundleId is null ? [] : signing.Profiles.Where(p => p.Kind == MacProfileKind.MacAppStore && p.Matches(ctx.BundleId) && !p.IsExpired(ctx.Now)).ToList();
        if (matching.Count == 0)
            setup.Add($"a Mac App Store provisioning profile for {ctx.BundleId ?? "the bundle identifier"} (developer.apple.com › Profiles › Mac App Store Connect; register the App ID first)");
        if (setup.Count > 0)
            return (PackagingAvailability.SetupRequired, "Missing: " + string.Join("; ", setup) + ". " + CertificatesHelp);
        var d = ctx.Distribution;
        if (d is null || d.AppStoreApplicationSha1 is null || d.AppStoreInstallerSha1 is null || d.AppStoreProfilePath is null)
            return (PackagingAvailability.SetupRequired, "Choose the Apple Distribution certificate, the installer certificate and the profile in Configure › Distribution.");
        if (!d.EntitlementsConfirmed)
            return (PackagingAvailability.SetupRequired, "Review and confirm the App Sandbox entitlements in Configure › Distribution.");
        var reviews = ctx.Evidence.Where(e => e.Kind == EntitlementEvidenceKind.Review).Select(e => e.Capability).ToList();
        return (PackagingAvailability.Available, reviews.Count == 0 ? null : "Ready; review before submitting: " + string.Join(", ", reviews) + ".");
    }
}
