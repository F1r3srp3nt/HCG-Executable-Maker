# Distribution Studio — production HCG Store compatibility

What HCG Executable Maker's Distribution Studio (the **Ship to Store** page and `--ship-to-store`) does against
production HCG Store (`https://store.hakmicapitalgroup.com`). This is based on what production answers and what HCG
Store main contains, not on assumptions. Maker reads the Store's capabilities on every connection. An operation the
Store does not offer stays disabled and is explained.

## What production is (27 Nov 2026)

| Evidence | Value |
|---|---|
| `GET /api/v1/health` (public) | `status ok` · `mode production` · `hcgConnect configured` · `entitlementAuthority hcg-ecosystem` · `payments disabled` · `protectedDownloads free-testing-only` · `anonymousFreeProducts ["hcg-mail"]` · **`publishing enabled`** |
| Deployed contract | at least Store #30. `anonymousFreeProducts` first appears in #30, and #30 contains #27 (release lifecycle, per-target current releases) and #28 (Distribution Studio API: official product metadata, descriptive draft products, publication requests, recorded requests) |
| Production configuration (Store main `web-hcg-store/fly.toml`) | `HCG_STORE_PUBLISHING_CLIENT_IDS = fb9b6edf-2bfc-4b5e-a354-dbb1af697029` (Maker's issued client; the only publishing client) · `HCG_STORE_PUBLISHER_CENTRAL_ID = store.publish` · `HCG_STORE_RELEASE_APPROVER_CENTRAL_ID = store.release.approve` · `HCG_STORE_RELEASE_PUBLICATION_CENTRAL_ID = store.release.publish` |
| Public release resolution for `hcg-mail` | HCG Mail `1.0.0-beta.1` (build `1.0.0.0`), channel beta, Windows x64, published. Platforms: windows confirmed; ios, ipados and macos in development |
| Signed-in endpoints (`/publishing/me`, `/capabilities`, `/products`, `/manager`) | `401 sign_in_required` without a sign-in, as expected. Read on the Mac in the physical test |

## What Maker does against it

| Capability | On production | How |
|---|---|---|
| Official product metadata | Yes (`productMetadata`) | `GET /publishing/products`: name, summary, category, status, platforms, official icon/cover, current release, product URL |
| Product catalogue | Yes | Products HCG authorises for the account (`store.publish` on `hcg.store`), with search, filters and Refresh |
| Private draft creation | Yes | `POST /publishing/releases`. Idempotent: a retry continues the same draft |
| Artifact upload | Yes | Resumable chunks to a Store-issued URL. HCG Store recomputes SHA-256 and checks format and kind |
| Release Passport attachment | Yes | `POST /artifacts/:id/passport`. HCG Store matches it to the stored bytes |
| Version comparison | Yes (in Maker) | From the product's history: duplicates refused, downgrades and identical bytes flagged, per-target current release read |
| Publication request | Yes | "Make current / supersede" is sent as `supersedePreviousOnPublish` (and `publicationRequest`). HCG Store applies it only when a person publishes |
| Current release resolution | Yes | Per target (product, edition, platform, architecture, channel) from release `targets[].current`, and the public release resolution |
| Disable / Enable (unpublished) | Offered from Maker | HCG Store's `/disable` and `/enable` endpoints, with a reason, the typed version, a confirmation dialog and `If-Match` revision. HCG Store checks HCG's permission |
| Supersede / Restore / re-enable a published release / Revoke | Not from Maker (browser only) | Maker explains and opens the release in HCG Store's review page. A person with `store.release.publish` decides |
| Approve / Publish | Not from Maker (browser only) | Never automatic. Separation of duties is enforced by HCG Store, unless a founder exception is recorded |

## Product-specific restrictions HCG Store enforces (and Maker respects)

- **Releases are immutable once published.** Files can be added only before publication. Maker adds files only to an
  open draft. For a submitted or published release it prepares a new build (Add to Listed Product).
- **Version + build is unique per product and edition** (`version_exists`). Maker's comparison refuses duplicates
  before uploading.
- **Supersede is per target.** Publishing a macOS release never touches Windows, ARM64 never touches x64, beta never
  touches stable. The edition-wide scope is refused by HCG Store.
- **HCG Mail is an anonymous free product.** Its published Windows release is downloadable without an account.
  Uploading a new HCG Mail draft from Maker does not change it. Nothing becomes current or public until a person
  publishes in HCG Store.
- **Do not alter the published HCG Mail 1.0.0-beta.1 Windows release** from Maker. The physical test creates only a
  separate private draft.

## Session and credential isolation

- **Separate stored sessions per environment:** production `session`, development `session-development`, local TEST
  identity server `session-local-test`.
  - Each is bound to its client ID and, since this release, to its issuer.
  - A stored session from another client or issuer is deleted and its refresh token is never sent.
- **Signing out** deletes only the current environment's session.
- **A sign-in goes only to a Store of the same kind** (`StoreSessionPolicy`): a production sign-in never to a local
  TEST Store, a TEST sign-in never to the production Store.
- **Permission is decided by HCG Store on every request.**
  - Maker's cached decisions are cleared when the account changes.
  - Answers about a previous account are discarded.
  - A revoked `store.publish` refuses the next upload (acceptance test 16b).
- **Unauthorised accounts receive HCG Store's denial** (acceptance test 16, `not_publisher`).
- **CI cleanup on macOS** removes only the TEST entry (`session-local-test`), never `session` (production).
