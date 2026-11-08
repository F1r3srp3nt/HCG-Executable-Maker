using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Node;

/// <summary>
/// Whether a Node.js project can become a desktop app on THIS host with what HCG implements, and if not, exactly why.
/// Only host/target/packaging combinations HCG implements are offered.
/// </summary>
public static class NodeCapabilities
{
    public sealed record Assessment(SupportLevel Support, string Summary, IReadOnlyList<string> Remediation, IReadOnlyList<ToolchainRequirement> Requirements);

    /// <summary>One buildable combination: electron-builder platform flag, architecture, and the HCG formats it yields.</summary>
    public sealed record Combination(TargetPlatform Platform, TargetArchitecture Architecture, string BuilderPlatform, IReadOnlyList<PackagingFormat> Formats);

    public const string NodeSetupUrl = "https://nodejs.org/en/download";

    /// <summary>Implemented combinations for this host. macOS: arm64/x64 .app (+ zip, dmg); Linux: unpacked app folder.</summary>
    public static IReadOnlyList<Combination> Combinations(HostEnvironment host) => host.Platform switch
    {
        TargetPlatform.MacOS =>
        [
            new(TargetPlatform.MacOS, TargetArchitecture.Arm64, "mac", [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg]),
            new(TargetPlatform.MacOS, TargetArchitecture.X64, "mac", [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg]),
        ],
        TargetPlatform.Linux =>
        [
            new(TargetPlatform.Linux, host.Architecture == TargetArchitecture.Arm64 ? TargetArchitecture.Arm64 : TargetArchitecture.X64, "linux", [PackagingFormat.PortableFolder]),
        ],
        _ => [],
    };

    /// <summary>electron-builder target name for an HCG format.</summary>
    public static string? BuilderTarget(PackagingFormat format) => format switch
    {
        PackagingFormat.MacAppBundle or PackagingFormat.PortableFolder => "dir",
        PackagingFormat.PortableZip => "zip",
        PackagingFormat.MacDmg => "dmg",
        _ => null,
    };

    /// <summary>The newest Node.js installation that satisfies the project's engines.node (null + reason when none does).</summary>
    public static (NodeInstallation? Node, string Detail) SelectNode(ToolchainReport? report, string? enginesNode)
    {
        var installs = report?.NodeInstallations ?? [];
        if (installs.Count == 0)
            return (null, "Node.js was not found (looked on PATH, /opt/homebrew, /usr/local, nvm, Volta, fnm, asdf). Install Node.js from " + NodeSetupUrl + ", then press Refresh.");
        var unreadable = false;
        foreach (var i in installs)
        {
            var r = NodeVersionRange.Check(i.Version, enginesNode);
            if (r == NodeVersionRange.Result.Satisfied) return (i, $"Node.js {i.Version} at {i.NodePath} ({i.Source})");
            if (r == NodeVersionRange.Result.Unreadable) unreadable = true;
        }
        var found = string.Join(", ", installs.Select(i => $"{i.Version} ({i.Source})"));
        return unreadable
            ? (null, $"HCG cannot read the project's engines.node range \"{enginesNode}\", so it cannot confirm a compatible Node.js. Found: {found}.")
            : (null, $"The project requires Node.js {enginesNode}; found {found}. Install a matching Node.js (" + NodeSetupUrl + ") or select it with nvm/Volta, then press Refresh.");
    }

    public static Assessment Evaluate(NodeProjectInfo project, ToolchainReport? report)
    {
        var requirements = new List<ToolchainRequirement>();
        var fixes = new List<string>(project.MissingForDesktop);
        if (project.Kind != NodeProjectKind.ElectronDesktop)
        {
            var what = project.Kind switch
            {
                NodeProjectKind.WebApp => "a browser app" + (project.Frameworks.Count > 0 ? $" ({string.Join(", ", project.Frameworks)})" : ""),
                NodeProjectKind.NodeService => "a Node.js server or command-line program",
                _ => "a Node.js library",
            };
            return new Assessment(SupportLevel.RequiresConfiguration,
                $"{project.DisplayName} ({project.DisplayDirectory}) is {what}, not an Electron desktop app, so HCG cannot package it as one yet.", fixes, requirements);
        }
        if (report is null)
            return new Assessment(SupportLevel.MissingToolchain, "Toolchains have not been detected yet.", ["Open the Toolchains page and press Refresh."], requirements);

        var combos = Combinations(report.Host);
        if (combos.Count == 0)
            return new Assessment(SupportLevel.RequiresDifferentHost,
                $"HCG packages Electron apps on macOS (arm64/x64 .app, zip, dmg) and Linux (unpacked app) in this version. Packaging on {report.Host.Platform.DisplayName()} is not implemented.",
                ["Build on a Mac to produce the macOS app."], requirements);

        var (node, nodeDetail) = SelectNode(report, project.EnginesNode);
        requirements.Add(new ToolchainRequirement(ToolchainIds.Node, "Node.js", project.EnginesNode, node is not null, nodeDetail));
        if (node is null) fixes.Add(nodeDetail);
        else
        {
            if (node.NpmCli is null)
            {
                requirements.Add(new ToolchainRequirement(ToolchainIds.Npm, "npm", project.EnginesNpm, false, $"npm was not found next to {node.NodePath}."));
                fixes.Add($"npm was not found next to Node.js {node.Version} ({node.NodePath}). Reinstall Node.js, which includes npm.");
            }
            else
            {
                var wanted = project.EnginesNpm;
                string? pinned = null;
                if (project.PackageManagerField is { } f && f.StartsWith("npm@", StringComparison.Ordinal)) pinned = f[4..].Split('+')[0];
                var npmOk = node.NpmVersion is { } nv
                    && NodeVersionRange.Check(nv, wanted) == NodeVersionRange.Result.Satisfied
                    && (pinned is null || NodeVersionRange.Check(nv, "^" + pinned.Split('.')[0]) == NodeVersionRange.Result.Satisfied);
                var detail = $"npm {node.NpmVersion ?? "(version unknown)"} ({node.NpmCli})" + (pinned is null ? "" : $"; packageManager pins npm@{pinned}");
                requirements.Add(new ToolchainRequirement(ToolchainIds.Npm, "npm", wanted ?? pinned, npmOk, detail));
                if (!npmOk)
                    fixes.Add(node.NpmVersion is null
                        ? $"The npm version could not be read ({node.NpmCli})."
                        : $"The project requires npm {wanted ?? "@" + pinned}; Node.js {node.Version} ships npm {node.NpmVersion}. Install a matching npm (npm install -g npm@<version>) or a Node.js that bundles it.");
            }
        }

        if (fixes.Count > 0)
        {
            var configGaps = project.MissingForDesktop.Count > 0;
            return new Assessment(configGaps ? SupportLevel.RequiresConfiguration : SupportLevel.MissingToolchain,
                configGaps ? $"Electron app found in {project.DisplayDirectory}, but it is not ready for a desktop build." : "Supported Electron app, but this computer is missing required tools.",
                fixes, requirements);
        }
        var targets = string.Join("; ", combos.Select(c => $"{c.Platform.DisplayName()} {c.Architecture.DisplayName()} ({string.Join(", ", c.Formats.Select(f => f.DisplayName()))})"));
        return new Assessment(SupportLevel.Supported,
            $"Electron {project.ElectronVersion} app, packaged with electron-builder {project.PackagerVersion}. Buildable here: {targets}.", [], requirements);
    }
}
