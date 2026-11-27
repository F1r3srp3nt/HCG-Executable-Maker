using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Distribution;

namespace HCG.ExecutableMaker.BuildEngine.MacXcode;

/// <summary>
/// The shared native build-provider contract (<c>hcg.native-build-request</c> / <c>hcg.native-build-result</c>, version
/// 1): what a build provider was authorized to do and what it did. Executable Maker's local Xcode provider writes both
/// files for every build. HCG Server's Build Host is the second provider of the same contract
/// (<see cref="BuildHost.HcgServerBuildHost"/>, docs/contracts/NATIVE-BUILD-PROVIDER.md); neither depends on GitHub Actions.
/// <list type="bullet">
/// <item>The request records the approval (who started it, when, and that it covers building only: no signing,
/// notarization or publication), the source provenance, the project, the actions and the limits.</item>
/// <item>The result records every step with its exit code and duration, the diagnostics counts, and every artifact with
/// its SHA-256, size and passport. A failed build is a failed result.</item>
/// </list>
/// </summary>
public static class NativeBuildContract
{
    public const string RequestFormat = "hcg.native-build-request", ResultFormat = "hcg.native-build-result";
    public const int FormatVersion = 1;
    public const string RequestFileName = "native-build-request.json", ResultFileName = "native-build-result.json";

    public sealed record StepRecord(string Title, string Command, int? ExitCode, double DurationSeconds);

    public static JsonObject Request(BuildRecord build, BuildSettings s, SourceProvenance source, string hostLabel, IReadOnlyList<string> actions, bool networkForPackages) => new()
    {
        ["format"] = RequestFormat,
        ["formatVersion"] = FormatVersion,
        ["build"] = build.Id.ToString("N"),
        ["provider"] = new JsonObject { ["kind"] = "executable-maker-local", ["host"] = hostLabel, ["dependsOnGitHubActions"] = false },
        ["approval"] = new JsonObject
        {
            ["approvedBy"] = "the person who started the build in HCG Executable Maker on this computer",
            ["approvedAt"] = build.StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["trustAcknowledged"] = build.TrustAcknowledged,
            ["scope"] = "build, test and package this source; no signing with a new identity, no notarization, no upload, no publication",
        },
        ["source"] = new JsonObject
        {
            ["repository"] = source.Repository, ["commit"] = source.Commit, ["branch"] = source.Branch, ["dirty"] = source.Dirty, ["basis"] = source.Basis,
        },
        ["project"] = new JsonObject
        {
            ["kind"] = s.MacXcode!.WorkspacePath is null ? "xcode-project" : "xcode-workspace",
            ["container"] = s.MacXcode.WorkspacePath ?? s.MacXcode.ProjectPath,
            ["scheme"] = s.MacXcode.Scheme, ["configuration"] = s.MacXcode.Configuration, ["architecture"] = s.MacXcode.Architecture.Archs(),
            ["appTarget"] = s.MacXcode.AppTarget,
        },
        ["actions"] = new JsonArray(actions.Select(a => (JsonNode?)a).ToArray()),
        ["signing"] = new JsonObject { ["mode"] = s.MacXcode.SignLocallyAdHoc ? "ad-hoc-local" : "project", ["note"] = s.MacXcode.SignLocallyAdHoc
            ? "signed ad hoc for this Mac (chosen in HCG); the project's identity was not used"
            : "Xcode signs as the project defines; HCG adds no identity" },
        ["outputs"] = new JsonArray(s.PackagingFormats.Select(f => (JsonNode?)f.ToString()).ToArray()),
        ["limits"] = new JsonObject
        {
            ["timeoutMinutes"] = (int)s.Timeout.TotalMinutes,
            ["isolatedWorkspace"] = true,
            ["workspace"] = "a copy of the imported source in HCG's per-build folder; derived data, package checkouts and result bundles stay there",
            ["network"] = networkForPackages ? "Swift package resolution only" : "not needed (no remote packages)",
            ["cpu"] = "not limited by the local provider (the HCG Server build host enforces its own)",
            ["memory"] = "not limited by the local provider (the HCG Server build host enforces its own)",
            ["projectScriptsRun"] = false,
        },
    };

    public static JsonObject Result(BuildRecord build, JsonObject request, IReadOnlyList<StepRecord> steps, XcodeDiagnostics.Report? diagnostics,
        IReadOnlyList<(string File, string Sha256, long Size, string Kind, string? Passport)> artifacts, IReadOnlyDictionary<string, string>? toolchain = null) => new()
    {
        ["format"] = ResultFormat,
        ["formatVersion"] = FormatVersion,
        ["build"] = build.Id.ToString("N"),
        ["request"] = Sha256(Serialize(request)),
        ["status"] = build.Status switch { BuildStatus.Succeeded => "succeeded", BuildStatus.Cancelled => "cancelled", BuildStatus.TimedOut => "timed-out", _ => "failed" },
        ["failureReason"] = build.Status == BuildStatus.Succeeded ? null : build.FailureReason,
        ["startedAt"] = build.StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["endedAt"] = build.EndedAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["durationSeconds"] = build.Duration is { } d ? Math.Round(d.TotalSeconds, 1) : null,
        ["toolchain"] = new JsonObject(((IEnumerable<KeyValuePair<string, string>>?)toolchain ?? build.ToolchainVersions.Where(kv => kv.Key is "xcode" or "macos-sdk" or "macos" or "host")).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["steps"] = new JsonArray(steps.Select(st => (JsonNode?)new JsonObject { ["title"] = st.Title, ["command"] = st.Command, ["exitCode"] = st.ExitCode, ["durationSeconds"] = Math.Round(st.DurationSeconds, 1) }).ToArray()),
        ["diagnostics"] = diagnostics is null ? null : new JsonObject
        {
            ["file"] = XcodeDiagnostics.JsonFileName, ["errors"] = diagnostics.Errors, ["warnings"] = diagnostics.Warnings, ["testFailures"] = diagnostics.TestFailures.Count,
            ["failedTargets"] = new JsonArray(diagnostics.FailedTargets.Select(t => (JsonNode?)t).ToArray()),
        },
        ["artifacts"] = new JsonArray(artifacts.Select(a => (JsonNode?)new JsonObject
        {
            ["file"] = a.File, ["sha256"] = a.Sha256, ["size"] = a.Size, ["kind"] = a.Kind, ["passport"] = a.Passport,
        }).ToArray()),
    };

    public static string Serialize(JsonObject o) =>
        o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
