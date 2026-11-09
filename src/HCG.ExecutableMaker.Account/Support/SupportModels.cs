using System.Text.Json.Serialization;

namespace HCG.ExecutableMaker.Account.Support;

/// <summary>
/// Request statuses. They follow HCG ONE's request status convention (Draft, Submitted, Under Review, More Information
/// Needed, In Progress, Completed, Closed), without its commercial states (Proposal Sent, Accepted).
/// </summary>
public enum SupportStatus
{
    /// <summary>Created, attachments still uploading. Not visible to staff yet.</summary>
    Draft,
    Submitted,
    UnderReview,
    MoreInformationNeeded,
    InProgress,
    Completed,
    Closed,
}

public enum SubmissionMode
{
    /// <summary>Only the diagnostics shown in the review (no project files).</summary>
    DiagnosticsOnly,
    /// <summary>Diagnostics plus the files or project archive the user selected.</summary>
    DiagnosticsAndFiles,
}

public static class SupportStatusText
{
    public static string Label(SupportStatus s) => s switch
    {
        SupportStatus.Draft => "Draft (upload not finished)",
        SupportStatus.Submitted => "Submitted",
        SupportStatus.UnderReview => "Under review",
        SupportStatus.MoreInformationNeeded => "More information needed",
        SupportStatus.InProgress => "In progress",
        SupportStatus.Completed => "Completed",
        _ => "Closed",
    };
}

/// <summary>What the user is trying to do, in their own words.</summary>
public sealed record SupportRequestDetails
{
    public required string Goal { get; init; }
    public string? LanguageFramework { get; init; }
    public string? TargetOs { get; init; }
    public string? OutputFormat { get; init; }
    public required string Problem { get; init; }
}

/// <summary>One diagnostic line the user sees before sending (already redacted).</summary>
public sealed record DiagnosticEntry(string Section, string Label, string Value);

public sealed record AttachmentManifest(string Name, long SizeBytes, string Sha256, string ContentType);

public sealed record CreateSupportRequest
{
    /// <summary>Generated once per draft: resubmitting the same draft returns the same request (duplicate protection).</summary>
    public required Guid ClientRequestId { get; init; }
    public required string Title { get; init; }
    public required SupportRequestDetails Details { get; init; }
    public required IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; }
    public required SubmissionMode Mode { get; init; }
    public IReadOnlyList<AttachmentManifest> Attachments { get; init; } = [];
    public required string AppVersion { get; init; }
}

public sealed record UploadSlot(Guid AttachmentId, string Name, Uri UploadUrl, DateTimeOffset ExpiresAt, long MaxBytes);

public sealed record SupportRequestSummary
{
    public required Guid Id { get; init; }
    public required string Reference { get; init; }
    public required string Title { get; init; }
    public required SupportStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public int StaffMessages { get; init; }
    public DateTimeOffset? LastStaffMessageAt { get; init; }
}

public sealed record CreateSupportResponse(SupportRequestSummary Request, IReadOnlyList<UploadSlot> Uploads, bool Duplicate);

public sealed record SupportAttachmentInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public long SizeBytes { get; init; }
    /// <summary>pending | received | rejected | deleted</summary>
    public required string State { get; init; }
    public string? Reason { get; init; }
    /// <summary>not_scanned | clean | flagged (server-side scanning, when HCG runs it)</summary>
    public string? Scan { get; init; }
    public DateTimeOffset? DeleteAfter { get; init; }
}

public sealed record SupportMessage
{
    public required Guid Id { get; init; }
    public required bool FromStaff { get; init; }
    public string? AuthorName { get; init; }
    public required string Body { get; init; }
    /// <summary>reply | response | more_information | status</summary>
    public required string Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record SupportRequestDetail
{
    public required SupportRequestSummary Summary { get; init; }
    public required SupportRequestDetails Details { get; init; }
    public required SubmissionMode Mode { get; init; }
    public IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; } = [];
    public IReadOnlyList<SupportAttachmentInfo> Attachments { get; init; } = [];
    public IReadOnlyList<SupportMessage> Messages { get; init; } = [];
    public string? RetentionNote { get; init; }
}

public sealed record SupportRequestList(IReadOnlyList<SupportRequestSummary> Requests);
public sealed record SupportRequestEnvelope(SupportRequestDetail Request);
public sealed record UploadSlotEnvelope(UploadSlot Upload);
public sealed record AttachmentEnvelope(SupportAttachmentInfo Attachment);
public sealed record DownloadLink(Uri Url, DateTimeOffset ExpiresAt);
