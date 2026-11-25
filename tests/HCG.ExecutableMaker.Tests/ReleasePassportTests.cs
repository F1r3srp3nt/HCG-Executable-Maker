using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// HCG Release Passport v1 (HCG Repo <c>docs/RELEASE_PASSPORT.md</c>, schema at HCG Repo main 3bf53c7): the pinned schema,
/// version compatibility, Maker's schema evaluator, the build-half rules (no repository signing, approvals, device
/// evidence or publication from the builder), reconciliation with the distribution authority's passport and the
/// mapping to HCG Store v2's provenance fields.
/// </summary>
public sealed class ReleasePassportTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private const string Sha = "ac4b31b10dbdf9f060b6e02274c3123c4e95045b8ba3b458f77f99387563099a";

    /// <summary>A build half in exactly the shape <see cref="ReleasePassport.BuildHalf"/> writes (values from a Mail-like package).</summary>
    internal static JsonObject BuildHalfSample() => JsonNode.Parse($$$"""
        {
          "format": "hcg.release-passport", "formatVersion": 1,
          "issuer": {"part": "build", "system": "HCG Executable Maker", "version": "HCG Executable Maker 0.4.0 (test)"},
          "product": {"key": null, "name": "HCG Mail Unleashed", "store_slug": null, "central_id": null},
          "edition": null, "platform": "ios-jailbreak-rootless", "architecture": "iphoneos-arm64",
          "package": "com.hakmicapitalgroup.hcgmail.unleashed", "version": "0.1.0~beta1",
          "source": {"repository": "F1r3srp3nt/HCG-Mail", "commit": "b0c6fee17c756a1dffff62e77574ad60b22415be", "branch": null},
          "build": {"provider": "executable-maker-local", "toolchain": "Xcode 26.6 (17F113) · iphoneos26.5, HCG Executable Maker 0.4.0", "built_at": "2026-11-25T09:00:00Z", "run_id": null, "artifact": null},
          "artifact": {"filename": "com.hakmicapitalgroup.hcgmail.unleashed_0.1.0~beta1_iphoneos-arm64.deb", "sha256": "{{{Sha}}}", "size": 43663718},
          "validation": {"levels": {
              "static_inspection": {"state": "passed"}, "maintainer_script_review": {"state": "passed"},
              "script_free_lifecycle": {"state": "not-run"}, "device_installation": {"state": "not-recorded"},
              "application_launch": {"state": "not-recorded"}, "runtime_verification": {"state": "not-recorded"}},
            "tool": "HCG Executable Maker 0.4.0 (test)", "checks": [{"check": "Signature integrity", "status": "Passed", "detail": "3 verified"}]},
          "device_evidence": [], "approvals": [],
          "distribution": {"requested_channel": "candidate", "note": "assigned by the distribution authority"},
          "publication": {"note": "not published: Executable Maker never publishes"},
          "repository_signing": {"scope": "{{{ReleasePassport.RepositorySigningScope}}}", "status": "{{{ReleasePassport.RepositorySigningNotFromBuilder}}}"},
          "code_signing": {"scope": "{{{ReleasePassport.CodeSigningScope}}}", "method_claimed": "ldid", "binaries": 3, "all_signed": true, "entitlements": {"var/jb/Applications/HCGMail.iOS.app/HCGMail.iOS": ["keychain-access-groups"]},
            "signed_by_builder": ["var/jb/Applications/HCGMail.iOS.app/HCGMail.iOS"]}
        }
        """)!.AsObject();

    /// <summary>The distribution authority's record for the same release, as HCG Repo 3bf53c7 writes it (tools/hcgrepo/passport.py).</summary>
    internal static JsonObject AuthoritySample()
    {
        var a = BuildHalfSample();
        a.Remove("issuer");
        a["build"] = JsonNode.Parse("""{"provider": "executable-maker-local", "toolchain": null, "built_at": null, "run_id": null, "artifact": null}""");
        a["validation"] = JsonNode.Parse("""{"levels": {"static_inspection": {"state": "passed"}, "maintainer_script_review": {"state": "passed"}, "script_free_lifecycle": {"state": "passed"}, "device_installation": {"state": "passed"}, "application_launch": {"state": "failed"}, "runtime_verification": {"state": "not-recorded"}}}""");
        a["approvals"] = JsonNode.Parse("""[{"kind": "checksum-approval", "by": "Owner", "sha256": "x"}]""");
        a["distribution"] = JsonNode.Parse("""{"channel": "candidate", "access": "public"}""");
        a["publication"] = JsonNode.Parse("""{"record_status": "published", "in_index_builds": true}""");
        a["repository_signing"] = JsonNode.Parse("""{"scope": "repository metadata", "configured": false, "fingerprint": null, "status": "unsigned: no production repository signing key configured yet"}""");
        a["code_signing"] = JsonNode.Parse("""{"scope": "application code", "method_claimed": "ldid", "binaries": 3, "all_signed": true, "entitlements": {"var/jb/Applications/HCGMail.iOS.app/HCGMail.iOS": ["keychain-access-groups"]}}""");
        return a;
    }

    private static string? RepoMain() => Environment.GetEnvironmentVariable("HCG_REPO_MAIN_CHECKOUT") is { } c && File.Exists(Path.Combine(c, "bin", "hcgrepo")) ? c : null;

    // ------------------------------------------------------------------ the pinned schema

    [Fact]
    public void The_embedded_schema_is_HCG_Repos_file_byte_for_byte_and_fully_understood()
    {
        Assert.Equal(ReleasePassport.SchemaSource.Sha256, Convert.ToHexStringLower(SHA256.HashData(ReleasePassport.SchemaFile)));
        Assert.Equal("hcg.release-passport", ReleasePassport.Schema["properties"]!["format"]!["const"]!.GetValue<string>());
        Assert.Equal(1, ReleasePassport.Schema["properties"]!["formatVersion"]!["const"]!.GetValue<int>());
        Assert.Empty(JsonSchemaLite.UnsupportedKeywords(ReleasePassport.Schema));   // every keyword the schema uses is checked
        // The two signatures are separate, required sections of the schema itself.
        var required = ReleasePassport.Schema["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("repository_signing", required);
        Assert.Contains("code_signing", required);
    }

    /// <summary>Drift: with an HCG Repo checkout, its schema must still be the one Maker embeds (re-pin deliberately when it changes).</summary>
    [Fact]
    public void HCG_Repos_current_schema_is_still_the_pinned_one()
    {
        if (RepoMain() is not { } repo) return;
        var theirs = File.ReadAllBytes(Path.Combine(repo, ReleasePassport.SchemaSource.Path));
        Assert.True(theirs.AsSpan().SequenceEqual(ReleasePassport.SchemaFile),
            $"HCG Repo's {ReleasePassport.SchemaSource.Path} (SHA-256 {Convert.ToHexStringLower(SHA256.HashData(theirs))}) differs from the schema Maker pins from {ReleasePassport.SchemaSource.Commit[..7]}: review the change, then update Distribution/release-passport-v1.schema.json and SchemaSource");
    }

    /// <summary>Every passport HCG Repo issues for its lock is read by Maker as a valid v1 record.</summary>
    [Fact]
    public async Task Every_passport_HCG_Repo_issues_is_valid_for_Maker()
    {
        if (RepoMain() is not { } repo || new ExecutableLocator().Find("python3") is not { } python) return;
        var outDir = _t.Combine("passports");
        var (r, _, stderr) = await new ProcessRunner().CaptureAsync(new ProcessSpec { Executable = python, Arguments = ["-I", Path.Combine(repo, "bin", "hcgrepo"), "passport", "--out", outDir], WorkingDirectory = repo }, CancellationToken.None);
        Assert.True(r.Succeeded, stderr);
        var files = Directory.GetFiles(outDir, "*.json");
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var p = JsonNode.Parse(File.ReadAllText(f))!;
            Assert.True(ReleasePassport.Validate(p) is { Count: 0 }, $"{Path.GetFileName(f)}: {string.Join("; ", ReleasePassport.Validate(p))}");
            Assert.NotNull(p["repository_signing"]?["configured"]);           // the authority states its repository signing …
            Assert.Equal(ReleasePassport.RepositorySigningText(p.AsObject()), ReleasePassport.RepositorySigningText(p.AsObject()));
            Assert.Contains("independent of repository signing", p["code_signing"]!["scope"]!.GetValue<string>());   // … separately from code signing
        }
    }

    // ------------------------------------------------------------------ version compatibility

    [Fact]
    public void Version_compatibility_refuses_newer_and_malformed_versions_and_accepts_additions()
    {
        Assert.Equal(ReleasePassport.Compatibility.Compatible, ReleasePassport.CheckVersion(BuildHalfSample()).State);
        var newer = BuildHalfSample(); newer["formatVersion"] = 2;
        var (state, detail) = ReleasePassport.CheckVersion(newer);
        Assert.Equal(ReleasePassport.Compatibility.NewerVersion, state);
        Assert.Contains("update Executable Maker", detail);
        Assert.Equal(["version: " + detail], ReleasePassport.Validate(newer));   // not half-read with v1 rules
        var text = BuildHalfSample(); text["formatVersion"] = "1";
        Assert.Equal(ReleasePassport.Compatibility.InvalidVersion, ReleasePassport.CheckVersion(text).State);
        var zero = BuildHalfSample(); zero["formatVersion"] = 0;
        Assert.Equal(ReleasePassport.Compatibility.InvalidVersion, ReleasePassport.CheckVersion(zero).State);
        var other = BuildHalfSample(); other["format"] = "hcg.repo.artifact-submission";
        Assert.Equal(ReleasePassport.Compatibility.NotAPassport, ReleasePassport.CheckVersion(other).State);
        Assert.Equal(ReleasePassport.Compatibility.NotAPassport, ReleasePassport.CheckVersion(JsonNode.Parse("[]")).State);
        // A v1 record with fields Maker does not know (added later on HCG Repo's side) stays valid.
        var extended = BuildHalfSample(); extended["compatibility_status"] = "experimental"; extended["future_field"] = new JsonObject { ["x"] = 1 };
        Assert.Empty(ReleasePassport.Validate(extended));
    }

    // ------------------------------------------------------------------ schema evaluation

    public static TheoryData<string, string> Mutations() => new()
    {
        { "missing code_signing", "code_signing" },
        { "missing repository_signing", "repository_signing" },
        { "sha256 in capitals", "sha" },
        { "size as text", "size" },
        { "unknown level state", "level" },
        { "lower-case fingerprint", "fingerprint" },
        { "unknown channel", "channel" },
        { "run_id as text", "run_id" },
        { "device result 'ok'", "device" },
        { "approval without kind", "approval" },
    };

    private static JsonObject Mutate(string what)
    {
        var p = BuildHalfSample();
        switch (what)
        {
            case "code_signing": p.Remove("code_signing"); break;
            case "repository_signing": p.Remove("repository_signing"); break;
            case "sha": p["artifact"]!["sha256"] = Sha.ToUpperInvariant(); break;
            case "size": p["artifact"]!["size"] = "43663718"; break;
            case "level": p["validation"]!["levels"]!["application_launch"]!["state"] = "launched"; break;
            case "fingerprint": p["repository_signing"]!["fingerprint"] = new string('a', 40); break;
            case "channel": p["distribution"]!["channel"] = "beta"; break;
            case "run_id": p["build"]!["run_id"] = "45580748239"; break;
            case "device": p["device_evidence"] = JsonNode.Parse("""[{"stage": "runtime", "result": "ok"}]"""); break;
            case "approval": p["approvals"] = JsonNode.Parse("""[{"by": "someone"}]"""); break;
        }
        return p;
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public void The_schema_catches(string description, string what)
    {
        _ = description;
        Assert.NotEmpty(ReleasePassport.Validate(Mutate(what)));
    }

    /// <summary>Maker's evaluator and the reference implementation (Python jsonschema, draft 2020-12) agree on every case.</summary>
    [Fact]
    public async Task Makers_schema_evaluator_agrees_with_the_reference_implementation()
    {
        if (new ExecutableLocator().Find("python3") is not { } python) return;
        var cases = new List<(string Name, JsonObject Doc)> { ("valid", BuildHalfSample()), ("authority", AuthoritySample()) };
        foreach (var row in Mutations()) cases.Add(((string)row[0], Mutate((string)row[1])));
        var schemaPath = _t.Write("schema.json", System.Text.Encoding.UTF8.GetString(ReleasePassport.SchemaFile));
        var docsPath = _t.Write("docs.json", new JsonArray(cases.Select(c => (JsonNode?)c.Doc.DeepClone()).ToArray()).ToJsonString());
        var script = "import json,sys\ntry:\n import jsonschema\nexcept ImportError:\n print('NO-JSONSCHEMA'); sys.exit(0)\n" +
                     "v = jsonschema.Draft202012Validator(json.load(open(sys.argv[1])))\nprint(json.dumps([v.is_valid(d) for d in json.load(open(sys.argv[2]))]))";
        var (r, stdout, stderr) = await new ProcessRunner().CaptureAsync(new ProcessSpec { Executable = python, Arguments = ["-I", "-c", script, schemaPath, docsPath] }, CancellationToken.None);
        Assert.True(r.Succeeded, stderr);
        if (stdout.Trim() == "NO-JSONSCHEMA") return;
        var reference = JsonSerializer.Deserialize<bool[]>(stdout.Trim())!;
        for (var i = 0; i < cases.Count; i++)
            Assert.True(reference[i] == (JsonSchemaLite.Validate(ReleasePassport.Schema, cases[i].Doc).Count == 0), $"{cases[i].Name}: jsonschema says {(reference[i] ? "valid" : "invalid")}");
    }

    // ------------------------------------------------------------------ build half: only the builder's facts

    [Fact]
    public void A_build_half_never_states_repository_signing_approvals_publication_or_device_results()
    {
        Assert.Empty(ReleasePassport.ValidateBuildHalf(BuildHalfSample()));
        var forged = BuildHalfSample();
        forged["repository_signing"]!["configured"] = true;
        forged["repository_signing"]!["fingerprint"] = new string('A', 40);   // schema-valid, but not the builder's to say
        Assert.Empty(ReleasePassport.Validate(forged));
        Assert.Contains(ReleasePassport.ValidateBuildHalf(forged), p => p.StartsWith("repository_signing:", StringComparison.Ordinal));
        var approved = BuildHalfSample(); approved["approvals"] = JsonNode.Parse("""[{"kind": "checksum-approval"}]""");
        Assert.Contains(ReleasePassport.ValidateBuildHalf(approved), p => p.StartsWith("approvals:", StringComparison.Ordinal));
        var launched = BuildHalfSample(); launched["validation"]!["levels"]!["application_launch"]!["state"] = "passed";
        Assert.Contains(ReleasePassport.ValidateBuildHalf(launched), p => p.Contains("cannot record device results"));
        var published = BuildHalfSample(); published["publication"] = JsonNode.Parse("""{"record_status": "published"}""");
        Assert.Contains(ReleasePassport.ValidateBuildHalf(published), p => p.StartsWith("publication:", StringComparison.Ordinal));
        var channel = BuildHalfSample(); channel["distribution"] = JsonNode.Parse("""{"channel": "candidate"}""");
        Assert.Contains(ReleasePassport.ValidateBuildHalf(channel), p => p.StartsWith("distribution:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ reconciliation with the distribution authority

    [Fact]
    public void The_authoritys_repository_signing_is_passed_through_verbatim_and_code_signing_is_compared()
    {
        var (findings, combined) = ReleasePassport.Reconcile(BuildHalfSample(), AuthoritySample());
        Assert.Empty(findings);
        Assert.True(JsonNode.DeepEquals(AuthoritySample()["repository_signing"], combined!["repository_signing"]));
        Assert.Equal("not configured; fingerprint none; unsigned: no production repository signing key configured yet", ReleasePassport.RepositorySigningText(combined));
        // The authority decides channel, access, approvals and levels; the builder only fills fields it left empty.
        Assert.Equal("public", combined["distribution"]!["access"]!.GetValue<string>());
        Assert.Equal("failed", combined["validation"]!["levels"]!["application_launch"]!["state"]!.GetValue<string>());
        Assert.Equal("Xcode 26.6 (17F113) · iphoneos26.5, HCG Executable Maker 0.4.0", combined["build"]!["toolchain"]!.GetValue<string>());
        Assert.Equal(["build.toolchain", "build.built_at"], combined["filled_from_build_half"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(ReleasePassport.Validate(combined));
    }

    [Fact]
    public void Different_bytes_or_different_code_signing_are_reported()
    {
        var other = AuthoritySample();
        other["artifact"]!["sha256"] = new string('0', 64);
        Assert.Contains(ReleasePassport.Reconcile(BuildHalfSample(), other).Findings, f => f.Level == "error" && f.Field == "artifact.sha256");
        var resigned = AuthoritySample();
        resigned["code_signing"]!["entitlements"] = JsonNode.Parse("""{"var/jb/Applications/HCGMail.iOS.app/HCGMail.iOS": ["com.apple.private.security.no-sandbox"]}""");
        resigned["code_signing"]!["all_signed"] = false;
        var f2 = ReleasePassport.Reconcile(BuildHalfSample(), resigned).Findings;
        Assert.Contains(f2, f => f.Field == "code_signing.entitlements");
        Assert.Contains(f2, f => f.Field == "code_signing.all_signed");
        var newer = AuthoritySample(); newer["formatVersion"] = 2;
        var (f3, combined) = ReleasePassport.Reconcile(BuildHalfSample(), newer);
        Assert.Null(combined);
        Assert.Contains(f3, f => f.Message.Contains("newer than this HCG Executable Maker understands"));
    }

    // ------------------------------------------------------------------ HCG Store v2

    [Fact]
    public void Ship_to_Store_reports_provenance_only_from_a_passport_of_the_exact_bytes()
    {
        var p = StoreProvenance.From(BuildHalfSample(), Sha, 43663718);
        Assert.True(p.Complete);
        Assert.Equal(("b0c6fee17c756a1dffff62e77574ad60b22415be", "local", "Xcode 26.6 (17F113) · iphoneos26.5, HCG Executable Maker 0.4.0"), (p.SourceCommit, p.BuildProvider, p.Toolchain));
        Assert.True(p.ValidationReport!.Passed);
        Assert.Contains("not repository signing", p.ValidationReport.Summary);
        Assert.DoesNotContain("fingerprint", p.ValidationReport.Summary);
        Assert.Contains("application launch not-recorded", p.ValidationReport.Summary);
        Assert.True(p.ValidationReport.Summary.Length <= 4000);

        var wrongBytes = StoreProvenance.From(BuildHalfSample(), new string('1', 64), 43663718);
        Assert.False(wrongBytes.Complete);
        Assert.Null(wrongBytes.SourceCommit);
        Assert.Contains(wrongBytes.Notes, n => n.Contains("different bytes"));
        var newer = BuildHalfSample(); newer["formatVersion"] = 2;
        Assert.Null(StoreProvenance.From(newer, Sha, 43663718).BuildProvider);
        var ci = BuildHalfSample(); ci["build"]!["provider"] = "github-actions"; ci["build"]!["toolchain"] = string.Join(", ", Enumerable.Repeat("Xcode 26.6 (17F113)", 20));
        var fromCi = StoreProvenance.From(ci, Sha, 43663718);
        Assert.Equal("github-actions", fromCi.BuildProvider);
        Assert.True(fromCi.Toolchain!.Length <= 200 && fromCi.Toolchain.StartsWith("Xcode 26.6 (17F113), ", StringComparison.Ordinal));
        var hosted = BuildHalfSample(); hosted["build"]!["provider"] = "hcg-repo-build";
        Assert.Null(StoreProvenance.From(hosted, Sha, 43663718).BuildProvider);   // no Store equivalent: not reported, not mapped to something else
    }

    /// <summary>HCG Store v2 (store main 3749fe6, server/src/publishing.ts ArtifactInput, a strict object): the exact field names.</summary>
    [Fact]
    public void The_Store_request_carries_provenance_in_HCG_Stores_field_names_and_omits_unknowns()
    {
        var with = new StoreArtifactRequest
        {
            FileName = "a.deb", Platform = "ios", Format = "deb", SizeBytes = 1, Sha256 = Sha,
            SourceCommit = "b0c6fee", BuildProvider = "local", Toolchain = "Xcode 26.6", ValidationReport = new("HCG Executable Maker", true, "ok"),
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(with, HcgStoreClient.Json))!.AsObject();
        Assert.Equal("b0c6fee", json["sourceCommit"]!.GetValue<string>());
        Assert.Equal("local", json["buildProvider"]!.GetValue<string>());
        Assert.Equal("Xcode 26.6", json["toolchain"]!.GetValue<string>());
        Assert.Equal(["tool", "passed", "summary"], json["validationReport"]!.AsObject().Select(kv => kv.Key));
        Assert.DoesNotContain(json, kv => kv.Key.Contains("passport", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("repository", StringComparison.OrdinalIgnoreCase));
        var without = JsonNode.Parse(JsonSerializer.Serialize(with with { SourceCommit = null, BuildProvider = null, Toolchain = null, ValidationReport = null }, HcgStoreClient.Json))!.AsObject();
        Assert.DoesNotContain(without, kv => kv.Key is "sourceCommit" or "buildProvider" or "toolchain" or "validationReport");   // older Store servers keep accepting it
    }

    [Fact]
    public async Task A_file_in_an_Unleashify_handoff_gets_its_provenance_from_the_handoffs_passport()
    {
        var dir = _t.Combine("handoff");
        Directory.CreateDirectory(dir);
        var deb = Path.Combine(dir, "x.deb");
        await File.WriteAllBytesAsync(deb, [1, 2, 3]);
        var sha = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));
        var half = BuildHalfSample(); half["artifact"]!["sha256"] = sha; half["artifact"]!["size"] = 3;
        await File.WriteAllTextAsync(Path.Combine(dir, ReleasePassport.FileName), half.ToJsonString());
        Assert.Equal(Path.Combine(dir, ReleasePassport.FileName), StoreProvenance.FindPassport(deb));
        var p = StoreProvenance.ForFile(deb, sha, 3);
        Assert.True(p.Complete);
        Assert.Equal(Path.Combine(dir, ReleasePassport.FileName), p.PassportPath);
        Assert.False(StoreProvenance.ForFile(_t.Write("lonely.zip", "z"), new string('2', 64), 1).Complete);
    }
}
