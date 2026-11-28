using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Readiness;

/// <summary>Where a chosen tool location applies.</summary>
public abstract record ToolPathScope
{
    public sealed record AllProjects : ToolPathScope;
    /// <summary>The project's toolchain profile (created and assigned to the project if it has none).</summary>
    public sealed record Project(Guid ProjectId, string ProjectName) : ToolPathScope;
}

/// <summary>
/// Build Readiness Center: what one build (or a project as a whole) needs, checked on this computer before compilation;
/// the installation each tool resolves to, honouring the project's toolchain profile and the locations chosen for all
/// projects; how to obtain what is missing; and authorized remote build hosts that report the tool. Never installs,
/// upgrades or replaces anything by itself, and never sends source code anywhere.
/// </summary>
public sealed class BuildReadinessService(
    IProcessRunner runner,
    ISettingsStore settings,
    IToolchainService toolchains,
    IEnumerable<IBuildHost>? hosts = null,
    TimeProvider? time = null,
    ToolHost? host = null)
{
    private readonly ToolHost _host = host ?? ToolHost.Current;
    private readonly ToolResolver _resolver = new(runner, host);
    private readonly DependencyInstaller _installer = new(runner, host);
    private readonly IReadOnlyList<IBuildHost> _hosts = hosts?.ToList() ?? [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ToolResolver Resolver => _resolver;
    public DependencyInstaller Installer => _installer;

    /// <summary>Requirements of a configured build (<paramref name="build"/>) or, without one, of the project as a whole.</summary>
    public async Task<BuildReadinessReport> CheckAsync(string projectName, string sourceRoot, Guid? projectId, BuildSettings? build, ProjectAnalysis? analysis, CancellationToken ct = default)
    {
        var requirements = build is null ? BuildRequirementsInspector.ForProject(sourceRoot, analysis) : BuildRequirementsInspector.ForBuild(sourceRoot, build, analysis);
        var s = await settings.LoadAsync(ct).ConfigureAwait(false);
        var profile = ProfileOf(s, projectId);
        var items = new List<RequirementStatus>();
        foreach (var r in requirements)
            items.Add(await CheckAsync(r, s, profile, build?.Platform ?? _host.Platform, ct).ConfigureAwait(false));
        return new BuildReadinessReport
        {
            Project = projectName,
            Target = build is null ? "all builds" : $"{build.Platform} {build.Architecture}".Replace("MacOS", "macOS", StringComparison.Ordinal),
            Items = items, Profile = profile?.Name, CheckedAt = _time.GetUtcNow(),
        };
    }

    private async Task<RequirementStatus> CheckAsync(ToolRequirement r, AppSettings s, ToolchainProfile? profile, TargetPlatform target, CancellationToken ct)
    {
        if (r.ToolId == "xcode") return await XcodeAsync(r, ct).ConfigureAwait(false);
        var tool = ToolCatalog.For(r.ToolId);
        var appleOnly = r.ToolId is "lipo" && _host.Platform != TargetPlatform.MacOS;
        if (appleOnly)
            return new RequirementStatus
            {
                Requirement = r, State = ReadinessState.OtherHost, ExecutableNames = tool.Executables, OfficialUrl = tool.OfficialUrl,
                Detail = $"{tool.DisplayName} exists only on macOS; this build has to run on a Mac.",
                Remote = Remote(r, target),
            };
        var res = await _resolver.ResolveAsync(tool, r.MinimumVersion, Preferred(s, profile, tool.Id), ct).ConfigureAwait(false);
        var detail = res.State == ReadinessState.Ready
            ? res.Detail
            : $"{r.DisplayName}{(r.MinimumVersion is null ? "" : $" {r.MinimumVersion} or newer")} is {(r.Required ? "required" : "used")} by {r.DeclaredBy}: {res.Detail}";
        return new RequirementStatus
        {
            Requirement = r, State = res.State, Selected = res.Selected, Found = res.Found, Detail = detail,
            Install = res.State is ReadinessState.Ready ? [] : _installer.Options(tool, res.State, r.MinimumVersion),
            Remote = res.State is ReadinessState.Ready ? [] : Remote(r, target),
            ExecutableNames = tool.Executables, OfficialUrl = tool.OfficialUrl.Length == 0 ? null : tool.OfficialUrl,
        };
    }

    /// <summary>Xcode comes from the Toolchains page's own Xcode discovery (selection, licence, first launch, SDKs).</summary>
    private async Task<RequirementStatus> XcodeAsync(ToolRequirement r, CancellationToken ct)
    {
        if (_host.Platform != TargetPlatform.MacOS)
            return new RequirementStatus { Requirement = r, State = ReadinessState.OtherHost, Detail = "Xcode exists only on macOS; this build has to run on a Mac.", Remote = Remote(r, TargetPlatform.MacOS) };
        var report = await toolchains.GetAsync(cancellationToken: ct).ConfigureAwait(false);
        var x = report.Xcode;
        var state = x?.State switch
        {
            XcodeState.Ready => ReadinessState.Ready,
            XcodeState.NotInstalled or null => ReadinessState.NotFound,
            _ => ReadinessState.SetupRequired,
        };
        return new RequirementStatus
        {
            Requirement = r, State = state, Detail = x?.Diagnosis ?? "Xcode was not detected.",
            Selected = x?.ActiveDeveloperDirectory is { } dev && state == ReadinessState.Ready ? new ToolInstallation(dev, x.VerifiedVersion, "Xcode (Toolchains › Xcode)") : null,
            Install = state == ReadinessState.Ready ? [] : [new InstallOption { Method = InstallMethod.Official, Label = "Xcode on the Mac App Store", Url = "https://apps.apple.com/app/xcode/id497799835", Note = "Select or set it up in Toolchains › Xcode." }],
            OfficialUrl = "https://developer.apple.com/xcode/",
        };
    }

    private IReadOnlyList<RemoteBuildOption> Remote(ToolRequirement r, TargetPlatform target) =>
        _hosts.Select(h => (Host: h, Info: h.Describe()))
            .Where(x => x.Info.Kind != BuildHostKind.Local && x.Host.CannotBuild(target) is null)
            .Select(x => (x.Info, Version: x.Info.Toolchains.FirstOrDefault(t => string.Equals(t.Key, r.ToolId, StringComparison.OrdinalIgnoreCase)
                                                                                || string.Equals(t.Key, r.DisplayName, StringComparison.OrdinalIgnoreCase)).Value))
            .Where(x => x.Version is not null && (r.MinimumVersion is null || ToolResolver.Version(ToolCatalog.For(r.ToolId), x.Version) is not { } v || ToolResolver.Meets(v, r.MinimumVersion, ToolCatalog.For(r.ToolId))))
            .Select(x => new RemoteBuildOption(x.Info.Id, x.Info.Label, x.Version!, x.Info.Kind == BuildHostKind.HcgServerHost
                ? "The Build Host checks out the repository commit its owner approved on HCG Server; Executable Maker sends no source files. Nothing starts until the owner approves the job."
                : "Nothing is sent until you start a build there and confirm what is transferred."))
            .ToList();

    // ---------- Locations chosen in HCG and toolchain profiles ----------

    public static ToolchainProfile? ProfileOf(AppSettings s, Guid? projectId) =>
        projectId is { } id && s.ProjectToolchainProfiles?.GetValueOrDefault(id.ToString()) is { } pid
            ? s.ToolchainProfiles?.FirstOrDefault(p => p.Id == pid)
            : null;

    private static IReadOnlyList<PreferredTool> Preferred(AppSettings s, ToolchainProfile? profile, string toolId)
    {
        if (profile?.ToolPaths.GetValueOrDefault(toolId) is { } p) return [new PreferredTool(p, $"Profile “{profile.Name}”")];
        if (s.ToolPaths?.GetValueOrDefault(toolId) is { } g) return [new PreferredTool(g, "Chosen in HCG for all projects")];
        return [];
    }

    /// <summary>The installation a build uses for <paramref name="toolName"/> (packaging-contract prebuild steps).</summary>
    public async Task<ToolResolution> ResolveForBuildAsync(string toolName, string? minimumVersion, Guid? projectId, CancellationToken ct)
    {
        var s = await settings.LoadAsync(ct).ConfigureAwait(false);
        var tool = ToolCatalog.For(toolName);
        return await _resolver.ResolveAsync(tool, minimumVersion, Preferred(s, ProfileOf(s, projectId), tool.Id), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a location the person picked (right tool, runs, new enough) and saves it for all projects or in the
    /// project's profile. Nothing is saved when the check fails. The system PATH and shell profiles are not touched.
    /// </summary>
    public async Task<(ToolResolution Check, bool Saved)> ChooseLocationAsync(string toolId, string pickedPath, string? minimumVersion, ToolPathScope scope, CancellationToken ct = default)
    {
        var tool = ToolCatalog.For(toolId);
        var check = await _resolver.VerifyChosenAsync(tool, pickedPath, minimumVersion, ct).ConfigureAwait(false);
        if (check.State != ReadinessState.Ready || check.Selected is null) return (check, false);
        var s = await settings.LoadAsync(ct).ConfigureAwait(false);
        var path = check.Selected.Path;
        switch (scope)
        {
            case ToolPathScope.AllProjects:
                var paths = new Dictionary<string, string>(s.ToolPaths ?? [], StringComparer.Ordinal) { [tool.Id] = path };
                s = s with { ToolPaths = paths };
                break;
            case ToolPathScope.Project p:
                var (s2, profile) = EnsureProfile(s, p.ProjectId, p.ProjectName);
                var updated = profile with { ToolPaths = new Dictionary<string, string>(profile.ToolPaths, StringComparer.Ordinal) { [tool.Id] = path } };
                s = s2 with { ToolchainProfiles = s2.ToolchainProfiles!.Select(x => x.Id == updated.Id ? updated : x).ToList() };
                break;
        }
        await settings.SaveAsync(s, ct).ConfigureAwait(false);
        return (check with { Detail = check.Detail.Replace("Chosen in HCG", scope is ToolPathScope.Project pr ? $"saved for {pr.ProjectName}" : "saved for all projects", StringComparison.Ordinal) }, true);
    }

    /// <summary>Forgets a chosen location (automatic detection applies again).</summary>
    public async Task ClearLocationAsync(string toolId, ToolPathScope scope, CancellationToken ct = default)
    {
        var s = await settings.LoadAsync(ct).ConfigureAwait(false);
        var id = ToolCatalog.For(toolId).Id;
        if (scope is ToolPathScope.AllProjects && s.ToolPaths?.ContainsKey(id) == true)
            s = s with { ToolPaths = s.ToolPaths.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) };
        else if (scope is ToolPathScope.Project p && ProfileOf(s, p.ProjectId) is { } profile)
        {
            var updated = profile with { ToolPaths = profile.ToolPaths.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) };
            s = s with { ToolchainProfiles = s.ToolchainProfiles!.Select(x => x.Id == updated.Id ? updated : x).ToList() };
        }
        else return;
        await settings.SaveAsync(s, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolchainProfile>> ProfilesAsync(CancellationToken ct = default) =>
        (await settings.LoadAsync(ct).ConfigureAwait(false)).ToolchainProfiles ?? [];

    /// <summary>Uses <paramref name="profileId"/> for the project (null: HCG's global choices and automatic detection).</summary>
    public async Task AssignProfileAsync(Guid projectId, string? profileId, CancellationToken ct = default)
    {
        var s = await settings.LoadAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(s.ProjectToolchainProfiles ?? [], StringComparer.Ordinal);
        if (profileId is null) map.Remove(projectId.ToString());
        else if (s.ToolchainProfiles?.Any(p => p.Id == profileId) == true) map[projectId.ToString()] = profileId;
        else throw new InvalidOperationException("That toolchain profile no longer exists.");
        await settings.SaveAsync(s with { ProjectToolchainProfiles = map }, ct).ConfigureAwait(false);
    }

    /// <summary>A new, empty, reusable profile.</summary>
    public async Task<ToolchainProfile> CreateProfileAsync(string name, CancellationToken ct = default)
    {
        var s = await settings.LoadAsync(ct).ConfigureAwait(false);
        var profile = new ToolchainProfile { Id = Guid.NewGuid().ToString("n"), Name = UniqueName(s, name.Trim().Length == 0 ? "Toolchain profile" : name.Trim()) };
        await settings.SaveAsync(s with { ToolchainProfiles = [.. s.ToolchainProfiles ?? [], profile] }, ct).ConfigureAwait(false);
        return profile;
    }

    private static (AppSettings, ToolchainProfile) EnsureProfile(AppSettings s, Guid projectId, string projectName)
    {
        if (ProfileOf(s, projectId) is { } existing) return (s, existing);
        var profile = new ToolchainProfile { Id = Guid.NewGuid().ToString("n"), Name = UniqueName(s, projectName) };
        var map = new Dictionary<string, string>(s.ProjectToolchainProfiles ?? [], StringComparer.Ordinal) { [projectId.ToString()] = profile.Id };
        return (s with { ToolchainProfiles = [.. s.ToolchainProfiles ?? [], profile], ProjectToolchainProfiles = map }, profile);
    }

    private static string UniqueName(AppSettings s, string name)
    {
        var names = (s.ToolchainProfiles ?? []).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name)) return name;
        for (var i = 2; ; i++) if (!names.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    /// <summary>Runs an approved install option, then re-detects toolchains so the next check sees the result.</summary>
    public async Task<ProcessResult> InstallAsync(InstallOption option, bool approved, Action<string> output, CancellationToken ct = default)
    {
        var result = await _installer.InstallAsync(option, approved, output, ct).ConfigureAwait(false);
        try { await toolchains.GetAsync(refresh: true, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { output("Toolchain refresh failed: " + ex.Message); }
        return result;
    }
}
