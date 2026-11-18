# HCG Connect: remaining setup checklist

> **16 Nov 2026:** HCG issued the production client `fb9b6edf-2bfc-4b5e-a354-dbb1af697029` and it is built in; the steps
> below about obtaining a client are done for production. What remains is the on-device sign-in acceptance (HANDOFF §9).
> See [../FEATURE-ACCESS.md](../FEATURE-ACCESS.md).

State on 11 November 2026, as checked against live HCG. The checks were read-only and used no account.

| What | State | Evidence |
|---|---|---|
| HCG identity service (`qggdyzoydveafkmjgkpq.supabase.co/auth/v1`) | Reachable. Publishes matching endpoints, PKCE S256, public clients and one ES256 key | `--hcg-session check` |
| HCG central Ecosystem API (`/api/public/ecosystem/v1`) | Deployed (401 without sign-in) | `--hcg-session check` |
| Executable Maker API (`/api/public/executable-maker/v1`) | **Not deployed** (HTTP 404/406) | `--hcg-session check` |
| An Executable Maker client ID | 11 Nov: none. **16 Nov: HCG issued the production client `fb9b6edf-2bfc-4b5e-a354-dbb1af697029`; it is built in** (development: none issued) | `HcgConnectConfiguration.Production`, HCG's Owner-screen export |
| Whether HCG has registered an Executable Maker client | 11 Nov: unknown. **16 Nov: registered.** HCG's identity service accepts `fb9b6edf-…` with redirect `http://127.0.0.1:47820/oauth/callback` and PKCE S256 (an unissued ID gets `oauth_client_not_found`) | `--hcg-session check`, no account |
| HCG AI service API | **No documented contract exists** (endpoints, request format, quotas). Requests stay behind the `IHcgAiGateway` adapter, which shows "Not connected" | HCG sources and handoffs seen so far |

## For HCG (central; the website and ERP are not changed by Executable Maker)

1. In ERP › App integrations, register **one public native client** for product `hcg.executable.maker` with the metadata in `hcg-executable-maker-registration.json`:
   - `token_endpoint_auth_method: none` (no secret);
   - grants `authorization_code` and `refresh_token`;
   - PKCE S256;
   - scopes `openid email profile`;
   - exactly these redirect URIs: `http://127.0.0.1:47820/oauth/callback`, `http://127.0.0.1:47821/oauth/callback`, `http://127.0.0.1:47822/oauth/callback`.

   Do **not** reuse the HCG AI website's confidential client. To get the files, use Account › HCG Connect › Export registration request…, or run `HCGExecutableMaker --hcg-session export <folder>`.
2. Put the issued client ID (a UUID) into `hcg-connect.template.json` in place of `<ISSUED_PUBLIC_CLIENT_ID>`, and save it as `hcg-connect.json`. It holds public values only.
3. Grant the account features centrally as HCG decides: `support.submit`, `support.attach`, `ai.use` for `hcg.executable.maker`. The app grants nothing itself.
4. Optional, for support requests: deploy the Executable Maker API (`integration/hcg-website`, see [../HCG-ACCOUNT.md](../HCG-ACCOUNT.md)).
5. Optional, for AI: publish the HCG AI service contract (capability discovery, request and response format, quotas). The app then gets a gateway implementation behind the existing adapter.

## For the user (on each computer)

1. Install **0.3.1 or later** and confirm it: the Account page footer, or `HCGExecutableMaker --version`, shows
   `0.3.1… (commit …)` and `HCG Connect: Production registration (hcg-executable-maker-desktop-production), client fb9b6edf-… built in`.
   A build that shows "Version 0.3.0 · unknown" predates the built-in client; it reports "clientId — missing".
2. Nothing to import. Account › HCG Connect shows *Client ID: fb9b6edf-2bfc-4b5e-a354-dbb1af697029* and
   *Source: built into this build: Production registration (hcg-executable-maker-desktop-production)*.
   (An `hcg-connect.json` is only for a development or test registration.)
3. **Check configuration.** Configuration, Identity server, Signing keys and Client registration must show **Passed**;
   Client registration asks HCG about every callback (ports 47820, 47821 and 47822).
   - "Client registration: Failed … oauth_client_not_found" is HCG's own answer: the client ID is wrong or not registered.
4. **Sign in with HCG.** Your browser opens HCG's sign-in page; Executable Maker never asks for your password. Allow the app; the tab then says you can close it.
5. Quit and reopen the app. It must still show **Signed in**, restored from the OS credential store without the browser.
6. Check the Account page:
   - your identity;
   - your organisations and the selected one;
   - plan and access, as HCG reports them;
   - support and AI permissions, shown separately from sign-in.
7. **Sign out.** The stored session is removed; after a restart the app shows Not signed in.

Steps 4–7 against live HCG are the **verified live sign-in**. They need a person to sign in and consent in the browser, so they cannot be automated without an account. CI runs the same browser flow (PKCE, loopback callback, restart, sign-out) in the packaged macOS app against a MOCK identity server, and checks every package against the live identity server up to the consent redirect.
