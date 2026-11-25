# Native macOS app — Xcode fixtures

- `DeskLoop/` — a small AppKit app with a compact (one object per line) `project.pbxproj`, a shared scheme, ad-hoc
  signing (`CODE_SIGN_IDENTITY = "-"`, no team), an asset-catalog app icon, AVFoundation/ServiceManagement links, a
  `LICENSE` and an `INSTALL.command` that HCG must never run (it would create `INSTALL-COMMAND-WAS-RUN`). Written for
  HCG's tests (MIT). Used by unit tests and by the CI job `xcode-macos` (cancellation test).
- `MotionWallpaper-ab038f7.sha256` — the SHA-256 of every file in MotionWallpaper at commit
  `ab038f7598f3542e6ba6b5a39afc46034997f007` (github.com/krol44/MotionWallpaper), exactly as in the
  `MotionWallpaper-master.zip` the acceptance case started from (29 files, identical contents). CI downloads that commit's
  archive from GitHub and checks it against this list before building it. **No MotionWallpaper source is stored in this
  repository**; the project is GPL-3.0 and keeps its own name, identity and licence when HCG builds it.
