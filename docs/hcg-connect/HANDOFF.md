# HCG Connect × HCG Executable Maker — integration handoff for the main HCG site

Prepared 9 November 2026 for the main HCG project (beta.hakmicapitalgroup.com, ERP ▸ HCG Ecosystem). It covers what HCG must issue and confirm so that the desktop app HCG Executable Maker can sign in with HCG and read organisation, licence and feature access from HCG's central APIs.

**Status labels**
- **LIVE-VERIFIED (9 Nov):** observed read-only on the live HCG systems on 9 November 2026, without an account.
- **CENTRAL GUIDANCE:** taken from HCG's own published handoff for HCG AI (ERP ▸ Integrations, 9 Nov 2026).
- **IMPLEMENTED + TESTED (mock):** built in this repository and tested against mock servers. Nothing has been run against live HCG with an Executable Maker client.
- **NEEDS HCG:** a decision or action only the main HCG site can take.

Nothing in this handoff was registered, issued, applied or published. No HCG setting was changed.

## 1. What HCG needs to issue

**One public OAuth client for a native desktop app.** Register it the way the ERP's App integrations workflow registers clients ("Inspect registrations" → "Issue or reuse login client"). Executable Maker must **never** receive a client secret.

| Registry field | Value |
|---|---|
| Product | `hcg.executable.maker`: the key of HCG Executable Maker in HCG's live product catalog (LIVE-VERIFIED). Confirm it is the `app_applications.product_id`. |
| Integration key (proposed) | `hcg-executable-maker-desktop` |
| Name shown on the consent page | `HCG Executable Maker` |
| Platform | Desktop: macOS, Windows, Linux (one client for all three) |
| Environment | Production. One client; no separate development client is needed. |
| Client type | **Public**: `token_endpoint_auth_method: none`. **No secret.** |
| Grant types | `authorization_code`, `refresh_token` |
| PKCE | **S256 only** (the app never sends `plain`) |
| Redirect URIs | Three exact strings, see §2 |
| Scopes | `openid email profile`, see §3 |
| Access-token audience | `authenticated` (the provider default for HCG users) |
| Logout return URLs | None. There is no end-session endpoint, so the app signs out locally (§4). |
| Required permissions | None for local use. Account features are decided by the central checks in §6. |

**What HCG gives back:** the issued `client_id` (a UUID). It goes into `hcg-connect.json` (§5) or into the app's built-in default. Nothing else is needed. A registration access token, if the provider returns one, must not be passed to the app.

## 2. Callback URIs and how the app handles them

**Register exactly these three** (RFC 8252 loopback redirects for native apps). The provider compares redirect URIs as **exact strings, port included**, so all three must be registered:

```
http://127.0.0.1:47820/oauth/callback
http://127.0.0.1:47821/oauth/callback
http://127.0.0.1:47822/oauth/callback
```

- The provider accepts `http` redirect URIs only for `localhost`, `127.0.0.1` and `::1` (SOURCE-VERIFIED in `internal/api/oauthserver`; the same rule LifeOS hit).
- The app uses `127.0.0.1`, never `localhost`, so the browser cannot resolve the name elsewhere.
- Three ports allow for one being in use. The app takes the first free one, and its authorize request names that exact URI.

**What the app does (IMPLEMENTED + TESTED, mock):**
1. It binds a listener on `127.0.0.1:<port>` only, never on all interfaces, before opening the browser.
2. It opens the authorize URL in the **system browser** (never an embedded web view). The URL carries:
   - `response_type=code`, `client_id` and the exact `redirect_uri`;
   - `scope=openid email profile`;
   - a fresh `state` and a fresh `nonce`;
   - `code_challenge` (S256 of a 32-byte random verifier) with `code_challenge_method=S256`.
3. The listener accepts **one** `GET` on the exact path `/oauth/callback`. Anything else gets 400 or 404, and the listener keeps waiting.
   - `state` is compared in **constant time**. A mismatch ends the attempt, and nothing is exchanged.
   - `error=access_denied` becomes "Not allowed". Any other `error` is reported.
   - The browser gets an HCG-branded page ("You can close this tab…"). The page never echoes the code or any query value, and is served with `Cache-Control: no-store` and `Referrer-Policy: no-referrer`.
4. The listener stops after the first valid callback, on cancel, or after 5 minutes.
5. The app exchanges the code at the token endpoint with `client_id`, `redirect_uri` and `code_verifier`, and **no client authentication**.

## 3. Scopes

`openid email profile` are identity scopes only. **They grant no Executable Maker permission**; access comes from the central checks (§6).

- `openid` is required, because the ID token is what proves who signed in.
- `offline_access` is **not requested**. CENTRAL GUIDANCE: the provider issues refresh tokens on code exchange without it, and HCG asks for minimal scopes.
  - If a code exchange ever returns no refresh token, the app keeps the session for that run only and says so.
  - If HCG would rather Executable Maker request `offline_access`, add it in `hcg-connect.json` (`"scopes"`); no code change is needed.
- No custom scopes. The app does not invent `executable-maker.*` scopes.

## 4. Issuer, audience and the token contract

| Item | Value | Status |
|---|---|---|
| Issuer | `https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1` | LIVE-VERIFIED (OIDC discovery) |
| Authorize | `…/auth/v1/oauth/authorize` | LIVE-VERIFIED |
| Token | `…/auth/v1/oauth/token` | LIVE-VERIFIED |
| Userinfo | `…/auth/v1/oauth/userinfo` | LIVE-VERIFIED |
| JWKS | `…/auth/v1/.well-known/jwks.json`: one EC P-256 key, `ES256` | LIVE-VERIFIED |
| Discovery advertises | `code_challenge_methods_supported: S256, plain`; auth methods `none`, `client_secret_post`, `client_secret_basic`; grants `authorization_code`, `refresh_token`; scopes `openid profile email phone offline_access` | LIVE-VERIFIED |
| **ID token `aud`** | **The issued client ID** | CENTRAL GUIDANCE; app enforces it |
| **Access token `aud`** | **`authenticated`**, with a signed `client_id` claim = the issued client ID | CENTRAL GUIDANCE; app checks `client_id` |
| Revocation / end-session | None advertised | LIVE-VERIFIED |

**What the app verifies (IMPLEMENTED + TESTED, mock):**
- **The ID token:**
  - ES256 signature against the JWKS (cached for 1 hour);
  - `iss` equals the issuer exactly;
  - `aud` contains the client ID;
  - `exp` and `iat`, with 2 minutes of clock skew;
  - `nonce` equals the request's nonce;
  - `sub` is present.
- **Userinfo:** must report the same `sub`.
- **The access token's** unverified claims: when they carry `client_id`, it must equal the app's client ID. The app does not validate access tokens beyond that; HCG's APIs do.

**Renewal:**
- The app refreshes 60 s before expiry, and once more after a 401 (a second 401 ends the session).
- A refresh response may omit a new ID token; the app does not require one. If it omits a new refresh token, the app keeps the previous one.
- A refreshed access token for another `sub` is refused.
- Dead refresh tokens are "Session expired, sign in again": `refresh_token_not_found`, `refresh_token_already_used`, `session_not_found`, `session_expired`, `invalid_grant`.
- Outages (network, 5xx, 429) keep the session, show "HCG unavailable" and retry every 2 minutes.

**Storage:**
- The app stores only the refresh token and the identity (`sub`, e-mail, name, chosen organisation). The access token stays in memory.
- The OS credential store holds them:
  - macOS: Keychain (generic password; service `com.hakmicapitalgroup.executablemaker.hcg`, account `session`);
  - Windows: Credential Manager;
  - Linux: Secret Service through `secret-tool`. Without a keyring the session lasts for that run only, and the app says so.
- A stored session from another client ID is discarded.

**Sign-out:** the app deletes the stored session on this computer and drops the tokens from memory. It promises no global logout, because none exists. HCG's APIs still check on every request that the session exists.

## 5. `hcg-connect.json` (the app's real schema)

- **Location:** the app data folder, which Settings shows as "Data folder".
  - macOS: `~/Library/Application Support/HCG/ExecutableMaker/hcg-connect.json`
  - Windows: `%LOCALAPPDATA%\HCG\ExecutableMaker\hcg-connect.json`
  - Linux: `~/.local/share/HCG/ExecutableMaker/hcg-connect.json`
  - `HCG_EXECUTABLE_MAKER_HOME` overrides the folder.
- **Alternative:** the issued client ID can be built in instead, by changing `ClientId` in `src/HCG.ExecutableMaker.Account/HcgConnectConfiguration.cs`.
- **Schema:** [`hcg-connect.schema.json`](hcg-connect.schema.json) (JSON Schema 2020-12). **Example:** [`hcg-connect.example.json`](hcg-connect.example.json).
- **Minimal file:** only `contractVersion` and `clientId` are required. Everything else defaults to the LIVE-VERIFIED values above.

```json
{
  "contractVersion": "hcg.executable-maker.connect/1",
  "clientId": "<ISSUED_PUBLIC_CLIENT_ID>"
}
```

**Full example** (every value shown is the app's built-in default):

```json
{
  "$schema": "./hcg-connect.schema.json",
  "contractVersion": "hcg.executable-maker.connect/1",
  "clientId": "<ISSUED_PUBLIC_CLIENT_ID>",
  "issuer": "https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1",
  "authorizationEndpoint": "https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/authorize",
  "tokenEndpoint": "https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/token",
  "userinfoEndpoint": "https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/userinfo",
  "jwksEndpoint": "https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/.well-known/jwks.json",
  "redirectUris": [
    "http://127.0.0.1:47820/oauth/callback",
    "http://127.0.0.1:47821/oauth/callback",
    "http://127.0.0.1:47822/oauth/callback"
  ],
  "scopes": ["openid", "email", "profile"],
  "apiBaseUrl": "https://beta.hakmicapitalgroup.com",
  "ecosystemApiPath": "/api/public/ecosystem/v1",
  "productId": "hcg.executable.maker",
  "accountPortalUrl": "https://beta.hakmicapitalgroup.com/client-portal",
  "trustedLinkHosts": ["beta.hakmicapitalgroup.com", "hakmicapitalgroup.com"]
}
```

**What the app refuses** (it then ignores the whole file and shows why):
- Another `contractVersion`. Each HCG app has its own; LifeOS's is `hcg.lifeos.connect/1`.
- Any key that is not in the schema. A typo such as `clientID` would otherwise be silently ignored.
- Any key containing `secret`, `password`, `service_role`, `private` or `apikey`.
- Anything that is not a JSON object.

A `clientId` that is not an issued UUID keeps sign-in **off**. That covers the placeholder, an empty value and the nil UUID. Identity endpoints must be HTTPS; plain `http` is accepted only on `127.0.0.1`/`localhost`, for a local test identity server, and the Account page then says "Local test identity server".

The tests check all of this: `HcgConnectConfigurationTests`, which also require the schema's keys and defaults to match the app exactly.

## 6. Central APIs Executable Maker uses

All calls send `Authorization: Bearer <access token>` to `https://beta.hakmicapitalgroup.com/api/public/ecosystem/v1`.

| Call | Purpose in the app | Status |
|---|---|---|
| `GET /me` | Account and **organisation memberships**, which fill the organisation selector | Route LIVE-VERIFIED (401 without token); response body NEEDS HCG confirmation |
| `GET /features?org=<org_id>&product=hcg.executable.maker` | **Features** allowed in the chosen organisation; also the **plan, licence and management links** (fields requested below) | Route LIVE-VERIFIED (401); response body NEEDS HCG |
| `POST /check` `{ org_id, product_id: "hcg.executable.maker", feature_key }` | Before a protected action: **allowed**, and the **blocking layer** to explain a refusal | Route listed in the ERP; response body NEEDS HCG |
| Error envelope | `{"error":{"code":"unauthorized","message":"…"},"api_version":"v1"}` | LIVE-VERIFIED |

**How the app reads the responses** (IMPLEMENTED + TESTED against a mock of this shape). The field names follow the names HCG's own ERP pages use for the same records: `org_id`, `name`, `feature_key`, `display_name`, `enabled`, `plan_key`, `billing_type`, `license_type`, `starts_at`, `ends_at`, `status`. **NEEDS HCG: confirm or correct them.** Correcting them is a one-file change in the app (`HcgEcosystemClient`).

```jsonc
// GET /me
{ "account": { "id": "<sub>", "email": "…", "display_name": "…" },
  "organisations": [ { "org_id": "…", "name": "…", "role": "owner|admin|member" } ] }   // also accepted: "memberships", "organizations"

// GET /features?org=…&product=hcg.executable.maker
{ "product_id": "hcg.executable.maker", "org_id": "…",
  "features": [ { "feature_key": "support.submit", "display_name": "…", "allowed": true } ],
  // Requested additions, so the app can show plan and subscription state from HCG (not from the app):
  "plan":    { "plan_key": "free|pro|employee", "name": "Pro", "billing_type": "free|consumer_subscription|employee" },
  "licence": { "status": "active|trial|expired|suspended|revoked", "license_type": "…", "starts_at": "…", "ends_at": "…|null" },
  "links":   { "manage": "https://beta.hakmicapitalgroup.com/…", "checkout": "https://beta.hakmicapitalgroup.com/…", "account": "https://beta.hakmicapitalgroup.com/client-portal" } }

// POST /check
{ "allowed": false, "blocking_layer": "organisation|licence|app_access|feature", "message": "…" }
```

**Feature keys to register** for `hcg.executable.maker` (NEEDS HCG):

| Key | Purpose |
|---|---|
| `support.submit` | Send support requests |
| `support.attach` | Attach project files to a support request |
| `ai.use` | AI assistance, which stays "Not connected" until HCG AI publishes its contract |

**Local builds, packaging and toolchains need no feature, plan or account.**

**How the app shows it:**
- **Plan:** `plan.name`. If HCG sends no plan, the app shows "Plan: not reported by HCG".
- **Access status:**
  - Granted;
  - Not granted (with the blocking layer);
  - Licence expired;
  - Suspended or revoked;
  - HCG unavailable. An outage is **never** shown as a denial.
- **Valid until:** `licence.ends_at`.
- **Organisation:** chosen from `/me` when there is more than one.
- **Links:**
  - The app offers `links.manage` and `links.checkout` **only** when HCG sends them, and only as HTTPS links on `beta.hakmicapitalgroup.com` or `hakmicapitalgroup.com`.
  - "Open HCG account" opens `accountPortalUrl` (`/client-portal`).

**Checkout (no proof from the return):**
- HCG currently has no central checkout. The live account page says "Paid plans aren't available yet — nothing here is billed", so the app shows no purchase button until HCG sends a `links.checkout`.
- When the user opens a checkout or management link, the app shows "Waiting for HCG to confirm". It re-reads `/features` when the user comes back to the app, every 15 s for 10 minutes, and on "Check again".
- **The plan changes in the app only when HCG's API reports the new plan or licence.** Returning to the app, or a browser page that says "success", proves nothing and changes nothing. After 10 minutes the app says HCG has not confirmed a change.
- If HCG wants checkout to return to the app, it can redirect to `https://beta.hakmicapitalgroup.com/client-portal`. The app needs no custom return URL.

## 7. What Executable Maker never does

- Hold, request or accept a client secret, a service-role key or an admin credential.
- Create accounts or roles, or copy a website session into the app.
- Decide access itself. The server checks every protected call again, so hidden buttons are not the control.
- Treat a checkout return as payment.
- Require an account for local builds.

## 8. Ready-to-paste request for the main HCG project

> Register HCG Executable Maker as an HCG Connect desktop client. Product `hcg.executable.maker` (confirm it is the registered `app_applications.product_id`), integration `hcg-executable-maker-desktop`, platform desktop (macOS/Windows/Linux), production.
>
> Use the App integrations workflow ("Inspect registrations", then "Issue or reuse login client") to issue **one public client**: `token_endpoint_auth_method none`, **no secret**, grants `authorization_code` + `refresh_token`, PKCE S256, scopes `openid email profile`, access-token audience `authenticated`. Register exactly these redirect URIs: `http://127.0.0.1:47820/oauth/callback`, `http://127.0.0.1:47821/oauth/callback`, `http://127.0.0.1:47822/oauth/callback`. No logout return URL. Record the binding in `eco_app_integrations` with status `configured_untested`; do not mark it connected until the app's acceptance below has passed.
>
> Give me only the issued `client_id` (a UUID). It is not a secret and goes into the app's `hcg-connect.json` (schema in `docs/hcg-connect/hcg-connect.schema.json` of the HCG Executable Maker repository).
>
> For the Ecosystem API, please:
> 1. Confirm the response bodies of `GET /me`, `GET /features?org=&product=` and `POST /check`. Handoff §6 shows the shape the app reads.
> 2. Add `plan`, `licence` and `links` (`manage`, `checkout`, `account`) to the `features` response for a product, or name the endpoint that provides them.
> 3. Register the feature keys `support.submit`, `support.attach` and `ai.use` for `hcg.executable.maker`.
>
> Do not create accounts, grant licences or enable billing as part of this.

## 9. Acceptance after issuance

**On a Mac, with the issued client ID in `hcg-connect.json`, sign in with the packaged app:**
- [ ] The Account page shows "Sign in with HCG". The browser opens the HCG consent page "Connect your HCG account" for "HCG Executable Maker".
- [ ] After consent, the browser tab says "You can close this tab". The app shows **Signed in as \<your e-mail\>**, and Keychain Access lists a generic password for the service `com.hakmicapitalgroup.executablemaker.hcg`, account `session`.
- [ ] Quit and reopen the app: it shows **Signed in** without opening the browser.
- [ ] Plan and access show what HCG reports, or "not reported by HCG", never invented values.
- [ ] Sign out: the Keychain item is gone, and after a restart the app shows **Not signed in**.

**HCG-side checks:**
- [ ] `curl -i https://beta.hakmicapitalgroup.com/api/public/ecosystem/v1/me` returns `401`.
- [ ] Revoking the account's licence or feature in the ERP makes the app show "Not granted" on its next check.

**Then** mark the registry row `verified_production`.

## 10. Status (implemented / configured / end-to-end tested)

**Kinds of verification:**
- **Implemented:** the code exists in this repository.
- **Configured:** set up for live HCG. Nothing is configured until HCG issues the client ID.
- **End-to-end tested:** run as a whole. Here that means against the **MOCK HCG** (`tools/hcg-mock/mock-hcg.mjs`), which is never HCG.

| Part | Implemented | Configured for live HCG | End-to-end tested |
|---|---|---|---|
| Sign-in: system browser, PKCE S256, loopback callback, state, nonce, ES256 ID token, userinfo | Yes | **No**: needs the issued client ID | Unit tests with a mock identity server. Real app build against the MOCK HCG (`--hcg-session sign-in`). Packaged macOS `.app` on CI (`hcg-connect-macos`). |
| OS credential storage | Yes | n/a | macOS login Keychain: packaged app on CI. Windows Credential Manager: unit test on the Windows runner. Linux Secret Service: only the "not reachable → this run only" path (no working keyring here). |
| Restore after restart, renewal (rotated refresh tokens), sign-out | Yes | **No** | Packaged macOS app on CI: two command-line restarts and one GUI start, then sign-out and a restart. Unit tests cover expiry, outages and revocation. |
| Organisations, features, plan, licence from the Ecosystem API | Yes, reading the shape in §6 | Routes live (401 without token); **response bodies unconfirmed** | MOCK HCG only: unit tests, real Account page (UI driver), packaged app |
| Plan-management and checkout links; recheck after return | Yes | **No**: HCG sends no links and has no billing yet | MOCK HCG only: unit tests and real Account page (return without HCG confirmation changes nothing) |
| Live HCG with a real account | n/a | n/a | **Not done.** Needs the issued client and your consent in a browser (§9). |
