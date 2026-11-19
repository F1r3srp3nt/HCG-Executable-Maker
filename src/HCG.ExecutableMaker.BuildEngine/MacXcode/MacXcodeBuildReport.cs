using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Packagers;

namespace HCG.ExecutableMaker.BuildEngine.MacXcode;

/// <summary>
/// BUILD-REPORT.md and build-report.json for a native macOS app built with Xcode: where the source came from, the
/// exact xcodebuild commands and every setting HCG overrode, the toolchain, what the built bundle contains (read from
/// the bundle itself), how it is signed, the artifacts with SHA-256, and the licence files shipped with it. It states
/// that the app keeps its own identity: building it with HCG does not make it an HCG product. Runtime behaviour is not
/// claimed: the build does not launch the app.
/// </summary>
public static class MacXcodeBuildReport
{
    public sealed record Input(BuildRecord Build, BuildSettings Settings, ProjectRecord Project, MacXcodeBuildPlanner.Resolved App, MacXcodeTargetInfo Target,
        string OutputRoot, IReadOnlyList<MacXcodeBuildPlanner.Override> Overrides, IReadOnlyList<string> Commands, ToolchainReport Toolchains);

    public static async Task<IReadOnlyList<string>> WriteAsync(Input input, IProcessRunner runner, CancellationToken ct)
    {
        var (build, s, app) = (input.Build, input.Settings, input.App);
        var o = s.MacXcode!;
        var bundle = Path.Combine(input.OutputRoot, app.FullProductName);
        var facts = MacAppFacts.Read(bundle);
        var codesign = input.Toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath ?? "/usr/bin/codesign";
        var signature = await new MacCodeSigner(runner, codesign).DescribeAsync(bundle, ct).ConfigureAwait(false);
        var source = Source(input.Project);
        var licences = o.LicenseFiles.Select(Path.GetFileName).OfType<string>().Where(n => File.Exists(Path.Combine(input.OutputRoot, n))).ToList();

        var md = new StringBuilder();
        md.AppendLine($"# {facts.Label} {facts.ShortVersion} ({facts.BuildVersion}) — build report").AppendLine();
        md.AppendLine($"Built by HCG Executable Maker {HostInfo.ProductVersion} on {build.StartedAt:u} (build {build.Id:N}).");
        md.AppendLine($"**{facts.Label}** keeps its own identity ({facts.BundleIdentifier}), name, icon and licence. Building it with HCG Executable Maker does not make it an HCG product.").AppendLine();

        md.AppendLine("## Source").AppendLine();
        md.AppendLine($"- Imported from: `{Path.GetFileName(input.Project.SourceLocation)}`{(source.ArchiveSha256 is { } sha ? $" (SHA-256 `{sha}`)" : "")}");
        if (source.Comment is { } comment) md.AppendLine($"- Archive comment (GitHub writes the commit here): `{comment}`");
        md.AppendLine($"- Project: `{o.WorkspacePath ?? o.ProjectPath}` · scheme `{o.Scheme}` · target `{app.Target}` · configuration `{o.Configuration}`");
        md.AppendLine("- Built in a copy inside HCG's build folder; the imported source and its project settings were not modified.");
        md.AppendLine("- Scripts that ship with the project were not run (HCG calls xcodebuild directly).").AppendLine();

        md.AppendLine("## Toolchain").AppendLine();
        foreach (var key in new[] { "host", "macos", "xcode", "macos-sdk", "xcodebuild" })
            if (build.ToolchainVersions.TryGetValue(key, out var v)) md.AppendLine($"- {key}: {v}");
        md.AppendLine();

        md.AppendLine("## xcodebuild").AppendLine();
        md.AppendLine("Settings HCG passed on the command line (everything else comes from the project unchanged):").AppendLine();
        foreach (var x in input.Overrides) md.AppendLine($"- `{x.Setting}={x.Value}` — {x.Reason}");
        md.AppendLine().AppendLine("```");
        foreach (var c in input.Commands) md.AppendLine(c);
        md.AppendLine("```").AppendLine();

        md.AppendLine("## The app").AppendLine();
        md.AppendLine($"- Bundle: `{app.FullProductName}` · ID `{facts.BundleIdentifier}` · name `{facts.Name}` · display name `{facts.DisplayName ?? "(none)"}`");
        md.AppendLine($"- Version {facts.ShortVersion} (build {facts.BuildVersion}) · requires macOS {facts.MinimumSystemVersion} or later (linked for {string.Join(", ", facts.LinkedMinimum)})");
        md.AppendLine($"- Architectures: {string.Join(" + ", facts.Architectures)} ({o.Architecture.Label()}); {facts.MachOFiles.Count} Mach-O file(s) in the bundle");
        md.AppendLine($"- Icon: {(facts.HasIcon ? facts.IconName is not null ? $"{facts.IconName} (asset catalog)" : facts.IconFile : "none in the bundle")} · {facts.ResourceFiles} resource file(s)");
        md.AppendLine($"- Linked frameworks (project): {(input.Target.Frameworks.Count > 0 ? string.Join(", ", input.Target.Frameworks) : "none listed")}").AppendLine();

        md.AppendLine("## Signing and Gatekeeper").AppendLine();
        md.AppendLine($"- {MacSignatureDescription.Describe(signature)}");
        md.AppendLine($"- Project signing setting: {input.Target.SigningDescription}{(o.SignLocallyAdHoc ? " — overridden in HCG: signed ad hoc to run locally" : "")}.");
        md.AppendLine("- Notarized: no. HCG never removes the quarantine attribute and never changes Gatekeeper or other security settings.").AppendLine();

        md.AppendLine("## Artifacts").AppendLine();
        md.AppendLine("| File | Size | SHA-256 | Verification |").AppendLine("|---|---|---|---|");
        foreach (var a in build.Artifacts)
            md.AppendLine($"| `{a.FileName}`{(a.Kind == ArtifactKind.Directory ? " (folder; SHA-256 of its file manifest)" : "")} | {a.SizeBytes:N0} bytes | `{a.Sha256}` | {a.Verification} |");
        md.AppendLine();
        foreach (var a in build.Artifacts)
            foreach (var c in a.Checks)
                md.AppendLine($"- {a.FileName} — {c.Name}: {c.Status} — {c.Detail}");
        md.AppendLine();

        md.AppendLine("## Licence").AppendLine();
        md.AppendLine(licences.Count > 0
            ? $"Shipped unchanged next to the app and inside the DMG: {string.Join(", ", licences.Select(l => $"`{l}`"))}. The corresponding source is the imported archive named above."
            : "The project ships no licence file.");
        md.AppendLine().AppendLine("## Not verified by the build").AppendLine();
        md.AppendLine("The build does not launch the app. Launching it and its own features (for example applying a wallpaper) are runtime checks, reported separately.");

        var mdPath = Path.Combine(input.OutputRoot, "BUILD-REPORT.md");
        await File.WriteAllTextAsync(mdPath, md.ToString(), ct).ConfigureAwait(false);
        var jsonPath = Path.Combine(input.OutputRoot, "build-report.json");
        await File.WriteAllTextAsync(jsonPath, HcgJson.Serialize(new
        {
            buildId = build.Id,
            builtBy = $"HCG Executable Maker {HostInfo.ProductVersion}",
            builtAt = build.StartedAt,
            source = new { imported = Path.GetFileName(input.Project.SourceLocation), archiveSha256 = source.ArchiveSha256, archiveComment = source.Comment, project = o.WorkspacePath ?? o.ProjectPath, o.Scheme, target = app.Target, o.Configuration },
            toolchain = build.ToolchainVersions,
            overrides = input.Overrides.Select(x => new { x.Setting, x.Value, x.Reason }),
            commands = input.Commands,
            app = new
            {
                bundle = app.FullProductName, facts.BundleIdentifier, facts.Name, facts.DisplayName, version = facts.ShortVersion, build = facts.BuildVersion,
                minimumMacOS = facts.MinimumSystemVersion, architectures = facts.Architectures, machOFiles = facts.MachOFiles, icon = facts.HasIcon, facts.ResourceFiles,
            },
            signing = new { kind = MacSignatureDescription.Classify(signature).ToString(), description = MacSignatureDescription.Describe(signature), teamId = signature?.TeamId, notarized = false },
            artifacts = build.Artifacts.Select(a => new { file = a.FileName, kind = a.Kind.ToString(), a.SizeBytes, a.Sha256, verification = a.Verification.ToString(), checks = a.Checks }),
            licences,
            runtimeVerified = false,
        }), ct).ConfigureAwait(false);
        return [mdPath, jsonPath];
    }

    private static (string? ArchiveSha256, string? Comment) Source(ProjectRecord project)
    {
        var archive = project.PreservedArchivePath is { } p && File.Exists(p) ? p
            : Directory.Exists(Path.Combine(project.WorkspacePath, "original")) ? Directory.EnumerateFiles(Path.Combine(project.WorkspacePath, "original")).FirstOrDefault() : null;
        if (archive is null) return (null, null);
        string? sha = null, comment = null;
        try
        {
            using (var fs = File.OpenRead(archive)) sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
            using var zip = ZipFile.OpenRead(archive);
            comment = zip.Comment is { Length: > 0 and <= 200 } c && c.All(ch => !char.IsControl(ch)) ? c : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return (sha, comment);
    }
}
