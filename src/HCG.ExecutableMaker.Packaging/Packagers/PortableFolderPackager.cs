using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Packagers;

public sealed class PortableFolderPackager : IPackager
{
    public PackagingFormat Format => PackagingFormat.PortableFolder;

    public Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var target = Path.Combine(request.OutputRoot, request.ArtifactBaseName);
        log.Info($"Copying self-contained output to {target}");
        FileOps.CopyDirectory(request.PublishDirectory, target, cancellationToken);
        var exe = Path.Combine(target, request.ExecutableFileName);
        if (request.Settings.Platform != TargetPlatform.Windows && File.Exists(exe)) FileOps.MarkExecutable(exe);

        var checks = new List<VerificationCheck>
        {
            File.Exists(exe)
                ? new VerificationCheck("Executable present", VerificationStatus.Passed, request.ExecutableFileName)
                : new VerificationCheck("Executable present", VerificationStatus.Failed, $"{request.ExecutableFileName} is missing."),
            BinaryInspector.CheckMatchesTarget(exe, request.Settings.Platform, request.Settings.Architecture),
        };
        if (File.Exists(exe)) checks.Add(Checks.ExecutablePermission(exe, request.Settings.Platform));
        var publishedCount = Directory.EnumerateFiles(request.PublishDirectory, "*", SearchOption.AllDirectories).Count();
        var copiedCount = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count();
        checks.Add(publishedCount == copiedCount
            ? new VerificationCheck("File count", VerificationStatus.Passed, $"{copiedCount} files")
            : new VerificationCheck("File count", VerificationStatus.Failed, $"Expected {publishedCount} files, found {copiedCount}."));
        checks.Add(Checks.LaunchNotPerformed());
        return (IReadOnlyList<PackagedOutput>)[new PackagedOutput(Format, ArtifactKind.Directory, target, checks)];
    }, cancellationToken);
}
