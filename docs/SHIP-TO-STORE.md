# Ship to Store

**Ship to Store** (sidebar, "Ship to Store…" on the result of a successful build in New Build and Mobile Build Studio,
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
  --product <id> [--edition <id>] [--draft-product "Title"] --release-version 1.2.0 --build-number 42 \
  --file app.ipa --file app-win.zip [--note "…"] [--distribution paid] [--eligibility private] [--hcg-repo] \
  [--release <id>] [--verify-download] [--submit]
HCGExecutableMaker --ship-to-store --release <id> --withdraw "reason"
HCGExecutableMaker --ship-to-store --store-status [--sign-in] [--store <url>] [--diagnostics-out diagnostics.txt]
```

`--store-status` prints the eleven connection checks below (exit 0 ready to upload private drafts, 5 not ready,
4 unreachable). After each upload the command attaches the file's Release Passport and prints what HCG Store's
Release Manager lists for the release.

## Verification

`tools/ci/ship-to-store-private-e2e.sh <store>/web-hcg-store <out>` (CI job `ship-to-store-private`) runs against the
TEST HCG Store with `HCG_STORE_PUBLISHING=submissions` and the mock HCG Connect provider: a **genuine executable**
(a .NET console program published for Windows x64 from a git commit) is packaged by Executable Maker into a portable
ZIP with its Release Passport; the diagnostics are checked with Ship to Store switched off (every stage named with the
server's reason) and on (ten checks OK, public publication Off, no token in the output); then a private draft, a
resumable upload with a dropped chunk, HCG Store's SHA-256 equal to the file's and to the bytes downloaded back, the
Release Passport attached, submission, and Release Manager listing the release as awaiting approval with an intact
audit trail. Unauthorised uploads are refused (an account without `store.publish`, no sign-in, a forged upload URL,
bytes that differ from the declared SHA-256), approval works in the browser, and public publication is refused
(`publication_disabled`) with nothing public.

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

## Connection diagnostics

**Check connection** on the Ship to Store page (and `--store-status`) runs eleven separate checks instead of one
status, shows HCG Store's actual answer to each request, and says what to do and who does it. It is read-only: no
draft is created and nothing is uploaded. **Copy diagnostics** copies them as text; the copy (like the page) contains no
access token, refresh token, cookie or password (`SecretRedactor.RedactCredentials`). Signed out, opening the page
contacts no Store; **Check connection** still runs the server checks.

| # | Check | How it is decided |
|---|---|---|
| 1 | Store server connectivity | `GET /api/v1/health` (public). DNS, TLS, connection, timeout and "a web page answered" are told apart |
| 2 | API version compatibility | `GET /api/v1/publishing/status`: publishing API version and the oldest client it accepts. A server without the status API (HCG Store 835ac1a, deployed) is named as such |
| 3 | HCG Connect authentication | The Account page's PKCE session, and an access token obtained from it |
| 4 | Desktop client registration | Maker's own registration (`hcg-executable-maker-desktop-production`, client `fb9b6edf-…`), the token's `client_id` equal to it (a token issued to HCG Store Manager or HCG Store's web client is refused), and HCG Store's answer that the client is registered (`HCG_STORE_PUBLISHING_CLIENT_IDS`) |
| 5 | Token validity | Expiry and scopes on this Mac; signature, issuer and expiry as HCG Store verified them (valid / expired / invalid / unverifiable) |
| 6 | Product publishing permissions | HCG's personal grant `store.publish` on `hcg.store`, as HCG Store asked HCG (a purchase or a role never counts) |
| 7 | Authorized product availability | The actual catalogue HCG lets this account submit to (`GET /api/v1/publishing/products`), with editions |
| 8 | Private draft creation | HCG Store's stage state plus the permission (nothing is created to test it) |
| 9 | Artifact upload availability | Same, with the chunk size and file size limit |
| 10 | Release submission | Same; a person approves in HCG Store |
| 11 | Public publication | Shown as **Off** when HCG Store has it switched off. Uploading and leaving a release waiting privately does not need it; Maker never publishes |

### "Publishing switched off" (0.4.0) and private submissions

HCG Store as deployed (835ac1a) has `HCG_STORE_PUBLISHING=disabled`, which switches off private drafts and uploads
along with publication. HCG Store branch `claude/private-submissions` adds `HCG_STORE_PUBLISHING=submissions`: private
drafts, resumable uploads, Release Passports, submission and review work, while public publication answers
`publication_disabled` and nothing is delivered. It also adds the status API these diagnostics read. Turning it on in
production is HCG Store's decision (its `docs/PUBLISHING.md` §6a); nothing on the Mac changes it.

### Products and editions

The product list is HCG Store's actual list of products HCG lets the signed-in account submit to, with their
editions. **Refresh** reloads it and keeps the selected product and edition when they still exist. Existing catalogue
products are selected, never re-created; **Create a draft product** is only for a product that is not in the catalogue.

## Not available yet (blockers)

1. **Private submissions are not switched on in production.** The HCG Store server runs (835ac1a on Fly.io) with
   `HCG_STORE_PUBLISHING=disabled`; every publishing route answers `publishing_disabled`, which 0.4.0 showed as
   "Publishing switched off". The private-submissions mode and status API are on HCG Store branch
   `claude/private-submissions`, not merged or deployed; deploying and setting `HCG_STORE_PUBLISHING=submissions` is
   the founder's decision (HCG Store `docs/PUBLISHING.md` §6a). The last HCG Store deploys failed (Fly API token 401).
2. **Central permission IDs** for publishing and release approval are not issued by HCG, and the entitlement API must
   serve them (`HCG_STORE_PUBLISHER_CENTRAL_ID`, `HCG_STORE_RELEASE_APPROVER_CENTRAL_ID`). The deployed `/check`
   only answers for `feature_key: "download"`, so against live HCG publishing fails closed until then. Diagnostics
   check 6 names this.
3. **HCG Store must allow Executable Maker's client**: HCG issued `fb9b6edf-2bfc-4b5e-a354-dbb1af697029`; the Store's
   `HCG_STORE_PUBLISHING_CLIENT_IDS` must list it (commented in its `fly.toml`). Diagnostics check 4 names this.
4. **Release record (founder question).** Decision D6 keeps releases with HCG Store, so nothing is needed from the
   main HCG backend. Still open: whether a release approved in HCG Store must also be added to the Store's
   founder-reviewed `catalogue/releases.json` before public delivery (`web-hcg-store/docs/PUBLISHING.md` §7).
5. HCG Repo does not list Store-held packages yet (`web-hcg-store/docs/PUBLISHING.md` §5).

Until 1–3 are done Ship to Store works only against a TEST Store; the diagnostics show which of them is missing.
