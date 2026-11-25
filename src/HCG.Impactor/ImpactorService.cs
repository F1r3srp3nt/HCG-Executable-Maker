using HCG.Impactor.Devices;
using HCG.Impactor.Diagnostics;
using HCG.Impactor.Inspection;
using HCG.Impactor.Model;
using HCG.Impactor.Signing;
using HCG.Impactor.Storage;

namespace HCG.Impactor;

/// <summary>One item of the Deployment Queue: an IPA (signed, or signed now) for one device.</summary>
public sealed record DeploymentItem(string IpaPath, AppleDevice Device)
{
    public bool LaunchAfterInstall { get; init; }
    /// <summary>The user confirmed the warnings shown before installation (existing app, team change, wireless …).</summary>
    public bool WarningsAcknowledged { get; init; }
}

public sealed record DeploymentResult
{
    public required DeploymentItem Item { get; init; }
    public IpaReport? Inspection { get; init; }
    public IReadOnlyList<Finding> Checks { get; init; } = [];
    public InstalledApp? ExistingApp { get; init; }
    public InstallResult? Install { get; init; }
    public LaunchResult? Launch { get; init; }
    public InstallOutcome Outcome { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// HCG Impactor's workflow: Drop IPA → Inspect → Sign → Select device → Install, plus the records behind Signing
/// Monitor and Installation History. UI-independent: the Avalonia workspace and the command line use exactly this.
/// </summary>
public sealed class ImpactorService(IAppSigner signer, IDeviceService devices, ISigningInventorySource inventory, ImpactorStore store, TimeProvider time)
{
    public ImpactorStore Store => store;
    public ISigningInventorySource Inventory => inventory;
    public IDeviceService Devices => devices;
    public IAppSigner Signer => signer;
    public TimeProvider Time => time;

    public ImpactorCapabilities Capabilities => ImpactorCapabilities.Detect(signer.IsAvailable, devices.IsAvailable);

    public IpaReport Inspect(string path, CancellationToken ct = default) => IpaInspector.Inspect(path, new IpaInspectionOptions { Time = time }, ct);

    public Task<SigningInventorySnapshot> ReadInventoryAsync(IReadOnlyList<string>? extraProfiles, CancellationToken ct) => inventory.ReadAsync(extraProfiles, ct);

    public SigningPlan Plan(IpaReport ipa, SigningInventorySnapshot inv, SigningRequest request) => SigningPlanner.Plan(ipa, inv, request, time.GetUtcNow());

    /// <summary>The default output name: next to the source, "&lt;name&gt;-signed.ipa", never overwriting anything.</summary>
    public static string DefaultOutput(string sourceIpa, string? folder = null)
    {
        var dir = folder ?? Path.GetDirectoryName(Path.GetFullPath(sourceIpa))!;
        var stem = Path.GetFileNameWithoutExtension(sourceIpa);
        var candidate = Path.Combine(dir, stem + "-signed.ipa");
        for (var i = 2; File.Exists(candidate); i++) candidate = Path.Combine(dir, $"{stem}-signed-{i}.ipa");
        return candidate;
    }

    public async Task<SignResult> SignAsync(string sourceIpa, IpaReport inspection, SigningPlan plan, string? output, IProgress<string>? progress, CancellationToken ct)
    {
        var result = await signer.SignAsync(new SignJob(sourceIpa, inspection, plan, output ?? DefaultOutput(sourceIpa)), progress, ct).ConfigureAwait(false);
        if (result.Succeeded && plan.Main is { } main)
        {
            var app = result.Reinspection?.App ?? inspection.App!;
            store.Add(new SignedAppRecord
            {
                SignedAt = time.GetUtcNow(),
                AppName = app.Name ?? main.FinalBundleIdentifier ?? "App",
                BundleIdentifier = main.FinalBundleIdentifier ?? app.BundleIdentifier ?? "",
                Version = app.ShortVersion, Build = app.BuildNumber,
                SourceIpa = Path.GetFullPath(sourceIpa), SignedIpa = result.OutputIpa, SignedSha256 = result.OutputSha256,
                IdentityName = plan.Identity?.CommonName, IdentitySha1 = plan.Identity?.Sha1, IdentityExpires = plan.Identity?.NotAfter,
                ProfileName = main.Profile?.Info.Name, ProfileUuid = main.Profile?.Info.Uuid, ProfileExpires = main.Profile?.Info.Expiration,
                Method = plan.Method?.ToString(), TeamId = plan.Identity?.TeamId, ExpiresAt = plan.ExpiresAt,
            });
        }
        return result;
    }

    public Task<DeviceListing> ListDevicesAsync(CancellationToken ct) => devices.ListAsync(ct);

    /// <summary>Pre-installation validation (no changes to the device): compatibility, signing facts, and whether the app is already installed.</summary>
    public async Task<(IReadOnlyList<Finding> Checks, InstalledApp? Existing)> CheckAsync(IpaReport signedIpa, AppleDevice device, CancellationToken ct)
    {
        var checks = new List<Finding>(InstallReadiness.Evaluate(signedIpa, device, time.GetUtcNow()));
        InstalledApp? existing = null;
        if (signedIpa.App?.BundleIdentifier is { } id && devices.IsAvailable && device.Connection != DeviceConnection.Unavailable)
        {
            existing = await devices.FindInstalledAsync(device, id, ct).ConfigureAwait(false);
            if (existing is not null)
                checks.Add(Finding.Warn("install.existing-app", "Already installed",
                    $"{existing.Name ?? id} {existing.Version} ({existing.Build}) is on {device.Name}. Installing replaces the app and keeps its data when it is signed by the same team. If the installed copy belongs to another team, iOS refuses the update; HCG never removes apps or their data.",
                    "To keep both, sign with a new bundle identifier."));
        }
        return (checks, existing);
    }

    /// <summary>Installs one item: validate → install → (launch) → record. Blockers stop before the device is touched.</summary>
    public async Task<DeploymentResult> DeployAsync(DeploymentItem item, IProgress<InstallProgress>? progress, CancellationToken ct, SignResult? alreadyUnpacked = null)
    {
        var ipa = Inspect(item.IpaPath, ct);
        var (checks, existing) = await CheckAsync(ipa, item.Device, ct).ConfigureAwait(false);
        var app = ipa.App;
        InstallRecord Record(InstallOutcome outcome, InstallResult? install, string summary, bool launched = false) => new()
        {
            At = time.GetUtcNow(), AppName = app?.Name ?? Path.GetFileName(item.IpaPath), BundleIdentifier = app?.BundleIdentifier, Version = app?.ShortVersion,
            IpaPath = ipa.SourcePath, IpaSha256 = ipa.Sha256, DeviceName = item.Device.Name, DeviceModel = item.Device.DisplayModel, DeviceOs = item.Device.OsVersion,
            DeviceIdentifier = item.Device.Identifier, DeviceUdidRedacted = item.Device.Udid is null ? null : AppleDeviceContract.Redact(item.Device.Udid),
            Connection = item.Device.Connection.ToString(), Outcome = outcome, DiagnosisKind = install?.Failure?.Kind.ToString(), Summary = summary,
            Duration = install?.Duration ?? TimeSpan.Zero, Launched = launched,
        };

        if (checks.HasBlockers())
        {
            var summary = "Not installed: " + string.Join("; ", checks.Blockers().Select(b => b.Title));
            store.Add(Record(InstallOutcome.Blocked, null, summary));
            return new DeploymentResult { Item = item, Inspection = ipa, Checks = checks, ExistingApp = existing, Outcome = InstallOutcome.Blocked, Summary = summary };
        }
        if (checks.Warnings().Any() && !item.WarningsAcknowledged)
            return new DeploymentResult { Item = item, Inspection = ipa, Checks = checks, ExistingApp = existing, Outcome = InstallOutcome.Blocked, Summary = "Waiting for confirmation of the warnings." };

        var unpacked = alreadyUnpacked ?? await signer.UnpackAsync(item.IpaPath, ct).ConfigureAwait(false);
        try
        {
            if (!unpacked.Succeeded || unpacked.SignedAppDirectory is null)
            {
                var r = new InstallResult { Failure = unpacked.Failure };
                store.Add(Record(InstallOutcome.Failed, r, unpacked.Failure?.Title ?? "Could not unpack the IPA."));
                return new DeploymentResult { Item = item, Inspection = ipa, Checks = checks, Install = r, Outcome = InstallOutcome.Failed, Summary = unpacked.Failure?.Title ?? "Could not unpack the IPA." };
            }
            var install = await devices.InstallAsync(item.Device, unpacked.SignedAppDirectory, progress, ct).ConfigureAwait(false);
            LaunchResult? launch = null;
            if (install.Succeeded && item.LaunchAfterInstall && app?.BundleIdentifier is { } id)
                launch = await devices.LaunchAsync(item.Device, id, CancellationToken.None).ConfigureAwait(false);
            var outcome = install.Succeeded ? InstallOutcome.Installed : install.Failure?.Kind == DiagnosisKind.Cancelled ? InstallOutcome.Cancelled : InstallOutcome.Failed;
            var summary = install.Succeeded
                ? $"Installed {app?.Name} {app?.ShortVersion} on {item.Device.Name} in {install.Duration.TotalSeconds:0}s" + (launch is null ? "." : launch.Succeeded ? " and launched it." : $"; launch failed: {launch.Failure?.Title}.")
                : $"{install.Failure?.Title}: {install.Failure?.Explanation}";
            store.Add(Record(outcome, install, summary, launch?.Succeeded == true));
            return new DeploymentResult { Item = item, Inspection = ipa, Checks = checks, ExistingApp = existing, Install = install, Launch = launch, Outcome = outcome, Summary = summary };
        }
        finally
        {
            if (alreadyUnpacked is null) signer.Cleanup(unpacked);
        }
    }

    /// <summary>Batch installation: items run one after another; a failure does not stop the rest. Cancelling stops the queue.</summary>
    public async Task<IReadOnlyList<DeploymentResult>> DeployAllAsync(IReadOnlyList<DeploymentItem> items, Action<DeploymentItem, InstallProgress>? progress, CancellationToken ct)
    {
        var results = new List<DeploymentResult>();
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            results.Add(await DeployAsync(item, progress is null ? null : new Progress<InstallProgress>(p => progress(item, p)), ct).ConfigureAwait(false));
        }
        return results;
    }

    public async Task<LaunchResult> LaunchAsync(AppleDevice device, string bundleId, CancellationToken ct) => await devices.LaunchAsync(device, bundleId, ct).ConfigureAwait(false);

    public string ExportDiagnostics(ImpactorSession session, string folder, string appVersion, string installationId) =>
        ImpactorReport.Export(session with { Capabilities = session.Capabilities ?? Capabilities }, folder, appVersion, installationId, time.GetUtcNow());
}
