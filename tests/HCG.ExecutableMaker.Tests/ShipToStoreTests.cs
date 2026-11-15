using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.BuildEngine.Distribution;

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
}
