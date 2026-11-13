using HCG.ExecutableMaker.Packaging.Icons;

namespace HCG.ExecutableMaker.BuildEngine.Electron;

/// <summary>
/// The app icon for a generated desktop project: a square PNG of at least 512×512 (electron-builder makes the macOS
/// .icns from it) or an .icns file. Checked from the file header only.
/// </summary>
public static class DesktopIcon
{
    public const int Minimum = 512;

    public sealed record Result(string? Problem, string Summary, bool IsIcns);

    public static Result Check(string path)
    {
        if (!File.Exists(path)) return new($"The icon {path} does not exist.", "", false);
        var info = IconInspector.Inspect(path);
        if (info.Errors.Count > 0) return new($"{Path.GetFileName(path)} is not a usable icon: {string.Join(" ", info.Errors)}", "", false);
        switch (info.Format)
        {
            case IconFormat.Icns:
                var big = info.Largest;
                return big is null ? new($"{Path.GetFileName(path)} contains no images.", "", true) : new(null, $"ICNS, up to {big.Width}×{big.Height}", true);
            case IconFormat.Png:
                var img = info.Images[0];
                if (img.Width != img.Height) return new($"{Path.GetFileName(path)} is {img.Width}×{img.Height}; the app icon must be square.", "", false);
                if (img.Width < Minimum) return new($"{Path.GetFileName(path)} is {img.Width}×{img.Height}; macOS app icons need at least {Minimum}×{Minimum} (1024×1024 is best).", "", false);
                return new(null, $"PNG {img.Width}×{img.Height}{(info.HasTransparency == true ? ", transparent" : "")}", false);
            default:
                return new($"{Path.GetFileName(path)} is {(info.Format == IconFormat.Ico ? "a Windows .ico" : "not a PNG or ICNS file")}. Use a square PNG of at least {Minimum}×{Minimum} or an .icns file.", "", false);
        }
    }

    /// <summary>The best icon the web project already has (largest square PNG of at least 512×512, or an .icns), if any.</summary>
    public static string? FindInProject(string projectDir)
    {
        var candidates = new List<string>();
        foreach (var folder in new[] { "build", "public", "assets", "src/assets", "resources", "icons" })
        {
            var full = Path.Combine(projectDir, folder);
            if (!Directory.Exists(full)) continue;
            candidates.AddRange(Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icns", StringComparison.OrdinalIgnoreCase))
                .Where(f => new FileInfo(f).Length < 10_000_000));
        }
        return candidates
            .Select(f => (Path: f, Check: Check(f), Size: IconInspector.Inspect(f).Largest?.Width ?? 0))
            .Where(c => c.Check.Problem is null)
            .OrderByDescending(c => c.Check.IsIcns)
            .ThenByDescending(c => c.Size)
            .ThenBy(c => Path.GetFileName(c.Path).Contains("icon", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .Select(c => c.Path)
            .FirstOrDefault();
    }
}
