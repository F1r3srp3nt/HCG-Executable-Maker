using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.Account.Store;

/// <summary>One HTTP request to HCG Store as it happened (diagnostics). The body excerpt is redacted and short.</summary>
public sealed record StoreProbe(string Path, int Status, string? ContentType, string? BodyExcerpt, string? ErrorCode, string? ErrorMessage, string? Transport, bool SentToken, TimeSpan Elapsed)
{
    /// <summary>The full body, for parsing only (never shown or copied).</summary>
    internal string? Body { get; init; }

    /// <summary>"GET /api/v1/health → HTTP 200 application/json (84 ms) {…}" — what the server actually answered.</summary>
    public string Line => Transport is not null
        ? $"GET /{Path} → no answer: {ErrorMessage}"
        : $"GET /{Path}{(SentToken ? " (with your HCG sign-in)" : "")} → HTTP {Status}{(ContentType is { } t ? " " + t : "")} ({Elapsed.TotalMilliseconds:0} ms)"
          + (ErrorCode is { Length: > 0 } c ? $" {c}: {ErrorMessage}" : BodyExcerpt is { Length: > 0 } b ? " " + b : "");

    internal static string? Excerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var flat = string.Join(' ', body.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        flat = SecretRedactor.RedactCredentials(flat);
        return flat.Length <= 400 ? flat : flat[..400] + "…";
    }
}

/// <summary>HCG Store's <c>GET /api/v1/publishing/status</c> (HCG Store server/src/publishing.ts).</summary>
public sealed record StorePublishingStatus
{
    public int ApiVersion { get; init; }
    public int MinimumClientApiVersion { get; init; }
    public IReadOnlyList<string> Features { get; init; } = [];
    public StoreStatusServer? Server { get; init; }
    /// <summary>disabled · submissions · enabled</summary>
    public string Publishing { get; init; } = "disabled";
    public StoreStatusConnect? HcgConnect { get; init; }
    public StoreStatusClients? DesktopClients { get; init; }
    public StoreStatusPermissionIds? Permissions { get; init; }
    public StoreStatusStages? Stages { get; init; }
    public StoreStatusLimits? Limits { get; init; }
    public StoreStatusCaller? Caller { get; init; }
}
public sealed record StoreStatusServer(string? Mode);
public sealed record StoreStatusConnect(bool Configured, string? Issuer);
public sealed record StoreStatusClients(int Registered);
public sealed record StoreStatusPermissionIds(string? Scope, string? Submit, string? Approve, string? Publish);
public sealed record StoreStage(bool Available, string? Where, string? Reason);
public sealed record StoreStatusStages(StoreStage? DraftCreation, StoreStage? ArtifactUpload, StoreStage? PassportAttachment, StoreStage? ReleaseSubmission, StoreStage? Approval, StoreStage? PublicPublication);
public sealed record StoreStatusLimits(long MaxUploadBytes, int ChunkBytes, int UploadUrlTtlSeconds);
public sealed record StoreStatusPermissionStates(string? Submit, string? Approve, string? Publish);
public sealed record StoreStatusCaller
{
    /// <summary>valid · expired · invalid · unverifiable · not-checked</summary>
    public string Token { get; init; } = "";
    public string? Reason { get; init; }
    public string? Sub { get; init; }
    public string? Email { get; init; }
    public long? ExpiresAt { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public string? ClientId { get; init; }
    public bool ClientRegistered { get; init; }
    public string? ClientReason { get; init; }
    public StoreStatusPermissionStates? Permissions { get; init; }
    public int? AuthorizedProducts { get; init; }
}

public enum StoreCheckState { Passed, Warning, Failed, Off, NotChecked }

/// <summary>One line of the Ship to Store diagnostics: what was checked, the result, the server's actual answer, and what to do.</summary>
public sealed record StoreCheck(string Id, string Title, StoreCheckState State, string Summary, string? Action = null, string? Response = null, string? WhoActs = null)
{
    public string StateLabel => State switch
    {
        StoreCheckState.Passed => "OK",
        StoreCheckState.Warning => "Attention",
        StoreCheckState.Failed => "Problem",
        StoreCheckState.Off => "Off",
        _ => "Not checked",
    };
    public string Tone => State switch
    {
        StoreCheckState.Passed => "success",
        StoreCheckState.Warning => "warning",
        StoreCheckState.Failed => "error",
        StoreCheckState.Off => "neutral",
        _ => "neutral",
    };
}

/// <summary>What the person, the Mac and HCG Connect contribute to the diagnostics (from the Account page's session).</summary>
public sealed record StoreDiagnosticsContext(bool SignedIn, string? AccountEmail, string? AccountMessage, HcgConnectConfiguration Connect, string? MakerVersion = null);

public sealed record StoreDiagnosticsReport(Uri Origin, DateTimeOffset CheckedAt, IReadOnlyList<StoreCheck> Checks, IReadOnlyList<StoreProduct> Products, StorePublishingStatus? Status)
{
    /// <summary>Everything needed to create a private draft and upload files is in place.</summary>
    public bool CanUpload => Checks.Where(c => c.Id is not ("submission" or "publication")).All(c => c.State is StoreCheckState.Passed or StoreCheckState.Warning);
    public StoreCheck this[string id] => Checks.First(c => c.Id == id);

    /// <summary>The one-line state for the page header, from the first check that is not OK.</summary>
    public (string Label, string Tone) Headline => Checks.FirstOrDefault(c => c.State == StoreCheckState.Failed) is { } f
        ? (f.Title + ": " + f.StateLabel.ToLowerInvariant(), "error")
        : CanUpload ? ("Ready to upload private drafts", "success") : ("Not ready", "warning");

    /// <summary>Plain text for Copy Diagnostics. Redacted again as a whole: no token, cookie or credential can be in it.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HCG Executable Maker — Ship to Store diagnostics");
        sb.AppendLine($"Store: {Origin}  ·  checked {CheckedAt:yyyy-MM-dd HH:mm:ss zzz}");
        if (Status is { } s) sb.AppendLine($"Store server: mode {s.Server?.Mode ?? "?"}, publishing {s.Publishing}, API v{s.ApiVersion} (clients from v{s.MinimumClientApiVersion})");
        sb.AppendLine();
        foreach (var c in Checks)
        {
            sb.AppendLine($"[{c.StateLabel}] {c.Title}: {c.Summary}");
            if (c.Response is { Length: > 0 } r) sb.AppendLine($"    Server: {r}");
            if (c.Action is { Length: > 0 } a) sb.AppendLine($"    What to do{(c.WhoActs is { } w ? $" ({w})" : "")}: {a}");
        }
        if (Products.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Products you may submit:");
            foreach (var p in Products) sb.AppendLine($"  {p.Id} — {p.Title} ({p.Source}){(p.Editions.Count > 0 ? ": " + string.Join(", ", p.Editions.Select(e => e.Id)) : "")}");
        }
        return SecretRedactor.RedactCredentials(sb.ToString());
    }
}

/// <summary>
/// Ship to Store connection diagnostics: eleven separate checks instead of one "publishing switched off", each with HCG
/// Store's actual answer and what to do. Read-only: nothing is created, uploaded, submitted or published. Tokens are
/// sent only to HCG Store over HTTPS and never appear in the report (it is redacted as a whole before it is shown or
/// copied).
/// </summary>
public static class StoreConnectionDiagnostics
{
    /// <summary>The publishing API versions this build of Maker speaks.</summary>
    public const int ClientApiVersion = 1;
    /// <summary>HCG Connect scopes Maker's sign-in asks for, and the Store's checks rely on.</summary>
    public static readonly IReadOnlyList<string> RequiredScopes = ["openid", "email", "profile"];

    public static async Task<StoreDiagnosticsReport> RunAsync(HcgStoreClient client, StoreDiagnosticsContext ctx, Func<CancellationToken, Task<string?>> accessToken, TimeProvider? time = null, CancellationToken ct = default)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var host = client.Origin.Host;
        var checks = new List<StoreCheck>();
        IReadOnlyList<StoreProduct> products = [];

        // 1 · Connectivity: the server's own public health answer.
        var health = await client.ProbeAsync("api/v1/health", withToken: false, ct).ConfigureAwait(false);
        var reachable = health.Transport is null;
        var isStore = health.Status == 200 && health.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && health.Body?.Contains("\"status\"", StringComparison.Ordinal) == true;
        checks.Add(!reachable
            ? new("connectivity", "Store server connectivity", StoreCheckState.Failed, $"No answer from {host}: {health.ErrorMessage}.",
                health.ErrorCode switch
                {
                    "dns_failure" => "Check the HCG Store address above, and that this Mac has internet access.",
                    "tls_failure" => "The server's HTTPS certificate was not accepted. Check the address; if it is right, the Store's certificate needs fixing.",
                    "store_timeout" => "Try again. If it keeps happening, the Store server may be overloaded or down.",
                    _ => "Check the network (VPN, proxy, firewall) and the address. This is not a refusal.",
                }, health.Line, "you")
            : isStore
                ? new("connectivity", "Store server connectivity", StoreCheckState.Passed, $"The HCG Store server at {host} answers ({StoreHealthText(health.Body)}).", null, health.Line)
                : new("connectivity", "Store server connectivity", StoreCheckState.Failed,
                    health.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                        ? $"{host} answers with a web page, not the HCG Store server (it serves the static storefront)."
                        : $"{host} answers, but not as an HCG Store server (HTTP {health.Status}).",
                    "Point Ship to Store at the address where the HCG Store server runs (Settings or the address above).", health.Line, "you"));

        // 2 · API version: the status endpoint (with the sign-in when there is one, for checks 5–7).
        var signedIn = ctx.SignedIn;
        // The sign-in goes only to a server that answered as an HCG Store (never to a web page or another service).
        var (status, statusProbe) = reachable ? await client.StatusAsync(withToken: signedIn && isStore, ct).ConfigureAwait(false) : (null, health);
        var olderServer = status is null && reachable && statusProbe.Status == 404 && statusProbe.ErrorCode is { Length: > 0 };
        checks.Add(!reachable ? Skipped("api", "API version compatibility", "the server did not answer")
            : status is null
                ? olderServer
                    ? new("api", "API version compatibility", StoreCheckState.Warning,
                        $"This HCG Store server predates the status API and answers \"{statusProbe.ErrorCode}\" to every publishing request, so its API version cannot be confirmed.",
                        "Nothing to change on this Mac. The HCG Store update that adds private submissions and this status API has to be deployed (HCG Store branch claude/private-submissions).", statusProbe.Line, "HCG Store administrator")
                    : new("api", "API version compatibility", StoreCheckState.Failed, $"The publishing status API did not answer as expected (HTTP {statusProbe.Status}).",
                        "This address may run another service or an incompatible HCG Store.", statusProbe.Line, "HCG Store administrator")
                : status.ApiVersion < ClientApiVersion || status.MinimumClientApiVersion > ClientApiVersion
                    ? new("api", "API version compatibility", StoreCheckState.Failed,
                        $"HCG Store speaks publishing API v{status.ApiVersion} and needs clients of at least v{status.MinimumClientApiVersion}; this Maker speaks v{ClientApiVersion}.",
                        status.MinimumClientApiVersion > ClientApiVersion ? "Update HCG Executable Maker." : "The HCG Store server is older than this Maker; it needs updating.", statusProbe.Line, status.MinimumClientApiVersion > ClientApiVersion ? "you" : "HCG Store administrator")
                    : new("api", "API version compatibility", StoreCheckState.Passed,
                        $"Publishing API v{status.ApiVersion} (Maker speaks v{ClientApiVersion}). Store mode {status.Server?.Mode ?? "?"}, Ship to Store \"{status.Publishing}\".", null, statusProbe.Line));

        // 3 · HCG Connect authentication (this Mac's session).
        string? token = null;
        string? tokenProblem = null;
        if (signedIn)
        {
            try { token = await accessToken(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { tokenProblem = SecretRedactor.RedactCredentials(ex.Message); }
        }
        checks.Add(!signedIn
            ? new("auth", "HCG Connect authentication", StoreCheckState.Failed, ctx.AccountMessage is { Length: > 0 } m ? $"Not signed in: {m}" : "Not signed in with HCG Connect.",
                "Sign in on the Account page (HCG Connect, in your browser with PKCE). Maker never asks for your password.", null, "you")
            : token is null
                ? new("auth", "HCG Connect authentication", StoreCheckState.Failed, $"Signed in as {ctx.AccountEmail ?? "your HCG account"}, but no access token could be obtained{(tokenProblem is { } p ? ": " + p : "")}.",
                    "Sign out and sign in again on the Account page.", null, "you")
                : new("auth", "HCG Connect authentication", StoreCheckState.Passed, $"Signed in as {ctx.AccountEmail ?? "your HCG account"} with HCG Connect (PKCE).", null, null));

        // 4 · Desktop client registration: Maker's own dedicated registration, and HCG Store's acceptance of it.
        var claims = token is null ? null : IdTokenValidator.PeekClaims(token);
        var tokenClient = claims is { } cl ? IdTokenValidator.Str(cl, "client_id") : null;
        var ownClient = ctx.Connect.ClientId;
        var dedicated = ctx.Connect.UsesBuiltInClient;
        var caller = status?.Caller;
        StoreCheck registration;
        if (string.IsNullOrWhiteSpace(ownClient))
            registration = new("client", "Desktop client registration", StoreCheckState.Failed, $"This build has no HCG Connect client for {ctx.Connect.Environment}.",
                "Use the production build, or import the issued client configuration on the Account page.", null, "you");
        else if (tokenClient is not null && tokenClient != ownClient)
            registration = new("client", "Desktop client registration", StoreCheckState.Failed,
                $"Your sign-in token was issued to another application's client ({tokenClient}), not Maker's ({ownClient}). Maker uses only its own registration, never HCG Store Manager's or HCG Store's.",
                "Sign out and sign in again from Maker's Account page.", null, "you");
        else if (caller is null)
            registration = new("client", "Desktop client registration", token is null ? StoreCheckState.NotChecked : StoreCheckState.Warning,
                $"Maker uses its own {(dedicated ? "built-in" : "imported")} registration {ctx.Connect.RegistryKey} (client {ownClient}). HCG Store's acceptance of it could not be checked{(status is null ? " (no status API on this server)" : token is null ? " (not signed in)" : "")}.",
                null, null);
        else if (caller.Token != "valid")
            registration = Skipped("client", "Desktop client registration", "HCG Store could not read the token (see Token validity)");
        else if (!caller.ClientRegistered)
            registration = new("client", "Desktop client registration", StoreCheckState.Failed,
                $"HCG Store does not accept Maker's client {caller.ClientId}: {caller.ClientReason}",
                $"An HCG Store administrator adds {ownClient} (HCG Executable Maker, {ctx.Connect.RegistryKey}) to HCG_STORE_PUBLISHING_CLIENT_IDS.", "status: " + CallerLine(caller), "HCG Store administrator");
        else
            registration = new("client", "Desktop client registration", StoreCheckState.Passed,
                $"Maker's dedicated registration {ctx.Connect.RegistryKey} (client {ownClient}) is registered for publishing on this Store.", null, "status: " + CallerLine(caller));
        checks.Add(registration);

        // 5 · Token validity: locally (expiry, scopes) and as HCG Store verified it (signature, issuer, expiry).
        if (token is null) checks.Add(Skipped("token", "Token validity", "not signed in"));
        else
        {
            var exp = claims is { } c2 && c2.TryGetProperty("exp", out var e) && e.TryGetInt64(out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs) : (DateTimeOffset?)null;
            var scopes = claims is { } c3 && IdTokenValidator.Str(c3, "scope") is { } sc ? sc.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            var missing = RequiredScopes.Where(s => !scopes.Contains(s)).ToList();
            var local = $"expires {(exp is { } x ? x.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "?")}, scopes {(scopes.Length > 0 ? string.Join(" ", scopes) : "none")}";
            if (caller is null)
                checks.Add(new("token", "Token validity", exp is { } ex2 && ex2 < now ? StoreCheckState.Failed : StoreCheckState.Warning,
                    $"Checked on this Mac only ({local}); HCG Store's verification is not available{(status is null ? " on this server" : "")}.", exp is { } ex3 && ex3 < now ? "Sign in again on the Account page." : null, null));
            else checks.Add(caller.Token switch
            {
                "valid" when missing.Count > 0 => new("token", "Token validity", StoreCheckState.Warning, $"HCG Store verified the token ({local}), but it lacks the scope(s) {string.Join(", ", missing)}.",
                    "Sign out and sign in again so HCG Connect issues the full set of scopes.", "status: " + CallerLine(caller), "you"),
                "valid" => new("token", "Token validity", StoreCheckState.Passed, $"HCG Store verified the token's signature, issuer and expiry ({local}).", null, "status: " + CallerLine(caller)),
                "expired" => new("token", "Token validity", StoreCheckState.Failed, "HCG Store says the token has expired.", "Sign in again on the Account page (Maker refreshes tokens automatically; a long sleep can outlast it).", "status: " + CallerLine(caller), "you"),
                "unverifiable" => new("token", "Token validity", StoreCheckState.Warning, $"HCG Store could not verify the token right now: {caller.Reason}", "Try again in a moment; this is not a refusal.", "status: " + CallerLine(caller), "HCG Store administrator"),
                "not-checked" => new("token", "Token validity", StoreCheckState.Failed, caller.Reason ?? "HCG Store cannot check sign-ins.", "HCG Connect must be configured on the HCG Store server.", "status: " + CallerLine(caller), "HCG Store administrator"),
                _ => new("token", "Token validity", StoreCheckState.Failed, $"HCG Store rejected the token: {caller.Reason}", "Sign out and sign in again on the Account page. If it persists, the Store and Maker may use different HCG Connect environments.", "status: " + CallerLine(caller), "you"),
            });
        }

        // 6 · Product publishing permissions: HCG's personal grants on HCG Store's central product (hcg.store).
        var perm = caller?.Permissions;
        var ids = status?.Permissions;
        checks.Add(caller is null || caller.Token != "valid"
            ? Skipped("permissions", "Product publishing permissions", token is null ? "not signed in" : status is null ? "no status API on this server" : "the token was not accepted")
            : perm?.Submit switch
            {
                "granted" => new("permissions", "Product publishing permissions", StoreCheckState.Passed,
                    $"HCG grants {caller.Email ?? "your account"} {ids?.Submit ?? "the submit permission"} on {ids?.Scope ?? "hcg.store"}{(perm.Approve == "granted" ? $" and {ids?.Approve} (approval in the browser)" : "")}.", null, "status: " + CallerLine(caller)),
                "not-granted" => new("permissions", "Product publishing permissions", StoreCheckState.Failed,
                    $"HCG has not granted {caller.Email ?? "your account"} {ids?.Submit ?? "the publishing permission"} on {ids?.Scope ?? "hcg.store"}. Buying or owning a product does not grant it.",
                    $"Ask HCG to grant {ids?.Submit ?? "store.publish"} on {ids?.Scope ?? "hcg.store"} to this account.", "status: " + CallerLine(caller), "HCG"),
                "unavailable" => new("permissions", "Product publishing permissions", StoreCheckState.Warning, "HCG could not confirm the permission right now.", "Try again in a moment; this is not a refusal.", "status: " + CallerLine(caller), "HCG"),
                "not-configured" => new("permissions", "Product publishing permissions", StoreCheckState.Failed, "This HCG Store has no publishing permission configured.", "HCG_STORE_PUBLISHER_CENTRAL_ID must be set on the HCG Store server.", "status: " + CallerLine(caller), "HCG Store administrator"),
                _ => Skipped("permissions", "Product publishing permissions", "HCG Store gave no answer"),
            });

        // 7 · Authorised products: the actual catalogue HCG lets this account submit to.
        var stages = status?.Stages;
        var submissionsOn = status is not null ? stages?.DraftCreation?.Available == true : (bool?)null;
        if (token is null) checks.Add(Skipped("products", "Authorized product availability", "not signed in"));
        else if (!isStore) checks.Add(Skipped("products", "Authorized product availability", "the address is not an HCG Store server, so your sign-in is not sent there"));
        else if (submissionsOn == false || olderServer)
            checks.Add(new("products", "Authorized product availability", StoreCheckState.Off,
                "Ship to Store is switched off on this server, so it lists no products.", stages?.DraftCreation?.Reason ?? statusProbe.ErrorMessage, statusProbe.Line, "HCG Store administrator"));
        else
        {
            var probe = await client.ProbeAsync("api/v1/publishing/products", withToken: true, ct).ConfigureAwait(false);
            if (probe.Status == 200 && probe.Body is { } b)
            {
                try { products = JsonSerializer.Deserialize<StoreProductList>(b, HcgStoreClient.Json)?.Products ?? []; } catch (JsonException) { }
                checks.Add(products.Count > 0
                    ? new("products", "Authorized product availability", StoreCheckState.Passed,
                        $"{products.Count} product(s): {string.Join(", ", products.Take(6).Select(p => p.Title + (p.Editions.Count > 0 ? $" ({p.Editions.Count} edition{(p.Editions.Count == 1 ? "" : "s")})" : "")))}{(products.Count > 6 ? ", …" : "")}.", null, probe.Line)
                    : new("products", "Authorized product availability", StoreCheckState.Failed, "HCG Store lists no product you may submit to.",
                        "HCG grants the publishing permission per person on hcg.store; once granted, every catalogue product is listed.", probe.Line, "HCG"));
            }
            else checks.Add(new("products", "Authorized product availability", StoreCheckState.Failed, $"The product list was refused: {probe.ErrorMessage ?? "HTTP " + probe.Status}.", Explain(probe), probe.Line, probe.Status == 403 ? "HCG" : "HCG Store administrator"));
        }

        // 8–11 · Stages, as HCG Store states them. Nothing is created to test them.
        checks.Add(StageCheck("draft", "Private draft creation", "draftCreation", stages?.DraftCreation, status, statusProbe, olderServer, perm?.Submit, "create private drafts"));
        checks.Add(StageCheck("upload", "Artifact upload availability", "artifactUpload", stages?.ArtifactUpload, status, statusProbe, olderServer, perm?.Submit,
            "upload files" + (status?.Limits is { } l ? $" (resumable, {l.ChunkBytes / 1048576} MB chunks, files up to {l.MaxUploadBytes / 1048576:N0} MB, SHA-256 checked by the server)" : "")));
        checks.Add(StageCheck("submission", "Release submission", "releaseSubmission", stages?.ReleaseSubmission, status, statusProbe, olderServer, perm?.Submit, "submit releases for review (a person approves them in HCG Store)"));
        var pub = stages?.PublicPublication;
        checks.Add(status is null
            ? new("publication", "Public publication", olderServer ? StoreCheckState.Off : StoreCheckState.NotChecked,
                olderServer ? "Switched off on this server (publishing is disabled). Maker never publishes; a person publishes approved releases in HCG Store." : "Not checked.", null, statusProbe.Line)
            : pub?.Available == true
                ? new("publication", "Public publication", StoreCheckState.Passed, "Available in HCG Store's browser review, by a person with HCG's publication permission. Maker never publishes.", null, StageLine("publicPublication", pub))
                : new("publication", "Public publication", StoreCheckState.Off, (pub?.Reason ?? "Switched off on this server.") + " Uploading and leaving releases waiting privately for approval does not need it.", null, StageLine("publicPublication", pub)));

        return new StoreDiagnosticsReport(client.Origin, now, checks, products, status);
    }

    /// <summary>The one field of HCG Store's status answer a stage check relies on (the full answer is under API version).</summary>
    private static string StageLine(string key, StoreStage? stage) =>
        $"status → stages.{key}: available {(stage?.Available == true ? "true" : "false")}, where {stage?.Where ?? "?"}{(stage?.Reason is { } r ? $", reason \"{r}\"" : "")}";

    private static StoreCheck StageCheck(string id, string title, string key, StoreStage? stage, StorePublishingStatus? status, StoreProbe statusProbe, bool olderServer, string? submit, string what)
    {
        if (status is null)
            return olderServer
                ? new(id, title, StoreCheckState.Failed, $"Switched off: this HCG Store server answers \"{statusProbe.ErrorCode}\" ({statusProbe.ErrorMessage}).",
                    "The HCG Store server must allow private submissions (HCG_STORE_PUBLISHING=submissions, in the HCG Store update that adds it). Public publication can stay off.", statusProbe.Line, "HCG Store administrator")
                : new(id, title, StoreCheckState.NotChecked, "Not checked: the server's status is unknown.", null, statusProbe.Line);
        if (stage is not { Available: true })
            return new(id, title, StoreCheckState.Failed, $"Switched off on this server. {stage?.Reason}".Trim(),
                $"An HCG Store administrator sets HCG_STORE_PUBLISHING=submissions (private drafts and uploads only; nothing becomes public).", StageLine(key, stage), "HCG Store administrator");
        return submit switch
        {
            "granted" => new(id, title, StoreCheckState.Passed, $"Available: you may {what}.", null, StageLine(key, stage)),
            "not-granted" => new(id, title, StoreCheckState.Failed, "Available on the server, but HCG has not granted you the publishing permission (see above).", null, StageLine(key, stage), "HCG"),
            _ => new(id, title, StoreCheckState.Warning, $"Available on the server; whether you may {what} depends on your sign-in and permission (see above).", null, StageLine(key, stage)),
        };
    }

    private static StoreCheck Skipped(string id, string title, string why) => new(id, title, StoreCheckState.NotChecked, $"Not checked: {why}.");

    private static string CallerLine(StoreStatusCaller c) =>
        $"token {c.Token}{(c.Reason is { } r ? $" ({r})" : "")}, client {c.ClientId ?? "?"} {(c.ClientRegistered ? "registered" : "not registered")}" +
        (c.Permissions is { } p ? $", submit {p.Submit}, approve {p.Approve}, publish {p.Publish}" : "") +
        (c.AuthorizedProducts is { } n ? $", {n} product(s)" : "");

    private static string StoreHealthText(string? body)
    {
        if (body is null) return "status unknown";
        try
        {
            var h = JsonSerializer.Deserialize<StoreHealth>(body, HcgStoreClient.Json);
            return $"status {h?.Status}, mode {h?.Mode}, HCG Connect {h?.HcgConnect}";
        }
        catch (JsonException) { return "status unknown"; }
    }

    private static string? Explain(StoreProbe p) => (p.Status, p.ErrorCode) switch
    {
        (401, _) => "HCG Store did not accept your sign-in: see Token validity and Desktop client registration.",
        (403, _) => "HCG has not granted this account the publishing permission.",
        (404, "publishing_disabled") => "Ship to Store is switched off on the server.",
        ( >= 500, _) => "The Store failed; try again later. This is not a refusal.",
        _ => null,
    };
}
