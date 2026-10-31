using System.Text.Json;

namespace HCG.ExecutableMaker.ProjectInspection.DotNet;

public sealed record GlobalJson(string? SdkVersion, string? RollForward, bool AllowPrerelease)
{
    public static GlobalJson? TryRead(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object)
                return new GlobalJson(null, null, false);
            string? Str(string name) => sdk.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var pre = sdk.TryGetProperty("allowPrerelease", out var p) && p.ValueKind == JsonValueKind.True;
            return new GlobalJson(Str("version"), Str("rollForward"), pre);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Approximates the .NET SDK resolver's roll-forward rules to tell the user up-front whether an installed SDK
    /// satisfies this global.json. The build itself remains the source of truth.
    /// </summary>
    public bool IsSatisfiedBy(IEnumerable<string> installedSdkVersions)
    {
        if (SdkVersion is null || !TryParseSdk(SdkVersion, out var req)) return true;
        var policy = (RollForward ?? "patch").ToLowerInvariant();
        foreach (var raw in installedSdkVersions)
        {
            if (!TryParseSdk(raw, out var have)) continue;
            if (raw.Contains('-') && !AllowPrerelease && !SdkVersion.Contains('-')) continue;
            var ok = policy switch
            {
                "disable" => have == req,
                "patch" or "latestpatch" => have.Major == req.Major && have.Minor == req.Minor && have.Build / 100 == req.Build / 100 && have.Build >= req.Build,
                "feature" or "latestfeature" => have.Major == req.Major && have.Minor == req.Minor && have.Build >= req.Build,
                "minor" or "latestminor" => have.Major == req.Major && (have.Minor > req.Minor || (have.Minor == req.Minor && have.Build >= req.Build)),
                "major" or "latestmajor" => have >= req,
                _ => have.Major == req.Major && have.Minor == req.Minor && have.Build >= req.Build,
            };
            if (ok) return true;
        }
        return false;
    }

    private static bool TryParseSdk(string s, out Version v) => Version.TryParse(s.Split('-')[0], out v!);
}
