# Native macOS apps built with Xcode

HCG Executable Maker imports a macOS app's Xcode project (a source ZIP or a folder), builds it with `xcodebuild` on a
Mac, and delivers the project's own `.app`, a ZIP of it, an optional DMG, SHA-256 checksums, the project's licence
files and a build report. It is shown as **Native macOS app — Xcode** in New Build.

Acceptance cases: MotionWallpaper 1.0.5 (`MotionWallpaper-master.zip`, commit `ab038f7`, GPL-3.0), and HCG Offensive
Frontier (private): an Xcode 16 project (objectVersion 77, synchronized folders) with a local Swift package, a shared
scheme whose Test action runs the package's tests, and Debug/Release configurations that differ in bundle ID and App
Sandbox. See *Native build provider* below.

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

## Native build provider (Xcode 16 projects, tests, archives, diagnostics)

The provider builds the project with the installed Xcode and treats it as an Xcode project. A local Swift package
inside it (`Packages/…`) is a dependency of the app: it is not offered as a separate Swift package build.

**Inspection** (static) also reads:

- `.xcworkspace` files next to the project or elsewhere in the import that reference it (`group:`, `container:`,
  `self:`, relative paths). The workspace is built when there is one.
- Xcode 16 synchronized folders (`PBXFileSystemSynchronizedRootGroup`) and the project format version.
- Swift package references: local (in the source; no download; an error when `Package.swift` is missing) and remote
  (URL and version requirement), each target's package products, and the pins in `Package.resolved`.
- Per configuration: bundle ID, signing identity, style and team, entitlements file, App Sandbox
  (`ENABLE_APP_SANDBOX` and the entitlements), hardened runtime, compilation conditions, architectures, and whether
  Info.plist is generated (`GENERATE_INFOPLIST_FILE`, `INFOPLIST_KEY_*`) or a file.
- Asset catalogs and the app-icon images that exist, and each entitlements file with its keys.
- Each shared scheme's Test action (the test targets it runs and their configuration) and its Archive configuration.

**Steps.** Every step is an `xcodebuild` call on a copy of the source in HCG's build folder. HCG adds nothing to the
project and runs none of its scripts.

| Step | Command | Fails the build when |
|---|---|---|
| Discover | `xcodebuild -list -json` | the chosen scheme or configuration is not listed |
| Resolve dependencies (remote packages only) | `-resolvePackageDependencies -clonedSourcePackagesDirPath <build>` | resolution fails (shown as a *dependency* error) |
| Build settings | `-showBuildSettings -json` | — (the product's real name and path come from here) |
| Compile | `build` with the result bundle | xcodebuild fails; the Swift/clang errors are listed with file, line and target |
| Test (optional) | the project's own test targets: `test -only-testing:<target> -destination platform=macOS,arch=<this Mac>`. A local Swift package's test targets: `xcrun swift test --package-path <package> --filter <target>` (see below) | a test fails (XCTest and Swift Testing) |
| Archive (optional, or when *Xcode archive* is chosen) | `archive -archivePath <build>/<scheme>.xcarchive` | archiving fails |

**Local package tests.** When a scheme's Test action runs a local Swift package's test target
(`ReferencedContainer = "container:Packages/…"`), `xcodebuild test` on the project reports "There are no test bundles
available to test." That is what Xcode 26.6 does, with or without HCG (CI evidence on the FieldKit fixture). HCG runs
those targets with that package's `swift test`, from the same Xcode, on the build copy. Its build products go in HCG's
build folder. The project's own test targets still run with `xcodebuild test`.

The `.app` is taken from the build settings' product path and validated (identity, architectures, minimum macOS,
icon, signature). Then it is packaged.

**Outputs:**

| Format | What it is |
|---|---|
| `.app` | the app exactly as Xcode built and signed it |
| ZIP | `ditto -c -k --keepParent` of the app |
| DMG | the app, an Applications shortcut and the licence files |
| PKG | an installer for `/Applications` (pkgbuild/productbuild, no scripts). It is **unsigned**: an Xcode build never signs the installer, so it is for local testing, and Gatekeeper blocks it on other Macs. |
| Xcode archive | the `.xcarchive` (Info.plist, `Products/Applications/<app>`, dSYMs) as a ZIP. It is not exported and not uploaded. |

**Signing Doctor** (read only; `--mac-signing-doctor <App.app>`) classifies the app and every piece of embedded code
(frameworks, dylibs, helpers, XPC services, app extensions, login items). Each one is:

- unsigned;
- ad hoc;
- development (Apple Development);
- Developer ID;
- Developer ID and notarized (Gatekeeper's *Notarized Developer ID*, plus `stapler validate` for the ticket);
- App Store.

It also reports the team, entitlements, App Sandbox, hardened runtime, secure timestamp and `codesign --verify --deep
--strict`. Findings name problems such as unsigned nested code, a team mismatch, ad hoc code inside a Developer ID
app, `get-task-allow` or `cs.*` exceptions. It changes nothing.

Signing with a new identity and notarization are separate, explicitly approved operations. They are never part of a
build, and HCG never edits the project's signing or sandbox settings.

**Reports.** These are written next to `build.log` for every build, and copied to the outputs on success:

- `XCODE-DIAGNOSTICS.md` and `xcode-diagnostics.json` (`hcg.build-diagnostics` v1, schema in
  `docs/contracts/build-diagnostics-v1.schema.json`). They contain:
  - the status (a failed build is reported as failed);
  - compiler errors and warnings with file, line, column and target, plus the source lines around each error;
  - failed targets and the commands xcodebuild listed as failed;
  - dependency, signing, linker and project errors, each named as such;
  - test failures;
  - duration;
  - the complete log path and result bundles.

  Authentication data is redacted. In the app: Results › *Xcode build diagnostics* (View report, Copy report for an
  agent, Open folder). In the CLI, the report is printed when a build fails.
- `native-build-request.json` and `native-build-result.json`: the shared native build-provider contract (below).
- `SIGNING-DOCTOR.txt`.
- `<file>.release-passport.json` for each ZIP, DMG, PKG and Xcode archive (Release Passport v1 build half). It holds:
  - the exact SHA-256 and size;
  - the source commit as Git states it;
  - provider `executable-maker-local`;
  - the Xcode version and macOS SDK;
  - the architecture;
  - `code_signing` from the Signing Doctor, including notarization;
  - the artifact checks and the test result.

  Ship to Store reads it for the exact file and submits a **private draft** only. `repository_signing` is never filled
  by Maker.

**CLI**

```
HCGExecutableMaker --xcode-mac <folder|zip> --inspect-only
HCGExecutableMaker --xcode-mac <folder|zip> --trust [--scheme S] [--configuration Release] [--arch arm64|x86_64|universal]
    [--test] [--archive] [--formats app,zip,dmg,pkg,xcarchive] [--sign-ad-hoc] [--out DIR] [--timeout MIN]
HCGExecutableMaker --mac-signing-doctor <App.app>
```

Exit codes:

| Code | Meaning |
|---|---|
| 0 | built, or inspected |
| 2 | usage error, or not trusted (`--trust` is required to build) |
| 4 | not a buildable macOS Xcode app on this computer |
| 6 | the build failed; the diagnostics report is printed |

The Xcode used is the one selected for HCG (Toolchains page), then `DEVELOPER_DIR`, then `xcode-select`.

**Native build-provider contract.** `docs/contracts/NATIVE-BUILD-PROVIDER.md` describes it. Maker's local provider
needs no GitHub Actions. An HCG Server build host would implement the same request and result.

**Without a Mac.** `docs/templates/maker-native-xcode-build.yml` is a workflow template for the app's own (private)
repository. It runs the same two commands on a macOS runner and uploads the diagnostics report, or the build, as
artifacts that stay private to that repository. Nothing is signed, notarized or published.
