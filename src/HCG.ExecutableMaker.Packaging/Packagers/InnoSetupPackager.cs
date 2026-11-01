using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// Builds a Windows Setup.exe with Inno Setup 6 (ISCC.exe): installs the self-contained publish output, creates a
/// Start-menu shortcut and an optional desktop shortcut, registers a proper uninstaller, and supports per-user or
/// all-users installation. ISCC runs only on Windows hosts; the catalog never offers this format elsewhere.
/// </summary>
public sealed class InnoSetupPackager(IProcessRunner runner) : IPackager
{
    public PackagingFormat Format => PackagingFormat.WindowsInstallerExe;

    /// <summary>Text the Inno Setup compiler embeds in every setup it produces.</summary>
    internal static readonly byte[] SetupDataMarker = "Inno Setup Setup Data"u8.ToArray();

    public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        if (request.Host.Platform != TargetPlatform.Windows)
            throw new InvalidOperationException("Inno Setup's compiler (ISCC.exe) runs only on Windows. Build on a Windows PC or use the Windows CI runner.");
        var iscc = request.Toolchains.Find(ToolchainIds.InnoSetup) is { IsAvailable: true, ExecutablePath: { } path } ? path
            : throw new InvalidOperationException("Inno Setup 6 (ISCC.exe) was not detected. Install it from https://jrsoftware.org/isinfo.php and refresh Toolchains.");
        var version = Version.TryParse(request.Toolchains.Find(ToolchainIds.InnoSetup)?.Version, out var v) ? v : null;

        // The script lives next to (not inside) the output so it can be reviewed; it holds no secrets.
        var setupName = request.ArtifactBaseName + "-Setup";
        var scriptDir = Path.Combine(request.OutputRoot, ".installer");
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, setupName + ".iss");
        var script = InnoSetupScript.Generate(new InnoSetupScript.Input
        {
            Settings = request.Settings,
            SourceDirectory = request.PublishDirectory,
            ExecutableFileName = request.ExecutableFileName,
            OutputDirectory = request.OutputRoot,
            OutputBaseFileName = setupName,
            SetupIconPath = request.Icons?.IcoPath,
            CompilerVersion = version,
        });
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);
        log.Info($"Inno Setup script: {scriptPath}");

        var result = await runner.RunAsync(new ProcessSpec
        {
            Executable = iscc,
            Arguments = ["/Qp", scriptPath],
            WorkingDirectory = scriptDir,
            Environment = BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment,
            Timeout = TimeSpan.FromMinutes(20),
        }, (_, line) => log.Info(line), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut) throw new InvalidOperationException("The Inno Setup compiler timed out.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.ExitCode == 2
                ? "The Inno Setup compiler reported a script error (exit code 2). See the build log for the line and message."
                : $"The Inno Setup compiler failed with exit code {result.ExitCode}.");

        var setupExe = Path.Combine(request.OutputRoot, setupName + ".exe");
        if (!File.Exists(setupExe))
            throw new InvalidOperationException($"ISCC reported success, but {Path.GetFileName(setupExe)} was not produced.");
        // Keep the generated script next to the installer: it documents exactly what the setup does.
        var keptScript = Path.Combine(request.OutputRoot, setupName + ".iss");
        File.Move(scriptPath, keptScript, overwrite: true);
        try { Directory.Delete(scriptDir); } catch (IOException) { }
        log.Info($"Installer script kept for review: {Path.GetFileName(keptScript)}");
        return [new PackagedOutput(Format, ArtifactKind.File, setupExe, Verify(setupExe, request))];
    }

    internal static List<VerificationCheck> Verify(string setupExe, PackagingRequest request)
    {
        var checks = new List<VerificationCheck>();
        var info = BinaryInspector.Inspect(setupExe);
        checks.Add(info.Format == BinaryFormat.PortableExecutable
            ? new VerificationCheck("Executable format", VerificationStatus.Passed, "Windows PE executable (Inno Setup loader)")
            : new VerificationCheck("Executable format", VerificationStatus.Failed, $"Not a Windows executable ({info.Format})."));

        var publishBytes = Directory.EnumerateFiles(request.PublishDirectory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var size = new FileInfo(setupExe).Length;
        checks.Add(size > 64 * 1024
            ? new VerificationCheck("Payload", VerificationStatus.Passed, $"{size:N0} bytes (compressed from {publishBytes:N0} bytes of application files)")
            : new VerificationCheck("Payload", VerificationStatus.Failed, $"Setup is only {size:N0} bytes; the application files are missing."));

        checks.Add(ContainsMarker(setupExe)
            ? new VerificationCheck("Inno Setup data", VerificationStatus.Passed, "Setup data block present")
            : new VerificationCheck("Inno Setup data", VerificationStatus.PassedWithWarnings, "The Inno Setup data marker was not found; the installer could not be confirmed statically."));

        checks.Add(HasAuthenticodeDirectory(setupExe)
            ? new VerificationCheck("Code signature", VerificationStatus.PassedWithWarnings, "An Authenticode signature block is present (not validated here).")
            : new VerificationCheck("Code signature", VerificationStatus.PassedWithWarnings,
                "Not code-signed. Windows SmartScreen will warn users (More info → Run anyway). Authenticode signing is planned for M0.4."));
        checks.Add(new VerificationCheck("Install test", VerificationStatus.NotVerifiable,
            "Not performed during the build (it would change this computer). Install/launch/uninstall is tested on the Windows CI runner."));
        return checks;
    }

    internal static bool ContainsMarker(string file)
    {
        using var fs = File.OpenRead(file);
        var buffer = new byte[1 << 20];
        var carry = 0;
        int read;
        while ((read = fs.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var span = buffer.AsSpan(0, carry + read);
            if (span.IndexOf(SetupDataMarker) >= 0) return true;
            carry = Math.Min(SetupDataMarker.Length - 1, span.Length);
            span[^carry..].CopyTo(buffer);
        }
        return false;
    }

    /// <summary>True when the PE optional header's security data directory (Authenticode) is non-empty.</summary>
    internal static bool HasAuthenticodeDirectory(string file)
    {
        using var fs = File.OpenRead(file);
        var head = new byte[Math.Min(fs.Length, 4096)];
        fs.ReadExactly(head);
        if (head.Length < 0x40 || head[0] != 'M' || head[1] != 'Z') return false;
        var pe = BitConverter.ToInt32(head, 0x3C);
        if (pe <= 0 || pe + 24 + 2 > head.Length) return false;
        var opt = pe + 24;
        var magic = BitConverter.ToUInt16(head, opt);
        var dirs = opt + (magic == 0x20B ? 112 : 96); // PE32+ : PE32
        var security = dirs + 4 * 8;
        return security + 8 <= head.Length && BitConverter.ToUInt32(head, security + 4) != 0;
    }
}
