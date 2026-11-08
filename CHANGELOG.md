# Changelog

All notable changes to HCG Executable Maker. Format based on [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased] — `feature/swift-macos` (not merged)

### Added — native Swift/macOS apps
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

### Fixed
- macOS-only Xcode projects were claimed by the iOS (Mobile) inspector.
- Results page: long output paths no longer run under the action buttons.

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
