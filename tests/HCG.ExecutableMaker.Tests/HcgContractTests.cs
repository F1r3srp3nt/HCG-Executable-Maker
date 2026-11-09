using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Support;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Runs only when HCG_EXM_CONTRACT_SERVER_INFO points at the info file written by integration/hcg-website/test/contract-server.mjs.</summary>
public sealed class ContractServerFactAttribute : FactAttribute
{
    public ContractServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HCG_EXM_CONTRACT_SERVER_INFO") is not { Length: > 0 } f || !File.Exists(f))
            Skip = "Start integration/hcg-website/test/contract-server.mjs and set HCG_EXM_CONTRACT_SERVER_INFO to run the cross-language contract test.";
    }
}

/// <summary>
/// Cross-language contract test: this app's C# client against the website package's real request handler and the real
/// migration on PostgreSQL. MOCK identity (opaque tokens stand in for verified HCG JWTs) and a storage stand-in; it does
/// not contact HCG.
/// </summary>
public class HcgContractTests
{
    private sealed record Info(string BaseUrl, string ApiPath, Dictionary<string, string> Tokens, string RevokeUrl, Dictionary<string, string> Subjects);

    private static Info Load() => JsonSerializer.Deserialize<Info>(File.ReadAllText(Environment.GetEnvironmentVariable("HCG_EXM_CONTRACT_SERVER_INFO")!), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static (HcgApiClient Api, HcgAccountService Account) Client(Info info, string tokenName, string? subject = null)
    {
        var config = MockIdentityServer.Config with { ApiBaseUrl = new Uri(info.BaseUrl), ApiPath = info.ApiPath };
        var account = new HcgAccountService(config, new HcgAuthClient(new HttpClient(new MockIdentityServer()), config, TimeProvider.System), new SessionOnlyCredentialStore(), TimeProvider.System);
        account.AdoptForTesting(new HcgSession
        {
            AccessToken = info.Tokens[tokenName], RefreshToken = "unused", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Identity = new HcgIdentity(subject ?? info.Subjects.GetValueOrDefault(tokenName) ?? "unknown", null, null), ClientId = MockIdentityServer.ClientId,
        });
        return (new HcgApiClient(new HttpClient(), account), account);
    }

    [ContractServerFact]
    public async Task App_access_matches_the_servers_erp_decision()
    {
        var info = Load();
        var (api, _) = Client(info, "granted");
        var access = new AppAccessService(api, TimeProvider.System);
        await access.RefreshAsync();
        Assert.Equal(AppAccessState.Granted, access.State);
        Assert.True(access.Has(HcgPermissions.SupportSubmit));
        Assert.Equal(52428800, access.Directory!.Limits!.MaxAttachmentBytes);

        var (api2, _) = Client(info, "outsider", subject: "ignored");
        var denied = new AppAccessService(api2, TimeProvider.System);
        await denied.RefreshAsync();
        Assert.True(denied.State is AppAccessState.NotGranted or AppAccessState.Unavailable); // subject mismatch is also refused
        Assert.False(denied.Has(HcgPermissions.SupportSubmit));
    }

    [ContractServerFact]
    public async Task Submit_with_an_archive_round_trips_and_is_not_duplicated()
    {
        var info = Load();
        var (api, _) = Client(info, "granted");
        using var t = new TempDir();
        t.Write("proj/pubspec.yaml", "name: demo");
        t.Write("proj/.env", "SECRET=1");
        var plan = AttachmentPlanner.Plan(t.Combine("proj"));
        var archive = AttachmentPlanner.BuildArchive(plan, t.Combine("out"), "project.zip");
        using (var zip = ZipFile.OpenRead(archive.Path)) Assert.DoesNotContain(zip.Entries, e => e.Name == ".env");
        var file = new UploadFile(archive.Path, "project.zip", "application/zip");
        var draft = new CreateSupportRequest
        {
            ClientRequestId = Guid.NewGuid(), Title = "Flutter desktop for macOS", Mode = SubmissionMode.DiagnosticsAndFiles, AppVersion = "0.4.0",
            Details = new SupportRequestDetails { Goal = "Build my Flutter app for macOS", LanguageFramework = "Dart / Flutter", TargetOs = "macOS", OutputFormat = ".app", Problem = "Reported as unsupported." },
            Diagnostics = [new DiagnosticEntry("Project", "Language", "Dart")],
            Attachments = [file.Manifest()],
        };
        var service = new SupportService(api, new HttpClient());
        var progress = new List<UploadProgress>();
        var result = await service.SubmitAsync(draft, [file], new Progress<UploadProgress>(progress.Add), default);
        Assert.Equal(SubmitOutcome.Submitted, result.Outcome);
        Assert.Matches("^HCG-[A-Z2-9]{6}$", result.Request.Reference);
        Assert.Equal(SupportStatus.Submitted, result.Request.Status);

        // The same draft again: same request, nothing new created.
        var again = await service.SubmitAsync(draft, [file], null, default);
        Assert.Equal(result.Request.Id, again.Request.Id);
        var mine = await service.ListAsync();
        Assert.Single(mine.Requests, r => r.Id == result.Request.Id);
        var detail = await service.GetAsync(result.Request.Id);
        Assert.Equal("received", detail.Attachments.Single().State);
        Assert.Equal("not_scanned", detail.Attachments.Single().Scan);
        Assert.Contains("90 days", detail.RetentionNote);

        // Another account cannot see it.
        var (other, _) = Client(info, "outsider");
        var ex = await Assert.ThrowsAsync<HcgApiException>(() => new SupportService(other, new HttpClient()).GetAsync(result.Request.Id));
        Assert.Equal(HcgApiFailure.Forbidden, ex.Failure); // no ERP access at all for that account
    }

    [ContractServerFact]
    public async Task Revocation_on_HCG_signs_the_app_out_on_the_next_request()
    {
        var info = Load();
        var (api, account) = Client(info, "revoked");
        var service = new SupportService(api, new HttpClient());
        await service.ListAsync();
        using (var http = new HttpClient()) (await http.PostAsync(info.RevokeUrl, null)).EnsureSuccessStatusCode();
        var ex = await Assert.ThrowsAsync<HcgApiException>(() => service.ListAsync());
        Assert.Equal(HcgApiFailure.Revoked, ex.Failure);
        Assert.Equal(HcgAccountStatus.Revoked, account.Status);
    }
}
