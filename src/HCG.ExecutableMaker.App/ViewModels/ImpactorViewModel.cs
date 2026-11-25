using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Signing;
using HCG.Impactor;
using HCG.Impactor.Devices;
using HCG.Impactor.Diagnostics;
using HCG.Impactor.Ecosystem;
using HCG.Impactor.Model;
using HCG.Impactor.Signing;
using HCG.Impactor.Storage;

namespace HCG.ExecutableMaker.App.ViewModels;

public enum ImpactorSection { Dashboard, Inspector, Signing, Doctor, Devices, Queue, Certificates, Profiles, Monitor, History, Diagnostics }

/// <summary>Navigation parameter: open the Signing Doctor for an app (from Mobile Build Studio).</summary>
public sealed record ImpactorDoctorRequest(string BundleId, string? EntitlementsPath);
public sealed record ImpDoctorCheck(string Title, string Detail, StatusTag Tag);
public sealed record ImpDoctorProfile(string Name, string Detail, StatusTag Tag, IReadOnlyList<ImpDoctorCheck> Checks);

public sealed record ImpactorSectionItem(ImpactorSection Section, string Title, string Icon);
public sealed record ImpFindingItem(string Title, string Detail, string? Remedy, string? Subject, StatusTag Tag)
{
    public bool HasRemedy => Remedy is not null;
    public static ImpFindingItem From(Finding f) => new(f.Title, f.Detail, f.Remedy is null ? null : "→ " + f.Remedy, f.Subject,
        f.Severity switch { FindingSeverity.Blocker => new("Blocker", "error"), FindingSeverity.Warning => new("Warning", "warning"), _ => new("Info", "info") });
}
public sealed record ImpKeyValueItem(string Key, string Value);
public sealed record ImpStepChip(string Title, string Tone)
{
    public StatusTag Tag => new(Title, Tone);
}
public sealed record ImpBundleItem(string Kind, string Path, string Identifier, string Detail, string Signature);
public sealed record ImpAssignmentItem(string Bundle, string Identifier, string Profile, string Entitlements);
public sealed record ImpIdentityItem(string Sha1, string Name, string Team, string Kind, string Expires, StatusTag Tag, AppleCertificateInfo Info)
{
    public override string ToString() => Sha1.Length == 0 ? Name : $"{Name} · expires {Expires}";
}
public sealed record ImpProfileItem(string Uuid, string Name, string Kind, string AppId, string Expires, string Devices, string Certificates, string Fit, StatusTag Tag, ProfileEntry Entry)
{
    public override string ToString() => Uuid.Length == 0 ? Name : $"{Name} · {Kind} · {AppId}";
}
public sealed record ImpHistoryItem(string When, string App, string Device, string Summary, StatusTag Tag);
public sealed record ImpSignedAppItem(Guid Id, string App, string Detail, string Expires, StatusTag Tag, SignedAppRecord Record);

public sealed partial class ImpDeviceItem(AppleDevice device, IReadOnlyList<string> guidance) : ObservableObject
{
    public AppleDevice Device { get; } = device;
    public string Name => Device.Name;
    public string Model => $"{Device.DisplayModel} · {Device.Platform} {Device.OsVersion} ({Device.OsBuild}) · {Device.CpuArchitecture}";
    public string Connection => Device.Connection switch { DeviceConnection.Usb => "USB", DeviceConnection.Network => "Wi-Fi (network)", _ => "Not connected" };
    public string Identity => $"UDID {(Device.Udid is null ? "not reported" : AppleDeviceContract.Redact(Device.Udid))} · id {Device.Identifier}";
    public StatusTag ConnectionTag => Device.Connection switch { DeviceConnection.Usb => new("USB", "success"), DeviceConnection.Network => new("Wi-Fi", "info"), _ => new("Offline", "warning") };
    public StatusTag PairingTag => Device.Pairing switch { PairingState.Paired => new("Paired", "success"), PairingState.Unpaired => new("Not paired", "error"), _ => new("Pairing unknown", "info") };
    public StatusTag DeveloperModeTag => Device.DeveloperMode switch { DeveloperModeState.Enabled => new("Developer Mode on", "success"), DeveloperModeState.Disabled => new("Developer Mode off", "error"), _ => new("Developer Mode ?", "info") };
    public StatusTag InstallTag => Device.CanInstall ? new("Can install", "success") : new("Install unavailable", "warning");
    public string Capabilities => Device.Capabilities.Count == 0 ? "No CoreDevice capabilities reported." : "Capabilities: " + string.Join(", ", Device.Capabilities.Select(c => c.Replace("com.apple.coredevice.feature.", "")));
    public string Guidance => string.Join("\n", guidance);
    public bool HasGuidance => guidance.Count > 0;
    public string Registration { get; set; } = "";
    [ObservableProperty] private bool _selected;
}

public sealed partial class ImpQueueItem(ImpDeviceItem device) : ObservableObject
{
    public ImpDeviceItem Device { get; } = device;
    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private StatusTag _tag = new("Queued", "info");
    [ObservableProperty] private string _detail = "";
}

/// <summary>
/// HCG Impactor workspace: Dashboard, IPA Inspector, Signing Studio, Device Manager, Deployment Queue, Certificate
/// Manager, Provisioning Manager, Signing Monitor, Installation History and Diagnostics over <see cref="ImpactorService"/>.
/// The main flow is Drop IPA → Inspect → Sign → Select device → Install; Advanced options are separate.
/// </summary>
public sealed partial class ImpactorViewModel : PageViewModel
{
    private readonly ImpactorService _service;
    private readonly IPlatformShell _shell;
    private readonly IDialogService _dialogs;
    private readonly AppleSigningAssetService _assets;
    private readonly ProvisioningProfileStore _profileStore;
    private readonly IAppPaths _paths;
    private SigningInventorySnapshot? _inventory;
    private SignResult? _signed;
    private CancellationTokenSource? _cts;
    private ImpactorSession _session = new();
    private readonly HCG.Impactor.MacOS.XcodeProvisioningAssistant? _provisioning;

    public ImpactorViewModel(ImpactorService service, IPlatformShell shell, IDialogService dialogs, AppleSigningAssetService assets, ProvisioningProfileStore profileStore, IAppPaths paths,
        HCG.Impactor.MacOS.XcodeProvisioningAssistant? provisioning = null)
    {
        _service = service;
        _shell = shell;
        _dialogs = dialogs;
        _assets = assets;
        _profileStore = profileStore;
        _paths = paths;
        _provisioning = provisioning;
        _selectedSectionItem = Sections[0];
        foreach (var (product, integration, status) in HcgLinks.Integrations) Integrations.Add(new ImpKeyValueItem($"{product} — {status}", integration));
    }

    public override string Title => "HCG Impactor";
    public override string Subtitle => "Inspect, sign and install iOS and iPadOS apps on your own devices";

    public IReadOnlyList<ImpactorSectionItem> Sections { get; } =
    [
        new(ImpactorSection.Dashboard, "Dashboard", "Icon.Dashboard"),
        new(ImpactorSection.Inspector, "IPA Inspector", "Icon.Zip"),
        new(ImpactorSection.Signing, "Signing Studio", "Icon.Security"),
        new(ImpactorSection.Doctor, "Signing Doctor", "Icon.Support"),
        new(ImpactorSection.Devices, "Device Manager", "Icon.Mobile"),
        new(ImpactorSection.Queue, "Deployment Queue", "Icon.Play"),
        new(ImpactorSection.Certificates, "Certificates", "Icon.Check"),
        new(ImpactorSection.Profiles, "Provisioning", "Icon.Log"),
        new(ImpactorSection.Monitor, "Signing Monitor", "Icon.History"),
        new(ImpactorSection.History, "Installation History", "Icon.Requests"),
        new(ImpactorSection.Diagnostics, "Diagnostics", "Icon.Info"),
    ];

    [ObservableProperty] private ImpactorSectionItem _selectedSectionItem;
    partial void OnSelectedSectionItemChanged(ImpactorSectionItem value)
    {
        OnPropertyChanged(nameof(Section));
        foreach (var n in new[] { nameof(ShowDashboard), nameof(ShowInspector), nameof(ShowSigning), nameof(ShowDoctor), nameof(ShowDevices), nameof(ShowQueue), nameof(ShowCertificates), nameof(ShowProfiles), nameof(ShowMonitor), nameof(ShowHistory), nameof(ShowDiagnostics) })
            OnPropertyChanged(n);
    }
    public ImpactorSection Section => SelectedSectionItem.Section;
    public bool ShowDashboard => Section == ImpactorSection.Dashboard;
    public bool ShowInspector => Section == ImpactorSection.Inspector;
    public bool ShowSigning => Section == ImpactorSection.Signing;
    public bool ShowDoctor => Section == ImpactorSection.Doctor;
    public bool ShowDevices => Section == ImpactorSection.Devices;
    public bool ShowQueue => Section == ImpactorSection.Queue;
    public bool ShowCertificates => Section == ImpactorSection.Certificates;
    public bool ShowProfiles => Section == ImpactorSection.Profiles;
    public bool ShowMonitor => Section == ImpactorSection.Monitor;
    public bool ShowHistory => Section == ImpactorSection.History;
    public bool ShowDiagnostics => Section == ImpactorSection.Diagnostics;

    /// <summary>Drop IPA → Inspect → Sign → Select device → Install, with the reached steps marked.</summary>
    public IReadOnlyList<ImpStepChip> FlowSteps =>
    [
        new("1  Drop IPA", FlowStep >= 1 ? "success" : "info"),
        new("2  Inspect", FlowStep >= 1 ? "success" : "info"),
        new("3  Sign", FlowStep >= 2 ? "success" : FlowStep == 1 ? "warning" : "info"),
        new("4  Select device", FlowStep >= 3 ? "success" : FlowStep == 2 ? "warning" : "info"),
        new("5  Install", FlowStep >= 4 ? "success" : FlowStep == 3 ? "warning" : "info"),
    ];
    partial void OnFlowStepChanged(int value) => OnPropertyChanged(nameof(FlowSteps));
    [RelayCommand] private void Go(string section) => SelectedSectionItem = Sections.First(s => s.Section.ToString() == section);

    // ------------------------------------------------------------------ dashboard

    public ImpactorCapabilities Capabilities => _service.Capabilities;
    public string PlatformText => $"{Capabilities.Platform} · inspect {(Capabilities.CanInspect ? "✓" : "✗")} · sign {(Capabilities.CanSign ? "✓" : "✗")} · devices {(Capabilities.CanListDevices ? "✓" : "✗")} · install {(Capabilities.CanInstall ? "✓" : "✗")}";
    public string? PlatformLimitation => Capabilities.Limitation;
    public bool HasPlatformLimitation => Capabilities.Limitation is not null;
    public ObservableCollection<ImpKeyValueItem> Integrations { get; } = [];
    public ObservableCollection<ImpFindingItem> Reminders { get; } = [];
    [ObservableProperty] private string _statsDevices = "—";
    [ObservableProperty] private string _statsIdentities = "—";
    [ObservableProperty] private string _statsProfiles = "—";
    [ObservableProperty] private string _statsApps = "—";
    [ObservableProperty] private int _flowStep;
    [ObservableProperty] private string _status = "Drop an IPA to begin.";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is string path && File.Exists(path)) await LoadIpaAsync(path);
        if (parameter is ImpactorDoctorRequest doctor)
        {
            DoctorBundleId = doctor.BundleId;
            DoctorEntitlementsPath = doctor.EntitlementsPath;
            SelectedSectionItem = Sections.First(s => s.Section == ImpactorSection.Doctor);
        }
        RefreshRecords();
        await RefreshInventoryAsync();
        if (Capabilities.CanListDevices) await RefreshDevicesAsync();
        if (parameter is ImpactorDoctorRequest) await RunDoctorAsync();
    }

    // ------------------------------------------------------------------ inspector

    [ObservableProperty] private string? _ipaPath;
    [ObservableProperty] private bool _hasIpa;
    [ObservableProperty] private string _ipaTitle = "No IPA loaded";
    [ObservableProperty] private string _ipaSubtitle = "Drop an .ipa here, or browse. The original file is never changed.";
    public ObservableCollection<ImpKeyValueItem> IpaFacts { get; } = [];
    public ObservableCollection<ImpBundleItem> Bundles { get; } = [];
    public ObservableCollection<ImpFindingItem> IpaFindings { get; } = [];
    public ObservableCollection<ImpKeyValueItem> Entitlements { get; } = [];
    public IpaReport? Inspection { get; private set; }

    [RelayCommand]
    private async Task BrowseIpaAsync()
    {
        var file = await _shell.PickFileAsync("Choose an iOS app (.ipa)", "iOS app", "*.ipa");
        if (file is not null) await LoadIpaAsync(file);
    }

    /// <summary>Called by the view's drop zone and by navigation with a path.</summary>
    public async Task LoadIpaAsync(string path)
    {
        IsBusy = true;
        Status = $"Inspecting {Path.GetFileName(path)} …";
        try
        {
            var report = await Task.Run(() => _service.Inspect(path));
            Inspection = report;
            _signed = null;
            _session = new ImpactorSession { Inspection = report, Capabilities = Capabilities };
            IpaPath = report.SourcePath;
            HasIpa = report.App is not null;
            ShowInspection(report);
            FlowStep = report.App is null ? 0 : 1;
            Status = report.Findings.HasBlockers() ? "Inspection found blockers — see IPA Inspector." : $"Inspected {report.App?.Name}. Next: Signing Studio.";
            SelectedSectionItem = Sections.First(s => s.Section == ImpactorSection.Inspector);
            SignedIpaPath = report.App?.Signature is { Present: true, AdHoc: false } ? report.SourcePath : null;
            BuildPlan();
            UpdateProfileFit();
        }
        catch (Exception ex) { ErrorMessage = "Could not inspect the file: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void ShowInspection(IpaReport r)
    {
        IpaFacts.Clear(); Bundles.Clear(); IpaFindings.Clear(); Entitlements.Clear();
        var a = r.App;
        IpaTitle = a is null ? Path.GetFileName(r.SourcePath) : $"{a.Name} {a.ShortVersion}";
        IpaSubtitle = a is null ? "Not a valid IPA — see findings." : $"{a.BundleIdentifier} · build {a.BuildNumber} · {Format.Bytes(r.SizeBytes)}";
        if (a is not null)
        {
            void F(string k, string? v) => IpaFacts.Add(new ImpKeyValueItem(k, string.IsNullOrEmpty(v) ? "—" : v));
            F("Application", a.Name);
            F("Bundle identifier", a.BundleIdentifier);
            F("Version (build)", $"{a.ShortVersion} ({a.BuildNumber})");
            F("Minimum iOS", a.MinimumOSVersion);
            F("Devices", string.Join(", ", Enum.GetValues<DeviceFamilies>().Where(f => f != DeviceFamilies.None && r.Families.HasFlag(f)).Select(f => f switch { DeviceFamilies.IPhone => "iPhone", DeviceFamilies.IPad => "iPad", _ => f.ToString() })));
            F("Architectures", string.Join(", ", a.Slices.Select(s => s.Architecture + (s.Encrypted ? " (encrypted)" : ""))));
            F("Required capabilities", string.Join(", ", r.RequiredCapabilities));
            F("Signature", Sig(a.Signature));
            F("Signed on", a.Signature.SigningTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            F("Certificate expires", a.Signature.Signer?.NotAfter.ToLocalTime().ToString("yyyy-MM-dd"));
            F("Certificate chain", string.Join(" ← ", a.Signature.Chain));
            F("Embedded profile", a.EmbeddedProfile?.Profile is { } p ? $"{p.Name} · {p.Kind} · expires {p.Expiration:yyyy-MM-dd} · {p.ProvisionedDevices.Count} device(s) · {p.Signature}" : a.EmbeddedProfile?.Problem ?? "none");
            F("Package integrity", (a.Resources.Present ? $"{a.Resources.Verified}/{a.Resources.Sealed} sealed files verified" + (a.Resources.Intact ? "" : $", {a.Resources.Modified.Count} modified, {a.Resources.Missing.Count} missing") : "no sealed resources (unsigned)") + $" · {r.FileCount:N0} files");
            F("SHA-256", r.Sha256);
            foreach (var (k, v) in a.Signature.Entitlements) Entitlements.Add(new ImpKeyValueItem(k, v));
        }
        foreach (var b in r.Nested)
            Bundles.Add(new ImpBundleItem(b.Kind.ToString(), b.RelativePath, b.BundleIdentifier ?? "—",
                $"{string.Join(", ", b.Architectures)}{(b.MinimumOSVersion is null ? "" : $" · iOS {b.MinimumOSVersion}+")}{(b.ExtensionPoint is null ? "" : $" · {b.ExtensionPoint}")}", Sig(b.Signature)));
        foreach (var f in r.Findings.OrderByDescending(f => f.Severity)) IpaFindings.Add(ImpFindingItem.From(f));
    }

    private static string Sig(CodeSignatureInfo s) => !s.Present ? "Unsigned" : s.AdHoc ? "Ad hoc (no certificate)" : $"{s.Signer?.CommonName ?? "certificate"} · team {(s.TeamId is { Length: > 0 } t ? t : s.Signer?.TeamId is { Length: > 0 } c ? c + " (certificate; none in the signature)" : "none recorded")}";

    // ------------------------------------------------------------------ inventory: certificates & profiles

    public ObservableCollection<ImpIdentityItem> Identities { get; } = [];
    public ObservableCollection<ImpProfileItem> Profiles { get; } = [];
    [ObservableProperty] private string? _inventoryNote;

    [RelayCommand]
    private async Task RefreshInventoryAsync()
    {
        try
        {
            _inventory = await _service.ReadInventoryAsync(ExtraProfiles, CancellationToken.None);
            var now = _service.Time.GetUtcNow();
            Identities.Clear();
            foreach (var c in _inventory.Certificates.Where(c => c.Kind is AppleCertificateKind.Development or AppleCertificateKind.AppleDistribution))
            {
                var ready = c.Readiness(now);
                var days = (int)(c.NotAfter - now).TotalDays;
                Identities.Add(new ImpIdentityItem(c.Sha1, c.CommonName, c.TeamId ?? "—", c.Kind == AppleCertificateKind.Development ? "Apple Development" : "Apple Distribution",
                    $"{c.NotAfter.ToLocalTime():yyyy-MM-dd} ({days} days)",
                    ready switch
                    {
                        SigningReadiness.Ready when days < 30 => new("Expires soon", "warning"),
                        SigningReadiness.Ready => new("Ready", "success"),
                        SigningReadiness.PrivateKeyMissing => new("No private key", "error"),
                        SigningReadiness.Expired => new("Expired", "error"),
                        SigningReadiness.TrustProblem => new("Trust problem", "warning"),
                        var other => new(other.ToString(), "warning"),
                    }, c));
            }
            Profiles.Clear();
            foreach (var p in _inventory.Profiles.OrderBy(p => p.Info.Expiration)) Profiles.Add(ProfileRow(p, now));
            InventoryNote = _service.Inventory.LastProblem;
            StatsIdentities = Identities.Count(i => i.Tag.Tone != "error").ToString();
            StatsProfiles = Profiles.Count(p => p.Tag.Tone != "error").ToString();
            IdentityChoices.Clear();
            IdentityChoices.Add(AutomaticIdentity);
            foreach (var i in Identities.Where(i => i.Tag.Tone != "error")) IdentityChoices.Add(i);
            SelectedIdentity ??= AutomaticIdentity;
            UpdateProfileFit();
            BuildPlan();
        }
        catch (Exception ex) { InventoryNote = "Could not read signing assets: " + ex.Message; }
    }

    private ImpProfileItem ProfileRow(ProfileEntry p, DateTimeOffset now)
    {
        var expired = p.Info.Expiration <= now;
        var soon = !expired && p.Info.Expiration - now < TimeSpan.FromDays(14);
        var fit = Inspection?.App?.BundleIdentifier is { } id ? (SigningPlanner.AppIdCovers(p.Info.ApplicationIdentifier, p.Team, id) ? "Covers this app" : "Other App ID") : "";
        return new ImpProfileItem(p.Info.Uuid, p.Info.Name, p.Info.Kind.ToString(), p.Info.ApplicationIdentifier ?? "—", $"{p.Info.Expiration?.ToLocalTime():yyyy-MM-dd}",
            p.Info.ProvisionsAllDevices ? "All devices" : $"{p.Info.ProvisionedDevices.Count} device(s)", $"{p.Info.CertificateSha1s.Count} certificate(s)", fit,
            expired ? new("Expired", "error") : soon ? new("Expires soon", "warning") : p.Info.Signature == ProfileSignatureStatus.VerifiedApple ? new("Valid", "success") : new("Not verified as Apple's", "warning"), p);
    }

    private void UpdateProfileFit()
    {
        if (_inventory is null) return;
        var now = _service.Time.GetUtcNow();
        Profiles.Clear();
        foreach (var p in _inventory.Profiles.OrderByDescending(p => Inspection?.App?.BundleIdentifier is { } id && SigningPlanner.AppIdCovers(p.Info.ApplicationIdentifier, p.Team, id)).ThenBy(p => p.Info.Expiration))
            Profiles.Add(ProfileRow(p, now));
        ProfileChoices.Clear();
        ProfileChoices.Add(AutomaticProfile);
        foreach (var p in Profiles.Where(p => p.Fit == "Covers this app" && p.Tag.Tone != "error")) ProfileChoices.Add(p);
        SelectedProfile ??= AutomaticProfile;
    }

    private List<string> ExtraProfiles { get; } = [];

    [RelayCommand]
    private async Task AddProfileFileAsync()
    {
        var file = await _shell.PickFileAsync("Choose a provisioning profile", "Provisioning profile", "*.mobileprovision");
        if (file is null) return;
        var import = await _dialogs.ConfirmAsync("Install the profile?",
            "Install it into Xcode's profile folder (verified, never overwrites) so Xcode and HCG can both use it? Choose Cancel to use it for this session only.", "Install");
        if (import)
        {
            var r = _profileStore.Import(file, _service.Time.GetUtcNow());
            InventoryNote = r.Message;
        }
        else ExtraProfiles.Add(file);
        await RefreshInventoryAsync();
    }

    // ------------------------------------------------------------------ signing studio

    public static readonly ImpIdentityItem AutomaticIdentity = new("", "Automatic — the best identity for the installed profiles", "", "", "", new("Auto", "info"), new AppleCertificateInfo { Sha1 = "", Sha256 = "", CommonName = "" });
    public static readonly ImpProfileItem AutomaticProfile = new("", "Automatic — the best matching profile for each bundle", "", "", "", "", "", "", new("Auto", "info"), null!);
    public ObservableCollection<ImpIdentityItem> IdentityChoices { get; } = [];
    public ObservableCollection<ImpProfileItem> ProfileChoices { get; } = [];
    public IReadOnlyList<string> Methods { get; } = ["Automatic", "Development", "Ad Hoc", "Enterprise", "App Store (export only)"];
    [ObservableProperty] private ImpIdentityItem? _selectedIdentity;
    [ObservableProperty] private ImpProfileItem? _selectedProfile;
    [ObservableProperty] private string _selectedMethod = "Automatic";
    [ObservableProperty] private string? _newBundleId;
    [ObservableProperty] private bool _dropUnsupported;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private string? _outputPath;
    [ObservableProperty] private bool _planReady;
    [ObservableProperty] private string _planSummary = "Load an IPA to see the signing plan.";
    public ObservableCollection<ImpAssignmentItem> Assignments { get; } = [];
    public ObservableCollection<ImpFindingItem> PlanFindings { get; } = [];
    public ObservableCollection<string> SignLog { get; } = [];
    [ObservableProperty] private string? _signResult;
    [ObservableProperty] private string? _signedIpaPath;
    public SigningPlan? Plan { get; private set; }

    partial void OnSelectedIdentityChanged(ImpIdentityItem? value) => BuildPlan();
    partial void OnSelectedProfileChanged(ImpProfileItem? value) => BuildPlan();
    partial void OnSelectedMethodChanged(string value) => BuildPlan();
    partial void OnNewBundleIdChanged(string? value) => BuildPlan();
    partial void OnDropUnsupportedChanged(bool value) => BuildPlan();
    partial void OnTargetDeviceChanged(ImpDeviceItem? value) => BuildPlan();

    [RelayCommand]
    private void BuildPlan()
    {
        Assignments.Clear();
        PlanFindings.Clear();
        if (Inspection?.App is null || _inventory is null) { PlanReady = false; return; }
        Plan = _service.Plan(Inspection, _inventory, new SigningRequest
        {
            IdentitySha1 = string.IsNullOrEmpty(SelectedIdentity?.Sha1) ? null : SelectedIdentity!.Sha1,
            ProfileUuid = string.IsNullOrEmpty(SelectedProfile?.Uuid) ? null : SelectedProfile!.Uuid,
            Method = SelectedMethod switch
            {
                "Development" => DistributionMethod.Development, "Ad Hoc" => DistributionMethod.AdHoc,
                "Enterprise" => DistributionMethod.Enterprise, "App Store (export only)" => DistributionMethod.AppStore, _ => null,
            },
            NewBundleIdentifier = string.IsNullOrWhiteSpace(NewBundleId) ? null : NewBundleId,
            DropUnsupportedEntitlements = DropUnsupported,
            TargetDevice = TargetDevice?.Device,
        });
        foreach (var a in Plan.Assignments)
            Assignments.Add(new ImpAssignmentItem(a.Label, a.FinalBundleIdentifier ?? "—", a.Profile is null ? $"No entitlements ({a.Bundle.Kind})" : $"{a.Profile.Info.Name} ({a.Profile.Info.Kind})",
                a.Entitlements is null ? "" : string.Join(", ", a.Entitlements.Keys)));
        foreach (var f in Plan.Findings.OrderByDescending(f => f.Severity)) PlanFindings.Add(ImpFindingItem.From(f));
        PlanReady = Plan.IsReady && Capabilities.CanSign;
        PlanSummary = Plan.Identity is null ? "No signing identity can sign this app yet — see below."
            : $"{Plan.Identity.CommonName} · {Plan.Method}{(Plan.Installable ? "" : " (not installable)")} · valid until {Plan.ExpiresAt?.ToLocalTime():yyyy-MM-dd}" + (Capabilities.CanSign ? "" : " · signing needs macOS");
        _session = _session with { Plan = Plan };
    }

    [RelayCommand]
    private async Task ChooseOutputAsync()
    {
        var folder = await _shell.PickFolderAsync("Where should the signed IPA go?");
        if (folder is not null && IpaPath is not null) OutputPath = ImpactorService.DefaultOutput(IpaPath, folder);
    }

    [RelayCommand]
    private async Task SignAsync()
    {
        if (Plan is null || Inspection is null || IpaPath is null || !Plan.IsReady) return;
        if (Plan.Findings.Warnings().Any() && !await _dialogs.ConfirmAsync("Sign with these warnings?", string.Join("\n\n", Plan.Findings.Warnings().Select(w => $"{w.Title}: {w.Detail}")), "Sign"))
            return;
        IsBusy = true;
        SignLog.Clear();
        SignResult = null;
        _cts = new CancellationTokenSource();
        try
        {
            var result = await _service.SignAsync(IpaPath, Inspection, Plan, string.IsNullOrWhiteSpace(OutputPath) ? null : OutputPath,
                new Progress<string>(l => SignLog.Add(l)), _cts.Token);
            if (_signed is not null) _service.Signer.Cleanup(_signed);
            _signed = result.Succeeded ? result : null;
            _session = _session with { Signing = result };
            if (result.Succeeded)
            {
                SignedIpaPath = result.OutputIpa;
                SignResult = $"Signed: {result.OutputIpa}\nSHA-256 {result.OutputSha256}\ncodesign --verify: {result.Verification?.Split('\n').LastOrDefault()}";
                FlowStep = 2;
                Status = "Signed and verified. Next: choose a device.";
                RefreshRecords();
            }
            else ShowDiagnosis(result.Failure, "Signing");
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ signing doctor

    [ObservableProperty] private string? _doctorBundleId;
    [ObservableProperty] private string? _doctorEntitlementsPath;
    [ObservableProperty] private string? _doctorTeam;
    [ObservableProperty] private bool _doctorUseDevice = true;
    [ObservableProperty] private string _doctorSummary = "Enter the app's bundle identifier and run the Signing Doctor.";
    [ObservableProperty] private bool _doctorHasResult;
    [ObservableProperty] private bool _canCreateProfile;
    /// <summary>Off by default: registering a device in the team is its own, explicit choice.</summary>
    [ObservableProperty] private bool _provisionRegisterDevice;
    [ObservableProperty] private string? _provisionResult;
    public ObservableCollection<ImpDoctorProfile> DoctorProfiles { get; } = [];
    public ObservableCollection<string> DoctorSteps { get; } = [];
    public ObservableCollection<ImpFindingItem> DoctorFindings { get; } = [];
    public ObservableCollection<string> ProvisionLog { get; } = [];
    public ObservableCollection<string> DoctorTeams { get; } = [];
    private SigningDoctorReport? _doctor;

    [RelayCommand]
    private async Task BrowseEntitlementsAsync()
    {
        var file = await _shell.PickFileAsync("Choose the app's .entitlements file", "Entitlements", "*.entitlements", "*.plist");
        if (file is not null) DoctorEntitlementsPath = file;
    }

    [RelayCommand]
    private async Task RunDoctorAsync()
    {
        var id = string.IsNullOrWhiteSpace(DoctorBundleId) ? Inspection?.App?.BundleIdentifier : DoctorBundleId!.Trim();
        if (id is null) { DoctorSummary = "Enter a bundle identifier (or load an IPA)."; return; }
        DoctorBundleId = id;
        IsBusy = true;
        try
        {
            IReadOnlyDictionary<string, object> ent = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(DoctorEntitlementsPath) && File.Exists(DoctorEntitlementsPath)) ent = ImpactorService.ReadEntitlements(DoctorEntitlementsPath);
            else if (Inspection?.App is { } app && app.BundleIdentifier == id) ent = app.Signature.RawEntitlements;
            var device = DoctorUseDevice ? (TargetDevice ?? Devices.FirstOrDefault(d => d.Device.Connection == DeviceConnection.Usb))?.Device : null;
            _doctor = await _service.DoctorAsync(new SigningDoctorRequest
            {
                BundleIdentifier = id, Device = device, Entitlements = ent, TeamId = string.IsNullOrWhiteSpace(DoctorTeam) ? null : DoctorTeam.Trim(),
            }, ExtraProfiles, CancellationToken.None);
            DoctorSummary = _doctor.Summary;
            DoctorProfiles.Clear();
            foreach (var v in _doctor.Profiles)
                DoctorProfiles.Add(new ImpDoctorProfile(v.Name, $"{v.Profile.Info.Kind} · {v.Profile.Info.ApplicationIdentifier} · expires {v.Profile.Info.Expiration:yyyy-MM-dd} · {v.Profile.Info.Uuid}",
                    v.Usable ? new("Usable", "success") : new($"{v.Failures.Count()} problem(s)", "error"),
                    v.Checks.Select(c => new ImpDoctorCheck(c.Title, c.Detail, c.Passed ? new("✓", "success") : new("✗", "error"))).ToList()));
            DoctorSteps.Clear();
            foreach (var step in _doctor.NextSteps) DoctorSteps.Add(step);
            DoctorFindings.Clear();
            foreach (var f in _doctor.Findings) DoctorFindings.Add(ImpFindingItem.From(f));
            DoctorTeams.Clear();
            foreach (var t in _doctor.Identities.Select(c => c.TeamId).OfType<string>().Distinct()) DoctorTeams.Add(t);
            DoctorTeam ??= _doctor.SuggestedTeam;
            DoctorHasResult = true;
            CanCreateProfile = !_doctor.HasUsableProfile && _provisioning is { IsAvailable: true } && DoctorTeams.Count > 0;
        }
        catch (Exception ex) { DoctorSummary = "The Signing Doctor could not run: " + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Creates the App ID and a Development profile with Xcode's automatic signing (the Xcode account), after explicit approval.</summary>
    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        if (_provisioning is null || DoctorBundleId is null || string.IsNullOrWhiteSpace(DoctorTeam)) { ProvisionResult = "Choose the team first."; return; }
        IReadOnlyDictionary<string, object> ent = !string.IsNullOrWhiteSpace(DoctorEntitlementsPath) && File.Exists(DoctorEntitlementsPath)
            ? ImpactorService.ReadEntitlements(DoctorEntitlementsPath) : new Dictionary<string, object>();
        var req = new HCG.Impactor.MacOS.ProvisioningRequest { BundleIdentifier = DoctorBundleId, TeamId = DoctorTeam.Trim(), Entitlements = ent, MinimumIos = Inspection?.App?.MinimumOSVersion ?? "16.0", RegisterConnectedDevices = ProvisionRegisterDevice };
        var ok = await _dialogs.ConfirmAsync("Create a profile with your Xcode account?",
            $"HCG will build a throwaway stub app with bundle ID {req.BundleIdentifier} for team {req.TeamId} using Xcode's automatic signing:\n\n" +
            string.Join(' ', HCG.Impactor.MacOS.XcodeProvisioningAssistant.Arguments("<stub folder>", req)) +
            "\n\nXcode uses the Apple Account signed in to Xcode › Settings › Accounts. Approving this allows Xcode to:\n" +
            $"• register the App ID {req.TeamId}.{req.BundleIdentifier} if your team does not have it yet, and enable the capabilities for its entitlements;\n" +
            (req.RegisterConnectedDevices ? "• register the connected iPhone/iPad in your team (you ticked \"Register the connected device\");\n" : "• NOT register any device (\"Register the connected device\" is off; a device that is not registered yet stays unregistered and the profile will not include it);\n") +
            "• download an iOS Team Provisioning Profile.\n\n" +
            "This changes your Apple Developer account. Nothing is revoked, no installed app is touched, and the stub is never installed.", "Create profile");
        if (!ok) return;
        IsBusy = true;
        ProvisionLog.Clear();
        try
        {
            var r = await _provisioning.CreateAsync(req, new Progress<string>(l => ProvisionLog.Add(l)), CancellationToken.None);
            ProvisionResult = r.Succeeded
                ? $"Done: {r.ProfileName ?? string.Join(", ", r.NewProfiles.Select(Path.GetFileName))}. Log: {r.LogPath}"
                : $"{r.Failure?.Title}: {r.Failure?.Explanation} → {r.Failure?.Remedy}  (log: {r.LogPath})";
            if (r.Failure is { } f) ShowDiagnosis(f, "Profile creation");
            await RefreshInventoryAsync();
            await RunDoctorAsync();
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ devices

    public ObservableCollection<ImpDeviceItem> Devices { get; } = [];
    [ObservableProperty] private ImpDeviceItem? _targetDevice;
    [ObservableProperty] private string _devicesNote = "";

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        IsBusy = true;
        try
        {
            var listing = await _service.ListDevicesAsync(CancellationToken.None);
            var selected = Devices.Where(d => d.Selected).Select(d => d.Device.Identifier).ToHashSet();
            Devices.Clear();
            foreach (var d in listing.Devices)
            {
                var guidance = Guidance(d);
                var item = new ImpDeviceItem(d, guidance) { Selected = selected.Contains(d.Identifier) };
                if (d.Udid is { } udid && _inventory is not null)
                {
                    var registered = _inventory.Profiles.Where(p => p.Info.ProvisionedDevices.Contains(udid, StringComparer.OrdinalIgnoreCase)).Select(p => p.Info.Name).ToList();
                    item.Registration = registered.Count == 0 ? "Not in any installed profile (register it at developer.apple.com › Devices)." : "Registered in: " + string.Join(", ", registered);
                }
                Devices.Add(item);
            }
            DevicesNote = listing.Problem is { } p ? $"{p.Title}: {p.Explanation} → {p.Remedy}"
                : listing.Devices.Count == 0 ? "No iPhone or iPad found. Connect one by cable, unlock it and tap Trust."
                : $"{listing.Devices.Count} device(s) · devicectl {listing.ToolVersion}";
            StatsDevices = listing.Devices.Count(d => d.Connection != DeviceConnection.Unavailable).ToString();
            TargetDevice ??= Devices.FirstOrDefault(d => d.Device.Connection == DeviceConnection.Usb);
            QueueItems.Clear();
        }
        finally { IsBusy = false; }
    }

    private static List<string> Guidance(AppleDevice d)
    {
        var g = new List<string>();
        if (d.Connection == DeviceConnection.Unavailable) g.Add("Connect the device by cable and unlock it.");
        if (d.Pairing == PairingState.Unpaired) g.Add("Unlock the device and tap Trust in \"Trust This Computer?\", then enter the passcode.");
        if (d.DeveloperMode == DeveloperModeState.Disabled) g.Add("Turn on Developer Mode: Settings › Privacy & Security › Developer Mode, restart, then confirm Turn On.");
        if (d.DeveloperServicesAvailable == false && d.Connection != DeviceConnection.Unavailable) g.Add("Developer services are not ready: open Xcode › Window › Devices and Simulators and wait for \"Preparing device\" to finish.");
        if (d.Connection == DeviceConnection.Network) g.Add("Connected over Wi-Fi: installation works but USB is faster and more reliable.");
        return g;
    }

    [RelayCommand]
    private void UseDevice(ImpDeviceItem item)
    {
        TargetDevice = item;
        item.Selected = true;
        SelectedSectionItem = Sections.First(s => s.Section == ImpactorSection.Queue);
    }

    [RelayCommand]
    private async Task CopyDeviceRecordAsync(ImpDeviceItem item)
    {
        var json = HcgLinks.DeviceHandoff(item.Device, _service.Time.GetUtcNow());
        var file = Path.Combine(_service.Store.Directory, "system-tester-device.json");
        Directory.CreateDirectory(_service.Store.Directory);
        await File.WriteAllTextAsync(file, json);
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w } && w.Clipboard is { } cb)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, json);
        DevicesNote = $"Device record ({AppleDeviceContract.Schema}, UDID redacted) copied and saved to {file}.";
    }

    [RelayCommand]
    private async Task OpenInSystemTesterAsync(ImpDeviceItem item)
    {
        await CopyDeviceRecordAsync(item);
        var ok = await _shell.OpenUrlAsync(HcgLinks.SystemTesterConnectedSystems.ToString());
        DevicesNote = ok ? $"Opened HCG System Tester › Connected Systems. {item.Name}'s device record is on the clipboard."
            : "HCG System Tester did not open (it is not installed on this Mac, or does not register hcgsystemtester://). The device record is on the clipboard.";
    }

    // ------------------------------------------------------------------ deployment queue

    public ObservableCollection<ImpQueueItem> QueueItems { get; } = [];
    public ObservableCollection<ImpFindingItem> QueueChecks { get; } = [];
    [ObservableProperty] private bool _launchAfterInstall = true;
    [ObservableProperty] private bool _installing;
    [ObservableProperty] private string _queueSummary = "Choose the signed IPA and the devices, then Check and Install.";

    [RelayCommand]
    private async Task BrowseSignedIpaAsync()
    {
        var file = await _shell.PickFileAsync("Choose a signed IPA", "iOS app", "*.ipa");
        if (file is not null) { SignedIpaPath = file; if (_signed?.OutputIpa != file) _signed = null; }
    }

    [RelayCommand]
    private async Task CheckDeploymentAsync()
    {
        QueueChecks.Clear();
        if (SignedIpaPath is null) { QueueSummary = "Sign an IPA first, or choose a signed IPA."; return; }
        var targets = Devices.Where(d => d.Selected).ToList();
        if (targets.Count == 0 && TargetDevice is not null) targets.Add(TargetDevice);
        if (targets.Count == 0) { QueueSummary = "Select at least one device in Device Manager."; return; }
        var ipa = await Task.Run(() => _service.Inspect(SignedIpaPath));
        QueueItems.Clear();
        foreach (var d in targets)
        {
            var (checks, _) = await _service.CheckAsync(ipa, d.Device, CancellationToken.None);
            var q = new ImpQueueItem(d)
            {
                Tag = checks.HasBlockers() ? new("Blocked", "error") : checks.Warnings().Any() ? new("Ready — review warnings", "warning") : new("Ready", "success"),
                Status = checks.HasBlockers() ? string.Join("; ", checks.Blockers().Select(b => b.Title)) : "Ready to install",
            };
            QueueItems.Add(q);
            foreach (var f in checks.Where(c => c.Severity != FindingSeverity.Info)) QueueChecks.Add(ImpFindingItem.From(f with { Subject = d.Name + (f.Subject is null ? "" : " · " + f.Subject) }));
            _session = _session with { Device = d.Device, DeviceFindings = checks };
        }
        QueueSummary = $"{QueueItems.Count(q => q.Tag.Tone != "error")} of {QueueItems.Count} device(s) ready.";
        FlowStep = Math.Max(FlowStep, 3);
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (SignedIpaPath is null) return;
        if (QueueItems.Count == 0) await CheckDeploymentAsync();
        var ready = QueueItems.Where(q => q.Tag.Tone != "error").ToList();
        if (ready.Count == 0) { QueueSummary = "Nothing to install: every device is blocked (see checks)."; return; }
        if (QueueChecks.Any(c => c.Tag.Tone == "warning") &&
            !await _dialogs.ConfirmAsync("Install with these warnings?", string.Join("\n\n", QueueChecks.Where(c => c.Tag.Tone == "warning").Select(c => $"{c.Subject}: {c.Title} — {c.Detail}")), "Install"))
            return;
        Installing = true;
        _cts = new CancellationTokenSource();
        var ok = 0;
        try
        {
            foreach (var q in ready)
            {
                if (_cts.IsCancellationRequested) { q.Tag = new("Cancelled", "warning"); continue; }
                q.Tag = new("Installing", "info");
                var item = new DeploymentItem(SignedIpaPath, q.Device.Device) { LaunchAfterInstall = LaunchAfterInstall, WarningsAcknowledged = true };
                var r = await _service.DeployAsync(item, new Progress<InstallProgress>(p =>
                {
                    if (p.Percent is { } pct) q.Progress = pct;
                    q.Status = p.Message;
                }), _cts.Token, _signed is { SignedAppDirectory: not null } s && s.OutputIpa == SignedIpaPath ? s : null);
                q.Tag = r.Outcome switch { InstallOutcome.Installed => new("Installed", "success"), InstallOutcome.Cancelled => new("Cancelled", "warning"), _ => new("Failed", "error") };
                q.Status = r.Summary;
                q.Detail = r.Install?.Failure is { } f ? $"{f.Explanation}\n→ {f.Remedy}" : r.Launch?.Failure is { } lf ? $"Launch: {lf.Title} — {lf.Remedy}" : "";
                if (r.Outcome == InstallOutcome.Installed) { ok++; q.Progress = 100; }
                _session = _session with { Device = q.Device.Device, DeviceFindings = r.Checks, Install = r.Install, Launch = r.Launch };
                if (r.Install?.Failure is { } fail) ShowDiagnosis(fail, "Installation");
            }
            QueueSummary = $"Installed on {ok} of {ready.Count} device(s).";
            FlowStep = ok > 0 ? 4 : FlowStep;
            Status = QueueSummary;
            RefreshRecords();
        }
        finally { Installing = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        QueueSummary = "Cancelling … (the current step stops; an installation already handed to the device may still complete).";
    }

    // ------------------------------------------------------------------ monitor & history

    public ObservableCollection<ImpSignedAppItem> SignedApps { get; } = [];
    public ObservableCollection<ImpHistoryItem> History { get; } = [];

    private void RefreshRecords()
    {
        var now = _service.Time.GetUtcNow();
        SignedApps.Clear();
        foreach (var a in _service.Store.SignedApps())
        {
            var days = a.ExpiresAt is { } e ? (int)Math.Floor((e - now).TotalDays) : (int?)null;
            SignedApps.Add(new ImpSignedAppItem(a.Id, $"{a.AppName} {a.Version} ({a.Build})",
                $"{a.BundleIdentifier} · {a.Method} · {a.IdentityName} · profile {a.ProfileName} · signed {a.SignedAt.ToLocalTime():yyyy-MM-dd HH:mm}" + (a.InstalledOn.Count > 0 ? $" · installed on {string.Join(", ", a.InstalledOn)}" : ""),
                a.ExpiresAt is null ? "—" : $"{a.ExpiresAt.Value.ToLocalTime():yyyy-MM-dd}",
                days is null ? new("Unknown", "info") : days < 0 ? new("Expired", "error") : days < 14 ? new($"{days} days left", "warning") : new($"{days} days left", "success"), a));
        }
        History.Clear();
        foreach (var h in _service.Store.History().Take(300))
            History.Add(new ImpHistoryItem(h.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), $"{h.AppName} {h.Version}", $"{h.DeviceName} · {h.DeviceModel} · iOS {h.DeviceOs} · {h.Connection}", h.Summary ?? "",
                h.Outcome switch { InstallOutcome.Installed => new("Installed", "success"), InstallOutcome.Blocked => new("Blocked", "warning"), InstallOutcome.Cancelled => new("Cancelled", "warning"), _ => new("Failed", "error") }));
        Reminders.Clear();
        foreach (var r in _service.Store.Reminders(now))
            Reminders.Add(new ImpFindingItem(r.App.AppName, r.Message, "→ Signing Monitor › Re-sign, then reinstall.", null, r.Level == "expired" ? new("Expired", "error") : new("Expires soon", "warning")));
        StatsApps = SignedApps.Count.ToString();
    }

    [RelayCommand]
    private async Task ResignAsync(ImpSignedAppItem item)
    {
        var source = item.Record.SourceIpa;
        if (source is null || !File.Exists(source)) { ErrorMessage = $"The original IPA ({source}) is no longer there. Choose it in IPA Inspector."; return; }
        await LoadIpaAsync(source);
        SelectedSectionItem = Sections.First(s => s.Section == ImpactorSection.Signing);
    }

    [RelayCommand]
    private void Reinstall(ImpSignedAppItem item)
    {
        if (item.Record.SignedIpa is not { } ipa || !File.Exists(ipa)) { ErrorMessage = "The signed IPA is no longer there; re-sign it."; return; }
        SignedIpaPath = ipa;
        _signed = null;
        SelectedSectionItem = Sections.First(s => s.Section == ImpactorSection.Queue);
    }

    // ------------------------------------------------------------------ diagnostics

    [ObservableProperty] private bool _hasDiagnosis;
    [ObservableProperty] private string _diagnosisTitle = "";
    [ObservableProperty] private string _diagnosisExplanation = "";
    [ObservableProperty] private string _diagnosisRemedy = "";
    [ObservableProperty] private string _diagnosisEvidence = "";
    [ObservableProperty] private string _diagnosisRaw = "";
    [ObservableProperty] private string _pastedOutput = "";
    [ObservableProperty] private string? _exportNote;

    private void ShowDiagnosis(Diagnosis? d, string step)
    {
        if (d is null) return;
        HasDiagnosis = true;
        DiagnosisTitle = $"{step}: {d.Title}";
        DiagnosisExplanation = d.Explanation;
        DiagnosisRemedy = "→ " + d.Remedy;
        DiagnosisEvidence = d.Evidence ?? "";
        DiagnosisRaw = d.RawRedacted;
        Status = $"{step}: {d.Title}. See Diagnostics.";
    }

    [RelayCommand]
    private void DiagnosePasted()
    {
        if (string.IsNullOrWhiteSpace(PastedOutput)) return;
        ShowDiagnosis(InstallDiagnostics.Diagnose(PastedOutput), "Pasted output");
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var folder = await _shell.PickFolderAsync("Export HCG Impactor diagnostics to …");
        if (folder is null) return;
        var dir = _service.ExportDiagnostics(_session, folder, HostInfo.ProductVersion, ImpactorCommand.InstallationId(_paths));
        ExportNote = $"Exported to {dir} (report.json — {ImpactorReport.Schema}, report.md). Personal and device identifiers are redacted.";
        await _shell.OpenFolderAsync(dir);
    }
}
