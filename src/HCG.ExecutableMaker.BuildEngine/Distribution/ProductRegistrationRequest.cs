using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

/// <summary>
/// The central registration request for a new product: what HCG needs to register it centrally and add it to HCG Store's
/// reviewed catalogue (name, description, category, platforms, edition, initial version, icon and banner). HCG Store's
/// draft product holds only the descriptive fields; the central product ID, editions, official artwork and listing are
/// HCG's decisions. This writes a folder for the person to send to HCG. It uploads nothing and grants nothing.
/// </summary>
public static partial class ProductRegistrationRequest
{
    public const string Format = "hcg.product-registration-request";
    public const int FormatVersion = 1;

    public sealed record Input(string Id, string Title, string Description, string Category, IReadOnlyList<string> Platforms, string Edition, string InitialVersion,
        string? IconPath, string? BannerPath, string? RequestedBy, string? StoreOrigin);

    public static IReadOnlyList<string> Problems(Input i)
    {
        var p = new List<string>();
        if (!IdRe().IsMatch(i.Id)) p.Add("Product ID: lowercase letters, digits and dashes (e.g. hcg-notes), at most 60 characters.");
        if (i.Title.Length is 0 or > 120) p.Add("Name: 1–120 characters.");
        if (i.Description.Length > 2000) p.Add("Description: at most 2,000 characters.");
        if (i.Category.Length > 0 && !CategoryRe().IsMatch(i.Category)) p.Add("Category: lowercase letters, digits and dashes (e.g. productivity).");
        if (i.InitialVersion.Length > 0 && !SemVerRe().IsMatch(i.InitialVersion)) p.Add("Initial version: Semantic Versioning (e.g. 0.1.0).");
        foreach (var (label, path) in new[] { ("Icon", i.IconPath), ("Banner", i.BannerPath) })
            if (path is not null && !File.Exists(path)) p.Add($"{label}: {path} does not exist.");
        return p;
    }

    /// <summary>Writes <c>&lt;folder&gt;/&lt;id&gt;-registration-request/</c> with request.json and copies of the artwork. Returns the JSON path.</summary>
    public static async Task<string> WriteAsync(string folder, Input i, TimeProvider? time = null, CancellationToken ct = default)
    {
        if (Problems(i) is { Count: > 0 } problems) throw new ArgumentException(string.Join(" ", problems));
        var dir = Path.Combine(folder, $"{i.Id}-registration-request");
        Directory.CreateDirectory(dir);
        async Task<JsonObject?> Art(string? path, string name)
        {
            if (path is null) return null;
            var target = Path.Combine(dir, name + Path.GetExtension(path).ToLowerInvariant());
            File.Copy(path, target, overwrite: true);
            await using var s = File.OpenRead(target);
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
            return new JsonObject { ["file"] = Path.GetFileName(target), ["sha256"] = sha, ["size"] = new FileInfo(target).Length, ["note"] = "proposed artwork; HCG decides the official artwork" };
        }
        var json = new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["createdAt"] = (time ?? TimeProvider.System).GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["requestedBy"] = i.RequestedBy,
            ["store"] = i.StoreOrigin,
            ["product"] = new JsonObject
            {
                ["id"] = i.Id, ["name"] = i.Title, ["description"] = i.Description.Length > 0 ? i.Description : null,
                ["category"] = i.Category.Length > 0 ? i.Category : null,
                ["platforms"] = new JsonArray(i.Platforms.Select(p => (JsonNode)p).ToArray()),
                ["edition"] = i.Edition.Length > 0 ? i.Edition : null,
                ["initialVersion"] = i.InitialVersion.Length > 0 ? i.InitialVersion : null,
            },
            ["artwork"] = new JsonObject { ["icon"] = await Art(i.IconPath, "icon").ConfigureAwait(false), ["banner"] = await Art(i.BannerPath, "banner").ConfigureAwait(false) },
            ["decidedByHcg"] = new JsonArray("central product ID", "editions", "official artwork", "listing in the reviewed catalogue", "prices and licences", "access"),
            ["note"] = "A request for HCG. Not uploaded by HCG Executable Maker; nothing is published and no access is granted.",
        };
        var path = Path.Combine(dir, "request.json");
        await File.WriteAllTextAsync(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        return path;
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")] private static partial Regex IdRe();
    [GeneratedRegex(@"^[a-z0-9-]{1,40}$")] private static partial Regex CategoryRe();
    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")] private static partial Regex SemVerRe();
}
