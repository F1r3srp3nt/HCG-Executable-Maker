# Distribution Studio — physical production test (MacBook Pro)

One private draft on production HCG Store (`https://store.hakmicapitalgroup.com`). Nothing is submitted, approved,
published, made current, superseded or disabled. Never paste a password, token or key anywhere: HCG Connect signs you
in in the browser, and Maker's diagnostics are redacted.

## Before you start

- **The app.** From the CI run of the commit on `main`, job **Package (macos)**, artifact `hcg-executable-maker-macos`:
  `HCG-Executable-Maker-<version>-osx-arm64.app.zip`, its `.release-passport.json`, and `SHA256SUMS-macos.txt`.
  1. Check the archive with `shasum -a 256 HCG-Executable-Maker-*-osx-arm64.app.zip`; it must equal the line in
     `SHA256SUMS-macos.txt` and `artifact.sha256` in the passport.
  2. Unzip it, move **HCG Executable Maker.app** to Applications, and open it with right-click → Open. The app is
     ad hoc signed, not notarized.
  3. **Settings → About** shows the version and commit.
- **A genuine file.** A Windows `.exe` or `.zip` you are allowed to distribute, ideally with its
  `<file>.release-passport.json` next to it (e.g. from HCG Mail's Windows packaging run).

## The 10 steps

| # | Do | Expect |
|---|---|---|
| 1 | Click **Ship to Store** in the sidebar. | **Distribution Studio** opens with the HCG logo. "Sign in required": nothing is contacted or uploaded. |
| 2 | **Account → Sign in with HCG**. Sign in in the browser, then return to Ship to Store and press **Check connection**. | Ten checks. On today's production all are **OK** except **Public publication: Off** (mode `uploads`). **Executable Maker client authorization** names client `fb9b6edf-2bfc-4b5e-a354-dbb1af697029`. Press **Copy diagnostics** and keep the text. |
| 3 | Look at **Products you may manage**; press **Refresh**; search "mail". | The products HCG authorises for your account, as HCG Store lists them. Icons and categories appear only where the Store sends them (the deployed Store sends none yet; initials are shown instead). |
| 4 | Select **HCG Mail** (or another listed product), edition **Windows**. | **Release history** shows what HCG Store holds for it. |
| 5 | Operation **Add New Update**. | The note says the current release stays available until a replacement is published. |
| 6 | **Artifacts → Choose files…**, then pick the `.exe` or `.zip`. | The file is listed as **Selected manually** (or **Maker build** when taken from Build History). |
| 7 | Check the file card. Type version and build, channel **Beta**, a release note, eligibility **Private**. Leave **Make current / Supersede** unticked. | The card shows platform, architecture, size, SHA-256 (compare with `shasum -a 256 <file>`), signing and the Release Passport line. Version comparison shows no **Blocked** line. Any discrepancy with the passport is listed and goes into the draft's notes. |
| 8 | Press **Upload private draft**. | Progress with transfer speed, then "Verified by HCG Store: SHA-256 … matches" (and "Release Passport attached" if one was next to the file). Status `draft`, with a **Release Manager** line for the same release ID. |
| 9 | Press **Open in Store Release Manager**. | The browser opens `…/publishing/releases/<id>`: the same release, same ID, same files and checksum. |
| 10 | **Synchronization Center → Refresh**. | **Private drafts 1**, **Uploaded and verified 1**, **Upload failures 0**. It is read from HCG Store's Release Manager, not assumed. |

**Stop at step 2** if any check except Public publication is not OK, and send the copied diagnostics:

| Check not OK | Who fixes it |
|---|---|
| Publishing mode | an HCG Store administrator |
| client authorization | an HCG Store administrator adds Maker's client to `HCG_STORE_PUBLISHING_CLIENT_IDS`; the client ID itself never changes |
| Central publisher permission | HCG grants `store.publish` |

Do **not** press **Submit for review**. To remove the test draft afterwards, withdraw it in HCG Store's Release Manager
in the browser. The command line can do the same: `--ship-to-store --release <id> --withdraw "Production test draft"`.

## Version management on this Store

Distribution Studio reads what the Store allows from its own capabilities. It does not assume anything, and Ship to
Store is never switched off because of it:

| Store | Disable / Enable | Supersede / Restore | Draft-time "replace current" |
|---|---|---|---|
| Deployed today (`uploads`, no lifecycle operations) | explained: done in HCG Store's Release Manager | explained: done in HCG Store's Release Manager | not offered (explained) |
| HCG Store main (release lifecycle) | from Maker, through HCG Store's endpoint (reason + typed version + confirmation; HCG's permission checked by the Store) | in HCG Store's browser review, by a person with the publication permission (Maker opens the release there) | recorded with the draft; applied only when a person publishes |
| Proposed `claude/distribution-studio-api` | recorded request | recorded request | recorded request |

A working public release is never disabled before its replacement is published: the replacement takes over only in
the same step as its publication, and only on its own platform, architecture and channel.
