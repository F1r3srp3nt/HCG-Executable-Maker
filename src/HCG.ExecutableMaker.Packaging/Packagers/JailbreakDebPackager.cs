using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Theos;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// The Debian package a Theos build produced, copied to the output folder, verified (<see cref="TheosDebVerifier"/>) and
/// accompanied by INSTALL.txt (how to get it onto a jailbroken device yourself) and PACKAGE-REPORT.txt (control fields,
/// payload, binary slices). HCG never installs the package, resprings or connects to a device.
/// </summary>
public sealed class JailbreakDebPackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.JailbreakDeb;
    public bool Handles(BuildSettings settings) => settings.Native is { Kind: NativeProjectKind.Theos };

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        var choice = request.Settings.Native?.TheosScheme ?? throw new InvalidOperationException("The Theos package configuration is missing from the build settings.");
        var source = Path.Combine(request.PublishDirectory, request.ExecutableFileName);
        if (!File.Exists(source) || !source.EndsWith(".deb", StringComparison.Ordinal))
            throw new InvalidOperationException($"The Theos build produced no .deb ({request.ExecutableFileName}).");
        var dest = Path.Combine(request.OutputRoot, Path.GetFileName(source));
        File.Copy(source, dest, overwrite: true);
        log.Info($"Checking {Path.GetFileName(dest)} for the {choice.Label} configuration (read only; nothing is installed)");
        var result = await TheosDebVerifier.VerifyAsync(dest, choice, runner, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(request.OutputRoot, "PACKAGE-REPORT.txt"), result.Report, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(request.OutputRoot, "INSTALL.txt"), Instructions(Path.GetFileName(dest), choice, result), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return [new PackagedOutput(Format, ArtifactKind.File, dest, result.Checks)];
    }

    public static string Instructions(string debName, TheosSchemeChoice choice, TheosDebVerifier.Result result)
    {
        var depends = result.Control.GetValueOrDefault("Depends");
        var sb = new StringBuilder();
        sb.AppendLine($"{debName}");
        sb.AppendLine($"Package {result.Control.GetValueOrDefault("Package")} {result.Control.GetValueOrDefault("Version")} · {result.Control.GetValueOrDefault("Architecture")} · {choice.Label}");
        sb.AppendLine();
        sb.AppendLine("This is a Debian package for a JAILBROKEN iOS device. It is not an IPA and cannot be installed from the App Store, TestFlight or by IPA sideloading.");
        sb.AppendLine($"It was built for the {choice.PackageScheme} layout (files under {new TheosScheme { PackageScheme = choice.PackageScheme }.InstallPrefix}) and iOS {choice.DeploymentTarget ?? "(Theos default)"} or later, for {string.Join(" + ", choice.Architectures.DefaultIfEmpty("the project's architectures"))}.");
        sb.AppendLine("Install it only on a device whose jailbreak uses that layout. HCG Executable Maker did not install it, did not respring, and did not connect to any device.");
        sb.AppendLine();
        sb.AppendLine("To install it yourself:");
        sb.AppendLine($"  1. Copy {debName} to the device (AirDrop, Files app, or scp over SSH).");
        sb.AppendLine("  2. Open it with your package manager (Sileo or Zebra: \"Install\"), or with Filza (tap the file, then Install),");
        sb.AppendLine($"     or over SSH as root: dpkg -i {debName}  (then apt-get -f install to fetch missing dependencies).");
        if (depends is { Length: > 0 }) sb.AppendLine($"  3. Dependencies the package declares: {depends}. Your package manager installs them from its repositories.");
        sb.AppendLine("  4. Respring when you choose to (e.g. from your package manager), only after reading what the package does.");
        sb.AppendLine();
        sb.AppendLine("Checks performed by HCG (see PACKAGE-REPORT.txt for the full control file, payload and binary details):");
        foreach (var c in result.Checks) sb.AppendLine($"  [{c.Status}] {c.Name}: {c.Detail}");
        return sb.ToString();
    }
}
