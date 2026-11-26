using System.Net;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Ship to Store connection diagnostics: eleven separate checks instead of one "Publishing switched off", with HCG Store's
/// actual answers, what to do, and nothing secret in what is shown or copied.
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

    /// <summary>An access token shaped like HCG Connect's (unsigned here: HCG Store verifies the real ones).</summary>
    private static string Token(string clientId, long? exp = null, string scope = "openid email profile")
    {
        static string B64(object o) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(o)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64(new { alg = "ES256" })}.{B64(new { sub = "user-1", email = "dev@example.test", client_id = clientId, scope, exp = exp ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })}.c2lnbmF0dXJl";
    }

    private static readonly object Health = new { status = "ok", mode = "production", hcgConnect = "configured", entitlementAuthority = "hcg-ecosystem", payments = "disabled", protectedDownloads = "disabled" };

    private static object Status(string publishing, object? caller) => new
    {
        apiVersion = 1, minimumClientApiVersion = 1, features = new[] { "status", "private-submissions" }, server = new { mode = "production" }, publishing,
        hcgConnect = new { configured = true, issuer = "https://example.supabase.co/auth/v1" }, desktopClients = new { registered = 1 },
        permissions = new { scope = "hcg.store", submit = "store.publish", approve = "store.release.approve", publish = (string?)null },
        stages = new
        {
            draftCreation = Stage(publishing != "disabled"), artifactUpload = Stage(publishing != "disabled"), passportAttachment = Stage(publishing != "disabled"),
            releaseSubmission = Stage(publishing != "disabled"), approval = Stage(publishing != "disabled", "browser"),
            publicPublication = new { available = publishing == "enabled", where = "browser", reason = publishing == "enabled" ? null : "Public publication is switched off on this HCG Store server (HCG_STORE_PUBLISHING=submissions)." },
        },
        limits = new { maxUploadBytes = 2048L * 1048576, chunkBytes = 8 * 1048576, uploadUrlTtlSeconds = 900 },
        caller,
    };
    private static object Stage(bool on, string where = "app") => new { available = on, where, reason = on ? null : "Ship to Store is switched off on this HCG Store server (HCG_STORE_PUBLISHING=disabled)." };

    private static object Caller(string token = "valid", string? clientId = null, bool registered = true, string submit = "granted", string? reason = null) => new
    {
        token, reason, sub = "user-1", email = "dev@example.test", expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), scopes = new[] { "openid", "email", "profile" },
        clientId = clientId ?? Maker.ClientId, clientRegistered = registered, clientReason = registered ? null : "This HCG Connect client is not registered for publishing on this Store (HCG_STORE_PUBLISHING_CLIENT_IDS).",
        permissions = new { submit, approve = "not-granted", publish = "switched-off" }, authorizedProducts = submit == "granted" ? 2 : (int?)null,
    };

    private static readonly object Products = new
    {
        products = new object[]
        {
            new { id = "hcg-mail", title = "HCG Mail", source = "catalogue", editions = new[] { new { id = "windows-desktop", name = "Windows", platforms = new[] { "windows" } }, new { id = "macos", name = "macOS", platforms = new[] { "macos" } } } },
            new { id = "hcg-executable-maker", title = "HCG Executable Maker", source = "catalogue", editions = Array.Empty<object>() },
        },
    };

    private static async Task<(StoreDiagnosticsReport Report, Routes Http)> Run(Func<HttpRequestMessage, HttpResponseMessage> answer, string? token, bool signedIn = true)
    {
        var http = new Routes(answer);
        var client = new HcgStoreClient(new HttpClient(http), Origin, _ => Task.FromResult(token));
        var ctx = new StoreDiagnosticsContext(signedIn, signedIn ? "dev@example.test" : null, signedIn ? "Signed in." : "Not signed in.", Maker);
        return (await StoreConnectionDiagnostics.RunAsync(client, ctx, _ => Task.FromResult(token)), http);
    }

    private static readonly string[] Ids = ["connectivity", "api", "auth", "client", "token", "permissions", "products", "draft", "upload", "submission", "publication"];

    [Fact]
    public async Task The_deployed_server_with_publishing_switched_off_gets_eleven_separate_findings_not_one_generic_status()
    {
        // HCG Store as deployed today (835ac1a): health answers; every publishing route, including status, answers 404 publishing_disabled.
        var (r, _) = await Run(req => req.RequestUri!.AbsolutePath == "/api/v1/health" ? Json(200, Health)
            : Json(404, new { error = new { code = "publishing_disabled", message = "Publishing is not enabled on this Store." } }), Token(Maker.ClientId!));
        Assert.Equal(Ids, r.Checks.Select(c => c.Id));
        Assert.Equal(StoreCheckState.Passed, r["connectivity"].State);
        Assert.Contains("mode production", r["connectivity"].Summary);
        Assert.Equal(StoreCheckState.Warning, r["api"].State);
        Assert.Contains("predates the status API", r["api"].Summary);
        Assert.Equal(StoreCheckState.Passed, r["auth"].State);
        Assert.Contains("hcg-executable-maker-desktop-production", r["client"].Summary);
        Assert.Equal(StoreCheckState.Off, r["products"].State);
        foreach (var id in new[] { "draft", "upload", "submission" })
        {
            Assert.Equal(StoreCheckState.Failed, r[id].State);
            Assert.Contains("publishing_disabled", r[id].Summary);
            Assert.Contains("HCG_STORE_PUBLISHING=submissions", r[id].Action);
            Assert.Equal("HCG Store administrator", r[id].WhoActs);
            Assert.Contains("GET /api/v1/publishing/status", r[id].Response);   // the server's actual answer is shown
        }
        Assert.Equal(StoreCheckState.Off, r["publication"].State);
        Assert.False(r.CanUpload);
        Assert.Equal("error", r.Headline.Tone);
    }

    [Fact]
    public async Task Private_submissions_with_publication_off_are_ready_to_upload_and_list_the_authorised_products()
    {
        var token = Token(Maker.ClientId!);
        var (r, http) = await Run(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/health" => Json(200, Health),
            "/api/v1/publishing/status" => Json(200, Status("submissions", Caller())),
            "/api/v1/publishing/products" => Json(200, Products),
            _ => Json(404, new { }),
        }, token);
        Assert.All(Ids.Except(["publication"]), id => Assert.Equal(StoreCheckState.Passed, r[id].State));
        Assert.Equal(StoreCheckState.Off, r["publication"].State);
        Assert.True(r.CanUpload);
        Assert.Equal(("Ready to upload private drafts", "success"), r.Headline);
        Assert.Equal(["hcg-mail", "hcg-executable-maker"], r.Products.Select(p => p.Id));
        Assert.Contains("HCG Mail (2 editions)", r["products"].Summary);
        Assert.Contains("8 MB chunks", r["upload"].Summary);
        // The health check is public; the token goes only to the calls that need it, never anywhere else.
        Assert.Contains(http.Seen, s => s.Path == "/api/v1/health" && s.Bearer is null);
        Assert.Contains(http.Seen, s => s.Path == "/api/v1/publishing/status" && s.Bearer == token);
    }

    [Fact]
    public async Task A_token_issued_to_another_application_is_never_used_as_Makers_own()
    {
        var (r, _) = await Run(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/health" => Json(200, Health),
            "/api/v1/publishing/status" => Json(200, Status("submissions", Caller(clientId: StoreManagerClient))),
            _ => Json(200, Products),
        }, Token(StoreManagerClient));
        Assert.Equal(StoreCheckState.Failed, r["client"].State);
        Assert.Contains(StoreManagerClient, r["client"].Summary);
        Assert.Contains("never HCG Store Manager's", r["client"].Summary);
        Assert.False(r.CanUpload);
    }

    [Theory]
    [InlineData("client", "HCG Store administrator")]
    [InlineData("expired", "you")]
    [InlineData("permission", "HCG")]
    public async Task Each_refusal_names_its_cause_and_who_fixes_it(string cause, string who)
    {
        var caller = cause switch
        {
            "client" => Caller(registered: false),
            "expired" => Caller(token: "expired", reason: "The access token has expired."),
            _ => Caller(submit: "not-granted"),
        };
        var (r, _) = await Run(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/health" => Json(200, Health),
            "/api/v1/publishing/status" => Json(200, Status("submissions", caller)),
            "/api/v1/publishing/products" => cause == "permission" ? Json(200, new { products = Array.Empty<object>() }) : Json(200, Products),
            _ => Json(404, new { }),
        }, Token(Maker.ClientId!));
        var check = cause switch { "client" => r["client"], "expired" => r["token"], _ => r["permissions"] };
        Assert.Equal(StoreCheckState.Failed, check.State);
        Assert.Equal(who, check.WhoActs);
        Assert.False(string.IsNullOrWhiteSpace(check.Action));
        Assert.Contains(cause switch { "client" => "HCG_STORE_PUBLISHING_CLIENT_IDS", "expired" => "expired", _ => "store.publish" }, check.Summary + check.Action);
        Assert.False(r.CanUpload);
    }

    [Fact]
    public async Task An_unreachable_server_a_web_page_and_a_newer_API_are_told_apart()
    {
        var (dns, _) = await Run(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Failed, dns["connectivity"].State);
        Assert.Contains("DNS", dns["connectivity"].Summary);
        Assert.Equal(StoreCheckState.NotChecked, dns["api"].State);

        var (page, _) = await Run(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!doctype html><title>HCG Store</title>", Encoding.UTF8, "text/html") }, Token(Maker.ClientId!));
        Assert.Contains("web page", page["connectivity"].Summary);

        var newer = JsonSerializer.SerializeToNode(Status("submissions", Caller()))!.AsObject();
        newer["minimumClientApiVersion"] = 2;
        var (api, _) = await Run(req => req.RequestUri!.AbsolutePath == "/api/v1/health" ? Json(200, Health) : Json(200, newer), Token(Maker.ClientId!));
        Assert.Equal(StoreCheckState.Failed, api["api"].State);
        Assert.Contains("Update HCG Executable Maker", api["api"].Action);
    }

    [Fact]
    public async Task Signed_out_the_server_is_still_checked_and_sign_in_is_the_named_step()
    {
        var (r, http) = await Run(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/health" => Json(200, Health),
            "/api/v1/publishing/status" => Json(200, Status("submissions", null)),
            _ => Json(401, new { error = new { code = "sign_in_required", message = "Sign in with HCG Connect." } }),
        }, token: null, signedIn: false);
        Assert.Equal(StoreCheckState.Passed, r["connectivity"].State);
        Assert.Equal(StoreCheckState.Failed, r["auth"].State);
        Assert.Contains("Account page", r["auth"].Action);
        Assert.Equal(StoreCheckState.NotChecked, r["token"].State);
        Assert.All(http.Seen, s => Assert.Null(s.Bearer));
    }

    [Fact]
    public async Task Copied_diagnostics_contain_no_token_cookie_or_credential_but_keep_ordinary_words()
    {
        var token = Token(Maker.ClientId!);
        var leaky = JsonSerializer.SerializeToNode(Status("submissions", Caller()))!.AsObject();
        leaky["debug"] = new System.Text.Json.Nodes.JsonObject { ["access_token"] = token, ["refresh_token"] = "r-0123456789abcdef", ["cookie"] = "__Host-hcg_store_session=abcdef0123456789" };
        var (r, _) = await Run(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/health" => Json(200, Health),
            "/api/v1/publishing/status" => Json(200, leaky),
            "/api/v1/publishing/products" => Json(200, Products),
            _ => Json(404, new { }),
        }, token);
        var text = r.ToText();
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(token.Split('.')[1], text);
        Assert.DoesNotContain("r-0123456789abcdef", text);
        Assert.DoesNotContain("abcdef0123456789", text);
        // Redaction does not hide ordinary words that merely look like credential names.
        Assert.Contains("HCG Connect authentication: Signed in as dev@example.test", text);
        Assert.Contains("\"entitlementAuthority\":\"hcg-ecosystem\"", text);
        Assert.Contains("[OK] Store server connectivity", text);
        Assert.Contains("[Off] Public publication", text);
    }
}
