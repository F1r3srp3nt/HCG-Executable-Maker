# Distribution Studio — controlled production test on the MacBook Pro

A step-by-step physical test of Distribution Studio (the **Ship to Store** sidebar entry) against production HCG Store
(`https://store.hakmicapitalgroup.com`) on the owner's MacBook Pro (Apple silicon). It uploads **one private draft**
and stops there. Nothing is approved, published, made current, superseded or disabled, and no download link is
created for anyone.

**Never** paste a password, token or key into a chat, a ticket or this document. Sign-in happens in the browser
(HCG Connect, PKCE); Maker never asks for the password. The diagnostics and logs Maker produces are redacted.

## 0 · What production states today (checked from the cloud, signed out, 26 Nov 2026)

| Fact | Live answer |
|---|---|
| `GET /api/v1/health` | `status ok`, `mode production`, `hcgConnect configured`, `entitlementAuthority hcg-ecosystem`, `payments disabled`, `protectedDownloads disabled`, **`publishing uploads`** |
| Publishing routes signed out | `401 sign_in_required` (expected) |
| Public release resolution for `hcg-mail` | name, summary, status `testing`, platforms; **no releases**; no download URLs |
| Distribution Studio Store additions (metadata, publication and availability requests) | **not deployed** (HCG Store branch `claude/distribution-studio-api`). Maker detects this and does not send them |

## 1 · Preparation

1. Install the Maker build to test, from the CI run of the commit on `main` (job **Package (macos)**, artifact
   `hcg-executable-maker-macos`, file `HCG-Executable-Maker-<version>-osx-arm64.app.zip`; check it against
   `SHA256SUMS-macos.txt`), or build it: `dotnet publish src/HCG.ExecutableMaker.App -c Release -r osx-arm64`.
   The app is not notarized; open it with right-click → Open the first time.
2. Confirm the build: **Settings → About** shows the version and commit. Keep the commit in your notes.
3. Have a genuine artifact ready: e.g. the Windows x64 ZIP of HCG Mail from its Windows packaging run, or any
   Maker-built file with its `*.release-passport.json` next to it. Do not use a file you are not allowed to distribute.

## 2 · Sign-in and diagnostics (no upload)

1. **Account → Sign in with HCG**. The browser opens HCG Connect; sign in there. Back in Maker the Account page shows
   your account. The client is Maker's own production client `fb9b6edf-2bfc-4b5e-a354-dbb1af697029`
   (`hcg-executable-maker-desktop-production`); it is built in and is not changed.
2. **Ship to Store** (opens Distribution Studio). Confirm that opening the page did not upload anything (every file
   list is empty, no progress bars).
3. **Check connection**. Expected with today's production:

   | Check | Expected | If not |
   |---|---|---|
   | Server availability | OK | network, DNS or TLS — the line says which |
   | Live API version | OK, v1 (capabilities listed) | Not checked / Warning: the permission is missing (see below) |
   | Publishing mode | OK — `uploads` | `disabled`: an HCG Store administrator sets `HCG_STORE_PUBLISHING=uploads` |
   | Private upload availability | OK | follows the mode and the permission |
   | HCG Connect authentication | OK | sign in again |
   | Executable Maker client authorization | OK | "HCG Store does not accept Maker's client": the Store administrator adds `fb9b6edf-…` to `HCG_STORE_PUBLISHING_CLIENT_IDS` (the client ID itself stays) |
   | Central publisher permission | OK — `store.publish` | HCG grants `store.publish` on `hcg.store` to this account |
   | Authorized catalogue | OK, with HCG Mail and the other products | follows the permission |
   | Release Manager connectivity | OK, audit trail intact | the line shows the server's answer |
   | Public publication | **Off** | expected in `uploads` mode |

4. **Copy diagnostics** and keep the text. Check that it contains no `eyJ…`, `Bearer`, `refresh_token` or cookie.
5. Command-line equivalent (optional, from Terminal):
   `/Applications/HCG\ Executable\ Maker.app/Contents/MacOS/HCGExecutableMaker --ship-to-store --store-status --diagnostics-out ~/Desktop/store-diagnostics.txt`
   (exit 0 ready, 5 not ready, 4 unreachable).

**Stop here** if any check other than Public publication is not OK, and send the copied diagnostics.

## 3 · Catalogue and history (read only)

1. In **Products you may manage**, search "mail", try the status and platform filters, press **Refresh**.
2. Select **HCG Mail**, edition **Windows**. Release history shows what HCG Store holds (today: no release).
3. Note what the catalogue shows. On today's production, icons and categories may be missing: the deployed Store does
   not send them yet. Maker shows initials instead and never invents them.

## 4 · One private upload (the only change made)

1. Operation **Add New Update**.
2. Release configuration: version and build of the artifact (e.g. `0.1.0`, build `1`), channel **Beta**, a release
   note, distribution as HCG decided, eligibility **Private**. Leave **Make current** and **Supersede** unticked. On
   today's Store they are disabled anyway ("This HCG Store does not take publication requests yet").
3. Artifacts: **Choose files…** (or **Latest build** / **Build History** for a Maker build). Check platform,
   architecture, SHA-256, signing and the Release Passport line. The file is labelled **Maker build** or **Selected
   manually**.
4. Version comparison: no **Blocked** line. Discrepancies, if any, are listed and will be recorded in the draft.
5. **Upload private draft**. Watch the progress and the transfer speed. Optional interruption test: switch Wi-Fi off
   for a few seconds during a large upload, then press **Upload private draft** again: it continues the same draft
   and resumes at the byte HCG Store confirmed.
6. Expected: "Verified by HCG Store: SHA-256 … matches", "Release Passport attached" (when a passport is next to the
   file), release status `draft`, and a Release Manager line for the same release.
7. **Open in Store Release Manager**: the browser shows the same release (same ID) on HCG Store's website, signed in.
8. **Synchronization Center → Refresh**: Private drafts 1, Uploaded and verified 1, no failures.

Do **not** press **Submit for review** unless the founder asks for the next step. Submission locks the draft and
waits for an approver. In `uploads` mode approval is off anyway.

## 5 · Clean-up and evidence

- Keep: the copied diagnostics, a screenshot of each section, the release ID and the review URL.
- To remove the test draft: in HCG Store's Release Manager (browser), withdraw it with a reason. Or ask Maker's
  command line: `--ship-to-store --release <id> --withdraw "Production test draft"`. Withdrawing a private draft
  affects nothing public.
- Nothing else was changed: no release published, made current, superseded or disabled, no download offered.

## What this test cannot show today

Approval, publication, make current, supersede and availability requests need HCG Store's `enabled` mode and the
Distribution Studio additions (branch `claude/distribution-studio-api`). Both are the founder's decision and are not
in production. They were verified against the TEST Store (`tools/ci/distribution-studio-e2e.sh`, acceptance tests
1–18).
