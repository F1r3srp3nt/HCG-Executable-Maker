using System.Text;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.Icons;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.Packaging.Windows;

namespace HCG.ExecutableMaker.BuildEngine.Windows;

/// <summary>
/// Packages an already-built Windows application folder (a <c>dotnet publish</c> output, or any folder with the
/// program and its files) into Executable Maker's Windows outputs, without building:
/// <list type="bullet">
/// <item>the Inno Setup installer (Setup.exe), a portable ZIP, or a folder;</item>
/// <item>the same verification checks, SHA-256 checksums and SHA256SUMS.txt;</item>
/// <item>a Release Passport for each file.</item>
/// </list>
/// The application's identity (name, publisher, version, ID, icon) is the caller's: nothing about Executable Maker
/// is put into the product. The folder is read, never changed. When signing is approved, the program is signed in a
/// staging copy before packaging, and the Setup.exe after.
/// </summary>
public sealed class WindowsPackagingService(PackagingRegistry packagers, IProcessRunner runner, TimeProvider time)
{
    public sealed record Signing(string SignTool, string Thumbprint, string TimestampUrl);

    public sealed record Request
    {
        public required string AppDirectory { get; init; }
        /// <summary>The program to start, relative to <see cref="AppDirectory"/> (e.g. HCGMail.exe).</summary>
        public required string ExecutableFileName { get; init; }
        public required AppIdentity Identity { get; init; }
        public required TargetArchitecture Architecture { get; init; }
        public required IReadOnlyList<PackagingFormat> Formats { get; init; }
        public required string OutputDirectory { get; init; }
        public required ToolchainReport Toolchains { get; init; }
        public WindowsInstallerOptions Installer { get; init; } = new();
        public bool AllowOverwriteOutput { get; init; }
        public string? BuildNumber { get; init; }
        public SourceProvenance Source { get; init; } = SourceProvenance.Unknown;
        public string Provider { get; init; } = "executable-maker-local";
        /// <summary>How the folder was built (e.g. ".NET SDK 10.0.100"), as the caller knows it; Maker and Inno Setup are added.</summary>
        public string? BuiltWith { get; init; }
        public string? ProductKey { get; init; }
        public string? Edition { get; init; }
        /// <summary>Signing approved by the person for this run (null: nothing is signed).</summary>
        public Signing? ApprovedSigning { get; init; }
    }

    public sealed record Result(bool Succeeded, string? Failure, string? OutputRoot, IReadOnlyList<ArtifactRecord> Artifacts, IReadOnlyList<string> Passports);

    public static TargetArchitecture? ArchitectureOf(PeFile pe) => pe.MachineName switch { "x64" => TargetArchitecture.X64, "arm64" => TargetArchitecture.Arm64, _ => null };

    /// <summary>What is wrong with the request before anything is written; null when it can be packaged.</summary>
    public static string? Problem(Request q)
    {
        if (!Directory.Exists(q.AppDirectory)) return $"The application folder {q.AppDirectory} does not exist.";
        if (q.ExecutableFileName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(q.ExecutableFileName)) return "The program is named relative to the application folder.";
        var exe = Path.Combine(q.AppDirectory, q.ExecutableFileName);
        if (!File.Exists(exe)) return $"{q.ExecutableFileName} is not in {q.AppDirectory}.";
        if (PeFile.Read(exe) is not { } pe) return $"{q.ExecutableFileName} is not a Windows program (no PE header).";
        if (pe.IsDll) return $"{q.ExecutableFileName} is a DLL, not a program.";
        if (ArchitectureOf(pe) is not { } arch) return $"{q.ExecutableFileName} is {pe.MachineName}; Executable Maker packages x64 and ARM64 Windows programs.";
        if (arch != q.Architecture) return $"{q.ExecutableFileName} is {pe.MachineName}, not {q.Architecture.ToString().ToLowerInvariant()}: package each architecture from its own build.";
        if (string.IsNullOrWhiteSpace(q.Identity.ApplicationName) || string.IsNullOrWhiteSpace(q.Identity.Version) || string.IsNullOrWhiteSpace(q.Identity.ApplicationId))
            return "The application's name, version and ID are required (they name the files and the installer's upgrade identity).";
        if (q.Formats.Count == 0 || q.Formats.Any(f => f is not (PackagingFormat.PortableZip or PackagingFormat.WindowsInstallerExe or PackagingFormat.PortableFolder)))
            return "Windows outputs are installer (Setup.exe), zip and folder.";
        if (q.ApprovedSigning is not null && !OperatingSystem.IsWindows()) return "Authenticode signing uses signtool on Windows; nothing was packaged.";
        return null;
    }

    public async Task<Result> PackageAsync(Request q, Action<string> log, CancellationToken ct = default)
    {
        if (Problem(q) is { } problem) return new Result(false, problem, null, [], []);
        var host = q.Toolchains.Host;
        var settings = new BuildSettings
        {
            ProjectRelativePath = "(already built) " + Path.GetFileName(Path.TrimEndingDirectorySeparator(q.AppDirectory)),
            Platform = TargetPlatform.Windows, Architecture = q.Architecture, OutputDirectory = q.OutputDirectory,
            Identity = q.Identity, PackagingFormats = q.Formats, WindowsInstaller = q.Installer,
        };
        var work = Directory.CreateTempSubdirectory("hcg-winpack-").FullName;
        try
        {
            var appDir = PathGuard.GetRealPath(q.AppDirectory);
            if (q.ApprovedSigning is { } signing)
            {
                // Signed in a copy: the person's folder is never changed.
                var staged = Path.Combine(work, "app");
                FileOps.CopyDirectory(appDir, staged, ct);
                appDir = staged;
                var target = Path.Combine(appDir, q.ExecutableFileName);
                var signed = await AuthenticodeSigner.SignAsync(new AuthenticodeSigner.Request(target, signing.SignTool, signing.Thumbprint, signing.TimestampUrl,
                    AuthenticodeSigner.Sha256(target), q.Identity.ApplicationName), runner, ct).ConfigureAwait(false);
                log($"Signing {q.ExecutableFileName}: {(signed.Succeeded ? "" : "FAILED — ")}{signed.Message}");
                if (!signed.Succeeded) return new Result(false, $"{q.ExecutableFileName} could not be signed: {signed.Message}", null, [], []);
            }
            GeneratedIcons? icons = null;
            if (q.Identity.IconSourcePath is { } icon)
            {
                icons = IconGenerator.Generate(icon, Path.Combine(work, "icons"), "AppIcon", ico: true, icns: false);
                foreach (var w in icons.Warnings) log("Icon: " + w);
            }
            var baseName = BuildService.ArtifactBaseName(settings);
            var outputRoot = BuildService.PrepareOutputRoot(q.OutputDirectory, baseName, q.AllowOverwriteOutput);
            var request = new PackagingRequest
            {
                Settings = settings, PublishDirectory = appDir, ExecutableFileName = q.ExecutableFileName, OutputRoot = outputRoot,
                ArtifactBaseName = baseName, Icons = icons, Host = host, Toolchains = q.Toolchains,
            };
            var plog = new Log(log);
            var outputs = new List<PackagedOutput>();
            foreach (var format in PackagingCatalog.WithDependencies(q.Formats, settings))
            {
                var packager = packagers.Get(format, settings) ?? throw new InvalidOperationException($"{format.DisplayName()} is not available in this Executable Maker.");
                log($"Packaging: {format.DisplayName()}");
                outputs.AddRange(await packager.PackageAsync(request, plog, ct).ConfigureAwait(false));
            }
            if (q.ApprovedSigning is { } s2)
                for (var i = 0; i < outputs.Count; i++)
                    if (outputs[i].Format == PackagingFormat.WindowsInstallerExe)
                    {
                        var setup = outputs[i].Path;
                        var signed = await AuthenticodeSigner.SignAsync(new AuthenticodeSigner.Request(setup, s2.SignTool, s2.Thumbprint, s2.TimestampUrl,
                            AuthenticodeSigner.Sha256(setup), q.Identity.ApplicationName + " Setup"), runner, ct).ConfigureAwait(false);
                        log($"Signing {Path.GetFileName(setup)}: {(signed.Succeeded ? "" : "FAILED — ")}{signed.Message}");
                        if (!signed.Succeeded) return new Result(false, $"{Path.GetFileName(setup)} could not be signed: {signed.Message}", outputRoot, [], []);
                        outputs[i] = outputs[i] with { Checks = InnoSetupPackager.Verify(setup, request) };
                    }

            var built = time.GetUtcNow();
            var artifacts = new List<ArtifactRecord>();
            var sums = new StringBuilder();
            var buildId = Guid.NewGuid();
            foreach (var o in outputs)
            {
                string sha; long size;
                if (o.Kind == ArtifactKind.Directory)
                {
                    var (digest, manifest, total) = await Checksums.Sha256DirectoryAsync(o.Path, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(outputRoot, Path.GetFileName(o.Path) + ".manifest.sha256.txt"), manifest, ct).ConfigureAwait(false);
                    (sha, size) = (digest, total);
                }
                else
                {
                    sha = await Checksums.Sha256FileAsync(o.Path, ct).ConfigureAwait(false);
                    size = new FileInfo(o.Path).Length;
                    sums.Append(sha).Append("  ").Append(Path.GetFileName(o.Path)).Append('\n');
                }
                var status = Checks.Combine(o.Checks);
                artifacts.Add(new ArtifactRecord
                {
                    Id = Guid.NewGuid(), BuildId = buildId, Format = o.Format, Kind = o.Kind, Path = o.Path, SizeBytes = size, Sha256 = sha,
                    Verification = status, Checks = o.Checks, CreatedAt = built,
                });
                log($"{Path.GetFileName(o.Path)} — {size:N0} bytes — SHA-256 {sha} — verification {status}");
                foreach (var c in o.Checks.Where(c => c.Status != VerificationStatus.Passed)) log($"  {c.Status}: {c.Name} — {c.Detail}");
            }
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "SHA256SUMS.txt"), sums.ToString(), ct).ConfigureAwait(false);
            if (artifacts.Any(a => a.Verification == VerificationStatus.Failed))
                return new Result(false, "Verification failed: " + string.Join("; ", artifacts.SelectMany(a => a.Checks).Where(c => c.Status == VerificationStatus.Failed).Select(c => $"{c.Name}: {c.Detail}")), outputRoot, artifacts, []);

            var passports = WritePassports(q, appDir, artifacts, built, log);
            return new Result(true, null, outputRoot, artifacts, passports);
        }
        finally { try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>A Release Passport (build half) next to each installer and ZIP, from the files' own bytes.</summary>
    internal static IReadOnlyList<string> WritePassports(Request q, string appDir, IReadOnlyList<ArtifactRecord> artifacts, DateTimeOffset built, Action<string> log)
    {
        var exe = Path.Combine(appDir, q.ExecutableFileName);
        var app = PeFile.Read(exe)!;
        var programs = Directory.EnumerateFiles(appDir, "*.exe", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("createdump.exe", StringComparison.OrdinalIgnoreCase))
            .Select(f => Authenticode.Inspect(f) with { Path = Path.GetRelativePath(appDir, f).Replace('\\', '/') }).ToList();
        var inno = q.Toolchains.Find(ToolchainIds.InnoSetup);
        var toolchain = string.Join("; ", new[] { q.BuiltWith, "HCG Executable Maker " + HostInfo.ProductVersion,
            q.Formats.Contains(PackagingFormat.WindowsInstallerExe) && inno?.Version is { } iv ? "Inno Setup " + iv : null }.OfType<string>());
        var arch = q.Architecture == TargetArchitecture.Arm64 ? "arm64" : "x64";
        var written = new List<string>();
        foreach (var a in artifacts.Where(a => a.Kind == ArtifactKind.File && a.Format is PackagingFormat.WindowsInstallerExe or PackagingFormat.PortableZip))
        {
            var installer = a.Format == PackagingFormat.WindowsInstallerExe;
            var passport = WindowsPassport.ForArtifact(new WindowsPassport.Facts(a, q.Identity.ApplicationName, q.Identity.ApplicationId, q.Identity.Version, q.BuildNumber, arch,
                q.Source, q.Provider, toolchain.Length > 200 ? toolchain[..200] : toolchain, built, app, q.ExecutableFileName.Replace('\\', '/'),
                installer ? PeFile.Read(a.Path) : null, installer ? Authenticode.Inspect(a.Path) with { Path = a.FileName } : null, programs, q.ProductKey, q.Edition));
            if (ReleasePassport.ValidateBuildHalf(passport) is { Count: > 0 } problems)
            {
                log($"Release Passport for {a.FileName} not written: {string.Join("; ", problems.Take(3))}");
                continue;
            }
            var file = a.Path + ".release-passport.json";
            File.WriteAllText(file, passport.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
            log($"Release Passport: {Path.GetFileName(file)} ({arch}; code signing {passport["code_signing"]!["status"]}; source {q.Source.Commit?[..Math.Min(12, q.Source.Commit.Length)] ?? "no commit"})");
            written.Add(file);
        }
        return written;
    }

    private sealed class Log(Action<string> log) : IPackagingLog
    {
        public void Info(string message) => log(message);
        public void Warning(string message) => log("WARNING: " + message);
    }
}
