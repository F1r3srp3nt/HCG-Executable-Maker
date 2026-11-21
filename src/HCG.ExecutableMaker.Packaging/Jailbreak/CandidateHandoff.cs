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
    string? CiCommit = null)
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
    string? OwnerApproval = null);

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
        PackageProvenance provenance, CandidateTexts texts, IReadOnlyList<string> signedFiles, DateTimeOffset now, CancellationToken ct, HandoffContext? context = null)
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
                ["tool"] = provenance.PackagedBy, ["built_with"] = provenance.BuiltWith, ["packaged_at"] = now.ToString("O"),
                ["install_path"] = f.AppPath, ["compression"] = "gzip",
                ["ldid_signed"] = new JsonArray(signedFiles.Select(s => (JsonNode?)s).ToArray()),
                ["entitlements_files"] = new JsonArray(f.Entitlements.Keys.Select(s => (JsonNode?)s).ToArray()),
            },
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
        await Write("control.txt", string.Join("\n", f.Control.Select(kv => $"{kv.Key}: {kv.Value}")) + "\n").ConfigureAwait(false);
        await Write("VALIDATION.txt", validation.Report).ConfigureAwait(false);

        // Changelog and known issues.
        var changelog = new StringBuilder($"# {spec.Name} changelog\n\n## {spec.Version}\n\n");
        changelog.Append($"Candidate beta (Beta / Experimental) for device testing");
        if (provenance.Commit is { } c) changelog.Append($" ({provenance.Repository ?? "source"} commit `{c[..Math.Min(7, c.Length)]}`{(provenance.CiRunId is { } run ? $", CI run {run}" : "")})");
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

        // Icon.
        if (app.LargestIcon() is { } icon) { File.Copy(icon, Path.Combine(folder, "icon.png"), overwrite: true); files.Add("icon.png"); }
        else missing.Add("icon.png: the app bundle has no loose AppIcon*.png (icons only in Assets.car); export a 180×180 PNG from the project's AppIcon set");

        if (texts.ProductionAccess == "undecided") missing.Add("production access: not recorded for this product in HCG Repo; package.toml uses HCG's default (connect) and the owner decides the production level before any stable release");
        if (NeedsApproval(texts)) missing.Add($"owner approval: HCG Repo Candidate serves only free public builds, and the owner approves each version as a free public demo (HCG Repo docs/ACCESS.md). No approval of {spec.Version} is recorded, so package.toml leaves [access] exception commented out and HCG Repo's importer will refuse the package until the owner's approval is written there");
        // package.toml draft in HCG Repo's format.
        await Write("package.toml", PackageToml(spec, app, f, texts, issues)).ConfigureAwait(false);

        // Import request: only a pinned, immutable source can be imported.
        var complete = provenance is { ImportRepository: { Length: > 0 }, CiRunId: not null, CiArtifact: { Length: > 0 }, ImportCommit.Length: 40 };
        var request = ImportRequest(spec, provenance, f.Sha256, complete);
        await Write(complete ? "import-request.toml" : "import-request.INCOMPLETE.toml", request).ConfigureAwait(false);
        if (!complete) missing.Add("import-request.toml: the package was not built by a CI run with an uploaded artifact, so it cannot be pinned; build it in CI (or attach it to a tagged release) and fill in run_id/artifact");

        await Write("DEVICE-TEST.md", DeviceTest(spec, app, f, context, debName)).ConfigureAwait(false);
        await Write("README.md", Readme(spec, f, files, complete, missing, context)).ConfigureAwait(false);
        return new Output(folder, files, complete, missing);
    }

    private static string TomlEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// HCG Repo Candidate serves only public builds, and an HCG package is public only by the owner's recorded approval of
    /// that version (HCG Repo docs/ACCESS.md), whatever its production level. Maker never writes an approval it was not given.
    /// </summary>
    public static bool NeedsApproval(CandidateTexts texts) => texts.OwnerApproval is not { Length: > 0 };

    private static string Toml(string s) => "\"\"\"\n" + s.Replace("\\", "\\\\").Replace("\"\"\"", "\\\"\\\"\\\"") + "\n\"\"\"";

    public static string PackageToml(RootlessAppPackageSpec spec, IosAppBundle app, RootlessAppDebValidator.Facts f, CandidateTexts texts, IReadOnlyList<string> issues)
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
        sb.AppendLine(f.Entitlements.Count == 0
            ? "# No entitlements are embedded in this build."
            : $"# Entitlements are embedded in: {string.Join(", ", f.Entitlements.Keys)} (see VALIDATION.txt). Review them before importing.");
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
        if (texts.OwnerApproval is { Length: > 0 } approval)
        {
            sb.AppendLine($"exception = \"{TomlEscape($"{exception}: {approval.Trim()}. For the versions in exception_versions only; production access is unchanged.")}\"");
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

    public static string ImportRequest(RootlessAppPackageSpec spec, PackageProvenance p, string sha256, bool complete)
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

    public static string DeviceTest(RootlessAppPackageSpec spec, IosAppBundle app, RootlessAppDebValidator.Facts f, HandoffContext? context = null, string? debName = null) => $"""
        # {spec.Name} {spec.Version}: device test and recovery

        Package `{spec.PackageId}` · `iphoneos-arm64` · SHA-256 `{f.Sha256}` · {f.Size:N0} bytes.
        Installs only `{f.AppPath}` (bundle ID `{f.BundleId}`, iOS {f.MinimumOS} or later). The postinst and postrm scripts only
        register or unregister the app with `uicache`. Nothing runs at boot, nothing is injected into other processes, and
        no respring is needed.

        ## Before you start
        - Rootless jailbreak (Dopamine, palera1n rootless) on iOS {f.MinimumOS} or later. Record: iPhone model, iOS version, jailbreak and version, package manager.
        - `uikittools` (provides `uicache`) is installed or offered as a dependency.
        - **No other build of the app is installed.** Over SSH: `uicache -l | grep {f.BundleId}` must print nothing (or only
          `{f.AppPath}`). A build from Xcode or an IPA with bundle ID `{f.BundleId}` must be deleted first (Home Screen › Remove App);
          its own data goes with it.{InstalledLine(context)}

        ## Install — before publication (this exact file)
        1. Copy `{debName ?? $"{spec.PackageId}_{spec.Version}_iphoneos-arm64.deb"}` from this folder to the phone (e.g. `scp` to `/var/mobile/`).
        2. Over SSH: `shasum -a 256 /var/mobile/{debName ?? $"{spec.PackageId}_{spec.Version}_iphoneos-arm64.deb"}` must print `{f.Sha256}`.
        3. `sudo dpkg -i /var/mobile/{debName ?? $"{spec.PackageId}_{spec.Version}_iphoneos-arm64.deb"}` (or open the file in your package manager and install).
        4. Expected: no errors, `dpkg -s {spec.PackageId}` shows `Status: install ok installed`, and the **{spec.Name}** icon appears.

        ## Install — after publication (Zebra)
        1. Sources › Add (+) › `{CandidateSource}`, then refresh.
        2. Search **{spec.Name}**, check the version is **{spec.Version}**, tap Get/Install, confirm and install.
        3. Expected: the install finishes without errors and the **{spec.Name}** icon appears on the home screen.

        ## Check
        1. Launch **{spec.Name}** from the home screen. Expected: it opens to its first screen without crashing.
        2. Optional over SSH: `dpkg -s {spec.PackageId}` shows `Status: install ok installed` and Version {spec.Version}.
        3. Record what you saw (works / crashes / icon missing) with the device details above.

        ## Uninstall
        1. Zebra › Installed › **{spec.Name}** › Remove, confirm (or `sudo dpkg -r {spec.PackageId}` over SSH).
        2. Expected: the icon disappears and `{f.AppPath}` no longer exists.

        ## Recovery
        - **Icon missing after install:** run `uicache -p {f.AppPath}` (or `uicache -a`), or respring.
        - **Icon stays after removal:** run `uicache -a`, or respring.
        - **The app crashes on launch:** it only affects this app. Copy the crash report (Settings › Privacy & Security › Analytics
          & Improvements › Analytics Data, entries starting with `{app.Executable}`), then remove the package as above.
        - **The package manager cannot remove it:** over SSH, `sudo dpkg -r {spec.PackageId}`, then `uicache -a`.
        - Nothing in this package changes SpringBoard or system files, so a safe-mode reboot is not needed for this package.

        """.Replace("\r", "");

    private static string InstalledLine(HandoffContext? context) =>
        context?.InstalledCheck is { } ic ? "\n  Maker's check at export: " + ic.State + " — " + ic.Summary : "";

    private static string Readme(RootlessAppPackageSpec spec, RootlessAppDebValidator.Facts f, IReadOnlyList<string> files, bool complete, IReadOnlyList<string> missing, HandoffContext? context)
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
        sb.Append(complete
            ? "\nTo submit: copy `import-request.toml` to HCG Repo `imports/`, `package.toml`, `CHANGELOG.md` and `icon.png` to `packages/" + spec.PackageId + "/`, push a branch; **Prepare packages** fetches and checks the pinned artifact.\n"
            : "\n**Not importable yet:** see `import-request.INCOMPLETE.toml`.\n");
        if (missing.Count > 0) { sb.Append("\nMissing:\n"); foreach (var m in missing) sb.Append("- ").Append(m).Append('\n'); }
        return sb.ToString();
    }
}
