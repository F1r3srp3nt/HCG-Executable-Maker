# HCG website × HCG Executable Maker: drop-in package

The website side of `hcg.executable-maker/1` for the HCG website (TanStack Start on Lovable, Supabase project `qggdyzoydveafkmjgkpq`). It provides:
- Executable Maker's App Access;
- project-support requests with private attachments;
- the ERP support desk.

It follows the LifeOS package's pattern (`hcg-lifeos/integration/hcg-website`).

**Not deployed.** Applying the migration and publishing the routes is the founder's action. The website's source is not in any repository this work could reach.

## Contents

| Path | What it is |
|---|---|
| `supabase/migrations/20261108200000_executable_maker_support.sql` | Schema `exm` and a private storage bucket with policies. The tables are requests, attachments, messages, audit, settings and clients. Every table has RLS on with no direct grants. Authorization lives in `SECURITY DEFINER` functions. |
| `src/lib/executable-maker/api.ts` | Framework-agnostic request handler: routing, contract header, error mapping, signed URLs as the caller, upload inspection. It makes no access decisions. |
| `src/lib/executable-maker/zipInspect.ts` | ZIP central-directory inspection. It never extracts anything. |
| `src/lib/executable-maker/supabase.ts` | PostgREST and Storage calls **with the caller's own token** and the public key. No service-role key. |
| `src/routes/api/public/executable-maker/v1/$.ts` | Server route that passes requests to the handler. |
| `src/routes/erp_.executable-maker-support.tsx` | ERP support desk at `/erp/executable-maker-support`. |
| `test/` | Supabase, ERP App Access, PostgREST and Storage stand-ins on real PostgreSQL. `support.test.mjs` (16 tests) covers access boundaries, upload validation, staff actions, audit and retention. `contract-server.mjs` is for the app's cross-language test. |

## Endpoints

Base `https://beta.hakmicapitalgroup.com/api/public/executable-maker/v1`. Every request needs `Authorization: Bearer <access token>` and `X-ExecutableMaker-Contract: hcg.executable-maker/1`. An organisation can be selected with the optional `X-HCG-Organisation: <id>` header.

**App routes** (the desktop app's OAuth token):

| Method & path | Database function | Purpose |
|---|---|---|
| `GET /access` | `exm_access_directory` | App Access directory, in the schema of `/api/public/mobile/workspaces`. Also returns `permissions`, `organisations` and `limits`. |
| `GET /support/requests` | `exm_support_list` | The caller's own requests. |
| `POST /support/requests` | `exm_support_create` | Idempotent per `clientRequestId`. Returns signed upload URLs. |
| `GET /support/requests/{id}` | `exm_support_get` | Own request: details, diagnostics, attachments, messages. |
| `POST …/attachments/{aid}/upload-url` | `exm_support_upload_target` | A fresh signed upload URL (retry). |
| `POST …/attachments/{aid}/complete` | `exm_support_complete_attachment` (+ `exm_support_reject_attachment`) | The size is checked from storage. The route then checks the SHA-256 and the archive structure. |
| `POST …/submit` | `exm_support_submit` | Only when every attachment was accepted. |
| `POST …/messages` | `exm_support_reply` | 1–4000 characters. |
| `POST …/withdraw` | `exm_support_withdraw` | Closes the request and deletes its attachments now. |
| `POST …/attachments/{aid}/download` | `exm_support_download_target` | Owner's 5-minute signed link. |

**Staff routes** (website session plus the ERP App Access product `executable-maker-support-desk`):

| Method & path | Database function |
|---|---|
| `GET /staff/requests?status=` | `exm_staff_list` |
| `GET /staff/requests/{id}` | `exm_staff_get` (audited, includes the audit trail) |
| `POST /staff/requests/{id}/respond` | `exm_staff_respond`, kind `response` or `more_information` |
| `POST /staff/requests/{id}/status` | `exm_staff_set_status` |
| `POST /staff/requests/{id}/task` | `exm_staff_link_task` |
| `POST /staff/requests/{id}/attachments/{aid}/download` | `exm_staff_attachment_target` (audited). The route re-inspects the file and then issues a 5-minute link. |
| `POST /staff/purge` | `exm_staff_purge_expired` (retention) |

**Errors:**
- `401 unauthorized`
- `403 forbidden` / `403 revoked`
- `404 not_found` (also for other users' requests, so their existence is not revealed)
- `400 invalid` / `400 unsupported_contract`
- `409 conflict`
- `413 too_large`
- `422 unsafe_attachment`
- `429 rate_limited`
- `503 unavailable`

## Who may do what (decided in the database, per request)

- **Session:**
  - The token's session must still exist in `auth.sessions`, and banned accounts are refused.
  - The token is verified by PostgREST. Never use `getUser` here: it refuses OAuth-client tokens.
- **Channel**, from the `client_id` claim:
  - Executable Maker's registered client(s) in `exm.clients`: the `app` channel.
  - No client: `website` (the ERP desk).
  - Any other OAuth client, such as LifeOS or CardX: 403.
- **Permissions:** only from ERP App Access, read by the `exm.entitlement` adapter through `app_effective_entitlements`. The adapter is copied from LifeOS's `access_state`.
  - Product `executable-maker` grants `support.submit` and `support.attach`. It also grants `ai.use` when the entitlement's `features.ai` is `true`.
  - Product `executable-maker-support-desk` grants `support.staff`, through a website session only.
  - A revoked or suspended entitlement answers `403 revoked`.
  - **No role system is created.** Without the ERP functions installed, nobody has access. There are no fallback grants.
- **Organisations:** no ERP organisation-membership object was found in any reachable source.
  - `exm.organisations()` returns none until HCG names the object to read.
  - An unknown `X-HCG-Organisation` is refused.
- **Attachments:**
  - They are stored in the private bucket `executable-maker-support` at `<owner>/<request>/<attachment>`.
  - The insert policy allows only a pending attachment of the caller's own draft.
  - The read policy allows the owner and support staff.
  - The delete policy allows only attachments already marked deleted.
  - There is no public access.
- **Limits:**
  - 50 MB per file, 100 MB per request, 3 files.
  - Archives: at most 1 GB expanded and 20,000 entries.
  - At most 10 requests per account per day.

## Untrusted uploads

- The database checks that the stored object exists and that its size equals the declaration (`storage.objects` metadata).
- The route then checks the SHA-256 and inspects ZIPs without extracting them. It refuses:
  - traversal, absolute paths and backslashes;
  - symbolic links and duplicates;
  - encrypted and ZIP64 entries;
  - more than 20,000 entries or 1 GB expanded;
  - archive-bomb compression ratios.
- An owner could bypass the route and mark their own file accepted through PostgREST, because the database can check size but not content. For that reason **the staff download route inspects the file again** and refuses unsafe files (tested). Staff never receive a link to an uninspected file.
- **Malware scanning: none exists centrally.** Every attachment shows `scan: not_scanned`, and the desk warns before every download. Nothing is ever extracted or executed during intake.

## Retention (PROPOSED: no HCG retention policy exists to reuse)

- Attachments are deleted **90 days after the request is completed or closed**, and **immediately on withdrawal**.
- Diagnostics and messages stay with the request until the account is deleted, because `on delete cascade` from `auth.users` removes them then.
- Retention runs from the desk ("Apply retention") or from a scheduled job that uses a staff session.
- **The founder must approve or change these values** in `exm.settings`.

## Running the tests

They need Node 22 and a local PostgreSQL 16 on `127.0.0.1:5432` (user `postgres`, password in `EXM_TEST_PGPASSWORD`, default `exm-local-test`).

```
npm install
npm test        # 16 tests
```

## Installing in the website (manual, founder)

1. Copy `src/lib/executable-maker/` and the two route files into the website project.
   - The imports use explicit `.ts` extensions. Vite projects normally allow them (`allowImportingTsExtensions`). If not, drop the extension.
   - Add `@supabase/supabase-js` if the site does not already have it.
2. The route needs `SUPABASE_URL` and `SUPABASE_PUBLISHABLE_KEY`, the public key the site already uses. **No service-role key.**
3. Apply the migration to the existing Supabase project.
4. Before relying on it, confirm in the SQL editor that the ERP objects match the adapter:
   - `select * from public.app_effective_entitlements('<account id>')` returns rows with `product_id` (and `features`).
   - `public.app_entitlements` has `employee_id`, `application_id`, `status` and `revoked_at`.

   If a name differs, change only `exm.entitlement`.
5. Register Executable Maker's OAuth client (see `docs/HCG-ACCOUNT.md` in HCG Executable Maker). Then run `insert into exm.clients (client_id, label) values ('<client id>', 'HCG Executable Maker');`.
6. In ERP ▸ App Access, register the products `executable-maker` and `executable-maker-support-desk`.
   - Grant the desk product to the HCG staff who handle support.
   - Grant `executable-maker` to the accounts that may submit requests. Set `features.ai = true` only if AI use should be allowed later.
7. Add "Executable Maker support" (`/erp/executable-maker-support`) to the ERP navigation. Publish to beta.
8. Live check: `curl -i https://beta.hakmicapitalgroup.com/api/public/executable-maker/v1/access -H 'X-ExecutableMaker-Contract: hcg.executable-maker/1'` should return `401`.
