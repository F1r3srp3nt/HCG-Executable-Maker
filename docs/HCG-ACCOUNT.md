# HCG account features: sign-in, ERP access, support requests, HCG AI

This document covers HCG Executable Maker's integration with HCG's central systems. Executable Maker uses the existing HCG systems:

| System | Role |
|---|---|
| HCG identity | Supabase Auth on project `qggdyzoydveafkmjgkpq`, behind `beta.hakmicapitalgroup.com/auth` |
| HCG ERP / Executive Control Center | Decides access through App Access |
| HCG website API | Serves Executable Maker's routes |
| HCG AI gateway | `ai.hakmicapitalgroup.com` |

It creates no account system and no role system, holds no secret, and never decides access itself.

**Local builds, packaging and toolchains work without an account.** No documented HCG licensing policy requires otherwise. Sign-in is needed only for support requests and AI assistance.

## Status at a glance

> **HCG Connect handoff for the main HCG site:** [docs/hcg-connect/HANDOFF.md](hcg-connect/HANDOFF.md). It covers the client to issue, the callback URIs, scopes, issuer and audience, and the `hcg-connect.json` schema with a placeholder client ID. It also covers the central Ecosystem API used for organisation, licence and feature access, and the implemented / configured / end-to-end-tested status of each part.

| Part | State |
|---|---|
| Sign in with HCG (browser, PKCE, loopback) | **Implemented and tested against a mock identity server.** It stays switched off ("Not available yet") until Executable Maker's OAuth client is registered (step 1 below). |
| Session storage, restore, refresh, expiry, sign-out, revocation | **Implemented and tested with mocks.** The OS stores were exercised as follows: Windows Credential Manager and the macOS Keychain by the CI test on those runners; libsecret on Linux falls back to a session-only store when no keyring exists. |
| ERP App Access, permissions, organisations | **Implemented** in the app and in the website package. Tested on PostgreSQL with an ERP stand-in. **Not deployed.** Organisation membership has no ERP contract yet (see below). |
| Support requests, uploads, My requests, ERP support desk | **Implemented and tested** (16 package tests and 3 cross-language contract tests). **Not deployed.** |
| HCG AI ("Explain this build error" and three prepared capabilities) | **Not connected.** No gateway contract exists anywhere in HCG's sources. Executable Maker shows "Not connected" and never simulates an answer. |
| Plan, licence, organisation, features (central Ecosystem API) | **Implemented and tested against the MOCK HCG.** Routes are live on HCG and need a token; their response bodies await HCG's confirmation (handoff §6). |
| Packaged macOS app: sign-in, restart, sign-out | **Passed in CI run 47 (9 Nov 2026) against the MOCK HCG** with the real login Keychain: sign-in, two restarts and a GUI start restored the session without the browser; sign-out removed the Keychain item. |
| Importing the issued configuration; connection check | **Implemented.** Account page › HCG Connect: *Import configuration…* (validated; nothing changes on a problem; applied without a restart), *Check connection* (names DNS, TLS, HTTP and identity-server errors), and the registration HCG needs, produced from code. Same in the CLI: `--hcg-session import <file> \| check \| registration`. |
| Account connection states | **Implemented.** The page separates three situations: *Not set up on this computer* (names the missing or invalid fields), *Rejected by HCG* (set only from the identity service's own answer, e.g. `oauth_client_not_found` or `invalid_client`, including a no-browser preflight before sign-in), and *HCG unavailable* (DNS/TLS/HTTP). Actions: Import HCG connection configuration…, Check configuration, Retry connection (re-reads the saved file), Export registration request… (request + template + steps). Remaining setup: [hcg-connect/SETUP-CHECKLIST.md](hcg-connect/SETUP-CHECKLIST.md). |
| Live verification against HCG | **None with an account.** Read-only checks on 9–10 Nov 2026: discovery, JWKS, `ecosystem/v1/*` answering 401. The connection check with an unissued client ID gets `oauth_client_not_found`, so **no Executable Maker client is registered yet**. `executable-maker/v1/*` is not deployed. Signing in needs the issued client ID and a person to consent. |

## 1. Identity (what was verified, and where)

Sources: HCG ONE (`HCG-CARDX-AUTH-INTEGRATION-REPORT.md`) and HCG LifeOS (`docs/HCG-CONNECT-LIFEOS-HANDOFF.md`). LifeOS live-verified these values on 6 Nov 2026. The redirect rules below were read in the Supabase Auth server source (`internal/api/oauthserver`, master on 6 Nov 2026).

| Item | Value |
|---|---|
| Issuer | `https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1` |
| Authorize, token and userinfo endpoints | `…/oauth/authorize`, `…/oauth/token`, `…/oauth/userinfo` |
| JWKS | `…/.well-known/jwks.json` (one ES256 P-256 key) |
| Grant | Authorization code + PKCE S256, public client (`token_endpoint_auth_method: none`), refresh tokens. **No client secret exists or is used.** |
| Scopes | `openid email profile offline_access` |
| ID token | Issued for `openid`. It carries the `nonce` from the authorize request (source: `handlers.go`). |
| Redirect URIs | Registration accepts `http` only for `localhost`, `127.0.0.1` and `::1`, plus custom schemes that have a host. The authorize step matches the URI as an **exact string**, port included. |
| Consent | `https://beta.hakmicapitalgroup.com/.lovable/oauth/consent` ("Connect your HCG account") |
| Sign-out | There is no revocation endpoint for OAuth clients. Sign-out deletes the session on this computer. HCG's API checks on every request that the session still exists. |
| `/auth/v1/user` | Refuses OAuth-client tokens. The website routes therefore verify tokens through PostgREST, never through `getUser`. |

**Flow** (RFC 8252 native app, desktop):
1. Executable Maker starts a listener on `127.0.0.1`, on the first free registered port: 47820, 47821 or 47822.
2. It opens the authorize URL in the **system browser** (never an embedded view). The URL carries a fresh PKCE verifier, `state` and `nonce`.
3. The callback must arrive on the exact registered path with the same `state`, compared in constant time. `error=access_denied` becomes "Not allowed".
4. The code is exchanged with the verifier. The **ID token is verified**:
   - its ES256 signature against the JWKS;
   - `iss`;
   - `aud` = the client ID;
   - `exp` and `iat` (2-minute skew);
   - `nonce`;
   - `sub`.

   userinfo must then report the same `sub`.
5. **Storage:** only the refresh token and the identity (subject, e-mail, name, organisation) are stored, in the OS credential store:
   - Windows Credential Manager;
   - macOS Keychain;
   - Secret Service via `secret-tool` on Linux. Without a keyring the session lasts for this run only, and the app says so.

   The access token stays in memory.
6. **Restore:** at the next start, the app refreshes the stored session. Then:
   - a dead refresh token (`refresh_token_not_found`, `…_already_used`, `session_not_found`, `session_expired`, `invalid_grant`) means **Session expired**;
   - an outage keeps the session, shows **HCG unavailable**, and retries every 2 minutes;
   - a stored session of another client is discarded.
7. **During use:**
   - the session refreshes 60 s before expiry;
   - a 401 triggers one refresh and one retry;
   - a second 401 means **Session expired**;
   - `403 revoked` means **Access withdrawn**, and the session is deleted.

Tokens are never logged. The file log, diagnostics and AI payloads pass through `SecretRedactor`, which masks bearer tokens, JWTs, keys and passwords.

## 2. Access: ERP App Access (the ERP is the authority)

`GET /api/public/executable-maker/v1/access` returns the directory schema of `/api/public/mobile/workspaces`, plus the permissions computed **in the database** from the caller's verified token. Each permission comes from an ERP App Access product:

| Permission | ERP App Access product (PROPOSED id) | Notes |
|---|---|---|
| `support.submit`, `support.attach` | `executable-maker` | |
| `ai.use` | `executable-maker` | Only with `features.ai = true` on the entitlement |
| `support.staff` | `executable-maker-support-desk` | Website session only |

How the server enforces it:
- **Revoked or suspended entitlement:** `403 revoked`.
- **Every protected operation** is checked again on the server: create, upload, submit, reply, withdraw, download, and every staff action. Hiding buttons is not the control.
- **Outages are not denials:** network errors, 5xx, 429 and a missing route are shown as "HCG unavailable" or "not live on HCG yet", never as denied.
- **Re-checks:** the app checks access again every 15 minutes and after any 401/403.

**Organisations:**
- No ERP organisation-membership object was found in any reachable source. The adapter `exm.organisations()` returns none until HCG names it.
- The app shows the organisation selector only when the directory lists more than one.
- The app sends the choice as `X-HCG-Organisation`. The server refuses an organisation the caller is not a member of.

## 3. Support requests

**Entry points:**
- "Request project support" on unsupported inspection results;
- "Request support for this build" on failed builds;
- Help & Support;
- My requests.

**What is sent:**
- **A. Diagnostics only:**
  - inspection summary, missing items and findings;
  - host and app version;
  - toolchain states;
  - for failed builds, the failure, errors and the last 120 log lines.

  Every value is redacted, and the home folder is shown as `~`.
- **B. Diagnostics plus files the user ticks,** as one ZIP under `project/`:
  - **Never sendable:** `.env*` (except `*.example`/`*.sample`), keys, certificates and signing material (`.pem .key .p12 .pfx .keystore .jks .mobileprovision .p8 …`), and credential files (`.npmrc`, `local.properties`, `google-services.json`, …). Symbolic links are never followed or sent.
  - **Excluded:** dependency folders, build output, VCS metadata, databases/logs/dumps and files over 20 MB.
  - **Unticked with the line numbers:** text files the secret scan flags. Ticking one again needs an explicit acknowledgement. The scan is pattern-based, and the UI says it does not guarantee that no secret remains.
- **Before Submit**, the app shows:
  - the destination (HCG Executable Maker support on `beta.hakmicapitalgroup.com`, private storage);
  - the total size and the server's limits;
  - the retention rule.

  Nothing is sent before **Submit**. Importing or building never uploads anything.

**Upload:**
- A short-lived signed URL is issued as the caller, so storage RLS applies.
- The app uploads only to HTTPS on HCG's own hosts.
- Upload progress is shown, and the upload can be cancelled.
- **Retry:**
  - a failed upload asks for a fresh URL and retries up to 3 times;
  - cancellation or failure leaves a **draft** that staff cannot see;
  - "Submit" again resumes the **same** request through `clientRequestId` (duplicate protection).
- **Server checks:**
  - size against `storage.objects` metadata;
  - SHA-256 and ZIP structure: traversal, absolute paths, symlinks, duplicates, encrypted or ZIP64 entries, at most 20,000 entries and 1 GB expanded, bomb ratio;
  - staff downloads are re-inspected before a 5-minute link is issued.
- **Never extracted or executed.** Malware scanning does not exist centrally, so every attachment shows `not_scanned`.
- **Limits:** 50 MB per file, 100 MB per request, 3 files, 10 requests per day.

**Statuses:** HCG ONE's request convention: Draft, Submitted, Under review, More information needed, In progress, Completed, Closed. Its commercial states are left out.

**Retention (PROPOSED: no HCG retention policy exists to reuse; founder approval needed):**
- Attachments are deleted 90 days after the request is completed or closed, and immediately on withdrawal.
- Diagnostics and messages are kept with the request until the account is deleted.

**ERP support desk** (`/erp/executable-maker-support`):
- staff list, view, respond, request more information, change status, and link a build-adapter development task (an ERP task reference or URL);
- inspect and download attachments, then apply retention;
- every view, attachment access, response, status change and task link is written to `exm.audit`.

A request **never** marks a project type as supported.

## 4. HCG AI (not connected)

**The intended architecture:**
- HCG identity supplies the token.
- The ERP grants `ai.use` and quota through entitlement features.
- The HCG AI gateway executes.
- Executable Maker consumes capabilities from the gateway's own capability list.

**What exists now:** the adapter `IHcgAiGateway` and the `NotConnectedAiGateway`, which is used today. The capabilities are:
- `explain_build_error` (first);
- `suggest_build_configuration`;
- `explain_missing_toolchains`;
- `analyse_unsupported_project`.

**Behaviour the tests cover:**
- The payload is minimal and redacted, and the user reviews it.
- Logs are sent as data fields marked `untrusted`.
- An explicit approval is needed for every send.
- Permission denial, quota exhaustion and outages are reported as different errors.
- Answers are suggestions only: nothing is run, changed, installed or published.

**Why it is not connected:** no endpoint, schema, capability discovery, quota or usage-reporting contract for `ai.hakmicapitalgroup.com` exists in any HCG repository. It appears only as a website link, and HCG RePrompter and LifeOS also mark HCG AI as "Not connected". Connecting it needs HCG to publish that contract. A gateway implementation then replaces `NotConnectedAiGateway` in `Composition.cs`.

## 5. Exact external configuration (founder actions)

Nothing below was done automatically. No central HCG security setting was changed, no client was registered, and nothing was deployed.

1. **Register Executable Maker's OAuth client** in Supabase ▸ Authentication ▸ OAuth Apps, or through the registration endpoint as for LifeOS:
   - name `HCG Executable Maker`;
   - **public** client (`token_endpoint_auth_method: none`, no secret);
   - grants `authorization_code`, `refresh_token`;
   - scopes `openid email profile offline_access`;
   - **redirect URIs, exactly:**
     ```
     http://127.0.0.1:47820/oauth/callback
     http://127.0.0.1:47821/oauth/callback
     http://127.0.0.1:47822/oauth/callback
     ```
2. **Give the app its client ID**, either way:
   - ship it as the built-in default (change `ClientId` in `HcgConnectConfiguration.cs`); or
   - place `hcg-connect.json` in the app data folder:
     ```json
     { "contractVersion": "hcg.executable-maker.connect/1", "clientId": "<client id>" }
     ```
   The app refuses a file that contains anything secret-like.
3. **Website package** (`integration/hcg-website/README.md`):
   - copy the routes;
   - apply the migration;
   - `insert into exm.clients (client_id, label) values ('<client id>', 'HCG Executable Maker');`
   - confirm the ERP App Access columns;
   - publish to beta.
4. **ERP ▸ App Access:**
   - register `executable-maker` and `executable-maker-support-desk`;
   - grant the desk product to support staff and `executable-maker` to eligible accounts;
   - set `features.ai` only when AI should be allowed.
5. **Organisations (optional):** name the ERP organisation-membership object. `exm.organisations()` then reads it.
6. **Retention:** approve or change the 90-day attachment rule in `exm.settings`.
7. **Optional cosmetic change:** the consent page's missing-request text says "CardX". LifeOS asked for it to be made generic.

**Live checks after deployment:**
- `curl -i …/api/public/executable-maker/v1/access -H 'X-ExecutableMaker-Contract: hcg.executable-maker/1'` should return `401`.
- Sign in from the app: the consent page opens, then the app shows **Signed in**. Then:
  - Account ▸ Access shows **Granted** for a licensed account;
  - a support request appears in the ERP desk;
  - revoking the entitlement makes the app show **Access withdrawn** on its next request.

## 6. Verification performed (this phase)

| What | How | Kind |
|---|---|---|
| PKCE (RFC 7636 test vector); authorize URL; state, error and code checks; ID token signature, issuer, audience, expiry, nonce and subject; dead and outage refresh paths; loopback listener and busy ports; config secret refusal | `HcgAuthTests` | **mock** identity server (scripted HTTP; test ES256 key) |
| Browser sign-in end to end (scripted browser → real loopback listener → token exchange); restore; expiry; outage then retry; other-client session discarded; sign-out; revocation; not-configured contacts nothing | `HcgSessionTests` | **mock** identity server and browser |
| OS credential store round trip | `HcgSessionTests.The_platform_credential_store_round_trips_a_secret` | real store on Windows/macOS CI runners; this Linux container has no keyring, so the session-only fallback was tested |
| API: 401 refresh-retry, revoked, forbidden, not deployed, outage, quota; App Access states; organisation header; another account's directory ignored | `HcgApiTests` | **mock** website |
| Uploads: progress, signed URL host check, outage leaves a resumable draft, cancellation | `SupportClientTests` | **mock** website |
| File planning (credentials, signing material, dependencies, outputs, flagged secrets, symlinks); archive contents; redaction; diagnostics hide the home folder and secrets | `SupportPlanningTests` | local files |
| AI: not connected, approval required, ERP permission, quota, outage, untrusted payload | `HcgAiTests` | **mock** gateway |
| Database and route: ERP-only permissions, revocation, ended sessions, banned accounts, other OAuth clients, organisations, isolation of other users' requests and attachments (API and storage RLS), intake checks, owner bypass caught at staff download, staff desk and audit, withdrawal, retention purge, rate limit, ZIP inspector | `integration/hcg-website` `npm test` (16) | real migration and handler on **PostgreSQL 16**; Supabase, ERP, PostgREST and Storage **stand-ins** |
| C# client ↔ handler ↔ migration | `HcgContractTests` (3) with `contract-server.mjs` | real code on both sides; **mock** identity tokens |
| UI: unsupported project → actions → support form → Account, My requests, AI | `tools/ui-driver --account-pages` | real app views, headless |
| Existing local builds | full .NET suite (477 passed) | unchanged behaviour |

**Not verified:** anything against live HCG. That covers the identity server with a real Executable Maker client, the deployed routes, the real ERP tables, real Supabase Storage, and the AI gateway.
