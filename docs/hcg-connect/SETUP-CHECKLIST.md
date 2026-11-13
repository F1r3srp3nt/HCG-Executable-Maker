# HCG Connect: remaining setup checklist

State on 11 November 2026, as checked against live HCG. The checks were read-only and used no account.

| What | State | Evidence |
|---|---|---|
| HCG identity service (`qggdyzoydveafkmjgkpq.supabase.co/auth/v1`) | Reachable. Publishes matching endpoints, PKCE S256, public clients and one ES256 key | `--hcg-session check` |
| HCG central Ecosystem API (`/api/public/ecosystem/v1`) | Deployed (401 without sign-in) | `--hcg-session check` |
| Executable Maker API (`/api/public/executable-maker/v1`) | **Not deployed** (HTTP 404/406) | `--hcg-session check` |
| An Executable Maker client ID | **None is configured in any build or shipped file.** The packaged app ships no `hcg-connect.json`, and the built-in client ID is empty | Code, packaged build |
| Whether HCG has registered an Executable Maker client | **Unknown.** No client ID was ever issued to this project. Any unissued ID gets `oauth_client_not_found` from HCG's identity service | Live probe with a random UUID |
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

1. Install a build that contains this fix. v0.3.0 predates the import UI; it can only read a hand-placed `hcg-connect.json`.
2. Account › HCG Connect › **Import HCG connection configuration…** and choose the `hcg-connect.json` from HCG.
3. **Check configuration.** Configuration, Identity server, Signing keys and Client registration must show **Passed**.
   - "Client registration: Failed … oauth_client_not_found" is HCG's own answer: the client ID is wrong or not registered. Go back to HCG step 1 or 2.
4. **Sign in with HCG.** Your browser opens HCG's sign-in page; Executable Maker never asks for your password. Allow the app; the tab then says you can close it.
5. Quit and reopen the app. It must still show **Signed in**, restored from the OS credential store without the browser.
6. Check the Account page:
   - your identity;
   - your organisations and the selected one;
   - plan and access, as HCG reports them;
   - support and AI permissions, shown separately from sign-in.
7. **Sign out.** The stored session is removed; after a restart the app shows Not signed in.

Steps 4–7 against live HCG are the **verified live sign-in**. They have not been done: they need the issued client ID and a person to consent in the browser. Every sign-in test so far ran against a mock identity server.
