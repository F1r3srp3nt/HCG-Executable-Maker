using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine.BuildHost;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>An operation the Build Host can run for the selected project (resolve, build, test, package).</summary>
public sealed partial class BuildHostOperationChoice(string operation, string description, Action changed) : ObservableObject
{
    public string Operation { get; } = operation;
    public string Description { get; } = description;
    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => changed();
}

/// <summary>
/// The HCG Server Build Host part of the New Build wizard's Build Provider selector: authorized discovery of the Build
/// Host on this computer (loopback, this computer's build-client token), a connection test, its toolchains, providers,
/// isolation and limits, the registered project that matches the imported one, the pinned commit, operations and
/// options, and the explicit authorization the person gives before anything is submitted. Running uses the existing
/// <see cref="HcgServerBuildHost"/> (the native build-provider contract); nothing here is a second protocol.
/// </summary>
public sealed partial class BuildHostPanelViewModel : ViewModelBase
{
    private readonly ISettingsStore _settings;
    private readonly BuildHostCredentials _credentials;
    private readonly HcgServerBuildHost _defaultHost;
    private readonly IAppPaths _paths;
    private readonly IBuildRepository _builds;
    private readonly IPlatformShell _shell;
    private readonly FeatureAccessService _features;
    private ProjectRecord? _project;
    private HcgBuildHostClient? _client;
    private bool _resetting;

    public BuildHostPanelViewModel(ISettingsStore settings, BuildHostCredentials credentials, HcgServerBuildHost defaultHost, IAppPaths paths,
        IBuildRepository builds, IPlatformShell shell, FeatureAccessService features, BuildReadinessPanelViewModel readiness)
    {
        _settings = settings;
        _credentials = credentials;
        _defaultHost = defaultHost;
        _paths = paths;
        _builds = builds;
        _shell = shell;
        _features = features;
        Readiness = readiness;
        var access = features.Decide(MakerFeatures.BuildHostServer);
        AccessLabel = $"{MakerFeatures.BuildHostServer.Title}: {access.StateLabel}. {MakerFeatures.BuildHostServer.Description}";
    }

    /// <summary>Toolchain Manager: what the project needs on this computer, and which tools the Build Host has instead.</summary>
    public BuildReadinessPanelViewModel Readiness { get; }
    public string AccessLabel { get; }
    public string StoreDescription => _credentials.StoreDescription;

    // ── Connection ──
    [ObservableProperty] private string _baseUrl = HcgBuildHostSettings.DefaultBaseUrl.ToString();
    [ObservableProperty] private string _keyPin = "";
    [ObservableProperty] private string _tokenEntry = "";
    [ObservableProperty] private bool _hasToken;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private StatusTag _connectionTag = new("Not tested", "neutral");
    [ObservableProperty] private string _connectionMessage = "Test the connection to HCG Server's Build Host on this computer.";
    [ObservableProperty] private bool _isConnected;
    public BuildHostConnection? Connection { get; private set; }

    // ── Host ──
    [ObservableProperty] private string _hostSummary = "";
    [ObservableProperty] private string _hostIdentity = "";
    [ObservableProperty] private string _hostKey = "";
    [ObservableProperty] private StatusTag _keyTag = new("Key not known yet", "neutral");
    [ObservableProperty] private bool _canPinKey;
    public ObservableCollection<SummaryRow> Toolchains { get; } = [];
    public ObservableCollection<SummaryRow> Providers { get; } = [];
    public ObservableCollection<string> Isolation { get; } = [];
    public string Limits =>
        "One job at a time. Each command runs inside HCG Server's sandbox with a timeout, a memory limit for the whole process tree and a file-size limit, " +
        "set by HCG Server (hcg.buildhost/1 does not publish the values). Writes only in the job's own workspace; no network except resolve with the " +
        "network.dependencies permission; no signing, no physical devices. Measured time, peak memory and CPU per step are in the result.";

    // ── Project and source ──
    public ObservableCollection<BuildHostProjectMatch> Projects { get; } = [];
    [ObservableProperty] private BuildHostProjectMatch? _selectedProject;
    [ObservableProperty] private string _matchReason = "";
    [ObservableProperty] private string _targets = "";
    [ObservableProperty] private string _providerState = "";
    [ObservableProperty] private string _sourceSummary = "";
    [ObservableProperty] private string _revisionDetail = "";
    [ObservableProperty] private string _commit = "";
    public BuildHostSource? Source { get; private set; }
    public ObservableCollection<BuildHostOperationChoice> Operations { get; } = [];
    public IReadOnlyList<string> Configurations { get; } = ["(provider default)", "debug", "release"];
    [ObservableProperty] private string _selectedConfiguration = "(provider default)";
    [ObservableProperty] private bool _hasConfiguration;
    [ObservableProperty] private string _products = "";
    [ObservableProperty] private string _version = "0.0.0";

    // ── Authorization ──
    [ObservableProperty] private string _authorizationText = "";
    [ObservableProperty] private bool _authorized;
    [ObservableProperty] private string _blocker = "Test the connection first.";
    public bool CanStart => Blocker.Length == 0;

    partial void OnSelectedProjectChanged(BuildHostProjectMatch? value)
    {
        Operations.Clear();
        if (value is null) { MatchReason = Targets = ProviderState = ""; Update(); return; }
        MatchReason = value.Reason;
        var known = Known();
        Targets = BuildProviderPlanning.Targets(value.Project.Provider, known);
        var provider = Connection?.Status?.Providers.FirstOrDefault(p => p.Id == value.Project.Provider);
        var tool = Connection?.Status?.Toolchains.Where(t => t.Provider == value.Project.Provider).Select(BuildProviderPlanning.ToolVersion).ToList() ?? [];
        ProviderState = provider is null ? $"The Build Host does not list the {value.Project.Provider} provider."
            : provider.Available ? $"{value.Project.Provider} provider available on the Build Host{(tool.Count > 0 ? ": " + string.Join(", ", tool) : "")}."
            : $"{value.Project.Provider} provider unavailable on the Build Host: {provider.Detail}";
        HasConfiguration = value.Project.Provider is "swiftpm" or "xcode" or "dotnet";
        foreach (var op in BuildProviderPlanning.AllowedOperations(value.Project))
            Operations.Add(new BuildHostOperationChoice(op, op switch
            {
                BuildOperations.Resolve => "Resolve dependencies (the only step with network, and only with the network.dependencies permission)",
                BuildOperations.Build => "Compile",
                BuildOperations.Test => "Run the tests",
                _ => "Package the output as artifacts",
            }, Update) { IsSelected = op is BuildOperations.Build or BuildOperations.Package });
        Update();
    }

    partial void OnAuthorizedChanged(bool value) => Update();
    partial void OnSelectedConfigurationChanged(string value) => ResetAuthorization();
    partial void OnProductsChanged(string value) => ResetAuthorization();
    partial void OnBlockerChanged(string value) => OnPropertyChanged(nameof(CanStart));

    /// <summary>Loads the imported project's source and the saved Build Host settings, then tests the connection.</summary>
    public async Task LoadAsync(ProjectRecord project)
    {
        _project = project;
        var s = await _settings.LoadAsync();
        BaseUrl = s.BuildHostUrl ?? _defaultHost.Client.BaseUrl.ToString();
        KeyPin = s.BuildHostKeyPin ?? _defaultHost.Client.Settings.ExpectedKeyFingerprint ?? "";
        Source = BuildHostSource.For(project);
        Commit = Source.Commit ?? "";
        SourceSummary = Source.Repository is { } r
            ? $"{r}{(Source.RelativePath.Length > 0 ? " · " + Source.RelativePath : "")} · working copy {Source.RepositoryRoot}"
            : Source.RepositoryRoot is { } root ? $"Working copy {root} (its origin is not a GitHub repository)" : "Not imported from a Git working copy.";
        RevisionDetail = Source.Revision.Detail;
        Version = "0.0.0";
        await TestConnectionAsync();
    }

    [RelayCommand]
    private async Task SaveTokenAsync()
    {
        try
        {
            await _credentials.SaveAsync(TokenEntry);
            TokenEntry = "";
            ConnectionMessage = $"Token stored in {_credentials.StoreDescription}." + (_credentials.IsPersistent ? "" : " This store keeps it until Executable Maker quits (no OS credential store was found).");
            await TestConnectionAsync();
        }
        catch (ArgumentException ex) { ConnectionMessage = ex.Message; TokenEntry = ""; }
    }

    [RelayCommand]
    private async Task ForgetTokenAsync()
    {
        await _credentials.ForgetAsync();
        await TestConnectionAsync();
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        try
        {
            HasToken = !string.IsNullOrWhiteSpace(await _credentials.ReadAsync());
            OnPropertyChanged(nameof(StoreDescription));
            HcgBuildHostSettings settings;
            try
            {
                settings = new HcgBuildHostSettings { BaseUrl = HcgBuildHostClient.ValidateBaseUrl(new Uri(BaseUrl.Trim().TrimEnd('/') + "/")), ExpectedKeyFingerprint = KeyPin.Trim() is { Length: > 0 } p ? p : null };
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException)
            {
                Connection = new BuildHostConnection(BuildHostConnectionState.NotLoopback, ex.Message);
                await ShowAsync();
                return;
            }
            _client = SameAsDefault(settings) ? _defaultHost.Client : new HcgBuildHostClient(HcgBuildHostClient.CreateHttpClient(), _credentials, settings);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Connection = await BuildProviderPlanning.ConnectAsync(_client, cts.Token);
            if (Connection.IsConnected && SameAsDefault(settings))
                try { await _defaultHost.RefreshAsync(cts.Token); } catch (HcgBuildHostException) { } // Build Readiness then offers the Build Host's toolchains
            var s = await _settings.LoadAsync();
            var url = settings.BaseUrl.ToString();
            if (s.BuildHostUrl != (url == HcgBuildHostSettings.DefaultBaseUrl.ToString() ? null : url) || (s.BuildHostKeyPin ?? "") != KeyPin.Trim())
                await _settings.SaveAsync(s with { BuildHostUrl = url == HcgBuildHostSettings.DefaultBaseUrl.ToString() ? null : url, BuildHostKeyPin = KeyPin.Trim() is { Length: > 0 } k ? k : null });
            await ShowAsync();
            // Toolchain Manager: re-checked with what the Build Host now reports, so a tool missing or too old here is
            // shown next to the Build Host's compatible version.
            if (_project is { } project) _ = Readiness.CheckAsync(project.Name, project.SourceDirectory, project.Id, null, project.Analysis);
        }
        finally { IsTesting = false; }
    }

    private bool SameAsDefault(HcgBuildHostSettings s) =>
        s.BaseUrl == _defaultHost.Client.BaseUrl && (s.ExpectedKeyFingerprint ?? "") == (_defaultHost.Client.Settings.ExpectedKeyFingerprint ?? "");

    private KnownBuildHost? _known;
    private KnownBuildHost? Known() => _known;

    private async Task ShowAsync()
    {
        var c = Connection!;
        IsConnected = c.IsConnected;
        ConnectionTag = c.State switch
        {
            BuildHostConnectionState.Connected => new("Connected", "success"),
            BuildHostConnectionState.NoToken => new("Token needed", "warning"),
            BuildHostConnectionState.Unauthorized => new("Token refused", "error"),
            BuildHostConnectionState.ContractMismatch => new("Not hcg.buildhost/1", "error"),
            BuildHostConnectionState.NotLoopback => new("Not loopback", "error"),
            _ => new("Not reachable", "warning"),
        };
        ConnectionMessage = c.Message;
        Toolchains.Clear();
        Providers.Clear();
        Isolation.Clear();
        var s = await _settings.LoadAsync();
        _known = s.KnownBuildHosts?.FirstOrDefault(k => k.BaseUrl == _client?.BaseUrl.ToString());
        if (c.Status is { } st)
        {
            HostSummary = $"{st.Contract} · isolation {st.Isolation}{(st.Enforced ? " (enforced)" : " (NOT enforced on that computer: nothing will be submitted)")} · queue {st.Queued}{(st.Running is { } r ? $" · running {r}" : "")}";
            foreach (var t in st.Toolchains.GroupBy(t => t.Tool).Select(g => g.First())) Toolchains.Add(new(t.Tool, $"{t.Version} · {t.Path}"));
            foreach (var p in st.Providers) Providers.Add(new(p.Id, (p.Available ? "available" : "unavailable") + $" — {p.Detail}"));
        }
        else HostSummary = "";
        HostIdentity = _known is { } k
            ? $"{k.DeviceName} ({k.DeviceId}) · {k.OperatingSystem}/{k.Architecture} · hcg-agent {k.AgentVersion} · last verified {k.LastVerifiedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Device, operating system and architecture are read from the signed provenance of the first verified build (hcg.buildhost/1's status does not include them).";
        if (_known is not null)
        {
            Isolation.Add($"{_known.IsolationBackend}:");
            foreach (var rule in _known.IsolationRules) Isolation.Add("• " + rule);
            foreach (var gap in _known.IsolationGaps) Isolation.Add("Gap: " + gap);
        }
        else if (c.Status is { } st2) Isolation.Add($"{st2.Isolation}{(st2.Enforced ? " (enforced)" : " (not enforced)")}; its rules are listed in the signed provenance of each build.");
        ShowKey();
        Projects.Clear();
        if (c.Projects is { } projects && Source is { } src)
            foreach (var m in BuildProviderPlanning.Match(src, projects)) Projects.Add(m);
        SelectedProject = Projects.FirstOrDefault(p => p.IsMatch);
        if (SelectedProject is null) OnSelectedProjectChanged(null);
    }

    private void ShowKey()
    {
        var pin = KeyPin.Trim();
        if (_known is null)
        {
            HostKey = pin.Length > 0 ? $"Pinned: {BuildProviderPlanning.Group(pin)}" : "Not known yet: the key that signs the first build's provenance is shown here.";
            KeyTag = pin.Length > 0 ? new("Pinned", "info") : new("Not pinned", "neutral");
            CanPinKey = false;
            return;
        }
        var (outcome, detail) = BuildProviderPlanning.KeyCheck(_known.KeyFingerprint, pin);
        HostKey = detail;
        KeyTag = outcome switch
        {
            SigningCheckOutcome.Passed => new("Pinned key verified", "success"),
            SigningCheckOutcome.Failed => new("Different key", "error"),
            _ => new("Verified, not pinned", "warning"),
        };
        CanPinKey = pin.Length == 0;
    }

    /// <summary>Pins the key that signed the last verified provenance: from now on, provenance signed by any other key is refused.</summary>
    [RelayCommand]
    private async Task PinKeyAsync()
    {
        if (_known is null) return;
        KeyPin = BuildProviderPlanning.Group(_known.KeyFingerprint);
        await TestConnectionAsync();
    }

    [RelayCommand]
    private async Task CopyApprovalCommandAsync(string? jobId)
    {
        if (jobId is { Length: > 0 }) await _shell.CopyAsync($"hcg-agent build approve {jobId} --confirm {jobId}");
    }

    private void ResetAuthorization()
    {
        if (_resetting) return;
        _resetting = true;
        try { Authorized = false; }
        finally { _resetting = false; }
        Update();
    }

    private IReadOnlyList<string> SelectedOperations() => Operations.Where(o => o.IsSelected).Select(o => o.Operation).ToList();

    private void Update()
    {
        var p = SelectedProject?.Project;
        var ops = SelectedOperations();
        var newText = p is null || Source?.HasPinnedCommit != true || _client is null ? ""
            : BuildProviderPlanning.AuthorizationStatement(p, Commit, ops, _client.BaseUrl, _known?.KeyFingerprint ?? (KeyPin.Length > 0 ? KeyPin : null));
        if (newText != AuthorizationText)
        {
            AuthorizationText = newText;
            if (Authorized && !_resetting) { _resetting = true; Authorized = false; _resetting = false; }
        }
        var provider = p is null ? null : Connection?.Status?.Providers.FirstOrDefault(x => x.Id == p.Provider);
        Blocker = Connection is not { IsConnected: true } ? "Connect to HCG Server's Build Host first (Test connection)."
            : Connection.Status is { Enforced: false } ? "HCG Server's isolation is not enforced on that computer: Executable Maker does not submit builds to it."
            : p is null ? (Projects.Count == 0 ? "No project is registered on the Build Host. The owner registers one with `hcg-agent build project add --id … --repo OWNER/NAME --remote <working copy> --provider …`." : "Choose the Build Host project to build.")
            : provider is not { Available: true } ? ProviderState
            : Source?.HasPinnedCommit != true ? "The imported copy has no recorded 40-character commit, so there is nothing pinned to build. Import the project from a Git working copy."
            : ops.Count == 0 ? "Choose at least one operation."
            : !Authorized ? "Authorize this build (the checkbox above) to submit it."
            : "";
    }

    /// <summary>The request and the provider that runs it. Only when <see cref="CanStart"/>.</summary>
    public (HcgServerBuildHost Provider, BuildHostBuildRequest Request) Prepare(string outputDirectory)
    {
        Update();
        if (!CanStart || _project is null || _client is null || SelectedProject is null) throw new InvalidOperationException(Blocker);
        var options = new BuildHostOptions
        {
            Configuration = HasConfiguration && SelectedConfiguration is "debug" or "release" ? SelectedConfiguration : null,
            Products = Products.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } pr ? pr : null,
        };
        var request = BuildProviderPlanning.Request(_project, SelectedProject.Project, Commit, SelectedOperations(), options, outputDirectory, Version);
        var provider = ReferenceEquals(_client, _defaultHost.Client) ? _defaultHost : new HcgServerBuildHost(_client, _paths, TimeProvider.System, _builds);
        // Each authorization covers one submission.
        _resetting = true;
        Authorized = false;
        _resetting = false;
        return (provider, request);
    }

    /// <summary>After a build: remembers the Build Host's device, platform, isolation and signing key from verified provenance.</summary>
    public async Task RecordAsync(BuildHostOutcome outcome)
    {
        if (outcome.Provenance is not { } pv || _client is null) { Update(); return; }
        var s = await _settings.LoadAsync();
        var known = BuildProviderPlanning.Remember(_client.BaseUrl, pv, DateTimeOffset.UtcNow);
        var list = (s.KnownBuildHosts ?? []).Where(k => k.BaseUrl != known.BaseUrl).Append(known).ToList();
        await _settings.SaveAsync(s with { KnownBuildHosts = list });
        _known = known;
        await ShowAsync();
    }

    public KnownBuildHost? KnownHost => _known;
}
