# Distribution Studio — physical acceptance on the MacBook Pro (M4 Max)

One private draft on production HCG Store (`https://store.hakmicapitalgroup.com`). Nothing is submitted, approved,
published, made current, superseded, disabled or withdrawn. **The published HCG Mail 1.0.0-beta.1 Windows release is
not touched.** Never paste a password, token or key anywhere: HCG Connect signs you in in the browser, and Maker's
diagnostics are redacted. What production offers is described in [PRODUCTION-COMPATIBILITY.md](PRODUCTION-COMPATIBILITY.md).

## A · The app (before signing in)

| # | Do | Expect |
|---|---|---|
| A1 | Download the CI artifact **`hcg-executable-maker-macos`** of the commit on `main`. It contains `HCG-Executable-Maker-<version>-osx-arm64.app.zip`, its `.release-passport.json` and `SHA256SUMS-macos.txt`. | |
| A2 | `shasum -a 256 HCG-Executable-Maker-*-osx-arm64.app.zip` | Equal to the line in `SHA256SUMS-macos.txt` **and** to `artifact.sha256` in the passport. |
| A3 | Unzip, move **HCG Executable Maker.app** to Applications. It is ad hoc signed and not notarized (the passport says so: Gatekeeper rejects a downloaded copy), so clear the download quarantine once: `xattr -cr "/Applications/HCG Executable Maker.app"`, then open it. | The window opens. The sidebar shows the HCG logo. Arm64 native: Activity Monitor › Kind "Apple". |
| A4 | Terminal: `"/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker" --version` | Version and `Source: <40-character commit>` equal to `source.commit` in the passport and to the commit on `main`. |
| A5 | Click **Ship to Store**. | **Distribution Studio** opens. "Sign in required"; nothing is contacted or uploaded. |

## B · The 10 production steps

| # | Do | Expect |
|---|---|---|
| 1 | Distribution Studio is open (A5). | |
| 2 | **Account → Sign in with HCG** (browser). Back in Distribution Studio press **Check connection**. | Ten checks, all **OK**, including **Public publication: OK** (production publishes; by a person, in the browser). **Executable Maker client authorization** names `fb9b6edf-2bfc-4b5e-a354-dbb1af697029`. The diagnostics line **Release management** reads "HCG Store release lifecycle — from apps: releases.disable, releases.enable, …; in HCG Store's browser review: …". Press **Copy diagnostics** and keep the text. |
| 3 | **Products you may manage → Refresh**. | The products HCG authorises your account for, with official names, summaries, categories, statuses and icons where the Store marks them official. |
| 4 | Select **HCG Mail**. | **Release history** lists `1.0.0-beta.1 (build 1.0.0.0)`, published, beta, Windows x64, current on Windows x64 beta. |
| 5 | Operation **Add New Update**. | The note says the current release stays available until a replacement is published. |
| 6 | **Artifacts → Choose files…** Pick a genuine EXE or ZIP you may distribute. Not the published HCG Mail file: use a new build. | Listed as **Selected manually** (or **Maker build** from Build History). |
| 7 | Check the file card. Enter a new version and build (e.g. `1.0.0-beta.2`, a new build), channel **Beta**, eligibility **Private**, a release note. Leave **Make current / Supersede** unticked. | Platform, architecture, size, SHA-256 (compare with `shasum -a 256 <file>`), signing and the Release Passport line. **Version comparison**: current published `1.0.0-beta.1`, no **Blocked** line. Discrepancies, if any, are listed and recorded. |
| 8 | **Upload private draft**. | Progress with transfer speed, then "Verified by HCG Store: SHA-256 … matches". Status `draft`, with a Release Manager line for the same release ID. |
| 9 | **Open in Store Release Manager**. | The browser opens `…/publishing/releases/<id>`: the same release, ID, file and checksum. |
| 10 | **Synchronization Center → Refresh**. | **Private drafts 1**, **Uploaded and verified 1**, **Upload failures 0**; HCG Mail 1.0.0-beta.1 under **Published** and **Current**, unchanged. |

**Stop at step 2** if a check is not OK, and send the copied diagnostics.

## C · Version management (look, do not change)

| # | Do | Expect |
|---|---|---|
| C1 | Operation **Manage Existing Versions**. Select HCG Mail `1.0.0-beta.1`. | Actions: **Disable version** (from Maker, needs reason + typed version + confirmation), **Supersede version** / **Restore previous version** (open HCG Store, a browser decision). **Do not apply any of them.** |
| C2 | Select your new private draft. | Offered actions follow its state; nothing is applied automatically. |

## D · Sessions and permissions

| # | Do | Expect |
|---|---|---|
| D1 | **Account → Sign out**, then reopen Distribution Studio → **Check connection**. | "Not signed in"; no upload possible; the draft is unchanged in HCG Store. |
| D2 | (Only if a second HCG account without `store.publish` is available) sign in with it and press **Check connection**. | **Central publisher permission: Problem** (`not_publisher`); uploading is refused. Sign back in with your own account afterwards. |

## E · Clean-up

Keep the diagnostics text, the screenshots, the release ID and the review URL. To remove the test draft: in HCG
Store's Release Manager (browser) withdraw or disable it with a reason. It affects nothing public, and the HCG Mail
Windows release stays as it is.

## Version management on each Store contract

Distribution Studio reads what the Store allows from its own capabilities. It does not assume anything, and Ship to
Store is never switched off because of it:

| Store | Disable / Enable | Supersede / Restore | Draft-time "replace current" |
|---|---|---|---|
| Production now (≥ #30: release lifecycle #27 + Distribution Studio API #28) | from Maker, through HCG Store's endpoint (reason + typed version + confirmation; HCG's permission checked by the Store) | in HCG Store's browser review, by a person with the publication permission (Maker opens the release there) | recorded with the draft; applied only when a person publishes |
| A Store without lifecycle operations (drafts and uploads only) | explained: done in HCG Store's Release Manager | explained | not offered (explained) |
| `claude/distribution-studio-api` (proposal, integrated as #28) | recorded request | recorded request | recorded request |

A working public release is never disabled before its replacement is published. The replacement takes over only in
the same step as its publication, and only on its own platform, architecture and channel.
