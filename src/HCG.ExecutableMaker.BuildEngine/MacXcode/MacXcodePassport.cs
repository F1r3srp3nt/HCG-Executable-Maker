using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.BuildEngine.MacXcode;

/// <summary>
/// The Release Passport build half for each artifact of a successful native macOS Xcode build. It records:
/// <list type="bullet">
/// <item>the exact file (SHA-256, size), bundle ID, version and build number, architecture and the Xcode that built it;</item>
/// <item>the source commit as Git states it;</item>
/// <item><c>code_signing</c> from the Signing Doctor: unsigned, ad hoc, development, Developer ID or notarized,
/// hardened runtime, team, entitlements and every embedded binary;</item>
/// <item>the artifact's verification checks and the test result.</item>
/// </list>
/// Written as <c>&lt;artifact&gt;.release-passport.json</c>, where Ship to Store finds it (StoreProvenance).
/// </summary>
public static class MacXcodePassport
{
    public static JsonObject CodeSigning(MacSigningDoctor.Report r, string appName)
    {
        var nested = r.Nested;
        return new JsonObject
        {
            ["scope"] = ReleasePassport.CodeSigningScope,
            ["method_claimed"] = r.Status,
            ["binaries"] = 1 + nested.Count,
            ["all_signed"] = r.SignatureKind != MacSigningDoctor.Kind.Unsigned && nested.All(n => n.Kind != MacSigningDoctor.Kind.Unsigned),
            ["entitlements"] = new JsonObject { [appName] = new JsonArray(r.Entitlements.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()) },
            ["status"] = r.Status,
            ["authority"] = r.Authority,
            ["team_id"] = r.TeamId,
            ["hardened_runtime"] = r.HardenedRuntime,
            ["secure_timestamp"] = r.SecureTimestamp,
            ["app_sandbox"] = r.AppSandbox,
            ["signature_valid"] = r.SignatureValid,
            ["gatekeeper"] = r.Gatekeeper,
            ["notarization"] = r.Notarization,
            ["notarized"] = r.Notarized,
            ["details"] = new JsonArray(nested.Select(n => (JsonNode?)new JsonObject
            {
                ["path"] = n.Path, ["signer"] = MacSigningDoctor.Describe(n.Kind), ["authority"] = n.Authority, ["team_id"] = n.TeamId,
                ["hardened_runtime"] = n.HardenedRuntime, ["verified"] = n.Valid,
            }).ToArray()),
            ["findings"] = new JsonArray(r.Findings.Select(f => (JsonNode?)f).ToArray()),
        };
    }

    private static bool TestsPassed(string verdict) =>
        verdict.Contains("TEST SUCCEEDED", StringComparison.Ordinal) || verdict.StartsWith("Test Suite 'All tests' passed", StringComparison.Ordinal)
        || (verdict.StartsWith("✔ Test run with", StringComparison.Ordinal) && verdict.Contains("passed", StringComparison.Ordinal));

    public static string ArchitectureName(IReadOnlyList<string> archs) =>
        archs.Contains("arm64") && archs.Contains("x86_64") ? "universal" : archs.FirstOrDefault() ?? "unknown";

    public static string KindOf(PackagingFormat f) => f switch
    {
        PackagingFormat.PortableZip => "zip of the .app",
        PackagingFormat.MacDmg => "disk image",
        PackagingFormat.MacPkg => "installer package",
        PackagingFormat.MacXcodeArchive => "zip of the .xcarchive",
        _ => f.DisplayName(),
    };

    /// <param name="facts">The app as built (MacAppFacts of the bundle in the output folder).</param>
    public static JsonObject ForArtifact(ArtifactRecord artifact, MacAppFacts facts, MacSigningDoctor.Report signing, SourceProvenance source,
        string? xcode, string? sdk, DateTimeOffset builtAt, XcodeDiagnostics.Report? diagnostics, bool testsRequested)
    {
        var failed = artifact.Checks.Any(c => c.Status == VerificationStatus.Failed);
        var warned = artifact.Checks.Any(c => c.Status == VerificationStatus.PassedWithWarnings);
        JsonObject? tests = testsRequested
            ? new JsonObject
            {
                ["run"] = true,
                ["result"] = diagnostics is null ? "unknown" : diagnostics.TestFailures.Count == 0 && diagnostics.ResultLines.Any(TestsPassed)
                    && !diagnostics.ResultLines.Any(l => l.Contains("TEST FAILED", StringComparison.Ordinal) || l.StartsWith("Test Suite 'All tests' failed", StringComparison.Ordinal) || l.StartsWith("✘ Test run", StringComparison.Ordinal)) ? "passed"
                    : diagnostics.TestFailures.Count > 0 ? "failed" : "unknown",
                ["failures"] = diagnostics?.TestFailures.Count,
                ["by"] = "the scheme's Test action: xcodebuild test for the project's test targets, swift test for local Swift package test targets",
            }
            : new JsonObject { ["run"] = false, ["result"] = "not-run" };
        return ReleasePassport.ForArtifact(new ReleasePassport.ArtifactFacts(
            Platform: "macos",
            Architecture: ArchitectureName(facts.Architectures),
            Package: facts.BundleIdentifier ?? facts.Label,
            Version: facts.ShortVersion ?? "0",
            BuildNumber: facts.BuildVersion,
            ProductName: facts.Label,
            FileName: artifact.FileName,
            Sha256: artifact.Sha256,
            Size: artifact.SizeBytes,
            ArtifactKind: KindOf(artifact.Format),
            Source: source,
            Provider: "executable-maker-local",
            Toolchain: string.Join(", ", new[] { xcode is null ? null : "Xcode " + xcode, sdk is null ? null : "macOS SDK " + sdk, "HCG Executable Maker " + HCG.ExecutableMaker.Core.HostInfo.ProductVersion }.OfType<string>()),
            BuiltAt: builtAt,
            StaticInspectionState: failed ? "failed" : warned ? "passed-with-warnings" : "passed",
            StaticInspectionNote: $"{artifact.Checks.Count} checks of the {KindOf(artifact.Format)} by Executable Maker (structure, identity, architectures, signature as built)",
            Checks: artifact.Checks.Select(c => (c.Name, c.Status.ToString(), c.Detail)).ToList(),
            Tests: tests,
            CodeSigning: CodeSigning(signing, Path.GetFileName(signing.App)),
            IssuedBy: "HCG Executable Maker " + HCG.ExecutableMaker.Core.HostInfo.ProductVersion));
    }
}
