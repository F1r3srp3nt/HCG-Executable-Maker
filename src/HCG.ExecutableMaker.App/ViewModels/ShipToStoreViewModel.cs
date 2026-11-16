using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>A file in the Ship to Store list, with what was detected and its upload state.</summary>
public sealed partial class ShipFileItem(StoreArtifactCandidate candidate) : ObservableObject
{
    [ObservableProperty] private StoreArtifactCandidate _candidate = candidate;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _state = "Ready to upload";
    [ObservableProperty] private string _tone = "neutral";
    [ObservableProperty] private string? _detail;
    public string FileName => Candidate.FileName;
    public string Platform => Candidate.Label;
    public string Size => Format.Bytes(Candidate.SizeBytes);
    public string Sha256 => Candidate.Sha256;
    public string Problems => string.Join(" ", Candidate.Problems.Concat(Candidate.Notes));
    public bool HasProblems => Problems.Length > 0;
    public string Signing => Candidate.Signing.Status switch
    {
        "notarized" => "Signed and notarized (checked on this computer)",
        "signed" => "Signed (checked on this computer)",
        "unsigned" => "Unsigned",
        "invalid" => "Signature INVALID",
        _ => "Signature not checked on this computer",
    };
    public string? SigningEvidence => Candidate.Signing.Evidence;
    public string Kind => Candidate.KindName;
    /// <summary>How it installs; never "ready" for unsigned, Simulator-only or jailbreak-only files.</summary>
    public StatusTag Install => new(Candidate.Readiness.OrdinaryInstall ? "Installable" : Candidate.Kind == "simulator-build" ? "Simulator only" : Candidate.Kind == "jailbreak-package" ? "Jailbroken devices only" : "Not ready for ordinary installation",
        Candidate.Readiness.OrdinaryInstall ? "success" : Candidate.Kind is "simulator-build" ? "error" : "warning");
    public string InstallDetail => Candidate.Readiness.Label + (Candidate.Ios is { ProfileName: { } pn } i ? $" Profile: {pn}{(i.ProfileVerified ? " (Apple signature verified)" : "")}{(i.ProfileExpires is { } e ? $", expires {e:yyyy-MM-dd}" : "")}." : "");
    partial void OnCandidateChanged(StoreArtifactCandidate value)
    {
        foreach (var n in new[] { nameof(Platform), nameof(Problems), nameof(HasProblems), nameof(Signing), nameof(SigningEvidence), nameof(Kind), nameof(Install), nameof(InstallDetail) }) OnPropertyChanged(n);
    }
}

public sealed record Choice(string Value, string Name);

/// <summary>
/// Ship to Store: one release of one product, with files for several platforms, uploaded to HCG Store as a private draft.
/// HCG Connect identifies the person; HCG decides whether they may publish; an approver releases it manually in HCG Store.
/// </summary>
public sealed partial class ShipToStoreViewModel(
    HcgAccountService account,
    StoreClientFactory stores,
    ISettingsStore settings,
    IProcessRunner runner,
    IDialogService dialogs,
    IPlatformShell shell,
    NavigationService navigation) : PageViewModel
{
    public override string Title => "Ship to Store";
    public override string Subtitle => "Upload a release to HCG Store as a private draft. Approval and publication happen in HCG Store, by a person.";

    [ObservableProperty] private string _storeUrl = HcgStoreClient.DefaultOrigin.ToString();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConnectionTag))] private string _connection = "Not checked";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConnectionTag))] private string _connectionTone = "neutral";
    public StatusTag ConnectionTag => new(Connection, ConnectionTone);
    [ObservableProperty] private string? _connectionDetail;
    public ObservableCollection<StoreProduct> Products { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editions), nameof(HasEditions), nameof(CanUseHcgRepo))] private StoreProduct? _selectedProduct;
    public IReadOnlyList<StoreEdition> Editions => SelectedProduct?.Editions ?? [];
    public bool HasEditions => Editions.Count > 0;
    [ObservableProperty] private StoreEdition? _selectedEdition;
    [ObservableProperty] private string _newProductId = "";
    [ObservableProperty] private string _newProductTitle = "";

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

    public ObservableCollection<ShipFileItem> Files { get; } = [];
    [ObservableProperty] private bool _isShipping;
    [ObservableProperty] private string? _releaseId;
    [ObservableProperty] private string? _reviewUrl;
    [ObservableProperty] private string _releaseStatus = "";
    [ObservableProperty] private bool _canSubmit;
    [ObservableProperty] private string? _summary;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBuildOrigin))] private string? _buildOrigin;
    public bool HasBuildOrigin => BuildOrigin is not null;
    public ObservableCollection<string> NotOffered { get; } = [];
    [ObservableProperty] private bool _hasNotOffered;
    private CancellationTokenSource? _cts;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        Distribution ??= Distributions[1];
        Eligibility ??= Eligibilities[0];
        Channel ??= Channels[0];
        var configured = (await settings.LoadAsync()).HcgStoreUrl;
        if (StoreOrigin.Parse(configured) is { } o) StoreUrl = o.ToString();
        // From a successful build: a new release with that build's verified files (not added to an earlier list).
        if (parameter is ShipFromBuild fromBuild && !IsShipping)
        {
            NewRelease();
            Files.Clear();
            NotOffered.Clear();
            BuildOrigin = $"From build: {fromBuild.Origin}. {fromBuild.Files.Count} verified file(s) offered.";
            foreach (var n in fromBuild.NotOffered) NotOffered.Add(n);
            HasNotOffered = NotOffered.Count > 0;
            if (fromBuild.Version is { Length: > 0 } v && SemVer().IsMatch(v)) Version = v;
            foreach (var p in fromBuild.Files) await AddFileAsync(p);
        }
        // From the Artifacts page: the files to ship.
        else if (parameter is IEnumerable<string> paths) foreach (var p in paths) await AddFileAsync(p);
        else if (parameter is string one) await AddFileAsync(one);
        await ConnectAsync();
    }

    private HcgStoreClient? Client()
    {
        if (StoreOrigin.Parse(StoreUrl) is not { } origin) { (Connection, ConnectionTone, ConnectionDetail) = ("Invalid address", "error", "Use an https address (http only for this computer)."); return null; }
        return stores.Create(origin);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Products.Clear();
        if (account.Status != HcgAccountStatus.SignedIn)
        {
            (Connection, ConnectionTone, ConnectionDetail) = ("Sign in required", "warning", "Sign in with HCG Connect on the Account page. HCG Store accepts only HCG accounts that HCG has allowed to publish.");
            return;
        }
        if (Client() is not { } client) return;
        try
        {
            var who = await client.WhoAmIAsync();
            foreach (var p in await client.ProductsAsync()) Products.Add(p);
            (Connection, ConnectionTone) = Products.Count > 0 ? ("Connected", "success") : ("No publishing permission", "warning");
            ConnectionDetail = Products.Count > 0
                ? $"Signed in as {who.Email ?? who.Sub}. HCG allows you to publish {Products.Count} product(s). Files up to {Format.Bytes(who.MaxUploadBytes)}."
                : $"Signed in as {who.Email ?? who.Sub}, but HCG has not granted this account permission to publish any HCG Store product ({who.PublisherPermission}).";
            await settings.SaveAsync((await settings.LoadAsync()) with { HcgStoreUrl = client.Origin.ToString() });
        }
        catch (HcgStoreException ex)
        {
            (Connection, ConnectionTone, ConnectionDetail) = (ex.Status switch { 401 => "Not accepted", 403 => "No publishing permission", 404 => "Publishing not enabled", 0 => "Unreachable", _ => "Error" }, "error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CreateDraftProductAsync()
    {
        if (Client() is not { } client) return;
        try
        {
            var p = await client.CreateDraftProductAsync(NewProductId.Trim(), NewProductTitle.Trim());
            Products.Add(p);
            SelectedProduct = p;
            await dialogs.AlertAsync("Draft product created", p.Note ?? "The draft exists only in HCG Store until HCG adds it to the catalogue.");
        }
        catch (HcgStoreException ex) { await dialogs.AlertAsync("Draft product not created", ex.Message); }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (await shell.PickFileAsync("Choose release files", "Release files", "*.exe", "*.msi", "*.msix", "*.zip", "*.dmg", "*.pkg", "*.ipa", "*.deb", "*.rpm", "*.AppImage") is { } f)
            await AddFileAsync(f);
    }

    public async Task AddFileAsync(string path)
    {
        if (Files.Any(f => f.Candidate.Path == path)) return;
        if (Directory.Exists(path) && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsMacOS()
            && await dialogs.ConfirmAsync("Package the app as a ZIP?", $"{Path.GetFileName(path)} is a folder. HCG Store takes macOS apps as ZIP archives. Package it with ditto (keeps its signature)?", "Package as ZIP"))
            path = await StoreArtifacts.ZipAppAsync(path, Path.GetDirectoryName(path)!, runner);
        var c = await Task.Run(() => StoreArtifacts.DetectAsync(path));
        var item = new ShipFileItem(c) { State = c.IsSupported ? "Checking signature…" : "Not supported", Tone = c.IsSupported ? "neutral" : "error" };
        Files.Add(item);
        if (!c.IsSupported) return;
        var signing = await Task.Run(() => StoreArtifacts.CheckSigningAsync(c, runner));
        item.Candidate = c with { Signing = signing };
        item.State = signing.Status == "invalid" ? "Signature invalid" : "Ready to upload";
        item.Tone = signing.Status == "invalid" ? "error" : "neutral";
    }

    [RelayCommand] private void RemoveFile(ShipFileItem item) { if (!IsShipping) Files.Remove(item); }

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
            Notes = CompatibilityNotes.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
        },
        Distribution = Distribution?.Value ?? "paid",
        Eligibility = Eligibility?.Value ?? "private",
        Destinations = AlsoHcgRepo && CanUseHcgRepo ? ["hcg-store", "hcg-repo"] : ["hcg-store"],
    };

    [RelayCommand]
    private async Task ShipAsync()
    {
        if (SelectedProduct is null || string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(Build) || Files.Count == 0)
        {
            await dialogs.AlertAsync("Not ready", "Choose a product, enter the version and build number, and add at least one file.");
            return;
        }
        if (Files.FirstOrDefault(f => !f.Candidate.IsSupported) is { } bad) { await dialogs.AlertAsync("Unsupported file", $"{bad.FileName}: {bad.Problems}\n\nRemove it from the list to ship the other files."); return; }
        // Public release of a file that cannot be installed on ordinary devices: say so before anything is uploaded.
        if (Eligibility?.Value == "public" && Files.Where(f => !f.Candidate.Readiness.OrdinaryInstall).ToList() is { Count: > 0 } limited
            && !await dialogs.ConfirmAsync("Not ready for ordinary installation", string.Join("\n", limited.Select(f => $"• {f.FileName}: {f.Candidate.Readiness.Label}")) +
                "\n\nYou asked for a public release. HCG Store shows these labels to approvers and customers, and refuses public approval of unsigned iOS device builds. Upload the draft anyway?", "Upload draft")) return;
        if (Client() is not { } client) return;
        IsShipping = true;
        _cts = new CancellationTokenSource();
        var service = new ShipToStoreService(client);
        var progress = new Progress<ShipItemProgress>(p =>
        {
            if (Files.FirstOrDefault(f => f.FileName == p.FileName) is not { } item) return;
            item.Progress = p.Total == 0 ? 0 : 100.0 * p.Received / p.Total;
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
            item.Tone = p.State switch { ShipItemState.Verified or ShipItemState.Skipped => "success", ShipItemState.Rejected or ShipItemState.Failed => "error", _ => "info" };
            if (p.Detail is not null) item.Detail = p.Detail;
        });
        try
        {
            var result = await service.ShipAsync(Request(), Files.Select(f => f.Candidate).ToList(), ReleaseId, progress, _cts.Token);
            ShowRelease(result.Release);
            Summary = (result.ContinuedExistingDraft ? "Continued your existing draft for this version and build (no second release was created). " : "") + (result.AllVerified
                ? $"All {result.Items.Count} file(s) are in HCG Store, verified by SHA-256. The release is a private draft until you submit it and an approver approves it in HCG Store."
                : "Some files did not make it. Fix the problem and press Retry: verified files are kept, interrupted uploads resume.");
        }
        catch (HcgStoreException ex) { Summary = ex.Message; await dialogs.AlertAsync("Ship to Store stopped", ex.Message); }
        catch (OperationCanceledException) { Summary = "Cancelled. Press Retry to resume; the bytes HCG Store confirmed are kept."; }
        finally { IsShipping = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    private void ShowRelease(StoreRelease r)
    {
        ReleaseId = r.Id;
        ReviewUrl = r.ReviewUrl;
        ReleaseStatus = $"{r.ProductTitle ?? r.ProductId} {r.Version} (build {r.Build}) · {r.Status}{(r.ApprovedEligibility is { } e ? $" · approved for {e}" : "")}";
        CanSubmit = r.Status == "draft" && r.Artifacts.Any(a => a.Status == "verified") && !r.Artifacts.Any(a => a.Status is "awaiting-upload" or "uploading");
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (ReleaseId is null || Client() is not { } client) return;
        if (!await dialogs.ConfirmAsync("Submit for review?", "The release is locked (no more files) and waits for a person with HCG's approver permission to review and approve it in HCG Store. Nothing is published automatically, and nothing is sent to TestFlight or any external store.", "Submit for review")) return;
        try { ShowRelease(await client.SubmitAsync(ReleaseId)); }
        catch (HcgStoreException ex) { await dialogs.AlertAsync("Not submitted", ex.Message); }
    }

    [RelayCommand]
    private async Task OpenReviewAsync()
    {
        if (ReviewUrl is { } u && !await shell.OpenUrlAsync(u)) await shell.CopyAsync(u);
    }

    [RelayCommand]
    private void NewRelease()
    {
        ReleaseId = null; ReviewUrl = null; ReleaseStatus = ""; Summary = null; CanSubmit = false;
        foreach (var f in Files) { f.State = "Ready to upload"; f.Progress = 0; f.Detail = null; f.Tone = "neutral"; }
    }

    [RelayCommand] private void OpenAccount() => navigation.Navigate("account");

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial System.Text.RegularExpressions.Regex SemVer();
}
