# Feature access: who decides what HCG Executable Maker may do

One shared service, `FeatureAccessService` (`src/HCG.ExecutableMaker.Account/FeatureAccess.cs`), decides every
feature. The UI (Account, Help & Support, My requests, AI assistance, Ship to Store) and the command line
(`--hcg-session features | require <feature>`, `--ship-to-store`) use the same instance and the same rules. The
services that perform privileged remote operations (support requests, HCG AI, Ship to Store) ask it again, fresh,
at the moment they run. Nothing here is a second licensing or permission system: HCG decides, HCG Store decides its
staff permission, and Executable Maker only shows and enforces those answers.

## The features

| Feature (`id`) | Decided by | Central feature key (`hcg.executable.maker`) | When locked |
|---|---|---|---|
| Local builds and packaging (`local.builds`) | Nobody: free, offline | none | Never. No account, network or licence. Projects, builds, settings and history stay on this computer. |
| Project support requests (`support.submit`) | HCG `POST /check` | `support.submit` | Explained by HCG's reason (below) |
| Attach project files (`support.attach`) | HCG `POST /check` | `support.attach` | Explained by HCG's reason |
| HCG AI assistance (`ai.use`) | HCG `POST /check` | `ai.use` | Explained by HCG's reason |
| Ship to Store, publishing (`store.publish`) | HCG Store (which asks HCG for its publisher permission) | none (a staff permission) | "Ask your organisation's HCG administrator". Never a purchase. |

The three central keys are the ones HCG's main site registered for `hcg.executable.maker` on 13 Nov 2026
(main site `docs/executable-maker/desktop-handoff.md`): **catalogued disabled/planned, with no grants**. Until HCG
enables them, HCG answers `feature_unavailable` and the app says "HCG has not enabled this yet" (no purchase, no
administrator). `ai.build_diagnostics` exists centrally but is disabled and unused by this app.

## The contracts (as HCG deployed them)

Base `https://beta.hakmicapitalgroup.com/api/public/ecosystem/v1`, the user's own HCG Connect access token
(`Authorization: Bearer`). Sources: main site `docs/executable-maker/desktop-handoff.md` (13 Nov) and HCG Store
`web-hcg-store/docs/MAIN-SITE-INTEGRATION.md` (16 Nov).

- `GET /me` → `{ api_version: "v1", user: { id, email }, organisations: [{ org_id, slug, name, kind, org_role }] }`.
  The subject must equal the signed-in account; another one is ignored.
- `GET /features?org=<org_id>&product=hcg.executable.maker` → a **map** `features: { key: true }` (only enabled
  features), `plan` (nullable, never inferred free), `licence` (only when exactly one product-wide licence exists),
  `licences[]` (`scope`, `scope_key`, `status`, `license_type`, `starts_at`, `ends_at`), `links`
  (`manage`/`checkout` null today), `revalidate_after_seconds`. 403 `not_org_member` for another organisation.
- `POST /check {org_id, product_id, feature_key}` → `{ allowed, layer, reason, verified_at }` echoing the three
  inputs. `layer`: availability · identity · licence. `reason`: product_unavailable · feature_unavailable ·
  not_org_member · org_inactive · not_licensed · user_not_permitted. No edition field (edition checks, D2, are not
  deployed; Executable Maker's features are not edition-scoped). Expiry and revocation both answer `not_licensed`.

An answer with another `api_version`, another shape, or about another organisation, product or feature is **not
recognised**: access is unknown, never a grant and never a denial.

## How a decision is made

1. **Signed out / not set up:** account features say "Sign in" or "HCG connection not set up". Local builds work.
2. **Organisation:** from `/me`. Several → the person chooses (Account page); none → "HCG licences belong to
   organisations; ask your HCG administrator"; removed from it → "not a member (anymore)".
3. **For display:** `/features` for the chosen organisation, used until `revalidate_after_seconds` (5 minutes by
   default) and then not used at all until re-read. For each account feature that the map does not enable, one
   `/check` is made so the reason can be shown.
4. **Before a privileged remote operation:** a fresh `/check` (or HCG Store's answer for publishing), never a
   cached one. A revocation therefore stops the next operation even inside the 5 minutes, and the UI updates at once.

**Refreshed:** on sign-in, when another account signs in, when the organisation changes, when HCG's answer expires
(timer), on "Check access again", after a recovered outage, and before privileged operations.

**Not permitted vs HCG unavailable:** network errors, 5xx, 429, a missing route or an unrecognised answer are
**HCG unavailable** ("this is not a refusal; try again; local builds keep working") and allow nothing. Only HCG's own
`allowed: false` (or HCG Store's 403) is **not permitted**.

## What the person sees

| HCG's reason | Shown as | What to do |
|---|---|---|
| `not_licensed` (licence layer) | Not permitted: the organisation has no active licence that includes it (none, expired or revoked) | **View HCG Executable Maker in HCG Store** (`https://store.hakmicapitalgroup.com/apps/hcg-executable-maker`) |
| `feature_unavailable`, `product_unavailable` | Not available yet: HCG has not enabled it | Nothing to buy or change |
| `user_not_permitted`, `org_inactive` | Not permitted | Ask your organisation's HCG administrator |
| `not_org_member` | Not permitted: not a member (anymore) | Choose another organisation, or ask its administrator |
| another reason | Shown exactly as HCG sent it | none invented |
| HCG Store 403 (publishing) | Not permitted: a staff permission | Ask your organisation's HCG administrator (**never** a purchase button) |
| outage / unrecognised | HCG unavailable | Try again later |

The Store link is the only purchase-related link, and only for a missing licence. HCG Store sells no Executable
Maker offer yet, so the page shows the product and its status, not a checkout. `links.checkout` from HCG is still
shown only when HCG sends it (it is null today).

## Command line

```
HCGExecutableMaker --hcg-session features [--sign-in]          # every decision, as the Account page shows them
HCGExecutableMaker --hcg-session require <feature> [--sign-in] # ask now: exit 0 allowed, 8 not permitted, 9 HCG unavailable / not available yet, 3 sign-in needed
HCGExecutableMaker --ship-to-store …                           # refuses with exit 8/9 before anything is uploaded
```

## Registration (HCG Connect)

| Registration | Registry key | Client | State |
|---|---|---|---|
| **Production** (default) | `hcg-executable-maker-desktop-production` | `fb9b6edf-2bfc-4b5e-a354-dbb1af697029` (public, no secret) | Issued by HCG 13 Nov 2026, `configured_untested`. Built in; identical to the `hcg-connect.json` HCG's Owner screen exports ([hcg-connect/production.hcg-connect.json](hcg-connect/production.hcg-connect.json)). |
| Development | `hcg-executable-maker-desktop` | none issued | `HCG_EXECUTABLE_MAKER_HCG_ENVIRONMENT=development` selects it; sign-in is off until an issued development file is imported. |

Each registration keeps its own session in the OS credential store (`session`, `session-development`; a local test
identity server uses `session-local-test`), so they never mix. An imported `hcg-connect.json` still overrides the
built-in values.

## Verification

**Automated, against mocks (never HCG):**
- `tests/…/HcgEcosystemTests.cs` (MOCK of the deployed contract): confirmed shapes; unrecognised shapes and answers
  about another organisation are unknown; each `/check` reason and its remedy; Store link only for `not_licensed`;
  staff-only publishing → administrator, asked again each time; local builds free signed out, offline and
  unconfigured; outages (network, 503, 500, 429) are "HCG unavailable" and allow nothing; organisation choice and
  per-organisation decisions; removal from an organisation; no organisation; another account's answer ignored;
  edition licences (another edition never counts; `edition:*` and product-wide do; with two and no named licence,
  none is picked); revocation stops the next operation inside the validity window; expiry; sign-in, account change
  and sign-out; 401; support requests refused before anything is sent.
- `tests/…/HcgConnectConfigurationTests.cs`: the built-in production registration equals HCG's export; development
  has no client and never borrows production; separate session entries; environment selection.
- Command line against `tools/hcg-mock/mock-hcg.mjs` (16 Nov): `require` → allowed 0, `not_licensed` 8 with the Store
  link, `feature_unavailable` 9, revoked licence 8, several organisations 8 (choose), access service down 9, local
  builds 0 during the outage, signed out 3.
- UI driver `--account-signed-in` (headless real app, MOCK HCG): the Feature access card shows free / allowed / not
  permitted with the Store link / staff-only without a purchase.
- `tools/ci/ship-to-store-e2e.sh` (26 checks, TEST HCG Store): an account without the publishing permission is
  refused by the shared check (exit 8, administrator, no Store link) before any release exists.

**Real, against live HCG (16 Nov 2026, no account):**
- `--hcg-session check` with the built-in production configuration and an empty data folder: configuration,
  identity server, signing keys **and client registration passed**. HCG's identity service accepted client
  `fb9b6edf-…` with redirect `http://127.0.0.1:47820/oauth/callback` and PKCE S256 (the sign-in page was not opened).
  The central Ecosystem API answered 401 as deployed. `executable-maker/v1` (support routes) is not deployed.
- Anonymous `GET /me`, `GET /features`, `POST /check`: 401 with HCG's JSON error envelope.

**Not verified (needs a person with an HCG account, on a device):** consent and code exchange with the real client;
the ID-token audience and nonce, the access token's `client_id`/audience; refresh without `offline_access`; whether
the Ecosystem API's `verifyBearer` (`getUser(token)`) accepts a desktop OAuth token (HCG's open blocker); the real
`/me`, `/features` and `/check` answers for an account; revocation and organisation isolation in the ERP;
Keychain/Credential Manager persistence with the real client. See [hcg-connect/HANDOFF.md](hcg-connect/HANDOFF.md) §9.
