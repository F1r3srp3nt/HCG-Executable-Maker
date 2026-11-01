using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed class ProjectItem(ProjectRecord project)
{
    public ProjectRecord Project { get; } = project;
    public string Name => Project.Name;
    public string Source => Project.SourceLocation;
    public string SourceKind => Format.Label(Project.SourceKind);
    public string Imported => Format.When(Project.ImportedAt);
    public string LastOpened => Format.When(Project.LastOpenedAt ?? Project.ImportedAt);
    public string Workspace => Project.WorkspacePath;
    public string Summary => Project.Analysis is { } a ? $"{a.Language} · {a.ApplicationType}" : "Not inspected";
    public string Framework => Project.Analysis?.Framework ?? "—";
    public StatusTag Status => Project.Analysis is { } a ? new StatusTag(Format.Label(a.Support), Format.Tone(a.Support)) : new StatusTag("Not inspected", "neutral");
}

public sealed class BuildItem(BuildRecord build)
{
    public BuildRecord Build { get; } = build;
    public string ProjectName => Build.ProjectName;
    public string AppName => Build.Settings.Identity.ApplicationName;
    public string Target => Build.Settings.Mobile is { } m ? $"{m.Output.DisplayName()}" : $"{Build.Settings.Platform.DisplayName()} {Build.Settings.Architecture.DisplayName()}";
    public string Rid => Build.Settings.Mobile is { } m ? m.Framework.DisplayName() : Build.Settings.RuntimeIdentifier ?? "—";
    public string Version => Build.Settings.Identity.Version;
    public string Started => Format.When(Build.StartedAt);
    public string Duration => Format.Duration(Build.Duration);
    public StatusTag Status => new(Format.Label(Build.Status), Format.Tone(Build.Status));
    public string Counts => $"{Build.WarningCount} warning{(Build.WarningCount == 1 ? "" : "s")} · {Build.ErrorCount} error{(Build.ErrorCount == 1 ? "" : "s")}";
    public string ArtifactSummary => Build.Artifacts.Count == 0 ? "No artifacts" : $"{Build.Artifacts.Count} artifact{(Build.Artifacts.Count == 1 ? "" : "s")}";
    public string? FailureReason => Build.FailureReason;
    public bool HasFailure => !string.IsNullOrEmpty(Build.FailureReason) && Build.Status != BuildStatus.Succeeded;
    public string Formats => Build.Settings.Mobile is { } m ? $"{m.Output.DisplayName()} · signing: {Format.Label(m.Signing)}" : string.Join(", ", Build.Settings.PackagingFormats.Select(f => f.DisplayName()));
    public IReadOnlyList<ArtifactItem> Artifacts => Build.Artifacts.Select(a => new ArtifactItem(a, Build)).ToList();
    public IReadOnlyList<MessageItem> Messages => Build.Messages.Select(m => new MessageItem(m)).ToList();
    public bool HasMessages => Build.Messages.Count > 0;
    public string Toolchains => string.Join(" · ", Build.ToolchainVersions.Select(kv => $"{kv.Key} {kv.Value}"));
}

public sealed class ArtifactItem(ArtifactRecord artifact, BuildRecord? build = null)
{
    public ArtifactRecord Artifact { get; } = artifact;
    public string Name => Artifact.FileName;
    public string Path => Artifact.Path;
    public string Kind => Artifact.Kind == ArtifactKind.Directory ? "Folder" : "File";
    public string FormatName => Artifact.Format.DisplayName();
    public string Size => Format.Bytes(Artifact.SizeBytes);
    public string Sha256 => Artifact.Sha256;
    public string ShortSha => Artifact.Sha256.Length > 16 ? Artifact.Sha256[..16] + "…" : Artifact.Sha256;
    public string ChecksumLabel => Artifact.Kind == ArtifactKind.Directory ? "SHA-256 (file manifest)" : "SHA-256";
    public string Created => Format.When(Artifact.CreatedAt);
    public string? Origin => build is null ? null : $"{build.ProjectName} · {build.Settings.Mobile?.Output.DisplayName() ?? build.Settings.RuntimeIdentifier}";
    public bool Exists => Artifact.Kind == ArtifactKind.Directory ? Directory.Exists(Artifact.Path) : File.Exists(Artifact.Path);
    public StatusTag Verification => Exists
        ? new StatusTag(Format.Label(Artifact.Verification), Format.Tone(Artifact.Verification))
        : new StatusTag("Missing on disk", "error");
    public IReadOnlyList<CheckItem> Checks => Artifact.Checks.Select(c => new CheckItem(c)).ToList();
}

public sealed class CheckItem(VerificationCheck check)
{
    public string Name => check.Name;
    public string Detail => check.Detail;
    public StatusTag Status => new(Format.Label(check.Status), Format.Tone(check.Status));
}

public sealed class MessageItem(BuildMessage message)
{
    public BuildMessage Message { get; } = message;
    public string Text => Message.Text;
    public string Code => Message.Code ?? "";
    public string Location => Message.File is null ? "" : Message.Line is { } l ? $"{System.IO.Path.GetFileName(Message.File)}:{l}" : System.IO.Path.GetFileName(Message.File);
    public bool IsError => Message.Severity == DiagnosticSeverity.Error;
    public bool IsWarning => Message.Severity == DiagnosticSeverity.Warning;
    public StatusTag Severity => new(Message.Severity.ToString(), Message.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" });
}

public sealed class DiagnosticItem(AnalysisDiagnostic d)
{
    public string Message => d.Message;
    public string? Path => d.Path;
    public bool HasPath => !string.IsNullOrEmpty(d.Path);
    public StatusTag Severity => new(d.Severity.ToString(), d.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" });
}

public sealed class ToolchainItem(ToolchainInfo info)
{
    public ToolchainInfo Info { get; } = info;
    public string Name => Info.DisplayName;
    public string Category => Info.Category;
    public string Version => Info.Version ?? "—";
    public string Location => Info.ExecutablePath ?? "—";
    public string? Detail => Info.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Info.Detail);
    public string? Hint => Info.Status is ToolchainStatus.Missing or ToolchainStatus.ValidationFailed ? Info.InstallHint : null;
    public bool HasHint => !string.IsNullOrEmpty(Hint);
    public string Others => Info.AdditionalVersions.Count > 1 ? "All installed: " + string.Join(", ", Info.AdditionalVersions) : "";
    public bool HasOthers => Info.AdditionalVersions.Count > 1;
    public StatusTag Status => new(Format.Label(Info.Status), Format.Tone(Info.Status));
}

public sealed record ToolchainGroup(string Category, IReadOnlyList<ToolchainItem> Items);

public sealed class RequirementItem(ToolchainRequirement r)
{
    public string Name => r.DisplayName;
    public string Detail => r.Detail ?? "";
    public StatusTag Status => r.IsSatisfied ? new StatusTag("Satisfied", "success") : new StatusTag("Missing", "warning");
}
