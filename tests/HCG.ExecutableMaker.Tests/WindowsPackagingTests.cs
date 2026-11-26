using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.BuildEngine.Windows;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Windows;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// The reusable Windows release path HCG Mail for Windows needed. It covers:
/// <list type="bullet">
/// <item>packaging an already-built folder with the product's own identity;</item>
/// <item>Release Passports that tell the installer's x86 bootstrapper apart from the x64/ARM64 program it installs;</item>
/// <item>Store detection of architectures and Authenticode;</item>
/// <item>the Store submission manifest;</item>
/// <item>Ship to Store against the deployed HCG Store (835ac1a), whose strict schema has no build provenance yet.</item>
/// </list>
/// Synthetic programs only.
/// </summary>
public sealed class WindowsPackagingTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static ToolchainReport Report(TargetPlatform host = TargetPlatform.Linux) => new(
        new HostEnvironment(host, TargetArchitecture.X64, "test", ".NET", "m"),
        [new ToolchainInfo { Id = ToolchainIds.InnoSetup, DisplayName = "Inno Setup", Category = "Windows", Status = ToolchainStatus.Installed, ExecutablePath = @"C:\Inno\ISCC.exe", Version = "6.4.3" }],
        null, null, DateTimeOffset.UtcNow);

    private string AppDir(ushort machine = 0x8664, string name = "Product")
    {
        var dir = _t.Combine("publish-" + machine.ToString("x"));
        Directory.CreateDirectory(Path.Combine(dir, "runtimes"));
        TestPe.Write(Path.Combine(dir, name + ".exe"), machine, pe32Plus: machine != 0x014C, version: ("1.2.0.0", "1.2.0-beta.1", "Example AB", "Example Product"));
        File.WriteAllText(Path.Combine(dir, name + ".dll"), "managed code");
        File.WriteAllText(Path.Combine(dir, "runtimes", "native.txt"), "x");
        return dir;
    }

    private static AppIdentity Identity => new() { ApplicationName = "Example Product", Publisher = "Example AB", Version = "1.2.0-beta.1", ApplicationId = "com.example.product" };

    private static WindowsPackagingService Service() =>
        new(new PackagingRegistry([new PortableFolderPackager(), new PortableZipPackager(), new InnoSetupPackager(new NoRunner())]), new NoRunner(), TimeProvider.System);

    [Fact]
    public void An_already_built_folder_is_checked_before_anything_is_written()
    {
        var dir = AppDir();
        WindowsPackagingService.Request Q(Action<WindowsPackagingService.Request>? _ = null) => new()
        {
            AppDirectory = dir, ExecutableFileName = "Product.exe", Identity = Identity, Architecture = TargetArchitecture.X64,
            Formats = [PackagingFormat.PortableZip], OutputDirectory = _t.Combine("out"), Toolchains = Report(),
        };
        Assert.Null(WindowsPackagingService.Problem(Q()));
        Assert.Contains("is x64, not arm64", WindowsPackagingService.Problem(Q() with { Architecture = TargetArchitecture.Arm64 }));
        Assert.Contains("is not in", WindowsPackagingService.Problem(Q() with { ExecutableFileName = "Missing.exe" }));
        Assert.Contains("not a Windows program", WindowsPackagingService.Problem(Q() with { ExecutableFileName = "Product.dll" }));
        Assert.Contains("relative to the application folder", WindowsPackagingService.Problem(Q() with { ExecutableFileName = "../x.exe" }));
        Assert.Contains("name, version and ID are required", WindowsPackagingService.Problem(Q() with { Identity = Identity with { ApplicationId = "" } }));
        var x86 = AppDir(0x014C);
        Assert.Contains("is x86; Executable Maker packages x64 and ARM64", WindowsPackagingService.Problem(Q() with { AppDirectory = x86 }));
        Assert.False(Directory.Exists(_t.Combine("out")));
    }

    [Fact]
    public async Task An_already_built_folder_becomes_a_portable_zip_with_its_own_identity_checksums_and_a_valid_passport()
    {
        var dir = AppDir();
        var result = await Service().PackageAsync(new WindowsPackagingService.Request
        {
            AppDirectory = dir, ExecutableFileName = "Product.exe", Identity = Identity, Architecture = TargetArchitecture.X64, Formats = [PackagingFormat.PortableZip],
            OutputDirectory = _t.Combine("out"), Toolchains = Report(), BuildNumber = "7", ProductKey = "example-product", Edition = "windows-desktop",
            Source = new SourceProvenance("Example/Product", new string('a', 40), "main", false, "git"), Provider = "github-actions", BuiltWith = ".NET SDK 10.0.100",
        }, _ => { });
        Assert.True(result.Succeeded, result.Failure);
        var zip = Assert.Single(result.Artifacts);
        Assert.Equal("Example Product-1.2.0-beta.1-win-x64.zip", zip.FileName);                  // the product's name, not Executable Maker's
        Assert.Equal(VerificationStatus.Passed, zip.Verification);
        Assert.Contains($"{zip.Sha256}  {zip.FileName}", File.ReadAllText(Path.Combine(result.OutputRoot!, "SHA256SUMS.txt")));
        using (var z = ZipFile.OpenRead(zip.Path))
            Assert.Equal(["Product.dll", "Product.exe", "runtimes/native.txt"], z.Entries.Select(e => e.FullName[(e.FullName.IndexOf('/') + 1)..]).Where(n => n.Length > 0 && !n.EndsWith('/')).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(dir, "Product.exe")));                                // the folder is read, not changed

        var passport = JsonNode.Parse(File.ReadAllText(Assert.Single(result.Passports)))!;
        Assert.Empty(ReleasePassport.ValidateBuildHalf(passport));
        Assert.Equal(("windows", "x64", "com.example.product", "1.2.0-beta.1", "7"),
            ((string)passport["platform"]!, (string)passport["architecture"]!, (string)passport["package"]!, (string)passport["version"]!, (string)passport["build_number"]!));
        Assert.Equal(("example-product", "windows-desktop"), ((string)passport["product"]!["key"]!, (string)passport["edition"]!));
        Assert.Equal(("x64", "1.2.0-beta.1", "Example AB"), ((string)passport["windows"]!["installed_program"]!["machine"]!, (string)passport["windows"]!["installed_program"]!["product_version"]!, (string)passport["windows"]!["installed_program"]!["company"]!));
        Assert.Null(passport["windows"]!["bootstrapper"]);
        Assert.Equal(("unsigned", false, 1), ((string)passport["code_signing"]!["status"]!, (bool)passport["code_signing"]!["all_signed"]!, (int)passport["code_signing"]!["binaries"]!));
        Assert.StartsWith(".NET SDK 10.0.100; HCG Executable Maker", (string)passport["build"]!["toolchain"]!);
        Assert.Equal(["scope", "status"], passport["repository_signing"]!.AsObject().Select(kv => kv.Key));

        // HCG Store reads it for the exact file: provider, commit, and the program's architecture from the ZIP itself.
        var c = await StoreArtifacts.DetectAsync(zip.Path);
        Assert.Equal(["x64"], c.Architectures);
        Assert.Equal(("github-actions", new string('a', 40)), (c.Provenance!.BuildProvider, c.Provenance.SourceCommit));
    }

    private string InnoSetup(string name, ushort loaderMachine = 0x014C)
    {
        var path = TestPe.Write(_t.Combine(name), loaderMachine, pe32Plus: false, version: ("6.4.3.0", "1.2.0-beta.1", "Example AB", "Example Product Setup"));
        File.AppendAllText(path, new string('\0', 70_000) + "Inno Setup Setup Data (6.4.3)");
        return path;
    }

    [Fact]
    public async Task An_installers_x86_bootstrapper_is_not_reported_as_the_architecture_it_installs()
    {
        var setup = InnoSetup("Example-Product-1.2.0-beta.1-win-arm64-Setup.exe");
        Assert.Equal("x86", PeFile.Read(setup)!.MachineName);

        // Without a passport, the Store is told no architecture rather than the loader's x86.
        var bare = await StoreArtifacts.DetectAsync(setup);
        Assert.Empty(bare.Architectures);
        Assert.Contains(bare.Notes, n => n.Contains("bootstrapper is a x86 program on every architecture", StringComparison.Ordinal));

        // The installer's passport states the installed program's architecture (ARM64), and the bootstrapper separately.
        var appDir = AppDir(0xAA64);
        var sha = await Checksums.Sha256FileAsync(setup);
        var artifact = new ArtifactRecord
        {
            Id = Guid.NewGuid(), BuildId = Guid.NewGuid(), Format = PackagingFormat.WindowsInstallerExe, Kind = ArtifactKind.File, Path = setup, SizeBytes = new FileInfo(setup).Length,
            Sha256 = sha, Verification = VerificationStatus.Passed, Checks = [new VerificationCheck("Installed application", VerificationStatus.Passed, "Product.exe: arm64")], CreatedAt = DateTimeOffset.UtcNow,
        };
        var written = WindowsPackagingService.WritePassports(new WindowsPackagingService.Request
        {
            AppDirectory = appDir, ExecutableFileName = "Product.exe", Identity = Identity, Architecture = TargetArchitecture.Arm64, Formats = [PackagingFormat.WindowsInstallerExe],
            OutputDirectory = _t.Combine("out"), Toolchains = Report(TargetPlatform.Windows),
        }, appDir, [artifact], DateTimeOffset.UtcNow, _ => { });
        var p = JsonNode.Parse(File.ReadAllText(Assert.Single(written)))!;
        Assert.Empty(ReleasePassport.ValidateBuildHalf(p));
        Assert.Equal(("arm64", "arm64", "x86"), ((string)p["architecture"]!, (string)p["windows"]!["installed_program"]!["machine"]!, (string)p["windows"]!["bootstrapper"]!["machine"]!));
        Assert.Equal("installer", (string)p["artifact"]!["kind"]!);
        Assert.Equal(2, (int)p["code_signing"]!["binaries"]!);                                   // the Setup.exe and the installed program
        Assert.Contains("Inno Setup 6.4.3", (string)p["build"]!["toolchain"]!);

        var withPassport = await StoreArtifacts.DetectAsync(setup);
        Assert.Equal(["arm64"], withPassport.Architectures);
        // A plain program reports its own machine type.
        Assert.Equal(["x64"], (await StoreArtifacts.DetectAsync(TestPe.Write(_t.Combine("tool.exe"), 0x8664, true))).Architectures);
    }

    [Fact]
    public async Task Authenticode_is_read_for_the_Store_on_any_computer_never_upgraded()
    {
        var unsigned = await StoreArtifacts.DetectAsync(TestPe.Write(_t.Combine("plain.exe"), 0x8664, true));
        var s1 = await StoreArtifacts.CheckSigningAsync(unsigned, new NoRunner());
        Assert.Equal("unsigned", s1.Status);
        Assert.StartsWith("HCG Executable Maker Authenticode verifier", s1.Tool);

        using var cert = TestPe.CodeSigningCertificate("CN=Example AB");
        var signedFile = TestPe.Write(_t.Combine("signed.exe"), 0x8664, true);
        TestPe.Sign(signedFile, cert, null, null);
        var s2 = await StoreArtifacts.CheckSigningAsync(await StoreArtifacts.DetectAsync(signedFile), new NoRunner());
        Assert.Equal("invalid", s2.Status);                          // intact, but its certificate is not trusted: not "signed"
        Assert.Contains("NOT trusted on this computer", s2.Evidence);
        Assert.DoesNotContain(_t.Path, s2.Evidence);                 // evidence names the file, never this computer's paths

        var zipPath = _t.Combine("portable.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(TestPe.Write(_t.Combine("App.exe"), 0xAA64, true), "App/App.exe");
            z.CreateEntryFromFile(TestPe.Write(_t.Combine("createdump.exe"), 0xAA64, true), "App/createdump.exe");
        }
        var zc = await StoreArtifacts.DetectAsync(zipPath);
        Assert.Equal(["arm64"], zc.Architectures);
        Assert.Equal("unsigned", (await StoreArtifacts.CheckSigningAsync(zc, new NoRunner())).Status);
    }

    [Fact]
    public async Task The_submission_manifest_is_the_deployed_Stores_exact_bodies_for_a_private_draft()
    {
        var result = await Service().PackageAsync(new WindowsPackagingService.Request
        {
            AppDirectory = AppDir(), ExecutableFileName = "Product.exe", Identity = Identity, Architecture = TargetArchitecture.X64, Formats = [PackagingFormat.PortableZip],
            OutputDirectory = _t.Combine("out"), Toolchains = Report(), Source = new SourceProvenance("Example/Product", new string('b', 40), "main", false, "git"),
        }, _ => { });
        var zip = result.Artifacts.Single().Path;
        var release = new StoreReleaseRequest { ProductId = "example-product", Version = "1.2.0-beta.1", Build = "7", Distribution = "free", Eligibility = "private", ReleaseNotes = ["Testing release."] };
        var manifest = await StoreSubmission.CreateAsync(release, [zip], new NoRunner());
        var declare = manifest["artifacts"]![0]!["declare"]!.AsObject();
        // Exactly the deployed HCG Store's ArtifactInput fields (its schema is strict): no provenance keys here.
        Assert.Equal(["architectures", "fileName", "format", "kind", "platform", "sha256", "signing", "sizeBytes"], declare.Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.Equal(("windows", "zip", "portable-app", "unsigned"), ((string)declare["platform"]!, (string)declare["format"]!, (string)declare["kind"]!, (string)declare["signing"]!["status"]!));
        Assert.Equal((new string('b', 40), "local"), ((string)manifest["artifacts"]![0]!["provenance"]!["sourceCommit"]!, (string)manifest["artifacts"]![0]!["provenance"]!["buildProvider"]!));
        Assert.Equal(("private", "free", false), ((string)manifest["release"]!["eligibility"]!, (string)manifest["release"]!["distribution"]!, (bool)manifest["publishes"]!));
        Assert.Equal(["x64"], manifest["release"]!["compatibility"]!["architectures"]!.AsArray().Select(a => (string)a!));

        var path = Path.Combine(result.OutputRoot!, StoreSubmission.FileName);
        File.WriteAllText(path, StoreSubmission.Serialize(manifest));
        var (r, files) = StoreSubmission.Load(path);
        Assert.Equal(("example-product", "private", "7"), (r.ProductId, r.Eligibility, r.Build));
        Assert.Equal(zip, files.Single().Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreSubmission.CreateAsync(release with { Eligibility = "public" }, [zip], new NoRunner()));
        File.WriteAllText(path, StoreSubmission.Serialize(manifest).Replace("\"private\"", "\"public\""));
        Assert.Contains("private drafts only", Assert.Throws<InvalidOperationException>(() => StoreSubmission.Load(path)).Message);
    }

    [Fact]
    public void Only_the_deployed_Stores_unknown_provenance_answer_is_retried_without_provenance()
    {
        static HcgStoreException E(string message, int status = 400, string code = "invalid_request") => new(code, message, status);
        // Exactly what HCG Store 835ac1a (zod 4.6.7 strictObject) answers: reproduced with the Store's own zod.
        Assert.True(ShipToStoreService.RejectsOnlyProvenance(E("request: Unrecognized keys: \"sourceCommit\", \"buildProvider\", \"toolchain\", \"validationReport\"")));
        Assert.True(ShipToStoreService.RejectsOnlyProvenance(E("request: Unrecognized key: \"toolchain\"")));
        Assert.False(ShipToStoreService.RejectsOnlyProvenance(E("request: Unrecognized keys: \"sourceCommit\", \"replaces\"")));
        Assert.False(ShipToStoreService.RejectsOnlyProvenance(E("sha256: SHA-256 must be 64 lowercase hexadecimal characters.")));
        Assert.False(ShipToStoreService.RejectsOnlyProvenance(E("request: Unrecognized keys: \"sourceCommit\"", 403, "not_publisher")));
    }

    [Fact]
    public async Task Ship_to_Store_on_the_deployed_server_declares_without_provenance_and_says_so()
    {
        var result = await Service().PackageAsync(new WindowsPackagingService.Request
        {
            AppDirectory = AppDir(), ExecutableFileName = "Product.exe", Identity = Identity, Architecture = TargetArchitecture.X64, Formats = [PackagingFormat.PortableZip],
            OutputDirectory = _t.Combine("out"), Toolchains = Report(), Source = new SourceProvenance("Example/Product", new string('c', 40), "main", false, "git"),
        }, _ => { });
        var c = await StoreArtifacts.DetectAsync(result.Artifacts.Single().Path);
        c = c with { Signing = await StoreArtifacts.CheckSigningAsync(c, new NoRunner()) };
        Assert.NotNull(c.Provenance?.SourceCommit);
        var store = new DeployedStore();
        var client = new HcgStoreClient(new HttpClient(store), new Uri("https://store.test/"), _ => Task.FromResult<string?>("t")) { RetryDelays = [TimeSpan.Zero] };
        var notes = new List<string>();
        var shipped = await new ShipToStoreService(client).ShipAsync(
            new StoreReleaseRequest { ProductId = "example-product", Version = "1.2.0-beta.1", Build = "7", Distribution = "free", Eligibility = "private" }, [c], null,
            new SyncProgress<ShipItemProgress>(p => { if (p.Detail is { } d) notes.Add(d); }), CancellationToken.None);
        Assert.Equal(2, store.Declarations.Count);
        Assert.Contains("sourceCommit", store.Declarations[0]);
        Assert.DoesNotContain("sourceCommit", store.Declarations[1]);
        Assert.Contains(notes, n => n.Contains("does not accept build provenance yet", StringComparison.Ordinal));
        Assert.True(shipped.AllVerified);
        Assert.Equal("draft", shipped.Release.Status);
    }

    /// <summary>Answers like HCG Store 835ac1a: strict ArtifactInput without provenance fields; a private draft.</summary>
    private sealed class DeployedStore : HttpMessageHandler
    {
        public readonly List<string> Declarations = [];
        private long _received;
        private string _status = "awaiting-upload";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            object Artifact() => new { id = "art-0000000000000001", fileName = "x.zip", platform = "windows", format = "zip", status = _status, sha256 = _status == "verified" ? "s" : null, receivedBytes = _received };
            object Release() => new { id = "rel-0000000000000001", productId = "example-product", version = "1.2.0-beta.1", build = "7", status = "draft", artifacts = _status == "awaiting-upload" ? Array.Empty<object>() : [Artifact()] };
            if (path == "/api/v1/publishing/releases" && r.Method == HttpMethod.Post) return Json(201, Release());
            if (path.EndsWith("/artifacts") && r.Method == HttpMethod.Post)
            {
                var body = await r.Content!.ReadAsStringAsync(ct);
                Declarations.Add(body);
                var extra = JsonNode.Parse(body)!.AsObject().Select(kv => kv.Key).Where(k => k is "sourceCommit" or "buildProvider" or "toolchain" or "validationReport").ToList();
                if (extra.Count > 0)
                    return Json(400, new { error = new { code = "invalid_request", message = "request: Unrecognized keys: " + string.Join(", ", extra.Select(k => $"\"{k}\"")) } });
                _status = "uploading";
                return Json(201, new { artifact = Artifact(), uploadUrl = "https://store.test/up/1", expiresAt = 0, chunkBytes = 1 << 20 });
            }
            if (path.StartsWith("/up/") && r.Method == HttpMethod.Put)
            {
                _received += (await r.Content!.ReadAsByteArrayAsync(ct)).Length;
                return Json(200, new { received = _received, size = r.Content.Headers.ContentRange!.Length });
            }
            if (path.EndsWith("/complete")) { _status = "verified"; return Json(200, new { artifact = Artifact() }); }
            if (path.StartsWith("/api/v1/publishing/releases/")) return Json(200, Release());
            return Json(404, new { error = new { code = "not_found", message = path } });
        }
        private static HttpResponseMessage Json(int status, object body) =>
            new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    private sealed class NoRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(1, TimeSpan.Zero, false, false));
    }
}
