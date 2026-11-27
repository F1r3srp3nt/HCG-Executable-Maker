using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Signing;

namespace HCG.ExecutableMaker.BuildEngine.Signing;

/// <summary>
/// Runs the Apple Signing Assistant for a build: reads the project's effective build settings (xcodebuild
/// -showBuildSettings, so xcconfig files and Config/Local.xcconfig count), the project's signing profile in Signing
/// Vault and this Mac's identities and profiles, then decides the signing build settings for the destination.
/// </summary>
public sealed class AppleSigningAssistantService(AppleSigningAssetService? assets, IProjectSigningStore? store, TimeProvider time)
{
    public static readonly TimeSpan SettingsTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The xcodebuild arguments that print the effective settings for this destination and configuration.</summary>
    public static IReadOnlyList<string> SettingsArguments(string flag, string container, string scheme, string configuration, AppleBuildDestination destination) =>
        [flag, container, "-scheme", scheme, "-configuration", configuration, "-sdk", destination == AppleBuildDestination.IosSimulatorDebug ? "iphonesimulator" : "iphoneos", "-showBuildSettings", "-json"];

    public async Task<AppleSigningPlan> PlanAsync(ProjectRecord project, MobileBuildOptions options, string configuration, XcodeTargetSettings? app,
        string sourceDirectory, string containerRelativePath, CancellationToken ct)
    {
        var destination = AppleDestinations.Of(options);
        var saved = store is null ? null : await store.LoadAsync(project.Id.ToString(), ct).ConfigureAwait(false);
        SigningAssetInventory? inventory = null;
        if (destination.NeedsDeviceSigning() && options.Signing != MobileSigningMode.Unsigned && assets is not null)
        {
            try { inventory = await assets.ReadAsync(null, null, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { inventory = new SigningAssetInventory { KeychainProblem = ex.Message }; }
        }
        string? bundleVar = null;
        if (containerRelativePath.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase))
        {
            var pbx = Path.Combine(sourceDirectory, containerRelativePath.Replace('/', Path.DirectorySeparatorChar), "project.pbxproj");
            try { if (File.Exists(pbx) && new FileInfo(pbx).Length < 8 * 1024 * 1024) bundleVar = XcodeBuildSettings.BundleIdentifierVariable(await File.ReadAllTextAsync(pbx, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return AppleSigningAssistant.Plan(new AppleSigningInput
        {
            Options = options, Configuration = configuration, App = app, Saved = saved,
            Certificates = inventory?.Certificates ?? [], Profiles = inventory?.Profiles ?? [],
            Entitlements = Entitlements(app, sourceDirectory), BundleIdentifierVariable = bundleVar, Now = time.GetUtcNow(),
        });
    }

    /// <summary>Entitlement keys from the app target's CODE_SIGN_ENTITLEMENTS file (inside the workspace only).</summary>
    public static IReadOnlyCollection<string> Entitlements(XcodeTargetSettings? app, string sourceDirectory)
    {
        if (app?.EntitlementsFile is not { } rel) return [];
        var root = app.Get("PROJECT_DIR") ?? app.Get("SRCROOT") ?? sourceDirectory;
        var path = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel));
        if (!PathGuard.IsWithin(sourceDirectory, path) || !File.Exists(path)) return [];
        try { return PlistReader.ParseDictionary(File.ReadAllBytes(path)).Keys.ToList(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException) { return []; }
    }

    /// <summary>Adds a device the project was installed on to its intended devices (Signing Vault), so later builds check its registration.</summary>
    public async Task RememberDeviceAsync(ProjectRecord project, string udid, CancellationToken ct)
    {
        if (store is null || string.IsNullOrWhiteSpace(udid)) return;
        var saved = await store.LoadAsync(project.Id.ToString(), ct).ConfigureAwait(false)
                    ?? new ProjectSigningConfiguration { ProjectId = project.Id.ToString() };
        if (saved.IntendedDevices.Contains(udid, StringComparer.OrdinalIgnoreCase)) return;
        await store.SaveAsync(saved with { IntendedDevices = [.. saved.IntendedDevices, udid], UpdatedAt = time.GetUtcNow() }, ct).ConfigureAwait(false);
    }

    /// <summary>Remembers a team chosen in Resolve Signing in the project's signing profile (Signing Vault), so later builds reuse it.</summary>
    public async Task RememberTeamAsync(ProjectRecord project, string team, CancellationToken ct)
    {
        if (store is null) return;
        var saved = await store.LoadAsync(project.Id.ToString(), ct).ConfigureAwait(false)
                    ?? new ProjectSigningConfiguration { ProjectId = project.Id.ToString() };
        await store.SaveAsync(saved with { TeamId = team, UpdatedAt = time.GetUtcNow() }, ct).ConfigureAwait(false);
    }
}
