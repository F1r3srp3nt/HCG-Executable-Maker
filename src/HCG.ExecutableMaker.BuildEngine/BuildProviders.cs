using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.BuildEngine;

/// <summary>
/// Where a build runs. Interchangeable options: Executable Maker's own worker on this computer
/// (<see cref="LocalBuildProvider"/>, the existing <see cref="BuildService"/> path), a GitHub Actions run
/// (<see cref="GitHubActionsBuildProvider"/>), or HCG Server's Build Host
/// (<see cref="BuildHost.HcgBuildHostProvider"/>). <see cref="Id"/> is the provider id HCG Repo records
/// (<see cref="BuildProviderIds"/>); only the GitHub providers have a CI run.
/// </summary>
public interface IBuildProvider
{
    string Id { get; }
    string DisplayName { get; }
    IsolationLevel Isolation { get; }
    /// <summary>The isolation and trust facts, shown to the user verbatim.</summary>
    string Description { get; }
    /// <summary>Who decides that the project's code may run.</summary>
    string Approval { get; }
    /// <summary>Whether the build's evidence includes a CI run (run ID, artifact, run repository).</summary>
    bool HasCiRun => BuildProviderIds.HasCiRun(Id);
}

/// <summary>Executable Maker's own build worker on this computer (<see cref="IBuildWorker"/>, <see cref="BuildService"/>).</summary>
public sealed class LocalBuildProvider(IBuildWorker worker) : IBuildProvider
{
    public string Id => BuildProviderIds.ExecutableMakerLocal;
    public string DisplayName => "This computer";
    public IsolationLevel Isolation => worker.Isolation;
    public string Description => worker.Description;
    public string Approval => "You, in Executable Maker: " + TrustPolicy.AcknowledgementLabel;
    public IBuildWorker Worker => worker;
}

/// <summary>
/// A GitHub Actions run in the project's repository. Executable Maker does not start it; the person packaging records
/// the run ID, artifact and run repository, which HCG Repo then pins (<c>github-artifact</c> import). Unchanged by the
/// Build Host.
/// </summary>
public sealed class GitHubActionsBuildProvider(bool privateRepository = false) : IBuildProvider
{
    public string Id => privateRepository ? BuildProviderIds.GitHubActionsPrivate : BuildProviderIds.GitHubActions;
    public string DisplayName => privateRepository ? "GitHub Actions (private repository)" : "GitHub Actions";
    public IsolationLevel Isolation => IsolationLevel.Isolated;
    public string Description => "A GitHub-hosted runner builds the pinned commit; the uploaded artifact is pinned by run ID, artifact name and SHA-256.";
    public string Approval => "The repository's workflow and whoever starts the run.";
}

/// <summary>The build providers Executable Maker offers, by id.</summary>
public sealed class BuildProviderCatalog(IEnumerable<IBuildProvider> providers)
{
    public IReadOnlyList<IBuildProvider> All { get; } = providers.ToList();

    public IBuildProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
