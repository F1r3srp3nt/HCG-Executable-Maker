# Changelog

All notable changes to HCG Executable Maker. Format based on [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased] — `feature/hcg-connect`

### Added — HCG Connect ([docs/hcg-connect/HANDOFF.md](docs/hcg-connect/HANDOFF.md))
- **Integration handoff for the main HCG site:**
  - one public desktop client (no secret);
  - the exact loopback callback URIs and how they are handled;
  - scopes, issuer and audiences;
  - the central Ecosystem API calls, and a ready-to-paste request.

  Values are labelled by how they were verified (live discovery and API probes, 9 Nov 2026).
- **`hcg-connect.json` schema** (`docs/hcg-connect/hcg-connect.schema.json`) and an example with a placeholder client ID. Tests keep both identical to what the app accepts:
  - unknown keys and secret-like keys make the app ignore the file;
  - sign-in stays off until the client ID is an issued UUID.
- **Plan and access** on the Account page, from HCG's central Ecosystem API (`/api/public/ecosystem/v1`):
  - plan, access status and valid-until;
  - organisation choice and features;
  - only the links HCG provides, and only HTTPS links on HCG hosts;
  - "Open HCG account" and "Check again".
- **Checkout and plan changes:** the app re-reads HCG after the user returns, every 15 s for 10 minutes, and on demand. **The plan changes only when HCG's API reports it**; returning from a checkout proves nothing.
- **`HCGExecutableMaker --hcg-session status|sign-in|sign-out`:** the account services without the UI. It never prints tokens.
- **`--page <name>`:** opens a page at start.
- **Test tooling:**
  - `tools/hcg-mock/mock-hcg.mjs`: a MOCK HCG (identity server and Ecosystem API) for verification only;
  - UI driver `--account-signed-in`;
  - CI job `hcg-connect-macos`: sign-in, restart persistence and sign-out in the self-packaged `.app` with the real Keychain.

### Added — web → desktop
- **"Prepare desktop app"** in the New Build wizard for Vite + React browser apps:
  - It shows precisely what is unsupported or risky (backend, sign-in, network), with file and line.
  - It previews every generated file and the `package.json` diff.
  - On request it writes a **separate** desktop workspace (the imported project is unchanged), resolves its lockfile with `--ignore-scripts`, and imports it for the existing Electron build.
  - The generated shell serves the production build from `app://bundle` (SPA fallback, base path, traversal guard), with context isolation, sandbox and webSecurity on and Node integration off.
- **Configuration vs. toolchains:** the wizard lists what the *project* lacks separately from what *this computer* lacks, for Electron apps and for browser apps.
- `TextDiff` (readable line diffs); fixture `tests/Fixtures/WebViteDesktop`; `tools/electron-launch-check/launch-check.cjs`; UI driver `--prepare-desktop`; CI job `web-desktop-macos`.

### Added — HCG Connect configuration
- **Account page › HCG Connect:**
  - the configuration in use;
  - **Import configuration…**: validated, refused with the reason, and applied without a restart; a different client's session is removed;
  - **Check connection**: discovery, keys, client registration and API deployment, with specific DNS, TLS, HTTP and identity-server errors;
  - the client registration HCG needs, produced from code.
- `--hcg-session import <file> | check | registration`. UI driver `--import-config`.

### Verified
- CI run 47 (b83fe9c): the packaged macOS `.app` signed in, was restored after two restarts and a GUI start from the login Keychain, and signed out. All against the MOCK HCG.

### Changed
- **Scopes:** sign-in requests `openid email profile`, following HCG's central guidance that `offline_access` is not needed.
- **Product key:** the central product key is `hcg.executable.maker`.
- **Renewal:** it keeps the current refresh token when a refresh returns none. An access token issued to another client is refused.

### Fixed
- **Unsaved sessions:** if the OS credential store refuses the session, the app now says the sign-in lasts for this run only. It used to claim the session was stored.
- **Linux:** the keyring item label is ASCII. `secret-tool` rejected the previous label (it contained an em dash) when no UTF-8 locale was set.
- **Keyring errors:** they are shown without being masked by the redactor, and an unreachable keyring is no longer described as "could not read the saved session".

## [0.3.0] — 2026-11-09 (pre-release)

Native Swift/macOS apps and macOS distribution, Electron desktop apps, guided toolchain setup, and the HCG account
features. HCG itself is still unsigned. HCG sign-in, support requests and AI assistance stay switched off ("Not
available yet" / "Not connected") until the external HCG setup in [docs/HCG-ACCOUNT.md](docs/HCG-ACCOUNT.md) is done.

### HCG account (sign-in, App Access, support requests, AI foundation)

#### Added — HCG account features ([docs/HCG-ACCOUNT.md](docs/HCG-ACCOUNT.md))
- **Sign in with HCG:**
  - the existing HCG identity, through HCG's OAuth 2.1 server (Supabase Auth), with authorization code + PKCE in the system browser and an RFC 8252 loopback callback;
  - state, nonce and ID-token checks (ES256 signature, issuer, audience, expiry), plus a userinfo cross-check;
  - public client, no secret anywhere.
  - It stays switched off ("Not available yet") until the client is registered.
- **Session handling:**
  - stored in Windows Credential Manager, the macOS Keychain or Secret Service; only the refresh token is stored;
  - restore, proactive refresh, expiry, outage-tolerant retry, sign-out and revocation;
  - Account page with organisation selection.
- **ERP App Access:** read from the ERP and enforced on the server. Outages are distinguished from denials.
- **Project-support requests:**
  - diagnostics only, or diagnostics plus files the user selects;
  - default exclusion of credentials, signing material, dependencies and outputs; secret scan with explanations;
  - review before Submit; upload progress, cancellation, resume and duplicate protection;
  - My requests, with HCG staff responses.
- **Website/ERP drop-in package** (`integration/hcg-website`, not deployed):
  - migration with SECURITY DEFINER authorization and a private bucket with RLS;
  - API route and ERP support desk;
  - audit trail and proposed retention;
  - 16 tests on PostgreSQL.
- **HCG AI foundation:** adapter interface, payload review and redaction, permission, quota and outage handling. It shows **Not connected**, because no gateway contract is published.
- **New pages:** Account, Help & Support, My requests, AI assistance. Unsupported-project and failed-build actions added.

#### Security
- Secrets are redacted from the file log, diagnostics and AI payloads (`SecretRedactor`).

### Electron desktop apps

#### Added — Electron desktop apps ([docs/ELECTRON.md](docs/ELECTRON.md))
- **Capability-based Node.js inspection** replaces the "planned for a later milestone" placeholder for every
  `package.json`:
  - each project is classified as an Electron desktop app, a browser app, a Node service/CLI or a library;
  - nested projects are listed and selectable, and build-output copies are ignored;
  - nothing is run during inspection: no npm, no scripts, no JavaScript configs.
- **electron-builder configuration** is read from YAML, JSON, `package.json` "build" or `--config`. Packaging scripts
  are split into shell-free compile steps and the electron-builder flags.
- **Precise gaps** are listed for web-only projects, Electron Forge / `@electron/packager`, yarn/pnpm/bun, missing or
  unbuilt main entries, and scripts HCG cannot follow.
- **Node.js and npm discovery** works from Finder (Homebrew, nvm, Volta, fnm, asdf, /opt). Each candidate is run.
  `engines.node`, `engines.npm` and the `packageManager` pin are validated per project.
- **Build stages:**
  - **Install:** `npm ci`.
  - **Compile:** the project's own script.
  - **Package:** electron-builder with `--publish never`, output in HCG's build folder.
  - Preconditions, streamed logs, cancellation and real exit codes.
- **Artifact discovery and verification:**
  - the `.app`: Info.plist, Mach-O architecture, app.asar package.json and main entry, `codesign --verify`;
  - the zip contents;
  - `hdiutil verify`;
  - the Linux unpacked app.
- **Offered combinations:** only what is implemented. macOS arm64/x64 (`.app`, zip, dmg) on a Mac, the unpacked app on
  Linux. Other electron-builder targets are listed as not run.
- **New Build wizard:**
  - an Electron panel: what it is, what is missing, the stages HCG will run;
  - identity is read from the project's configuration;
  - HCG's re-signing options are hidden.
- **Verification:**
  - Presentation Studio was built through the wizard on Linux and passed its own packaged-app test (15/15);
  - the `electron-macos` CI job builds the arm64 `.app`/zip/dmg, launches it and runs the same test.

#### Security
- `CSC_*` / `WIN_CSC_*` (electron-builder signing certificates and passwords) are removed from every build environment.

### Toolchain discovery and guided setup

#### Fixed — toolchain discovery and setup ([docs/TOOLCHAINS.md](docs/TOOLCHAINS.md))
- **Java:**
  - JDKs are found without the login shell (saved path, JAVA_HOME, Android Studio's bundled JDK, `java_home -V`,
    JavaVirtualMachines, Homebrew, SDKMAN, system folders, then PATH);
  - each one is run, with stdout and stderr both read;
  - the macOS `/usr/bin/java` stub without a JDK, broken shims and JREs without `javac` are rejected with the reason;
  - Browse… / Verify / Use automatic detection.
- **Android SDK:**
  - saved path, environment or the default location;
  - platforms (with `android.jar`) and build-tools reported separately;
  - apksigner, zipalign and aapt2 from one complete build-tools;
  - licence state reported (never accepted by HCG);
  - Browse… / Verify.
- **Xcode:**
  - with the Command Line Tools selected, Xcode and the iOS SDK show **Setup required** with the HCG-only fix, not
    "Missing";
  - installed Xcode versions are shown, and aliases are listed once.
- **New states:** Invalid path, Incompatible version, Setup required. Only validated tools are Ready.
- **Project readiness** in Mobile Build Studio:
  - required vs optional per project, output and signing mode;
  - the JDK is checked against the project's Gradle and AGP;
  - bundletool is required only for APK sets;
  - profiles are not needed for Simulator builds;
  - development certificates never count as distribution readiness;
  - other hosts are explained;
  - Start is blocked only by required items.
- **Builds use the inspected toolchain:** JAVA_HOME, ANDROID_HOME and PATH come from the report, so Finder launches
  work.
- **Toolchains page and dashboard:**
  - groups say what they are for;
  - optional SDKs and other-host tools are shown neutrally;
  - links to the official setup guides.
- `HCGExecutableMaker --toolchain-report` prints the detection result without the UI.

### Native Swift/macOS apps and macOS distribution

#### Added — macOS distribution ([docs/MACOS-DISTRIBUTION.md](docs/MACOS-DISTRIBUTION.md))
- **Xcode discovery** (Toolchains › Xcode):
  - installed Xcode apps from /Applications, ~/Applications, Spotlight and Browse…;
  - diagnosis: not installed, installed but not selected, license, first launch, SDKs, validation failure;
  - Select Xcode / Open Xcode / Recheck / Use system setting;
  - an HCG-only developer directory (`DEVELOPER_DIR` for HCG's processes; `xcode-select` untouched);
  - the build log records the directory used.
- **Signing inventory**:
  - identities from `security find-identity -v`, including installer certificates, classified by type;
  - macOS provisioning profiles (type, App ID, team, expiry, certificates).
- **Evidence-based entitlements**:
  - hardened-runtime and App Sandbox entitlements proposed from the sources;
  - sandbox review items and blockers;
  - the user confirms them; deselected ones are listed as disabled features.
- **Developer ID signing** of Swift apps and DMGs:
  - hardened runtime, secure timestamp, confirmed entitlements;
  - independent checks: signature, certificate/team, runtime, timestamp, entitlements, Gatekeeper.
- **Installer package (.pkg)**:
  - pkgbuild/productbuild, not relocatable, no scripts, OS and architecture checks;
  - unsigned for local testing or signed with Developer ID Installer;
  - inspection by expanding and comparing the payload byte for byte.
- **Mac App Store package**:
  - store-signed copy with embedded profile and sandbox entitlements;
  - Mac Installer Distribution signature;
  - blockers with exact fixes.
- **Explicit steps after a build**:
  - Notarize (notarytool + stapler + Gatekeeper, per container);
  - Test install into ~/Applications (optional launch, then removal);
  - Validate with App Store Connect (altool; no upload).
- **Settings › Apple credentials**: notarytool keychain profile name (with the store-credentials command and a check)
  and App Store Connect API key IDs. Secrets stay in the keychain or key file.

#### Changed
- Packaging states are now Ready / Setup required / Incompatible with this project / Failed / Needs another host. The
  "planned for M0.3/M0.4" placeholders for macOS are gone.
- DMGs are mounted and compared with the built app after creation.
- A build whose artifacts fail verification no longer presents them: the output folder is removed, and the failing
  checks are recorded as build errors.
- Signing & Security reports Developer ID, notarization and the Mac App Store as implemented.

#### Added — native Swift/macOS apps
- **Swift package discovery** (passive, `Package.swift` read as text, never run):
  - packages at the root, inside a ZIP's wrapping folder, and several per repository;
  - build/cache folders ignored;
  - no package pre-selected when there are several;
  - tools version, macOS platform, conditional declarations, GUI entry points and privacy-sensitive APIs.
- **Trusted evaluation**: `swift package dump-package` through the build worker after explicit trust. Its JSON is the
  authoritative product list: executables separate from libraries and test targets; products declared under
  `#if os(macOS)` are included.
- **Toolchain checks**: macOS version, selected developer directory, Swift compiler (vs the package tools version),
  macOS SDK (vs the minimum macOS), codesign and hdiutil. Each gap names its exact fix. The states *Missing toolchain*,
  *Needs another host* and *Unsupported* are kept distinct.
- **Build**: `swift package resolve`, then `swift build --product … --arch …` (arm64, x86_64 or universal), with
  scratch space outside the project, cancellation, timeouts, diagnostics parsing and history.
- **`SwiftAppBundlePackager`**: Swift-specific `.app` layout and Info.plist. The deployment target comes from the
  package / Mach-O, never the .NET defaults. Adds privacy usage keys, resource bundles in `Contents/Resources` and an
  ad-hoc signature.
- **Swift verifier**: Mach-O slices, deployment target, privacy keys, `codesign --verify --strict`.
- **App ZIP** via `ditto`, with an extract-and-re-verify round trip. DMG via the existing packager. CLI products use
  the existing portable folder/ZIP packagers.
- **New Build wizard**:
  - package/product choice, trusted evaluation panel, macOS app options;
  - .NET-only options hidden;
  - macOS Xcode projects explained as not built;
  - Windows/Linux hosts blocked, with the reason.
- `tools/ui-driver`: drives the real wizard headlessly (Avalonia headless + Skia) and saves screenshots.
- CI `swift-macos`: builds Distil.app (+ZIP, DMG), universal Relay.app (+ZIP) and the relay-runner CLI from the
  pinned Oslo-Sprint repo (folder and ZIP imports), then verifies and launches them.
- 37 unit tests; docs `docs/SWIFT-MACOS.md`.

#### Fixed
- macOS-only Xcode projects were claimed by the iOS (Mobile) inspector.
- Results page: long output paths no longer run under the action buttons.

#### Fixed — Swift/macOS
- **SwiftPM resources:** resource bundles are now placed where SwiftPM's `Bundle.module` actually looks, the `.app`
  root. Before, they went in `Contents/Resources`, where `Bundle.module` fails at launch. `Contents` is signed and
  verified separately, and the unsealed root bundles are reported.
- Folder and ZIP imports skip SwiftPM/Xcode generated folders (`.build`, `.swiftpm`, `DerivedData`). Built working
  copies previously copied the whole build tree into the workspace and could exceed the import limits.

#### Added
- Build identity: the version, git commit, build time, executable and `.app` path are shown in Settings › About this
  build and in the footer, and printed by `HCGExecutableMaker --version`. `self-package` passes the commit through
  `HCG_SOURCE_REVISION`.
- The Configure step preserves a package's configured identity by reading its own packaging script (never running
  it). For Distil, Relay and similar packages that means the bundle id, display name, version, build number, minimum
  macOS, category and privacy texts.
- An XCTest probe, reported only as a test prerequisite. Build logs state that tests were not run.
- `tools/build-local-mac.sh` builds this checkout as an app on your Mac and prints the exact path. `--verify` runs the
  Distil/Relay/parent-repo/resource checks.
- CI `swift-macos` for current Oslo-Sprint (`b5d8697`, product `Distil`):
  - first builds both packages with their own scripts, then imports `distil/` and `relay/` separately, the parent
    repository as a folder and as a ZIP, and a resource fixture;
  - checks that the identity was preserved, launches every app, verifies resource loading, and compares Info.plist
    keys with the scripts' apps.
- Fixture `ResourceProbe`: a library and an app target, both with resources.

## [0.2.0] — 2026-11-01 (pre-release)

Unsigned pre-release. Adds the Windows Setup.exe installer (M0.2) and Mobile Build Studio (M0.5). Verified in CI: real
install → launch → uninstall on Windows; real Android APK/AAB builds on Ubuntu; iOS Simulator build and unsigned
archive on macOS (signed IPA export not verified — no Apple certificates in CI).

### Milestone 0.2 — Windows installer

#### Added
- **Windows Setup.exe installer** (Inno Setup 6): name, publisher, version, icon, per-user (default) or all-users
  installation folder, Start-menu shortcut, optional desktop shortcut, launch after install, uninstaller registered in
  Settings → Apps, stable AppId for in-place upgrades, all self-contained publish files bundled. The generated `.iss`
  script is kept next to the installer; ISCC output is part of the build log; artifacts are verified and checksummed.
- New Build wizard: the installer option is enabled only on Windows hosts with Inno Setup 6 and for self-contained
  builds (otherwise disabled with the reason); installer options panel; summary row.
- Inno Setup version detection (Program Files, Program Files (x86), per-user install, PATH).
- `tools/installer-test/Test-Installer.ps1`: install → verify files/shortcuts/registration → launch → uninstall →
  confirm cleanup.
- CI: Windows test job runs the real installer end-to-end test; `windows-installer` job builds and tests HCG's own
  Setup.exe; release packaging includes `*-Setup.exe`.
- `self-package.cs --installer`.
- Docs: `docs/WINDOWS-INSTALLER.md` (incl. the MSI/WiX plan for M0.2b).

#### Changed
- "Needs macOS host" label generalized to "Needs another host".
- Self-packaging CI runs on main, tags and manual runs; feature branches run tests and installer verification.
### Milestone 0.5 — Mobile Build Studio

#### Added
- **Mobile project detection** (static): Android Gradle (Groovy/Kotlin DSL, version catalogs, flavors, signing
  configs, AGP/Gradle/JDK compatibility table), Xcode projects/workspaces (OpenStep pbxproj parser, schemes, Swift
  packages, CocoaPods), Flutter, .NET MAUI; React Native and Unity recognized as unsupported.
- **Mobile toolchain detection**: JDK, Android SDK/build-tools/platforms/apksigner/zipalign/aapt2, bundletool, Xcode,
  iOS SDKs, signing identities, provisioning profiles, Flutter, MAUI workloads. Nothing is installed automatically.
- **Mobile build engine**: Gradle through the project's wrapper (java + wrapper jar, no shell, HCG-managed Gradle and
  Android user homes), xcodebuild Simulator/archive/export (explicit signing modes, `destination=export`), bundletool
  AAB → APK set (universal/device-targeted), Flutter and .NET MAUI planners; cancellation, timeouts, stale-output
  protection, history.
- **Mobile artifact verification**: APK, AAB, APK set, IPA, xcarchive, .app — integrity (CRC, traversal, bombs),
  binary/protobuf manifests, plists, Mach-O architectures, signature schemes and signer, provisioning profiles; plus
  `apksigner verify`, `bundletool validate`, `codesign --verify` when available.
- **Artifact import**: `.apk/.aab/.apks/.ipa` imported as artifact projects (copied, never extracted; renamed
  non-ZIP files rejected).
- **Mobile Build Studio** page; New Build redirects mobile projects there; `tools/mobile-build` CLI.
- CI: feature branches run the test matrix plus real Android (Ubuntu) and iOS (macOS) builds of the fixtures.
- Fixtures `HelloAndroid` (Gradle 8.14.3 wrapper, AGP 8.7.3) and `HelloIOS` (SwiftUI).
- Docs: MOBILE-BUILDS, ANDROID-BUILDING, IOS-BUILDING, MOBILE-ARTIFACT-VERIFICATION.

#### Changed
- Flutter/Android/Xcode signatures moved from "recognized, unsupported" to Mobile Build Studio.
- Self-packaging CI runs on main, tags and manual runs only (feature branches skip it).

## [0.1.0] — Milestone 0.1 "Functional Foundation" — 2026-10-31 (pre-release)

### Added
- Modular .NET 10 solution: Domain, Core, Security, Infrastructure, ProjectInspection, Packaging, BuildEngine, App, Tests.
- Avalonia 12 desktop application with the **HCG Future** design-token theme, persistent sidebar, Dashboard, six-step
  New Build wizard, Projects, Build History, Artifacts, Toolchains, Signing & Security and Settings pages.
- **HCG Classic** theme as a token-level preview; runtime theme switching.
- Secure project import from ZIP archives and folders into isolated workspaces (traversal/symlink/duplicate/bomb
  protection, configurable limits, original archive preserved, atomic commit, progress and cancellation).
- Static .NET project inspection (sln/slnx/csproj/fsproj/vbproj, Directory.Build.props, central package management,
  global.json roll-forward, TFMs, RIDs, references, entry points, icons, UI-framework and platform detection).
- Recognition (without build support) of Rust, Node/Electron, Swift/Xcode, Python, Java, Kotlin/Android, Flutter, C/C++.
- Toolchain detection for .NET SDK, Git, host OS/CPU, disk space, macOS and Windows packaging/signing tools.
- Build engine: structured `dotnet restore/build/publish`, Release/Debug, win/osx/linux × x64/arm64, self-contained,
  single-file, live log with warning/error parsing, cancellation (process-tree kill), timeouts, explicit trust gate,
  secret-scrubbed build environment, `IBuildWorker` isolation seam.
- Packaging: portable folder, portable ZIP with Unix permissions, macOS `.app` bundle (+ transfer ZIP off-Mac),
  `.dmg` on macOS hosts.
- Artifact verification (format/architecture, permissions, bundle structure, signature presence, archive integrity),
  SHA-256 checksums, per-file manifests, `SHA256SUMS.txt`, `.hcg-build.json` output marker.
- Icon engine: PNG/ICO/ICNS inspection and aspect-preserving ICO/ICNS generation.
- SQLite persistence with versioned migrations; settings store; JSON-lines application log.
- 162 automated tests including real end-to-end builds of an Avalonia fixture for linux-x64, osx-arm64 and win-x64,
  green in CI on Linux, macOS and Windows.
- Documentation: architecture, security model, environment report, status/continuation, HCG Mail validation workflow.

### Branding
- Installed the founder-provided temporary official HCG logo: sidebar wordmark, About card, window and dialog icons,
  Windows application icon. The original artwork is preserved unmodified in `branding/`; derived assets (transparent
  wordmark, square app icon, `.ico`) are reproducible with `tools/branding/make_logo.py`.

### Testing & distribution
- `tools/self-package/self-package.cs`: HCG Executable Maker builds and packages **itself** through its own engine
  (macOS arm64/x64 `.app` + transfer ZIPs, Windows x64 ZIP), verified and checksummed.
- GitHub Actions workflow *Build & Release*: tests on Linux/macOS/Windows, self-packaging on each real OS (`.dmg` on
  macOS, embedded `.exe` icon on Windows), artifacts on every push, GitHub pre-release on `v*` tags only.
- `self-package.cs` options: `--targets`, `--version`, `--dist`; `.dmg` produced automatically on Mac hosts.
- `DmgPackager` retries `hdiutil create` (intermittent "Resource busy" failures).
- Tests are portable to Windows (platform-appropriate absolute paths; Windows MSBuild diagnostics covered).
- `docs/TESTING.md`: how to get a build, first launch of the unsigned app on macOS/Windows, and a 10-minute test script.

### Fixed during M0.1 verification
- Path containment check mishandled file-system roots (`/`), breaking disk-space detection.
- Cancelling a build during packaging left a partial output folder that blocked later builds; HCG now claims the folder
  immediately and removes its own incomplete output.
- First CI run on real macOS: cross-building a Windows app on a Mac failed when Avalonia's XAML compiler hit a transient
  file lock on the freshly compiled DLL. Builds now always disable shared compilation, and a build step that fails
  *only* with that file-lock signature is retried once (logged as a warning; diagnostics from the discarded attempt are
  dropped). Any other failure still fails immediately.
- CI on macOS: self-packaging failed with hundreds of CS0246/CS0234 errors. Root cause (reproduced on Linux): a standalone
  `dotnet restore` run through a **symlinked path** (macOS `/var` → `/private/var`, where the temp folder lives) writes an
  assets file without the project's ProjectReferences. The build engine now resolves workspace, project and publish
  paths to real paths (`PathGuard.GetRealPath`, realpath semantics: link targets that pass through other links are
  resolved fully; link loops are detected) before invoking `dotnet`. Regression tests run green with a normal and a
  symlinked temp folder.
- macOS has `false` at `/usr/bin/false`, not `/bin/false`; process tests now locate Unix tools portably.
