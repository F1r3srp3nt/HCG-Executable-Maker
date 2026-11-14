# Changelog

All notable changes to HCG Executable Maker. Format based on [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased] — HCG Signing Vault ([docs/SIGNING-VAULT.md](docs/SIGNING-VAULT.md))

### Added
- **Signing & Security › Apple Signing.** Identities (type, team, fingerprints, validity, private key on this Mac, revocation only as macOS reports it, location) and provisioning profiles (UUID, App ID, platforms, devices, entitlements, distribution type, signature). Assets on this Mac, recorded in the HCG account and on another Mac are shown separately; only local assets with a private key are ever "ready".
- **Keychain import:** password-protected `.p12` (password checked in memory, never on a command line or stored; no `-A`/`-T` grants), `.cer` with an explanation of the missing private key, verified profile import. Duplicates by SHA-1 / profile UUID; refresh; specific errors.
- **Separate removals:** remove HCG record, delete from this Mac (certificate + key, or certificate), revoke with Apple (explained, never performed).
- **Project signing configuration:** Xcode automatic or manual; routes for iOS development / Ad Hoc / App Store / In-house and macOS development / Developer ID / Mac App Store; per-target profiles; stored locally, non-secret.
- **Pre-build validation** (Signing & Security and before iOS archive/IPA builds with Apple signing): certificate type/key/validity/team, profile signature/expiry/platform/type/team/certificate, bundle ID with wildcard rules, entitlements with values, devices, embedded targets, tools. Apple-side checks are labelled not verified.
- **Account metadata** client and website package routes `/signing/assets`, `/signing/projects/{key}` (migration `20261113230000`, tests on PostgreSQL, C# contract test). Not deployed: the app shows account backup as unavailable.
- **Encrypted vault:** design contract only; shown as not available.
- **App Store Connect API key** boundary (reference + validation), separate from signing.
- Tests: 57 C# tests (synthetic certificates and CMS-signed profiles), 5 website tests, 1 contract test, opt-in macOS keychain test (CI step), UI driver `--signing`.

## [Unreleased] — `feature/mobile-conversion`

### Added — desktop → mobile conversion workspace ([docs/MOBILE-CONVERSION.md](docs/MOBILE-CONVERSION.md))
- **Porting Studio as the conversion workspace.** The flow is: import → inspect compatibility → choose target → review plan → generate → validate → build/export.
  - Porting Studio was brought onto current main.
- **Compatibility report:**
  - sources (framework, language, entry point, evidence);
  - every feature in one of four classes (reusable, requires adaptation, requires a backend or companion computer, unsupported by the current converter);
  - per-target options with mode, status and requirements;
  - plain-language blockers. An executable alone is refused as conversion source.
- **Support matrix** (`ConversionCatalog`): every recognized framework × iOS/iPadOS and Android, implemented, planned (with precise requirements) or not planned. It includes companion mode and HCG Server, which are planned.
- **Web-based mobile packaging:** Vite + React → Android and iOS/iPadOS with Capacitor 8.5.3 (pinned).
  - Generation runs nothing: it writes a separate copy, the Capacitor config without a dev-server URL, the viewport, and touch/safe-area/accessibility styles.
  - Server calls, sign-in callbacks, tokens in web storage, desktop-only browser APIs and permissions are classified and listed as manual steps; secrets in the web build are blockers.
- **Validate** (a new level between generated and compiled): the original and the imported copy are unchanged (SHA-256), output is complete, plus path checks. For web packaging, after trust: npm install, the web build, and `cap add` creating the native project.
- **Reproducibility and migration report:** each generated project records its source revision (Git commit/branch, content fingerprints) and conversion configuration in `HCG-PORT/conversion.json`. `HCG-PORT/MIGRATION.md` lists modifications, unresolved features and manual steps.
- **Mobile Build Studio: Capacitor framework.** Detection; readiness (Node 22+, JDK 21, SDK/Xcode); a planner that runs npm ci → web build → cap sync → Gradle/xcodebuild.
- **Launch check for apps:** install, still running, no crash, screenshot, visible text through uiautomator, optional expected text.
- **Tests and CI:** `MobileConversionTests` (17), the opt-in `MobileConversionRealTests`, UI driver `--path/--validate/--expect-text/--expect-executable-only`, CI job `port-web-android` (emulator), and a web → iOS step in `port-ios`.

### Changed
- The web → desktop conversion's change kind is now `WorkspaceChangeKind`, so it no longer clashes with the porting domain's `FileChangeKind`.

## [Unreleased] — HCG Nexus, HCG Mail iOS, HCG Sentinel ([docs/HCG-PROJECTS.md](docs/HCG-PROJECTS.md))

### Added
- **Theos jailbreak packages:** detection before generic Make (tweak, preference bundle, app, tool, library; subprojects;
  separately packaged projects), the configurations the Makefile defines (rootless / rootful, SDK and deployment target
  from TARGET, project refusals shown verbatim), per-item toolchain checks (Theos, SDK, GNU make 4+, ldid, perl, iOS
  clang), `make package` in a per-configuration copy, and `.deb` verification (metadata, Architecture, install prefix,
  payload, binary slices, arm64e pointer-authentication ABI, minimum iOS, maintainer scripts) with INSTALL.txt.
  Never an IPA; never installed; never a respring.
- **.NET for iOS applications (non-MAUI, e.g. HCG Mail's Avalonia.iOS head):** detected through the imported props
  chain; repository-root references; workload pin readiness; Simulator build, device archive (`ArchiveOnBuild` into
  HCG's folder), signed IPA through a separate `xcodebuild -exportArchive` stage.
- **Xcode deployment-range preflight** (from the selected Xcode's iPhoneOS SDKSettings.json) per archived target, with
  the routes: another Xcode or another scheme. The project's target is never changed.
- **Readable failures:** project-level xcodebuild diagnostics parsed; the first actionable error in full (copy), full log
  and `.xcresult` export; result bundles kept for every xcodebuild action; wrapping log lines.
- **Precise states** in New Build and Mobile Build Studio, and a pre-build summary (project, adapter, toolchain,
  architecture, output type).

### Changed
- .NET for iOS archives are offered signed only: the iOS SDK refuses an unsigned archive ("Code signing must be enabled
  to create an Xcode archive"); Maker blocks the archive before building until a certificate is in the keychain.
- A first error that ends in ":" carries the reason xcodebuild prints on the following lines (e.g. "iOS 18.0 is not
  installed. To use with Xcode, first download and install the platform").
- The Theos iOS toolchain is found from the selected developer directory without starting `xcrun --sdk` (which starts
  xcodebuild); the macOS SDK probe waits for xcrun's first answer and reports a timeout as one.
- The static MSBuild reader follows `<Import>` with `$(MSBuildThisFileDirectory)` and never reads outside the import.
- A net*-ios Exe is no longer listed as an "Avalonia desktop application".

## [Unreleased] — `feature/native-cpp`

### Added — C/C++ projects ([docs/NATIVE-CPP.md](docs/NATIVE-CPP.md))
- **The "C/C++ support planned" placeholder is gone.** C/C++ projects are inspected, configured, built, verified and
  packaged through the New Build wizard.
- **Detection (static):** CMake (topmost `project()`), generic Make, sources without a build system (only when a
  `main()` exists) and Theos, checked before Make. Project roots inside repositories skip `build/`, `_deps/`,
  `third_party/`, `vendor/`, `cmake-build-*` and similar folders. Meson, Autotools without a Makefile and Visual Studio
  solutions are recognized and explained.
- **CMake:** a trusted *Configure with CMake* step runs CMake in HCG's own folder and reads the targets through the
  CMake File API. Presets, build type, cache entries and an explicit downloads switch
  (`FETCHCONTENT_FULLY_DISCONNECTED` by default). Each executable target is an entry under *Project to build*;
  libraries are listed separately and never offered as runnable.
- **Make:** an explicit target, expected output and variables; the build runs in a copy of the project.
- **Sources:** explicit file selection, language and standard; the generated CMakeLists.txt lives in HCG's build folder.
- **Build:** the existing trusted-build flow and worker, argument lists only, streamed logs, GCC/Clang/MSVC/CMake/linker
  error parsing with file and line, cancellation and time limits that stop child processes.
- **Verification:** real executable image (ELF/Mach-O/PE), format and architecture, a runtime-library report
  (`RUNTIME-DEPENDENCIES.txt`) with a warning when the project's own shared libraries are not found from the
  executable's folder. Portable folder/ZIP; on a Mac, the project's own `.app` with a separate signing stage and a
  ditto ZIP. HCG never runs or installs the produced program.
- **Wizard states:** toolchain missing · configuration required · ready · building · failed · verified (or not
  supported). Only this computer's platform and architectures can be chosen.
- **Toolchains:** a C / C++ group (CMake 3.20+, Ninja, make, C and C++ compilers, MSVC via vswhere).
- **Tests and CI:** 28 tests with fixtures under `tests/Fixtures/Native/`, a `--native` UI-driver flow, and a
  `native-cpp` CI job on Linux and macOS (including a universal `MACOSX_BUNDLE` app).

## [Unreleased] — `feature/web-desktop-app`

### Added — Swift packaging contracts (`AppPackaging.json`) ([docs/SWIFT-MACOS.md](docs/SWIFT-MACOS.md#packaging-contracts-apppackagingjson))
- **Swift packages can declare what a complete .app contains:** helper executables built by prebuild commands,
  resources, an Info.plist template and an icon (schema `hcg-app-packaging/1`). Apps such as HCG Server, whose Go
  agent lives in `Contents/Helpers`, are now packaged complete instead of without their helper.
- **Read as data during inspection, with strict validation:**
  - unknown keys and schemas are rejected;
  - commands must be argument lists;
  - paths must stay inside the workspace;
  - helpers go only to `Contents/Helpers` and resources to `Contents/Resources`;
  - invalid contracts are reported in Configure.
- **Configure shows the prebuild commands verbatim.** They run only after explicit confirmation, and the build refuses
  if the file changed after it was confirmed (SHA-256).
- **Required tools** (for example `go >= 1.25`) are located (PATH, Homebrew, `/usr/local`) and version-checked, then
  put first on the command's PATH.
- **Helpers are placed in `Contents/Helpers` and signed before the app** (ad hoc, or Developer ID with the hardened
  runtime). They are verified: present, executable, promised slices, own signature, and `<helper> version` matching
  the app version.
- Identity is prefilled from the contract and its Info.plist template; the template's other keys are kept in the app.

### Changed — Web app to desktop app ([docs/ELECTRON.md](docs/ELECTRON.md#browser-apps-create-desktop-application-vite--react))
- **A recognised web app is now a selectable conversion candidate.** Browser apps, including nested ones such as
  `web-hcg-store/`, are listed under *Project to build*, each with its category. The picker no longer says "No runnable
  application project found", and outputs are recommended only when something can produce them.
- **Classification (static):**
  - browser app (Vite + React, npm);
  - Electron app without packaging (packaging added, main process kept);
  - existing Electron app (built as it is);
  - needs a server (SSR frameworks, a runtime backend);
  - not supported or ambiguous (other frameworks or package managers, monorepo roots, local or sibling dependencies,
    no `vite build`).
- **Create desktop application** panel:
  - product name, application identifier and version;
  - icon (auto-detected, validated);
  - target platform and architecture (only what this computer packages);
  - package formats (`.app`, ZIP, DMG), output folder;
  - signing status and choice (ad-hoc, or a Developer ID identity from the keychain);
  - notarization shown as a separate step that the build never runs;
  - public `VITE_` settings.
- **The created desktop project opens at the Build step** with those choices. After the trust confirmation, it builds
  in separate stages: install → compile the web app → package → verify.
- **Converting again updates the desktop project generated earlier** instead of adding another.
  `hcg-desktop/conversion.json` records every generated file (SHA-256), the options, the source and the original
  `package.json`. Regeneration replaces recorded files, removes stale ones and never overwrites a file edited afterwards.
- **Generated main process:**
  - CSP on every page;
  - `will-redirect` guard; only https/mailto links leave the app, every other scheme is refused;
  - permission checks denied; single instance;
  - an error page if the start page cannot load; failed remote requests logged by host.
- **The verify step after the web build** also checks every asset `index.html` references.
- **Electron 45.3.0** (requires Node.js `>=22.12.0`, checked before generating) and electron-builder 26.18.0. The resolved
  `vite`, `electron` and `electron-builder` engines are checked against the chosen Node.js after the lockfile is resolved.
- **npm workspace members** get a lockfile seeded from the monorepo's pinned versions.

### Fixed
- **Build logs and messages are passed through `SecretRedactor`.** npm or electron-builder output that contains
  credentials no longer reaches the log.
- **Electron builds on macOS log their signing state**, for example *UNSIGNED for distribution and NOT notarized* for an
  ad-hoc signature.
- The build summary shows that an Electron app's icon comes from its electron-builder configuration.

### Verified
- Unit tests.
- On Linux, through the real wizard: the fixture and the real HCG Store were converted, built and launched with no dev
  server (deep links, reloads, lazy chunk, links, CSP, window security), and converting again updated the same
  project.
- macOS: CI job `web-desktop-macos`.

## [Unreleased] — `fix/account-connection`

### Fixed
- **The app no longer claims its client is "not registered" without evidence.** A missing client ID is a *local configuration* state (Not set up on this computer), with the exact missing or invalid fields. *Rejected by HCG* is shown only when HCG's identity service itself answers `oauth_client_not_found`, `invalid_client` or `unauthorized_client`. Previously a token-endpoint `invalid_client` was folded into "not configured".
- **Cancelling sign-in was reported as "timed out after 5 minutes".** It now says the sign-in was cancelled. A real timeout says so and why.
- Support-request upload progress could finish below 100% (reports were posted to the thread pool out of order); they are now reported in order. This also fixes an intermittent test failure.
- The exported configuration template keeps `<ISSUED_PUBLIC_CLIENT_ID>` readable (it was escaped as `\u003C…`).

### Added
- **Account › HCG Connect:**
  - Import HCG connection configuration… (validated; persisted in the data folder; applied at once);
  - Check configuration (a rejection is recorded only from HCG's answer);
  - Retry connection (re-reads the saved file, restores the session, re-reads plan and access);
  - Export registration request… (`hcg-executable-maker-registration.json`, `hcg-connect.template.json`, `HCG-CONNECT-SETUP.txt`).
- **Sign-in preflight:** before the browser opens, the app asks the identity service (no credentials) whether it accepts the client and redirect URI. A rejected client never opens an error page in the browser.
- **Field-level validation:** `HcgConnectConfiguration.Issues()` returns field and reason pairs. JSON type errors name the field. The identity endpoints must be on the issuer's host and use HTTPS.
- The organisation in use is shown with the signed-in identity.
- **CLI:** `--hcg-session export <folder>`. `sign-in` exits with 7 when HCG rejects the client.
- **Tests:** import and persistence across restart, Retry re-reading a replaced file, field errors, rejection only on HCG's evidence (preflight and token endpoint), network failure vs rejection, browser cancellation and failure to open, the template and export. UI driver: unconfigured, rejected by HCG, cleared, Retry, then sign-in (MOCK HCG).

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
