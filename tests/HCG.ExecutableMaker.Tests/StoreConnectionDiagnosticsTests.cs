using HCG.ExecutableMaker.BuildEngine.Distribution;
using System.Net;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Distribution Studio connection diagnostics against HCG Store's live publishing contract (health, whoami, capabilities,
/// products, Release Manager): ten separate checks with HCG Store's actual answers, what to do, and nothing secret in what
/// is shown or copied.
/// </summary>
public class StoreConnectionDiagnosticsTests
{
    private static readonly Uri Origin = new("https://store.hakmicapitalgroup.com/");
    private static readonly HcgConnectConfiguration Maker = HcgConnectConfiguration.Production;
    private const string StoreManagerClient = "77777777-6666-4555-8444-333333333333";

    private sealed class Routes(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(string Path, string? Bearer)> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add((r.RequestUri!.AbsolutePath, r.Headers.Authorization?.Parameter));
            return Task.FromResult(answer(r));
        }
    }

    private static HttpResponseMessage Json(int status, object body) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Error(int status, string code, string message) => Json(status, new { error = new { code, message } });

    /// <summary>An access token shaped like HCG Connect's (unsigned here: HCG Store verifies the real ones).</summary>
    private static string Token(string clientId, long? exp = null, string scope = "openid email profile")
    {
        static string B64(object o) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(o)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64(new { alg = "ES256" })}.{B64(new { sub = "user-1", email = "dev@example.test", client_id = clientId, scope, exp = exp ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })}.c2lnbmF0dXJl";
    }

    /// <summary>The live production health answer (store.hakmicapitalgroup.com, 26 Nov 2026).</summary>
    private static object Health(string publishing = "uploads") => new { status = "ok", mode = "production", hcgConnect = "configured", entitlementAuthority = "hcg-ecosystem", payments = "disabled", protectedDownloads = "disabled", publishing };

    private static object Flags(string mode, bool studio = true) => new
    {
        products = true, drafts = true, uploads = true, resumableUploads = true, passports = true, submission = true,
        approval = mode == "enabled", publication = mode == "enabled", publicDelivery = false, offers = false,
        productMetadata = studio, publicationRequests = studio, availabilityRequests = studio, supersedeCompatible = studio, withdraw = studio,
    };
    private static object WhoAmI(string mode = "uploads") => new { sub = "user-1", email = "dev@example.test", via = "desktop", publisherPermission = "store.publish", approverPermission = (string?)null, maxUploadBytes = 2048L * 1048576, chunkBytes = 8 * 1048576, publishingMode = mode, capabilities = Flags(mode) };
    private static object Caps(string mode = "uploads", bool studio = true) => new { apiVersion = 1, maxUploadBytes = 2048L * 1048576, chunkBytes = 8 * 1048576, uploadUrlTtlSeconds = 900, approvalAndPublication = "browser-only", publishingMode = mode, capabilities = Flags(mode, studio) };

    private static readonly object Products = new
    {
        products = new object[]
        {
            new { id = "hcg-mail", title = "HCG Mail", source = "catalogue", editions = new[] { new { id = "windows-desktop", name = "Windows", platforms = new[] { "windows" } }, new { id = "macos", name = "macOS", platforms = new[] { "macos" } } }, icon = "https://store.hakmicapitalgroup.com/media/products/hcg-mail/icon-256.webp", category = "communication" },
            new { id = "hcg-executable-maker", title = "HCG Executable Maker", source = "catalogue", editions = Array.Empty<object>() },
        },
    };
    private static readonly object Manager = new { releases = new[] { new { id = "rel-0000000000000001", productId = "hcg-mail", version = "1.0.0", build = "1", status = "draft", stage = "uploaded", files = 1 } }, auditIntact = true };

    private static Func<HttpRequestMessage, HttpResponseMessage> Live(string mode = "uploads", Func<string, HttpResponseMessage?>? over = null) => req =>
        over?.Invoke(req.RequestUri!.AbsolutePath) ?? req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/health" => Json(200, Health(mode)),
            "/api/v1/publishing/whoami" => Json(200, WhoAmI(mode)),
            "/api/v1/publishing/capabilities" => Json(200, Caps(mode)),
            "/api/v1/publishing/products" => Json(200, Products),
            "/api/v1/publishing/manager" => Json(200, Manager),
            _ => Error(404, "not_found", "Not found."),
        };

    private static async Task<(StoreDiagnosticsReport Report, Routes Http)> Run(Func<HttpRequestMessage, HttpResponseMessage> answer, string? token, bool signedIn = true)
    {
        var http = new Routes(answer);
        var client = new HcgStoreClient(new HttpClient(http), Origin, _ => Task.FromResult(token));
        var ctx = new StoreDiagnosticsContext(signedIn, signedIn ? "dev@example.test" : null, signedIn ? "Signed in." : "Not signed in.", Maker);
        return (await StoreConnectionDiagnostics.RunAsync(client, ctx, _ => Task.FromResult(token)), http);
    }

    [Fact]
    public async Task Uploads_mode_with_the_issued_client_and_permission_is_ready_and_states_every_capability()
    {
        var token = Token(Maker.ClientId!);
        var (r, http) = await Run(Live(), token);
        Assert.Equal(StoreConnectionDiagnostics.CheckIds, r.Checks.Select(c => c.Id));
        Assert.All(r.Checks.Where(c => c.Id != "publication"), c => Assert.Equal(StoreCheckState.Passed, c.State));
        Assert.Equal(StoreCheckState.Off, r["publication"].State);
        Assert.True(r.CanUpload);
        Assert.Equal(("Ready to upload private drafts", "success"), r.Headline);
        Assert.Equal("uploads", r.PublishingMode);
        Assert.Contains("v1", r["api"].Summary);
        Assert.Contains("uploads: private drafts", r["mode"].Summary);
        Assert.Contains("hcg-executable-maker-desktop-production", r["client"].Summary);
        Assert.Contains(HcgConnectConfiguration.ProductionClientId, r["client"].Summary);
        Assert.Contains("8 MB chunks", r["upload"].Summary);
        Assert.Contains("HCG Mail (2 editions)", r["products"].Summary);
        Assert.Contains("1 release(s)", r["manager"].Summary);
        Assert.Equal(["hcg-mail", "hcg-executable-maker"], r.Products.Select(p => p.Id));
        Assert.Equal("communication", r.Products[0].Category);
        // The health check is public; the token goes only to the publishing calls.
        Assert.Contains(http.Seen, s => s.Path == "/api/v1/health" && s.Bearer is null);
        Assert.All(http.Seen.Where(s => s.Path.StartsWith("/api/v1/publishing/")), s => Assert.Equal(token, s.Bearer));
        Assert.DoesNotContain(http.Seen, s => s.Path == "/api/v1/publishing/status");
    }

    [Fact]
    public async Task Disabled_publishing_is_named_with_who_fixes_it_and_nothing_is_uploaded()
    {
        var (r, _) = await Run(Live("disabled", path => path == "/api/v1/health" ? null : Error(404, "publishing_disabled", "Publishing is not enabled on this Store.")), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Failed, r["mode"].State);
        Assert.Contains("HCG_STORE_PUBLISHING=uploads", r["mode"].Action);
        Assert.Equal("HCG Store administrator", r["mode"].WhoActs);
        Assert.Equal(StoreCheckState.Failed, r["upload"].State);
        Assert.False(r.CanUpload);
    }

    [Fact]
    public async Task Disabled_public_publication_does_not_block_authorised_private_uploads()
    {
        var (r, _) = await Run(Live("uploads"), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Off, r["publication"].State);
        Assert.Equal(StoreCheckState.Passed, r["upload"].State);
        Assert.True(r.CanUpload);
        var (enabled, _) = await Run(Live("enabled"), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Passed, enabled["publication"].State);
    }

    [Fact]
    public async Task A_token_issued_to_another_application_is_never_used_as_Makers_own()
    {
        var (r, _) = await Run(Live(), Token(StoreManagerClient));
        Assert.Equal(StoreCheckState.Failed, r["client"].State);
        Assert.Contains(StoreManagerClient, r["client"].Summary);
        Assert.Contains("never HCG Store Manager's", r["client"].Summary);
        Assert.False(r.CanUpload);
    }

    [Fact]
    public async Task A_Store_that_does_not_accept_Makers_client_says_so_and_keeps_the_issued_client_ID()
    {
        var (r, _) = await Run(Live(over: p => p == "/api/v1/publishing/whoami" ? Error(401, "invalid_token", "HCG Connect did not confirm this sign-in: This application is not allowed to publish to HCG Store.") : null), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Failed, r["client"].State);
        Assert.Equal("HCG Store administrator", r["client"].WhoActs);
        Assert.Contains("HCG_STORE_PUBLISHING_CLIENT_IDS", r["client"].Action);
        Assert.Contains(HcgConnectConfiguration.ProductionClientId, r["client"].Action);
        Assert.Contains("stays as HCG issued it", r["client"].Action);
        Assert.NotEqual(StoreCheckState.Passed, r["permission"].State);
        Assert.False(r.CanUpload);
    }

    [Fact]
    public async Task An_account_without_the_central_publisher_permission_cannot_upload()
    {
        var (r, _) = await Run(Live(over: p => p switch
        {
            "/api/v1/publishing/capabilities" => Error(403, "not_publisher", "HCG has not granted your account a publishing permission."),
            "/api/v1/publishing/products" => Json(200, new { products = Array.Empty<object>() }),
            _ => null,
        }), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Failed, r["permission"].State);
        Assert.Equal("HCG", r["permission"].WhoActs);
        Assert.Contains("store.publish", r["permission"].Action);
        Assert.Equal(StoreCheckState.Failed, r["products"].State);
        Assert.Equal(StoreCheckState.Failed, r["upload"].State);
        Assert.False(r.CanUpload);
    }

    [Fact]
    public async Task An_expired_sign_in_is_named_and_a_newer_API_asks_for_an_update()
    {
        var (expired, _) = await Run(Live(), Token(Maker.ClientId!, exp: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()));
        Assert.Equal(StoreCheckState.Failed, expired["auth"].State);
        Assert.Contains("expired", expired["auth"].Summary);

        var newer = JsonSerializer.SerializeToNode(Caps())!.AsObject();
        newer["apiVersion"] = 2;
        var (api, _) = await Run(Live(over: p => p == "/api/v1/publishing/capabilities" ? Json(200, newer) : null), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Failed, api["api"].State);
        Assert.Contains("Update HCG Executable Maker", api["api"].Action);
    }

    [Fact]
    public async Task An_unreachable_server_and_a_web_page_are_told_apart()
    {
        var (dns, _) = await Run(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Failed, dns["connectivity"].State);
        Assert.Contains("DNS", dns["connectivity"].Summary);
        Assert.Equal(StoreCheckState.NotChecked, dns["api"].State);

        var (page, _) = await Run(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!doctype html><title>HCG Store</title>", Encoding.UTF8, "text/html") }, Token(Maker.ClientId!));
        Assert.Contains("web page", page["connectivity"].Summary);
    }

    [Fact]
    public async Task Signed_out_the_public_server_facts_are_still_shown_and_sign_in_is_the_named_step()
    {
        var (r, http) = await Run(Live(), token: null, signedIn: false);
        Assert.Equal(StoreCheckState.Passed, r["connectivity"].State);
        Assert.Equal(StoreCheckState.Passed, r["mode"].State);
        Assert.Equal(StoreCheckState.Warning, r["upload"].State);
        Assert.Equal(StoreCheckState.Failed, r["auth"].State);
        Assert.Contains("Account page", r["auth"].Action);
        Assert.Equal(StoreCheckState.NotChecked, r["permission"].State);
        Assert.Equal(["/api/v1/health"], http.Seen.Select(s => s.Path));
        Assert.All(http.Seen, s => Assert.Null(s.Bearer));
    }

    [Fact]
    public async Task An_older_Store_without_capability_flags_is_read_without_assuming_them()
    {
        var oldCaps = new { apiVersion = 1, maxUploadBytes = 1L << 30, chunkBytes = 8 << 20, uploadUrlTtlSeconds = 900 };
        var oldWho = new { sub = "user-1", email = "dev@example.test", via = "desktop", publisherPermission = "store.publish", maxUploadBytes = 1L << 30, chunkBytes = 8 << 20 };
        var (r, _) = await Run(Live(over: p => p switch
        {
            "/api/v1/health" => Json(200, new { status = "ok", mode = "production", hcgConnect = "configured" }),
            "/api/v1/publishing/whoami" => Json(200, oldWho),
            "/api/v1/publishing/capabilities" => Json(200, oldCaps),
            _ => null,
        }), Token(Maker.ClientId!));
        Assert.Null(r.Flags);
        Assert.Equal(StoreCheckState.Warning, r["mode"].State);
        Assert.Contains("none stated", r["api"].Summary);
        Assert.Equal(StoreCheckState.Off, r["publication"].State);
        Assert.True(r.CanUpload);
    }

    [Fact]
    public async Task Copied_diagnostics_contain_no_token_cookie_or_credential_but_keep_ordinary_words()
    {
        var token = Token(Maker.ClientId!);
        var leaky = JsonSerializer.SerializeToNode(Health())!.AsObject();
        leaky["debug"] = new System.Text.Json.Nodes.JsonObject { ["access_token"] = token, ["refresh_token"] = "r-0123456789abcdef", ["cookie"] = "__Host-hcg_store_session=abcdef0123456789" };
        var (r, _) = await Run(Live(over: p => p == "/api/v1/health" ? Json(200, leaky) : null), token);
        var text = r.ToText();
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(token.Split('.')[1], text);
        Assert.DoesNotContain("r-0123456789abcdef", text);
        Assert.DoesNotContain("abcdef0123456789", text);
        Assert.Contains("HCG Connect authentication: Signed in as dev@example.test", text);
        Assert.Contains("\"entitlementAuthority\":\"hcg-ecosystem\"", text);
        Assert.Contains("[OK] Server availability", text);
        Assert.Contains("[Off] Public publication", text);
        Assert.Contains("publishing uploads", text);
        Assert.Contains("Capabilities: products, drafts, uploads", text);
    }
}

/// <summary>Review follow-ups: tokens never go to a non-Store host; a failing token refresh still yields a report; redaction of camelCase names.</summary>
public class StoreDiagnosticsHardeningTests
{
    private static HttpResponseMessage Html() => new(HttpStatusCode.OK) { Content = new StringContent("<!doctype html><title>Not a Store</title>", Encoding.UTF8, "text/html") };

    [Fact]
    public async Task The_sign_in_is_never_sent_to_an_address_that_is_not_an_HCG_Store_server()
    {
        var seen = new List<string?>();
        var handler = new Lambda(r => { seen.Add(r.Headers.Authorization?.Parameter); return Html(); });
        var client = new HcgStoreClient(new HttpClient(handler), new Uri("https://example.test/"), _ => Task.FromResult<string?>("secret-token-value"));
        var report = await StoreConnectionDiagnostics.RunAsync(client, new(true, "dev@example.test", "Signed in.", HcgConnectConfiguration.Production), _ => Task.FromResult<string?>("secret-token-value"));
        Assert.Equal(StoreCheckState.Failed, report["connectivity"].State);
        Assert.All(seen, b => Assert.Null(b));
        Assert.Contains("not sent", report["products"].Summary);
    }

    [Fact]
    public async Task A_token_refresh_that_throws_still_produces_a_report_naming_the_sign_in()
    {
        var handler = new Lambda(r => r.RequestUri!.AbsolutePath == "/api/v1/health"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"mode\":\"production\"}", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"publishing_disabled\",\"message\":\"off\"}}", Encoding.UTF8, "application/json") });
        Func<CancellationToken, Task<string?>> broken = _ => throw new InvalidOperationException("The keychain could not be written.");
        var client = new HcgStoreClient(new HttpClient(handler), new Uri("https://store.test/"), broken);
        var report = await StoreConnectionDiagnostics.RunAsync(client, new(true, "dev@example.test", "Signed in.", HcgConnectConfiguration.Production), broken);
        Assert.Equal(StoreCheckState.Failed, report["auth"].State);
        Assert.Contains("keychain could not be written", report["auth"].Summary);
        Assert.Equal(10, report.Checks.Count);
    }

    [Theory]
    [InlineData("{\"accessToken\":\"opaque-0123456789\"}", "opaque-0123456789")]
    [InlineData("{\"refreshToken\":\"opaque-0123456789\"}", "opaque-0123456789")]
    [InlineData("{\"token\":\"opaque-0123456789\"}", "opaque-0123456789")]
    [InlineData("{\"sessionId\":\"opaque-0123456789\"}", "opaque-0123456789")]
    public void Credential_fields_are_redacted_in_snake_and_camel_case(string json, string secret) =>
        Assert.DoesNotContain(secret, Security.SecretRedactor.RedactCredentials(json));

    [Fact]
    public void The_Stores_token_status_words_are_kept() =>
        Assert.Contains("\"token\":\"expired\"", Security.SecretRedactor.RedactCredentials("{\"caller\":{\"token\":\"expired\",\"reason\":\"The access token has expired.\"}}"));

    private sealed class Lambda(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(answer(r));
    }
}

/// <summary>Release Passport attachment never turns a verified upload into a failure, and never hides a passport that disagrees with the file.</summary>
public sealed class ShipPassportAttachmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-passport-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Store : HttpMessageHandler
    {
        public readonly MemoryStream Bytes = new();
        public System.Text.Json.Nodes.JsonObject? Passport;
        public long Size;
        public string Sha = "";
        private static HttpResponseMessage J(int s, object b) => new((HttpStatusCode)s) { Content = new StringContent(JsonSerializer.Serialize(b), Encoding.UTF8, "application/json") };
        private object Artifact(string status) => new { id = "art-1", fileName = "Q.exe", platform = "windows", format = "exe", sizeBytes = Size, declaredSha256 = Sha, sha256 = status == "verified" ? Sha : null, status };
        private object Release(string status) => new { id = "rel-1", productId = "p", version = "1.0.0", build = "1", status = "draft", artifacts = status == "none" ? Array.Empty<object>() : new[] { Artifact(status) } };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var p = r.RequestUri!.AbsolutePath;
            if (p == "/api/v1/publishing/releases" && r.Method == HttpMethod.Post) return J(201, Release("none"));
            if (p.EndsWith("/artifacts")) { var d = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)).RootElement; Size = d.GetProperty("sizeBytes").GetInt64(); Sha = d.GetProperty("sha256").GetString()!;
                return J(201, new { artifact = Artifact("awaiting-upload"), uploadUrl = "https://store.test/up/1", expiresAt = 0, chunkBytes = 1 << 20 }); }
            if (p == "/up/1" && r.Method == HttpMethod.Get) return J(200, new { received = Bytes.Length, size = Size });
            if (p == "/up/1") { await r.Content!.CopyToAsync(Bytes, ct); return J(200, new { received = Bytes.Length, size = Size }); }
            if (p.EndsWith("/complete")) return J(200, new { artifact = Artifact("verified") });
            if (p.EndsWith("/passport")) { Passport = System.Text.Json.Nodes.JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!.AsObject(); return J(200, new { artifact = Artifact("verified") }); }
            if (p == "/api/v1/publishing/releases/rel-1") return J(200, Release("verified"));
            return J(404, new { error = new { code = "not_found", message = "Not found." } });
        }
    }

    private async Task<(ShipResult Result, Store Store)> Ship(Func<string, string, object> passport)
    {
        var file = Path.Combine(_dir, "Q.exe");
        await File.WriteAllBytesAsync(file, [(byte)'M', (byte)'Z', .. new byte[2048]]);
        var c = await StoreArtifacts.DetectAsync(file);
        var pp = file + ".release-passport.json";
        await File.WriteAllTextAsync(pp, JsonSerializer.Serialize(passport(c.FileName, c.Sha256)));
        c = c with { Provenance = new StoreProvenance.Result("0123abcd", "local", ".NET", null, pp, []) };
        var store = new Store();
        var client = new HcgStoreClient(new HttpClient(store), new Uri("https://store.test/"), _ => Task.FromResult<string?>("t"));
        var req = new StoreReleaseRequest { ProductId = "p", Version = "1.0.0", Build = "1", Distribution = "free", Eligibility = "private" };
        return (await new ShipToStoreService(client).ShipAsync(req, [c], null, null, CancellationToken.None), store);
    }

    [Fact]
    public async Task A_matching_passport_is_attached_with_the_SHA_512_computed_from_the_file()
    {
        var (r, store) = await Ship((name, sha) => new { format = "hcg.release-passport", formatVersion = 1, artifact = new { filename = name, sha256 = sha, size = 2050 } });
        Assert.True(r.AllVerified);
        Assert.Equal("attached", r.Items.Single().Passport);
        Assert.Equal(128, store.Passport!["artifact"]!["sha512"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData("sha512-differs")]
    [InlineData("filename-not-text")]
    [InlineData("other-file")]
    public async Task A_passport_that_disagrees_is_not_sent_and_the_upload_stays_verified(string defect)
    {
        var (r, store) = await Ship((name, sha) => defect switch
        {
            "sha512-differs" => new { format = "hcg.release-passport", formatVersion = 1, artifact = new { filename = name, sha256 = sha, size = 2050, sha512 = new string('0', 128) } },
            "filename-not-text" => new { format = "hcg.release-passport", formatVersion = 1, artifact = (object)new { filename = 5, sha256 = sha, size = 2050 } },
            _ => new { format = "hcg.release-passport", formatVersion = 1, artifact = (object)new { filename = "Other.exe", sha256 = sha, size = 2050 } },
        });
        Assert.True(r.AllVerified);
        Assert.Equal("refused", r.Items.Single().Passport);
        Assert.Null(store.Passport);
    }
}
