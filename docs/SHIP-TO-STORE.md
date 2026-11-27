# Ship to Store — Distribution Studio

The **Ship to Store** sidebar entry opens **Distribution Studio** (see [Distribution Studio](#distribution-studio)
below). **Ship to Store** (sidebar, "Ship to Store…" on the result of a successful build in New Build and Mobile Build Studio,
and on every artifact) uploads a release to HCG Store
(`store.hakmicapitalgroup.com`) as a **private draft**. HCG Store's server side is in the `F1r3srp3nt/store` repository
(`web-hcg-store/server/src/publishing.ts`, documented in `web-hcg-store/docs/PUBLISHING.md`).

## Who decides what

Ship to Store is the staff-only feature `store.publish` of the shared feature-access service
([FEATURE-ACCESS.md](FEATURE-ACCESS.md)): HCG Store decides it (asking HCG for its publisher permission) when you
connect, before an upload, and before Submit, for the UI and `--ship-to-store` alike. A refusal says to ask your
organisation's HCG administrator; it is never offered for sale.

- **HCG Connect** identifies you: Ship to Store uses the session from the Account page; the app holds no Store or storage
  credentials.
- **HCG** decides whether you may publish or approve (central permissions), and who owns a product (entitlements).
  HCG Store has no roles of its own; Executable Maker has none either.
- **A person approves** in HCG Store, in a browser. The app can submit for review but never approves or publishes, and
  never submits anything to TestFlight or another store.

## Contracts it uses (and the ones that do not exist yet)

| Contract | Where it is | State |
|---|---|---|
| Sign-in, tokens | HCG Connect (OAuth 2.1 + PKCE), Executable Maker's existing account code | Implemented; HCG's production client `fb9b6edf-…` is built in (issued 13 Nov 2026, not yet used for a real sign-in) |
| Who may publish / approve | HCG's entitlement authority, read by HCG Store (`HCG_STORE_PUBLISHER_CENTRAL_ID`, `HCG_STORE_RELEASE_APPROVER_CENTRAL_ID`) | Implemented in HCG Store; the central permission IDs are not issued yet |
| Products and editions | HCG Store catalogue (`catalogue/products.json`) + Store draft products | Implemented |
| Upload, storage, drafts, review, approval | HCG Store publishing API (`web-hcg-store/server/src/publishing.ts`) | Merged (`F1r3srp3nt/store` PR #12) and tested in TEST mode; not deployed (the HCG Store server is not hosted, D1) |
| Release and file records | HCG Store (decision D6 in `web-hcg-store/docs/MAIN-SITE-INTEGRATION.md`: the Store's catalogue and protected storage stay authoritative for releases and files; no central release-approval authority) | Decided. The main site has no release or upload API, and Executable Maker calls no guessed endpoint. |

## Ship to Store after a build

When a build in New Build or Mobile Build Studio succeeds, **Ship to Store…** appears on the result. It opens the page
with a **new release** holding only that build's **verified output files** (verification Passed or Passed with
warnings, still on disk) and the build's version. Folders (portable folders, an iOS Simulator `.app`) and unverified
files are listed as *not offered*, with the reason. Nothing is uploaded until you press Ship.

## What each file is

Executable Maker decides each file's **kind** from its content, and HCG Store decides it again from the uploaded bytes
(a declaration that contradicts the content is refused):

| Kind | Files | How it is labelled |
|---|---|---|
| Installer | `.exe .msi .msix .dmg .pkg .rpm`, Linux `.deb` | by signature: unsigned / signature present but not verified / signed (Windows) / signed and notarized (macOS) |
| Portable app | Windows `.zip`, macOS app `.zip`, `.AppImage` | as installers; a Windows ZIP is judged by its own top-level programs (not vendor-signed runtime helpers such as .NET's `createdump.exe`) |
| iOS device build | `.ipa` | unsigned · development or ad hoc (registered devices only) · in-house · App Store profile (installs only through the App Store/TestFlight, which HCG never submits to) · profile expired · signed without a profile. The embedded profile's CMS signature is verified; "Apple" only when it chains to Apple on this computer |
| Linux app in a ZIP | `.zip` with no `.exe` or `.app` and a Linux (ELF) program inside, e.g. Executable Maker's own `*-linux-x64.zip` | **not accepted**: HCG Store takes Linux apps only as `.deb`, `.rpm` or `.AppImage`. Before 17 Nov 2026 such a ZIP was wrongly declared as a Windows ZIP. Executable Maker cannot build those Linux packages yet, so its own Linux build cannot be shipped through HCG Store |
| iOS Simulator build | Simulator `.app`, or an `.ipa`/`.zip` whose app is built for the Simulator | **not accepted**: it runs only in the Simulator on a Mac and cannot be installed on an iPhone or iPad |
| Jailbreak package | `.deb` with an `iphoneos-*` architecture | jailbroken devices only (Sileo), never an ordinary device |

Only a file that installs on an ordinary device of its platform as it is gets **Installable**; an unsigned, Simulator or
jailbreak file never does. Asking for a public release with such files asks first; HCG Store refuses public approval
of unsigned iOS device builds. Unsupported types (`.apk`, `.aab`, `.tar.gz`, `.xcarchive`, libraries…) are refused
with what to ship instead and the list of accepted types.

## Steps

1. **HCG Store**: the address (https; http only for a server on this computer) and Connect. Shows who you are and
   which products HCG allows you to publish; "No publishing permission" when HCG has not granted it.
2. **Product**: one you may publish (and its edition), or a **draft product** (stays in HCG Store; private or internal
   testing only until HCG adds it to the catalogue).
3. **Release**: version (SemVer), build number (unique: releases are immutable), channel, release notes, compatibility
   notes, distribution (free, paid, subscription, entitlement-gated — prices and licences are HCG's), requested
   eligibility (private, internal testing, public) and, for products mapped to HCG Repo, delivery of `.deb` packages
   through HCG Repo.
4. **Files**, one per platform: Windows `.exe .msi .msix .zip`; macOS `.zip` with an `.app`, `.dmg`, `.pkg` (a `.app`
   folder is zipped with `ditto` on a Mac, keeping its signature); iOS/iPadOS `.ipa` (iPad-only apps are iPadOS);
   Linux `.deb .rpm .AppImage`; jailbroken iOS `.deb`. For each: platform and format from the file's content, SHA-256,
   minimum OS (IPA), package fields (DEB), and the signature checks this computer can run — macOS: `codesign --verify
   --deep --strict`, `spctl --assess`, `xcrun stapler validate`, `pkgutil --check-signature`; Windows:
   `Get-AuthenticodeSignature`. Elsewhere the file is marked *not checked*, never *unsigned* by assumption.
5. **Upload and review**: progress counts only bytes HCG Store confirmed. Interrupted uploads resume where the Store
   stopped; each chunk is retried at most three times (1 s, 3 s, 8 s), then the upload stops with the byte it reached.
   **Retry** continues the same draft: verified files are skipped, interrupted ones resume, rejected ones are replaced.
   Creating a draft is **idempotent**: shipping again with the same product, edition, version, build and details (after
   a crash, a lost answer or a restart, with or without the release ID) continues your existing draft; the same
   version and build with different details is refused and names the draft to continue. No second release is created.
   HCG Store recomputes SHA-256 and checks the format and archive safety; the result is shown per file. Then
   **Submit for review…** and **Open review in HCG Store**.

## Command line

```
HCGExecutableMaker --ship-to-store [--sign-in] --store https://store.hakmicapitalgroup.com \
  --product <id> [--edition <id>] --release-version 1.2.0 --build-number 42 \
  --file app.ipa --file app-win.zip [--note "…"] [--compat-note "…"] [--distribution paid] [--eligibility private] [--hcg-repo] \
  [--make-current] [--supersede-compatible] [--release <id>] [--verify-download] [--submit]
HCGExecutableMaker --ship-to-store --product <id> --add-to-release <release id> --file app-win-arm64.zip     # Add to Listed Product
HCGExecutableMaker --ship-to-store --product <id> --draft-product "Title" [--description "…"] [--category c] [--platform p]… \
  [--registration-out <folder>] [--icon icon.png] [--banner banner.png] [--edition e] [--release-version 0.1.0]
HCGExecutableMaker --ship-to-store --store-catalogue                       # products you may manage, metadata, history
HCGExecutableMaker --ship-to-store --store-history --product <id>
HCGExecutableMaker --ship-to-store --store-compare --product <id> --release-version V --build-number B [--file …]   # exit 6 = HCG Store would refuse
HCGExecutableMaker --ship-to-store --store-request disable|restore|supersede|make-current --release <id> --reason "…" --confirm <version>
HCGExecutableMaker --ship-to-store --release <id> --withdraw "reason"
HCGExecutableMaker --ship-to-store --store-status [--sign-in] [--store <url>] [--diagnostics-out diagnostics.txt]
```

`--store-status` prints the ten connection checks below (exit 0 ready to upload private drafts, 5 not ready,
4 unreachable). Before an upload the command prints the version comparison and refuses a version HCG Store would
refuse (exit 6). After each upload it attaches the file's Release Passport, prints the transfer speed, and prints what
HCG Store's Release Manager lists for the release.

## Verification

`tools/ci/distribution-studio-e2e.sh <store>/web-hcg-store <out>` (CI job `distribution-studio`) runs the Distribution
Studio acceptance tests 1–18 against the TEST HCG Store (HCG Store branch `claude/distribution-studio-api`, **real
catalogue**, so HCG Mail is a listed product) and the mock HCG Connect provider. Genuine executables (a .NET console
program published for Windows x64 and ARM64 from a git commit) are packaged by Executable Maker into portable ZIPs with
Release Passports. The run covers: the authorised catalogue with official metadata; HCG Mail, Add New Update, version
comparison; import, inspection, passport, upload to private staging; an upload **interrupted by killing the app** and
resumed at the byte HCG Store had; exact server checksums and the bytes downloaded back; the same release in Release
Manager from Maker, the website's browser session and HCG Store Manager's client; Windows ARM64 added to the published
1.0.0 as a new revision (the published release unchanged); a private product draft and its registration request;
version history; a supersede request (make current + supersede compatible) and a recorded availability request;
publication before approval failing with 1.0.0 still published; publication by a person superseding only the
compatible x64 release while the ARM64 revision and the beta channel stay published; unauthorised accounts, no sign-in
and forged upload URLs refused; and `uploads` mode (as production) where authorised private uploads work while approval
answers `publication_disabled`. The real app's Distribution Studio page is driven headlessly against the same Store for
the screenshots.

`tools/ci/ship-to-store-e2e.sh <store>/web-hcg-store <out>` (26 checks) runs the whole flow against a local HCG Store in
TEST mode with the mock HCG Connect provider (nothing real is contacted):

- sign-in through the app's own PKCE code; a private draft with an IPA (CMS-signed ad hoc profile), a Windows ZIP and a
  Linux DEB; a dropped chunk retried; SHA-256 verified by the Store and by downloading each file back; kinds and install
  labels from the app and from the Store's review page;
- **unauthorised uploads**: an account HCG has not allowed to publish (through the app: HTTP 403, no release created),
  adding a file to someone else's draft (403), a forged upload URL (403); signed-out and unauthorised downloads refused;
- manual approval by a different person in a browser session (self-approval refused), an entitled customer's download,
  rollback (withdraw stops an issued link);
- **interrupted-upload recovery**: the app is killed (SIGKILL) half-way through an upload; the same command again
  continues the same draft, resumes at the byte the Store confirmed, verifies the file, and no duplicate release exists;
- an iOS Simulator build refused with the reason before any byte is sent;
- **a real build**: New Build builds `tests/Fixtures/HelloAvalonia` (.NET, cross-built for Windows x64) headlessly in the
  real app (UI driver `--ship-build`), **Ship to Store…** on the result offers exactly the verified ZIP (the folder is
  listed as not offered), labelled unsigned; sign-in with HCG Connect; upload to a DRAFT with its review link; shipping
  again continues the same draft. Then an approver opens the draft in HCG Store in a browser session and presses
  *Download to review*: the bytes' SHA-256 equals the build's. A customer gets 404 for the draft, anonymous 401.

## Distribution Studio

The Ship to Store page is **Distribution Studio**: the same publishing API as HCG Store's website and HCG Store Manager
(`/api/v1/publishing/*`), so a release created in Maker appears in Release Manager and on the website with the same
release ID and audit trail, and the other way round. Opening it uploads nothing and changes no release; signed out it
contacts no Store.

| Section | What it does | Where the facts come from |
|---|---|---|
| Connection | Ten checks, Copy diagnostics, the Store's capability flags | `health`, `whoami`, `capabilities`, `products`, `manager` |
| Products you may manage | Name, icon, identifier, category, status, platforms, editions, current release; search, status and platform filters, **Refresh** (asks HCG Store again; no copy is kept) | `GET /publishing/products` (official metadata with the `productMetadata` capability; otherwise HCG Store's public release resolution for summary, status and platforms). Icons only from the Store's own origin |
| Operation | **Add New Update**, **Add New Product**, **Add to Listed Product**, **Manage Existing Versions** | — |
| Release history | Every release of the product with status, channel, platforms and architectures, requests | `GET /publishing/releases?productId=` |
| Version comparison | Current published (the one HCG Store names current, else the newest published), latest private draft, latest local build, proposed version, published platforms and architectures; duplicates (refused), continued drafts, revisions, downgrades, identical bytes already in the Store, platform overlaps. A newer local build never disables anything | `DistributionStudio.Compare` |
| Release configuration | Version, build, channel, notes, distribution, eligibility; **Make this the current release after approval and publication** and **Supersede previous compatible release** | Sent as `publicationRequest` only to a Store with `publicationRequests`; applied by HCG Store only when a person publishes |
| Artifacts | Latest build, Build History, Artifact Vault, files from disk; several files per draft; no rebuild. Each file shows **Maker build** or **Selected manually** | Facts from the file (platform, architecture, format, size, SHA-256, signing, notarization) and its Release Passport; provenance only from a passport of the exact bytes |
| Metadata | Typed values may differ from the files' facts, but each discrepancy (passport version/build/architecture, package version) is shown and recorded in the draft's compatibility notes | `DistributionStudio.Discrepancies` |
| Upload | Inspection, SHA-256, passport attachment, private draft; resumable chunks with progress, **transfer speed**, Cancel, Retry; server verification; **Open in Store Release Manager** | The existing Ship to Store upload protocol (no second one) |
| Synchronization Center | Local artifacts, uploaded and verified, private drafts, pending review, published, current, superseded or withdrawn, upload failures, conflicts | Release Manager's answer and this session's upload results only |

**Add New Product** creates a private product draft (name, identifier, description, category, platforms — descriptive
fields only to a Store that takes them) and writes a **registration request** for HCG
(`<id>-registration-request/request.json` with the proposed edition, initial version, icon and banner and their
SHA-256). It is not uploaded; the central product ID, editions, official artwork and listing are HCG's decisions, and
no access is granted.

**Add to Listed Product** (Windows ARM64, a macOS DMG, a Linux package, a portable ZIP next to an installer): an open
draft takes the files; a submitted, approved or published release is never changed, so a new revision is prepared
(same version, next build) and the earlier release keeps every file.

**Manage Existing Versions** (Disable, Enable, Supersede, Restore previous version) follows the contract HCG Store
states in its capabilities. HCG Store is the authoritative backend:

| HCG Store | How Maker offers it |
|---|---|
| **main — release lifecycle** (`capabilities.operations`) | **Disable** and **Enable** (unpublished) through `POST /publishing/releases/:id/disable` and `/enable`, with a reason, the typed version, a confirmation dialog and `If-Match: "<revision>"`; HCG Store checks HCG's permission. **Supersede**, **Restore** and re-enabling a published release are publication decisions made in HCG Store's browser review: Maker explains this and opens the release there. "Make current / supersede" on a draft is sent as `supersedePreviousOnPublish`. |
| **proposed** `claude/distribution-studio-api` | Recorded requests (`POST …/requests`, typed version) for the people who approve and publish. |
| **deployed today** (drafts and uploads only) | Each action is listed and explained ("done in HCG Store's Release Manager"); nothing is sent. Uploads and drafts work as usual. |

In every case:

- supersede happens only at publication, in the same transaction, so a failed publication supersedes nothing;
- other platforms, architectures and channels are kept;
- files are never deleted;
- a working public release is never disabled before its replacement is published.

**HCG UEM readiness**: the diagnostics include this device's description (OS, architecture, Maker version) marked
"reported, not attested". Maker is not a device trust authority; uploads do not depend on HCG UEM. When HCG issues
device-aware permissions, HCG Store decides them as it decides `store.publish` today.

## Connection diagnostics

**Check connection** (and `--store-status`) runs ten separate checks against the publishing contract as deployed,
shows HCG Store's actual answer to each request, and says what to do and who does it. It is read-only: no draft is
created and nothing is uploaded. **Copy diagnostics** copies them as text; the copy (like the page) contains no access
token, refresh token, cookie or password (`SecretRedactor.RedactCredentials`). Signed out, opening the page contacts no
Store; **Check connection** still runs the public checks. The token is sent only to a server that answered as HCG Store.

| # | Check | How it is decided |
|---|---|---|
| 1 | Server availability | `GET /api/v1/health` (public). DNS, TLS, connection, timeout and "a web page answered" are told apart |
| 2 | Live API version | `GET /api/v1/publishing/capabilities` → `apiVersion` and the capability flags (it needs a publishing permission) |
| 3 | Publishing mode | `health.publishing` (`disabled` · `uploads` · `enabled`), else whoami/capabilities `publishingMode` |
| 4 | Private upload availability | The mode, the Store's `uploads` flag and the permission; chunk size and size limit |
| 5 | HCG Connect authentication | The Account page's PKCE session; HCG Store verifying the token (whoami); expiry on this Mac |
| 6 | Executable Maker client authorization | The token's `client_id` equals Maker's issued client (`hcg-executable-maker-desktop-production`, `fb9b6edf-…`), never HCG Store Manager's; HCG Store accepting it (a refusal names `HCG_STORE_PUBLISHING_CLIENT_IDS`; the client ID itself is never changed) |
| 7 | Central publisher permission | HCG's personal grant `store.publish` on `hcg.store`, as HCG Store asked HCG (capabilities 403 `not_publisher` = not granted) |
| 8 | Authorized catalogue | `GET /api/v1/publishing/products`, with editions and metadata |
| 9 | Release Manager connectivity | `GET /api/v1/publishing/manager`: releases listed and the audit trail state |
| 10 | Public publication | **Off** unless HCG Store states it on. Not needed for private uploads; Maker never publishes |

Live production (checked 26 Nov 2026 without signing in): `health` answers `status ok, mode production, hcgConnect
configured, payments disabled, protectedDownloads disabled, publishing uploads`; every publishing route needs a
sign-in. Checks 5–9 therefore need the physical test on the Mac (see
[DISTRIBUTION-STUDIO-PRODUCTION-TEST.md](DISTRIBUTION-STUDIO-PRODUCTION-TEST.md)).

### Products and editions

The product list is HCG Store's actual list of products HCG lets the signed-in account submit to, with their
editions. **Refresh** reloads it and keeps the selected product and edition when they still exist. Existing catalogue
products are selected, never re-created; **Create a draft product** is only for a product that is not in the catalogue.

## Not available yet (blockers)

1. **Store main is not deployed yet.** Production HCG Store runs `HCG_STORE_PUBLISHING=uploads` (private drafts
   and uploads on; approval and publication off) on the earlier contract. HCG Store main has the release lifecycle
   (#27) and the reviewed Distribution Studio API (#28: official product metadata, descriptive draft products,
   publication requests, recorded requests). It is merged there but not deployed, and deploying is the founder's
   decision. Until then Maker shows names and editions from the product list plus the public release resolution.
   It explains availability changes instead of sending them, and everything else works. Once main is deployed, Maker
   discovers its capabilities and uses them with no Maker change. CI checks M1–M5 already run against Store main.
2. **Signed-in production checks** (client authorization, `store.publish`, catalogue, Release Manager) can only be run
   with a real HCG sign-in on the Mac: see the production test plan. Whether HCG Store lists Maker's client in
   `HCG_STORE_PUBLISHING_CLIENT_IDS`, and whether HCG has granted `store.publish`, is shown by checks 6 and 7.
3. **Release record (founder question).** Decision D6 keeps releases with HCG Store. Whether an approved release must
   also be added to the Store's founder-reviewed `catalogue/releases.json` before public delivery is open
   (`web-hcg-store/docs/PUBLISHING.md` §7).
4. HCG Repo does not list Store-held packages yet (`web-hcg-store/docs/PUBLISHING.md` §5).
