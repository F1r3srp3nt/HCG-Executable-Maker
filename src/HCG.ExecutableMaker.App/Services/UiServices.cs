using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform.Storage;
using HCG.ExecutableMaker.App.Views.Dialogs;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.App.Services;

public sealed class MainWindowProvider
{
    public Window? Window { get; set; }
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", bool destructive = false);
    Task AlertAsync(string title, string message);
    Task ShowLogAsync(string title, string path);
}

public sealed class DialogService(MainWindowProvider owner) : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", bool destructive = false)
    {
        if (owner.Window is null) return false;
        var dialog = new ConfirmDialog(title, message, confirmText, "Cancel", destructive);
        return await dialog.ShowDialog<bool>(owner.Window);
    }

    public async Task AlertAsync(string title, string message)
    {
        if (owner.Window is null) return;
        await new ConfirmDialog(title, message, "OK", null, false).ShowDialog<bool>(owner.Window);
    }

    public async Task ShowLogAsync(string title, string path)
    {
        if (owner.Window is null) return;
        string text;
        try
        {
            text = File.Exists(path) ? await File.ReadAllTextAsync(path) : $"The log file no longer exists:\n{path}";
        }
        catch (IOException ex) { text = $"The log could not be read: {ex.Message}"; }
        await new LogViewerDialog(title, path, text).ShowDialog(owner.Window);
    }
}

/// <summary>OS integration: pickers, clipboard and opening folders. All paths are opened, never executed.</summary>
public interface IPlatformShell
{
    Task<string?> PickZipAsync();
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickIconAsync();
    /// <summary>Picks a file matching the given patterns (e.g. *.apk). Returns a local path or null.</summary>
    Task<string?> PickFileAsync(string title, string typeName, params string[] patterns);
    Task CopyAsync(string text);
    Task<bool> OpenFolderAsync(string path);
}

public sealed class PlatformShell(MainWindowProvider owner) : IPlatformShell
{
    private TopLevel? Top => owner.Window;

    public async Task<string?> PickZipAsync()
    {
        if (Top is null) return null;
        var files = await Top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import ZIP archive",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("ZIP archives") { Patterns = ["*.zip"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Top is null) return null;
        var folders = await Top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickIconAsync()
    {
        if (Top is null) return null;
        var files = await Top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose application icon",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Icons (PNG, ICO, ICNS)") { Patterns = ["*.png", "*.ico", "*.icns"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string typeName, params string[] patterns)
    {
        if (Top is null) return null;
        var files = await Top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeName)
            {
                Patterns = patterns,
                AppleUniformTypeIdentifiers = patterns switch
                {
                    ["*.app"] => ["com.apple.application-bundle"],
                    ["*.xcodeproj"] => ["com.apple.xcode.project"],
                    _ => null,
                },
            }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task CopyAsync(string text)
    {
        if (Top?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public async Task<bool> OpenFolderAsync(string path)
    {
        if (Top is null) return false;
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return false;
        return await Top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
    }
}

public sealed class NavigationService
{
    public event Action<string, object?>? NavigationRequested;
    public void Navigate(string key, object? parameter = null) => NavigationRequested?.Invoke(key, parameter);
}

public sealed record ThemeOption(string Id, string Name, string Description, bool IsPreview);

/// <summary>Swaps the design-token dictionary (merged dictionary index 0) at runtime.</summary>
public sealed class ThemeService(ISettingsStore settings)
{
    public static readonly IReadOnlyList<ThemeOption> Themes =
    [
        new("hcg-future", "HCG Future", "Electric cyan on deep graphite — the default HCG interface.", false),
        new("hcg-classic", "HCG Classic", "Understated, low-glow professional palette. Preview: refinement planned.", true),
    ];

    public string CurrentId { get; private set; } = "hcg-future";

    public async Task ApplySavedAsync()
    {
        var s = await settings.LoadAsync();
        Apply(s.ThemeId);
    }

    public void Apply(string id)
    {
        if (Themes.All(t => t.Id != id)) id = "hcg-future";
        var app = Application.Current;
        if (app is null) return;
        var file = id == "hcg-classic" ? "Tokens.HcgClassic.axaml" : "Tokens.HcgFuture.axaml";
        var include = new ResourceInclude(new Uri("avares://HCGExecutableMaker/App.axaml")) { Source = new Uri($"avares://HCGExecutableMaker/Themes/{file}") };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = include; else merged.Add(include);
        CurrentId = id;
    }
}
