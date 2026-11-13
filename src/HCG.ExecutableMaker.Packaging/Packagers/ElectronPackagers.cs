using System.IO.Compression;
using System.Text.Json;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// Electron builds: electron-builder has already produced the app (and its zip/dmg) in HCG's staging folder
/// (<see cref="PackagingRequest.PublishDirectory"/>). These packagers copy each requested artifact into the output folder
/// and verify it: the .app structure, the executable's architecture, the app.asar contents (package.json and its main
/// entry), the code signature, and the zip/dmg integrity. They never re-sign or modify what electron-builder produced.
/// </summary>
public abstract class ElectronPackagerBase(IProcessRunner runner) : IPackager
{
    public abstract PackagingFormat Format { get; }
    public bool Handles(BuildSettings settings) => settings.Electron is not null;
    protected IProcessRunner Runner => runner;

    public abstract Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken);

    /// <summary>Copies a directory tree keeping symbolic links as links (Electron frameworks depend on them) and Unix modes.</summary>
    public static async Task CopyTreeAsync(IProcessRunner runner, string source, string destination, HostEnvironment host, CancellationToken ct)
    {
        if (host.Platform == TargetPlatform.MacOS && File.Exists("/usr/bin/ditto"))
        {
            var r = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = [source, destination], Timeout = TimeSpan.FromMinutes(10) }, null, ct).ConfigureAwait(false);
            if (!r.Succeeded) throw new InvalidOperationException($"ditto could not copy {Path.GetFileName(source)} (exit code {r.ExitCode}).");
            return;
        }
        CopyTreeManaged(source, destination, ct);
    }

    public static void CopyTreeManaged(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo) Directory.CreateSymbolicLink(target, link);
                else File.CreateSymbolicLink(target, link);
            }
            else if (entry is DirectoryInfo d) CopyTreeManaged(d.FullName, target, ct);
            else
            {
                File.Copy(entry.FullName, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName));
            }
        }
    }

    /// <summary>app.asar (or an unpacked app/ folder): package.json present, its main entry packaged.</summary>
    public static IReadOnlyList<VerificationCheck> VerifyAppPayload(string resourcesDir, ElectronBuildOptions options)
    {
        var checks = new List<VerificationCheck>();
        var asarPath = Path.Combine(resourcesDir, "app.asar");
        var folder = Path.Combine(resourcesDir, "app");
        string? packageJson = null;
        Func<string, bool> contains;
        string where;
        if (File.Exists(asarPath))
        {
            Asar.Archive archive;
            try { archive = Asar.Open(asarPath); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                checks.Add(new VerificationCheck("App archive", VerificationStatus.Failed, $"Resources/app.asar could not be read: {ex.Message}"));
                return checks;
            }
            var bytes = archive.ReadFile("package.json");
            packageJson = bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
            contains = rel => archive.Contains(rel) || File.Exists(Path.Combine(resourcesDir, "app.asar.unpacked", rel));
            where = "app.asar";
        }
        else if (Directory.Exists(folder))
        {
            var pj = Path.Combine(folder, "package.json");
            packageJson = File.Exists(pj) ? File.ReadAllText(pj) : null;
            contains = rel => File.Exists(Path.Combine(folder, rel));
            where = "app/";
        }
        else
        {
            checks.Add(new VerificationCheck("App archive", VerificationStatus.Failed, "Neither Resources/app.asar nor Resources/app/ exists: the app's code was not packaged."));
            return checks;
        }
        if (packageJson is null)
        {
            checks.Add(new VerificationCheck("App archive", VerificationStatus.Failed, $"{where} has no package.json."));
            return checks;
        }
        string? main;
        try
        {
            using var doc = JsonDocument.Parse(packageJson);
            main = doc.RootElement.TryGetProperty("main", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "index.js";
        }
        catch (JsonException ex)
        {
            checks.Add(new VerificationCheck("App archive", VerificationStatus.Failed, $"{where}/package.json is not valid JSON: {ex.Message}"));
            return checks;
        }
        var mainRel = (main ?? "index.js").TrimStart('.', '/');
        checks.Add(contains(mainRel)
            ? new VerificationCheck("App archive", VerificationStatus.Passed, $"{where} contains package.json and its main entry {mainRel}.")
            : new VerificationCheck("App archive", VerificationStatus.Failed, $"{where}/package.json names main {mainRel}, but that file is not in the package: the app would not start."));
        if (options.MainEntry is { } expected && !string.Equals(expected.TrimStart('.', '/'), mainRel, StringComparison.Ordinal))
            checks.Add(new VerificationCheck("Main entry", VerificationStatus.PassedWithWarnings, $"The packaged main is {mainRel}; the project's package.json declares {expected} (electron-builder extraMetadata may override it)."));
        return checks;
    }

    protected static string StagingPath(PackagingRequest request, string relative) =>
        Path.GetFullPath(Path.Combine(request.PublishDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
}

/// <summary>The macOS .app electron-builder produced (mac-arm64/Name.app), verified and copied to the output folder.</summary>
public sealed class ElectronAppBundlePackager(IProcessRunner runner) : ElectronPackagerBase(runner)
{
    public override PackagingFormat Format => PackagingFormat.MacAppBundle;

    public override async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        var source = StagingPath(request, request.ExecutableFileName);
        if (!source.EndsWith(".app", StringComparison.Ordinal) || !Directory.Exists(source))
            throw new InvalidOperationException($"electron-builder did not produce the expected .app ({request.ExecutableFileName}).");
        var dest = Path.Combine(request.OutputRoot, Path.GetFileName(source));
        log.Info($"Copying {Path.GetFileName(source)} from electron-builder's output");
        await CopyTreeAsync(Runner, source, dest, request.Host, cancellationToken).ConfigureAwait(false);
        var checks = await VerifyAsync(Runner, dest, request.Settings, request.Toolchains, request.Host, cancellationToken).ConfigureAwait(false);
        return [new PackagedOutput(Format, ArtifactKind.Directory, dest, checks)];
    }

    public static async Task<IReadOnlyList<VerificationCheck>> VerifyAsync(IProcessRunner runner, string app, BuildSettings settings, ToolchainReport toolchains, HostEnvironment host, CancellationToken ct)
    {
        var checks = new List<VerificationCheck>();
        var contents = Path.Combine(app, "Contents");
        var plistPath = Path.Combine(contents, "Info.plist");
        if (!File.Exists(plistPath))
        {
            checks.Add(new VerificationCheck("Bundle structure", VerificationStatus.Failed, "Contents/Info.plist is missing."));
            return checks;
        }
        Dictionary<string, object> plist;
        try { plist = PlistReader.ParseDictionary(await File.ReadAllBytesAsync(plistPath, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            checks.Add(new VerificationCheck("Info.plist", VerificationStatus.Failed, $"Info.plist could not be read: {ex.Message}"));
            return checks;
        }
        var exeName = plist.GetValueOrDefault("CFBundleExecutable") as string;
        var exe = exeName is null ? null : Path.Combine(contents, "MacOS", exeName);
        if (exe is null || !File.Exists(exe))
        {
            checks.Add(new VerificationCheck("Bundle structure", VerificationStatus.Failed, $"CFBundleExecutable {exeName ?? "(missing)"} does not exist in Contents/MacOS."));
            return checks;
        }
        var framework = Path.Combine(contents, "Frameworks", "Electron Framework.framework");
        checks.Add(Directory.Exists(framework)
            ? new VerificationCheck("Bundle structure", VerificationStatus.Passed, $"Info.plist, Contents/MacOS/{exeName} and Electron Framework.framework are present.")
            : new VerificationCheck("Bundle structure", VerificationStatus.Failed, "Contents/Frameworks/Electron Framework.framework is missing."));
        var id = plist.GetValueOrDefault("CFBundleIdentifier") as string;
        var version = plist.GetValueOrDefault("CFBundleShortVersionString") as string;
        checks.Add(new VerificationCheck("Info.plist", id is { Length: > 0 } ? VerificationStatus.Passed : VerificationStatus.Failed,
            $"CFBundleIdentifier {id ?? "(missing)"}, version {version ?? "(missing)"}, minimum macOS {plist.GetValueOrDefault("LSMinimumSystemVersion") ?? "(not set)"}."));
        checks.Add(Checks.ExecutablePermission(exe, TargetPlatform.MacOS));
        checks.Add(BinaryInspector.CheckMatchesTarget(exe, TargetPlatform.MacOS, settings.Architecture));
        checks.AddRange(VerifyAppPayload(Path.Combine(contents, "Resources"), settings.Electron!));

        if (host.Platform != TargetPlatform.MacOS)
        {
            checks.Add(new VerificationCheck("Code signature", VerificationStatus.NotVerifiable, "codesign runs only on macOS."));
        }
        else if (toolchains.Find(ToolchainIds.Codesign)?.ExecutablePath is not { } codesign)
        {
            checks.Add(new VerificationCheck("Code signature", VerificationStatus.NotVerifiable, "codesign was not detected on this Mac."));
        }
        else
        {
            var output = new List<string>();
            var verify = await runner.RunAsync(new ProcessSpec { Executable = codesign, Arguments = ["--verify", "--deep", "--strict", "--verbose=2", app], Timeout = TimeSpan.FromMinutes(5) },
                (_, l) => { lock (output) output.Add(l); }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var details = new List<string>();
            await runner.RunAsync(new ProcessSpec { Executable = codesign, Arguments = ["-dv", "--verbose=2", app], Timeout = TimeSpan.FromMinutes(1) },
                (_, l) => { lock (details) details.Add(l); }, ct).ConfigureAwait(false);
            var authority = details.FirstOrDefault(l => l.StartsWith("Authority=", StringComparison.Ordinal));
            var adHoc = details.Any(l => l.Contains("Signature=adhoc", StringComparison.Ordinal));
            if (verify.Succeeded)
                checks.Add(new VerificationCheck("Code signature", adHoc ? VerificationStatus.PassedWithWarnings : VerificationStatus.Passed,
                    adHoc ? "codesign --verify --deep --strict passed. Ad-hoc signature (electron-builder identity \"-\"): it runs on this Mac, but it is not signed with a Developer ID or notarized, so other Macs show a Gatekeeper warning."
                          : $"codesign --verify --deep --strict passed. {authority ?? "Signed"}."));
            else
                checks.Add(new VerificationCheck("Code signature",
                    settings.Architecture == TargetArchitecture.X64 ? VerificationStatus.PassedWithWarnings : VerificationStatus.Failed,
                    $"codesign --verify failed (exit code {verify.ExitCode}): {string.Join(" ", output.TakeLast(3))}" +
                    (settings.Architecture == TargetArchitecture.X64 ? " Intel builds can run unsigned locally." : " Apple Silicon refuses to run unsigned code: set mac.identity (\"-\" for ad hoc) in the electron-builder configuration.")));
        }
        return checks;
    }
}

/// <summary>The zip electron-builder produced for the mac target (contains the .app), copied and checked.</summary>
public sealed class ElectronZipPackager(IProcessRunner runner) : ElectronPackagerBase(runner)
{
    public override PackagingFormat Format => PackagingFormat.PortableZip;

    public override Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<PackagedOutput>>(() =>
    {
        var zip = Newest(request.PublishDirectory, "*.zip") ?? throw new InvalidOperationException("electron-builder did not produce a .zip (the zip target was requested).");
        var dest = Path.Combine(request.OutputRoot, Path.GetFileName(zip));
        File.Copy(zip, dest);
        log.Info($"Copied {Path.GetFileName(zip)} from electron-builder's output");
        var checks = new List<VerificationCheck>();
        using (var archive = ZipFile.OpenRead(dest))
        {
            checks.Add(ZipIntegrity.Check(archive, cancellationToken));
            var appName = Path.GetFileName(request.ExecutableFileName);
            var hasPlist = archive.Entries.Any(e => e.FullName == $"{appName}/Contents/Info.plist");
            var hasExe = archive.Entries.Any(e => e.FullName.StartsWith($"{appName}/Contents/MacOS/", StringComparison.Ordinal) && e.Length > 0);
            checks.Add(hasPlist && hasExe
                ? new VerificationCheck("Archive contents", VerificationStatus.Passed, $"{appName} with Info.plist and its executable.")
                : new VerificationCheck("Archive contents", VerificationStatus.Failed, $"The zip does not contain {appName}/Contents/Info.plist and an executable in Contents/MacOS."));
        }
        return [new PackagedOutput(Format, ArtifactKind.File, dest, checks)];
    }, cancellationToken);

    public static string? Newest(string dir, string pattern) =>
        Directory.EnumerateFiles(dir, pattern).Where(f => !f.EndsWith(".blockmap", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
}

/// <summary>The dmg electron-builder produced, copied and checked with hdiutil verify.</summary>
public sealed class ElectronDmgPackager(IProcessRunner runner) : ElectronPackagerBase(runner)
{
    public override PackagingFormat Format => PackagingFormat.MacDmg;

    public override async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        var dmg = ElectronZipPackager.Newest(request.PublishDirectory, "*.dmg") ?? throw new InvalidOperationException("electron-builder did not produce a .dmg (the dmg target was requested).");
        var dest = Path.Combine(request.OutputRoot, Path.GetFileName(dmg));
        File.Copy(dmg, dest);
        log.Info($"Copied {Path.GetFileName(dmg)} from electron-builder's output");
        var checks = new List<VerificationCheck>();
        if (request.Toolchains.Find(ToolchainIds.Hdiutil)?.ExecutablePath is { } hdiutil)
        {
            var verify = await Runner.RunAsync(new ProcessSpec { Executable = hdiutil, Arguments = ["verify", dest], Timeout = TimeSpan.FromMinutes(5) }, null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            checks.Add(verify.Succeeded
                ? new VerificationCheck("hdiutil verify", VerificationStatus.Passed, "Checksum verified by hdiutil.")
                : new VerificationCheck("hdiutil verify", VerificationStatus.Failed, $"hdiutil verify failed with exit code {verify.ExitCode}."));
        }
        else checks.Add(new VerificationCheck("hdiutil verify", VerificationStatus.NotVerifiable, "hdiutil was not detected."));
        return [new PackagedOutput(Format, ArtifactKind.File, dest, checks)];
    }
}

/// <summary>Linux: electron-builder's unpacked app folder (linux-unpacked), copied and checked.</summary>
public sealed class ElectronFolderPackager(IProcessRunner runner) : ElectronPackagerBase(runner)
{
    public override PackagingFormat Format => PackagingFormat.PortableFolder;

    public override async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken)
    {
        var exe = StagingPath(request, request.ExecutableFileName);
        var source = Path.GetDirectoryName(exe)!;
        if (!File.Exists(exe)) throw new InvalidOperationException($"electron-builder did not produce the expected executable ({request.ExecutableFileName}).");
        var dest = Path.Combine(request.OutputRoot, request.ArtifactBaseName);
        log.Info($"Copying {Path.GetFileName(source)} from electron-builder's output");
        await CopyTreeAsync(Runner, source, dest, request.Host, cancellationToken).ConfigureAwait(false);
        var destExe = Path.Combine(dest, Path.GetFileName(exe));
        var checks = new List<VerificationCheck>
        {
            Checks.ExecutablePermission(destExe, request.Settings.Platform),
            BinaryInspector.CheckMatchesTarget(destExe, request.Settings.Platform, request.Settings.Architecture),
        };
        checks.AddRange(VerifyAppPayload(Path.Combine(dest, "resources"), request.Settings.Electron!));
        return [new PackagedOutput(Format, ArtifactKind.Directory, dest, checks)];
    }
}
