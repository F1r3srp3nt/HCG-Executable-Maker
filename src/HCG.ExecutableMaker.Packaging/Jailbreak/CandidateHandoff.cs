using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// Where the package came from. Everything is recorded as given; nothing is inferred. <see cref="Repository"/> and
/// <see cref="Commit"/> are the app's source; the CI fields name the run that built and uploaded the .deb (its own
/// repository and the commit that run built, which HCG Repo's importer checks against the run's head).
/// </summary>
public sealed record PackageProvenance(
    string? Repository,
    string? Commit,
    bool? SourceDirty,
    long? CiRunId = null,
    string? CiArtifact = null,
    string? CiRunUrl = null,
    string? BuiltWith = null,
    string? PackagedBy = null,
    string? CiRepository = null,
    string? CiCommit = null,
    LocalBuildInfo? Local = null)
{
    /// <summary>The run's repository and commit: given explicitly, or the app's own when its repository built it.</summary>
    public string? ImportRepository => CiRepository ?? Repository;
    public string? ImportCommit => CiCommit ?? (CiRepository is null ? Commit : null);
}

/// <summary>Texts for the HCG Repo review. The person packaging writes them; HCG adds only facts it measured.</summary>
public sealed record CandidateTexts(
    string Summary,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> KnownIssues,
    string ProductionAccess = "public",
    string? DeviceTarget = null,
    string? OwnerApproval = null,
    string? ApprovedSha256 = null,
    string? IconArtwork = null);

/// <summary>
/// What Unleashify knew when it exported: the mode, the scheme, the source revision and whether the imported copy was
/// current, the installed-build check and each test device's compatibility. Recorded as measured; nothing is inferred.
/// </summary>
public sealed record HandoffContext(
    string Mode,
    string? Scheme,
    ImportedRevision? Revision,
    RevisionState? RevisionState,
    string? RevisionDetail,
    ConflictReport? InstalledCheck,
    IReadOnlyList<DeviceCompatibility> Devices)
{
    /// <summary>A validated package is ready for a device test only when no installed build conflicts and the test phone can run it.</summary>
    public bool ReadyForDeviceTesting => InstalledCheck?.State == ConflictState.Clear && Devices.Count > 0 && Devices[0].Compatible && RevisionState != Domain.RevisionState.Stale;

    public string ReadinessText => RevisionState == Domain.RevisionState.Stale ? "Not ready: the imported source is stale (re-import and rebuild)."
        : InstalledCheck is null || InstalledCheck.State == ConflictState.NotChecked ? "Not yet ready for device testing: the installed-build check has not been done."
        : InstalledCheck.State == ConflictState.Conflict ? "Not ready: " + InstalledCheck.Summary
        : Devices.Count == 0 ? "Not ready: no test device named."
        : !Devices[0].Compatible ? $"Not ready: the test device ({Devices[0].Device.Name}, iOS {Devices[0].Device.IosVersion}) cannot run this build."
        : $"Ready for device testing on {Devices[0].Device.Name} (iOS {Devices[0].Device.IosVersion}). Not yet device-tested.";
}

/// <summary>
/// Exports everything HCG Repo needs to review a candidate build: the .deb, checksums, provenance, control, validation
/// report, changelog, known issues, a package.toml draft in HCG Repo's format, the app icon, the import request (only when
/// the source is pinned to an immutable CI artifact) and device test / recovery instructions. Nothing is uploaded.
/// </summary>
public static class CandidateHandoff
{
    public const string CandidateSource = "https://repo.hakmicapitalgroup.com/repo/candidate/";

    public sealed record Output(string Folder, IReadOnlyList<string> Files, bool ImportRequestComplete, IReadOnlyList<string> Missing);

    public static async Task<Output> ExportAsync(string folder, string debPath, IosAppBundle app, RootlessAppPackageSpec spec, RootlessAppDebValidator.Result validation,
        PackageProvenance provenance, CandidateTexts texts, IReadOnlyList<string> signedFiles, DateTimeOffset now, CancellationToken ct, HandoffContext? context = null,
        RootlessLifecycleSimulator.Result? lifecycle = null)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        var missing = new List<string>();
        async Task Write(string name, string text) { await File.WriteAllTextAsync(Path.Combine(folder, name), text.Replace("\r", ""), new UTF8Encoding(false), ct).ConfigureAwait(false); files.Add(name); }

        var debName = Path.GetFileName(debPath);
        var dest = Path.Combine(folder, debName);
        if (!string.Equals(Path.GetFullPath(dest), Path.GetFullPath(debPath), StringComparison.Ordinal)) File.Copy(debPath, dest, overwrite: true);
        files.Add(debName);
        var f = validation.Facts;
        await Write("SHA256SUMS", $"{f.Sha256}  {debName}\n").ConfigureAwait(false);

        // Provenance.
        var prov = new JsonObject
        {
            ["package"] = spec.PackageId, ["version"] = spec.Version, ["architecture"] = RootlessAppPackager.Architecture,
            ["file"] = debName, ["sha256"] = f.Sha256, ["size"] = f.Size,
            ["source"] = new JsonObject
            {
                ["repository"] = provenance.Repository, ["commit"] = provenance.Commit, ["dirty"] = provenance.SourceDirty,
                ["ci_repository"] = provenance.ImportRepository, ["ci_commit"] = provenance.ImportCommit,
                ["ci_run_id"] = provenance.CiRunId, ["ci_run_url"] = provenance.CiRunUrl, ["ci_artifact"] = provenance.CiArtifact,
            },
            ["app"] = new JsonObject
            {
                ["bundle_id"] = app.BundleId, ["name"] = app.Name, ["short_version"] = app.ShortVersion, ["build_version"] = app.BuildVersion,
                ["minimum_os"] = app.MinimumOS, ["device_families"] = new JsonArray(app.DeviceFamilies.Select(d => (JsonNode?)d).ToArray()),
                ["input"] = app.SourceKind, ["build_info"] = new JsonObject(app.BuildInfo.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            },
            ["packaging"] = new JsonObject
            {
                // The toolchain that compiled the app: as given for CI, otherwise read from the app itself (DTXcode…). HCG Repo's
                // handoff adapter records built_with + tool as the submission's build.toolchain, which it requires.
                ["tool"] = provenance.PackagedBy ?? provenance.Local?.BuiltBy,
                ["built_with"] = provenance.BuiltWith ?? provenance.Local?.Toolchain ?? LocalBuild.ToolchainFrom(app), ["packaged_at"] = now.ToString("O"),
                ["install_path"] = f.AppPath, ["compression"] = "gzip",
                ["ldid_signed"] = new JsonArray(signedFiles.Select(s => (JsonNode?)s).ToArray()),
                ["entitlements_files"] = new JsonArray(f.Entitlements.Keys.Select(s => (JsonNode?)s).ToArray()),
            },
            ["local_build"] = provenance.Local is { } lb ? new JsonObject
            {
                ["built_by"] = lb.BuiltBy, ["toolchain"] = lb.Toolchain, ["built_at"] = lb.BuiltAt.ToString("O"), ["host"] = lb.Host,
                ["source_visibility"] = lb.SourceVisibility, ["validation"] = lb.Validation,
                ["note"] = "Built on the owner's computer, not by a CI run: there is no run ID or artifact.",
            } : null,
            ["validation"] = new JsonArray(validation.Checks.Select(c => (JsonNode?)new JsonObject { ["check"] = c.Name, ["status"] = c.Status.ToString(), ["detail"] = c.Detail }).ToArray()),
            ["device_tested"] = false,
        };
        if (context is not null)
            prov["unleashify"] = new JsonObject
            {
                ["mode"] = context.Mode, ["scheme"] = context.Scheme,
                ["source_revision"] = new JsonObject
                {
                    ["kind"] = context.Revision?.Kind, ["commit"] = context.Revision?.Commit, ["branch"] = context.Revision?.Branch,
                    ["origin"] = context.Revision?.Origin, ["recorded_at"] = context.Revision?.CapturedAt.ToString("O"),
                    ["state"] = context.RevisionState?.ToString(), ["detail"] = context.RevisionDetail,
                },
                ["installed_build_check"] = context.InstalledCheck is { } ic ? new JsonObject
                {
                    ["state"] = ic.State.ToString(), ["summary"] = ic.Summary, ["findings"] = new JsonArray(ic.Findings.Select(x => (JsonNode?)x).ToArray()),
                } : null,
                ["devices"] = new JsonArray(context.Devices.Select(d => (JsonNode?)new JsonObject
                {
                    ["name"] = d.Device.Name, ["ios"] = d.Device.IosVersion, ["compatible"] = d.Compatible, ["detail"] = d.Detail,
                }).ToArray()),
                ["ready_for_device_testing"] = context.ReadyForDeviceTesting,
                ["readiness"] = context.ReadinessText,
            };
        await Write("provenance.json", prov.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n").ConfigureAwait(false);
        await Write("control.txt", ControlText(f.Control)).ConfigureAwait(false);
        await Write("VALIDATION.txt", validation.Report).ConfigureAwait(false);

        // Changelog and known issues.
        var changelog = new StringBuilder($"# {spec.Name} changelog\n\n## {spec.Version}\n\n");
        changelog.Append($"Candidate beta (Beta / Experimental) for device testing");
        if (provenance.Commit is { } c) changelog.Append($" ({provenance.Repository ?? "source"} commit `{c[..Math.Min(7, c.Length)]}`{(provenance.CiRunId is { } run ? $", CI run {run}" : "")}{(provenance.Local is { } lbc ? $", built locally{(lbc.Toolchain is { } tc ? " with " + tc : "")}" : "")})");
        changelog.Append(".\n\n");
        foreach (var line in texts.Changes) changelog.Append("- ").Append(line).Append('\n');
        changelog.Append("- Packaged as a rootless app (`iphoneos-arm64`, `").Append(f.AppPath).Append("`) with gzip members.\n");
        changelog.Append("- Not yet verified on a physical device.\n");
        await Write("CHANGELOG.md", changelog.ToString()).ConfigureAwait(false);

        var issues = new List<string> { "**Not yet device-tested.** No device result is recorded for this build." };
        if (texts.DeviceTarget is { } target) issues.Add($"First test target: {target}.");
        issues.AddRange(texts.KnownIssues);
        foreach (var check in validation.Checks.Where(ch => ch.Status == VerificationStatus.PassedWithWarnings)) issues.Add($"{check.Name}: {check.Detail}");
        await Write("KNOWN-ISSUES.md", $"# {spec.Name} {spec.Version}: known issues\n\n" + string.Join("\n", issues.Select(i => "- " + i)) + "\n").ConfigureAwait(false);

        // Icon: standard PNG only (Xcode's CgBI icons are converted), validated with a standard decoder; approved artwork as fallback.
        var icon = PngIcon.Choose(app.LargestIcon(), texts.IconArtwork);
        if (icon.Icon is { } chosen)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "icon.png"), chosen.Png, ct).ConfigureAwait(false);
            files.Add("icon.png");
            prov["icon"] = new JsonObject
            {
                ["file"] = "icon.png", ["source"] = chosen.Source, ["detail"] = chosen.Detail, ["width"] = chosen.Width, ["height"] = chosen.Height,
                ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(chosen.Png)), ["format"] = "PNG (standard)",
                ["notes"] = new JsonArray(icon.Notes.Select(n => (JsonNode?)n).ToArray()),
            };
            await File.WriteAllTextAsync(Path.Combine(folder, "provenance.json"), prov.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        else missing.Add("icon.png: no usable standard PNG — " + string.Join("; ", icon.Notes) +
                         ". Choose the project's AppIcon artwork (e.g. the 1024×1024 image in Assets.xcassets/AppIcon.appiconset) as approved artwork and export again");

        if (texts.ProductionAccess == "undecided") missing.Add("production access: not recorded for this product in HCG Repo; package.toml uses HCG's default (connect) and the owner decides the production level before any stable release");
        if (provenance.Local is not null)
        {
            // HCG Repo's submission contract: the draft carries no [access] exception. The owner approves a free public
            // Candidate demo in HCG Repo itself (release prepare --free-demo-approval), after approving the exact SHA-256.
            await Write("package.toml", PackageToml(spec, app, f, texts with { OwnerApproval = null }, issues, local: true)).ConfigureAwait(false);
        }
        else
        {
            if (LocalBuild.ApprovalProblem(texts, provenance, f.Sha256) is { } approvalProblem)
            {
                missing.Add("owner approval NOT recorded: " + approvalProblem);
                texts = texts with { OwnerApproval = null };
            }
            if (NeedsApproval(texts)) missing.Add($"owner approval: HCG Repo Candidate serves only free public builds, and the owner approves each version as a free public demo (HCG Repo docs/ACCESS.md). No approval of {spec.Version} is recorded, so package.toml leaves [access] exception commented out and HCG Repo's importer will refuse the package until the owner's approval is written there");
            // package.toml draft in HCG Repo's format.
            await Write("package.toml", PackageToml(spec, app, f, texts, issues)).ConfigureAwait(false);
        }

        // Import request: only a pinned, immutable source can be imported.
        // HCG Repo Distribution v2 submission manifest (hcg.repo.artifact-submission v1): only for a pinned, clean source.
        var manifest = SubmissionManifest.Build(spec, app, validation, provenance, context, now, debName, lifecycle, signedFiles);
        if (manifest.Complete) await Write(SubmissionManifest.FileName, SubmissionManifest.Serialize(manifest.Manifest!)).ConfigureAwait(false);
        else missing.Add("submission.json (HCG Repo Distribution v2): " + string.Join("; ", manifest.Problems));

        // HCG Release Passport v1, build half: what Maker measured. The distribution authority adds channel, access,
        // approvals, publication, device evidence and repository signing in its own passport.
        if (manifest.Complete)
        {
            var passport = Distribution.ReleasePassport.BuildHalf(manifest.Manifest!, validation, lifecycle, provenance.PackagedBy ?? "HCG Executable Maker");
            if (Distribution.ReleasePassport.ValidateBuildHalf(passport) is { Count: > 0 } passportProblems)
                missing.Add(Distribution.ReleasePassport.FileName + ": " + string.Join("; ", passportProblems));
            else await Write(Distribution.ReleasePassport.FileName, SubmissionManifest.Serialize(passport)).ConfigureAwait(false);
        }

        // Import request. A CI build is pinned by its run and artifact (github-artifact). A local build has none: it goes
        // through HCG Repo's Release Manager (Ship to Repo submits; the owner approves and prepares in HCG Repo), so Maker
        // writes no request of its own for it.
        var local = provenance.Local is not null;
        bool complete;
        if (local)
        {
            complete = manifest.Complete;
            if (!complete) missing.Add("Ship to Repo: the local build is not pinned to its source; build from a pinned commit (repository OWNER/NAME, the full 40-character commit, no local changes)");
        }
        else
        {
            complete = provenance is { ImportRepository: { Length: > 0 }, CiRunId: not null, CiArtifact: { Length: > 0 }, ImportCommit.Length: 40 };
            await Write(complete ? "import-request.toml" : "import-request.INCOMPLETE.toml", ImportRequest(spec, provenance, f.Sha256, complete, debName)).ConfigureAwait(false);
            if (!complete) missing.Add("import-request.toml: the package was not built by a CI run with an uploaded artifact, so it cannot be pinned; build it in CI (or attach it to a tagged release) and fill in run_id/artifact, or use Ship to Repo");
        }

        await Write("DEVICE-TEST.md", DeviceTest(spec, app, f, context, debName)).ConfigureAwait(false);
        await Write("README.md", Readme(spec, f, files, complete, missing, context, provenance.Local is not null)).ConfigureAwait(false);
        return new Output(folder, files, complete, missing);
    }

    /// <summary>The package's control fields in Debian control syntax: continuation lines start with a space (blank ones are " .").</summary>
    public static string ControlText(IReadOnlyDictionary<string, string> control) =>
        string.Concat(control.Select(kv => $"{kv.Key}: " + string.Join("\n ", kv.Value.Replace("\r", "").Split('\n').Select((l, i) => i > 0 && l.Trim().Length == 0 ? "." : l)) + "\n"));

    private static string TomlEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// HCG Repo Candidate serves only public builds, and an HCG package is public only by the owner's recorded approval of
    /// that version (HCG Repo docs/ACCESS.md), whatever its production level. Maker never writes an approval it was not given.
    /// </summary>
    public static bool NeedsApproval(CandidateTexts texts) => texts.OwnerApproval is not { Length: > 0 };

    private static string Toml(string s) => "\"\"\"\n" + s.Replace("\\", "\\\\").Replace("\"\"\"", "\\\"\\\"\\\"") + "\n\"\"\"";

    public static string PackageToml(RootlessAppPackageSpec spec, IosAppBundle app, RootlessAppDebValidator.Facts f, CandidateTexts texts, IReadOnlyList<string> issues, bool local = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Presentation and review metadata for {spec.Name} (rootless jailbreak app). Draft exported by HCG Executable Maker.");
        sb.AppendLine("# Version, architecture and dependencies come from the .deb itself. Review every text before importing.");
        sb.AppendLine();
        sb.AppendLine("[release]").AppendLine("channel = \"beta\"").AppendLine();
        sb.AppendLine("[display]");
        sb.AppendLine("known_issues = " + Toml(string.Join("\n", issues.Select(i => "- " + i))));
        sb.AppendLine($"summary = \"{texts.Summary.Replace("\"", "\\\"")}\"");
        sb.AppendLine("icon = \"icon.png\"");
        sb.AppendLine("notes = " + Toml($"{spec.Description}\n\n**Beta / Experimental.** Device compatibility is unverified until a test is recorded.\n\n**Free public demo in HCG Repo Candidate:** downloading and installing needs no sign-in and no payment. HCG account data, server connections and protected features in the app still require their own HCG sign-in and permissions."));
        sb.AppendLine("limitations = " + Toml($"- **Tested:** nothing yet.\n- Rootless only (`iphoneos-arm64`), installed at `{f.AppPath}`. The app's binaries declare iOS {f.MinimumOS ?? "(unknown)"} or later (`firmware (>= {f.MinimumOS})`).\n- The app's capabilities are those of the App Store-style build it was packaged from; the package adds none."));
        sb.AppendLine("install = " + Toml($"1. Add **HCG Repo Candidate** (`{CandidateSource}`), refresh, and install **{spec.Name}** {spec.Version}.\n2. The package manager registers the app; its icon appears on the home screen. No respring is needed.\n3. Open **{spec.Name}**."));
        sb.AppendLine("removal = " + Toml($"1. In your package manager, remove **{spec.Name}**, or `sudo dpkg -r {spec.PackageId}` in a terminal.\n2. The icon disappears. If it stays, run `uicache -a` or respring.\n3. The app's own data in its container is not removed by the package manager."));
        sb.AppendLine();
        sb.AppendLine("[review]").AppendLine("allow_setuid = []");
        var reviewed = SubmissionManifest.EntitlementsByPath(f);
        if (f.Entitlements.Count == 0) sb.AppendLine("# No entitlements are embedded in this build.");
        else
        {
            sb.AppendLine("# The exact entitlements each binary carries, as measured in the package (see VALIDATION.txt). HCG Repo's review");
            sb.AppendLine("# compares them with the binaries; the owner reviews them before the import. Keys must stay above these tables.");
            foreach (var (path, dict) in reviewed)
            {
                sb.AppendLine().AppendLine($"[review.entitlements.{SubmissionManifest.TomlString(path)}]");
                foreach (var (k, v) in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.AppendLine($"{SubmissionManifest.TomlString(k)} = {SubmissionManifest.ToToml(v)}");
            }
            foreach (var rel in f.Entitlements.Keys.Where(r => !reviewed.ContainsKey(f.AppPath?.TrimStart('/') + "/" + r)))
                sb.AppendLine($"# {rel}: entitlements with data or date values; record them by hand after review.");
        }
        sb.AppendLine();
        sb.AppendLine("[access]");
        // HCG Repo (docs/ACCESS.md): HCG packages need HCG Connect sign-in by default; a build is free and public only by a
        // recorded, per-version owner approval (exception + exception_versions), and only in the candidate channel.
        var level = texts.ProductionAccess is "public" or "connect" or "paid" ? texts.ProductionAccess : "connect";
        sb.AppendLine(texts.ProductionAccess == "undecided"
            ? "# Production access for this product is NOT recorded in HCG Repo and was not decided here: HCG's default (connect) applies."
            : $"# Production access: {level}. This file does not change it.");
        sb.AppendLine($"level = \"{level}\"");
        var exception = $"Free public beta demo (no sign-in, no payment) in HCG Repo Candidate only, under the owner's free-demo policy for approved candidate builds";
        if (local)
        {
            sb.AppendLine("# No [access] exception: under HCG Repo's submission contract only the owner approves a free public Candidate demo,");
            sb.AppendLine($"# per version, in HCG Repo: bin/hcgrepo release prepare <submission> --free-demo-approval \"<owner's words>\" records it.");
        }
        else if (texts.OwnerApproval is { Length: > 0 } approval)
        {
            var forBytes = texts.ApprovedSha256 is { Length: > 0 } approvedSha ? $" Approved package SHA-256: {approvedSha.Trim().ToLowerInvariant()}." : "";
            sb.AppendLine($"exception = \"{TomlEscape($"{exception}: {approval.Trim()}. For the versions in exception_versions only; production access is unchanged.{forBytes}")}\"");
            sb.AppendLine($"exception_versions = [\"{spec.Version}\"]");
        }
        else
        {
            sb.AppendLine($"# NOT IMPORTABLE YET: no owner approval of {spec.Version} as a free public demo is recorded (approval is per version,");
            sb.AppendLine("# HCG Repo docs/ACCESS.md), and HCG Repo Candidate serves only public builds. Once the owner approves it, record who,");
            sb.AppendLine("# when and their words, and uncomment:");
            sb.AppendLine($"# exception = \"{exception}: approved by <owner> on <date>: '<their words>'. For the versions in exception_versions only; production access is unchanged.\"");
            sb.AppendLine($"# exception_versions = [\"{spec.Version}\"]");
        }
        return sb.ToString();
    }

    public static string ImportRequest(RootlessAppPackageSpec spec, PackageProvenance p, string sha256, bool complete, string? debName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {spec.Name} {spec.Version}, rootless. Exported by HCG Executable Maker.");
        if (!complete)
            sb.AppendLine("# INCOMPLETE: not importable yet. The .deb was not produced by a CI run with an uploaded artifact, so there is no immutable")
              .AppendLine("# source to pin. Build it in CI (or attach it to a tagged release), then fill in the source and rename this file.");
        if (p.Repository is { } srcRepo && p.Commit is { } srcCommit && (p.CiRepository is not null && p.CiRepository != srcRepo))
            sb.AppendLine($"# App source: {srcRepo} commit {srcCommit}. Built and packaged by the run below (see provenance.json).");
        sb.AppendLine().AppendLine("[request]");
        sb.AppendLine($"package = \"{spec.PackageId}\"").AppendLine($"version = \"{spec.Version}\"").AppendLine($"architecture = \"{RootlessAppPackager.Architecture}\"").AppendLine("channel = \"candidate\"");
        sb.AppendLine().AppendLine("[source]").AppendLine("kind = \"github-artifact\"");
        sb.AppendLine($"repository = \"{p.ImportRepository ?? "OWNER/NAME"}\"");
        sb.AppendLine(p.CiRunId is { } run ? $"run_id = {run}" : "# run_id = (the CI run that built this .deb)");
        sb.AppendLine(p.ImportCommit is { Length: 40 } c ? $"commit = \"{c}\"" : "# commit = (the 40-character commit that run built)");
        sb.AppendLine(p.CiArtifact is { Length: > 0 } a ? $"artifact = \"{a}\"" : "# artifact = (the uploaded artifact's name)");
        sb.AppendLine($"sha256 = \"{sha256}\"");
        return sb.ToString();
    }

    public static string DeviceTest(RootlessAppPackageSpec spec, IosAppBundle app, RootlessAppDebValidator.Facts f, HandoffContext? context = null, string? debName = null)
    {
        var deb = debName ?? $"{spec.PackageId}_{spec.Version}_iphoneos-arm64.deb";
        var exe = app.Executable;
        var bundle = f.BundleId ?? app.BundleId;
        var terms = Unleashify.ProductTerms(bundle, spec.PackageId, exe);
        var grep = string.Join(' ', terms.Select(t => $"-e {t}"));
        var slug = Unleashify.Slug(spec.PackageId);
        var family = Unleashify.FamilyBundleId(bundle);
        var quoted = string.Join(", ", terms.Select(t => $"\"{t}\""));
        var variantNote = family != bundle
            ? $"\n- `{family}` (or another bundle ID of the same family): a **different app ID**, e.g. the standard app next to this variant. It can stay installed; record it."
            : "";
        var target = context?.Devices.FirstOrDefault() is { } d ? $"\n- Test target: {d.Device.Name}, iOS {d.Device.IosVersion} — {(d.Compatible ? "compatible" : "NOT compatible")}: {d.Detail}" : "";
        return $"""
        # {spec.Name} {spec.Version}: device test and rollback

        Package `{spec.PackageId}` · `iphoneos-arm64` · SHA-256 `{f.Sha256}` · {f.Size:N0} bytes.
        Installs only `{f.AppPath}` (bundle ID `{f.BundleId}`, iOS {f.MinimumOS} or later). The postinst and postrm scripts only
        register or unregister the app with `uicache`. Nothing runs at boot, nothing is injected into other processes, nothing
        outside `{f.AppPath}` is written, and no respring is needed.

        Run every step only with the owner's go-ahead for this device. Commands are run over SSH as the `mobile` user
        (`sudo` where shown). Paste each command's output into the result record at the end.

        ## 0 · Device
        - Rootless jailbreak (Dopamine, palera1n rootless) on iOS {f.MinimumOS} or later; `/var/jb` exists (`ls -d /var/jb`).
        - Record: iPhone model, iOS version, jailbreak and version, package manager and version.
        - `uikittools` installed (`dpkg -s uikittools | grep Status`); it provides `uicache`.
        - **Stop** if iOS is below {f.MinimumOS} or there is no `/var/jb`: the package does not apply to this device.{target}

        ## 1 · Detect existing {spec.Name} installations (before anything is installed)
        ```
        uicache -l | grep -i -e {bundle} {grep}
        dpkg -l | grep -i -e {spec.PackageId} {grep}
        ls -d /var/jb/Applications/*.app | grep -i -e {app.AppDirectoryName} {grep}
        ls -d /var/containers/Bundle/Application/*/*.app 2>/dev/null | grep -i -e {app.AppDirectoryName} {grep}
        ```
        - Nothing printed: no other build is installed; continue.
        - `{f.BundleId}` at a path under `/var/containers/Bundle/Application/`: a **sideloaded / Xcode / TestFlight build**. Two
          apps with one bundle ID cannot both be registered. **Stop** and ask the owner: removing it (Home Screen › Remove App)
          deletes that build *and its data*; rollback then means reinstalling it from its original source (Xcode, IPA, TestFlight).
        - `{spec.PackageId}` already in `dpkg -l`: an earlier copy of this package. Same version: reinstall; older: upgrade;
          **newer: stop** (that would be a downgrade).
        - Another package or path matching {quoted}: record it; **stop** only if it is another build with bundle ID `{bundle}`.{variantNote}
        - Paste the output into Maker's Unleashify page (*Test devices and installed builds*) to have it checked.{InstalledLine(context)}

        ## 2 · Snapshot for rollback
        ```
        dpkg -l > /var/mobile/{slug}-test-before-dpkg.txt
        uicache -l > /var/mobile/{slug}-test-before-uicache.txt
        ```

        ## 3 · Install this exact file (before publication)
        1. Copy `{deb}` from this folder to the phone: `scp {deb} mobile@<phone>:/var/mobile/`.
        2. `shasum -a 256 /var/mobile/{deb}` must print `{f.Sha256}`. **Stop** if it differs.
        3. `sudo dpkg -i /var/mobile/{deb}`
        4. Expected: no `dpkg: error`; dependency notes for `firmware`/`uikittools` are satisfied by the jailbreak.
           **Stop and roll back** (step 7) on any error.

        ## 4 · Verify the installation
        ```
        dpkg -s {spec.PackageId} | grep -E '^(Status|Version)'
        dpkg -L {spec.PackageId}
        uicache -l | grep {f.BundleId}
        ```
        Expected: `Status: install ok installed`, `Version: {spec.Version}`; every listed path is under `{f.AppPath}` (plus its
        parent folders); `{f.BundleId}` is registered at `{f.AppPath}` and nowhere else. The **{spec.Name}** icon is on the home screen.

        ## 5 · Launch
        1. Open **{spec.Name}** from the home screen. Expected: it opens to its first screen without crashing.
        2. Use it briefly as you would the standard app. It has the standard app's features only; record anything that fails.
        3. On a crash: Settings › Privacy & Security › Analytics & Improvements › Analytics Data → entries starting with `{exe}`; keep the newest.

        ### If the app closes at launch: collect the evidence before changing anything
        The crash report decides whether the cause is the package, the jailbreak's trust of its code, or the app itself.
        ```
        ls -t /var/mobile/Library/Logs/CrashReporter/ | grep -i -e '^{exe}' -e jetsam | head
        cat "/var/mobile/Library/Logs/CrashReporter/$(ls -t /var/mobile/Library/Logs/CrashReporter/ | grep '^{exe}' | head -1)"
        ```
        Read the `termination` (or `exception`) section of the newest `{exe}-….ips`:
        - `"namespace" : "CODESIGNING"` (e.g. `Invalid Page`, `Launch Constraint Violation`): the system refused the code's
          signature or its trust. Record the `indicator` and which image it names; this points at packaging or at the
          jailbreak's trust of ad hoc code, and is the only case where the signing of the package should change.
        - `"namespace" : "DYLD"`: a library or symbol is missing (the message names it), e.g. a framework that is not in the
          package or an API newer than iOS on this device.
        - `EXC_CRASH` / `SIGABRT` with the app's own frames (for .NET apps: `mono_`, `xamarin_`, `System.` or the app's
          namespaces): the app itself stopped; its own logs and start-up trace come next.
        - `"namespace" : "FRONTBOARD"` / `0x8badf00d`: the app took too long to show its first screen (launch watchdog).
        - No report at all: the process may not have started; attach the device to a Mac, open Console, filter for `{exe}`,
          launch again and save the log.
        Attach the `.ips` (it holds no passwords; review it for personal data before sharing) to the result record.

        ## 6 · Remove
        1. `sudo dpkg -r {spec.PackageId}` (or Zebra › Installed › **{spec.Name}** › Remove).
        2. Expected: `ls {f.AppPath}` → "No such file or directory"; `uicache -l | grep {f.BundleId}` prints nothing; the icon is gone.
        3. `sudo dpkg -P {spec.PackageId}` purges dpkg's record.
        4. `dpkg -l > /var/mobile/{slug}-test-after-dpkg.txt; diff /var/mobile/{slug}-test-before-dpkg.txt /var/mobile/{slug}-test-after-dpkg.txt`
           Expected: no difference.

        ## 7 · Rollback and recovery
        - **Install failed or the app misbehaves:** `sudo dpkg -r {spec.PackageId}`; `sudo dpkg -P {spec.PackageId}`; `uicache -a`.
          The phone is then back to the snapshot in step 2 (compare with `diff` as in step 6).
        - **Icon missing after install:** `uicache -p {f.AppPath}` (or `uicache -a`), or respring.
        - **Icon stays after removal:** `uicache -a`, or respring.
        - **dpkg reports a half-installed package:** `sudo dpkg --configure -a`, then `sudo dpkg -r {spec.PackageId}`.
        - **A build removed in step 1:** reinstall it from its original source (Xcode, IPA, TestFlight); its earlier data does not return.
        - The app's own data container (`/var/mobile/Containers/Data/Application/…`) is not removed by dpkg; delete it from
          Settings › General › iPhone Storage if needed.
        - Nothing in this package changes SpringBoard, daemons or system files, so a safe-mode reboot is not needed for this package.

        ## Install after publication (only if the owner publishes it)
        Zebra › Sources › Add `{CandidateSource}`, refresh, install **{spec.Name}** {spec.Version}; then steps 4–7.

        ## Result record
        ```
        Device: <model> · iOS <x.y> · <jailbreak and version> · <package manager>
        Step 1 detection output: <paste>
        Install (step 3): pass / fail — <output>
        Verify (step 4): pass / fail — <output>
        Launch (step 5): opens / crashes / icon missing — <notes>
        Remove (step 6): pass / fail — <diff output>
        Tester, date: <name>, <date>
        ```

        """.Replace("\r", "");
    }

    private static string InstalledLine(HandoffContext? context) =>
        context?.InstalledCheck is { } ic ? "\n- Maker's check at export: " + ic.State + " — " + ic.Summary : "";

    private static string Readme(RootlessAppPackageSpec spec, RootlessAppDebValidator.Facts f, IReadOnlyList<string> files, bool complete, IReadOnlyList<string> missing, HandoffContext? context, bool local = false)
    {
        var sb = new StringBuilder($"# Candidate handoff: {spec.Name} {spec.Version} (Beta / Experimental)\n\n");
        sb.Append($"`{spec.PackageId}` · `iphoneos-arm64` · {f.Size:N0} bytes ({f.Size} bytes) · SHA-256 `{f.Sha256}`\n\n");
        if (context is not null)
        {
            sb.Append($"- Mode: {context.Mode}{(context.Scheme is null ? "" : $" · scheme `{context.Scheme}`")} · bundle ID `{f.BundleId}` · iOS {f.MinimumOS}+ · installs `{f.AppPath}`\n");
            sb.Append($"- Source revision: {(context.Revision?.Commit is { } c ? $"`{c}`{(context.Revision.Branch is { } br ? $" ({br})" : "")}" : "not recorded")} — {context.RevisionDetail}\n");
            sb.Append($"- Installed-build check: {context.InstalledCheck?.State.ToString() ?? "NotChecked"} — {context.InstalledCheck?.Summary}\n");
            foreach (var d in context.Devices) sb.Append($"- {d.Device.Name}, iOS {d.Device.IosVersion}: {(d.Compatible ? "compatible" : "NOT compatible")} — {d.Detail}\n");
            sb.Append($"- **{context.ReadinessText}**\n\n");
        }
        sb.Append($"For HCG Repo Candidate (`{CandidateSource}`). Nothing has been published. Not device-tested.\n\n");
        sb.Append("Files:\n");
        foreach (var file in files.Append("README.md").Distinct()) sb.Append("- `").Append(file).Append("`\n");
        sb.Append(complete && local
            ? "\nTo submit (local build): use **Ship to Repo** (Unleashify, or `--ship-to-repo submit`) with your HCG Repo checkout, or run `bin/hcgrepo release submit --handoff <this folder> --result-json <file>` there. HCG Repo's Release Manager validates `" + SubmissionManifest.FileName + "` and the .deb and reports six evidence levels. " +
              "Approving the exact SHA-256 (`release approve`) and preparing the Candidate (`release prepare`, with `--free-demo-approval` for a free demo) are the owner's steps in HCG Repo; Executable Maker never runs them. " +
              "Nothing from the app's source is included. Publishing stays a separate, manual step.\n"
            : complete
            ? "\nTo submit: copy `import-request.toml` to HCG Repo `imports/`, `package.toml`, `CHANGELOG.md` and `icon.png` to `packages/" + spec.PackageId + "/`, push a branch; **Prepare packages** fetches and checks the pinned artifact.\n"
            : "\n**Not importable yet:** see `import-request.INCOMPLETE.toml`.\n");
        if (missing.Count > 0) { sb.Append("\nMissing:\n"); foreach (var m in missing) sb.Append("- ").Append(m).Append('\n'); }
        return sb.ToString();
    }
}
