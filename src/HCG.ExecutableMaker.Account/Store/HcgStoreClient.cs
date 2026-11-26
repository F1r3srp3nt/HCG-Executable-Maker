using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCG.ExecutableMaker.Account.Store;

/// <summary>
/// Ship to Store: the client for HCG Store's publishing API (store.hakmicapitalgroup.com, server/src/publishing.ts).
/// Every call carries the person's HCG Connect access token (from <see cref="HcgAccountService"/>); HCG Store checks it
/// and asks HCG whether the person may publish. The desktop app holds no Store or storage credentials: files go to
/// short-lived upload URLs the Store issues for one file. Nothing here approves, publishes, or submits to TestFlight
/// or any external store — approval is a manual step in HCG Store.
/// </summary>
public sealed class HcgStoreClient(HttpClient http, Uri storeOrigin, Func<CancellationToken, Task<string?>> accessToken, Func<string, CancellationToken, Task<bool>>? recoverUnauthorized = null)
{
    public static readonly Uri DefaultOrigin = new("https://store.hakmicapitalgroup.com/");
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public Uri Origin { get; } = storeOrigin;
    /// <summary>Delays between upload retries of one chunk (bounded; never unlimited).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    // ───────────── API ─────────────

    /// <summary>
    /// The server's own public health answer (<c>GET /api/v1/health</c>, no sign-in): mode, sign-in, payments and protected
    /// downloads as the deployed server states them. Null when the address answers but is not an HCG Store server.
    /// </summary>
    public async Task<StoreHealth?> HealthAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync(new Uri(Origin, "api/v1/health"), ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || !(response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false)) return null;
        try { return JsonSerializer.Deserialize<StoreHealth>(text, Json); }
        catch (JsonException) { return null; }
    }

    public Task<StoreWhoAmI> WhoAmIAsync(CancellationToken ct = default) => SendAsync<StoreWhoAmI>(HttpMethod.Get, "api/v1/publishing/whoami", null, ct);
    public async Task<IReadOnlyList<StoreProduct>> ProductsAsync(CancellationToken ct = default) => (await SendAsync<StoreProductList>(HttpMethod.Get, "api/v1/publishing/products", null, ct).ConfigureAwait(false)).Products;
    public Task<StoreProduct> CreateDraftProductAsync(string id, string title, CancellationToken ct = default) => SendAsync<StoreProduct>(HttpMethod.Post, "api/v1/publishing/products", new { id, title }, ct);
    public Task<StoreRelease> CreateReleaseAsync(StoreReleaseRequest request, CancellationToken ct = default) => SendAsync<StoreRelease>(HttpMethod.Post, "api/v1/publishing/releases", request, ct);
    public Task<StoreRelease> ReleaseAsync(string releaseId, CancellationToken ct = default) => SendAsync<StoreRelease>(HttpMethod.Get, $"api/v1/publishing/releases/{Uri.EscapeDataString(releaseId)}", null, ct);
    public Task<StoreDeclaredArtifact> DeclareArtifactAsync(string releaseId, StoreArtifactRequest request, CancellationToken ct = default) => SendAsync<StoreDeclaredArtifact>(HttpMethod.Post, $"api/v1/publishing/releases/{Uri.EscapeDataString(releaseId)}/artifacts", request, ct);
    public Task<StoreUploadUrl> RenewUploadUrlAsync(string artifactId, CancellationToken ct = default) => SendAsync<StoreUploadUrl>(HttpMethod.Post, $"api/v1/publishing/artifacts/{Uri.EscapeDataString(artifactId)}/upload-url", new { }, ct);
    public Task<StoreArtifactResult> CompleteAsync(string artifactId, CancellationToken ct = default) => SendAsync<StoreArtifactResult>(HttpMethod.Post, $"api/v1/publishing/artifacts/{Uri.EscapeDataString(artifactId)}/complete", new { }, ct, accept422: true);
    public Task<StoreRelease> RemoveArtifactAsync(string artifactId, CancellationToken ct = default) => SendAsync<StoreRelease>(HttpMethod.Delete, $"api/v1/publishing/artifacts/{Uri.EscapeDataString(artifactId)}", null, ct);
    public Task<StoreRelease> SubmitAsync(string releaseId, CancellationToken ct = default) => SendAsync<StoreRelease>(HttpMethod.Post, $"api/v1/publishing/releases/{Uri.EscapeDataString(releaseId)}/submit", new { }, ct);
    public Task<StoreRelease> WithdrawAsync(string releaseId, string reason, CancellationToken ct = default) => SendAsync<StoreRelease>(HttpMethod.Post, $"api/v1/publishing/releases/{Uri.EscapeDataString(releaseId)}/withdraw", new { reason }, ct);
    public Task<StoreDownloadUrl> DownloadUrlAsync(string artifactId, CancellationToken ct = default) => SendAsync<StoreDownloadUrl>(HttpMethod.Post, $"api/v1/publishing/artifacts/{Uri.EscapeDataString(artifactId)}/download-url", new { }, ct);

    /// <summary>
    /// Uploads <paramref name="file"/> in chunks to the Store-issued URL. Resumes at the byte the Store has (after a
    /// network error, a refused chunk or an expired URL), retrying each chunk a bounded number of times. Progress
    /// reports bytes the Store has confirmed, never bytes merely sent.
    /// </summary>
    public async Task UploadAsync(string artifactId, string file, StoreUploadUrl url, IProgress<StoreUploadProgress>? progress, CancellationToken ct)
    {
        var size = new FileInfo(file).Length;
        var current = url;
        var received = await ReceivedAsync(current, ct).ConfigureAwait(false) ?? 0;
        progress?.Report(new(received, size, 0, null));
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var buffer = new byte[current.ChunkBytes];
        var failures = 0;
        while (received < size)
        {
            ct.ThrowIfCancellationRequested();
            var length = (int)Math.Min(current.ChunkBytes, size - received);
            stream.Position = received;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            string? problem;
            try
            {
                using var content = new ByteArrayContent(buffer, 0, length);
                content.Headers.ContentRange = new ContentRangeHeaderValue(received, received + length - 1, size);
                using var response = await http.PutAsync(current.UploadUrl, content, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    received = (await response.Content.ReadFromJsonAsync<StoreChunkResult>(Json, ct).ConfigureAwait(false))!.Received;
                    failures = 0;
                    progress?.Report(new(received, size, 0, null));
                    continue;
                }
                var error = await StoreError.ReadAsync(response, ct).ConfigureAwait(false);
                problem = $"{(int)response.StatusCode} {error.Message}";
                if (response.StatusCode == HttpStatusCode.Forbidden && error.Code == "upload_url_expired")
                {
                    current = await RenewUploadUrlAsync(artifactId, ct).ConfigureAwait(false);
                    received = current.Received ?? received;
                    progress?.Report(new(received, size, failures, "The upload URL expired; a new one was issued and the upload resumes."));
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    && error.Code is not ("short_chunk" or "upload_interrupted" or "offset_mismatch" or "upload_in_progress"))
                    throw new HcgStoreException(error.Code, $"The Store refused the upload: {error.Message}", (int)response.StatusCode);
            }
            catch (HttpRequestException ex) { problem = ex.Message; }
            catch (IOException ex) { problem = ex.Message; }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { problem = "timed out: " + ex.Message; }
            failures++;
            if (failures > RetryDelays.Count)
                throw new HcgStoreException("upload_failed", $"The upload stopped after {failures} failed attempts at byte {received:N0} of {size:N0}: {problem}. The Store keeps the bytes it confirmed; Retry resumes there.", 0);
            progress?.Report(new(received, size, failures, $"Attempt {failures} failed ({problem}); retrying in {RetryDelays[failures - 1].TotalSeconds:0} s."));
            await Task.Delay(RetryDelays[failures - 1], ct).ConfigureAwait(false);
            received = await ReceivedAsync(current, ct).ConfigureAwait(false) ?? received;
        }
    }

    private async Task<long?> ReceivedAsync(StoreUploadUrl url, CancellationToken ct)
    {
        try
        {
            using var r = await http.GetAsync(url.UploadUrl, ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode ? (await r.Content.ReadFromJsonAsync<StoreChunkResult>(Json, ct).ConfigureAwait(false))?.Received : null;
        }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Downloads an artifact through a short-lived URL and compares its SHA-256 with the expected value.</summary>
    public async Task<(bool Matches, string Sha256, long Bytes)> DownloadAndVerifyAsync(string artifactId, string expectedSha256, string? saveTo, CancellationToken ct)
    {
        var link = await DownloadUrlAsync(artifactId, ct).ConfigureAwait(false);
        using var response = await http.GetAsync(link.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var e = await StoreError.ReadAsync(response, ct).ConfigureAwait(false);
            throw new HcgStoreException(e.Code, e.Message, (int)response.StatusCode);
        }
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var target = saveTo is null ? null : new FileStream(saveTo, FileMode.Create, FileAccess.Write);
        var buffer = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            if (target is not null) await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            total += n;
        }
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        return (string.Equals(actual, expectedSha256, StringComparison.Ordinal), actual, total);
    }

    // ───────────── HTTP ─────────────

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, bool accept422 = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await accessToken(ct).ConfigureAwait(false)
                        ?? throw new HcgStoreException("sign_in_required", "Sign in with HCG Connect (Account page) to ship to HCG Store.", 401);
            using var request = new HttpRequestMessage(method, new Uri(Origin, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex)
            {
                var (code, what) = ex.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => ("dns_failure", $"the name {Origin.Host} does not resolve (DNS)"),
                    HttpRequestError.SecureConnectionError => ("tls_failure", $"a secure (HTTPS) connection to {Origin.Host} failed; the certificate may be missing or invalid"),
                    HttpRequestError.ConnectionError => ("connection_failed", $"no connection to {Origin.Host} could be made (refused, or no network)"),
                    HttpRequestError.ProxyTunnelError => ("proxy_failure", $"the proxy refused the connection to {Origin.Host}"),
                    _ => ("store_unreachable", $"{Origin.Host} could not be reached"),
                };
                throw new HcgStoreException(code, $"HCG Store could not be reached: {what} ({ex.Message}).", 0);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient's timeout, not the person cancelling: the Store did not answer. Not a refusal.
                throw new HcgStoreException("store_timeout", $"HCG Store ({Origin.Host}) did not answer in time.", 0);
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && recoverUnauthorized is not null && await recoverUnauthorized(token, ct).ConfigureAwait(false)) continue;
                if (response.IsSuccessStatusCode || (accept422 && (int)response.StatusCode == 422))
                {
                    var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    try { return JsonSerializer.Deserialize<T>(text, Json) ?? throw new JsonException("empty"); }
                    catch (JsonException) { throw new HcgStoreException("bad_response", $"HCG Store answered {(int)response.StatusCode} with an unexpected body.", (int)response.StatusCode); }
                }
                var e = await StoreError.ReadAsync(response, ct).ConfigureAwait(false);
                throw new HcgStoreException(e.Code, e.Message, (int)response.StatusCode);
            }
        }
    }
}

/// <summary>Why HCG Store publishing cannot be used right now, precisely (never a vague "not available").</summary>
public enum StoreProblem
{
    /// <summary>Sign in again: HCG Store did not accept the HCG session.</summary>
    SignInRejected,
    /// <summary>HCG has not given this account the publishing permission.</summary>
    NoPermission,
    /// <summary>The HCG Store server runs there, but its publishing feature is switched off.</summary>
    PublishingDisabled,
    /// <summary>A web page answered (a static site, e.g. the storefront on GitHub Pages): no HCG Store server at that address.</summary>
    NotStoreServer,
    /// <summary>A JSON service answered, but it has no publishing API v1 (older Store, or another service).</summary>
    EndpointMissing,
    /// <summary>The Store failed (HTTP 5xx).</summary>
    ServerError,
    DnsFailure,
    TlsFailure,
    ConnectionFailed,
    Timeout,
    /// <summary>An answer Maker cannot read (unexpected body or status).</summary>
    UnexpectedResponse,
}

public static class StoreDiagnosis
{
    public static string Label(StoreProblem p) => p switch
    {
        StoreProblem.SignInRejected => "Sign-in not accepted",
        StoreProblem.NoPermission => "No publishing permission",
        StoreProblem.PublishingDisabled => "Publishing switched off",
        StoreProblem.NotStoreServer => "No HCG Store server at this address",
        StoreProblem.EndpointMissing => "Publishing API not found",
        StoreProblem.ServerError => "HCG Store server error",
        StoreProblem.DnsFailure => "Store address not found (DNS)",
        StoreProblem.TlsFailure => "Secure connection failed",
        StoreProblem.ConnectionFailed => "HCG Store unreachable",
        StoreProblem.Timeout => "HCG Store did not answer",
        _ => "Unexpected answer from HCG Store",
    };

    /// <summary>The precise problem, a short label, and an explanation with what to do. Never a refusal for an outage.</summary>
    public static (StoreProblem Problem, string Label, string Explanation) Classify(HcgStoreException ex, Uri origin)
    {
        var (problem, explanation) = Explain(ex, origin.Host);
        return (problem, Label(problem), explanation);
    }

    private static (StoreProblem, string) Explain(HcgStoreException ex, string host) => (ex.Status, ex.Code) switch
    {
        (401, _) or (_, "sign_in_required") => (StoreProblem.SignInRejected,
            $"HCG Store at {host} did not accept your HCG session. Sign in with HCG again on the Account page."),
        (403, _) => (StoreProblem.NoPermission,
            $"{ex.Message} Publishing is a staff permission granted by HCG; buying a product does not grant it."),
        (_, "publishing_disabled") => (StoreProblem.PublishingDisabled,
            $"The HCG Store server at {host} is running, but publishing is switched off there (HCG_STORE_PUBLISHING). An HCG Store administrator enables it; nothing on this Mac needs fixing."),
        (_, "not_store_api") => (StoreProblem.NotStoreServer,
            $"{host} answered {ex.Message}. That address serves a website (the static storefront), not the HCG Store server that handles sign-in and publishing. The HCG Store server must be deployed at this address, or Settings must point to where it runs."),
        (404, _) or (405, _) => (StoreProblem.EndpointMissing,
            $"{host} has no HCG Store publishing API v1 (HTTP {ex.Status}: {ex.Message}). It may run an older HCG Store, or another service answers at this address."),
        ( >= 500, _) => (StoreProblem.ServerError,
            $"HCG Store at {host} failed (HTTP {ex.Status}: {ex.Message}). This is not a refusal; try again later."),
        (_, "dns_failure") => (StoreProblem.DnsFailure, ex.Message + " Check the HCG Store address in Settings."),
        (_, "tls_failure") => (StoreProblem.TlsFailure, ex.Message),
        (_, "connection_failed" or "proxy_failure" or "store_unreachable") => (StoreProblem.ConnectionFailed, ex.Message + " This is not a refusal."),
        (_, "store_timeout") => (StoreProblem.Timeout, ex.Message + " This is not a refusal."),
        _ => (StoreProblem.UnexpectedResponse, $"HCG Store at {host} answered HTTP {ex.Status} ({ex.Code}): {ex.Message}"),
    };
}

public sealed class HcgStoreException(string code, string message, int status) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

internal sealed record StoreError(string Code, string Message)
{
    public static async Task<StoreError> ReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            // HCG Store answers {"error":{"code","message"}} (and {"error":"code","message"} for upload states).
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object) root = e;
            string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var code = Str(root, "code") is { Length: > 0 } c ? c : Str(doc.RootElement, "error");
            var message = Str(root, "message") is { Length: > 0 } m ? m : Str(doc.RootElement, "message");
            return new(code, message.Length > 0 ? message : $"HTTP {(int)r.StatusCode}");
        }
        catch (JsonException)
        {
            var type = r.Content.Headers.ContentType?.MediaType ?? "no content type";
            var server = r.Headers.Server.ToString() is { Length: > 0 } s ? $", server {s}" : "";
            return type.Contains("html", StringComparison.OrdinalIgnoreCase)
                ? new("not_store_api", $"HTTP {(int)r.StatusCode} with a web page ({type}{server}), not HCG Store's API")
                : new("http_" + (int)r.StatusCode, $"HCG Store answered HTTP {(int)r.StatusCode} ({type}{server}).");
        }
    }
}

public sealed record StoreUploadProgress(long Received, long Total, int Failures, string? Note)
{
    public double Fraction => Total == 0 ? 1 : (double)Received / Total;
}

public sealed record StoreWhoAmI(string Sub, string? Email, string Via, string? PublisherPermission, string? ApproverPermission, long MaxUploadBytes, int ChunkBytes);
public sealed record StoreProductList(IReadOnlyList<StoreProduct> Products);
public sealed record StoreProduct(string Id, string Title, string Source)
{
    public IReadOnlyList<StoreEdition> Editions { get; init; } = [];
    public string? CentralId { get; init; }
    public IReadOnlyList<string> HcgRepo { get; init; } = [];
    public string? Note { get; init; }
}
public sealed record StoreEdition(string Id, string Name, IReadOnlyList<string> Platforms);

/// <summary>HCG Store's public health answer, as the deployed server states it (fields it does not send stay null).</summary>
public sealed record StoreHealth(string? Status, string? Mode, string? HcgConnect, string? EntitlementAuthority, string? Payments, string? ProtectedDownloads, string? Publishing);

public sealed record StoreReleaseRequest
{
    public required string ProductId { get; init; }
    public string? EditionId { get; init; }
    public required string Version { get; init; }
    public required string Build { get; init; }
    public string Channel { get; init; } = "beta";
    public IReadOnlyList<string> ReleaseNotes { get; init; } = [];
    public StoreCompatibility Compatibility { get; init; } = new();
    /// <summary>free · paid · subscription · entitlement (prices and licences stay with HCG).</summary>
    public required string Distribution { get; init; }
    /// <summary>private · internal-testing · public (what the publisher asks for; an approver decides).</summary>
    public required string Eligibility { get; init; }
    public IReadOnlyList<string> Destinations { get; init; } = ["hcg-store"];
}

public sealed record StoreCompatibility
{
    public IReadOnlyDictionary<string, string> MinimumOs { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Architectures { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record StoreArtifactRequest
{
    public required string FileName { get; init; }
    public required string Platform { get; init; }
    public required string Format { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public IReadOnlyList<string> Architectures { get; init; } = [];
    public string? MinimumOs { get; init; }
    public StoreSigningReport Signing { get; init; } = new("not-checked", null, null);
    public StorePackage? Package { get; init; }
    /// <summary>installer · portable-app · device-build · simulator-build · jailbreak-package. HCG Store decides the kind
    /// from the file's content and refuses a declaration that contradicts it.</summary>
    public string? Kind { get; init; }
    /// <summary>Build provenance, as REPORTED by Maker (HCG Store v2 needs it on every file before a release is submitted).
    /// Sent only when known, from a release passport that matches the file's exact bytes; never guessed. Null fields are
    /// omitted, so HCG Store servers without these fields keep accepting the request.</summary>
    public string? SourceCommit { get; init; }
    /// <summary>local · github-actions · hcg-server-build-host (HCG Store's taxonomy).</summary>
    public string? BuildProvider { get; init; }
    public string? Toolchain { get; init; }
    public StoreValidationReport? ValidationReport { get; init; }
}

/// <summary>The uploading tool's own validation, recorded by HCG Store as reported (HCG Store adds its own checks).</summary>
public sealed record StoreValidationReport(string Tool, bool Passed, string Summary);

/// <summary>How a file installs, as HCG Store (or this computer, before upload) judges it. <see cref="OrdinaryInstall"/> is
/// true only when it installs on an ordinary device of its platform as it is.</summary>
public sealed record StoreReadiness(string Status, string Label, bool OrdinaryInstall);

public sealed record StoreSigningReport(string Status, string? Evidence, string? Tool);
public sealed record StorePackage(string Id, string Version, string Architecture);

public sealed record StoreRelease
{
    public required string Id { get; init; }
    public required string ProductId { get; init; }
    public string? ProductTitle { get; init; }
    public string? ProductSource { get; init; }
    public string? EditionId { get; init; }
    public required string Version { get; init; }
    public required string Build { get; init; }
    public string? Channel { get; init; }
    public IReadOnlyList<string> ReleaseNotes { get; init; } = [];
    public string? Distribution { get; init; }
    public string? RequestedEligibility { get; init; }
    public string? ApprovedEligibility { get; init; }
    public IReadOnlyList<string> Destinations { get; init; } = [];
    public required string Status { get; init; }
    public string? ReviewUrl { get; init; }
    public string? WithdrawnReason { get; init; }
    public IReadOnlyList<StoreArtifact> Artifacts { get; init; } = [];
    public bool? AuditIntact { get; init; }
    /// <summary>True when HCG Store returned an existing draft for the same request (a retry) instead of creating one.</summary>
    public bool? Reused { get; init; }
}

public sealed record StoreArtifact
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string Platform { get; init; }
    public required string Format { get; init; }
    public long SizeBytes { get; init; }
    public string? DeclaredSha256 { get; init; }
    public string? Sha256 { get; init; }
    public required string Status { get; init; }
    public long ReceivedBytes { get; init; }
    public JsonElement? Validation { get; init; }
    public string? Kind { get; init; }
    public bool KindCheckedByStore { get; init; }
    public StoreReadiness? Readiness { get; init; }
}

public sealed record StoreUploadUrl(string UploadUrl, long ExpiresAt, int ChunkBytes)
{
    public long? Received { get; init; }
}
public sealed record StoreDeclaredArtifact(StoreArtifact Artifact, string UploadUrl, long ExpiresAt, int ChunkBytes)
{
    public StoreUploadUrl Upload => new(UploadUrl, ExpiresAt, ChunkBytes);
}
public sealed record StoreArtifactResult(StoreArtifact Artifact)
{
    public string? Error { get; init; }
    public string? Message { get; init; }
}
public sealed record StoreDownloadUrl(string Url, long ExpiresAt, string? Sha256, string? Basis);
internal sealed record StoreChunkResult(long Received, long Size);

public static class StoreOrigin
{
    /// <summary>An https origin, or http only for this computer (local test servers); anything else is refused.</summary>
    public static Uri? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u)) return null;
        var local = u.Host is "127.0.0.1" or "localhost" or "::1";
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && local)) return null;
        return new Uri(u.GetLeftPart(UriPartial.Authority) + "/");
    }
}
