using CommunityToolkit.Mvvm.ComponentModel;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>A page shown in the main content area.</summary>
public abstract partial class PageViewModel : ViewModelBase
{
    public abstract string Title { get; }
    public abstract string Subtitle { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    public virtual Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;
}

public static class Format
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes.Value;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
    }

    public static string Duration(TimeSpan? d) => d is null ? "—" :
        d.Value.TotalHours >= 1 ? d.Value.ToString(@"h\:mm\:ss") :
        d.Value.TotalMinutes >= 1 ? $"{(int)d.Value.TotalMinutes}m {d.Value.Seconds:00}s" : $"{d.Value.TotalSeconds:0.0}s";

    public static string When(DateTimeOffset? t)
    {
        if (t is null) return "—";
        var local = t.Value.ToLocalTime();
        var age = DateTimeOffset.Now - local;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1) && local.Date == DateTime.Today) return $"today {local:HH:mm}";
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>Maps a status to a chip style class: success, warning, error, info or neutral.</summary>
    public static string Tone(BuildStatus s) => s switch
    {
        BuildStatus.Succeeded => "success",
        BuildStatus.Failed or BuildStatus.TimedOut => "error",
        BuildStatus.Cancelled => "warning",
        BuildStatus.Running => "info",
        _ => "neutral",
    };

    public static string Tone(VerificationStatus s) => s switch
    {
        VerificationStatus.Passed => "success",
        VerificationStatus.PassedWithWarnings => "warning",
        VerificationStatus.Failed => "error",
        _ => "neutral",
    };

    public static string Tone(ToolchainStatus s) => s switch
    {
        ToolchainStatus.Installed => "success",
        ToolchainStatus.Missing or ToolchainStatus.SetupRequired or ToolchainStatus.IncompatibleVersion => "warning",
        ToolchainStatus.ValidationFailed or ToolchainStatus.InvalidPath => "error",
        _ => "neutral",
    };

    public static string Tone(SupportLevel s) => s switch
    {
        SupportLevel.Supported => "success",
        SupportLevel.MissingToolchain or SupportLevel.RequiresConfiguration or SupportLevel.RequiresDifferentHost => "warning",
        SupportLevel.Unsupported => "error",
        _ => "neutral",
    };

    public static string Label(BuildStatus s) => s switch { BuildStatus.TimedOut => "Timed out", _ => s.ToString() };

    public static string Label(VerificationStatus s) => s switch
    {
        VerificationStatus.PassedWithWarnings => "Passed with notes",
        VerificationStatus.NotVerifiable => "Not verifiable here",
        _ => s.ToString(),
    };

    public static string Label(ToolchainStatus s) => s switch
    {
        ToolchainStatus.Installed => "Ready",
        ToolchainStatus.Missing => "Not found",
        ToolchainStatus.UnsupportedOnHost => "Other host",
        ToolchainStatus.ValidationFailed => "Validation failed",
        ToolchainStatus.InvalidPath => "Invalid path",
        ToolchainStatus.IncompatibleVersion => "Incompatible version",
        ToolchainStatus.SetupRequired => "Setup required",
        _ => s.ToString(),
    };

    public static string Label(SupportLevel s) => s switch
    {
        SupportLevel.MissingToolchain => "Missing toolchain",
        SupportLevel.RequiresConfiguration => "Needs configuration",
        SupportLevel.RequiresDifferentHost => "Needs another host",
        _ => s.ToString(),
    };

    public static string Label(ProjectSourceKind k) => k switch
    {
        ProjectSourceKind.ZipArchive => "ZIP archive",
        ProjectSourceKind.Artifact => "Mobile artifact",
        _ => "Folder",
    };

    public static string Label(MobileSigningMode m) => m switch
    {
        MobileSigningMode.ProjectDefault => "Project signing configuration",
        MobileSigningMode.Unsigned => "Unsigned",
        MobileSigningMode.DebugKeystore => "HCG local debug key (testing)",
        MobileSigningMode.AppleAutomatic => "Xcode automatic signing",
        _ => "Xcode manual signing (provisioning profile)",
    };

    public static string Label(IosExportMethod m) => m switch
    {
        IosExportMethod.AppStoreConnect => "App Store Connect (export only — never uploaded)",
        IosExportMethod.ReleaseTesting => "Release testing (ad hoc)",
        IosExportMethod.Enterprise => "Enterprise",
        _ => "Development (debugging)",
    };

    public static string Label(ReadinessState s) => s switch
    {
        ReadinessState.Ready => "Ready",
        ReadinessState.NotFound => "Not found",
        ReadinessState.InvalidPath => "Invalid path",
        ReadinessState.IncompatibleVersion => "Incompatible version",
        ReadinessState.SetupRequired => "Setup required",
        ReadinessState.NotVerifiable => "Not verifiable",
        ReadinessState.NotNeeded => "Not needed for this build",
        _ => "Other host",
    };

    public static string Tone(ReadinessState s, bool required) => s switch
    {
        ReadinessState.Ready => "success",
        ReadinessState.NotNeeded or ReadinessState.OtherHost when !required => "neutral",
        ReadinessState.NotVerifiable => "info",
        _ when !required => "neutral",
        ReadinessState.InvalidPath or ReadinessState.IncompatibleVersion => "error",
        _ => "warning",
    };

    public static string Label(PackagingAvailability a) => a switch
    {
        PackagingAvailability.Available => "Ready",
        PackagingAvailability.MissingTool => "Setup required",
        PackagingAvailability.SetupRequired => "Setup required",
        PackagingAvailability.RequiresDifferentHost => "Needs another host",
        PackagingAvailability.NotImplemented => "Planned",
        PackagingAvailability.Failed => "Failed",
        _ => "Incompatible with this project",
    };
}

/// <summary>Visual state of an item: label plus the chip tone class.</summary>
public sealed record StatusTag(string Text, string Tone)
{
    public bool IsSuccess => Tone == "success";
    public bool IsWarning => Tone == "warning";
    public bool IsError => Tone == "error";
    public bool IsInfo => Tone == "info";
}
