using CommunityToolkit.Mvvm.ComponentModel;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

public enum WizardStep
{
    Import,
    Inspect,
    Configure,
    Package,
    Build,
    Results,
}

public sealed partial class StepItem(WizardStep step, string title) : ObservableObject
{
    public WizardStep Step { get; } = step;
    public string Number => ((int)Step + 1).ToString();
    public string Title { get; } = title;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
    public bool IsLast => Step == WizardStep.Results;
}

public sealed record Choice<T>(T Value, string Name, bool IsEnabled = true, string? Note = null)
{
    public override string ToString() => IsEnabled ? Name : $"{Name} — {Note}";
}

public sealed partial class PackagingOptionItem(PackagingOption option) : ObservableObject
{
    public PackagingOption Option { get; } = option;
    public string Name => Option.DisplayName;
    public string Description => Option.Description;
    public bool IsEnabled => Option.IsAvailable;
    public string? Reason => Option.Reason;
    public bool HasReason => !string.IsNullOrEmpty(Option.Reason);
    public StatusTag Status => new(Format.Label(Option.Availability), Option.IsAvailable ? "success"
        : Option.Availability == PackagingAvailability.NotImplemented ? "neutral"
        : Option.Availability == PackagingAvailability.Failed ? "error" : "warning");
    [ObservableProperty] private bool _isSelected;
}

public sealed class LogLineItem(BuildLogLine line)
{
    public string Time => line.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string Text => line.Text;
    public bool IsError => line.Kind == BuildLogKind.Error;
    public bool IsWarning => line.Kind == BuildLogKind.Warning;
    public bool IsSuccess => line.Kind == BuildLogKind.Success;
    public bool IsCommand => line.Kind == BuildLogKind.Command;
    public bool IsInfo => line.Kind == BuildLogKind.Info;
}

public sealed record SummaryRow(string Label, string Value);

public sealed record IconNote(string Text, string Tone)
{
    public bool IsWarning => Tone == "warning";
    public bool IsError => Tone == "error";
}

public sealed class ProjectChoice(DetectedProject project)
{
    public DetectedProject Project { get; } = project;
    public string Name => $"{Project.Name}  ·  {Project.ApplicationType}";
    public string Path => Project.RelativePath;
    public override string ToString() => Name;
}
