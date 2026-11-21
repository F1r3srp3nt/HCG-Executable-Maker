using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Tests;

public sealed class ShipToStoreTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private string Zip(string name, params (string Path, string Text)[] entries)
    {
        var file = _t.Combine(name);
        using var z = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (p, text) in entries)
        {
            using var w = new StreamWriter(z.CreateEntry(p).Open());
            w.Write(text);
        }
        return file;
    }

    private const string Plist = """<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>MinimumOSVersion</key><string>15.0</string><key>UIDeviceFamily</key><array><integer>2</integer></array></dict></plist>""";

    [Fact]
    public async Task Detects_platform_format_minimum_os_and_hash_from_the_file_itself()
    {
        var ipa = await StoreArtifacts.DetectAsync(Zip("App.ipa", ("Payload/App.app/Info.plist", Plist), ("Payload/App.app/App", "bin")));
        Assert.True(ipa.IsSupported, string.Join(" ", ipa.Problems));
        Assert.Equal(("ipados", "ipa", "15.0"), (ipa.Platform, ipa.Format, ipa.MinimumOs)); // iPad-only (UIDeviceFamily 2)
        Assert.Equal(64, ipa.Sha256.Length);

        var mac = await StoreArtifacts.DetectAsync(Zip("Tool.zip", ("Tool.app/Contents/Info.plist", Plist), ("Tool.app/Contents/MacOS/Tool", "x")));
        Assert.Equal(("macos", "zip"), (mac.Platform, mac.Format));
        var win = await StoreArtifacts.DetectAsync(Zip("Tool-win.zip", ("tool.exe", "MZ")));
        Assert.Equal(("windows", "zip"), (win.Platform, win.Format));

        var exe = _t.Write("setup.exe", "MZ fake");
        Assert.Equal(("windows", "exe"), ((await StoreArtifacts.DetectAsync(exe)).Platform, (await StoreArtifacts.DetectAsync(exe)).Format));
        var renamed = _t.Write("not-really.exe", "PK\u0003\u0004");
        Assert.Contains((await StoreArtifacts.DetectAsync(renamed)).Problems, p => p.Contains("MZ"));
        Assert.False((await StoreArtifacts.DetectAsync(_t.Write("notes.txt", "x"))).IsSupported);
        Assert.Contains("ZIP first", (await StoreArtifacts.DetectAsync(Directory.CreateDirectory(_t.Combine("My.app")).FullName)).Problems[0]);
    }

    [Fact]
    public async Task Refuses_unsafe_archives_and_ipas_without_an_app()
    {
        var traversal = await StoreArtifacts.DetectAsync(Zip("Evil.ipa", ("Payload/App.app/Info.plist", Plist), ("Payload/../../etc/x", "x")));
        Assert.Contains(traversal.Problems, p => p.Contains("Unsafe archive entry"));
        var noApp = await StoreArtifacts.DetectAsync(Zip("Empty.ipa", ("readme.txt", "x")));
        Assert.Contains(noApp.Problems, p => p.Contains("Payload"));
    }

    [Fact]
    public async Task Reads_debian_control_fields_and_maps_jailbreak_packages_to_ios()
    {
        string Deb(string name, string arch)
        {
            static byte[] Tar(string path, string text)
            {
                using var ms = new MemoryStream();
                using (var w = new System.Formats.Tar.TarWriter(ms, leaveOpen: true))
                {
                    var e = new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };
                    w.WriteEntry(e);
                }
                return ms.ToArray();
            }
            static byte[] Gz(byte[] b) { using var ms = new MemoryStream(); using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(b); return ms.ToArray(); }
            static byte[] Member(string n, byte[] data)
            {
                var h = Encoding.ASCII.GetBytes($"{n,-16}{"0",-12}{"0",-6}{"0",-6}{"100644",-8}{data.Length,-10}`\n");
                return [.. h, .. data, .. data.Length % 2 == 1 ? new byte[] { (byte)'\n' } : []];
            }
            var control = Gz(Tar("./control", $"Package: com.example.sample\nVersion: 1.0.0\nArchitecture: {arch}\n"));
            var bytes = (byte[])[.. Encoding.ASCII.GetBytes("!<arch>\n"), .. Member("debian-binary", "2.0\n"u8.ToArray()), .. Member("control.tar.gz", control), .. Member("data.tar.gz", Gz(Tar("./x", "x")))];
            var f = _t.Combine(name);
            File.WriteAllBytes(f, bytes);
            return f;
        }
        var jb = await StoreArtifacts.DetectAsync(Deb("tweak.deb", "iphoneos-arm64"));
        Assert.Equal(("ios", "deb"), (jb.Platform, jb.Format));
        Assert.Equal(new StorePackage("com.example.sample", "1.0.0", "iphoneos-arm64"), jb.Package);
        Assert.Equal("linux", (await StoreArtifacts.DetectAsync(Deb("tool.deb", "amd64"))).Platform);
    }

    // ───────────── Upload: resume, retry, expired URL, errors ─────────────

    /// <summary>A fake HCG Store that receives chunks, can drop some, and can expire the upload URL once.</summary>
    private sealed class FakeStore : HttpMessageHandler
    {
        public readonly MemoryStream Received = new();
        public int DropNext;
        public bool ExpireOnce;
        public int Renewals, Puts;
        public readonly List<string> Bearer = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Headers.Authorization is { } a) Bearer.Add(a.Parameter!);
            var path = r.RequestUri!.AbsolutePath;
            if (path.StartsWith("/up/") && r.Method == HttpMethod.Get) return Json(200, new { received = Received.Length, size = 0 });
            if (path.StartsWith("/up/") && r.Method == HttpMethod.Put)
            {
                Puts++;
                if (ExpireOnce && path == "/up/1") { ExpireOnce = false; return Json(403, new { error = new { code = "upload_url_expired", message = "expired" } }); }
                if (DropNext > 0) { DropNext--; throw new HttpRequestException("connection reset"); }
                var range = r.Content!.Headers.ContentRange!;
                if (range.From != Received.Length) return Json(409, new { error = "offset_mismatch", message = "resume", received = Received.Length });
                await r.Content.CopyToAsync(Received, ct);
                return Json(200, new { received = Received.Length, size = range.Length });
            }
            if (path.EndsWith("/upload-url")) { Renewals++; return Json(200, new { uploadUrl = "https://store.test/up/2", expiresAt = 0, chunkBytes = 4, received = Received.Length }); }
            if (path.EndsWith("/whoami")) return Json(403, new { error = new { code = "not_publisher", message = "HCG has not granted your account permission to publish this product." } });
            return Json(404, new { error = new { code = "not_found", message = "Not found." } });
        }
        private static HttpResponseMessage Json(int status, object body) =>
            new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private static HcgStoreClient Client(FakeStore store) =>
        new(new HttpClient(store), new Uri("https://store.test/"), _ => Task.FromResult<string?>("token-1")) { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

    [Fact]
    public async Task Upload_resumes_after_a_dropped_chunk_and_an_expired_url_and_reports_confirmed_bytes_only()
    {
        var file = _t.Write("data.bin", "0123456789abcdefghij");
        var store = new FakeStore { DropNext = 1, ExpireOnce = true };
        var reports = new List<StoreUploadProgress>();
        await Client(store).UploadAsync("art-1", file, new StoreUploadUrl("https://store.test/up/1", 0, 4), new SyncProgress<StoreUploadProgress>(reports.Add), CancellationToken.None);
        Assert.Equal("0123456789abcdefghij", Encoding.ASCII.GetString(store.Received.ToArray()));
        Assert.Equal(1, store.Renewals);
        Assert.Contains(reports, r => r.Note?.Contains("expired") == true);
        Assert.Contains(reports, r => r.Failures == 1 && r.Note?.Contains("retrying") == true);
        Assert.Equal(20, reports[^1].Received);
        Assert.True(reports.Select(r => r.Received).SequenceEqual(reports.Select(r => r.Received).Order()), "progress never goes backwards");
    }

    [Fact]
    public async Task Upload_gives_up_after_the_bounded_retries_and_says_where_it_stopped()
    {
        var file = _t.Write("data.bin", "0123456789");
        var store = new FakeStore { DropNext = 99 };
        var ex = await Assert.ThrowsAsync<HcgStoreException>(() => Client(store).UploadAsync("art-1", file, new StoreUploadUrl("https://store.test/up/2", 0, 4), null, CancellationToken.None));
        Assert.Equal("upload_failed", ex.Code);
        Assert.Contains("after 3 failed attempts at byte 0", ex.Message);
        Assert.Equal(3, store.Puts); // 1 + 2 retries, never unlimited
    }

    [Fact]
    public async Task Store_errors_keep_their_code_and_message_and_the_token_is_the_hcg_connect_session()
    {
        var store = new FakeStore();
        var ex = await Assert.ThrowsAsync<HcgStoreException>(() => Client(store).WhoAmIAsync());
        Assert.Equal(("not_publisher", 403), (ex.Code, ex.Status));
        Assert.Contains("permission to publish", ex.Message);
        Assert.Equal(["token-1"], store.Bearer);
        var signedOut = new HcgStoreClient(new HttpClient(store), new Uri("https://store.test/"), _ => Task.FromResult<string?>(null));
        Assert.Equal("sign_in_required", (await Assert.ThrowsAsync<HcgStoreException>(() => signedOut.WhoAmIAsync())).Code);
    }

    [Theory]
    [InlineData("https://store.hakmicapitalgroup.com/x/y", "https://store.hakmicapitalgroup.com/")]
    [InlineData("http://127.0.0.1:8787", "http://127.0.0.1:8787/")]
    [InlineData("http://store.hakmicapitalgroup.com", null)]
    [InlineData("ftp://x", null)]
    [InlineData("", null)]
    public void Only_https_or_local_http_store_addresses_are_accepted(string input, string? expected) =>
        Assert.Equal(expected, StoreOrigin.Parse(input)?.ToString());

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    // ───────────── Ship to Shop: kinds, signing and install labels ─────────────

    private string ZipBytes(string name, params (string Path, byte[] Data)[] entries)
    {
        var file = _t.Combine(name);
        using var z = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (p, data) in entries) { using var w = z.CreateEntry(p).Open(); w.Write(data); }
        return file;
    }

    private const string DevicePlist = """<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.example.app</string><key>DTPlatformName</key><string>iphoneos</string><key>CFBundleSupportedPlatforms</key><array><string>iPhoneOS</string></array><key>MinimumOSVersion</key><string>15.0</string></dict></plist>""";
    private const string SimulatorPlist = """<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.example.app</string><key>DTPlatformName</key><string>iphonesimulator</string><key>CFBundleSupportedPlatforms</key><array><string>iPhoneSimulator</string></array></dict></plist>""";

    private string Ipa(string name, string plist = DevicePlist, bool signed = true, byte[]? profile = null)
    {
        var entries = new List<(string, byte[])> { ("Payload/App.app/Info.plist", Encoding.UTF8.GetBytes(plist)), ("Payload/App.app/App", "bin"u8.ToArray()) };
        if (signed) entries.Add(("Payload/App.app/_CodeSignature/CodeResources", "x"u8.ToArray()));
        if (profile is not null) entries.Add(("Payload/App.app/embedded.mobileprovision", profile));
        return ZipBytes(name, [.. entries]);
    }

    /// <summary>Answers every request with one fixed response (or throws), to see how Maker names the problem.</summary>
    private sealed class OneAnswer(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer());
    }

    private static async Task<(StoreProblem Problem, string Label, string Explanation)> Diagnose(HttpMessageHandler handler)
    {
        var origin = new Uri("https://store.hakmicapitalgroup.com/");
        var client = new HcgStoreClient(new HttpClient(handler), origin, _ => Task.FromResult<string?>("token"));
        var ex = await Assert.ThrowsAsync<HcgStoreException>(() => client.WhoAmIAsync());
        return StoreDiagnosis.Classify(ex, origin);
    }

    private static HttpResponseMessage Html(HttpStatusCode status)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent("<!DOCTYPE html><html><body>404 File not found</body></html>", System.Text.Encoding.UTF8, "text/html") };
        r.Headers.Server.ParseAdd("GitHub.com");
        return r;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string code, string message) =>
        new(status) { Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}", System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_static_site_at_the_store_address_is_reported_as_no_store_server_not_as_publishing_unavailable()
    {
        // store.hakmicapitalgroup.com is configured as the static storefront (GitHub Pages): GET /api/v1/publishing/whoami → 404 HTML.
        var (problem, label, explanation) = await Diagnose(new OneAnswer(() => Html(HttpStatusCode.NotFound)));
        Assert.Equal(StoreProblem.NotStoreServer, problem);
        Assert.Equal("No HCG Store server at this address", label);
        Assert.Contains("web page (text/html, server GitHub.com)", explanation, StringComparison.Ordinal);
        Assert.Contains("HCG Store server must be deployed", explanation, StringComparison.Ordinal);
        Assert.Equal(StoreProblem.NotStoreServer, (await Diagnose(new OneAnswer(() => Html(HttpStatusCode.MethodNotAllowed)))).Problem);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "publishing_disabled", StoreProblem.PublishingDisabled, "switched off")]
    [InlineData(HttpStatusCode.NotFound, "not_found", StoreProblem.EndpointMissing, "no HCG Store publishing API v1")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "method_not_allowed", StoreProblem.EndpointMissing, "no HCG Store publishing API v1")]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized", StoreProblem.SignInRejected, "Sign in with HCG again")]
    [InlineData(HttpStatusCode.Forbidden, "not_publisher", StoreProblem.NoPermission, "buying a product does not grant it")]
    [InlineData(HttpStatusCode.InternalServerError, "internal", StoreProblem.ServerError, "not a refusal")]
    [InlineData(HttpStatusCode.BadGateway, "bad_gateway", StoreProblem.ServerError, "HTTP 502")]
    public async Task Store_answers_are_named_precisely(HttpStatusCode status, string code, StoreProblem expected, string says)
    {
        var (problem, _, explanation) = await Diagnose(new OneAnswer(() => Json(status, code, "m")));
        Assert.Equal(expected, problem);
        Assert.Contains(says, explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, StoreProblem.DnsFailure, "does not resolve (DNS)")]
    [InlineData(HttpRequestError.SecureConnectionError, StoreProblem.TlsFailure, "secure (HTTPS) connection")]
    [InlineData(HttpRequestError.ConnectionError, StoreProblem.ConnectionFailed, "no connection")]
    public async Task Network_failures_are_named_and_never_a_refusal(HttpRequestError error, StoreProblem expected, string says)
    {
        var (problem, _, explanation) = await Diagnose(new OneAnswer(() => throw new HttpRequestException(error, "boom")));
        Assert.Equal(expected, problem);
        Assert.Contains(says, explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Simulator_builds_are_recognised_in_every_form_and_never_offered_as_installable()
    {
        var ipa = await StoreArtifacts.DetectAsync(Ipa("Sim.ipa", SimulatorPlist));
        Assert.False(ipa.IsSupported);
        Assert.Equal(("simulator-build", "simulator-only", false), (ipa.Kind, ipa.Readiness.Status, ipa.Readiness.OrdinaryInstall));
        Assert.Contains("cannot be installed on an iPhone or iPad", ipa.Problems[0]);
        Assert.Contains("Simulator", ipa.Label);

        var zipped = await StoreArtifacts.DetectAsync(ZipBytes("Sim.zip", ("App.app/Info.plist", Encoding.UTF8.GetBytes(SimulatorPlist)), ("App.app/App", "x"u8.ToArray())));
        Assert.Equal("simulator-build", zipped.Kind); // an iOS .app (flat Info.plist) is not taken for a macOS app
        Assert.Contains("Simulator", zipped.Problems[0]);

        var folder = Directory.CreateDirectory(_t.Combine("App.app")).FullName;
        File.WriteAllText(Path.Combine(folder, "Info.plist"), SimulatorPlist);
        var dir = await StoreArtifacts.DetectAsync(folder);
        Assert.Equal(("simulator-build", false), (dir.Kind, dir.IsSupported));
        Assert.Contains("does not accept", dir.Problems[0]);
        Assert.Contains("Export IPA", (await StoreArtifacts.DetectAsync(Directory.CreateDirectory(_t.Combine("App.xcarchive")).FullName)).Problems[0]);
    }

    [Fact]
    public async Task Device_builds_are_labelled_by_their_signature_and_provisioning_profile()
    {
        var unsigned = await StoreArtifacts.DetectAsync(Ipa("Unsigned.ipa", signed: false));
        Assert.True(unsigned.IsSupported);
        Assert.Equal(("device-build", "absent", "unsigned", false), (unsigned.Kind, unsigned.SignatureStructure, unsigned.Readiness.Status, unsigned.Readiness.OrdinaryInstall));
        Assert.Equal("unsigned", ShipToStoreService.DeclaredSigning(unsigned).Status); // a fact from the file, reported as such

        StoreArtifactCandidate With(string name, SigningFixtures.ProfileSpec spec) => StoreArtifacts.DetectAsync(Ipa(name, profile: SigningFixtures.Profile(spec))).GetAwaiter().GetResult();
        var adHoc = With("AdHoc.ipa", new() { Devices = ["00008110-000000000000001E"] });
        Assert.Equal(("ad-hoc", "registered-devices-only", false), (adHoc.Ios!.Provisioning, adHoc.Readiness.Status, adHoc.Readiness.OrdinaryInstall));
        Assert.Equal("Synthetic Test Profile", adHoc.Ios.ProfileName);
        Assert.False(adHoc.Ios.ProfileVerified); // synthetic signer: intact but not Apple's
        Assert.Equal("development", With("Dev.ipa", new() { Devices = ["x"], GetTaskAllow = true }).Ios!.Provisioning);
        Assert.Equal("enterprise-devices-only", With("Ent.ipa", new() { AllDevices = true }).Readiness.Status);
        Assert.Equal("app-store-profile", With("Store.ipa", new()).Readiness.Status);
        Assert.Equal("profile-expired", With("Old.ipa", new() { Devices = ["x"], Expires = DateTimeOffset.UtcNow.AddDays(-1) }).Readiness.Status);
        Assert.Equal("not-installable", (await StoreArtifacts.DetectAsync(Ipa("NoProfile.ipa"))).Readiness.Status);
        var garbage = await StoreArtifacts.DetectAsync(Ipa("Garbage.ipa", profile: "not a profile"u8.ToArray()));
        Assert.Equal(("unreadable", "signature-not-verified"), (garbage.Ios!.Provisioning, garbage.Readiness.Status));
    }

    private static byte[] Pe(bool signed)
    {
        var b = new byte[1024];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(128).CopyTo(b, 60);
        "PE\0\0"u8.CopyTo(b.AsSpan(128));
        BitConverter.GetBytes((ushort)0x20b).CopyTo(b, 128 + 24);
        if (signed) BitConverter.GetBytes(4096u).CopyTo(b, 128 + 24 + 112 + 32 + 4);
        return b;
    }

    [Fact]
    public async Task Installers_portable_apps_and_jailbreak_packages_get_their_own_kind_and_honest_labels()
    {
        // As .NET publishes it: the app's own unsigned Tool.exe next to Microsoft's signed createdump.exe.
        var unsignedZip = await StoreArtifacts.DetectAsync(ZipBytes("Tool-win-x64.zip", ("Tool.exe", Pe(false)), ("createdump.exe", Pe(true)), ("Tool.dll", "x"u8.ToArray())));
        Assert.Equal(("windows", "portable-app", "absent", "unsigned"), (unsignedZip.Platform, unsignedZip.Kind, unsignedZip.SignatureStructure, unsignedZip.Readiness.Status));
        Assert.Contains("SmartScreen", unsignedZip.Readiness.Label);
        var signedExe = _t.Combine("setup.exe");
        File.WriteAllBytes(signedExe, Pe(true));
        var setup = await StoreArtifacts.DetectAsync(signedExe);
        Assert.Equal(("installer", "present"), (setup.Kind, setup.SignatureStructure));
        Assert.Equal("signature-not-verified", setup.Readiness.Status); // present is not valid: only Windows can check it
        Assert.Equal("ready", (setup with { Signing = new("signed", "Valid", "Get-AuthenticodeSignature") }).Readiness.Status);

        var mac = await StoreArtifacts.DetectAsync(Zip("Tool.zip", ("Tool.app/Contents/Info.plist", Plist), ("Tool.app/Contents/MacOS/Tool", "x")));
        Assert.Equal(("portable-app", "unsigned"), (mac.Kind, mac.Readiness.Status));
        Assert.Contains("Gatekeeper", mac.Readiness.Label);
        Assert.Equal("not-notarized", (mac with { SignatureStructure = "present", Signing = new("signed", null, "codesign") }).Readiness.Status);
        Assert.True((mac with { SignatureStructure = "present", Signing = new("notarized", null, "codesign") }).Readiness.OrdinaryInstall);

        // A Linux portable ZIP (as Maker packages its own Linux build) is not a Windows app, and HCG Store takes no Linux ZIP.
        var elf = new byte[64]; "\u007FELF"u8.CopyTo(elf);
        var linuxZip = await StoreArtifacts.DetectAsync(ZipBytes("Tool-linux-x64.zip", ("Tool/Tool", elf), ("Tool/Tool.dll", "x"u8.ToArray())));
        Assert.Equal("linux", linuxZip.Platform);
        Assert.False(linuxZip.IsSupported);
        Assert.Contains("Linux apps only as .deb, .rpm or .AppImage", Assert.Single(linuxZip.Problems));
        Assert.DoesNotContain(linuxZip.Notes, n => n.Contains("Windows ZIP"));
        // A self-contained .NET Linux publish: hundreds of managed DLLs before the app host (no extension) in the archive.
        var many = Enumerable.Range(0, 400).Select(i => ($"Tool/System.Lib{i:D3}.dll", "MZ-not-elf"u8.ToArray())).Append(("Tool/zz-tool", elf)).ToArray();
        var bigLinux = await StoreArtifacts.DetectAsync(ZipBytes("Tool-big-linux-x64.zip", many));
        Assert.Equal("linux", bigLinux.Platform);
        Assert.Contains("Linux apps only as .deb, .rpm or .AppImage", Assert.Single(bigLinux.Problems));
        var noProgram = await StoreArtifacts.DetectAsync(ZipBytes("Docs.zip", ("README.txt", "hello"u8.ToArray())));
        Assert.Equal("windows", noProgram.Platform); // unchanged: no program at all is still only a note
        Assert.Contains(noProgram.Notes, n => n.Contains("Windows ZIP"));

        var apk = await StoreArtifacts.DetectAsync(_t.Write("app.apk", "PK"));
        Assert.Contains("Android packages are not distributed through HCG Store", apk.Problems[0]);
        Assert.Contains("HCG Store accepts:", apk.Problems[0]);
        Assert.Contains("Linux apps ship as .deb", (await StoreArtifacts.DetectAsync(_t.Write("tool.tar.gz", "x"))).Problems[0]);
    }

    private sealed class Hang : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_store_that_does_not_answer_in_time_is_unreachable_not_a_crash_and_not_a_refusal()
    {
        var client = new HcgStoreClient(new HttpClient(new Hang()) { Timeout = TimeSpan.FromMilliseconds(200) }, new Uri("https://store.test/"), _ => Task.FromResult<string?>("t"));
        var ex = await Assert.ThrowsAsync<HcgStoreException>(() => client.WhoAmIAsync());
        Assert.Equal(("store_timeout", 0), (ex.Code, ex.Status));
        // The person cancelling is still a cancellation, not "did not answer".
        using var cts = new CancellationTokenSource(100);
        var slow = new HcgStoreClient(new HttpClient(new Hang()), new Uri("https://store.test/"), _ => Task.FromResult<string?>("t"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.WhoAmIAsync(cts.Token));
    }

    [Fact]
    public void Ship_from_build_offers_only_verified_files_and_says_why_the_rest_are_not_offered()
    {
        var ok = _t.Write("Tool-1.2.0.zip", "zip");
        var record = new BuildRecord
        {
            Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), ProjectName = "Tool", StartedAt = DateTimeOffset.UtcNow, Status = BuildStatus.Succeeded,
            Settings = new BuildSettings { Identity = new AppIdentity { ApplicationName = "Tool", Version = "1.2.0", ApplicationId = "com.example.tool" }, ProjectRelativePath = ".", OutputDirectory = "/y", Platform = TargetPlatform.Linux, Architecture = TargetArchitecture.X64 },
        };
        ArtifactRecord Art(string path, ArtifactKind kind, VerificationStatus v) => new() { Id = Guid.NewGuid(), BuildId = record.Id, Path = path, Kind = kind, Format = PackagingFormat.PortableZip, SizeBytes = 3, Sha256 = "", Verification = v, CreatedAt = DateTimeOffset.UtcNow };
        record.Artifacts.Add(Art(ok, ArtifactKind.File, VerificationStatus.Passed));
        record.Artifacts.Add(Art(_t.Combine("Tool"), ArtifactKind.Directory, VerificationStatus.Passed));
        record.Artifacts.Add(Art(_t.Write("broken.zip", "x"), ArtifactKind.File, VerificationStatus.Failed));
        var ship = ShipFromBuild.From(record)!;
        Assert.Equal([ok], ship.Files);
        Assert.Equal("1.2.0", ship.Version);
        Assert.Equal(2, ship.NotOffered.Count);
        Assert.Contains(ship.NotOffered, n => n.Contains("folder"));
        Assert.Contains(ship.NotOffered, n => n.Contains("not verified"));
        record.Status = BuildStatus.Failed;
        Assert.Null(ShipFromBuild.From(record)); // only after a successful build
    }

    /// <summary>A Store whose create-release answer is lost once (connection reset after the server created it).</summary>
    private sealed class LostAnswerStore : HttpMessageHandler
    {
        public int Creates;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            HttpResponseMessage J(int s, object b) => new((HttpStatusCode)s) { Content = new StringContent(JsonSerializer.Serialize(b), Encoding.UTF8, "application/json") };
            if (path == "/api/v1/publishing/releases" && r.Method == HttpMethod.Post) Creates++;
            var release = new { id = "rel-0123456789abcdef", productId = "p", version = "1.0.0", build = "1", status = "draft", reused = Creates > 1, artifacts = Array.Empty<object>() };
            if (path == "/api/v1/publishing/releases" && r.Method == HttpMethod.Post)
            {
                if (Creates == 1) throw new HttpRequestException("connection reset by peer");
                return Task.FromResult(J(200, release));
            }
            if (path == "/api/v1/publishing/releases/rel-0123456789abcdef") return Task.FromResult(J(200, release));
            return Task.FromResult(J(404, new { error = new { code = "not_found", message = "Not found." } }));
        }
    }

    [Fact]
    public async Task A_lost_create_answer_is_retried_and_the_same_draft_is_continued_not_duplicated()
    {
        var store = new LostAnswerStore();
        var client = new HcgStoreClient(new HttpClient(store), new Uri("https://store.test/"), _ => Task.FromResult<string?>("t"));
        var file = await StoreArtifacts.DetectAsync(_t.Write("x.deb", "not a deb")); // unsupported → refused before any request
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShipToStoreService(client).ShipAsync(Req(), [file], null, null, CancellationToken.None));
        Assert.Equal(0, store.Creates);
        var ok = await StoreArtifacts.DetectAsync(Zip("Tool-win.zip", ("tool.exe", "MZ")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShipToStoreService(client).ShipAsync(Req(), [ok, ok], null, null, CancellationToken.None));
        var result = await new ShipToStoreService(client) { RetryDelay = TimeSpan.Zero }.ShipAsync(Req(), [ok with { Path = "/nonexistent" }], null, null, CancellationToken.None);
        Assert.Equal(2, store.Creates);
        Assert.True(result.ContinuedExistingDraft);
        Assert.Equal("rel-0123456789abcdef", result.Release.Id);
        static StoreReleaseRequest Req() => new() { ProductId = "p", Version = "1.0.0", Build = "1", Distribution = "free", Eligibility = "private" };
    }
}
