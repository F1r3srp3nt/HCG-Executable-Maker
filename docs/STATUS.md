# Status — 0.3.0 pre-release

0.3.0 adds native Swift/macOS apps and macOS distribution (docs/SWIFT-MACOS.md, docs/MACOS-DISTRIBUTION.md), Electron
desktop apps (docs/ELECTRON.md), guided toolchain setup (docs/TOOLCHAINS.md) and the HCG account features
(docs/HCG-ACCOUNT.md — not live until HCG's external setup is done) to 0.2.0.
0.2.0 contained M0.1, M0.2 (Windows installer) and M0.5 (Mobile Build Studio), merged into `main` with founder approval.

# HCG Nexus, HCG Mail iOS, HCG Sentinel (unreleased, not pushed)

Details: docs/HCG-PROJECTS.md. Repositories inspected at Nexus c7cd62c, Mail 46d4228, Sentinel 810c6f9.

## Verified locally (Linux x64)

- Tests (TheosTests, AppleProjectRoutesTests) with fixtures copied from the three repositories: Theos detection of
  Nexus (tweak + preference bundle subproject, separate preview app, rootless/rootful/roothide), missing tools listed
  one by one (incl. the iPhoneOS SDK a configuration needs), the build plan (whole-repository copy, one folder per
  configuration, space-free path, argument list), `.deb` verification against real lzma/xz packages with synthesized
  Mach-O binaries (rootless pass; wrong layout, wrong Architecture field, missing files, old arm64e ABI, maintainer
  scripts each reported; a non-deb rejected); Mail's iOS head through the imported props chain at the repository root
  and as a partial import; workload pin readiness; the .NET iOS plans; Sentinel's targets/schemes, the deployment-range
  preflight, SDKSettings parsing, the project-level xcodebuild error, result bundles.
- The real wizard / Mobile Build Studio (ui-driver, headless) on the real repositories: Nexus shows *Required toolchain
  missing* with Theos, ldid and the iOS toolchain listed; Mail (repository root) shows *Required toolchain missing* (a
  Mac is required) with the iOS head's evaluated properties; Mail (submission folder only) shows *Configuration required*.
- Full test suite passes.

## Real builds (CI on macOS, not yet run: needs a push)

- `hcg-nexus-theos`: rootless and rootful `.deb` and the preview app's `.deb` through the wizard, then HCG Nexus' own
  `inspect-package.py --require-new-arm64e-abi` on Maker's output.
- `hcg-mail-ios`: Simulator build and unsigned device archive through Mobile Build Studio with Xcode 26.3 and the
  pinned workload.
- `hcg-sentinel-archive`: archive of both schemes with every installed Xcode on macos-15 and macos-26, with the range
  inventory; complete logs and `.xcresult` bundles uploaded.

## Not verified

- Signed IPA export for Mail and Sentinel (no certificates or profiles in CI); installing the Nexus `.deb` or any app on
  a physical device; an Xcode whose range excludes 14.7 (none is installed on hosted runners — the preflight is covered
  by tests with that range).

# C/C++ projects (unreleased, branch `feature/native-cpp`, not pushed)

Details: docs/NATIVE-CPP.md. Development host: Linux x64 (Ubuntu 24.04), CMake 3.28.3, Ninja 1.11.1, GNU Make 4.3,
GCC 13.3, Clang 18.

## Verified locally (Linux)

- The 28 C/C++ tests pass, including real builds: a C project in paths with spaces (configure refused without trust),
  a C++17 project (Debug), a multi-target project (calc + greeter, shared library shipped with a portability warning),
  a library-only project (refused), a Make target with a variable, a missing expected output, a compile error reported
  at `main.c:4`, cancellation of a running make (child processes stopped), and loose sources.
- The real New Build wizard (`tools/ui-driver --native`, Avalonia headless) on the fixtures: MultiTarget (trusted
  configure → targets calc and greeter, libraries listed separately → build calc → Verified), CppHello, MakeHello
  (target, variable, expected output), LooseSources (selected files), MacBundle (plain executable on Linux) and
  TheosTweak (Not supported, explained). The produced programs were run afterwards by hand, outside HCG, and printed
  the expected output.
- The full test suite passes (see the commit for counts).

## Implemented, not verified on that platform yet

- macOS: `MACOSX_BUNDLE` and Makefile-built `.app` bundles kept as built, ad-hoc or Developer ID signing stage, ZIP
  with ditto, universal (arm64 + x86_64) builds, `otool` runtime-library report, the `xcode-select` check before the
  compiler probe. Covered by the new `native-cpp` CI job (macos-latest), which has not run yet.
- Windows: Visual Studio generator with MSVC (`vswhere`), MinGW Makefiles, PE verification. Not run on Windows.

## Not implemented

- Cross-compilation of any kind; DMG, PKG, Windows installer, AppImage or .deb outputs for C/C++ builds; Theos,
  Meson, Autotools-without-Makefile, Bazel and Visual-Studio-solution-only projects; installing compilers, packages or
  package managers (vcpkg, Conan).

# Swift macOS apps (unreleased, branch `feature/swift-macos`, not merged)

Evidence: CI run #20 (<https://github.com/F1r3srp3nt/HCG-Executable-Maker/actions/runs/42428955743>), all jobs green.
Host: macOS 26.7.2, Xcode 26.6 (`/Applications/Xcode_26.6.app/Contents/Developer`), Swift 6.3.3, macOS SDK 26.5.
Reference repository: Oslo-Sprint @ `45431255`.

## Verified on a real Mac (through the real New Build wizard, `tools/ui-driver`, headless)

| What | Result |
|---|---|
| Folder import, both packages listed, none pre-selected; `distil/` evaluated after trust. Products: TokenLab (app); libraries: TokenLabCore; tests: TokenLabCoreTests | Passed |
| **Distil.app** (TokenLab, display name Distil, `dev.tokenlab.prototype` 0.4.0 (4), developer-tools, arm64) + app ZIP + DMG | Built and verified. `codesign --verify --strict` valid. Launched with `open`, still running, window screenshot. DMG mounted: app verifies, Applications link present |
| ZIP import with a wrapping folder; `relay/` evaluated. Products Relay (app) and relay-runner (CLI), neither pre-selected. The wizard asks for `NSMicrophoneUsageDescription` and blocks the build while it is empty | Passed |
| **Relay.app** universal (x86_64 + arm64, minos 14.0) + app ZIP | Built and verified. Launched from the build output and again after `ditto -x -k` of the ZIP; window screenshot |
| **relay-runner** CLI (folder + ZIP) | Built; `relay-runner help` and `export-procedure` ran, also from the unzipped ZIP |
| Builds refused before trust; evaluation refused before trust | Passed |

Screenshots: `docs/screenshots/11`–`15-swift-*.jpg`.

## Verified locally (Linux)

- Wizard on Linux: evaluation and Continue disabled, *Needs another host*, the reason shown (ui-driver
  `--expect-blocked-host`).
- 313 tests passed, 1 skipped (Windows-only).

## Not verified

- Clicking through the wizard by hand on a Mac desktop. CI drives the same view models and views headlessly; only the
  file picker and dialogs are scripted.
- Packages with SwiftPM resource bundles: the reference projects have none, and older `Bundle.module` accessors may
  not find `Contents/Resources`.
- Packages with remote dependencies: implemented (`swift package resolve`), not exercised.
- The Command Line Tools-only developer directory (CI uses full Xcode). Running the apps on other Macs (Gatekeeper
  warns: ad-hoc signature).

## Not implemented

Xcode macOS projects/workspaces (recognized, explained in the UI); Developer ID signing, notarization, `.pkg`, Mac App
Store; Swift builds on Windows/Linux.

---

# Milestone 0.2 (Windows installer)

Evidence: CI run #13
(<https://github.com/F1r3srp3nt/HCG-Executable-Maker/actions/runs/41299757852>) — all jobs green.

## Verified on a real Windows machine (GitHub `windows-latest`, Inno Setup 6 installed by CI)

| What | Result |
|---|---|
| `WindowsInstallerEndToEndTests`: HelloAvalonia fixture built for win-x64 → portable ZIP + **Setup.exe** (icon, name, publisher, version) → SHA-256 recorded in `SHA256SUMS.txt` → silent per-user install → every published file + `unins000.exe` present → Start-menu and desktop shortcuts present → uninstall registration (name 1.4.2, publisher, UninstallString) → **installed app launched and kept running** → silent uninstall → folder, both shortcuts and registration removed | **Passed** (1 min 39 s) |
| `windows-installer` job: HCG Executable Maker builds **its own** `HCG-Executable-Maker-0.2.0-win-x64-Setup.exe` with `self-package.cs --installer`, then `Test-Installer.ps1`: install (252 files), shortcuts exist and point to `HCGExecutableMaker.exe`, registration `{8ABCE15B-…}_is1` "HCG Executable Maker 0.2.0 — Hakmi Capital Group", app launched and running after 8 s, uninstaller exit 0, folder/shortcuts/registration removed | **All checks passed** |
| Portable folder / ZIP for Windows (desktop end-to-end test) | Unchanged, passing |
| Full test suite on Windows / macOS / Linux | 179 tests; Windows 169 passed, 10 skipped (Unix-only tests) |

Local (Linux container): 175 passed, 4 skipped (3 integration tests skipped with `HCG_SKIP_INTEGRATION=1`, plus the
Windows-only installer test).

## Not verified

- Clicking through the New Build wizard on a Windows desktop to produce a Setup.exe (CI exercises the same
  `BuildService` + `InnoSetupPackager` path headlessly; the wizard option is disabled on this Linux host, by design).
- All-users (administrator) installation — implemented (`PrivilegesRequired=admin`), not exercised in CI.
- ARM64 installers — produced by the `package` job on main/tags only; not installed on ARM64 hardware.
- Installing on a clean consumer PC (SmartScreen prompt, since the installer is unsigned).

## Not implemented

MSI (WiX) — next milestone M0.2b; MSIX; Authenticode signing (M0.4).

---

# Milestone 0.5 (Mobile Build Studio)

Evidence below is from CI run #10
(<https://github.com/F1r3srp3nt/HCG-Executable-Maker/actions/runs/41280121136>) and local runs in a Linux container
that has a JDK but **no Android SDK** (Google's Maven/SDK hosts are blocked there) and no Xcode.

## Verified with real toolchains

| What | Where | Result |
|---|---|---|
| Android **debug APK** of `tests/Fixtures/HelloAndroid` via the project's Gradle 8.14.3 wrapper (AGP 8.7.3, JDK 17, SDK on the runner) | CI Ubuntu | Succeeded |
| Android **release APK, unsigned** (no signing in the project) | CI Ubuntu | Succeeded |
| Android **release APK signed with HCG's local debug key** (injected signing) | CI Ubuntu | Succeeded |
| Android **App Bundle** `app-release.aab` — metadata `com.hakmicapital.hello 1.2.0 (3)`, minSdk 24, targetSdk 35, unsigned; `bundletool validate`: valid | CI Ubuntu | Succeeded |
| **AAB → universal APK** with bundletool 1.18.1 from the project: `.apks` + `app-release-universal.apk`, debug-signed v2+v3, **`apksigner verify` passed** | CI Ubuntu | Succeeded |
| **AAB → universal APK** from an **imported `.aab` artifact** (artifact project path) | CI Ubuntu | Succeeded |
| iOS **Simulator build** of `tests/Fixtures/HelloIOS` (Xcode 26.6, iOS SDK 26.5) | CI macOS | Succeeded |
| iOS **unsigned archive** `HelloIOS.xcarchive` — `com.hakmicapital.helloios 1.3.0 (7)`, min iOS 16.0, arm64, *Unsigned · not distributable*, exportability warning | CI macOS | Succeeded |
| IPA refused without code-signing identities ("No code-signing identities were found in the keychain…") | CI macOS | Refused as expected |
| Signed archive without a development team fails with xcodebuild's own error (no certificates in CI) | CI macOS | Failed honestly as expected |
| Gradle wrapper started as `java -classpath gradle-wrapper.jar GradleWrapperMain` through the proxy translation; stops only at AGP resolution because Google Maven is blocked | Local container | As expected |
| Each build persisted in SQLite history with artifacts, checksums and checks | CI + local | Verified |
| Mobile Build Studio page renders with honest availability reasons (screenshot `docs/screenshots/10-mobile-build-studio.png`) | Local, Xvfb | Verified |

Automated tests: **260 passed, 0 failed, 0 skipped** locally (Linux, including real desktop end-to-end builds);
green on Linux, macOS and Windows in CI. Mobile coverage: detection/parsers (MobileInspectionTests), artifact
inspection incl. corrupt/traversal/CRC/fat Mach-O/expired profiles and a real `jarsigner` cross-check
(MobileArtifactTests), argument construction for Gradle/xcodebuild/bundletool/Flutter/MAUI, masking, output parsing
(MobilePlannerTests), and the orchestrator with cancellation, timeouts, missing SDK, wrong host, stale outputs,
unsigned-archive export refusal, export-options cleanup and history (MobileBuildServiceTests), artifact import
(MobileArtifactImportTests).

## Implemented but NOT verified with real toolchains

- **Signed IPA export** (automatic/manual signing, all export methods): requires an Apple Developer account and
  certificates on a Mac — never configured by HCG or CI. Covered by unit tests only.
- **Flutter** and **.NET MAUI** builds: planners and capability rules are unit-tested; no Flutter SDK or MAUI workloads
  were installed in CI.
- **Device-targeted APK sets** (`--device-spec`): unit-tested only.
- Release builds using a project's **own release signingConfig**: not exercised (the fixture has none).
- Mobile Build Studio **GUI build run** on a machine with the SDK (CI drives the same engine through the CLI).
- Installing the produced APKs on a device/emulator and running the Simulator app (never done automatically).

## Not implemented / not offered (by design)

APK → AAB conversion; desktop-to-mobile "conversion"; uploads to Google Play, TestFlight or App Store Connect;
creation of production keystores, certificates or provisioning profiles; React Native, Unity and Capacitor builds;
Electron apps packaged with Electron Forge or @electron/packager, or installed with yarn/pnpm/bun (recognized and
explained; electron-builder + npm is implemented, see docs/ELECTRON.md); Electron packaging on Windows hosts; MAUI separate `.xcarchive` output; Flutter builds on Windows hosts.

## Follow-ups

1. Verify a signed IPA on a Mac with a real Apple Developer team, and a Flutter/.NET MAUI project.
2. Install a CI-built fixture APK on a device or emulator.

---

# Milestone 0.1 (Functional Foundation)

## Complete and verified

Verification was done in a Linux x64 development container (see `ENVIRONMENT-REPORT.md`).

| M0.1 requirement | Evidence |
|---|---|
| Working Avalonia desktop app, HCG Future theme, sidebar navigation | App launched under Xvfb; every page rendered and exercised; screenshots in `docs/screenshots/` |
| Dashboard from real local data | Counts, recent projects/builds, host and toolchains come from SQLite and live detection; empty states shown when empty |
| ZIP import, directory import, previously imported projects | ZIP imported through the real GTK file picker in the running app; 19 importer + 19 path-guard tests incl. traversal/symlink/limits |
| .NET project inspection | 31 inspection tests; fixture correctly analysed in-app (Avalonia 12.1.4, .NET 10, entry point, icon) |
| Toolchain detection | Real detection test on host (.NET SDK 10.0.114, Git 2.43.0, disk space); macOS/Windows tools report *Not on this host* |
| Functional .NET build with live logs, cancel, timeout | GUI build of the fixture for osx-arm64 succeeded (21.5 s, 4 artifacts); GUI cancellation mid-build terminated the process tree and recorded *Cancelled* |
| Packaging + verification + checksums | End-to-end test builds the fixture for linux-x64, osx-arm64 and win-x64: ELF, Mach-O arm64 (signed ad-hoc) and PE x64 verified, `.app` Info.plist/icon/permissions checked, SHA-256 recorded |
| Build history, artifacts, export | History/Artifacts pages show real records; repeat build, view log, delete, export with overwrite confirmation |
| Automated tests | `dotnet test`: **162 passed, 0 failed, 0 skipped** (includes real `dotnet` builds) — green in CI on **Linux, macOS 26 (Apple Silicon) and Windows** |
| Real-OS packaging (CI run #5) | HCG Executable Maker packaged itself on GitHub's runners: macOS → `.app` + **`.dmg` (arm64, x64), `hdiutil verify` passed**; Windows → x64/ARM64 ZIPs built on Windows (icon embedded in `.exe`); Linux → x64/ARM64 ZIPs |
| HCG Executable Maker itself publishes for macOS & Windows | `dotnet publish -r osx-arm64` / `-r win-x64 --self-contained` succeed with native SQLite/Skia/HarfBuzz libraries |

## Implemented but NOT verified on a real target

- Running HCG Executable Maker's **GUI** on macOS and Windows desktops (CI exercises the engine, not the UI).
- **Launching** produced `.app`/`.exe` files (never done automatically — it would execute project code).
- macOS/Windows toolchain probes on a desktop (CI runs them only inside the test suite's host-detection test).

## Not implemented (by design labelled *Planned* / *Not implemented* in the UI)

- Windows installer `.exe`, `.msi`, `.msix` (M0.2) · Windows `.exe` icon embedding when cross-building (needs Windows host)
- Authenticode and Microsoft Store signing (M0.4)
- macOS `.pkg`, Developer ID, notarization and Mac App Store preparation are implemented for Swift apps (docs/MACOS-DISTRIBUTION.md)
- Linux AppImage/DEB/RPM, Android, non-.NET build pipelines (M0.5+)
- Isolated/sandboxed build workers (the `IBuildWorker` abstraction exists; only local execution is implemented)
- **Final** HCG logo: the founder-provided *temporary* official logo is installed (sidebar, About, window/app icon); replace it when the final artwork is approved
- HCG Classic theme is a token-level *preview* (palette only)

## Known limitations

- Secret scrubbing is name-based (see `SECURITY.md`).
- Projects needing authenticated NuGet feeds will fail to restore (credentials are intentionally not forwarded).
- Inspection is static: properties computed by MSBuild logic (custom targets, imported `.props` outside the workspace)
  are reported as not statically evaluable; the build itself remains the source of truth.
- Folder-type artifacts' checksum is a SHA-256 over a sorted per-file manifest (saved next to the artifact), not over
  an archive.

## Continuation instructions

```bash
git checkout feature/m0.1-foundation
dotnet build HCG.ExecutableMaker.slnx && dotnet test     # expect 154 passing
dotnet run --project src/HCG.ExecutableMaker.App
```

Suggested next steps, in order:

1. **Founder review on real hardware:** run the app on a Mac and a Windows PC; build `tests/Fixtures/HelloAvalonia`
   for each, launch the outputs manually, and try `.dmg` creation on the Mac.
2. **Final branding:** when the final HCG logo is approved, replace `branding/hcg-logo-temporary-original.png` and rerun
   `tools/branding/make_logo.py` (see `branding/README.md`); approve the derived square app icon.
3. **M0.2 Windows packaging:** add `InnoSetupPackager` (`IPackager`, uses detected `ISCC.exe`) and a WiX MSI packager;
   flip their `PackagingCatalog` entries to `Available` when the tool is detected; add installer metadata
   (publisher, version, shortcuts, uninstall) from `AppIdentity`; add Windows-host CI.
4. **M0.3 macOS:** `.pkg` via pkgbuild/productbuild — done for Swift and .NET apps (unsigned) and Swift apps (Developer ID Installer); see docs/MACOS-DISTRIBUTION.md.
5. **M0.4 signing:** credentials from OS keychain/certificate store only, per-operation founder approval, never inside
   untrusted builds; notarization with `notarytool` + stapling; release manifests.
6. Generalize `BuildService` into `IBuildPipeline` per ecosystem before adding non-.NET toolchains (M0.5).

Nothing has been pushed to GitHub. Local commits are on branch `feature/m0.1-foundation`.
