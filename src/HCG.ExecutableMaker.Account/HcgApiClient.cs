using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCG.ExecutableMaker.Account;

public enum HcgApiFailure
{
    NotSignedIn,
    /// <summary>401 after one refresh: the session is no longer accepted.</summary>
    Unauthorized,
    /// <summary>403: this account may not do this (ERP App Access / staff permission). Not an outage.</summary>
    Forbidden,
    /// <summary>403 revoked: HCG withdrew Executable Maker access for this account.</summary>
    Revoked,
    NotFound,
    /// <summary>The API route does not exist on HCG yet (the website package is not deployed).</summary>
    NotDeployed,
    Invalid,
    Conflict,
    TooLarge,
    RateLimited,
    QuotaExhausted,
    /// <summary>HCG could not be reached or failed (network, 5xx). Says nothing about access.</summary>
    Unavailable,
    Cancelled,
}

public sealed class HcgApiException(HcgApiFailure failure, string message, string? code = null) : Exception(message)
{
    public HcgApiFailure Failure { get; } = failure;
    public string? Code { get; } = code;
    /// <summary>True when the problem is HCG's availability, not this account's permission.</summary>
    public bool IsOutage => Failure is HcgApiFailure.Unavailable or HcgApiFailure.NotDeployed or HcgApiFailure.RateLimited;
}

/// <summary>
/// Calls Executable Maker's routes on the HCG website (<c>hcg.executable-maker/1</c>). Every request carries the
/// caller's own access token; the server verifies it and decides in the database (ERP App Access) on every request.
/// The app never decides access itself. One 401 triggers one refresh-and-retry; revocation clears the session.
/// </summary>
public sealed class HcgApiClient(HttpClient http, HcgAccountService account)
{
    public const string Contract = "hcg.executable-maker/1";
    public const string ContractHeader = "X-ExecutableMaker-Contract";
    public const string OrganisationHeader = "X-HCG-Organisation";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public HcgAccountService Account => account;

    public Uri Url(string relative) =>
        new(account.Configuration.ApiBaseUrl, account.Configuration.ApiPath.TrimEnd('/') + "/" + relative.TrimStart('/'));

    public Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);
    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct) => SendAsync<T>(HttpMethod.Post, path, body, ct);

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await account.GetAccessTokenAsync(ct).ConfigureAwait(false)
                        ?? throw new HcgApiException(HcgApiFailure.NotSignedIn, "Sign in with HCG to use this feature.");
            using var request = new HttpRequestMessage(method, Url(path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add(ContractHeader, Contract);
            if (account.OrganisationId is { } org) request.Headers.Add(OrganisationHeader, org);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new HcgApiException(HcgApiFailure.Cancelled, "Cancelled."); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new HcgApiException(HcgApiFailure.Unavailable, "HCG could not be reached. Check your connection and try again. Nothing was changed.");
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && await account.RecoverFromUnauthorizedAsync(token, ct).ConfigureAwait(false))
                    continue;
                if (response.IsSuccessStatusCode)
                {
                    try { return JsonSerializer.Deserialize<T>(text, Json) ?? throw new JsonException("empty"); }
                    catch (JsonException) { throw new HcgApiException(HcgApiFailure.Unavailable, "HCG returned an unreadable response."); }
                }
                throw await MapErrorAsync(response.StatusCode, text, path).ConfigureAwait(false);
            }
        }
        throw new HcgApiException(HcgApiFailure.Unauthorized, "HCG no longer accepts this session. Sign in again.");
    }

    private async Task<HcgApiException> MapErrorAsync(HttpStatusCode status, string text, string path)
    {
        string? code = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            code = IdTokenValidator.Str(doc.RootElement, "error");
            message = IdTokenValidator.Str(doc.RootElement, "message");
        }
        catch (JsonException) { }
        message = message is null ? null : HCG.ExecutableMaker.Security.SecretRedactor.Redact(message.Length > 300 ? message[..300] : message);
        switch ((int)status)
        {
            case 401:
                await account.MarkExpiredAsync("Your HCG session has ended. Sign in again.").ConfigureAwait(false);
                return new HcgApiException(HcgApiFailure.Unauthorized, "HCG no longer accepts this session. Sign in again.", code);
            case 403 when code == "revoked":
                var revoked = message ?? "HCG has withdrawn Executable Maker access for this account.";
                await account.MarkRevokedAsync(revoked).ConfigureAwait(false);
                return new HcgApiException(HcgApiFailure.Revoked, revoked, code);
            case 403 when code == "quota_exhausted":
                return new HcgApiException(HcgApiFailure.QuotaExhausted, message ?? "Your HCG quota for this feature is used up.", code);
            case 403:
                return new HcgApiException(HcgApiFailure.Forbidden, message ?? "Your HCG account does not have permission for this. Access is granted by HCG in ERP ▸ App Access.", code);
            case 404 when code is null:
                return new HcgApiException(HcgApiFailure.NotDeployed, "This HCG service is not available yet (its website route does not exist).", code);
            case 404:
                return new HcgApiException(HcgApiFailure.NotFound, message ?? "Not found (or not yours).", code);
            case 409:
                return new HcgApiException(HcgApiFailure.Conflict, message ?? "This was already changed.", code);
            case 413:
                return new HcgApiException(HcgApiFailure.TooLarge, message ?? "This is larger than HCG accepts.", code);
            case 429:
                return new HcgApiException(HcgApiFailure.RateLimited, "Too many requests to HCG. Wait a minute and try again.", code);
            case >= 500:
                return new HcgApiException(HcgApiFailure.Unavailable, "HCG is temporarily unavailable. Try again later. Nothing was changed.", code);
            default:
                return new HcgApiException(HcgApiFailure.Invalid, message ?? $"HCG did not accept the request (HTTP {(int)status}).", code);
        }
    }
}
