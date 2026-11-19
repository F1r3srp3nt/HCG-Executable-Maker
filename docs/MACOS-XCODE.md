# Native macOS apps built with Xcode

HCG Executable Maker imports a macOS app's Xcode project (a source ZIP or a folder), builds it with `xcodebuild` on a
Mac, and delivers the project's own `.app`, a ZIP of it, an optional DMG, SHA-256 checksums, the project's licence
files and a build report. It is shown as **Native macOS app — Xcode** in New Build.

Acceptance case: MotionWallpaper 1.0.5 (`MotionWallpaper-master.zip`, commit `ab038f7`, GPL-3.0).

## What happens

1. **Import** — a ZIP is extracted safely (no paths outside the workspace, no links, size limits). A single enclosing
   folder such as `MotionWallpaper-master/` is kept as it is: paths include it and nothing has to be moved.
2. **Detection** (static: nothing is run) — macOS-only `.xcodeproj` files with an application target. For each:
   targets, shared schemes (and the target each one builds), configurations, `MACOSX_DEPLOYMENT_TARGET`, bundle ID,
   version and build, display name, signing (`CODE_SIGN_IDENTITY`, team), `ARCHS`, linked frameworks, languages and UI
   framework (AppKit / SwiftUI), licence files, the asset-catalog app icon, and helper scripts. Several projects or
   app targets are listed under *Project to build*; several schemes and configurations are offered in the panel.
3. **Preflight** — each item with the exact fix:
   - a Mac (on Windows/Linux the project is recognised but marked *Needs a Mac*; nothing is built or verified, and
     HCG does not offer a remote Mac build host in this version);
   - full Xcode selected (on the Toolchains page or with `xcode-select`; the Command Line Tools alone cannot build an
     Xcode app project);
   - `xcodebuild` in that Xcode;
   - a macOS SDK at least as new as the deployment target (MotionWallpaper: macOS 26.0 → Xcode 26 or later);
   - the architectures the SDK builds (from its `SDKSettings.json`);
   - `codesign`.
   If this Mac runs an older macOS than the deployment target, the app builds but cannot be launched here; that is said.
4. **Build** — the imported source is copied into HCG's per-build folder and `xcodebuild` runs on the copy:
   `-project … -scheme … -configuration Release -destination generic/platform=macOS -derivedDataPath <build>
   -resultBundlePath <build> -clonedSourcePackagesDirPath <build> … build`. The imported source and its project
   settings are not modified. The only settings HCG adds are shown before the build and in the report:

   | Setting | Value | Why |
   |---|---|---|
   | `ARCHS` | `arm64` (default on Apple silicon), `arm64 x86_64` (Universal) or `x86_64` | the architecture chosen; Universal is offered only when the SDK builds both |
   | `ONLY_ACTIVE_ARCH` | `NO` | build every architecture in `ARCHS` |
   | `COMPILER_INDEX_STORE_ENABLE` | `NO` | skips Xcode's editor index; no effect on the app |
   | `CODE_SIGN_IDENTITY=-` … | only if *Sign ad hoc to run on this Mac* is ticked | offered when the project signs with a certificate or team this Mac may not have |

   Scripts that ship with the project (MotionWallpaper's `INSTALL.command` builds Debug and opens the app) are never
   run. The log streams live; Cancel stops xcodebuild and everything it started. A failure shows the first actionable
   error (file and line) and the path of the `.xcresult` bundle to open in Xcode.
5. **Output** (in the output folder, `<PRODUCT_NAME>-<version>-osx-arm64` or `-osx-universal`):
   - `MotionWallpaper.app` — exactly as Xcode built it: name, bundle ID, version, icon, resources, entitlements and
     signature. It is verified from the bundle itself: identity, architectures of every Mach-O file, minimum macOS,
     icon, resources, `codesign --verify --deep --strict`, and Gatekeeper's verdict (reported, never changed).
   - `….zip` — `ditto -c -k --keepParent` of the app; checked by expanding it again (identical, signature verifies).
   - `….dmg` (optional) — the app, an **Applications** shortcut and the licence files; `hdiutil verify` and a mount check.
   - `SHA256SUMS.txt`, `LICENSE` (unchanged), `BUILD-REPORT.md` and `build-report.json`.

## Signing, quarantine and identity

- The app keeps the signature Xcode applied with the project's own settings. MotionWallpaper is **ad hoc** signed
  (`CODE_SIGN_IDENTITY = "-"`, no team): it runs on the Mac that built it; on another Mac, Gatekeeper blocks the first
  launch until the person chooses System Settings › Privacy & Security › Open Anyway. It is **not** Developer ID signed
  and **not** notarized; the report says which applies. For a Developer ID build, set the Developer ID identity in the
  project; notarization is a separate step.
- HCG never removes the quarantine attribute and never changes Gatekeeper or other security settings (the project's
  README suggests `xattr -dr com.apple.quarantine` and re-signing — HCG does neither).
- Building with HCG does not rebrand the app: its name, bundle ID, icon and licence notices are the project's. The
  report states this.

## Verification: build and runtime are reported separately

CI job `xcode-macos` (macOS 26 runner, Xcode 26): downloads MotionWallpaper at commit `ab038f7` and checks it file by
file against the uploaded ZIP (`tests/Fixtures/MacXcode/MotionWallpaper-ab038f7.sha256`; no MotionWallpaper source is
stored in this repository), then through the real wizard (headless):

- **Build** — ZIP import → detection → preflight → Release build (Apple silicon) → `.app` + ZIP + DMG, then
  `tools/mac/verify-xcode-app-build.sh` checks the outputs with Apple's tools (PlistBuddy, `lipo`, `vtool`, `codesign`,
  `spctl`, `hdiutil`, `shasum`); a Universal build is checked with `lipo`; the DeskLoop fixture is cancelled during
  `xcodebuild`.
- **Runtime** — `tools/mac/verify-motionwallpaper-runtime.sh`, on the runner's disposable session: launch the app,
  import a generated MP4 through **File › Add Video…** (UI scripting; reported *not verified* if the session refuses
  Accessibility), relaunch so the app starts its desktop renderer, and compare two screenshots to show the video
  playing on the desktop. Lock-screen replacement, the native wallpaper and *Launch at login* are never touched: they
  stay explicit actions in the app.
