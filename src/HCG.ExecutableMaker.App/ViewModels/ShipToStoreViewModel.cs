using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>A file in the Distribution Studio list, with what was detected, where it came from and its upload state.</summary>
public sealed partial class ShipFileItem(StoreArtifactCandidate candidate, ArtifactSource source = ArtifactSource.LocalFile) : ObservableObject
{
    [ObservableProperty] private StoreArtifactCandidate _candidate = candidate;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _state = "Ready to upload";
    [ObservableProperty] private string _tone = "neutral";
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private string? _speed;
    /// <summary>local · verified · skipped · failed · rejected (for the Synchronization Center).</summary>
    public string SyncState { get; set; } = "local";
    public ArtifactSource Source { get; } = source;
    /// <summary>Maker-built or chosen by the person; provenance is still only what a Release Passport of these bytes says.</summary>
    public StatusTag SourceTag => new(DistributionStudio.SourceLabel(Source), DistributionStudio.MakerBuilt(Source) ? "info" : "neutral");
    public string FileName => Candidate.FileName;
    public string Platform => Candidate.Label;
    public string Architectures => Candidate.Architectures.Count > 0 ? string.Join(", ", Candidate.Architectures) : "architecture not stated in the file";
    public string Size => Format.Bytes(Candidate.SizeBytes);
    public string Sha256 => Candidate.Sha256;
    public string Problems => string.Join(" ", Candidate.Problems.Concat(Candidate.Notes));
    /// <summary>The build provenance reported to HCG Store, from a release passport of these exact bytes.</summary>
    public string Provenance => "Build provenance: " + ShipToStoreCommand.ProvenanceText(Candidate.Provenance);
    public string Passport => Candidate.Provenance?.PassportPath is { } p ? $"Release Passport: {Path.GetFileName(p)} (describes these exact bytes)" : "Release Passport: none for these bytes";
    public bool HasProblems => Problems.Length > 0;
    public string Signing => Candidate.Signing.Status switch
    {
        "notarized" => "Signed and notarized (checked on this computer)",
        "signed" => "Signed (checked on this computer)",
        "unsigned" => "Unsigned",
        "invalid" => "Signature INVALID",
        _ => Candidate.SignatureStructure switch
        {
            "absent" => "No signature in the file",
            "present" => "Signature present in the file; not verified on this computer",
            _ => "Signature not checked on this computer",
        },
    };
    public string? SigningEvidence => Candidate.Signing.Evidence;
    public string Kind => Candidate.KindName;
    /// <summary>How it installs; never "ready" for unsigned, Simulator-only or jailbreak-only files.</summary>
    public StatusTag Install => new(Candidate.Readiness.OrdinaryInstall ? "Installable" : Candidate.Kind == "simulator-build" ? "Simulator only" : Candidate.Kind == "jailbreak-package" ? "Jailbroken devices only" : "Not ready for ordinary installation",
        Candidate.Readiness.OrdinaryInstall ? "success" : Candidate.Kind is "simulator-build" ? "error" : "warning");
    public string InstallDetail => Candidate.Readiness.Label + (Candidate.Ios is { ProfileName: { } pn } i ? $" Profile: {pn}{(i.ProfileVerified ? " (Apple signature verified)" : "")}{(i.ProfileExpires is { } e ? $", expires {e:yyyy-MM-dd}" : "")}." : "");
    partial void OnCandidateChanged(StoreArtifactCandidate value)
    {
        foreach (var n in new[] { nameof(Platform), nameof(Architectures), nameof(Problems), nameof(HasProblems), nameof(Signing), nameof(SigningEvidence), nameof(Kind), nameof(Install), nameof(InstallDetail), nameof(Provenance), nameof(Passport) }) OnPropertyChanged(n);
    }
}

public sealed record Choice(string Value, string Name);

/// <summary>One connection diagnostic as shown on the page.</summary>
public sealed record ShipCheckItem(StoreCheck Check)
{
    public StatusTag Chip => new(Check.StateLabel, Check.Tone);
    public bool HasResponse => Check.Response is { Length: > 0 };
    public bool HasAction => Check.Action is { Length: > 0 };
    public string ActionText => (Check.WhoActs is { } w ? $"What to do ({w}): " : "What to do: ") + Check.Action;
}

/// <summary>A product of the authorised catalogue, as HCG Store states it.</summary>
public sealed partial class CatalogueItem(StoreProduct product) : ObservableObject
{
    public StoreProduct Product { get; } = product;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIcon))] private Bitmap? _icon;
    public bool HasIcon => Icon is not null;
    public string Initials => string.Concat(Product.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string Title => Product.Title;
    public string Identifier => Product.Id + (Product.CentralId is { } c ? $" · central {c}" : "");
    public string Category => Product.Category ?? (Product.Source == "draft" ? "draft product" : "category not stated");
    public string Summary => Product.Summary ?? "";
    public bool HasSummary => Product.Summary is { Length: > 0 };
    public StatusTag Status => new(Product.DevelopmentStatus ?? Product.Source, Product.Source == "draft" ? "warning" : Product.DevelopmentStatus is "available" or "released" ? "success" : "info");
    public string Platforms => StoreCatalogue.PlatformsOf(Product) is { Count: > 0 } ps
        ? string.Join(" · ", ps.Select(p => StoreArtifacts.PlatformName(p) + (Product.Platforms.FirstOrDefault(x => x.Platform == p) is { Support: { } s } ? $" ({s})" : "")))
        : "platforms not stated";
    public string Editions => Product.Editions.Count > 0 ? "Editions: " + string.Join(", ", Product.Editions.Select(e => e.Name)) : "No editions";
    public string Current => Product.CurrentReleaseId is { } r ? $"Current release: {r}" : "No current release stated by HCG Store";
}

/// <summary>A release of the selected product (history), with the availability actions HCG Store accepts for it.</summary>
public sealed record ReleaseItem(StoreRelease Release, bool IsCurrent, StoreContract Contract)
{
    public string Title => $"{Release.Version} (build {Release.Build})";
    public StatusTag Status => new(IsCurrent ? "current" : Release.Availability is { } av && av != "available" ? av : Release.Stage ?? Release.Status,
        Release.Availability is "disabled" or "revoked" ? "error" : Release.Availability == "archived" ? "neutral" : Release.Status switch
    {
        "published" => "success",
        "submitted" or "approved" => "warning",
        "withdrawn" => "error",
        "superseded" => "neutral",
        _ => "info",
    });
    public string Detail =>
        $"{Release.Channel ?? "?"} channel{(Release.EditionId is { } e ? " · edition " + e : "")} · {Release.Artifacts.Count(a => a.Status != "removed")} file(s)" +
        (Release.Artifacts.Where(a => a.Status == "verified").Select(a => $"{StoreArtifacts.PlatformName(a.Platform)} {(a.Architectures.Count > 0 ? string.Join("/", a.Architectures) : "")} {a.Format}".Replace("  ", " ")).Distinct().ToList() is { Count: > 0 } ps ? " · " + string.Join(", ", ps) : "") +
        (Release.SupersededBy is { } s ? $" · superseded by {s}" : "") + (Release.WithdrawnReason is { } w ? $" · withdrawn: {w}" : "") +
        (Release.AvailabilityReason is { } ar ? $" · {Release.Availability}: {ar}" : "") +
        (Release.Targets.Where(t => t.Current).Select(t => $"{StoreArtifacts.PlatformName(t.Platform)} {t.Arch} {t.Channel}").ToList() is { Count: > 0 } ct ? " · current on " + string.Join(", ", ct) : "") +
        (Release.SupersedePreviousOnPublish == true ? " · chosen: replace the current release of its targets when published" : "") +
        (Release.PublicationRequest is { } pr ? $" · asks: {(pr.MakeCurrent ? "make current" : "")}{(pr.MakeCurrent && pr.SupersedePrevious != "none" ? ", " : "")}{(pr.SupersedePrevious != "none" ? "supersede " + pr.SupersedePrevious : "")}" : "");
    public string Requests => Release.Requests.Count == 0 ? "" : "Requests: " + string.Join("; ", Release.Requests.Select(r => $"{DistributionStudio.ActionName(r.Action ?? "?")} — {r.Reason} ({DateTimeOffset.FromUnixTimeMilliseconds(r.At).ToLocalTime():yyyy-MM-dd HH:mm})"));
    public bool HasRequests => Release.Requests.Count > 0;
    public IReadOnlyList<AvailabilityOption> Actions => DistributionStudio.AvailabilityOptions(Release, Contract);
    public bool HasActions => Actions.Count > 0;
    public bool IsOpen => DistributionStudio.OpenStatuses.Contains(Release.Status);
}

public sealed record StudioFindingItem(DistributionFinding Finding)
{
    public StatusTag Chip => new(Finding.Level switch { FindingLevel.Blocker => "Blocked", FindingLevel.Warning => "Warning", _ => "Info" },
        Finding.Level switch { FindingLevel.Blocker => "error", FindingLevel.Warning => "warning", _ => "neutral" });
    public string Message => Finding.Message;
}

public sealed record SyncItem(SyncGroup Group)
{
    public StatusTag Chip => new(Group.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Group.Count == 0 ? "neutral" : Group.Tone);
    public string Title => Group.Title;
    public string Items => Group.Count == 0 ? "—" : string.Join("\n", Group.Items.Take(8)) + (Group.Count > 8 ? $"\n… and {Group.Count - 8} more" : "");
}

public sealed record BuildChoice(BuildRecord Build, ShipFromBuild Ship)
{
    public string Name => Ship.Origin + $" · {Ship.Files.Count} verified file(s)";
}

public sealed record VaultChoice(ArtifactRecord Artifact, BuildRecord Build)
{
    public string Name => $"{Artifact.FileName} · {Build.ProjectName} · {Artifact.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>
/// Distribution Studio (the Ship to Store page): the products HCG lets this account manage, four operations (new update,
/// new product, add to a listed product, manage versions), artifacts from builds or the file system, version comparison,
/// resumable private uploads, and the synchronization state — all from HCG Store's own answers. HCG Connect identifies
/// the person; HCG decides whether they may publish; approval and publication happen in HCG Store, by a person. Opening
/// the page never uploads anything or changes any release.
/// </summary>
public sealed partial class ShipToStoreViewModel(
    HcgAccountService account,
    StoreClientFactory stores,
    ISettingsStore settings,
    IProcessRunner runner,
    IDialogService dialogs,
    IPlatformShell shell,
    NavigationService navigation,
    FeatureAccessService features,
    IBuildRepository builds) : PageViewModel
{
    public override string Title => "Distribution Studio";
    public override string Subtitle => "Ship to Store: private drafts, uploads and version management in HCG Store. Approval and publication happen in HCG Store, by a person.";

    // ───────────── Connection ─────────────
    [ObservableProperty] private string _storeUrl = HcgStoreClient.DefaultOrigin.ToString();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConnectionTag))] private string _connection = "Not checked";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConnectionTag))] private string _connectionTone = "neutral";
    public StatusTag ConnectionTag => new(Connection, ConnectionTone);
    [ObservableProperty] private string? _connectionDetail;
    public ObservableCollection<ShipCheckItem> Diagnostics { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDiagnostics), nameof(CapabilitiesText), nameof(CanRequestPublication), nameof(PublicationRequestNote), nameof(Contract), nameof(AvailabilityHelp), nameof(OperationNote))] private StoreDiagnosticsReport? _report;
    public bool HasDiagnostics => Report is not null;
    /// <summary>The checks stay open when something needs attention; when everything is ready they fold away (one click shows them).</summary>
    [ObservableProperty] private bool _diagnosticsExpanded = true;
    public string CapabilitiesText => Report?.Flags is { } f ? "HCG Store capabilities: " + StoreConnectionDiagnostics.FlagsText(f) : "HCG Store capabilities: not read yet";
    [ObservableProperty] private bool _isChecking;

    // ───────────── Catalogue ─────────────
    private readonly List<StoreProduct> _allProducts = [];
    public ObservableCollection<CatalogueItem> Catalogue { get; } = [];
    /// <summary>The products for the selectors (kept for the command line and existing bindings).</summary>
    public ObservableCollection<StoreProduct> Products { get; } = [];
    [ObservableProperty] private string _search = "";
    public IReadOnlyList<Choice> StatusFilters { get; } = [new("", "All statuses"), new("in-development", "In development"), new("testing", "Testing"), new("available", "Available"), new("draft", "Draft products")];
    [ObservableProperty] private Choice? _statusFilter;
    public IReadOnlyList<Choice> PlatformFilters { get; } = [new("", "All platforms"), new("windows", "Windows"), new("macos", "macOS"), new("ios", "iOS"), new("ipados", "iPadOS"), new("linux", "Linux"), new("android", "Android")];
    [ObservableProperty] private Choice? _platformFilter;
    [ObservableProperty] private CatalogueItem? _selectedItem;
    [ObservableProperty] private string? _productsNote;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editions), nameof(HasEditions), nameof(CanUseHcgRepo), nameof(HasProduct), nameof(ProductHeading))] private StoreProduct? _selectedProduct;
    public bool HasProduct => SelectedProduct is not null;
    public string ProductHeading => SelectedProduct is { } p ? $"{p.Title} ({p.Id})" : "No product selected";
    public IReadOnlyList<StoreEdition> Editions => SelectedProduct?.Editions ?? [];
    public bool HasEditions => Editions.Count > 0;
    [ObservableProperty] private StoreEdition? _selectedEdition;
    private readonly Dictionary<string, Bitmap?> _icons = [];

    // ───────────── History / versions ─────────────
    private IReadOnlyList<StoreRelease> _history = [];
    public ObservableCollection<ReleaseItem> History { get; } = [];
    [ObservableProperty] private string? _historyNote;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTargetRelease), nameof(AvailabilityActions))] private ReleaseItem? _selectedRelease;
    public bool HasTargetRelease => SelectedRelease is not null;
    public IReadOnlyList<AvailabilityOption> AvailabilityActions => SelectedRelease?.Actions ?? [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AvailabilityHelp), nameof(AvailabilityButton))] private AvailabilityOption? _availabilityAction;
    /// <summary>The HCG Store contract, from its own capability discovery (release lifecycle · requests · drafts and uploads only).</summary>
    public StoreContract Contract => new(Report?.Flags, Report?.Capabilities?.Operations);
    public string AvailabilityHelp => AvailabilityAction is { } o ? $"{o.Name}: {o.Explanation}" : Contract.Explanation;
    public string AvailabilityButton => AvailabilityAction?.Route switch
    {
        AvailabilityRoute.Direct => "Apply in HCG Store…",
        AvailabilityRoute.Browser => "Open in HCG Store to decide",
        AvailabilityRoute.Request => "Send request",
        _ => "Explain",
    };
    [ObservableProperty] private string _requestReason = "";
    [ObservableProperty] private string _requestConfirm = "";
    [ObservableProperty] private string? _requestResult;
    [ObservableProperty] private string? _additionPlan;

    // ───────────── Operation ─────────────
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNewUpdate), nameof(IsNewProduct), nameof(IsAddToListed), nameof(IsManageVersions), nameof(ShowsUpload), nameof(OperationNote))]
    private DistributionOperation _operation = DistributionOperation.NewUpdate;
    public bool IsNewUpdate { get => Operation == DistributionOperation.NewUpdate; set { if (value) Operation = DistributionOperation.NewUpdate; } }
    public bool IsNewProduct { get => Operation == DistributionOperation.NewProduct; set { if (value) Operation = DistributionOperation.NewProduct; } }
    public bool IsAddToListed { get => Operation == DistributionOperation.AddToListedProduct; set { if (value) Operation = DistributionOperation.AddToListedProduct; } }
    public bool IsManageVersions { get => Operation == DistributionOperation.ManageVersions; set { if (value) Operation = DistributionOperation.ManageVersions; } }
    public bool ShowsUpload => Operation is DistributionOperation.NewUpdate or DistributionOperation.AddToListedProduct or DistributionOperation.NewProduct;
    public string OperationNote => Operation switch
    {
        DistributionOperation.NewUpdate => "A new version or build of the selected product, uploaded as a private draft. The current release stays available until a replacement is approved and published in HCG Store.",
        DistributionOperation.NewProduct => "A private product draft in HCG Store. Listing it, its central product ID, editions and official artwork are HCG's decisions: export the registration request for HCG. Nothing is published and no access is granted.",
        DistributionOperation.AddToListedProduct => "Another platform, architecture or package for a release of the selected product. An open draft takes the files; a submitted or published release is never changed, so a new revision is prepared.",
        _ => "Current, superseded, disabled and private releases, as HCG Store holds them. " + Contract.Explanation + " Every change needs a reason and the version typed to confirm.",
    };

    // ───────────── New product ─────────────
    [ObservableProperty] private string _newProductId = "";
    [ObservableProperty] private string _newProductTitle = "";
    [ObservableProperty] private string _newProductDescription = "";
    [ObservableProperty] private string _newProductCategory = "";
    [ObservableProperty] private string _newProductEdition = "";
    [ObservableProperty] private string _newProductVersion = "";
    [ObservableProperty] private string? _newProductIcon;
    [ObservableProperty] private string? _newProductBanner;
    public ObservableCollection<PlatformToggle> NewProductPlatforms { get; } = [new("windows", "Windows"), new("macos", "macOS"), new("ios", "iOS"), new("ipados", "iPadOS"), new("linux", "Linux"), new("android", "Android")];
    [ObservableProperty] private string? _newProductResult;

    // ───────────── Release configuration ─────────────
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _build = "";
    [ObservableProperty] private string _releaseNotes = "";
    [ObservableProperty] private string _compatibilityNotes = "";
    public IReadOnlyList<Choice> Distributions { get; } = [new("free", "Free"), new("paid", "Paid (price set by HCG)"), new("subscription", "Subscription (set by HCG)"), new("entitlement", "Entitlement-gated (licence from HCG)")];
    [ObservableProperty] private Choice? _distribution;
    public IReadOnlyList<Choice> Eligibilities { get; } = [new("private", "Private (publishers only)"), new("internal-testing", "Internal testing"), new("public", "Public release (after manual approval)")];
    [ObservableProperty] private Choice? _eligibility;
    public IReadOnlyList<Choice> Channels { get; } = [new("beta", "Beta"), new("stable", "Stable")];
    [ObservableProperty] private Choice? _channel;
    public bool CanUseHcgRepo => SelectedProduct?.HcgRepo.Count > 0;
    [ObservableProperty] private bool _alsoHcgRepo;
    [ObservableProperty] private bool _makeCurrent;
    [ObservableProperty] private bool _supersedeCompatible;
    public bool CanRequestPublication => Contract.PublicationChoice;
    public string PublicationRequestNote => Contract.Lifecycle
        ? "Prepared with the draft and applied by HCG Store only when a person publishes this release: it then replaces the current release of each platform, architecture and channel it serves. Other targets are untouched, and until publication succeeds the previous release stays current."
        : CanRequestPublication
        ? "Sent with the draft as a request. HCG Store applies it only when a person publishes this release; the previous release is never disabled before the replacement is published, and other platforms, architectures and channels are kept."
        : "This HCG Store does not take publication requests yet. Ask the person who publishes in HCG Store; nothing changes from here.";

    // ───────────── Artifacts ─────────────
    public ObservableCollection<ShipFileItem> Files { get; } = [];
    public ObservableCollection<BuildChoice> BuildHistory { get; } = [];
    [ObservableProperty] private BuildChoice? _selectedBuild;
    public ObservableCollection<VaultChoice> Vault { get; } = [];
    [ObservableProperty] private VaultChoice? _selectedVault;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBuildOrigin))] private string? _buildOrigin;
    public bool HasBuildOrigin => BuildOrigin is not null;
    public ObservableCollection<string> NotOffered { get; } = [];
    [ObservableProperty] private bool _hasNotOffered;
    private string? _latestLocalVersion;

    // ───────────── Comparison and metadata ─────────────
    public ObservableCollection<StudioFindingItem> Findings { get; } = [];
    public ObservableCollection<StudioFindingItem> Discrepancies { get; } = [];
    [ObservableProperty] private string _comparisonSummary = "Select a product to compare versions.";
    [ObservableProperty] private VersionComparison? _comparison;

    // ───────────── Upload and sync ─────────────
    [ObservableProperty] private bool _isShipping;
    [ObservableProperty] private string? _releaseId;
    [ObservableProperty] private string? _reviewUrl;
    [ObservableProperty] private string _releaseStatus = "";
    [ObservableProperty] private bool _canSubmit;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string? _releaseManagerStage;
    public ObservableCollection<SyncItem> Sync { get; } = [];
    [ObservableProperty] private string? _syncNote;
    private CancellationTokenSource? _cts;

    /// <summary>Device context for future HCG UEM device-aware permissions: reported, not attested. Maker is not a device trust authority.</summary>
    public string DeviceContext => $"This device (reported, not attested): {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, Maker {HostInfo.ProductVersion}. " +
        "HCG UEM device-aware permissions will be decided by HCG; uploads do not depend on HCG UEM today.";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        Distribution ??= Distributions[1];
        Eligibility ??= Eligibilities[0];
        Channel ??= Channels[0];
        StatusFilter ??= StatusFilters[0];
        PlatformFilter ??= PlatformFilters[0];
        var configured = (await settings.LoadAsync()).HcgStoreUrl;
        if (StoreOrigin.Parse(configured) is { } o) StoreUrl = o.ToString();
        await LoadLocalBuildsAsync();
        // From a successful build: a new release with that build's verified files (not added to an earlier list).
        if (parameter is ShipFromBuild fromBuild && !IsShipping)
        {
            NewRelease();
            Files.Clear();
            await AddBuildAsync(fromBuild, ArtifactSource.LatestBuild);
        }
        // From the Artifacts page: the files to ship.
        else if (parameter is IEnumerable<string> paths) foreach (var p in paths) await AddFileAsync(p, ArtifactSource.ArtifactVault);
        else if (parameter is string one) await AddFileAsync(one, ArtifactSource.ArtifactVault);
        // Opening the page uploads nothing and changes no release. Signed out, it contacts no Store either.
        if (account.Status != HcgAccountStatus.SignedIn)
        {
            (Connection, ConnectionTone, ConnectionDetail) = ("Sign in required", "warning", "Sign in with HCG Connect on the Account page. Check connection shows, step by step, what HCG Store says about uploading.");
            return;
        }
        await ConnectAsync();
    }

    private HcgStoreClient? Client()
    {
        if (StoreOrigin.Parse(StoreUrl) is not { } origin) { (Connection, ConnectionTone, ConnectionDetail) = ("Invalid address", "error", "Use an https address (http only for this computer)."); return null; }
        return stores.Create(origin);
    }

    private int _connectGeneration;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        // Only the latest check may change the page: an earlier one that answers late must not overwrite a newer result.
        var generation = ++_connectGeneration;
        if (Client() is not { } client) return;
        IsChecking = true;
        (Connection, ConnectionTone, ConnectionDetail) = ("Checking…", "info", $"Checking HCG Store at {client.Origin.Host}, step by step…");
        try
        {
            var ctx = new StoreDiagnosticsContext(account.Status == HcgAccountStatus.SignedIn, account.Identity?.Email, account.Message, account.Configuration, HostInfo.ProductVersion);
            var report = await StoreConnectionDiagnostics.RunAsync(client, ctx, account.GetAccessTokenAsync);
            if (generation != _connectGeneration) return;
            Report = report;
            DiagnosticsExpanded = !report.CanUpload || report.Checks.Any(c => c.State is StoreCheckState.Warning or StoreCheckState.Failed);
            Diagnostics.Clear();
            foreach (var c in report.Checks) Diagnostics.Add(new ShipCheckItem(c));
            (Connection, ConnectionTone) = report.Headline;
            ConnectionDetail = report.CanUpload
                ? $"You can upload private drafts to {client.Origin.Host}. {report["publication"].Summary}"
                : report.Checks.FirstOrDefault(c => c.State == StoreCheckState.Failed) is { } f ? f.Summary + (f.Action is { } a ? " " + a : "") : "See the checks below.";
            await ApplyProductsAsync(client, report.Products, report.Flags, report["products"].Summary);
            if (report.CanUpload)
            {
                await settings.SaveAsync((await settings.LoadAsync()) with { HcgStoreUrl = client.Origin.ToString() });
                await RefreshSyncAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Never leave the previous Store's products and checks on screen after a check that failed.
            if (generation != _connectGeneration) return;
            Report = null; Diagnostics.Clear(); Products.Clear(); Catalogue.Clear(); _allProducts.Clear();
            (Connection, ConnectionTone, ConnectionDetail) = ("Check failed", "error", Security.SecretRedactor.RedactCredentials(ex.Message));
        }
        finally { if (generation == _connectGeneration) IsChecking = false; }
    }

    /// <summary>Copies the diagnostics as plain text (redacted: no token, cookie or credential).</summary>
    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        if (Report is { } r) await shell.CopyAsync(r.ToText() + Environment.NewLine + DeviceContext + Environment.NewLine);
    }

    // ───────────── Catalogue ─────────────

    /// <summary>Reloads the products HCG lets you manage, keeping the selected product and edition when they still exist.</summary>
    [RelayCommand]
    private async Task RefreshProductsAsync()
    {
        if (Client() is not { } client) return;
        try { await ApplyProductsAsync(client, await client.ProductsAsync(), Report?.Flags, null); }
        catch (HcgStoreException ex) { ProductsNote = StoreDiagnosis.Classify(ex, client.Origin).Explanation; }
    }

    private async Task ApplyProductsAsync(HcgStoreClient client, IReadOnlyList<StoreProduct> products, StoreCapabilityFlags? flags, string? note)
    {
        var enriched = await StoreCatalogue.EnrichAsync(client, products, flags);
        var (productId, editionId) = (SelectedProduct?.Id, SelectedEdition?.Id);
        _allProducts.Clear();
        _allProducts.AddRange(enriched);
        Products.Clear();
        foreach (var p in enriched) Products.Add(p);
        ApplyFilter();
        SelectedItem = Catalogue.FirstOrDefault(c => c.Product.Id == productId);
        SelectedProduct = SelectedItem?.Product;
        SelectedEdition = SelectedProduct?.Editions.FirstOrDefault(e => e.Id == editionId);
        ProductsNote = note ?? (Products.Count > 0 ? $"{Products.Count} product(s) from HCG Store, refreshed {DateTime.Now:HH:mm}." : "HCG Store lists no product you may manage.");
        foreach (var item in Catalogue) _ = LoadIconAsync(client, item);
    }

    private async Task LoadIconAsync(HcgStoreClient client, CatalogueItem item)
    {
        if (StoreCatalogue.IconUri(item.Product, client.Origin) is not { } uri) return;
        if (_icons.TryGetValue(uri.AbsoluteUri, out var cached)) { item.Icon = cached; return; }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var bytes = await http.GetByteArrayAsync(uri);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            _icons[uri.AbsoluteUri] = bmp;
            item.Icon = bmp;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException) { _icons[uri.AbsoluteUri] = null; }
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnStatusFilterChanged(Choice? value) => ApplyFilter();
    partial void OnPlatformFilterChanged(Choice? value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keep = SelectedItem?.Product.Id;
        var existing = Catalogue.ToDictionary(c => c.Product.Id);
        Catalogue.Clear();
        foreach (var p in StoreCatalogue.Filter(_allProducts, Search, StatusFilter?.Value, PlatformFilter?.Value))
            Catalogue.Add(existing.TryGetValue(p.Id, out var e) && e.Product == p ? e : new CatalogueItem(p) { Icon = null });
        if (keep is not null) SelectedItem = Catalogue.FirstOrDefault(c => c.Product.Id == keep) ?? SelectedItem;
    }

    partial void OnSelectedItemChanged(CatalogueItem? value)
    {
        if (value is not null && value.Product != SelectedProduct) SelectedProduct = value.Product;
    }

    partial void OnSelectedProductChanged(StoreProduct? value)
    {
        if (value is not null && SelectedItem?.Product.Id != value.Id) SelectedItem = Catalogue.FirstOrDefault(c => c.Product.Id == value.Id);
        SelectedEdition = value?.Editions.Count == 1 ? value.Editions[0] : null;
        _history = [];
        History.Clear();
        SelectedRelease = null;
        if (value is not null) _ = LoadHistoryAsync();
        Recompare();
    }

    partial void OnSelectedEditionChanged(StoreEdition? value) => Recompare();
    partial void OnVersionChanged(string value) => Recompare();
    partial void OnBuildChanged(string value) => Recompare();
    partial void OnChannelChanged(Choice? value) => Recompare();
    partial void OnAlsoHcgRepoChanged(bool value) => Recompare();

    /// <summary>The product's version history, from HCG Store.</summary>
    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (SelectedProduct is not { } p || Client() is not { } client) return;
        try
        {
            var list = await client.ReleasesAsync(p.Id);
            if (SelectedProduct?.Id != p.Id) return;
            _history = list;
            var keep = SelectedRelease?.Release.Id;
            History.Clear();
            var (current, _) = DistributionStudio.CurrentPublished(p, list, SelectedEdition?.Id, null);
            foreach (var r in list) History.Add(new ReleaseItem(r, r.IsCurrentSomewhere || r.Id == current?.Id || r.Id == p.CurrentReleaseId, Contract));
            SelectedRelease = History.FirstOrDefault(h => h.Release.Id == keep);
            HistoryNote = list.Count == 0 ? "HCG Store holds no release of this product yet." : $"{list.Count} release(s) in HCG Store, read {DateTime.Now:HH:mm}.";
        }
        catch (HcgStoreException ex) { HistoryNote = "History could not be read: " + StoreDiagnosis.Classify(ex, client.Origin).Explanation; }
        Recompare();
    }

    // ───────────── Version comparison and metadata ─────────────

    private void Recompare()
    {
        Findings.Clear();
        Discrepancies.Clear();
        var files = Files.Where(f => f.Candidate.IsSupported).Select(f => f.Candidate).ToList();
        if (SelectedProduct is null) { Comparison = null; ComparisonSummary = "Select a product to compare versions."; return; }
        var c = DistributionStudio.Compare(SelectedProduct, _history, SelectedEdition?.Id, Channel?.Value ?? "beta", Version, Build, files, _latestLocalVersion,
            Operation == DistributionOperation.AddToListedProduct ? SelectedRelease?.Release.Id : null);
        Comparison = c;
        foreach (var f in c.Findings) Findings.Add(new StudioFindingItem(f));
        foreach (var d in DistributionStudio.Discrepancies(Version.Trim(), Build.Trim(), SelectedProduct.Title, files)) Discrepancies.Add(new StudioFindingItem(d));
        // A .deb for HCG Repo must carry the release version (HCG Store's rule; Sileo asks for the version in the index).
        if (AlsoHcgRepo && CanUseHcgRepo)
            foreach (var problem in ShipToStoreService.RepoDeliveryProblems(Version.Trim(), ["hcg-store", "hcg-repo"], files))
                Findings.Add(new StudioFindingItem(new DistributionFinding("repo-debian-version", FindingLevel.Blocker, problem)));
        ComparisonSummary =
            $"Current published: {(c.CurrentPublished is { } cp ? $"{cp.Version} (build {cp.Build}){(c.CurrentBasis == "catalogue" ? " — named current by HCG Store" : " — newest published")}" : "none")}" +
            $" · Latest private draft: {(c.LatestPrivateDraft is { } d0 ? $"{d0.Version} (build {d0.Build}, {d0.Stage ?? d0.Status})" : "none")}" +
            $" · Latest local build: {c.LatestLocalBuild ?? "none"} · Proposed: {(c.Proposed.Length > 0 ? c.Proposed : "—")}" +
            (c.PlatformReleases.Count > 0 ? "\nPublished platforms: " + string.Join(", ", c.PlatformReleases.Select(p => $"{StoreArtifacts.PlatformName(p.Platform)} {p.Architecture} {p.Format} → {p.Version} ({p.Build}, {p.Channel})").Distinct()) : "");
    }

    partial void OnOperationChanged(DistributionOperation value)
    {
        if (value == DistributionOperation.AddToListedProduct) PlanAddition();
        Recompare();
    }

    partial void OnSelectedReleaseChanged(ReleaseItem? value)
    {
        AvailabilityAction = null;
        RequestConfirm = "";
        RequestResult = null;
        if (Operation == DistributionOperation.AddToListedProduct) PlanAddition();
    }

    private void PlanAddition()
    {
        if (SelectedRelease is not { } r) { AdditionPlan = "Choose the release to extend in the version history."; return; }
        var plan = DistributionStudio.PlanAddition(r.Release, _history);
        AdditionPlan = plan.Explanation;
        Version = plan.Version;
        Build = plan.Build;
        if (r.Release.Channel is { } ch) Channel = Channels.FirstOrDefault(c => c.Value == ch) ?? Channel;
        if (r.Release.EditionId is { } ed) SelectedEdition = Editions.FirstOrDefault(e => e.Id == ed) ?? SelectedEdition;
    }

    // ───────────── New product ─────────────

    [RelayCommand]
    private async Task CreateDraftProductAsync()
    {
        if (Client() is not { } client) return;
        try
        {
            var platforms = NewProductPlatforms.Where(p => p.IsSelected).Select(p => p.Value).ToList();
            // Descriptive fields go only to a Store that takes them (older Stores refuse unknown fields).
            var p = Report?.Flags?.ProductMetadata == true
                ? await client.CreateDraftProductAsync(new StoreDraftProductRequest
                {
                    Id = NewProductId.Trim(), Title = NewProductTitle.Trim(),
                    Description = NewProductDescription.Trim() is { Length: > 0 } d ? d : null,
                    Category = NewProductCategory.Trim() is { Length: > 0 } c ? c : null,
                    Platforms = platforms.Count > 0 ? platforms : null,
                })
                : await client.CreateDraftProductAsync(NewProductId.Trim(), NewProductTitle.Trim());
            NewProductResult = $"Draft product {p.Id} created in HCG Store. {p.Note}";
            await RefreshProductsAsync();
            SelectedProduct = Products.FirstOrDefault(x => x.Id == p.Id) ?? p;
            if (NewProductVersion.Trim() is { Length: > 0 } v) Version = v;
            Operation = DistributionOperation.NewUpdate;
        }
        catch (HcgStoreException ex) { NewProductResult = "Not created: " + ex.Message; }
    }

    [RelayCommand] private async Task PickIconAsync() => NewProductIcon = await shell.PickFileAsync("Product icon", "Images", "*.png", "*.webp", "*.jpg", "*.jpeg") ?? NewProductIcon;
    [RelayCommand] private async Task PickBannerAsync() => NewProductBanner = await shell.PickFileAsync("Product banner", "Images", "*.png", "*.webp", "*.jpg", "*.jpeg") ?? NewProductBanner;

    /// <summary>Writes the central registration request (name, description, category, platforms, edition, initial version,
    /// icon and banner with their SHA-256) to a folder for HCG. It is not sent anywhere and grants nothing.</summary>
    [RelayCommand]
    private async Task ExportRegistrationAsync()
    {
        if (await shell.PickFolderAsync("Folder for the registration request") is not { } folder) return;
        try
        {
            var path = await ProductRegistrationRequest.WriteAsync(folder, new ProductRegistrationRequest.Input(NewProductId.Trim(), NewProductTitle.Trim(), NewProductDescription.Trim(),
                NewProductCategory.Trim(), NewProductPlatforms.Where(p => p.IsSelected).Select(p => p.Value).ToList(), NewProductEdition.Trim(), NewProductVersion.Trim(),
                NewProductIcon, NewProductBanner, account.Identity?.Email, StoreUrl));
            NewProductResult = $"Registration request written to {path}. Send it to HCG for central registration; nothing was uploaded.";
            await shell.OpenFolderAsync(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { NewProductResult = "Not written: " + ex.Message; }
    }

    // ───────────── Artifacts ─────────────

    private async Task LoadLocalBuildsAsync()
    {
        try
        {
            var list = await builds.ListAsync(limit: 40);
            BuildHistory.Clear();
            foreach (var b in list.Where(b => b.Status == BuildStatus.Succeeded).OrderByDescending(b => b.EndedAt ?? b.StartedAt))
                if (ShipFromBuild.From(b) is { Files.Count: > 0 } s) BuildHistory.Add(new BuildChoice(b, s));
            Vault.Clear();
            foreach (var (a, b) in await builds.ListBuildsWithArtifactsAsync())
                if (a.Kind != ArtifactKind.Directory && File.Exists(a.Path)) Vault.Add(new VaultChoice(a, b));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* the lists stay empty; files can still be chosen */ }
    }

    [RelayCommand]
    private async Task AddLatestBuildAsync()
    {
        await LoadLocalBuildsAsync();
        if (BuildHistory.FirstOrDefault() is { } latest) await AddBuildAsync(latest.Ship, ArtifactSource.LatestBuild);
        else await dialogs.AlertAsync("No verified build", "Build History has no successful build with verified files. Choose files instead.");
    }

    [RelayCommand]
    private async Task AddSelectedBuildAsync()
    {
        if (SelectedBuild is { } b) await AddBuildAsync(b.Ship, ArtifactSource.BuildHistory);
    }

    [RelayCommand]
    private async Task AddSelectedVaultAsync()
    {
        if (SelectedVault is { } v) await AddFileAsync(v.Artifact.Path, ArtifactSource.ArtifactVault);
    }

    private async Task AddBuildAsync(ShipFromBuild fromBuild, ArtifactSource source)
    {
        NotOffered.Clear();
        BuildOrigin = $"From build: {fromBuild.Origin}. {fromBuild.Files.Count} verified file(s) offered. No rebuild.";
        foreach (var n in fromBuild.NotOffered) NotOffered.Add(n);
        HasNotOffered = NotOffered.Count > 0;
        if (fromBuild.Version is { Length: > 0 } v && SemVer().IsMatch(v))
        {
            _latestLocalVersion = v;
            if (Version.Length == 0 && Operation != DistributionOperation.AddToListedProduct) Version = v;
        }
        foreach (var p in fromBuild.Files) await AddFileAsync(p, source);
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (await shell.PickFileAsync("Choose release files", "Release files", "*.exe", "*.msi", "*.msix", "*.zip", "*.dmg", "*.pkg", "*.ipa", "*.deb", "*.rpm", "*.AppImage") is { } f)
            await AddFileAsync(f, ArtifactSource.LocalFile);
    }

    public Task AddFileAsync(string path) => AddFileAsync(path, ArtifactSource.LocalFile);

    public async Task AddFileAsync(string path, ArtifactSource source)
    {
        if (Files.Any(f => f.Candidate.Path == path)) return;
        if (Directory.Exists(path) && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsMacOS()
            && await dialogs.ConfirmAsync("Package the app as a ZIP?", $"{Path.GetFileName(path)} is a folder. HCG Store takes macOS apps as ZIP archives. Package it with ditto (keeps its signature)?", "Package as ZIP"))
            path = await StoreArtifacts.ZipAppAsync(path, Path.GetDirectoryName(path)!, runner);
        var c = await Task.Run(() => StoreArtifacts.DetectAsync(path));
        var item = new ShipFileItem(c, source) { State = c.IsSupported ? "Checking signature…" : "Not supported", Tone = c.IsSupported ? "neutral" : "error" };
        Files.Add(item);
        if (!c.IsSupported) { Recompare(); return; }
        var signing = await Task.Run(() => StoreArtifacts.CheckSigningAsync(c, runner));
        item.Candidate = c with { Signing = signing };
        item.State = signing.Status == "invalid" ? "Signature invalid" : "Ready to upload";
        item.Tone = signing.Status == "invalid" ? "error" : "neutral";
        Recompare();
    }

    [RelayCommand] private void RemoveFile(ShipFileItem item) { if (!IsShipping) { Files.Remove(item); Recompare(); } }

    private StoreReleaseRequest Request() => new()
    {
        ProductId = SelectedProduct!.Id,
        EditionId = SelectedEdition?.Id,
        Version = Version.Trim(),
        Build = Build.Trim(),
        Channel = Channel?.Value ?? "beta",
        ReleaseNotes = ReleaseNotes.Split('\n').Select(l => l.Trim().TrimStart('-', '•').Trim()).Where(l => l.Length > 0).ToList(),
        Compatibility = new StoreCompatibility
        {
            MinimumOs = Files.Where(f => f.Candidate.MinimumOs is not null).GroupBy(f => f.Candidate.Platform!).ToDictionary(g => g.Key, g => g.First().Candidate.MinimumOs!),
            Architectures = Files.SelectMany(f => f.Candidate.Architectures).Distinct().ToList(),
            // What the person wrote, then every discrepancy between typed metadata and the files' verified facts.
            Notes = DistributionStudio.CompatibilityNotes(CompatibilityNotes.Split('\n'), Discrepancies.Select(d => d.Finding)),
        },
        Distribution = Distribution?.Value ?? "paid",
        Eligibility = Eligibility?.Value ?? "private",
        Destinations = AlsoHcgRepo && CanUseHcgRepo ? ["hcg-store", "hcg-repo"] : ["hcg-store"],
        PublicationRequest = DistributionStudio.PublicationRequest(MakeCurrent, SupersedeCompatible, Report?.Flags),
        SupersedePreviousOnPublish = DistributionStudio.SupersedeOnPublish(MakeCurrent, SupersedeCompatible, Contract),
    };

    // ───────────── Upload ─────────────

    [RelayCommand]
    private async Task ShipAsync()
    {
        if (SelectedProduct is null || string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(Build) || Files.Count == 0)
        {
            await dialogs.AlertAsync("Not ready", "Choose a product, enter the version and build number, and add at least one file.");
            return;
        }
        if (Files.FirstOrDefault(f => !f.Candidate.IsSupported) is { } bad) { await dialogs.AlertAsync("Unsupported file", $"{bad.FileName}: {bad.Problems}\n\nRemove it from the list to ship the other files."); return; }
        Recompare();
        if (Findings.Where(f => f.Finding.Code == "repo-debian-version").Select(f => "• " + f.Finding.Message).ToList() is { Count: > 0 } repo)
        {
            await dialogs.AlertAsync("Not deliverable through HCG Repo", string.Join("\n", repo) + "\n\nNothing was uploaded.");
            return;
        }
        if (Comparison is { Blocked: true } blocked && ReleaseId is null)
        {
            await dialogs.AlertAsync("Version conflict", string.Join("\n", blocked.Findings.Where(f => f.Level == FindingLevel.Blocker).Select(f => "• " + f.Message)));
            return;
        }
        if (Discrepancies.Count > 0 && !await dialogs.ConfirmAsync("Metadata differs from the files", string.Join("\n", Discrepancies.Select(d => "• " + d.Message)) +
                "\n\nThe files' verified facts are not changed. Each discrepancy is recorded in the draft's compatibility notes for reviewers. Upload the draft?", "Upload with notes")) return;
        // Public release of a file that cannot be installed on ordinary devices: say so before anything is uploaded.
        if (Eligibility?.Value == "public" && Files.Where(f => !f.Candidate.Readiness.OrdinaryInstall).ToList() is { Count: > 0 } limited
            && !await dialogs.ConfirmAsync("Not ready for ordinary installation", string.Join("\n", limited.Select(f => $"• {f.FileName}: {f.Candidate.Readiness.Label}")) +
                "\n\nYou asked for a public release. HCG Store shows these labels to approvers and customers, and refuses public approval of unsigned iOS device builds. Upload the draft anyway?", "Upload draft")) return;
        if (Client() is not { } client) return;
        if (StoreSessionPolicy.Refusal(account.Configuration, client.Origin) is { } refusal) { await dialogs.AlertAsync("Not sent", refusal); return; }
        IsShipping = true;
        _cts = new CancellationTokenSource();
        var service = new ShipToStoreService(client, features);
        var meters = new Dictionary<string, (DateTime At, long Bytes)>();
        var progress = new Progress<ShipItemProgress>(p =>
        {
            if (Files.FirstOrDefault(f => f.FileName == p.FileName) is not { } item) return;
            item.Progress = p.Total == 0 ? 0 : 100.0 * p.Received / p.Total;
            if (p.State == ShipItemState.Uploading)
            {
                var now = DateTime.UtcNow;
                if (meters.TryGetValue(p.FileName, out var m) && (now - m.At).TotalSeconds >= 0.5 && p.Received >= m.Bytes)
                {
                    item.Speed = $"{Format.Bytes((long)((p.Received - m.Bytes) / (now - m.At).TotalSeconds))}/s";
                    meters[p.FileName] = (now, p.Received);
                }
                else if (!meters.ContainsKey(p.FileName)) meters[p.FileName] = (now, p.Received);
            }
            else item.Speed = null;
            item.State = p.State switch
            {
                ShipItemState.Uploading => $"Uploading {Format.Bytes(p.Received)} of {Format.Bytes(p.Total)}",
                ShipItemState.Verifying => "Verifying in HCG Store…",
                ShipItemState.Verified => "Verified by HCG Store",
                ShipItemState.Skipped => "Already uploaded",
                ShipItemState.Rejected => "Rejected by HCG Store",
                ShipItemState.Failed => "Failed",
                _ => p.State.ToString(),
            };
            item.SyncState = p.State switch { ShipItemState.Verified => "verified", ShipItemState.Skipped => "skipped", ShipItemState.Failed => "failed", ShipItemState.Rejected => "rejected", _ => item.SyncState };
            item.Tone = p.State switch { ShipItemState.Verified or ShipItemState.Skipped => "success", ShipItemState.Rejected or ShipItemState.Failed => "error", _ => "info" };
            if (p.Detail is not null) item.Detail = p.Detail;
        });
        try
        {
            // Add to a listed product: an open draft takes the files; otherwise a new revision draft is created.
            var existing = ReleaseId ?? (Operation == DistributionOperation.AddToListedProduct && SelectedRelease?.Release is { Status: "draft" } open ? open.Id : null)
                                     ?? Comparison?.ContinuesDraft?.Id;
            var result = await service.ShipAsync(Request(), Files.Select(f => f.Candidate).ToList(), existing, progress, _cts.Token);
            ShowRelease(result.Release);
            await SyncReleaseManagerAsync(client, result.Release.Id);
            Summary = (result.ContinuedExistingDraft ? "Continued the existing draft for this version and build (no second release was created). " : "") + (result.AllVerified
                ? $"All {result.Items.Count} file(s) are in HCG Store, verified by SHA-256. The release is a private draft until you submit it and an approver approves it in HCG Store."
                : "Some files did not make it. Fix the problem and press Retry: verified files are kept, interrupted uploads resume.");
            await LoadHistoryAsync();
            await RefreshSyncAsync();
        }
        catch (HcgStoreException ex) { Summary = ex.Message; await dialogs.AlertAsync("Upload stopped", ex.Message); }
        catch (FeatureAccessDeniedException ex)
        {
            Summary = ex.Decision.Explanation + (ex.Decision.RemedyText is { Length: > 0 } r ? " " + r : "");
            await dialogs.AlertAsync(ex.Decision.IsUnavailable ? "HCG Store could not decide" : "Not permitted", Summary);
        }
        catch (OperationCanceledException) { Summary = "Cancelled. Press Retry to resume; the bytes HCG Store confirmed are kept."; }
        finally { IsShipping = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    /// <summary>What HCG Store's Release Manager shows for this release (read back from the Store, not assumed).</summary>
    private async Task SyncReleaseManagerAsync(HcgStoreClient client, string releaseId)
    {
        try
        {
            var m = await client.ReleaseManagerAsync();
            ReleaseManagerStage = m.Releases.FirstOrDefault(r => r.Id == releaseId) is { } r
                ? $"Release Manager: {r.ProductTitle ?? r.ProductId} {r.Version} (build {r.Build}) · {r.Stage ?? r.Status} · {r.Files} file(s){(m.AuditIntact == true ? " · audit trail intact" : "")}"
                : "Release Manager does not list this release yet.";
        }
        catch (HcgStoreException ex) { ReleaseManagerStage = "Release Manager could not be read: " + ex.Message; }
    }

    private void ShowRelease(StoreRelease r)
    {
        ReleaseId = r.Id;
        ReviewUrl = r.ReviewUrl;
        ReleaseStatus = $"{r.ProductTitle ?? r.ProductId} {r.Version} (build {r.Build}) · {r.Status}{(r.ApprovedEligibility is { } e ? $" · approved for {e}" : "")}" +
                        (r.SupersedePreviousOnPublish == true ? " · choice recorded: replace the current release of its targets when published" : "") +
                        (r.PublicationRequest is { } pr ? $" · request recorded: {(pr.MakeCurrent ? "make current" : "")}{(pr.MakeCurrent && pr.SupersedePrevious != "none" ? ", " : "")}{(pr.SupersedePrevious != "none" ? "supersede " + pr.SupersedePrevious + " releases" : "")}" : "");
        CanSubmit = r.Status == "draft" && r.Artifacts.Any(a => a.Status == "verified") && !r.Artifacts.Any(a => a.Status is "awaiting-upload" or "uploading");
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (ReleaseId is null || Client() is not { } client) return;
        if (!await dialogs.ConfirmAsync("Submit for review?", "The release is locked (no more files) and waits for a person with HCG's approver permission to review and approve it in HCG Store. Nothing is published automatically, and nothing is sent to TestFlight or any external store.", "Submit for review")) return;
        try
        {
            await features.RequireAsync(MakerFeatures.StorePublish, storeOrigin: client.Origin);
            ShowRelease(await client.SubmitAsync(ReleaseId));
            await SyncReleaseManagerAsync(client, ReleaseId);
            await LoadHistoryAsync();
            await RefreshSyncAsync();
        }
        catch (HcgStoreException ex) { await dialogs.AlertAsync("Not submitted", ex.Message); }
        catch (FeatureAccessDeniedException ex) { await dialogs.AlertAsync("Not submitted", ex.Decision.Explanation + " " + ex.Decision.RemedyText); }
    }

    /// <summary>Opens this release's review page in HCG Store (the same release HCG Store Manager shows).</summary>
    [RelayCommand]
    private async Task OpenReviewAsync()
    {
        var url = ReviewUrl ?? SelectedRelease?.Release.ReviewUrl;
        if (url is { } u && !await shell.OpenUrlAsync(u)) await shell.CopyAsync(u);
    }

    /// <summary>Opens HCG Store's Release Manager page (the website's list of the same releases).</summary>
    [RelayCommand]
    private async Task OpenReleaseManagerAsync()
    {
        if (StoreOrigin.Parse(StoreUrl) is not { } origin) return;
        var u = new Uri(origin, "release-manager").ToString();
        if (!await shell.OpenUrlAsync(u)) await shell.CopyAsync(u);
    }

    [RelayCommand]
    private void NewRelease()
    {
        ReleaseId = null; ReviewUrl = null; ReleaseStatus = ""; Summary = null; CanSubmit = false; ReleaseManagerStage = null;
        foreach (var f in Files) { f.State = "Ready to upload"; f.Progress = 0; f.Detail = null; f.Tone = "neutral"; f.Speed = null; f.SyncState = "local"; }
    }

    [RelayCommand] private void OpenAccount() => navigation.Navigate("account");

    // ───────────── Manage versions ─────────────

    /// <summary>
    /// Disable, enable, supersede or restore the selected release, the way this HCG Store offers it: through its own
    /// availability endpoint (disable, enable), in HCG Store's browser review (supersede, restore: publication decisions),
    /// as a recorded request (proposed contract), or not at all (explained). Every change needs a reason and the version
    /// typed to confirm, and HCG Store checks HCG's permission for it.
    /// </summary>
    [RelayCommand]
    private async Task SendAvailabilityRequestAsync()
    {
        if (SelectedRelease is not { } r || AvailabilityAction is not { } option || Client() is not { } client) { RequestResult = "Choose a release and an action."; return; }
        if (option.Route == AvailabilityRoute.Unavailable) { RequestResult = option.Explanation + " Nothing was changed."; return; }
        if (option.Route == AvailabilityRoute.Browser)
        {
            RequestResult = option.Explanation + " The release is opened in HCG Store; nothing was changed from Maker.";
            if (r.Release.ReviewUrl is { } u && !await shell.OpenUrlAsync(u)) await shell.CopyAsync(u);
            return;
        }
        if (RequestReason.Trim().Length == 0) { RequestResult = "Give a reason; it is recorded in HCG Store's audit trail."; return; }
        if (RequestConfirm.Trim() != r.Release.Version) { RequestResult = $"Type the version ({r.Release.Version}) to confirm."; return; }
        try
        {
            if (option.Route == AvailabilityRoute.Direct)
            {
                if (!await dialogs.ConfirmAsync($"{option.Name} {r.Title}?", option.Explanation + $"\n\nReason: {RequestReason.Trim()}", option.Name, destructive: option.Action == "disable")) { RequestResult = "Cancelled; nothing was changed."; return; }
                var changed = await client.ChangeAvailabilityAsync(r.Release.Id, option.Action, RequestReason.Trim(), RequestConfirm.Trim(), r.Release.Revision);
                RequestResult = $"{option.Name}: HCG Store now lists {changed.Version} (build {changed.Build}) as {changed.Availability ?? changed.Status}.";
            }
            else
            {
                var recorded = await client.RequestAsync(r.Release.Id, option.Action, RequestReason.Trim(), RequestConfirm.Trim());
                RequestResult = $"{option.Name} requested for {r.Title}. {recorded.Note}";
            }
            RequestReason = ""; RequestConfirm = "";
            await LoadHistoryAsync();
            await RefreshSyncAsync();
        }
        catch (HcgStoreException ex) { RequestResult = $"Not changed: {ex.Message}"; }
    }

    // ───────────── Synchronization Center ─────────────

    /// <summary>Reads Release Manager and shows what is where. Uses HCG Store's answers only.</summary>
    [RelayCommand]
    private async Task RefreshSyncAsync()
    {
        if (Client() is not { } client || account.Status != HcgAccountStatus.SignedIn) return;
        try
        {
            var m = await client.ReleaseManagerAsync();
            var current = _allProducts.Select(p => p.CurrentReleaseId).OfType<string>().Concat(History.Where(h => h.IsCurrent).Select(h => h.Release.Id)).ToHashSet();
            var groups = DistributionStudio.Sync(m.Releases, current, Files.Select(f => (f.FileName, f.SyncState)).ToList(), Findings.Select(f => f.Finding).ToList(), SelectedProduct?.Id);
            Sync.Clear();
            foreach (var g in groups) Sync.Add(new SyncItem(g));
            SyncNote = $"From HCG Store's Release Manager at {DateTime.Now:HH:mm}{(SelectedProduct is { } p ? $", for {p.Title}" : ", all products")}; audit trail {(m.AuditIntact == true ? "intact" : m.AuditIntact == false ? "NOT intact" : "not stated")}.";
        }
        catch (HcgStoreException ex) { SyncNote = "Release Manager could not be read: " + StoreDiagnosis.Classify(ex, client.Origin).Explanation; }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial System.Text.RegularExpressions.Regex SemVer();
}

public sealed partial class PlatformToggle(string value, string name) : ObservableObject
{
    public string Value { get; } = value;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;
}
