using System.IO.Compression;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Packagers;

public sealed class PortableZipPackager : IPackager
{
    public PackagingFormat Format => PackagingFormat.PortableZip;
    public bool Handles(BuildSettings settings) => settings.Swift is not { Kind: SwiftAppKind.GuiApp } && settings.Electron is null && settings.Native is not { IsMacBundle: true }; // Swift GUI apps: SwiftAppZipPackager; Electron: ElectronZipPackager

    public Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var zipPath = Path.Combine(request.OutputRoot, request.ArtifactBaseName + ".zip");
        var rootName = request.ArtifactBaseName;
        log.Info($"Creating {Path.GetFileName(zipPath)}");
        var files = Directory.EnumerateFiles(request.PublishDirectory, "*", SearchOption.AllDirectories)
            .Select(f => (f, Rel: Path.GetRelativePath(request.PublishDirectory, f).Replace('\\', '/')))
            .OrderBy(x => x.Rel, StringComparer.Ordinal)
            .Select(x => (x.f, $"{rootName}/{x.Rel}", FileOps.IsUnixExecutable(x.Rel, request.ExecutableFileName, request.Settings.Platform)))
            .ToList();
        UnixZip.Create(zipPath, files, [rootName], cancellationToken);
        return (IReadOnlyList<PackagedOutput>)[new PackagedOutput(Format, ArtifactKind.File, zipPath, Verify(zipPath, rootName, files.Count, request))];
    }, cancellationToken);

    internal static List<VerificationCheck> Verify(string zipPath, string rootName, int expectedFiles, PackagingRequest request)
    {
        var checks = new List<VerificationCheck>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var fileEntries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            checks.Add(fileEntries.Count == expectedFiles
                ? new VerificationCheck("Archive integrity", VerificationStatus.Passed, $"{fileEntries.Count} files readable")
                : new VerificationCheck("Archive integrity", VerificationStatus.Failed, $"Expected {expectedFiles} entries, found {fileEntries.Count}."));
            var exeEntry = zip.GetEntry($"{rootName}/{request.ExecutableFileName}");
            if (exeEntry is null)
            {
                checks.Add(new VerificationCheck("Executable present", VerificationStatus.Failed, $"{request.ExecutableFileName} not found in archive."));
            }
            else
            {
                using (var s = exeEntry.Open())
                {
                    var header = new byte[Math.Min(exeEntry.Length, 64 * 1024)];
                    s.ReadExactly(header);
                    var info = BinaryInspector.Inspect(header);
                    checks.Add(info.Platform == request.Settings.Platform && (info.Architecture is null || info.Architecture == request.Settings.Architecture)
                        ? new VerificationCheck("Executable format", VerificationStatus.Passed, $"{info.Format} {info.Architecture?.DisplayName()}")
                        : new VerificationCheck("Executable format", VerificationStatus.Failed, $"Unexpected executable format: {info.Format} {info.Architecture?.DisplayName()}"));
                }
                if (request.Settings.Platform != TargetPlatform.Windows)
                {
                    var mode = UnixZip.GetMode(exeEntry);
                    checks.Add((mode & 0b001_000_000) != 0
                        ? new VerificationCheck("Executable permission", VerificationStatus.Passed, $"Recorded mode {Convert.ToString(mode, 8)}")
                        : new VerificationCheck("Executable permission", VerificationStatus.Failed, "Executable bit not recorded in archive."));
                }
            }
        }
        catch (InvalidDataException ex)
        {
            checks.Add(new VerificationCheck("Archive integrity", VerificationStatus.Failed, ex.Message));
        }
        return checks;
    }
}
