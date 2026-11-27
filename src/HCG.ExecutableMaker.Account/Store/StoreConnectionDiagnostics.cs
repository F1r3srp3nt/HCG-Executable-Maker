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

public sealed record StoreDiagnosticsReport(Uri Origin, DateTimeOffset CheckedAt, IReadOnlyList<StoreCheck> Checks, IReadOnlyList<StoreProduct> Products)
{
    /// <summary>The server's public health answer (null when it did not answer as HCG Store).</summary>
    public StoreHealth? Health { get; init; }
    /// <summary>HCG Store's whoami answer for this sign-in (null when not checked or refused).</summary>
    public StoreWhoAmI? WhoAmI { get; init; }
    /// <summary>HCG Store's capabilities (null when not checked or refused).</summary>
    public StoreCapabilities? Capabilities { get; init; }
    /// <summary>The capability flags the Store stated (capabilities, else whoami). Null: the Store stated none.</summary>
    public StoreCapabilityFlags? Flags => Capabilities?.Capabilities ?? WhoAmI?.Capabilities;
    /// <summary>disabled · uploads · enabled (· submissions on an interim Store), as the server stated it; null when unknown.</summary>
    public string? PublishingMode => Health?.Publishing ?? Capabilities?.PublishingMode ?? WhoAmI?.PublishingMode;
    /// <summary>Releases Release Manager listed for this account (null when not read).</summary>
    public int? ManagedReleases { get; init; }

    /// <summary>Everything needed to create a private draft and upload files is in place (public publication is not needed).</summary>
    public bool CanUpload => Checks.Where(c => c.Id is not ("publication" or "manager")).All(c => c.State is StoreCheckState.Passed or StoreCheckState.Warning);
    public StoreCheck this[string id] => Checks.First(c => c.Id == id);

    /// <summary>The one-line state for the page header, from the first check that is not OK.</summary>
    public (string Label, string Tone) Headline => Checks.FirstOrDefault(c => c.State == StoreCheckState.Failed) is { } f
        ? (f.Title + ": " + f.StateLabel.ToLowerInvariant(), "error")
        : CanUpload ? ("Ready to upload private drafts", "success") : ("Not ready", "warning");

    /// <summary>Plain text for Copy Diagnostics. Redacted again as a whole: no token, cookie or credential can be in it.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("HCG Executable Maker — Distribution Studio (Ship to Store) diagnostics");
        sb.AppendLine($"Store: {Origin}  ·  checked {CheckedAt:yyyy-MM-dd HH:mm:ss zzz}");
        if (Health is { } h) sb.AppendLine($"Store server: status {h.Status}, mode {h.Mode}, publishing {h.Publishing ?? "not stated"}, HCG Connect {h.HcgConnect}, payments {h.Payments}, protected downloads {h.ProtectedDownloads}");
        if (Capabilities is { } c0) sb.AppendLine($"Publishing API v{c0.ApiVersion} (Maker speaks v{StoreConnectionDiagnostics.ClientApiVersion}); approval and publication: {c0.ApprovalAndPublication ?? "not stated"}");
        if (Flags is { } f0) sb.AppendLine("Capabilities: " + StoreConnectionDiagnostics.FlagsText(f0));
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
/// Distribution Studio connection diagnostics, against the HCG Store publishing contract as deployed: the public health
/// answer, then (signed in) whoami, capabilities, the authorised products and Release Manager. Ten separate checks, each
/// with HCG Store's actual answer and what to do. Read-only: nothing is created, uploaded, submitted or published. The
/// HCG Connect token is sent only to a server that answered as HCG Store, and never appears in the report (it is
/// redacted as a whole before it is shown or copied).
/// </summary>
public static class StoreConnectionDiagnostics
{
    /// <summary>The publishing API version this build of Maker speaks.</summary>
    public const int ClientApiVersion = 1;
    /// <summary>HCG Connect scopes Maker's sign-in asks for.</summary>
    public static readonly IReadOnlyList<string> RequiredScopes = ["openid", "email", "profile"];
    /// <summary>The check ids, in order.</summary>
    public static readonly IReadOnlyList<string> CheckIds = ["connectivity", "api", "mode", "upload", "auth", "client", "permission", "products", "manager", "publication"];

    public static string FlagsText(StoreCapabilityFlags f)
    {
        var on = new List<string>();
        void Add(bool v, string n) { if (v) on.Add(n); }
        Add(f.Products, "products"); Add(f.Drafts, "drafts"); Add(f.Uploads, "uploads"); Add(f.ResumableUploads, "resumable uploads"); Add(f.Passports, "passports");
        Add(f.Submission, "submission"); Add(f.Approval, "approval"); Add(f.Publication, "publication"); Add(f.PublicDelivery, "public delivery"); Add(f.Offers, "offers");
        Add(f.ProductMetadata, "product metadata"); Add(f.PublicationRequests, "publication requests"); Add(f.AvailabilityRequests, "availability requests");
        Add(f.SupersedeCompatible, "compatible supersede"); Add(f.Withdraw, "withdraw");
        return on.Count == 0 ? "none stated" : string.Join(", ", on);
    }

    public static async Task<StoreDiagnosticsReport> RunAsync(HcgStoreClient client, StoreDiagnosticsContext ctx, Func<CancellationToken, Task<string?>> accessToken, TimeProvider? time = null, CancellationToken ct = default)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var host = client.Origin.Host;
        var checks = new List<StoreCheck>();
        IReadOnlyList<StoreProduct> products = [];

        // 1 · Server availability: the server's own public health answer (no sign-in).
        var healthProbe = await client.ProbeAsync("api/v1/health", withToken: false, ct).ConfigureAwait(false);
        var reachable = healthProbe.Transport is null;
        var isStore = healthProbe.Status == 200 && healthProbe.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && healthProbe.Body?.Contains("\"status\"", StringComparison.Ordinal) == true;
        StoreHealth? health = null;
        if (isStore) try { health = JsonSerializer.Deserialize<StoreHealth>(healthProbe.Body!, HcgStoreClient.Json); } catch (JsonException) { isStore = false; }
        checks.Add(!reachable
            ? new("connectivity", "Server availability", StoreCheckState.Failed, $"No answer from {host}: {healthProbe.ErrorMessage}.",
                healthProbe.ErrorCode switch
                {
                    "dns_failure" => "Check the HCG Store address above, and that this Mac has internet access.",
                    "tls_failure" => "The server's HTTPS certificate was not accepted. Check the address; if it is right, the Store's certificate needs fixing.",
                    "store_timeout" => "Try again. If it keeps happening, the Store server may be overloaded or down.",
                    _ => "Check the network (VPN, proxy, firewall) and the address. This is not a refusal.",
                }, healthProbe.Line, "you")
            : isStore
                ? new("connectivity", "Server availability", health?.Status == "ok" ? StoreCheckState.Passed : StoreCheckState.Warning,
                    $"The HCG Store server at {host} answers: status {health?.Status ?? "?"}, mode {health?.Mode ?? "?"}, HCG Connect {health?.HcgConnect ?? "?"}.", null, healthProbe.Line)
                : new("connectivity", "Server availability", StoreCheckState.Failed,
                    healthProbe.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                        ? $"{host} answers with a web page, not the HCG Store server (it serves the static storefront)."
                        : $"{host} answers, but not as an HCG Store server (HTTP {healthProbe.Status}).",
                    "Point Distribution Studio at the address where the HCG Store server runs (Settings or the address above).", healthProbe.Line, "you"));

        // HCG Connect: this Mac's session (the token is fetched once; it goes only to a server that answered as HCG Store).
        var signedIn = ctx.SignedIn;
        string? token = null, tokenProblem = null;
        if (signedIn)
        {
            try { token = await accessToken(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { tokenProblem = SecretRedactor.RedactCredentials(ex.Message); }
        }
        var send = token is not null && isStore;
        StoreProbe? whoProbe = null, capsProbe = null, productsProbe = null, managerProbe = null;
        StoreWhoAmI? who = null;
        StoreCapabilities? caps = null;
        if (send)
        {
            whoProbe = await client.ProbeAsync("api/v1/publishing/whoami", withToken: true, ct).ConfigureAwait(false);
            if (whoProbe.Status == 200) who = Parse<StoreWhoAmI>(whoProbe);
            if (who is not null)
            {
                capsProbe = await client.ProbeAsync("api/v1/publishing/capabilities", withToken: true, ct).ConfigureAwait(false);
                if (capsProbe.Status == 200) caps = Parse<StoreCapabilities>(capsProbe);
                productsProbe = await client.ProbeAsync("api/v1/publishing/products", withToken: true, ct).ConfigureAwait(false);
                if (productsProbe.Status == 200) products = Parse<StoreProductList>(productsProbe)?.Products ?? [];
                managerProbe = await client.ProbeAsync("api/v1/publishing/manager", withToken: true, ct).ConfigureAwait(false);
            }
        }
        var flags = caps?.Capabilities ?? who?.Capabilities;
        var mode = health?.Publishing ?? caps?.PublishingMode ?? who?.PublishingMode;
        var disabledByServer = whoProbe?.ErrorCode == "publishing_disabled" || mode == "disabled";
        var uploadsOn = mode is "uploads" or "enabled" or "submissions";

        // 2 · Live API version: from capabilities (it needs a publishing permission), else what can be said without it.
        checks.Add(!isStore ? Skipped("api", "Live API version", reachable ? "the address is not an HCG Store server" : "the server did not answer")
            : caps is { } cv
                ? cv.ApiVersion == ClientApiVersion
                    ? new("api", "Live API version", StoreCheckState.Passed, $"Publishing API v{cv.ApiVersion} (Maker speaks v{ClientApiVersion}). Capabilities: {(cv.Capabilities is { } cf ? FlagsText(cf) : "none stated (an older HCG Store)")}.", null, capsProbe!.Line)
                    : new("api", "Live API version", StoreCheckState.Failed, $"HCG Store speaks publishing API v{cv.ApiVersion}; this Maker speaks v{ClientApiVersion}.",
                        cv.ApiVersion > ClientApiVersion ? "Update HCG Executable Maker." : "The HCG Store server is older than this Maker.", capsProbe!.Line, cv.ApiVersion > ClientApiVersion ? "you" : "HCG Store administrator")
                : disabledByServer && whoProbe is not null
                    ? new("api", "Live API version", StoreCheckState.Off, "The publishing API is switched off on this server, so its version cannot be read.", null, whoProbe.Line)
                    : who is not null
                        ? new("api", "Live API version", StoreCheckState.Warning, $"Publishing API v1 answers (whoami), but its capabilities were not given to this account ({capsProbe?.ErrorCode ?? "HTTP " + capsProbe?.Status}).", null, capsProbe?.Line)
                        : Skipped("api", "Live API version", token is null ? "it is read after you sign in (the capabilities need a publishing permission)" : "HCG Store did not accept the sign-in (see below)"));

        // 3 · Publishing mode, as the server states it.
        checks.Add(!isStore ? Skipped("mode", "Publishing mode", reachable ? "the address is not an HCG Store server" : "the server did not answer")
            : mode switch
            {
                "uploads" => new("mode", "Publishing mode", StoreCheckState.Passed, "uploads: private drafts, uploads and passports are on; approval and public publication are off on this server.", null, StoreLine(healthProbe, whoProbe)),
                "submissions" => new("mode", "Publishing mode", StoreCheckState.Passed, "submissions: private drafts and uploads are on; public publication is off.", null, StoreLine(healthProbe, whoProbe)),
                "enabled" => new("mode", "Publishing mode", StoreCheckState.Passed, "enabled: private drafts, uploads, approval and publication (by a person, in HCG Store's browser review) are on.", null, StoreLine(healthProbe, whoProbe)),
                "disabled" => new("mode", "Publishing mode", StoreCheckState.Failed, "disabled: the HCG Store server has publishing switched off, including private uploads.",
                    "An HCG Store administrator sets HCG_STORE_PUBLISHING=uploads (private drafts and uploads only; nothing becomes public). Nothing on this Mac needs fixing.", StoreLine(healthProbe, whoProbe), "HCG Store administrator"),
                _ when whoProbe?.ErrorCode == "publishing_disabled" => new("mode", "Publishing mode", StoreCheckState.Failed, "This HCG Store answers \"publishing_disabled\": publishing is switched off, including private uploads.",
                    "An HCG Store administrator sets HCG_STORE_PUBLISHING=uploads.", whoProbe.Line, "HCG Store administrator"),
                _ => new("mode", "Publishing mode", StoreCheckState.Warning, "The server does not state its publishing mode (an older HCG Store).", null, healthProbe.Line),
            });

        // 4 · HCG Connect authentication (this Mac's session).
        checks.Add(!signedIn
            ? new("auth", "HCG Connect authentication", StoreCheckState.Failed, ctx.AccountMessage is { Length: > 0 } m ? $"Not signed in: {m}" : "Not signed in with HCG Connect.",
                "Sign in on the Account page (HCG Connect, in your browser with PKCE). Maker never asks for your password.", null, "you")
            : token is null
                ? new("auth", "HCG Connect authentication", StoreCheckState.Failed, $"Signed in as {ctx.AccountEmail ?? "your HCG account"}, but no access token could be obtained{(tokenProblem is { } p ? ": " + p : "")}.",
                    "Sign out and sign in again on the Account page.", null, "you")
                : LocalTokenCheck(token, ctx, now, whoProbe, who));

        // 5 · Dedicated Executable Maker client: this build's issued registration, and HCG Store's acceptance of it.
        checks.Add(ClientCheck(token, ctx, whoProbe, who, isStore));

        // 6 · Central publisher permission: HCG's personal grant (store.publish on hcg.store), as HCG Store asked HCG.
        checks.Add(who is null ? Skipped("permission", "Central publisher permission", token is null ? "not signed in" : !isStore ? "the address is not an HCG Store server, so your sign-in is not sent there" : "HCG Store did not accept the sign-in")
            : capsProbe?.Status == 403 ? new("permission", "Central publisher permission", StoreCheckState.Failed,
                $"HCG has not granted {who.Email ?? "your account"} a publishing permission ({who.PublisherPermission ?? "store.publish"} on hcg.store). Buying or owning a product does not grant it.",
                $"Ask HCG to grant {who.PublisherPermission ?? "store.publish"} on hcg.store to this account.", capsProbe.Line, "HCG")
            : products.Count > 0 ? new("permission", "Central publisher permission", StoreCheckState.Passed,
                $"HCG grants {who.Email ?? "your account"} {who.PublisherPermission ?? "the publisher permission"} (checked by HCG Store with HCG just now).", null, productsProbe!.Line)
            : caps is not null ? new("permission", "Central publisher permission", StoreCheckState.Warning,
                $"HCG grants {who.Email ?? "your account"} a review permission, but not {who.PublisherPermission ?? "store.publish"}: HCG Store lists no product you may upload to.",
                $"Ask HCG to grant {who.PublisherPermission ?? "store.publish"} on hcg.store.", productsProbe?.Line, "HCG")
            : capsProbe is { Status: >= 500 } or { Transport: not null } ? new("permission", "Central publisher permission", StoreCheckState.Warning, "HCG could not confirm the permission right now. This is not a refusal.", "Try again in a moment.", capsProbe.Line, "HCG")
            : new("permission", "Central publisher permission", StoreCheckState.Failed, $"The permission could not be confirmed: {capsProbe?.ErrorMessage ?? "no answer"}.", null, capsProbe?.Line, "HCG Store administrator"));

        // 7 · Private upload availability: the mode, the Store's own flags, and the permission.
        checks.Add(!isStore ? Skipped("upload", "Private upload availability", reachable ? "the address is not an HCG Store server" : "the server did not answer")
            : !uploadsOn && mode is not null || disabledByServer ? new("upload", "Private upload availability", StoreCheckState.Failed, "Switched off on this server (publishing mode \"disabled\").",
                "An HCG Store administrator sets HCG_STORE_PUBLISHING=uploads. Public publication can stay off.", StoreLine(healthProbe, whoProbe), "HCG Store administrator")
            : flags is { Uploads: false } ? new("upload", "Private upload availability", StoreCheckState.Failed, "HCG Store states that uploads are not available.", null, capsProbe?.Line, "HCG Store administrator")
            : who is null ? new("upload", "Private upload availability", StoreCheckState.Warning, $"On, on the server{(mode is null ? "" : $" ({mode})")}; whether you may upload is decided after you sign in.", null, healthProbe.Line)
            : products.Count == 0 ? new("upload", "Private upload availability", StoreCheckState.Failed, "On, on the server, but HCG has not given you a product to upload to (see the permission).", null, productsProbe?.Line, "HCG")
            : new("upload", "Private upload availability", StoreCheckState.Passed,
                $"Available: private drafts and resumable uploads{(caps is { } l ? $" ({l.ChunkBytes / 1048576} MB chunks, files up to {l.MaxUploadBytes / 1048576:N0} MB, SHA-256 checked by the server)" : "")}{(flags?.Passports == true ? ", Release Passports" : "")}. Public publication does not need to be on.", null, capsProbe?.Line ?? whoProbe?.Line));

        // 8 · Authorized catalogue: the actual products HCG lets this account upload to.
        checks.Add(token is null ? Skipped("products", "Authorized catalogue", "not signed in")
            : !isStore ? Skipped("products", "Authorized catalogue", "the address is not an HCG Store server, so your sign-in is not sent there")
            : productsProbe is null ? Skipped("products", "Authorized catalogue", "HCG Store did not accept the sign-in")
            : productsProbe.Status == 200 ? products.Count > 0
                ? new("products", "Authorized catalogue", StoreCheckState.Passed,
                    $"{products.Count} product(s): {string.Join(", ", products.Take(6).Select(p => p.Title + (p.Editions.Count > 0 ? $" ({p.Editions.Count} edition{(p.Editions.Count == 1 ? "" : "s")})" : "")))}{(products.Count > 6 ? ", …" : "")}." +
                    (flags?.ProductMetadata == true ? " With official metadata from the reviewed catalogue." : " This Store sends names and editions only; official summaries come from its public release resolution."), null, productsProbe.Line)
                : new("products", "Authorized catalogue", StoreCheckState.Failed, "HCG Store lists no product you may upload to.", "HCG grants store.publish per person on hcg.store; once granted, every catalogue product is listed.", productsProbe.Line, "HCG")
            : new("products", "Authorized catalogue", StoreCheckState.Failed, $"The product list was refused: {productsProbe.ErrorMessage ?? "HTTP " + productsProbe.Status}.", Explain(productsProbe), productsProbe.Line, productsProbe.Status == 403 ? "HCG" : "HCG Store administrator"));

        // 9 · Release Manager connectivity: the shared release list HCG Store and HCG Store Manager use.
        int? managed = null;
        bool? auditIntact = null;
        if (managerProbe is { Status: 200 } && Parse<StoreReleaseManager>(managerProbe) is { } mgr) { managed = mgr.Releases.Count; auditIntact = mgr.AuditIntact; }
        checks.Add(managerProbe is null ? Skipped("manager", "Release Manager connectivity", token is null ? "not signed in" : "HCG Store did not accept the sign-in")
            : managed is { } n ? new("manager", "Release Manager connectivity", auditIntact == false ? StoreCheckState.Warning : StoreCheckState.Passed,
                $"Release Manager answers: {n} release(s) you may submit or review; audit trail {(auditIntact == true ? "intact" : auditIntact == false ? "NOT intact" : "not stated")}. The same list appears in HCG Store and HCG Store Manager.",
                auditIntact == false ? "Tell an HCG Store administrator: the audit trail failed its integrity check." : null, managerProbe.Line, auditIntact == false ? "HCG Store administrator" : null)
            : new("manager", "Release Manager connectivity", managerProbe.Status == 403 ? StoreCheckState.Off : StoreCheckState.Failed, $"Release Manager did not answer as expected: {managerProbe.ErrorMessage ?? "HTTP " + managerProbe.Status}.", Explain(managerProbe), managerProbe.Line));

        // 10 · Public publication: informational. Maker never publishes; a person does, in HCG Store.
        checks.Add(!isStore ? Skipped("publication", "Public publication", "the server's mode is unknown")
            : flags?.Publication == true || (flags is null && mode == "enabled")
                ? new("publication", "Public publication", StoreCheckState.Passed, "Available in HCG Store's browser review, to a person with HCG's publication permission. Maker never publishes.", null, capsProbe?.Line ?? healthProbe.Line)
                : new("publication", "Public publication", StoreCheckState.Off, $"Switched off on this server{(mode is { } md ? $" (mode {md})" : "")}. Uploading and leaving releases waiting privately does not need it; nothing becomes public.", null, capsProbe?.Line ?? healthProbe.Line));

        // Reorder to the documented order.
        var ordered = CheckIds.Select(id => checks.First(c => c.Id == id)).ToList();
        return new StoreDiagnosticsReport(client.Origin, now, ordered, products) { Health = health, WhoAmI = who, Capabilities = caps, ManagedReleases = managed };
    }

    private static StoreCheck LocalTokenCheck(string token, StoreDiagnosticsContext ctx, DateTimeOffset now, StoreProbe? whoProbe, StoreWhoAmI? who)
    {
        var claims = IdTokenValidator.PeekClaims(token);
        var exp = claims is { } c2 && c2.TryGetProperty("exp", out var e) && e.TryGetInt64(out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs) : (DateTimeOffset?)null;
        var scopes = claims is { } c3 && IdTokenValidator.Str(c3, "scope") is { } sc ? sc.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        var local = $"expires {(exp is { } x ? x.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "?")}, scopes {(scopes.Length > 0 ? string.Join(" ", scopes) : "none")}";
        var account = ctx.AccountEmail ?? "your HCG account";
        if (exp is { } ex && ex < now) return new("auth", "HCG Connect authentication", StoreCheckState.Failed, $"Signed in as {account}, but the access token expired ({local}).", "Sign in again on the Account page.", whoProbe?.Line, "you");
        if (who is not null) return new("auth", "HCG Connect authentication", StoreCheckState.Passed, $"Signed in as {who.Email ?? account} with HCG Connect (PKCE). HCG Store verified the token's signature, issuer and expiry ({local}).", null, whoProbe!.Line);
        if (whoProbe is { Status: 401 } && !ClientRefusal(whoProbe))
            return new("auth", "HCG Connect authentication", StoreCheckState.Failed, $"HCG Store did not accept the sign-in: {whoProbe.ErrorMessage}",
                "Sign out and sign in again on the Account page. If it persists, the Store and Maker may use different HCG Connect environments.", whoProbe.Line, "you");
        if (whoProbe is { Status: 503 })
            return new("auth", "HCG Connect authentication", StoreCheckState.Failed, $"HCG Store cannot check sign-ins: {whoProbe.ErrorMessage}", "HCG Connect must be configured on the HCG Store server.", whoProbe.Line, "HCG Store administrator");
        return new("auth", "HCG Connect authentication", StoreCheckState.Warning, $"Signed in as {account} with HCG Connect (PKCE); checked on this Mac only ({local}).", null, whoProbe?.Line);
    }

    /// <summary>HCG Store refuses a token issued to an application that is not allowed to publish with this message.</summary>
    private static bool ClientRefusal(StoreProbe p) => p.Status == 401 && (p.ErrorMessage?.Contains("not allowed to publish", StringComparison.OrdinalIgnoreCase) == true || p.ErrorMessage?.Contains("another client", StringComparison.OrdinalIgnoreCase) == true);

    private static StoreCheck ClientCheck(string? token, StoreDiagnosticsContext ctx, StoreProbe? whoProbe, StoreWhoAmI? who, bool isStore)
    {
        const string Title = "Executable Maker client authorization";
        var own = ctx.Connect.ClientId;
        var issued = HcgConnectConfiguration.ForEnvironment(ctx.Connect.Environment).ClientId;
        var tokenClient = token is null ? null : IdTokenValidator.PeekClaims(token) is { } cl ? IdTokenValidator.Str(cl, "client_id") : null;
        var reg = $"{ctx.Connect.RegistryKey} (client {own})";
        if (string.IsNullOrWhiteSpace(own))
            return new("client", Title, StoreCheckState.Failed, $"This build has no HCG Connect client for {ctx.Connect.Environment}.", "Use the production build, or import the issued client configuration on the Account page.", null, "you");
        if (tokenClient is not null && tokenClient != own)
            return new("client", Title, StoreCheckState.Failed,
                $"Your sign-in token was issued to another application's client ({tokenClient}), not Maker's ({own}). Maker uses only its own registration, never HCG Store Manager's or HCG Store's.",
                "Sign out and sign in again from Maker's Account page.", null, "you");
        var differs = issued is { Length: > 0 } && own != issued
            ? $" This differs from the client issued for this build ({issued}); an imported configuration is in use."
            : "";
        if (whoProbe is not null && ClientRefusal(whoProbe))
            return new("client", Title, StoreCheckState.Failed, $"HCG Store does not accept Maker's client {own}: {whoProbe.ErrorMessage}",
                $"An HCG Store administrator adds {own} (HCG Executable Maker, {ctx.Connect.RegistryKey}) to HCG_STORE_PUBLISHING_CLIENT_IDS. The client ID itself stays as HCG issued it.", whoProbe.Line, "HCG Store administrator");
        if (who is not null)
            return new("client", Title, differs.Length > 0 ? StoreCheckState.Warning : StoreCheckState.Passed,
                $"HCG Store accepted a token issued to Maker's dedicated registration {reg} (signed in via {who.Via}).{differs}", null, whoProbe!.Line);
        return new("client", Title, token is null || !isStore ? StoreCheckState.NotChecked : StoreCheckState.Warning,
            $"Maker uses its own {(ctx.Connect.UsesBuiltInClient ? "built-in" : "imported")} registration {reg}.{differs} HCG Store's acceptance {(token is null ? "is checked after you sign in" : !isStore ? "was not checked (not an HCG Store server)" : "could not be checked")}.", null, whoProbe?.Line);
    }

    private static T? Parse<T>(StoreProbe p) where T : class
    {
        if (p.Body is null) return null;
        try { return JsonSerializer.Deserialize<T>(p.Body, HcgStoreClient.Json); } catch (JsonException) { return null; }
    }

    private static string StoreLine(StoreProbe health, StoreProbe? who) => who is { Status: not 200 } w && w.ErrorCode == "publishing_disabled" ? w.Line : health.Line;

    private static StoreCheck Skipped(string id, string title, string why) => new(id, title, StoreCheckState.NotChecked, $"Not checked: {why}.");

    private static string? Explain(StoreProbe p) => (p.Status, p.ErrorCode) switch
    {
        (401, _) => "HCG Store did not accept your sign-in: see HCG Connect authentication and the client authorization.",
        (403, _) => "HCG has not granted this account the publishing permission.",
        (404, "publishing_disabled") => "Publishing is switched off on the server.",
        ( >= 500, _) => "The Store failed; try again later. This is not a refusal.",
        _ => null,
    };
}
