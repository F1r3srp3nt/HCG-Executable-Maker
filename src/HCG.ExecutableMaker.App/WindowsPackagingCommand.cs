using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.BuildEngine.Windows;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// Windows packaging without the UI, for any application (nothing about Executable Maker goes into the product):
/// <code>
/// HCGExecutableMaker --package-windows
///     (--app-dir &lt;already-built folder&gt; --arch x64|arm64  |  --source &lt;project folder&gt; [--project &lt;rel .csproj&gt;] [--targets win-x64,win-arm64])
///     [--identity app.json] --name "Product" --publisher "Company" --app-id com.company.product --version 1.2.0[-beta.1]
///     [--exe Product.exe] [--assembly-name Product] [--icon icon.png] [--description "…"] [--install-folder "Product"]
///     [--formats installer,zip,folder] [--out DIR] [--dist DIR] [--dist-name "{name}-{version}-{rid}"]
///     [--build-number N] [--product-key key] [--edition id] [--built-with ".NET SDK 10.0.100"]
///     [--store-product id [--store-edition id] [--channel beta|stable] [--distribution free|paid|…] [--note "…"]…]   → store-submission.json
///     [--authenticode-thumbprint SHA1 --timestamp-url URL --approve-signing]   (Windows; signs the program and the Setup.exe)
/// HCGExecutableMaker --windows-signing-doctor &lt;file.exe|portable.zip|folder&gt;
/// HCGExecutableMaker --authenticode-sign &lt;file.exe&gt; --thumbprint SHA1 --timestamp-url URL --approve &lt;SHA-256 of the file&gt;
/// </code>
/// Exit codes: 0 succeeded, 2 usage, 4 not possible on this computer, 6 packaging or verification failed. Nothing is
/// uploaded or published; Ship to Store takes the manifest separately (--ship-to-store --manifest), as a private draft.
/// </summary>
internal static class WindowsPackagingCommand
{
    private sealed class ConsoleObserver : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) => Console.WriteLine($"   [{stage}] {description}");
        public void OnLog(BuildLogLine line)
        {
            if (line.Kind is BuildLogKind.Error or BuildLogKind.Warning or BuildLogKind.Success) Console.WriteLine($"   {line.Kind.ToString().ToUpperInvariant()}: {line.Text}");
            else if (line.Text.StartsWith("Release Passport", StringComparison.Ordinal)) Console.WriteLine("   " + line.Text);
        }
    }

    public static int SigningDoctor(string[] args)
    {
        var target = args.SkipWhile(a => a != "--windows-signing-doctor").Skip(1).FirstOrDefault();
        if (target is null || (!File.Exists(target) && !Directory.Exists(target))) { Console.Error.WriteLine("Usage: --windows-signing-doctor <file.exe|portable.zip|folder>"); return 2; }
        var report = WindowsSigningDoctor.InspectAsync(Path.GetFullPath(target), new Infrastructure.Processes.ProcessRunner()).GetAwaiter().GetResult();
        Console.Write(WindowsSigningDoctor.ToText(report));
        return report.Status == "invalid" ? 6 : 0;
    }

    public static int Sign(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var file = Arg("--authenticode-sign");
        if (file is null || !File.Exists(file) || Arg("--thumbprint") is not { } thumb || Arg("--timestamp-url") is not { } tsa)
        {
            Console.Error.WriteLine("Usage: --authenticode-sign <file.exe> --thumbprint <certificate SHA-1> --timestamp-url <RFC 3161 URL> --approve <SHA-256 of the file>");
            return 2;
        }
        file = Path.GetFullPath(file);
        Console.WriteLine($"File {file}: SHA-256 {AuthenticodeSigner.Sha256(file)}");
        Console.WriteLine("Before: " + Authenticode.Inspect(file).Summary);
        if (Arg("--approve") is not { } approval)
        {
            Console.WriteLine("Not signed: signing changes the file and uses your certificate. Pass --approve <SHA-256 shown above> to approve signing this exact file.");
            return 2;
        }
        var signtool = Arg("--signtool") ?? DetectSignTool();
        if (signtool is null) { Console.Error.WriteLine("signtool.exe (Windows SDK) was not found; pass --signtool <path>."); return 4; }
        var result = AuthenticodeSigner.SignAsync(new AuthenticodeSigner.Request(file, signtool, thumb, tsa, approval), new Infrastructure.Processes.ProcessRunner())
            .GetAwaiter().GetResult();
        foreach (var l in result.Log.TakeLast(8)) Console.WriteLine("  signtool: " + l);
        Console.WriteLine((result.Succeeded ? "Signed: " : "NOT signed: ") + result.Message);
        return result.Succeeded ? 0 : OperatingSystem.IsWindows() ? 6 : 4;
    }

    private static string? DetectSignTool()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
        if (!Directory.Exists(kits)) return null;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        return Directory.EnumerateFiles(kits, "signtool.exe", SearchOption.AllDirectories)
            .Where(p => p.Contains(Path.DirectorySeparatorChar + arch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>An app identity file: the product's own name, publisher, ID, version and Store details (no Maker branding).</summary>
    internal sealed record IdentityFile
    {
        public string? Name { get; init; }
        public string? Publisher { get; init; }
        public string? ApplicationId { get; init; }
        public string? Version { get; init; }
        public string? Description { get; init; }
        public string? Icon { get; init; }
        public string? Executable { get; init; }
        public string? AssemblyName { get; init; }
        public string? BuildNumber { get; init; }
        public string? ProductKey { get; init; }
        public string? Edition { get; init; }
        public string? InstallFolder { get; init; }
        public string? DistName { get; init; }
        public StoreDetails? Store { get; init; }
        public sealed record StoreDetails(string? ProductId, string? EditionId, string? Channel, string? Distribution, IReadOnlyList<string>? ReleaseNotes);
    }

    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        IEnumerable<string> All(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]);
        Console.WriteLine(HostInfo.BuildIdentity);
        IdentityFile id = new();
        string? identityDir = null;
        if (Arg("--identity") is { } idPath)
        {
            identityDir = Path.GetDirectoryName(Path.GetFullPath(idPath));
            id = JsonSerializer.Deserialize<IdentityFile>(File.ReadAllText(idPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        }
        var name = Arg("--name") ?? id.Name;
        var version = Arg("--version") ?? id.Version;
        var appId = Arg("--app-id") ?? id.ApplicationId;
        if (name is null || version is null || appId is null)
        {
            Console.Error.WriteLine("The product's --name, --version and --app-id are required (or an --identity file). Executable Maker puts no name of its own into your installer.");
            return 2;
        }
        var icon = Arg("--icon") ?? (id.Icon is { } ri && identityDir is not null ? Path.Combine(identityDir, ri) : id.Icon);
        if (icon is not null && !File.Exists(icon)) { Console.Error.WriteLine($"The icon {icon} does not exist."); return 2; }
        var identity = new AppIdentity
        {
            ApplicationName = name, Publisher = Arg("--publisher") ?? id.Publisher ?? "", Version = version, ApplicationId = appId,
            Description = Arg("--description") ?? id.Description ?? "", IconSourcePath = icon is null ? null : Path.GetFullPath(icon),
        };
        var formats = new List<PackagingFormat>();
        foreach (var f in (Arg("--formats") ?? "installer,zip").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            formats.Add(f switch
            {
                "installer" or "setup" or "exe" => PackagingFormat.WindowsInstallerExe, "zip" => PackagingFormat.PortableZip, "folder" => PackagingFormat.PortableFolder,
                _ => throw new ArgumentException($"Unknown format {f}: use installer, zip, folder."),
            });
        var installer = new WindowsInstallerOptions { InstallFolderName = Arg("--install-folder") ?? id.InstallFolder ?? name };
        var output = Path.GetFullPath(Arg("--out") ?? Path.Combine(Path.GetTempPath(), "hcg-windows-packages"));
        var dist = Arg("--dist") is { } d ? Path.GetFullPath(d) : null;
        WindowsPackagingService.Signing? signing = null;
        if (Arg("--authenticode-thumbprint") is { } thumb)
        {
            if (!args.Contains("--approve-signing"))
            {
                Console.Error.WriteLine("Not packaged: signing uses your certificate and changes the files. Add --approve-signing to approve signing this run's program and Setup.exe.");
                return 2;
            }
            var st = Arg("--signtool") ?? DetectSignTool();
            if (st is null) { Console.Error.WriteLine("signtool.exe (Windows SDK) was not found; pass --signtool <path>."); return 4; }
            signing = new(st, thumb, Arg("--timestamp-url") ?? throw new ArgumentException("--timestamp-url is required with --authenticode-thumbprint (an RFC 3161 timestamp authority)."));
        }

        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        var report = services.GetRequiredService<IToolchainDetector>().DetectAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"Host: {report.Host.DisplayName} · Inno Setup {report.Find(ToolchainIds.InnoSetup)?.Version ?? "not found"}");
        if (formats.Contains(PackagingFormat.WindowsInstallerExe)
            && PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.Windows, report.Host, report, IsGuiApplication: true)).First(o => o.Format == PackagingFormat.WindowsInstallerExe) is { IsAvailable: false } setup)
        {
            Console.Error.WriteLine("The Windows installer is not available here: " + setup.Reason);
            return 4;
        }
        var source = SourceProvenance.Unknown;
        var produced = new List<(string Rid, string File, string? Passport)>();
        var failures = 0;
        var runner = services.GetRequiredService<IProcessRunner>();

        if (Arg("--app-dir") is { } appDir)
        {
            var arch = Arg("--arch") switch { "x64" => TargetArchitecture.X64, "arm64" => TargetArchitecture.Arm64, _ => (TargetArchitecture?)null };
            if (arch is null) { Console.Error.WriteLine("--arch x64 or arm64 is required with --app-dir (the architecture the folder was built for)."); return 2; }
            var exe = Arg("--exe") ?? id.Executable ?? (Arg("--assembly-name") ?? id.AssemblyName) + ".exe";
            if (Arg("--source-repo") is { } repo) source = SourceProvenance.ReadAsync(runner, report.Find(ToolchainIds.Git)?.ExecutablePath, repo, CancellationToken.None).GetAwaiter().GetResult();
            var service = new WindowsPackagingService(services.GetRequiredService<PackagingRegistry>(), runner, TimeProvider.System);
            var result = service.PackageAsync(new WindowsPackagingService.Request
            {
                AppDirectory = Path.GetFullPath(appDir), ExecutableFileName = exe, Identity = identity, Architecture = arch.Value, Formats = formats,
                OutputDirectory = output, Toolchains = report, Installer = installer, AllowOverwriteOutput = true, BuildNumber = Arg("--build-number") ?? id.BuildNumber,
                Source = source, Provider = Arg("--provider") ?? "executable-maker-local", BuiltWith = Arg("--built-with"),
                ProductKey = Arg("--product-key") ?? id.ProductKey, Edition = Arg("--edition") ?? id.Edition, ApprovedSigning = signing,
            }, m => Console.WriteLine("   " + m)).GetAwaiter().GetResult();
            var rid = arch == TargetArchitecture.Arm64 ? "win-arm64" : "win-x64";
            Console.WriteLine($"{rid}: {(result.Succeeded ? "SUCCEEDED" : "FAILED — " + result.Failure)}");
            if (!result.Succeeded) return result.Failure?.Contains("on Windows", StringComparison.Ordinal) == true ? 4 : 6;
            foreach (var a in result.Artifacts.Where(a => a.Kind == ArtifactKind.File))
                produced.Add((rid, a.Path, File.Exists(a.Path + ".release-passport.json") ? a.Path + ".release-passport.json" : null));
        }
        else if (Arg("--source") is { } src)
        {
            if (signing is not null) { Console.Error.WriteLine("Signing applies to --app-dir packaging (sign the built program before it is packaged). Build the folder, then package it with --app-dir."); return 2; }
            var projects = services.GetRequiredService<ProjectService>();
            var imported = projects.ImportDirectoryAsync(Path.GetFullPath(src), null, CancellationToken.None).GetAwaiter().GetResult();
            var analysis = imported.Project.Analysis!;
            var project = Arg("--project") ?? analysis.PrimaryProjectPath;
            Console.WriteLine($"Imported {imported.FileCount} files. Project: {project} ({analysis.ApplicationType}, {analysis.Support})");
            if (analysis.Support != SupportLevel.Supported || project is null) { Console.Error.WriteLine("Not a buildable Windows desktop project here: " + analysis.SupportSummary); return 4; }
            foreach (var rid in (Arg("--targets") ?? "win-x64").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var arch = rid switch { "win-x64" => TargetArchitecture.X64, "win-arm64" => TargetArchitecture.Arm64, _ => throw new ArgumentException($"Unsupported target {rid}: win-x64 or win-arm64.") };
                var build = services.GetRequiredService<BuildService>().RunAsync(new BuildRequest
                {
                    Project = imported.Project, TrustAcknowledged = true, AllowOverwriteOutput = true, AssemblyName = Arg("--assembly-name") ?? id.AssemblyName,
                    Settings = new BuildSettings
                    {
                        ProjectRelativePath = project, Platform = TargetPlatform.Windows, Architecture = arch, OutputDirectory = output, Configuration = BuildConfigurationKind.Release,
                        Identity = identity, PackagingFormats = formats, WindowsInstaller = installer,
                    },
                }, new ConsoleObserver(), CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"{rid}: {build.Status} {build.FailureReason}");
                if (build.Status != BuildStatus.Succeeded) { failures++; continue; }
                foreach (var a in build.Artifacts.Where(a => a.Kind == ArtifactKind.File))
                    produced.Add((rid, a.Path, File.Exists(a.Path + ".release-passport.json") ? a.Path + ".release-passport.json" : null));
            }
        }
        else { Console.Error.WriteLine("Choose what to package: --app-dir <already-built folder> --arch x64|arm64, or --source <project folder>."); return 2; }

        foreach (var (rid, file, passport) in produced) Console.WriteLine($"  {file} · SHA-256 {Checksums.Sha256FileAsync(file).GetAwaiter().GetResult()}{(passport is null ? "" : " · passport " + Path.GetFileName(passport))}");
        var shipFiles = produced.Select(p => p.File).ToList();
        if (dist is not null)
        {
            Directory.CreateDirectory(dist);
            shipFiles = [];
            var pattern = Arg("--dist-name") ?? id.DistName;
            foreach (var (rid, file, passport) in produced)
            {
                var suffix = file.EndsWith("-Setup.exe", StringComparison.Ordinal) ? "-Setup.exe" : Path.GetExtension(file);
                var to = Path.Combine(dist, pattern is null ? Path.GetFileName(file)
                    : pattern.Replace("{name}", PathGuard.SanitizeFileName(name, "App")).Replace("{version}", version).Replace("{rid}", rid).Replace("{arch}", rid[4..]) + suffix);
                File.Copy(file, to, overwrite: true);
                shipFiles.Add(to);
                if (passport is not null)
                {
                    // The same bytes under the distribution name: the passport names the file it describes.
                    var p = JsonNode.Parse(File.ReadAllText(passport))!.AsObject();
                    p["artifact"]!["filename"] = Path.GetFileName(to);
                    File.WriteAllText(to + ".release-passport.json", p.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
                }
            }
            File.WriteAllText(Path.Combine(dist, "SHA256SUMS.txt"), string.Concat(shipFiles.Order(StringComparer.Ordinal)
                .Select(f => $"{Checksums.Sha256FileAsync(f).GetAwaiter().GetResult()}  {Path.GetFileName(f)}\n")));
            Console.WriteLine($"Distribution folder: {dist}");
        }
        if ((Arg("--store-product") ?? id.Store?.ProductId) is { } storeProduct && shipFiles.Count > 0)
        {
            var notes = All("--note").ToList();
            var release = new StoreReleaseRequest
            {
                ProductId = storeProduct, EditionId = Arg("--store-edition") ?? id.Store?.EditionId, Version = version, Build = Arg("--build-number") ?? id.BuildNumber ?? "1",
                Channel = Arg("--channel") ?? id.Store?.Channel ?? (version.Contains('-') ? "beta" : "stable"), ReleaseNotes = notes.Count > 0 ? notes : id.Store?.ReleaseNotes ?? [],
                Distribution = Arg("--distribution") ?? id.Store?.Distribution ?? "paid", Eligibility = "private",
            };
            var manifest = StoreSubmission.CreateAsync(release, shipFiles, runner).GetAwaiter().GetResult();
            var at = Path.Combine(Path.GetDirectoryName(shipFiles[0])!, StoreSubmission.FileName);
            File.WriteAllText(at, StoreSubmission.Serialize(manifest));
            Console.WriteLine($"Store submission manifest: {at} (a PRIVATE draft when shipped with --ship-to-store --manifest; nothing was sent)");
        }
        return failures == 0 ? 0 : 6;
    }
}
