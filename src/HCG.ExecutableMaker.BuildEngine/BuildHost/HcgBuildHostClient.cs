using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.BuildEngine.BuildHost;

public enum BuildHostFailure
{
    /// <summary>No build-client token is stored (the owner creates one with <c>hcg-agent build client add executable-maker</c>).</summary>
    NotConfigured,
    /// <summary>The token was refused (unknown or revoked).</summary>
    Unauthorized,
    /// <summary>The server did not identify itself as <c>hcg.buildhost/1</c>, or speaks another major version.</summary>
    ContractMismatch,
    NotFound,
    /// <summary>The document is written when the job ends.</summary>
    NotReady,
    /// <summary>The request does not match the project or provider (bad_commit, bad_operations, …).</summary>
    Invalid,
    /// <summary>A downloaded artifact does not match the job record or the server's checksum header.</summary>
    Integrity,
    /// <summary>Provenance signature, key or fingerprint did not verify.</summary>
    SignatureInvalid,
    /// <summary>HCG Server could not be reached or failed.</summary>
    Unavailable,
    Cancelled,
}

public sealed class HcgBuildHostException(BuildHostFailure failure, string message, string? code = null, int? status = null) : Exception(message)
{
    public BuildHostFailure Failure { get; } = failure;
    /// <summary>The Build Host's error code (unknown_project, bad_commit, integrity, …), when it sent one.</summary>
    public string? Code { get; } = code;
    public int? HttpStatus { get; } = status;
}

/// <summary>Where Executable Maker finds HCG Server's Build Host. Never contains the token.</summary>
public sealed record HcgBuildHostSettings
{
    public static readonly Uri DefaultBaseUrl = new("http://127.0.0.1:7600/buildhost/v1/");

    public Uri BaseUrl { get; init; } = DefaultBaseUrl;
    /// <summary>
    /// Optional pin: the SHA-256 fingerprint of HCG Server's device key (as <c>hcg-agent</c> prints it; colons and spaces
    /// are ignored). When set, provenance signed by any other key is refused even if its signature is valid.
    /// </summary>
    public string? ExpectedKeyFingerprint { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The build-client token, kept in the operating system's credential store (<see cref="ISecureCredentialStore"/>:
/// Keychain, Credential Manager, Secret Service) and never in a plain configuration file. Where no OS store is
/// available the session-only store keeps it in memory for this run.
/// </summary>
public sealed class BuildHostCredentials(ISecureCredentialStore store)
{
    public const string Account = "buildhost-client-token";
    public string StoreDescription => store.Description;
    public bool IsPersistent => store.IsPersistent;

    public Task<string?> ReadAsync(CancellationToken ct = default) => store.ReadAsync(Account, ct);

    public Task SaveAsync(string token, CancellationToken ct = default)
    {
        token = token.Trim();
        if (token.Length is < 16 or > 512 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("That is not a Build Host client token (hcg-agent build client add executable-maker prints it once).");
        return store.WriteAsync(Account, token, ct);
    }

    public Task ForgetAsync(CancellationToken ct = default) => store.DeleteAsync(Account, ct);
}

/// <summary>A provenance document whose ES256 signature verified, with the key that signed it.</summary>
public sealed record VerifiedProvenance(SignedDocument Envelope, BuildProvenance Document, string KeyFingerprint);

/// <summary>
/// Client for HCG Server's Build Host (<c>hcg.buildhost/1</c>). Loopback only: the Build Host is served on the owner's
/// own computer and never over the network, and requests never use a proxy or follow redirects, so the build-client
/// token cannot leave this computer. The client can submit, follow, cancel and download; it has no way to approve a
/// job — only the owner does, on HCG Server (<see cref="ApprovalInstruction"/>).
/// </summary>
public sealed partial class HcgBuildHostClient
{
    private readonly HttpClient _http;
    private readonly BuildHostCredentials _credentials;
    private readonly Uri _base;

    public HcgBuildHostClient(HttpClient http, BuildHostCredentials credentials, HcgBuildHostSettings? settings = null)
    {
        Settings = settings ?? new HcgBuildHostSettings();
        _base = ValidateBaseUrl(Settings.BaseUrl);
        _http = http;
        _credentials = credentials;
    }

    public HcgBuildHostSettings Settings { get; }
    public Uri BaseUrl => _base;
    public BuildHostCredentials Credentials => _credentials;

    /// <summary>An HttpClient fit for the Build Host: no proxy (the token must not pass through one), no redirects, no cookies.</summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>What the owner runs on HCG Server to approve the job. Executable Maker never runs it.</summary>
    public static string ApprovalInstruction(string jobId) =>
        $"Waiting for approval on HCG Server (hcg-agent build approve {jobId} --confirm {jobId}).";

    /// <summary>The base URL with a trailing slash, or an exception when it is not a loopback http(s) URL.</summary>
    public static Uri ValidateBaseUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
            throw new ArgumentException($"The Build Host URL must be http(s)://127.0.0.1…, not {url}.");
        if (!IsLoopbackHost(url.Host))
            throw new ArgumentException($"The Build Host is served only on this computer's loopback interface; {url.Host} is not a loopback address. " +
                                        "Executable Maker does not send the build-client token to other hosts.");
        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new ArgumentException("The Build Host URL must not carry credentials, a query or a fragment.");
        var text = url.GetLeftPart(UriPartial.Path);
        return new Uri(text.EndsWith('/') ? text : text + "/");
    }

    public static bool IsLoopbackHost(string host)
    {
        host = host.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    // ---------------------------------------------------------------- operations

    public Task<BuildHostStatus> GetStatusAsync(CancellationToken ct = default) => GetJsonAsync<BuildHostStatus>("status", ct);

    public Task<IReadOnlyList<BuildProject>> GetProjectsAsync(CancellationToken ct = default) => GetJsonAsync<IReadOnlyList<BuildProject>>("projects", ct);

    public Task<IReadOnlyList<BuildJob>> ListJobsAsync(CancellationToken ct = default) => GetJsonAsync<IReadOnlyList<BuildJob>>("jobs", ct);

    /// <summary>Submits a job for a pinned commit. The job starts awaiting the owner's approval on HCG Server.</summary>
    public async Task<BuildJob> SubmitAsync(BuildJobRequest request, CancellationToken ct = default)
    {
        if (!Commit40().IsMatch(request.Commit))
            throw new HcgBuildHostException(BuildHostFailure.Invalid, "The Build Host builds pinned commits only: give the full 40-character commit.", "bad_commit");
        if (request.Permissions?.FirstOrDefault(p => p is BuildPermissions.Signing or BuildPermissions.Device) is { } refused)
            throw new HcgBuildHostException(BuildHostFailure.Invalid, refused == BuildPermissions.Signing
                ? "The Build Host builds unsigned; Executable Maker signs afterwards with its own approval."
                : "Physical devices are not available through the Build Host.", refused + "_unavailable");
        // The server sets Client from the token; never claim one.
        var body = request with { Client = null, Commit = request.Commit.ToLowerInvariant() };
        using var response = await SendAsync(HttpMethod.Post, "jobs", JsonContent(body), ct).ConfigureAwait(false);
        return await ReadJsonAsync<BuildJob>(response, ct).ConfigureAwait(false);
    }

    public Task<BuildJob> GetJobAsync(string jobId, CancellationToken ct = default) => GetJsonAsync<BuildJob>($"jobs/{Id(jobId)}", ct);

    public async Task<BuildJob> CancelAsync(string jobId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"jobs/{Id(jobId)}/cancel", null, ct).ConfigureAwait(false);
        return await ReadJsonAsync<BuildJob>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls until the job is succeeded, failed, cancelled or rejected. <paramref name="onUpdate"/> sees every poll's
    /// job (e.g. to show <see cref="ApprovalInstruction"/> while it awaits approval). Cancelling the token stops waiting;
    /// it does not cancel the job (call <see cref="CancelAsync"/> for that).
    /// </summary>
    public async Task<BuildJob> WaitForTerminalAsync(string jobId, Func<BuildJob, Task>? onUpdate = null, CancellationToken ct = default)
    {
        while (true)
        {
            var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
            if (onUpdate is not null) await onUpdate(job).ConfigureAwait(false);
            if (job.IsTerminal) return job;
            try { await Task.Delay(Settings.PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new HcgBuildHostException(BuildHostFailure.Cancelled, "Stopped waiting for the Build Host job."); }
        }
    }

    /// <summary>Redacted log bytes from <paramref name="offset"/>, and the offset to ask for next time.</summary>
    public async Task<(byte[] Bytes, long NextOffset)> GetLogsAsync(string jobId, long offset, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"jobs/{Id(jobId)}/logs?offset={Math.Max(0, offset)}", null, ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var next = response.Headers.TryGetValues(BuildHostContract.NextOffsetHeader, out var v) && long.TryParse(v.FirstOrDefault(), out var n) ? n : offset + bytes.Length;
        return (bytes, next);
    }

    /// <summary>
    /// Downloads one artifact of <paramref name="job"/> to <paramref name="destination"/> and verifies it three ways: the
    /// server's <c>X-HCG-SHA256</c> header and <c>Content-Length</c> must equal the job record, and the bytes received
    /// must hash to that SHA-256 and size. On any mismatch the file is deleted and <see cref="BuildHostFailure.Integrity"/>
    /// is thrown.
    /// </summary>
    public async Task<BuildArtifact> DownloadArtifactAsync(BuildJob job, string artifactName, string destination, CancellationToken ct = default)
    {
        var record = job.Artifacts.FirstOrDefault(a => a.Name == artifactName)
                     ?? throw new HcgBuildHostException(BuildHostFailure.NotFound, $"Job {job.Id} has no artifact named {artifactName}.", "not_found");
        if (!ArtifactName().IsMatch(record.Name) || record.Name is "." or "..")
            throw new HcgBuildHostException(BuildHostFailure.Integrity, $"The job names an artifact with an unsafe file name ({record.Name}).", "integrity");
        if (!Sha256Hex().IsMatch(record.Sha256) || record.Size < 0)
            throw new HcgBuildHostException(BuildHostFailure.Integrity, $"The job record of {record.Name} has no valid SHA-256 and size.", "integrity");

        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"jobs/{Id(job.Id)}/artifacts/{Uri.EscapeDataString(record.Name)}", null, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var header = response.Headers.TryGetValues(BuildHostContract.Sha256Header, out var hv) ? hv.FirstOrDefault() : null;
            if (!string.Equals(header, record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Integrity(record, $"the server's {BuildHostContract.Sha256Header} header ({header ?? "missing"}) differs from the job record ({record.Sha256})");
            if (response.Content.Headers.ContentLength is { } length && length != record.Size)
                throw Integrity(record, $"Content-Length {length} differs from the recorded size {record.Size}");

            long received = 0;
            string actual;
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > record.Size) throw Integrity(record, $"the server sent more than the recorded {record.Size} bytes");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (received != record.Size) throw Integrity(record, $"received {received} bytes, the job records {record.Size}");
            if (!string.Equals(actual, record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Integrity(record, $"the bytes received hash to {actual}, the job records {record.Sha256}");
            File.Move(partial, destination, overwrite: true);
            return record;
        }
        catch
        {
            // Nothing unverified is kept: the bytes only ever reach the destination after every check passed.
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Fetches the signed provenance and verifies it (<see cref="VerifyProvenance"/>) for exactly this job.</summary>
    public async Task<VerifiedProvenance> GetProvenanceAsync(string jobId, CancellationToken ct = default)
    {
        var envelope = await GetJsonAsync<SignedDocument>($"jobs/{Id(jobId)}/provenance", ct).ConfigureAwait(false);
        var verified = VerifyProvenance(envelope, Settings.ExpectedKeyFingerprint);
        if (verified.Document.Job.Id != jobId)
            throw new HcgBuildHostException(BuildHostFailure.SignatureInvalid, $"The provenance is signed for job {verified.Document.Job.Id}, not {jobId}.");
        return verified;
    }

    public Task<BuildValidation> GetValidationAsync(string jobId, CancellationToken ct = default) => GetJsonAsync<BuildValidation>($"jobs/{Id(jobId)}/validation", ct);

    /// <summary>Asks the Build Host to re-hash its stored artifacts.</summary>
    public async Task<IReadOnlyList<BuildValidationCheck>> VerifyStoredAsync(string jobId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"jobs/{Id(jobId)}/verify", null, ct).ConfigureAwait(false);
        return await ReadJsonAsync<IReadOnlyList<BuildValidationCheck>>(response, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- signature

    /// <summary>
    /// Verifies a Build Host <see cref="SignedDocument"/>: algorithm ES256-ASN1, a P-256 public key (PKIX DER), the key
    /// fingerprint (lowercase hex SHA-256 of the PKIX DER, as HCG Server's <c>identity.PublicKeyFingerprint</c> computes it)
    /// equal to the envelope's and to <paramref name="expectedFingerprint"/> when pinned, and the ASN.1 ECDSA signature over
    /// SHA-256 of the exact payload bytes. Only then is the payload parsed.
    /// </summary>
    public static VerifiedProvenance VerifyProvenance(SignedDocument envelope, string? expectedFingerprint = null)
    {
        static HcgBuildHostException Bad(string why) => new(BuildHostFailure.SignatureInvalid, "The Build Host provenance does not verify: " + why + ".");
        if (envelope.Algorithm != BuildHostContract.SignatureAlgorithm) throw Bad($"algorithm {envelope.Algorithm}, expected {BuildHostContract.SignatureAlgorithm}");
        if (envelope.PayloadType != BuildHostContract.ProvenancePayloadType) throw Bad($"payload type {envelope.PayloadType}");
        if (envelope.Payload.Length == 0) throw Bad("empty payload");
        byte[] der, signature;
        try { der = Convert.FromBase64String(envelope.PublicKey); signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { throw Bad("the public key or signature is not base64"); }

        using var key = ECDsa.Create();
        try { key.ImportSubjectPublicKeyInfo(der, out var used); if (used != der.Length) throw Bad("trailing bytes after the public key"); }
        catch (CryptographicException) { throw Bad("the public key is not a PKIX ECDSA key"); }
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid?.Value != "1.2.840.10045.3.1.7") throw Bad("the key is not P-256");

        // identity.PublicKeyFingerprint: hex(SHA-256(x509.MarshalPKIXPublicKey(pub))). The canonical re-encoding must be the DER given.
        var canonical = key.ExportSubjectPublicKeyInfo();
        if (!canonical.AsSpan().SequenceEqual(der)) throw Bad("the public key is not in canonical PKIX form");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(canonical));
        if (!string.Equals(fingerprint, NormalizeFingerprint(envelope.KeyFingerprint), StringComparison.Ordinal))
            throw Bad($"the envelope names key {envelope.KeyFingerprint}, the key is {fingerprint}");
        if (expectedFingerprint is { Length: > 0 } pin && !string.Equals(fingerprint, NormalizeFingerprint(pin), StringComparison.Ordinal))
            throw Bad($"signed by key {fingerprint}, not the pinned HCG Server key {NormalizeFingerprint(pin)}");
        if (!key.VerifyData(envelope.Payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw Bad("the signature does not match the payload");

        BuildProvenance document;
        try { document = JsonSerializer.Deserialize<BuildProvenance>(envelope.Payload, BuildHostContract.Json) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { throw Bad("the signed payload is not provenance JSON (" + ex.Message + ")"); }
        if (document.Format != BuildHostContract.ProvenanceFormat) throw Bad($"format {document.Format}, expected {BuildHostContract.ProvenanceFormat}");
        // The payload's own copy of the key fingerprint, when HCG Server fills it in, must name the same key.
        if (document.BuildHost.KeyFingerprint is { Length: > 0 } inner && NormalizeFingerprint(inner) != fingerprint)
            throw Bad($"the payload names key {inner}, the signature is by {fingerprint}");
        return new VerifiedProvenance(envelope, document, fingerprint);
    }

    /// <summary>Lowercase hex without separators (hcg-agent prints groups of four).</summary>
    public static string NormalizeFingerprint(string fingerprint) =>
        new string(fingerprint.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    // ---------------------------------------------------------------- HTTP

    private async Task<T> GetJsonAsync<T>(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, relative, null, ct).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body, BuildHostContract.Json), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, HttpContent? content, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var token = await _credentials.ReadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new HcgBuildHostException(BuildHostFailure.NotConfigured,
                "No Build Host client token is stored. The owner creates one on HCG Server (hcg-agent build client add executable-maker); store it in Executable Maker.");
        var url = new Uri(_base, relative);
        if (!IsLoopbackHost(url.Host)) throw new InvalidOperationException("Refusing a non-loopback Build Host URL.");
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, completion, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new HcgBuildHostException(BuildHostFailure.Cancelled, "Cancelled."); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new HcgBuildHostException(BuildHostFailure.Unavailable, $"HCG Server's Build Host could not be reached at {_base} ({ex.GetType().Name}). Is hcg-agent running?");
        }

        try
        {
            CheckContract(response);
            if (response.IsSuccessStatusCode) return response;
            throw await MapErrorAsync(response, ct).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Every authenticated response carries <c>X-HCG-Contract: hcg.buildhost/1</c>. A missing header on a success, a
    /// different contract or another major version is refused. (HCG Server answers a refused token before it sets the
    /// header, so a 401 without it is reported as an authorization failure.)
    /// </summary>
    private static void CheckContract(HttpResponseMessage response)
    {
        var header = response.Headers.TryGetValues(BuildHostContract.ContractHeader, out var values) ? values.FirstOrDefault() : null;
        if (header is null)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.MisdirectedRequest) return;
            throw new HcgBuildHostException(BuildHostFailure.ContractMismatch,
                $"The server did not identify itself as an HCG Build Host ({BuildHostContract.ContractHeader} missing, HTTP {(int)response.StatusCode}).", status: (int)response.StatusCode);
        }
        var m = ContractValue().Match(header.Trim());
        if (!m.Success || m.Groups["name"].Value != BuildHostContract.Name)
            throw new HcgBuildHostException(BuildHostFailure.ContractMismatch, $"The server speaks {header}, not {BuildHostContract.Contract}.", status: (int)response.StatusCode);
        if (!int.TryParse(m.Groups["major"].Value, out var major) || major != BuildHostContract.MajorVersion)
            throw new HcgBuildHostException(BuildHostFailure.ContractMismatch,
                $"The Build Host speaks {header}; this version of Executable Maker supports {BuildHostContract.Contract} only. Update Executable Maker or HCG Server.", status: (int)response.StatusCode);
    }

    private static async Task<HcgBuildHostException> MapErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? code = null, message = null;
        try
        {
            var e = JsonSerializer.Deserialize<BuildHostError>(text, BuildHostContract.Json);
            code = e?.Code;
            message = e?.Error;
        }
        catch (JsonException) { }
        message = SecretRedactor.Redact(message is { Length: > 300 } ? message[..300] : message ?? $"HTTP {(int)response.StatusCode}");
        var status = (int)response.StatusCode;
        var failure = (status, code) switch
        {
            (401, _) or (403, _) => BuildHostFailure.Unauthorized,
            (_, "not_found") or (404, _) => BuildHostFailure.NotFound,
            (_, "not_ready") => BuildHostFailure.NotReady,
            (_, "integrity") => BuildHostFailure.Integrity,
            (>= 400 and < 500, _) => BuildHostFailure.Invalid,
            _ => BuildHostFailure.Unavailable,
        };
        var prefix = failure == BuildHostFailure.Unauthorized ? "The Build Host refused Executable Maker's client token: " : "Build Host: ";
        return new HcgBuildHostException(failure, prefix + message + (code is null ? "" : $" ({code})"), code, status);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, BuildHostContract.Json, ct).ConfigureAwait(false) ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new HcgBuildHostException(BuildHostFailure.ContractMismatch, $"The Build Host returned a response this client cannot read ({ex.Message}).");
        }
    }

    private static string Id(string jobId) => JobId().IsMatch(jobId)
        ? jobId
        : throw new HcgBuildHostException(BuildHostFailure.Invalid, $"{jobId} is not a Build Host job id.");

    private static HcgBuildHostException Integrity(BuildArtifact a, string why) =>
        new(BuildHostFailure.Integrity, $"{a.Name} was not accepted: {why}. The download was deleted.", "integrity");

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [GeneratedRegex("^(?<name>[a-z][a-z0-9.-]*)/(?<major>[0-9]+)(\\.[0-9]+)*$")] private static partial Regex ContractValue();
    [GeneratedRegex("^[0-9a-fA-F]{40}$")] private static partial Regex Commit40();
    [GeneratedRegex("^[0-9a-fA-F]{64}$")] private static partial Regex Sha256Hex();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")] private static partial Regex JobId();
    [GeneratedRegex("^[A-Za-z0-9._+-]{1,255}$")] private static partial Regex ArtifactName();
}
