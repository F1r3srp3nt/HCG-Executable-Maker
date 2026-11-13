using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account.Support;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Account.Ai;

/// <summary>Capabilities Executable Maker can use from HCG AI. Only <see cref="ExplainBuildError"/> is offered first.</summary>
public static class AiCapabilities
{
    public const string ExplainBuildError = "explain_build_error";
    public const string SuggestBuildConfiguration = "suggest_build_configuration";
    public const string ExplainMissingToolchains = "explain_missing_toolchains";
    public const string AnalyseUnsupportedProject = "analyse_unsupported_project";

    public static string Title(string id) => id switch
    {
        ExplainBuildError => "Explain this build error",
        SuggestBuildConfiguration => "Suggest a build configuration",
        ExplainMissingToolchains => "Explain missing toolchains",
        AnalyseUnsupportedProject => "Analyse an unsupported project",
        _ => id,
    };
}

public enum AiConnectionState
{
    /// <summary>The HCG AI gateway contract is not published/verified: nothing is contacted, no response is ever simulated.</summary>
    NotConnected,
    SignedOut,
    /// <summary>HCG (ERP) has not granted AI use to this account.</summary>
    NotPermitted,
    Available,
    /// <summary>The gateway could not be reached (outage), distinct from a permission denial.</summary>
    Unavailable,
}

public sealed record AiQuota(int? Remaining, int? Limit, DateTimeOffset? ResetsAt);
public sealed record AiCapability(string Id, string Title, bool Available, string? Reason, AiQuota? Quota);
public sealed record AiStatus(AiConnectionState State, string Message, IReadOnlyList<AiCapability> Capabilities);

/// <summary>Exactly what would be sent for one AI action. Shown to the user, who must approve it first.</summary>
public sealed record AiPayload
{
    public required string Capability { get; init; }
    public required IReadOnlyList<DiagnosticEntry> Items { get; init; }
    /// <summary>The user's own question, if any.</summary>
    public string? Question { get; init; }
    public long SizeBytes => Encoding.UTF8.GetByteCount(ToJson());

    /// <summary>
    /// Wire form. Build logs and project text are sent as data fields marked <c>untrusted</c>; the gateway, not this
    /// app, holds the instructions, and nothing in the logs can change what the request is allowed to do.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        capability = Capability,
        question = Question,
        context = Items.Select(i => new { section = i.Section, label = i.Label, value = i.Value, untrusted = true }),
        client = new { product = "executable-maker", contract = "hcg.executable-maker.ai/1" },
    }, HcgApiClient.Json);
}

/// <summary>A suggestion for the user to review. Executable Maker never runs, applies or installs anything from it.</summary>
public sealed record AiSuggestion(string Title, string Detail);
public sealed record AiResult(string Explanation, IReadOnlyList<AiSuggestion> Suggestions, string? UsageNote);

public enum AiFailure { NotConnected, NotPermitted, QuotaExhausted, Unavailable, Rejected, Cancelled }

public sealed class AiException(AiFailure failure, string message) : Exception(message)
{
    public AiFailure Failure { get; } = failure;
}

/// <summary>
/// The HCG AI gateway (ai.hakmicapitalgroup.com). HCG identity supplies the token, ERP decides permission and quota, the
/// gateway executes. An implementation must fetch capabilities from the gateway's verified contract; Executable Maker
/// never decides AI access itself and holds no provider key.
/// </summary>
public interface IHcgAiGateway
{
    string Description { get; }
    Task<AiStatus> GetStatusAsync(CancellationToken ct);
    Task<AiResult> RunAsync(AiPayload approvedPayload, CancellationToken ct);
}

/// <summary>
/// Used while the HCG AI gateway has no published, verified contract (the situation today: HCG AI appears only as a
/// website link in HCG's apps and documents). Shows "Not connected" and never fabricates a response.
/// </summary>
public sealed class NotConnectedAiGateway : IHcgAiGateway
{
    public const string Reason =
        "HCG AI is not connected yet. Executable Maker is prepared for the HCG AI gateway (ai.hakmicapitalgroup.com), but HCG has not " +
        "published its service contract (capability discovery, request format, quotas). Until it does, nothing is sent and no AI answer is shown.";

    public string Description => "Not connected";

    public Task<AiStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(new AiStatus(AiConnectionState.NotConnected, Reason,
        new[] { AiCapabilities.ExplainBuildError, AiCapabilities.SuggestBuildConfiguration, AiCapabilities.ExplainMissingToolchains, AiCapabilities.AnalyseUnsupportedProject }
            .Select(id => new AiCapability(id, AiCapabilities.Title(id), false, "Not connected", null)).ToList()));

    public Task<AiResult> RunAsync(AiPayload approvedPayload, CancellationToken ct) => throw new AiException(AiFailure.NotConnected, Reason);
}

/// <summary>Builds the minimal, redacted payload for each capability.</summary>
public static class AiPayloadBuilder
{
    public static AiPayload ExplainBuildError(BuildRecord build, IReadOnlyList<string> logLines, string? question = null) => new()
    {
        Capability = AiCapabilities.ExplainBuildError,
        Items = DiagnosticsCollector.FromBuild(build, logLines),
        Question = question is null ? null : DiagnosticsCollector.Scrub(question, 1000),
    };

    public static AiPayload AnalyseUnsupportedProject(ProjectAnalysis analysis, HostEnvironment host, string appVersion) => new()
    {
        Capability = AiCapabilities.AnalyseUnsupportedProject,
        Items = DiagnosticsCollector.FromAnalysis(analysis, host, appVersion),
    };

    public static AiPayload ExplainMissingToolchains(ToolchainReport report) => new()
    {
        Capability = AiCapabilities.ExplainMissingToolchains,
        Items = DiagnosticsCollector.FromToolchains(report.Toolchains.Where(t => !t.IsAvailable && t.Status != ToolchainStatus.UnsupportedOnHost) is var missing
            ? report with { Toolchains = missing.ToList() } : report),
    };

    public static AiPayload SuggestBuildConfiguration(ProjectAnalysis analysis, HostEnvironment host, string appVersion) => new()
    {
        Capability = AiCapabilities.SuggestBuildConfiguration,
        Items = DiagnosticsCollector.FromAnalysis(analysis, host, appVersion),
    };
}

/// <summary>
/// Combines the account, ERP App Access (permission <c>ai.use</c>) and the gateway's own capability list. The server
/// side (ERP + gateway) enforces permission and quota again on every call; this only decides what to show.
/// </summary>
public sealed class AiAssistant(IHcgAiGateway gateway, AppAccessService access, HcgAccountService account)
{
    public bool IsConnected => gateway is not NotConnectedAiGateway;

    public async Task<AiStatus> StatusAsync(CancellationToken ct = default)
    {
        var status = await gateway.GetStatusAsync(ct).ConfigureAwait(false);
        if (status.State == AiConnectionState.NotConnected) return status;
        if (!account.IsSignedIn) return status with { State = AiConnectionState.SignedOut, Message = "Sign in with HCG to use HCG AI assistance." };
        if (access.State == AppAccessState.Unavailable) return status with { State = AiConnectionState.Unavailable, Message = access.Message };
        if (!access.Has(HcgPermissions.AiUse))
            return status with { State = AiConnectionState.NotPermitted, Message = "HCG has not granted AI assistance to your account. Access and quotas are managed by HCG (ERP)." };
        return status;
    }

    /// <summary>Runs an action the user approved after reviewing <paramref name="payload"/>. Never runs anything from the answer.</summary>
    public async Task<AiResult> RunAsync(AiPayload payload, bool userApproved, CancellationToken ct = default)
    {
        if (!userApproved) throw new AiException(AiFailure.Rejected, "Review and approve what will be sent first.");
        var status = await StatusAsync(ct).ConfigureAwait(false);
        var capability = status.Capabilities.FirstOrDefault(c => c.Id == payload.Capability);
        return status.State switch
        {
            AiConnectionState.NotConnected => throw new AiException(AiFailure.NotConnected, status.Message),
            AiConnectionState.SignedOut or AiConnectionState.NotPermitted => throw new AiException(AiFailure.NotPermitted, status.Message),
            AiConnectionState.Unavailable => throw new AiException(AiFailure.Unavailable, status.Message),
            _ when capability is not { Available: true } => throw new AiException(AiFailure.NotPermitted, capability?.Reason ?? "This AI capability is not available to your account."),
            _ when capability.Quota is { Remaining: <= 0 } => throw new AiException(AiFailure.QuotaExhausted,
                $"Your HCG AI quota for this is used up{(capability.Quota.ResetsAt is { } r ? $"; it resets {r.LocalDateTime:g}" : "")}."),
            _ => await gateway.RunAsync(payload, ct).ConfigureAwait(false),
        };
    }
}
