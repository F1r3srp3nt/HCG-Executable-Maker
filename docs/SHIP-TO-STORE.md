# Ship to Store

**Ship to Store** (sidebar, and "Ship to Store…" on every artifact) uploads a release to HCG Store
(`store.hakmicapitalgroup.com`) as a **private draft**. HCG Store's server side is in the `F1r3srp3nt/store` repository
(`web-hcg-store/server/src/publishing.ts`, documented in `web-hcg-store/docs/PUBLISHING.md`).

## Who decides what

- **HCG Connect** identifies you: Ship to Store uses the session from the Account page; the app holds no Store or storage
  credentials.
- **HCG** decides whether you may publish or approve (central permissions), and who owns a product (entitlements).
  HCG Store has no roles of its own; Executable Maker has none either.
- **A person approves** in HCG Store, in a browser. The app can submit for review but never approves or publishes, and
  never submits anything to TestFlight or another store.

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
   HCG Store recomputes SHA-256 and checks the format and archive safety; the result is shown per file. Then
   **Submit for review…** and **Open review in HCG Store**.

## Command line

```
HCGExecutableMaker --ship-to-store [--sign-in] --store https://store.hakmicapitalgroup.com \
  --product <id> [--edition <id>] [--draft-product "Title"] --release-version 1.2.0 --build-number 42 \
  --file app.ipa --file app-win.zip [--note "…"] [--distribution paid] [--eligibility private] [--hcg-repo] \
  [--release <id>] [--verify-download] [--submit]
HCGExecutableMaker --ship-to-store --release <id> --withdraw "reason"
```

## Verification

`tools/ci/ship-to-store-e2e.sh <store>/web-hcg-store <out>` runs the whole flow against a local HCG Store in TEST mode
with the mock HCG Connect provider (nothing real is contacted): sign-in through the app's own PKCE code, a private draft
with an IPA, a Windows ZIP and a Linux DEB, a dropped chunk retried, SHA-256 verified by the Store and by downloading
each file back, signed-out and unauthorised downloads refused, manual approval by a different person in a browser
session (self-approval refused), the review page showing the metadata and hashes, an entitled customer's download,
rollback (withdraw stops an issued link), and recovery of an upload killed half-way.

## Not available yet

HCG Store is not live, the central publishing/approver permissions and Executable Maker's HCG Connect client are not
issued, and HCG Repo does not list Store-held packages yet. Until then Ship to Store works only against a test Store.
