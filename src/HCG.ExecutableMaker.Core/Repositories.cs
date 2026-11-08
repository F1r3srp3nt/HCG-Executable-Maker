using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Core;

public interface IProjectRepository
{
    Task UpsertAsync(ProjectRecord project, CancellationToken cancellationToken = default);
    Task<ProjectRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record BuildStatistics(int ProjectCount, int TotalBuilds, int Succeeded, int Failed, int Cancelled, int ArtifactCount);

public interface IBuildRepository
{
    Task InsertAsync(BuildRecord build, CancellationToken cancellationToken = default);
    /// <summary>Updates status/timing/counters and replaces messages and artifacts for the build.</summary>
    Task UpdateAsync(BuildRecord build, CancellationToken cancellationToken = default);
    Task<BuildRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BuildRecord>> ListAsync(Guid? projectId = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtifactRecord>> ListArtifactsAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
    Task<BuildStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

public sealed record AppSettings
{
    public string ThemeId { get; init; } = "hcg-future";
    public ImportLimits ImportLimits { get; init; } = new();
    public int BuildTimeoutMinutes { get; init; } = 30;
    public string? DefaultOutputDirectory { get; init; }
    public string? LastPublisher { get; init; }
    /// <summary>HCG's own developer directory (…/Xcode.app/Contents/Developer), passed to HCG's processes as DEVELOPER_DIR. Null = system setting.</summary>
    public string? DeveloperDirectory { get; init; }
    /// <summary>Name of a notarytool keychain profile (created with xcrun notarytool store-credentials). Only the name is stored; the secret stays in the login keychain.</summary>
    public string? NotaryKeychainProfile { get; init; }
    /// <summary>App Store Connect API key ID (for altool --validate-app). The .p8 key stays in ~/.appstoreconnect/private_keys.</summary>
    public string? AppStoreConnectKeyId { get; init; }
    public string? AppStoreConnectIssuerId { get; init; }
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
