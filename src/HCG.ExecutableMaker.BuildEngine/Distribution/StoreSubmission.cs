using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

/// <summary>
/// A Store-compatible submission manifest (<c>hcg.store-submission</c> v1, <c>store-submission.json</c>) for files
/// Executable Maker packaged. It has two parts:
/// <list type="bullet">
/// <item><c>release</c> is the exact body of HCG Store's <c>POST /api/v1/publishing/releases</c>. Eligibility is always
/// <c>private</c>, so the Store creates a private draft.</item>
/// <item>Each artifact's <c>declare</c> is the exact body of <c>POST …/releases/:id/artifacts</c>, with only the fields
/// every deployed HCG Store accepts (its schema is strict).</item>
/// </list>
/// Build provenance (source commit, build provider, toolchain, validation report) is kept apart under
/// <c>provenance</c>. It is sent only where the Store accepts it (Ship to Store v2), and it always stays in each file's
/// Release Passport. Nothing in the manifest publishes. <c>--ship-to-store --manifest</c> creates the private draft
/// from it after the file checks pass again.
/// </summary>
public static class StoreSubmission
{
    public const string Format = "hcg.store-submission";
    public const int FormatVersion = 1;
    public const string FileName = "store-submission.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<JsonObject> CreateAsync(StoreReleaseRequest release, IReadOnlyList<string> files, IProcessRunner runner, CancellationToken ct = default)
    {
        if (release.Eligibility != "private") throw new InvalidOperationException("A submission manifest asks for a private draft only; eligibility is decided at approval in HCG Store.");
        var artifacts = new JsonArray();
        var architectures = new SortedSet<string>(StringComparer.Ordinal);
        var minimumOs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var c = await StoreArtifacts.DetectAsync(Path.GetFullPath(path), ct).ConfigureAwait(false);
            if (!c.IsSupported) throw new InvalidOperationException($"{c.FileName}: {string.Join(" ", c.Problems)}");
            c = c with { Signing = await StoreArtifacts.CheckSigningAsync(c, runner, ct).ConfigureAwait(false) };
            foreach (var a in c.Architectures) architectures.Add(a);
            if (c.MinimumOs is { } m) minimumOs.TryAdd(c.Platform!, m);
            var declare = new StoreArtifactRequest
            {
                FileName = c.FileName, Platform = c.Platform!, Format = c.Format!, SizeBytes = c.SizeBytes, Sha256 = c.Sha256, Architectures = c.Architectures,
                MinimumOs = c.MinimumOs, Signing = ShipToStoreService.DeclaredSigning(c), Package = c.Package, Kind = c.Kind,
            };
            var p = c.Provenance;
            artifacts.Add(new JsonObject
            {
                ["file"] = c.FileName,
                ["declare"] = JsonSerializer.SerializeToNode(declare, Json),
                ["provenance"] = p is null || (p.SourceCommit is null && p.BuildProvider is null && p.Toolchain is null) ? null : new JsonObject
                {
                    ["sourceCommit"] = p.SourceCommit, ["buildProvider"] = p.BuildProvider, ["toolchain"] = p.Toolchain,
                    ["validationReport"] = p.ValidationReport is null ? null : JsonSerializer.SerializeToNode(p.ValidationReport, Json),
                },
                ["passport"] = p?.PassportPath is { } pp ? Path.GetFileName(pp) : null,
                ["notes"] = new JsonArray(c.Notes.Select(n => (JsonNode?)n).ToArray()),
            });
        }
        var body = release with
        {
            Compatibility = new StoreCompatibility
            {
                MinimumOs = release.Compatibility?.MinimumOs is { Count: > 0 } given ? given : minimumOs,
                Architectures = release.Compatibility?.Architectures is { Count: > 0 } a2 ? a2 : architectures.ToList(),
                Notes = release.Compatibility?.Notes ?? [],
            },
        };
        return new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["createdBy"] = "HCG Executable Maker " + HostInfo.ProductVersion,
            ["publishes"] = false,
            ["note"] = "release and each artifact's declare are HCG Store publishing API bodies (a PRIVATE draft). declare holds only the fields every deployed HCG Store accepts; " +
                       "provenance is sent where the Store accepts it (Ship to Store v2) and always stays in each file's Release Passport. Approval and publication are people's decisions in HCG Store.",
            ["release"] = JsonSerializer.SerializeToNode(body, Json),
            ["artifacts"] = artifacts,
        };
    }

    public static string Serialize(JsonObject manifest) => manifest.ToJsonString(Json) + "\n";

    /// <summary>The release body and the files (next to the manifest) with the SHA-256 and size each was declared with.</summary>
    public static (StoreReleaseRequest Release, IReadOnlyList<(string Path, string Sha256, long Size)> Files) Load(string manifestPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new InvalidOperationException("The submission manifest is empty.");
        if ((string?)root["format"] != Format) throw new InvalidOperationException($"{Path.GetFileName(manifestPath)} is not an {Format} manifest.");
        if (root["formatVersion"]?.GetValue<int>() is not FormatVersion) throw new InvalidOperationException($"{Format} version {root["formatVersion"]} is not supported by this Executable Maker (it reads version {FormatVersion}).");
        var release = root["release"].Deserialize<StoreReleaseRequest>(Json) ?? throw new InvalidOperationException("The manifest has no release.");
        if (release.Eligibility != "private") throw new InvalidOperationException("The manifest asks for a non-private draft; Ship to Store from a manifest creates private drafts only.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var files = (root["artifacts"] as JsonArray ?? []).Select(a =>
        {
            var name = (string?)a!["file"] ?? throw new InvalidOperationException("An artifact without a file name.");
            if (name.Contains('/') || name.Contains('\\') || name is "." or "..") throw new InvalidOperationException($"{name}: a manifest names files next to it only.");
            return (Path.Combine(dir, name), (string)a["declare"]!["sha256"]!, a["declare"]!["sizeBytes"]!.GetValue<long>());
        }).ToList();
        return (release, files);
    }
}
