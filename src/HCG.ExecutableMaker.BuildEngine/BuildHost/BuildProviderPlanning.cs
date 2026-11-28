using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.BuildHost;

/// <summary>Where a New Build runs.</summary>
public enum BuildProviderKind
{
    /// <summary>This computer, through Executable Maker's own build worker (the existing wizard path).</summary>
    LocalMachine,
    /// <summary>HCG Server's Build Host (<c>hcg.buildhost/1</c>), through <see cref="HcgServerBuildHost"/>.</summary>
    HcgServerBuildHost,
    /// <summary>GitHub Actions: recorded as evidence by HCG Repo; not started from the New Build wizard (not implemented).</summary>
    GitHubActions,
}

/// <summary>One entry of the Build Provider selector.</summary>
public sealed record BuildProviderOption(BuildProviderKind Kind, string Id, string Label, string Description, bool Implemented)
{
    public override string ToString() => Implemented ? Label : Label + " — not available yet";
}

public static class BuildProviderCatalog
{
    public static readonly BuildProviderOption LocalMachine = new(BuildProviderKind.LocalMachine, BuildProviderIds.ExecutableMakerLocal, "Local Machine",
        "Builds on this computer with its own toolchains, after you confirm that you trust the project. Nothing leaves this computer.", true);
    public static readonly BuildProviderOption HcgServer = new(BuildProviderKind.HcgServerBuildHost, BuildProviderIds.HcgServerBuildHost, "HCG Server Build Host",
        "Builds on HCG Server's Build Host inside its sandbox. The Build Host checks out a pinned commit from the source its owner approved; " +
        "Executable Maker sends no source files. You authorize each build here and the HCG Server owner approves it there. Its output is unsigned.", true);
    public static readonly BuildProviderOption GitHubActions = new(BuildProviderKind.GitHubActions, BuildProviderIds.GitHubActions, "GitHub Actions",
        "Not available in the New Build wizard yet. A GitHub Actions run is used as evidence in HCG Repo (run ID, artifact and repository); it is not started from here.", false);

    public static IReadOnlyList<BuildProviderOption> All { get; } = [LocalMachine, HcgServer, GitHubActions];
}

/// <summary>
/// Where an imported project's code lives in Git, as far as the Build Host is concerned: the original working copy,
/// the project's folder inside it, the GitHub repository (OWNER/NAME) of its <c>origin</c>, and the commit recorded at
/// import (the commit the Build Host will be asked to build).
/// </summary>
public sealed record BuildHostSource(string? RepositoryRoot, string RelativePath, string? Repository, string? Commit, RevisionStatus Revision)
{
    public bool HasPinnedCommit => Commit is { Length: 40 } c && c.All(Uri.IsHexDigit);

    /// <summary>Reads the source from the project's import record (import-revision.json) and the original folder's .git.</summary>
    public static BuildHostSource For(ProjectRecord project)
    {
        var revision = ImportRevisions.Check(project);
        var commit = revision.Recorded?.Commit?.ToLowerInvariant();
        if (revision.Recorded is { Kind: "git", Origin: { } origin } && GitRevision.Repository(origin) is { } repo)
        {
            var rel = Path.GetRelativePath(repo.Root, Path.GetFullPath(origin)).Replace('\\', '/');
            return new(repo.Root, rel == "." ? "" : rel, GitRevision.GitHubRepository(repo.OriginUrl), commit, revision);
        }
        return new(null, "", null, commit, revision);
    }
}

public enum BuildHostMatchQuality
{
    /// <summary>The Build Host's approved remote is this working copy and the project folder is the same.</summary>
    SameWorkingCopy,
    /// <summary>Same GitHub repository (OWNER/NAME) and the same project folder.</summary>
    SameRepository,
    /// <summary>Same repository, another folder inside it.</summary>
    SameRepositoryOtherFolder,
    /// <summary>Nothing in common: chosen by hand.</summary>
    Unrelated,
}

/// <summary>A project registered on the Build Host, and how it relates to the imported project.</summary>
public sealed record BuildHostProjectMatch(BuildProject Project, BuildHostMatchQuality Quality, string Reason)
{
    public bool IsMatch => Quality is BuildHostMatchQuality.SameWorkingCopy or BuildHostMatchQuality.SameRepository;
    public override string ToString() => $"{Project.Name} ({Project.Id}) · {Project.Provider}{(IsMatch ? " · matches this project" : "")}";
}

/// <summary>What Executable Maker knows about a Build Host it can reach, after <see cref="BuildProviderPlanning.ConnectAsync"/>.</summary>
public enum BuildHostConnectionState
{
    Connected,
    /// <summary>No build-client token is stored yet.</summary>
    NoToken,
    /// <summary>HCG Server refused the token (unknown or revoked).</summary>
    Unauthorized,
    /// <summary>Not HCG Server's Build Host, or another major version of the contract.</summary>
    ContractMismatch,
    /// <summary>Nothing answered on the loopback address (HCG Server not running).</summary>
    Unreachable,
    /// <summary>The address is not a loopback address: refused before any request.</summary>
    NotLoopback,
}

public sealed record BuildHostConnection(BuildHostConnectionState State, string Message, BuildHostStatus? Status = null, IReadOnlyList<BuildProject>? Projects = null)
{
    public bool IsConnected => State == BuildHostConnectionState.Connected;
}

/// <summary>
/// The Build Provider part of the New Build wizard that does not need a UI: discovering and testing the Build Host,
/// matching the imported project to a registered Build Host project, what it can build, and the request — the
/// existing <see cref="BuildHostBuildRequest"/> that <see cref="HcgServerBuildHost.RunAsync"/> turns into the shared
/// <c>native-build-request.json</c> / <c>native-build-result.json</c>. No separate execution protocol.
/// </summary>
public static class BuildProviderPlanning
{
    /// <summary>
    /// Authorized discovery: the configured loopback address (HCG Server's Build Host is never reached over the network),
    /// read with this computer's build-client token. Connected only when the server identifies as <c>hcg.buildhost/1</c>
    /// and accepts the token.
    /// </summary>
    public static async Task<BuildHostConnection> ConnectAsync(HcgBuildHostClient client, CancellationToken ct)
    {
        if (!HcgBuildHostClient.IsLoopbackHost(client.BaseUrl.Host))
            return new(BuildHostConnectionState.NotLoopback, $"{client.BaseUrl} is not a loopback address. HCG Server's Build Host is reached on this computer only.");
        if (string.IsNullOrWhiteSpace(await client.Credentials.ReadAsync(ct).ConfigureAwait(false)))
            return new(BuildHostConnectionState.NoToken,
                "No build-client token is stored. On HCG Server, the owner runs `hcg-agent build client add executable-maker` and gives you the token it prints once; enter it here (it goes to " +
                client.Credentials.StoreDescription + ", never to a file).");
        try
        {
            var status = await client.GetStatusAsync(ct).ConfigureAwait(false);
            var projects = await client.GetProjectsAsync(ct).ConfigureAwait(false);
            return new(BuildHostConnectionState.Connected,
                $"Connected to {client.BaseUrl} ({BuildHostContract.Contract}) · isolation {status.Isolation}{(status.Enforced ? " (enforced)" : " (NOT enforced)")} · {projects.Count} registered project(s) · queue {status.Queued}{(status.Running is { } r ? $", running {r}" : "")}." +
                (status.Refusal is { } refusal ? " " + refusal : ""),
                status, projects);
        }
        catch (HcgBuildHostException ex)
        {
            return ex.Failure switch
            {
                BuildHostFailure.Unauthorized => new(BuildHostConnectionState.Unauthorized, "HCG Server refused the stored build-client token (unknown or revoked). Ask the owner for a new one (`hcg-agent build client add executable-maker`)."),
                BuildHostFailure.ContractMismatch => new(BuildHostConnectionState.ContractMismatch, ex.Message),
                BuildHostFailure.NotConfigured => new(BuildHostConnectionState.NoToken, ex.Message),
                _ => new(BuildHostConnectionState.Unreachable, $"HCG Server's Build Host did not answer at {client.BaseUrl}: {ex.Message} Start HCG Server on this computer, then test again."),
            };
        }
    }

    /// <summary>Registered projects, best match first: the same working copy, then the same GitHub repository and folder.</summary>
    public static IReadOnlyList<BuildHostProjectMatch> Match(BuildHostSource source, IEnumerable<BuildProject> projects) =>
        projects.Select(p => Classify(source, p)).OrderBy(m => m.Quality).ThenBy(m => m.Project.Id, StringComparer.Ordinal).ToList();

    private static BuildHostProjectMatch Classify(BuildHostSource source, BuildProject p)
    {
        var folder = (p.Path ?? "").Trim('/');
        var sameFolder = string.Equals(folder, source.RelativePath.Trim('/'), StringComparison.Ordinal);
        var sameRepo = source.Repository is not null && string.Equals(p.Source.Repository, source.Repository, StringComparison.OrdinalIgnoreCase);
        var sameCopy = source.RepositoryRoot is not null && p.Source.Remote.Length > 0 && !p.Source.Remote.Contains("://", StringComparison.Ordinal)
                       && SamePath(p.Source.Remote, source.RepositoryRoot);
        var where = folder.Length == 0 ? "the repository root" : folder;
        if (sameCopy && sameFolder)
            return new(p, BuildHostMatchQuality.SameWorkingCopy, $"The owner approved this working copy ({p.Source.Remote}) and folder {where} as the project's source.");
        if (sameRepo && sameFolder)
            return new(p, BuildHostMatchQuality.SameRepository, $"Same repository ({p.Source.Repository}) and folder {where}; the Build Host fetches from {p.Source.Remote}.");
        if (sameRepo || sameCopy)
            return new(p, BuildHostMatchQuality.SameRepositoryOtherFolder, $"Same repository, but the Build Host builds {where}, not {(source.RelativePath.Length == 0 ? "the repository root" : source.RelativePath)}.");
        return new(p, BuildHostMatchQuality.Unrelated, $"{p.Source.Repository} ({where}) is not where this project was imported from.");
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            static string N(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[1..].TrimStart('/', '\\')) : p));
            return string.Equals(N(a), N(b), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// What a Build Host provider produces. The Go, .NET, Node, Python and SwiftPM providers build for the Build Host's
    /// own operating system and architecture (hcg.buildhost/1 has no cross-compilation option); the Xcode provider
    /// builds for Apple platforms by destination. <paramref name="host"/> is from the last verified provenance, if any.
    /// </summary>
    public static string Targets(string provider, KnownBuildHost? host)
    {
        var own = host is null ? "the Build Host's own operating system and architecture (shown after its first verified build)" : $"{host.OperatingSystem}/{host.Architecture} (the Build Host's own)";
        return provider switch
        {
            "xcode" => "iOS, iPadOS and macOS by destination (generic/platform=iOS, iOS Simulator, platform=macOS); unsigned (CODE_SIGNING_ALLOWED=NO)",
            "node" => $"{own}; npm pack produces a platform-independent package unless the project builds native modules",
            "python" => $"{own}; pip wheel builds a pure-Python wheel unless the project has extensions",
            _ => own,
        };
    }

    /// <summary>Operations a project may run, in the order the Build Host runs them, filtered to what the owner allowed.</summary>
    public static IReadOnlyList<string> AllowedOperations(BuildProject project) =>
        new[] { BuildOperations.Resolve, BuildOperations.Build, BuildOperations.Test, BuildOperations.Package }.Where(project.Operations.Contains).ToList();

    /// <summary>
    /// The statement the person authorizes before anything is submitted. The Build Host fetches the commit itself from
    /// the source the owner approved: Executable Maker transfers no source files. Built per request so a different
    /// project, commit or operation needs a new authorization.
    /// </summary>
    public static string AuthorizationStatement(BuildProject project, string commit, IReadOnlyList<string> operations, Uri host, string? keyFingerprint) =>
        $"I authorize HCG Server's Build Host at {host.GetLeftPart(UriPartial.Authority)}" +
        (keyFingerprint is { Length: > 0 } fp ? $" (device key {HcgBuildHostClient.NormalizeFingerprint(fp)[..16]}…)" : "") +
        $" to fetch commit {commit[..Math.Min(12, commit.Length)]} of {project.Source.Repository} ({(string.IsNullOrEmpty(project.Path) ? "repository root" : project.Path)}) from the source its owner approved ({project.Source.Remote}), " +
        $"and to run {string.Join(", ", operations)} with the {project.Provider} provider inside its sandbox. Executable Maker sends no source files. " +
        "The owner must still approve the job on HCG Server. The output is unsigned.";

    /// <summary>The request <see cref="HcgServerBuildHost.RunAsync"/> runs, for an imported project.</summary>
    public static BuildHostBuildRequest Request(ProjectRecord project, BuildProject hostProject, string commit, IReadOnlyList<string> operations,
        BuildHostOptions options, string outputDirectory, string version)
    {
        commit = commit.Trim().ToLowerInvariant();
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
            throw new ArgumentException("The Build Host builds a full 40-character commit; the imported copy has none recorded. Import the project from a Git working copy.", nameof(commit));
        var unknown = operations.Except(AllowedOperations(hostProject)).ToList();
        if (operations.Count == 0 || unknown.Count > 0)
            throw new ArgumentException(unknown.Count > 0 ? $"The owner did not allow {string.Join(", ", unknown)} for {hostProject.Id}." : "Choose at least one operation.", nameof(operations));
        var platform = TargetPlatformOf(hostProject.Provider);
        return new BuildHostBuildRequest
        {
            ProjectId = project.Id, ProjectName = project.Name,
            Settings = new BuildSettings
            {
                ProjectRelativePath = string.IsNullOrEmpty(hostProject.Path) ? "." : hostProject.Path,
                Platform = platform ?? HostInfo.CurrentPlatform, Architecture = HostInfo.CurrentArchitecture, OutputDirectory = outputDirectory,
                Identity = new AppIdentity { ApplicationName = project.Name, Version = version, ApplicationId = "hcg.buildhost." + hostProject.Id },
            },
            BuildHostProjectId = hostProject.Id, Commit = commit, Operations = operations, Options = options,
            Purpose = $"HCG Executable Maker New Build of {project.Name} ({hostProject.Source.Repository}) at {commit[..12]}",
        };
    }

    private static TargetPlatform? TargetPlatformOf(string provider) => provider == "xcode" ? TargetPlatform.IOS : null;

    /// <summary>What Executable Maker remembers about a Build Host after a verified provenance (never the token).</summary>
    public static KnownBuildHost Remember(Uri baseUrl, VerifiedProvenance provenance, DateTimeOffset now) => new()
    {
        BaseUrl = baseUrl.ToString(), KeyFingerprint = provenance.KeyFingerprint, DeviceId = provenance.Document.BuildHost.DeviceId,
        DeviceName = provenance.Document.BuildHost.Name, AgentVersion = provenance.Document.BuildHost.AgentVersion,
        OperatingSystem = provenance.Document.BuildHost.Os, Architecture = provenance.Document.BuildHost.Architecture,
        IsolationBackend = provenance.Document.Isolation.Backend, IsolationRules = provenance.Document.Isolation.Rules,
        IsolationGaps = provenance.Document.Isolation.Gaps ?? [], LastVerifiedAt = now,
    };

    /// <summary>Whether the key that signed a provenance is the one pinned for this Build Host.</summary>
    public static (SigningCheckOutcome Outcome, string Detail) KeyCheck(string signedBy, string? pinned) =>
        pinned is not { Length: > 0 } p
            ? (SigningCheckOutcome.Warning, $"Signed by HCG Server key {Group(signedBy)}. No key is pinned yet: compare it with `hcg-agent fingerprint` on HCG Server, then pin it so a different key is refused.")
            : HcgBuildHostClient.NormalizeFingerprint(p) == HcgBuildHostClient.NormalizeFingerprint(signedBy)
                ? (SigningCheckOutcome.Passed, $"Signed by the pinned HCG Server key {Group(signedBy)}.")
                : (SigningCheckOutcome.Failed, $"Signed by {Group(signedBy)}, not the pinned key {Group(p)}.");

    /// <summary>"go 1.25.4" style: the tool's name, then its version line unless it already names the tool ("go version go1.25.4 linux/amd64").</summary>
    public static string ToolVersion(BuildToolchain t) =>
        t.Version.StartsWith(t.Tool, StringComparison.OrdinalIgnoreCase) ? t.Version : $"{t.Tool} {t.Version}";

    /// <summary>A fingerprint in groups of four, as <c>hcg-agent fingerprint</c> prints it.</summary>
    public static string Group(string fingerprint)
    {
        var f = HcgBuildHostClient.NormalizeFingerprint(fingerprint).ToUpperInvariant();
        return string.Join(' ', Enumerable.Range(0, (f.Length + 3) / 4).Select(i => f.Substring(i * 4, Math.Min(4, f.Length - i * 4))));
    }
}
