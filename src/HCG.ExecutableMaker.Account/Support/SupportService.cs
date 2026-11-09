using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace HCG.ExecutableMaker.Account.Support;

/// <summary>One file to upload with a request (the project archive or a single file the user picked).</summary>
public sealed record UploadFile(string Path, string Name, string ContentType)
{
    public AttachmentManifest Manifest()
    {
        var info = new FileInfo(Path);
        using var s = File.OpenRead(Path);
        return new AttachmentManifest(Name, info.Length, Convert.ToHexStringLower(SHA256.HashData(s)), ContentType);
    }
}

public sealed record UploadProgress(string File, long SentBytes, long TotalBytes, int FileIndex, int FileCount);

public enum SubmitOutcome
{
    /// <summary>Submitted: HCG received the request and every attachment.</summary>
    Submitted,
    /// <summary>The request exists on HCG as a draft, but an upload did not finish. Retry resumes it (same request).</summary>
    UploadIncomplete,
}

public sealed record SubmitResult(SubmitOutcome Outcome, SupportRequestSummary Request, string Message, IReadOnlyList<SupportAttachmentInfo> Attachments);

/// <summary>
/// Support requests on HCG (<c>{api}/support/…</c>). Nothing is sent until the user presses Submit; importing or building
/// a project never calls this. The server checks ownership and permissions on every call (support.submit /
/// support.attach), stores attachments privately and validates them again after upload.
/// </summary>
public sealed class SupportService(HcgApiClient api, HttpClient uploadHttp)
{
    public Task<SupportRequestList> ListAsync(CancellationToken ct = default) => api.GetAsync<SupportRequestList>("support/requests", ct);

    public async Task<SupportRequestDetail> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<SupportRequestEnvelope>($"support/requests/{id}", ct).ConfigureAwait(false)).Request;

    public async Task<SupportRequestDetail> ReplyAsync(Guid id, string body, CancellationToken ct = default) =>
        (await api.PostAsync<SupportRequestEnvelope>($"support/requests/{id}/messages", new { body = body.Trim() }, ct).ConfigureAwait(false)).Request;

    /// <summary>Withdraws the request; its attachments are deleted by HCG (retention policy: immediately on withdrawal).</summary>
    public async Task<SupportRequestDetail> WithdrawAsync(Guid id, CancellationToken ct = default) =>
        (await api.PostAsync<SupportRequestEnvelope>($"support/requests/{id}/withdraw", new { }, ct).ConfigureAwait(false)).Request;

    public Task<DownloadLink> DownloadLinkAsync(Guid id, Guid attachmentId, CancellationToken ct = default) =>
        api.PostAsync<DownloadLink>($"support/requests/{id}/attachments/{attachmentId}/download", new { }, ct);

    /// <summary>
    /// Creates (or, for the same <see cref="CreateSupportRequest.ClientRequestId"/>, resumes) the request, uploads each
    /// attachment with progress, asks HCG to validate it, then submits. Cancellation or a failed upload leaves a draft on
    /// HCG that the same call resumes; nothing is submitted twice.
    /// </summary>
    public async Task<SubmitResult> SubmitAsync(CreateSupportRequest request, IReadOnlyList<UploadFile> files, IProgress<UploadProgress>? progress, CancellationToken ct)
    {
        if (request.Mode == SubmissionMode.DiagnosticsOnly && files.Count > 0)
            throw new ArgumentException("Diagnostics-only requests carry no files.", nameof(files));
        var created = await api.PostAsync<CreateSupportResponse>("support/requests", request, ct).ConfigureAwait(false);
        var byName = files.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var attachments = new List<SupportAttachmentInfo>();
        var index = 0;
        foreach (var slot in created.Uploads)
        {
            index++;
            if (!byName.TryGetValue(slot.Name, out var file))
                throw new InvalidOperationException($"HCG asked for an attachment that was not selected ({slot.Name}).");
            try
            {
                await UploadWithRenewalAsync(created.Request.Id, slot, file, new Progress<long>(sent =>
                    progress?.Report(new UploadProgress(file.Name, sent, new FileInfo(file.Path).Length, index, created.Uploads.Count))), ct).ConfigureAwait(false);
                var done = await api.PostAsync<AttachmentEnvelope>($"support/requests/{created.Request.Id}/attachments/{slot.AttachmentId}/complete", new { }, ct).ConfigureAwait(false);
                attachments.Add(done.Attachment);
                if (done.Attachment.State == "rejected")
                    return new SubmitResult(SubmitOutcome.UploadIncomplete, created.Request,
                        $"HCG rejected {file.Name}: {done.Attachment.Reason}. The request was kept as a draft; remove or replace the file and submit again.", attachments);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HcgApiException { Failure: HcgApiFailure.Cancelled })
            {
                return new SubmitResult(SubmitOutcome.UploadIncomplete, created.Request,
                    $"Upload cancelled. Request {created.Request.Reference} is kept as a draft on HCG and is not visible to staff; Submit again to resume, or withdraw it.", attachments);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or HcgApiException { IsOutage: true })
            {
                return new SubmitResult(SubmitOutcome.UploadIncomplete, created.Request,
                    $"The upload of {file.Name} did not finish ({ex.Message}). Request {created.Request.Reference} is kept as a draft; Submit again to retry — it will not be duplicated.", attachments);
            }
        }
        var submitted = await api.PostAsync<SupportRequestEnvelope>($"support/requests/{created.Request.Id}/submit", new { }, ct).ConfigureAwait(false);
        return new SubmitResult(SubmitOutcome.Submitted, submitted.Request.Summary,
            created.Duplicate
                ? $"Request {submitted.Request.Summary.Reference} was already on HCG; it is now complete."
                : $"Request {submitted.Request.Summary.Reference} was sent to HCG support.", submitted.Request.Attachments);
    }

    private async Task UploadWithRenewalAsync(Guid requestId, UploadSlot slot, UploadFile file, IProgress<long> progress, CancellationToken ct)
    {
        var current = slot;
        for (var attempt = 1; ; attempt++)
        {
            if (current.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
                current = (await api.PostAsync<UploadSlotEnvelope>($"support/requests/{requestId}/attachments/{slot.AttachmentId}/upload-url", new { }, ct).ConfigureAwait(false)).Upload;
            try
            {
                await UploadAsync(current, file, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                current = current with { ExpiresAt = DateTimeOffset.MinValue }; // ask for a fresh URL and retry
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// PUTs the file to the short-lived signed upload URL HCG issued. The URL must be HTTPS on HCG's own hosts (the
    /// identity/storage project or the website), so a response can never redirect an upload elsewhere.
    /// </summary>
    internal async Task UploadAsync(UploadSlot slot, UploadFile file, IProgress<long> progress, CancellationToken ct)
    {
        var allowed = new[] { api.Account.Configuration.Issuer.Host, api.Account.Configuration.ApiBaseUrl.Host };
        var schemeOk = slot.UploadUrl.Scheme == Uri.UriSchemeHttps || (slot.UploadUrl.Scheme == Uri.UriSchemeHttp && slot.UploadUrl.IsLoopback); // loopback: local test servers only
        if (!schemeOk || !allowed.Contains(slot.UploadUrl.Host, StringComparer.OrdinalIgnoreCase))
            throw new HcgApiException(HcgApiFailure.Invalid, $"HCG returned an upload address outside HCG ({slot.UploadUrl.Host}); nothing was uploaded.");
        var length = new FileInfo(file.Path).Length;
        if (length > slot.MaxBytes)
            throw new HcgApiException(HcgApiFailure.TooLarge, $"{file.Name} is {length:N0} bytes; HCG accepts at most {slot.MaxBytes:N0} bytes per file.");
        await using var stream = File.OpenRead(file.Path);
        using var content = new ProgressContent(stream, length, progress);
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, slot.UploadUrl) { Content = content };
        request.Headers.Add("x-upsert", "false");
        using var response = await uploadHttp.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict) return; // already uploaded by an earlier attempt; "complete" verifies it
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"storage answered HTTP {(int)response.StatusCode}");
    }

    /// <summary>Streams a file and reports bytes sent.</summary>
    private sealed class ProgressContent(Stream source, long length, IProgress<long> progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long sent = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sent += read;
                progress.Report(sent);
            }
        }

        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => SerializeToStreamAsync(target, context, CancellationToken.None);

        protected override bool TryComputeLength(out long l)
        {
            l = length;
            return true;
        }
    }
}

/// <summary>Keeps the draft (client request id) of an unfinished submission so a retry resumes the same HCG request.</summary>
public sealed class PendingSubmissionStore(string directory)
{
    private string PathFor(string key) => System.IO.Path.Combine(directory, "support-drafts", key + ".json");

    public void Save(string key, CreateSupportRequest request)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(directory, "support-drafts"));
        File.WriteAllText(PathFor(key), JsonSerializer.Serialize(request, HcgApiClient.Json));
    }

    public CreateSupportRequest? Load(string key)
    {
        var p = PathFor(key);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<CreateSupportRequest>(File.ReadAllText(p), HcgApiClient.Json); }
        catch (JsonException) { return null; }
    }

    public void Remove(string key)
    {
        var p = PathFor(key);
        if (File.Exists(p)) File.Delete(p);
    }
}
