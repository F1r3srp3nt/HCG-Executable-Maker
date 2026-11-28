# Changelog

All notable changes to HCG Executable Maker. Format based on [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased] — .NET for iOS: genuine XCArchive and IPA (HCG Mail), device route, signing by route

### Fixed
- **"dotnet publish succeeded but created no .xcarchive"** (HCG Mail, Xcode 27.2, workload 27.2.10000). The .NET iOS
  SDK's Archive task uses `ArchiveDir` verbatim as the archive's root. Maker passed a parent folder and then searched it
  for `*.xcarchive`, so it never found the archive. `ArchiveDir` now names the `.xcarchive` itself, in a per-build folder.
- **The IPA is the .NET iOS SDK's own** (`BuildIpa=true`, `IpaPackagePath` per build), with the archive alongside.
  Maker no longer passes `BuildIpa=false` and no longer runs `xcodebuild -exportArchive` for .NET projects.
- **Entitlement variables** (`$(AppIdentifierPrefix)`, `$(TeamIdentifierPrefix)`, `$(CFBundleIdentifier)`,
  `$(PRODUCT_BUNDLE_IDENTIFIER)`) are resolved for each profile's team before entitlements are compared with the profile.
- **A manually named profile must cover the bundle ID.** A profile for another App ID of the same team is refused with
  the reason. App Store distribution never accepts a wildcard profile.
- **A .NET for iOS app signed with a named certificate and profile was refused with "No Apple Developer Team ID is
  selected"**: such a project states no team. The team is now the chosen certificate's (or profile's); a certificate
  and profile of different teams are still refused. Found by the HCG Mail CI run.
- The .NET archive was judged as "unsigned" for availability and always showed unavailable. It is now judged with this Mac's certificates and profiles.

### Added
- **Signed device-app route for .NET for iOS** (`dotnet build -r ios-arm64`, Debug or Release; no archive, no IPA).
  Debug/Release choices for every .NET iOS route. A distribution IPA is Release only.
- **Effective MSBuild properties** are read with the build's own settings before compiling (`dotnet msbuild -getProperty`),
  logged and checked: Exe, ArchiveOnBuild, ArchiveDir, BuildIpa, code signing.
- **Signing Vault validation for .NET for iOS**: the app's ApplicationId and CodesignEntitlements file are validated
  against the chosen identity and profile, by route.
- **When packaging fails after signing**, the signed `.app` is kept and named in the failure.
- The archive and IPA are named after the project's AssemblyName (`HCGMail.iOS.xcarchive`, `HCGMail.iOS.ipa`), the name
  the SDK gives the app, as HCG Mail's own script does since its PR #28. CI now builds HCG Mail at `b3ecfad` and also
  checks the names, arm64 executables, and that the IPA's app binary and profile equal the archive's.
- **Release Passport for iOS IPAs from Mobile Build Studio** (`<file>.ipa.release-passport.json`, build half): the IPA's
  SHA-256, bundle ID, version, source commit, toolchain and `code_signing` read from the app inside (Mach-O signatures,
  signed entitlements, embedded profile, Apple's `codesign --verify` verdict). Device installation and launch are never
  claimed by the builder.
- **Apple's `codesign --verify --deep --strict` now also runs on the app inside an IPA** (on a Mac), in Maker's
  production verifier. A failing codesign fails the IPA.
- **Artifact actions in Mobile Build Studio**: Launch in Simulator, Open in Xcode Organizer, Open in HCG Impactor, and
  Inspect signature and entitlements. Install on device now also covers the .NET device app and development IPA.
- CI job `hcg-mail-ios-signed` (`xcode-27`) builds the real HCG Mail (`b3ecfad`, Xcode 27.2, workload 27.2.10000)
  through Mobile Build Studio's UI:
  - a Simulator launch;
  - a signed device app, an XCArchive and the SDK's IPA, signed with a synthetic identity and wildcard development profile;
  - independent codesign, plutil, `security cms` and unzip checks;
  - Maker's own codesign verdict on the device app, archive and IPA, and the IPA's Release Passport.

  See [docs/DOTNET-IOS.md](docs/DOTNET-IOS.md).

## [Unreleased] — HCG Store Manager compatibility (Swift apps with a command-line tool)

### Added
- **Product preselection.** A Swift package whose `AppPackaging.json` names a product has that product preselected
  when it has several executables (HCG Store Manager: `HCGStoreManager`, not the `hcg-store-manager` CLI).
- **Product guidance.** Without a contract, the Inspect step names the app product. Choosing a command-line tool next
  to an app is explained: it is built as a folder or ZIP, not a macOS app.
- **Regression case `store-manager`.** `tests/Fixtures/StoreShapedApp` has HCG Store Manager's shape: library, CLI and
  SwiftUI app, a Linux-only dependency, and a contract with an icon, an Info.plist template and artwork. CI builds it
  through the wizard and checks:
  - the preselected product, and only the app in `Contents/MacOS`;
  - `AppIcon.icns` and the artwork in the bundle, and the template keys kept;
  - a full seal;
  - APP, ZIP, DMG and PKG, with the PKG installed;
  - a GUI launch that finds its artwork.

  The real HCG Store Manager (private) is built at a pinned commit when the read-only secret
  `HCG_STORE_MANAGER_READ_TOKEN` is configured; until then it is reported as pending.

### Fixed
- **Info.plist template's icon name kept.** A contract template that already names the installed icon
  (`CFBundleIconFile` `AppIcon` or `AppIcon.icns`) keeps its own spelling instead of being rewritten to `AppIcon.icns`
  (HCG Store Manager's template uses `AppIcon`; both resolve to `Contents/Resources/AppIcon.icns`).
- **iOS signing refusal names every gap.** When the Apple Signing Assistant refuses a device build or development IPA,
  the missing certificate found before it ran ("no code-signing identities were found for this destination") is part of
  the refusal again, next to the missing team, instead of only a warning in the log.
- **A Debug distribution archive is refused by its rule first.** The build verification that runs before the Apple
  Signing Assistant refused a Debug distribution archive only for the development flag it would ship; the refusal now
  starts with the rule ("… must be a Release build") and keeps the flag as evidence.

## [Unreleased] — Build Provider (HCG Server Build Host in the New Build wizard)

### Added
- **Build provider** selector on the New Build wizard's Inspect step: Local Machine (default, unchanged), HCG Server
  Build Host, and GitHub Actions (listed, not available yet). See docs/BUILD-PROVIDERS.md.
- **HCG Server Build Host** goes Inspect → Build. The panel offers:
  - authorized discovery on loopback, with the build-client token stored in the OS credential store and never shown again;
  - Test connection, with distinct states (token needed, token refused, not `hcg.buildhost/1`, not loopback, not reachable);
  - isolation, queue, toolchains, providers and the registered projects;
  - the host's device, OS/architecture and isolation rules from verified provenance;
  - the signing key, with **Pin this key**;
  - the resource limits;
  - the registered project matched to the imported one (same working copy, or same repository and folder);
  - the commit recorded at import;
  - target OS/architecture per provider;
  - operations and options.
- **Explicit authorization before submitting.** The statement names the host, key, commit, repository, source remote,
  operations and provider, and says that Executable Maker sends no source files. It is cleared by any change and used
  once.
- **Running reuses `HcgServerBuildHost`**, writing the same `native-build-request.json` / `native-build-result.json`.
  - The plan and the redacted log stream into the build view.
  - The wizard shows that the job waits for the owner's approval on HCG Server.
  - Cancel Build cancels the job there.
- **Results: Build provenance and evidence.**
  - Job and approver; each step with exit code, time, peak memory and CPU.
  - The verified provenance, the host key check, device and OS/architecture.
  - Source commit and tree, toolchain, validation, artifact SHA-256s, Release Passports, HCG Repo evidence and the
    contract files.
  - **Unsigned — not ready for public distribution**: Ship to Store is not offered for this output.
- **Toolchain Manager.** The wizard's Build Readiness panel shows the Build Host's compatible version next to a tool
  that is missing or too old here (Go 1.25 for the HCG Server-shaped helper).
- `MakerFeatures.BuildHostServer` (`buildhost.local`, free, decided by HCG Server's owner). Proposed for founder review,
  not registered: a central key for non-loopback build hosts, and OS/arch, key and limits in the Build Host's status.

### Fixed
- **Keyring not reachable.** When the OS keyring exists but cannot be reached (no D-Bus session), the Build Host token
  is kept for this run only and the panel says so. Before, the connection test failed with an exception.
- **Commit in the request purpose.** A Build Host request's purpose text now uses the normalized (lowercase) commit.
- **Test on Windows.** The SigningProbe fixture test no longer depends on line endings.
- **Unsigned iOS archives.** Unsigned archives (Mobile Build Studio, `mobile-build --output ios-archive --signing
  unsigned`, the Sentinel archive matrix) again pass `CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO
  CODE_SIGN_IDENTITY=`. The Apple Signing Assistant's arguments replace the planner's, and its unsigned branch had
  dropped them, so Xcode asked for a development team.
- **Device builds without a certificate.** For native Xcode iOS projects, a signing gap found by the generic
  capability check (for example no Apple Development certificate in the keychain) is now decided by the Apple Signing
  Assistant. It runs after the build's own verification (expected commit, development flags), knows that authorized
  Xcode-managed provisioning can create the development certificate, and reports what is missing with Resolve
  Signing. Before, the build stopped earlier with "not available".

## [Unreleased] — Apple Signing Assistant

### Added
- **Apple Signing Assistant** (Mobile Build Studio, native Xcode iOS projects). Before compiling it reads the effective
  build settings (`xcodebuild -showBuildSettings`, so xcconfig files and `Config/Local.xcconfig` count), the project's
  Signing Vault profile, and this Mac's identities and profiles. It then decides the signing for the destination and
  passes it as build settings for this build only; the project is never modified. See docs/APPLE-SIGNING-ASSISTANT.md.
- **Build destinations:**
  - iOS Simulator (Debug);
  - Physical iPhone / iPad (Debug): a new **iOS device build**, signed with Apple Development;
  - iOS distribution archive;
  - TestFlight and App Store: the App Store Connect .ipa, exported locally and never uploaded;
  - macOS development and Developer ID (existing macOS Xcode provider).
- **Debug/Release are explicit.** Simulator and device builds default to Debug, so development features such as
  Unimatch's Development Run stay available. Distribution builds must be Release: a Debug distribution build, or a
  Release configuration that compiles with `DEBUG`, is refused before compiling.
- **Simulator builds never need a team.** A project without one is signed to run locally (ad hoc, entitlements kept).
- **Device signing checks:**
  - team (chosen, saved in Signing Vault, or the project's own);
  - Apple Development identity with its private key;
  - development profile for the bundle identifier;
  - entitlements;
  - registration of intended devices;
  - iPad family.

  Xcode-managed provisioning (`-allowProvisioningUpdates -allowProvisioningDeviceRegistration`) is used only when
  authorized.
- **"Use team …"** for teams with a usable identity on this Mac. The chosen team is remembered in the project's signing
  profile.
- **A bundle identifier for one build** overrides the project's identifier variable (for example `UNIMATCH_BUNDLE_ID`).
- **Resolve Signing.**
  - Missing teams, certificates, profiles or device registrations, before or during the build, including xcodebuild's
    "requires a development team" and "No profiles for", become `HCG-SIGNING-RESOLVE`.
  - **Resolve Signing** opens Signing & Security for the project, and the build can be started again without
    re-importing.
- `mobile-build`: `--output ios-device`, `--device-family`, `--bundle-id`, `--allow-provisioning-updates`,
  `--destination app-store`.

- **Development builds are verified before compiling** (Unimatch: the Development Run was missing because the device
  build was a Release archive):
  - The source commit of the imported copy is checked. A stale copy is refused, and an optional expected commit can be
    given.
  - The compilation flags are checked. Flags that are in Debug but not in Release are the project's development-only
    flags (`UNIMATCH_DEVELOPMENT_RUN`).
  - A development build reports `DEBUG` and those flags as active, and optional expected flags must be active.
  - A distribution build must contain none of them.
  - The build records the source commit, configuration, compilation flags and development-only flags. Mobile Build
    Studio shows them under "What this build is".
- **A development-signed .ipa** (export method Debugging) is a device development build: Debug by default, not a
  distribution build.
- **Install and open on a connected device.** After a development build, Mobile Build Studio lists paired iPhones and
  iPads (devicectl) and installs the build after confirmation, then opens it. The device is added to the project's
  intended devices in Signing Vault, so later builds check its registration in the development profile.
- `mobile-build`: `--expect-commit SHA`, `--expect-flags DEBUG,FLAG`.

### Changed
- A signed iOS archive without a development team now stops before compiling with the reason and Resolve Signing.
  Previously it failed inside xcodebuild with exit code 65.

### Tests
- 22 Signing Assistant tests:
  - Unimatch's effective settings;
  - every destination;
  - team source order;
  - provisioning authorization;
  - private key, profile, entitlements, device and iPad checks;
  - the Debug distribution refusal;
  - bundle-identifier variable override;
  - xcodebuild error recognition;
  - the device-build planner.
- CI (macOS) with the Unimatch-shaped `tests/Fixtures/SigningProbe`:
  - Simulator Debug without a team (ad hoc, contains the Development Run);
  - device Debug stopped without a team;
  - Xcode's signing error becomes Resolve Signing;
  - Debug archive refused;
  - Release archive without the Development Run.

## [Unreleased] — Build Readiness Center

### Added
- **Build Readiness Center** (Toolchains › Build Readiness, the New Build wizard, `--build-readiness`).
  - Before compilation it checks every tool the build needs. Requirements are read from the packaging contract and
    the scripts it runs, `go.mod`, `Package.swift`, `package.json`, `Cargo.toml`, `pyproject.toml`, `CMakeLists.txt`,
    `global.json` and the chosen build kind.
  - For each tool it shows the installed and required versions, its path and where the requirement comes from.
  - Go, Swift, Xcode, .NET, Node.js/npm, Python, Rust/Cargo, JDK, CMake, Ninja, Clang, GCC, make, lipo, Git, Docker
    and pkg-config are found on PATH and in their install locations: Homebrew (Apple Silicon and Intel), official
    installers and version managers. See docs/BUILD-READINESS.md.
- **Missing tools are explained, not just a failed build.** The panel offers:
  - **Install…**, only after approving the exact command: a Homebrew catalogue formula, which upgrades nothing, or
    Apple's Command Line Tools;
  - **Choose for this project / all projects** (verified before saving);
  - the official download and **Refresh Toolchains**.

  Start Build resumes once the tool is ready. HCG never installs Homebrew or runs installer scripts.
- **Toolchain profiles:** reusable per-project tool locations, so projects can use different SDK versions without
  replacing a shared installation.
- **Remote build hosts:** authorized hosts that report a compatible tool are listed. Nothing is sent.
- **The packaging-contract build uses the same resolution.** HCG Server's Go helper now builds with Go from Homebrew,
  `/usr/local/go`, `~/sdk` or a chosen location. A missing tool names Build Readiness (`HCG-READINESS`).

### Tests
- 44 Build Readiness tests:
  - Go discovery on PATH, in Apple Silicon and Intel Homebrew, from the official installer and golang.org/dl, and at a
    chosen location;
  - version parsing and choosing a compatible installation among several;
  - installer safeguards;
  - HCG Server's contract (go.mod, script checks, universal lipo);
  - profiles;
  - remote hosts.
- CI `build-readiness-go` (macOS) with the HCG Server-shaped fixture `tests/Fixtures/AgentHelperApp`: missing Go
  blocks Start Build, Go is chosen for the project, then the universal helper is built, the app is launched and the
  helper is run. A Homebrew install of Go runs only with `--approve`.

## [Unreleased] — HCG Server Build Host

### Added
- **HCG Server's Build Host** (`hcg.buildhost/1`, HCG-Server 417a9cd) is an `IBuildHost` (`HcgServerBuildHost`,
  kind `HcgServerHost`) next to this computer. GitHub Actions stays an option.
  - Executable Maker submits a job for a pinned commit and shows "Waiting for approval on HCG Server
    (hcg-agent build approve <job> --confirm <job>)". It never approves.
  - It follows the job and its log.
  - It downloads artifacts and checks each one against the job record, `X-HCG-SHA256` and the bytes received. A
    mismatch deletes the download.
  - It verifies the ES256-signed provenance: the key fingerprint in the envelope and in the payload, and an optional
    pin.
  - Loopback only, no proxy, no redirects. The client token is kept in the OS credential store.
- **The Build Host writes the shared native build-provider files**: `native-build-request.json` and
  `native-build-result.json` (provider kind `hcg-server-build-host`, the owner's approval from the signed provenance,
  scope build/test/package, isolation as limits). It also writes the signed provenance, the validation report and
  Release Passport build halves for distributable outputs. The native-build-request schema was widened (more project
  kinds, `unsigned`, optional `timeoutMinutes`, `limits.isolation`).
- **Unleashify handoffs of Build Host builds** (`PackageProvenance.FromBuildHost`): `submission.json` and the passport
  say `provider = "hcg-server-build-host"`, with no run ID, artifact name or run repository. They go through Ship to
  Repo like local builds.
- `HCGExecutableMaker --build-host status|jobs|token|run`. See docs/HCG-SERVER-BUILD-HOST.md.

## [Unreleased] — Distribution Studio final qualification

### Security
- **The stored HCG session is bound to its issuer.**
  - A session from another issuer is never refreshed: its refresh token is never sent, and the session is deleted.
  - Sessions stored before this change restore as before and gain their issuer at the next refresh.
  - Production (`session`), development (`session-development`) and TEST (`session-local-test`) sessions were
    already separate entries.
- **A sign-in goes only to a Store of the same kind** (`StoreSessionPolicy`): a production sign-in is never sent to
  a Store on this computer, and a TEST sign-in never to a real Store. Diagnostics, the CLI and the upload button
  explain the refusal.

### Added
- **Maker's own build provenance is embedded at packaging time** (`build-source.txt` → assembly metadata):
  - it records the full commit, whether the tree had uncommitted changes, and the GitHub repository;
  - `--version` prints `Source: <commit>`;
  - `--release-passport --self` uses it, needs no git, and is valid only for the running app's own bundle;
  - a checkout given with it must agree, or no passport is written.
- **Projects Maker packages keep their own git provenance.** Without git or a commit, the source is unknown; nothing
  is guessed.
- `--release-passport` covers `.pkg` and `.dmg` archives as well as `.zip`.
- **Acceptance test 16b:** a revoked central `store.publish` refuses the next upload, even with a stored session.
- **CI (Package macos):**
  - the packaged app's passport must name the build commit;
  - the same with git removed from PATH;
  - a project without git is recorded as source unknown;
  - `--self` is refused for another bundle.
- **Docs:**
  - `PRODUCTION-COMPATIBILITY.md`: the live Store and what Maker does against it;
  - `HCG-MAIL-MACOS.md`: implemented, partial and unsupported steps for HCG Mail on Apple Silicon;
  - the M4 Max acceptance checklist.

### Fixed
- `--release-passport` resolves git to an absolute path; it no longer aborts.
- The macOS acceptance run clears only the TEST Keychain entry between accounts and Stores.

## [Unreleased] — Distribution Studio delivery

### Added
- **Store contract discovery**: Distribution Studio works with three contracts, chosen from HCG Store's own
  capabilities and never assumed.
  - **Deployed contract (drafts and uploads only).** Availability changes are explained; Ship to Store is never
    disabled.
  - **HCG Store main's release lifecycle** (`capabilities.operations`):
    - **Disable** and **Enable** go through HCG Store's endpoints from Maker, with a reason, the typed version, a
      confirmation and the release revision as `If-Match`;
    - **Supersede** and **Restore previous version** are publication decisions made in HCG Store's browser review,
      so Maker opens the release there;
    - "Make current / supersede" is sent as `supersedePreviousOnPublish` with the draft;
    - per-target current releases are read and shown.
  - **The proposed `claude/distribution-studio-api`** contract: recorded requests.
- **Release management in the diagnostics**: one line saying how this Store lets apps manage releases.
- `--release-passport --app X.app --file X.app.zip`: the Release Passport build half for an existing macOS app archive.
  It records the measured SHA-256, size, bundle identity and architectures, the code signing as the Signing Doctor
  reads it, and the Git source.
- **CI evidence:**
  - the macOS package job opens the packaged app on Distribution Studio and captures the window;
  - it issues the app archive's own Release Passport;
  - the Distribution Studio job runs on Ubuntu and macOS ARM64, and also against HCG Store main's TEST server (checks
    M1–M5).
- **UI acceptance screenshots** for upload progress (mid-upload, with transfer speed) and Add to Listed Product (open
  draft, and a new revision for a locked release).

### Changed
- `--store-request` follows the discovered contract. An action the Store offers only in the browser, or not at all,
  is explained (exit 7) and nothing is changed. A direct change needs `--confirm <version>`.
- `docs/DISTRIBUTION-STUDIO-PRODUCTION-TEST.md` is now the 10-step MacBook Pro procedure.

## [Unreleased] — Distribution Studio (Ship to Store)

### Added
- **Distribution Studio**: the Ship to Store page (the sidebar entry keeps its name), with the HCG logo:
  - connection;
  - the products you may manage;
  - an operation selector: **Add New Update**, **Add New Product**, **Add to Listed Product**, **Manage Existing
    Versions**;
  - release history and version comparison;
  - release configuration;
  - artifacts;
  - upload to private staging;
  - the **Synchronization Center**.

  Opening it uploads nothing and changes no release.
- **Catalogue from HCG Store**:
  - shows official name, icon, identifier, category, status, platforms, editions and current release;
  - search, status and platform filters, and **Refresh**;
  - with the Store's `productMetadata` capability the metadata comes from the product list; otherwise the summary,
    status and platforms come from the Store's public release resolution;
  - icons are loaded only from the Store's own origin, and nothing is kept as a local copy.
- **Version comparison** (`DistributionStudio.Compare`):
  - shows the current published release (the one the Store names current, else the newest published), the latest
    private draft, the latest local build, the proposed version, and the published platforms and architectures;
  - refuses duplicates and names the draft a retry continues;
  - flags revisions, downgrades, identical bytes already in the Store, and platform overlaps;
  - a newer local build never disables anything.
- **Metadata discrepancies**: a typed version, build or architecture that differs from the Release Passport or the
  package is shown and recorded in the draft's compatibility notes. Verified facts are never overwritten.
- **Artifact sources**: latest build, Build History, Artifact Vault, files from disk, several per draft, no rebuild.
  Each file is labelled **Maker build** or **Selected manually**, and provenance comes only from a passport of the
  exact bytes.
- **Publication request**: "Make this the current release after approval and publication" and "Supersede previous
  compatible release". Sent only to a Store with the `publicationRequests` capability, and applied by HCG Store only
  when a person publishes.
- **Availability requests**: disable, restore, supersede and make current, with the version typed to confirm. HCG Store
  records them for the people who approve and publish; they change nothing.
- **Add to Listed Product**: an open draft takes the files. A published release is never changed; a new revision is
  prepared instead (same version, next build).
- **Add New Product**: a private product draft (descriptive fields only to a Store that takes them), plus a central
  **registration request** for HCG (name, description, category, platforms, edition, initial version, icon and banner
  with SHA-256). It is not uploaded.
- **Upload transfer speed**, plus **Open in Store Release Manager** and the website's Release Manager page.
- **Command line**: `--store-catalogue`, `--store-history`, `--store-compare` (exit 6 when the Store would refuse),
  `--store-request`, `--make-current`, `--supersede-compatible`, `--add-to-release`, `--compat-note`, and
  `--description`, `--category`, `--platform` and `--registration-out` for `--draft-product`.
- **HCG UEM readiness**: the diagnostics show this device's description marked "reported, not attested". Maker is not a
  device trust authority.
- `tools/ci/distribution-studio-e2e.sh` and the CI job `distribution-studio`: acceptance tests 1–18 against the TEST
  HCG Store with the real catalogue (HCG Mail), genuine Windows x64 and ARM64 executables, and screenshots of the page.
- `docs/DISTRIBUTION-STUDIO-PRODUCTION-TEST.md`: the controlled production test on the MacBook Pro (one private
  draft, nothing published).

### Changed
- **Connection diagnostics follow the live HCG Store contract**. Production has no `/publishing/status`; it answers
  `401` there. There are now ten checks:
  - server availability;
  - live API version;
  - publishing mode (`disabled` · `uploads` · `enabled`);
  - private upload availability;
  - HCG Connect authentication;
  - Executable Maker client authorization (the issued client `fb9b6edf-…`, never replaced; a Store refusal names
    `HCG_STORE_PUBLISHING_CLIENT_IDS`);
  - central publisher permission;
  - authorized catalogue;
  - Release Manager connectivity;
  - public publication.

  They are read from `health`, `whoami`, `capabilities`, `products` and `manager`, and capability flags are never
  assumed. When everything is OK the diagnostics fold away.

### Removed
- `tools/ci/ship-to-store-private-e2e.sh` and the CI job `ship-to-store-private`. They targeted the deleted HCG Store
  branch `claude/private-submissions`; production uses `uploads` mode instead.

## [Unreleased] — Ship to Store connection repair

### Added
- **Connection diagnostics** on the Ship to Store page and `--ship-to-store --store-status`: eleven separate checks
  instead of one status (Store connectivity, API version, HCG Connect sign-in, Maker's own client registration, token
  validity, publishing permission, authorised products, private draft creation, artifact upload, release submission,
  public publication). Each one shows HCG Store's actual answer to the request, what to do, and who does it.
  **Copy diagnostics** copies them; the text contains no token, cookie or credential. The checks are read-only and
  nothing is created.
- **Products from HCG Store with Refresh**: the actual list of products and editions HCG lets the account submit to.
  The selection is kept on refresh. A draft product is only for products that are not in the catalogue.
- **Release Passport attachment**: after HCG Store verifies a file, the passport next to it is attached. HCG Store
  matches it to the stored bytes (SHA-256 and SHA-512). A missing or refused passport never fails the upload.
- **Release Manager read-back**: after an upload or a submission, the page and the command line show what HCG Store's
  Release Manager lists for the release (stage, files, audit trail).
- `tools/ci/ship-to-store-private-e2e.sh` and CI job `ship-to-store-private`: a genuine executable through the whole
  private flow against the TEST HCG Store, with public publication off.

### Changed
- The authentication checks verify that the token was issued to Maker's own client (`hcg-executable-maker-desktop-*`).
  A token issued to HCG Store Manager or to HCG Store's web sign-in is refused.
- Signed out, opening Ship to Store contacts no Store; **Check connection** runs the diagnostics.

## [Unreleased] — Windows packaging, Authenticode and the deployed HCG Store

Reusable parts from HCG Mail for Windows 0.1.0-beta.1 (docs/WINDOWS-PACKAGING.md).

### Added
- **Generic headless packaging:** `--package-windows`, with the product's own identity (`--identity app.json` or flags).
  Nothing of Executable Maker's name or icon goes into the product, and the installer's upgrade identity is unchanged.
- **Packaging of already-built folders** (`--app-dir --arch`), without building. The program's PE machine type must
  match the target, and the folder is never changed.
- **PE facts:** machine type, PE32/PE32+, subsystem, certificate table and version resource (`PeFile`).
- **Bootstrapper and installed program are told apart:**
  - the Inno Setup checks report the x86 loader separately from the installed program, whose architecture and
    version must match the target;
  - the Store takes a Setup.exe's architecture from its passport, never from the loader;
  - portable ZIPs report their programs' architectures.
- **Signing Doctor for Windows** (`--windows-signing-doctor`), the same on every OS. It checks the PE image digest
  (a changed file fails), the signer's signature, an RFC 3161 timestamp or countersignature, and the chain for code
  signing (trust reported apart from integrity). On Windows, Windows' own verdict is added. Store detection uses it
  for `.exe` and Windows ZIPs.
- **Authenticode signing** (`--authenticode-sign`, `--package-windows --approve-signing`): signtool with SHA-256 and an
  RFC 3161 timestamp, with the person's own certificate by thumbprint. It runs only with an approval tied to the
  file's SHA-256, and is verified afterwards.
- **Windows Release Passport** for each installer and ZIP, from `--package-windows` and from every Windows .NET build.
  It records the installed program, the bootstrapper, Authenticode for the file and each program, source, provider
  and toolchain.
- **Store submission manifest** (`store-submission.json`, `hcg.store-submission` v1). It holds the exact release and
  artifact bodies of the deployed HCG Store's API, for a private draft only, with provenance kept apart.
  `--ship-to-store --manifest` re-checks every file first. `--ship-to-store --store-status` reads the server's health
  and publishing answer.
- **Deployed HCG Store (835ac1a):**
  - its strict artifact schema refuses build provenance; on exactly that answer, Maker declares again without it and
    says so;
  - `publishing_disabled` is reported as such.
- CI job `windows-packaging` on a real Windows runner:
  - an already-built fixture is packaged for x64 (signed after approval, with a throwaway certificate and a real
    timestamp) and for ARM64 (unsigned);
  - Maker's Authenticode verdicts are compared with `Get-AuthenticodeSignature` on Microsoft-signed `dotnet.exe`, on
    the self-signed Setup.exe and on a tampered copy;
  - the job checks bootstrapper vs installed machine types, passports, the manifest and checksums, and runs the
    install, launch and uninstall test.
- Regression tests: synthetic PE images with version resources, and signtool-shaped Authenticode signatures
  (tampering, forgery, countersignature). They also cover already-built folders, passports, Store detection, the
  manifest, and a fake of the deployed Store's strict schema.

### Fixed

- **`--version` no longer swallows a command.** `--package-windows … --version 0.1.0-beta.1` printed Executable Maker's own
  version, exited 0 and packaged nothing; HCG Mail's prerelease run showed this. Commands are now matched first, and
  `--version` means Maker's version only when no command is named. The same applied to `--ship-to-store` and
  `--xcode-mac`. Regression tests check this, and the Windows CI job passes `--version` explicitly.
- **Cancelling a process tree on Linux kills what it starts at the last moment.** On Linux the root is paused while its
  tree is collected, then killed with SIGKILL sent directly to its process ID, with SIGCONT as a backstop. It is never
  left stopped. On macOS, pausing hung CI test runs, so macOS keeps its previous behaviour: collect the tree, then kill
  every member. On macOS a query that a cancelled `xcodebuild` started just before it died can still outlive it
  briefly. The DeskLoop cancellation check now allows 10 s before it fails.
- **The Store summary for a Windows file says "Windows PE".** It used to call the binaries "Mach-O".
- The Inno Setup "Code signature" check verifies the signature instead of only noting that a signature block is
  present.

## [Unreleased] — HCG Impactor

### Added
- **Ship to Repo** (Unleashify › Build output, `--ship-to-repo submit|status|show`), under HCG Repo's Executable Maker
  submission contract (`docs/EXECUTABLE_MAKER_SUBMISSION.md` in HCG Repo).
  - **Submit:** `release submit --handoff … --result-json …`; HCG Repo validates the exact .deb into its private
    incoming store. **Status:** `release status <id>`.
  - Maker shows the state, the SHA-256, HCG Repo's **six evidence levels separately** (the script-free lifecycle is
    never presented as device evidence), the findings and the owner's next commands.
  - **Approval of the exact SHA-256, Candidate preparation and publication are the owner's**, run in HCG Repo. Maker
    has no approve or prepare operation (`--ship-to-repo approve|prepare` is refused with an explanation).
  - `HCG_REPO_INCOMING` is passed to HCG Repo, so Maker's submissions are in the same store as the owner's commands.
- **HCG Release Passport v1** (`docs/RELEASE-PASSPORT.md`): schema pinned byte for byte from HCG Repo 3bf53c7.
  - **Build half:** every Unleashify handoff carries `release-passport.json`. It holds the artifact, SHA-256, source,
    provider, toolchain, Maker's validation evidence and `code_signing` (each Mach-O's signer, verified pages,
    identifier, cdhash and entitlements). It states no repository signing, approvals, publication or device results.
  - **Ship to Repo:** once the owner has prepared a release, Maker reads HCG Repo's own passport and reconciles it with
    the build half. It shows `repository_signing` exactly as HCG Repo states it, apart from `code_signing`.
  - **Ship to Store:** HCG Store v2's source commit, build provider, toolchain and validation report are filled from a
    passport of the file's exact bytes (or `--passport`), and left empty otherwise.
  - **Checks:** version compatibility (newer versions refused with a reason; extra fields accepted), and a schema
    evaluator cross-checked against Python `jsonschema`.
- **CI:** HCG Repo's Release Manager review runs on Linux. HCG Repo 3bf53c7's script-free lifecycle upgrades from the
  version in `pool/`, and macOS refuses dpkg's backup hard links inside an installed `.app`.
- **Signature integrity** check in rootless package validation: every Mach-O signature's page hashes and special
  slots are recomputed against the files (`MachOSignatureAudit`). It fails a binary changed after signing, an
  inconsistent signature, or a main executable with library validation or hardened runtime loading an ad hoc or
  differently signed framework. VALIDATION.txt lists each binary's signer, verified pages, identifier and cdhash.
- **DEVICE-TEST.md:** "If the app closes at launch": how to collect the crash report and read its termination
  namespace (CODESIGNING, DYLD, EXC_CRASH, FRONTBOARD) before changing anything.
- Regression tests for a .NET/Avalonia iOS bundle shaped like HCG Mail Unleashed (unsigned main executable,
  SkiaSharp/HarfBuzzSharp frameworks with their own ad hoc signatures, AOT data, managed assemblies) and an opt-in
  check of the published HCG Mail Unleashed 0.1.0~beta1 package (`HCG_MAIL_UNLEASHED_DEB`, read only).
- **HCG Repo Distribution v2 submission manifest** (`submission.json`, `hcg.repo.artifact-submission` v1) in every
  handoff with a pinned, clean source; validated by Maker and by HCG Repo's own `validate_manifest` in CI. It carries
  the `signing` claim (method `ldid`, the files Maker signed, each binary's exact entitlements), and the `package.toml`
  draft records the same entitlements under `[review.entitlements]`. A local handoff's draft has no `[access]
  exception`: the owner approves a free demo in HCG Repo.

### Fixed
- **icon.png** was Apple's CgBI PNG, copied from the Xcode build, which browsers and Sileo cannot decode. It is now
  converted to a standard RGBA PNG and verified with a standard decoder (square, at least 60 pixels). Approved
  source artwork is the fallback (`--icon-artwork`, or Package › Icon artwork with a suggestion from the project's
  AppIcon set).
- **DEVICE-TEST.md** searched for "sentinel" and wrote `sentinel-test-*` snapshots for every product. All
  instructions now use the actual product name, bundle ID, package ID, version and install path. The installed-build
  check matches the product's own family.
- **control.txt** wrote multi-line descriptions without Debian continuation lines, so HCG Repo's handoff reader saw
  no dependencies.
- **provenance.json** now records the app's toolchain (from `DTXcode`) for local builds, which HCG Repo requires.

- **Unleashify on your Mac, without GitHub Actions** (`docs/UNLEASHIFY-LOCAL.md`). This uses the same packager,
  validator and handoff as CI.
  - **Pinned commit:** a clean checkout of one exact commit of your local repository (read only), imported and
    recorded.
  - **Lifecycle simulation:** install → configure → upgrade → remove in a throwaway folder; Maker's scripts run with a
    recording `uicache`.
  - **Local provenance:** built by, toolchain read from the app's `DTXcode`, time, host label without computer or user
    names, source visibility, validation. No CI run or artifact is recorded.
  - **HCG Repo handoff** through Ship to Repo (HCG Repo's Release Manager; `--via local-file` stages the .deb for
    Prepare packages). Maker writes no import request of its own for a local build.
  - **Owner approval bound to the exact SHA-256**, given in HCG Repo (`release approve`), not in Maker.
  - **HCG Repo import preview:** HCG Repo's own importer, dry run, on a throwaway copy of your checkout.
  - The same in the CLI: `--rootless-deb … --local --source-visibility … [--approved-sha256 …] [--hcg-repo …]`.
- `IBuildHost` / `LocalBuildHost`: the seam for a future owner-authorized HCG Server build host (proposal in
  `docs/BUILD-HOSTS.md`).

### Changed
- Creating a profile with the Xcode account registers a device only when you tick **Register the connected device**
  (command line `--register-device`). The approval dialog names the App ID and device registration.
- Impactor labels the team when a signature records none (codesign records a team only for Apple-issued
  certificates).
- Windows installer test: a `taskkill` child that had already exited no longer fails the test.

- **HCG Impactor** (sidebar › HCG Impactor; `--impactor` on the command line): inspect, sign, install and diagnose iOS
  and iPadOS apps on your own devices. Flow: Drop IPA → Inspect → Sign → Select device → Install. See `docs/IMPACTOR.md`.
  - **IPA Inspector** (all platforms): identity, version and build, minimum iOS, device families, architectures,
    Simulator/Mac/32-bit/App Store-encrypted binaries, every extension, framework and dylib, embedded signatures
    (identifier, team, signer, chain, signing time, entitlements), embedded profiles (verified), sealed-resource
    integrity and safe extraction. The original IPA is never changed; `.deb` files go to the jailbreak workflow.
  - **Signing Studio** (macOS):
    - Automatic or chosen identity and profiles per bundle; Development, Ad Hoc, Enterprise and App Store export.
    - Every entitlement must be granted by the profile, and every removal is reported.
    - Bundles are signed innermost first with `codesign` and your Keychain identity.
    - `codesign --verify --deep --strict`, then a new IPA, inspected again.
    - Warnings for bundle ID and team changes.
  - **Device Manager / Deployment Queue** (macOS, Xcode `devicectl`):
    - Device details: model, iOS, connection, pairing, Developer Mode, capabilities, registration in installed
      profiles; guidance for Trust, pairing and Developer Mode.
    - Pre-installation validation; installation with progress, cancellation, optional launch and batch mode; never
      removes apps.
  - **Certificates, Provisioning, Signing Monitor** (expiry reminders, manual re-sign and reinstall), **Installation
    History** and **Diagnostics**: 15 failure types explained, original output redacted, and an export in
    `hcg.diagnostic-report/1` plus Markdown.
  - **Signing Doctor** (Impactor, Mobile Build Studio › Signing Doctor…, `--impactor doctor`): for one bundle ID,
    every installed profile is checked for bundle ID, team, certificate, certificate and profile expiry, registered
    device, development vs distribution, entitlements, platform and signature, with the exact reason and next steps.
  - **Create profile with Xcode** (`--impactor provision`): with approval, a throwaway stub project is built once with
    `xcodebuild -allowProvisioningUpdates` using the Apple Account signed in to Xcode. Xcode registers the App ID
    (and the connected device) and downloads a Development profile. No manual temporary project; nothing is revoked
    or installed.
  - Shared device record **`hcg.apple-device/1`** (`docs/contracts/`) proposed to HCG System Tester. "Open in System
    Tester" uses its existing `hcgsystemtester://open/connected-systems` route.
  - `src/HCG.Impactor` is a self-contained module (no UI), ready to become a standalone app.
- CI job **`impactor`** (macOS), using real Apple tools with a synthetic identity and profile; no physical device on CI:
  - a real Xcode-built device app is inspected and signed;
  - Apple's `codesign` verifies the result, and a tampered copy is caught;
  - real `devicectl` output is translated;
  - the workspace signs through Signing Studio.

  Physical-device installation is to be validated on the owner's Mac (`docs/IMPACTOR.md`).

## [Unreleased] — Xcode projects that only wrap another build (HCG Mail)

### Fixed
- **HCG Mail's iOS app is found again when its repository contains `HCGMail.xcodeproj`.** That project only runs the
  .NET build through External Build System targets. Mobile Build Studio treated it as the iOS app, found "no iOS
  application target", and blocked every iOS output.
  - An `.xcodeproj` whose targets are all External Build System or aggregate targets (no native target) now only wraps
    another build. It no longer leads detection, and the .NET iOS project it wraps is attached as before.
  - The analysis names the wrapper in an Info diagnostic.
  - Real iOS app projects, which have a native target, are unaffected.
  - The "HCG Mail like" fixture now includes the project.

## [Unreleased] — Xcode project for the macOS app

### Added
- **`macOS Xcode Project/HCGExecutableMaker.xcodeproj`**: open HCG Executable Maker in Xcode, choose the **HCG Executable Maker** scheme and press Run. An External Build System target runs `Scripts/build-app.sh`, which builds the real .NET/Avalonia app for osx-arm64 with the repository's own self-packaging pipeline (no second build system), validates the bundle (Info.plist, bundle ID `com.hakmicapital.executablemaker`, version from `Directory.Build.props`, arm64, bundled .NET runtime, icon, signature, `--version`), and skips rebuilding when nothing changed. A small Swift launcher target makes Run start the real app in place (its output in Xcode's console, Stop quits it). Debug and Release schemes; Release adds a `.app.zip` and SHA-256 and optional Developer ID signing (`HCG_CODESIGN_IDENTITY`); nothing is published or notarized. See `macOS Xcode Project/README.md`.
- `self-package.cs`: `--configuration Debug|Release` and `--app-only`.
- CI `xcode-project`: xcodebuild Debug and Release builds on macOS 26 (arm64), bundle checks, an up-to-date second build, Run as Xcode does with a screenshot of the app's window, and a failure-path check; `XcodeProjectFileTests` checks the project file and schemes on every platform.
## [Unreleased] — Ship to Store diagnostics

### Fixed
- Ship to Store names the exact reason publishing cannot be used instead of "Publishing not available": no HCG Store server at the address (a web page answered — today's case for store.hakmicapitalgroup.com, which serves the static storefront), publishing switched off on the Store, publishing API not found, sign-in not accepted, no publishing permission, server error, DNS failure, TLS failure, connection failure or timeout. Network problems and server errors are never shown as refusals. See docs/SHIP-TO-STORE.md.
## [Unreleased] — Project detection

### Fixed
- Projects inside test-fixture folders (`Fixtures`, `__fixtures__`, `testdata`, …) are never offered as a repository's app. Importing HCG Executable Maker itself into Mobile Build Studio offered a test fixture (0.3.1: `tests/Fixtures/DotNetIos/HCG Mail like`; 0.4.0: `HelloDotNetIos`) as "the" iOS app, and attached a Theos route and an Electron note from fixtures. It is now reported as an Avalonia desktop app (built in New Build), with its fixture mobile projects listed as test samples.
- "No iOS application target was found in the Xcode project" now says what the project contains and what to do: an unreadable `project.pbxproj` (invalid project), a project without native targets, only frameworks/extensions/tests (each target listed with its kind and SDK), a macOS application (New Build or Porting Studio), or a Capacitor app without its iOS project.

## [0.4.0] — 2026-11-21 (release candidate; publication requires the owner's approval) — Build Doctor and Unleashify

Identify this build with `--version` (or the Account page footer): `HCG Executable Maker 0.4.0… (commit …)`.

Highlights:
- **Build Doctor** in Mobile Build Studio: dependency diagnostics across the project graph (references, MSBuild
  properties and imports, source files, SDK/global.json, target framework, iOS workload and its pin, Xcode, restore,
  runtime identifier, bundle ID, entitlements, signing and provisioning), each with root cause, affected file,
  severity, repair mode and verification.
- **Approved repair workflows**: review → approval → backup → apply → re-check → rollback; nothing is installed or
  changed without approval.
- **Multi-project (monorepo) import**: an app that references sibling folders is offered an import of the repository
  root, keeping its layout (HCG Mail's iOS app now builds for the Simulator).
- **iOS workload detection**: the pinned .NET iOS workload and Xcode version are read from the project
  (rollback file or toolchain props) and shown with the exact install command, run only on approval.
- **Mobile Build Studio UI**: readiness summary, actions and expandable findings instead of repeated error blocks.
- **Unleashify**: rootless `.deb` generation for jailbroken devices from an iOS build, full validation, and the
  existing **HCG Repo Candidate handoff** (publication to HCG Repo stays a separate owner decision).

### Build Doctor

#### Added
- **Build Doctor** in Mobile Build Studio: structured findings (problem, root cause, affected file, severity, repair, approval mode, verification) for project references, MSBuild properties and imports, source files, import scope (monorepos), SDK and global.json, target framework, workload and its pin, Xcode, NuGet restore, runtime identifier, bundle identifier, entitlements, signing and provisioning, and the host. Readiness summary ("3 unresolved project references", "1 missing workload"…) with Run Build Doctor, Review proposed repairs, Install missing workload, Configure signing and Recheck actions; findings expand to technical detail. See docs/BUILD_DOCTOR.md.
- Repairs with review → approval → backup → apply → re-validation → rollback: import the repository root for monorepo apps, reconnect an unambiguous broken reference, restore NuGet packages, install the pinned .NET iOS workload. Evaluate with MSBuild (`dotnet msbuild -getProperty/-getItem`, after trust) confirms every reference.
- Known-good .NET for iOS sample (tests/Fixtures/DotNetIos/HelloDotNetIos) and the current HCG Mail build files as a fixture.

#### Fixed
- MSBuild evaluation: properties and imports are evaluated in order with their conditions, undefined properties are empty, MSBuild path functions are evaluated, imports guarded by property conditions are followed, the environment is never read. HCG Mail's shared projects (HcgSharedDir) now resolve when the repository is imported.
- .NET iOS toolchain pins are read from toolchain props (HcgIosWorkloadManifest, HcgRequiredXcode) as well as rollback files; the exact pinned install command is shown.
- Output cards no longer repeat the same orange block: a reason shared by several unavailable outputs is shown once above them, other cards show only the first sentence of their own reason (full text in the tooltip and in Build Doctor). The "outside the imported folder" reason names the files instead of listing long relative paths.
- After a repair is approved and applied, the review closes (it cannot be approved twice); the outcome, verification and log stay visible.

### Unleashify: rootless packages for HCG Repo Candidate

#### Added
- **Rootless package** workflow (UI page, Mobile Build Studio action, `--rootless-deb` / `--rootless-deb-validate`): an iOS app a build produced (device .app, .xcarchive or .ipa) → rootless `iphoneos-arm64` .deb with gzip members at `/var/jb/Applications` → full validation (paths, dependencies, Mach-O architecture/platform/minimum iOS/signature/entitlements/libraries, versions, gzip and complete extraction, SHA-256) → HCG Repo candidate handoff (provenance, changelog, known issues, package.toml, icon, import request, device test and recovery). The app is packaged unchanged: no added entitlements, privileged ones refused. See docs/ROOTLESS-PACKAGING.md.
- **Unleashify** in Mobile Build Studio (replaces the separate Rootless package page): source revision with a stale-copy warning, the three modes (Jailbreak App available; Jailbreak Tweak and App + Tweak shown as not available, with the reason), build from the Xcode project (scheme, bundle ID and deployment target shown and kept) or from an existing device build (Simulator builds refused), package ID/version, output, test-device compatibility (an iOS 13.3 device is explained as a separate target metadata cannot reach), an installed-build conflict check, **Build** and **Export Candidate Handoff**. See docs/UNLEASHIFY.md.
- Import records the source revision (`import-revision.json`: Git commit/branch, or a GitHub ZIP's commit).
- `tools/unleashify/hcgrepo-dry-run.py`: HCG Repo's own importer (`load_request` + `import_deb(dry_run=True)`) on the exact exported bytes, in a throwaway copy of HCG Repo.
- CI `unleashify-sentinel` (HCGSentinelModern on macos-26, the full click path through the real views, byte-identical CLI and existing-build packages, validation, HCG Repo inspect and importer dry run) and `unleashify-artifact-roundtrip` (the uploaded artifact is the manifest's exact bytes; importer dry run and dpkg sandbox on the downloaded bytes).

#### Changed
- The handoff's `package.toml` follows HCG Repo's current access rules: the production level (`connect` when undecided) and a free-demo `[access] exception` only from the owner's recorded approval of that version (`--owner-approval`, or the field on the Unleashify page). Previously it wrote `level = "public"` (rejected by HCG Repo) or an exception no approval backed.
- `DEVICE-TEST.md` is a complete procedure: detection of existing installations (uicache, dpkg, sideloaded bundles), a dpkg/uicache snapshot, install of the exact exported file (SHA-256 checked), verification, launch, removal with a before/after diff, rollback and a result record.
- HCG Sentinel 0.1.0~beta1: the owner's approval (21 Nov 2026, preparation and validation only) is recorded in `tools/unleashify/approvals/` and entered on the Unleashify page in CI; the HCG Repo dry run uses `--require-genuine-approval` (no placeholder) in both Unleashify jobs.

#### Fixed
- Validation resolves `@rpath` in embedded libraries through the main executable's run paths, as dyld does (an embedded Swift runtime library linking `@rpath/libswiftCore.dylib` was reported unresolved).

## [Unreleased] — Native macOS apps built with Xcode

### Added
- **Native macOS app — Xcode**: import a source ZIP (a GitHub-style enclosing folder is handled) or folder with a
  macOS Xcode project, choose project/target, scheme, configuration and architecture, and build it with `xcodebuild`
  into the project's own `.app`, a ZIP, an optional DMG with an Applications shortcut and the project's licence files,
  SHA-256 checksums and a build report. Release and this Mac's architecture by default (Apple silicon); Universal when
  the SDK builds both. Preflight names exactly what is missing (a Mac, full Xcode, xcodebuild, a macOS SDK at least as
  new as `MACOSX_DEPLOYMENT_TARGET`). Built in a copy of the source; only the listed overrides are passed; helper
  scripts (e.g. `INSTALL.command`) are never run. The app keeps its identity and signature; ad hoc, development and
  Developer ID signatures are reported as such; quarantine and Gatekeeper are never changed. See docs/MACOS-XCODE.md.
- CI job `xcode-macos`: MotionWallpaper 1.0.5 (pinned commit, checked against the uploaded ZIP) through the wizard —
  build verification with Apple's tools, Universal build, cancellation, and runtime checks (launch, MP4 import, desktop
  playback) on the runner's disposable session.
- **Native build provider for Xcode 16 projects** (docs/MACOS-XCODE.md › Native build provider):
  - **Inspection:** synchronized folders (objectVersion 77); local and remote Swift packages with `Package.resolved`
    pins; workspaces that reference the project from another folder; per-configuration facts (bundle ID, signing,
    team, entitlements, App Sandbox, hardened runtime, compilation conditions, generated Info.plist); asset catalogs,
    icon images and entitlement files; each scheme's Test action.
  - **Steps:** `xcodebuild -list -json` checks the scheme and configuration; packages are resolved
    (`-resolvePackageDependencies`, remote packages only); the build follows; then the optional `test` (on this Mac's
    architecture) and `archive`.
  - **Tests:** a local Swift package's test targets run with that package's `swift test`, from the same Xcode.
    `xcodebuild test` on the project finds no test bundle for them (seen on Xcode 26.6, with and without HCG). The
    project's own test targets run with `xcodebuild test -only-testing:`.
  - **Outputs:** PKG installer (unsigned) and Xcode archive (zipped `.xcarchive` with dSYMs), alongside .app, ZIP and
    DMG.
  - **Signing Doctor** for macOS apps (`--mac-signing-doctor`, read only). It tells unsigned, ad hoc, development,
    Developer ID, notarized and App Store signatures apart, for the app and each embedded framework, helper and
    extension. It reports team, entitlements, sandbox, hardened runtime, Gatekeeper and the stapled ticket.
  - **Build diagnostics** (`hcg.build-diagnostics` v1): compiler errors and warnings with file, line, column and
    target, source excerpts, failed targets and commands, dependency, signing, linker and test failures, and duration.
    Written for every build and redacted. Results › *Xcode build diagnostics* views it, copies it for an agent, or
    opens its folder.
  - **Native build-provider contract** (`hcg.native-build-request` / `-result` v1, docs/contracts/): the approval,
    source provenance, isolated workspace and limits, then every step's exit code and the artifacts. It runs locally
    without GitHub Actions and is proposed for an HCG Server build host.
  - **Release Passport** build half for each ZIP, DMG, PKG and Xcode archive. Ship to Store reads it for the exact file
    and creates private drafts only.
  - **Wizard:** dependencies, configuration facts, *Run the scheme's tests* and *Also create an Xcode archive*.
  - **CLI:** `--xcode-mac <folder|zip> [--inspect-only] [--test] [--archive] [--formats …] --trust`.
- Regression tests: a synthetic project shaped like an Xcode 16 SwiftUI app with a local package, plus an opt-in check
  of the real HCG Offensive Frontier checkout (`HCG_OFFENSIVE_FRONTIER_CHECKOUT`). Diagnostics are parsed from real
  xcodebuild output shapes. Schemas are validated for diagnostics, request and result.

### Fixed
- Inspection no longer skips folders named `Packages`. Only a NuGet `packages/` folder (repositories.config or
  `.nupkg` files) is skipped, so an Xcode app's local Swift package is seen and the project is no longer shown as
  unsupported.
- A Swift package that belongs to an Xcode app (referenced by its project) is no longer offered as a separate Swift
  package build.

## [0.3.1] — 2026-11-18 — Production HCG Connect built into the packaged app; real HCG integration

Identify this build with `--version` (or the Account page footer): it prints `HCG Executable Maker 0.3.1… (commit …)` and
`HCG Connect: Production registration (hcg-executable-maker-desktop-production), client fb9b6edf-… built in`.
Builds that show "Version 0.3.0 · unknown" predate the built-in production client and cannot sign in without importing a file.

### Fixed (0.3.1)
- **Sign-in works out of the box**: the packaged app uses Maker's own production HCG Connect client (`fb9b6edf-2bfc-4b5e-a354-dbb1af697029`, public PKCE client, no secret) with no `hcg-connect.json` to import. The Account page now says "Source: built into this build: Production registration (hcg-executable-maker-desktop-production)" instead of "built-in defaults (no hcg-connect.json imported)".
- An `hcg-connect.json` in the data folder that names no `clientId` no longer switches off the built-in client (it showed "clientId — missing"). A wrong value, such as the template placeholder, is still reported.
- `--version` names the HCG Connect registration and client built into the build.
- CI "Package" now runs each packaged macOS and Linux app with an empty data folder and fails unless it loads client `fb9b6edf-…`, reports its own version and commit, and HCG's identity server accepts the client and all three registered callbacks (`http://127.0.0.1:47820–47822/oauth/callback`).


### Added
- **Production HCG Connect registration built in**: client `fb9b6edf-2bfc-4b5e-a354-dbb1af697029` issued by HCG (`hcg-executable-maker-desktop-production`), identical to HCG's exported `hcg-connect.json` (`docs/hcg-connect/production.hcg-connect.json`). The development registration (`hcg-executable-maker-desktop`, no client issued) is selected with `HCG_EXECUTABLE_MAKER_HCG_ENVIRONMENT=development`. Each registration keeps its own session in the OS credential store.
- **One shared feature-access service** (`FeatureAccessService`) for the UI and the command line: local builds free and offline; `support.submit`, `support.attach`, `ai.use` decided by HCG's central `/check`; Ship to Store's staff permission decided by HCG Store. Decisions refresh on sign-in, account and organisation change, when HCG's answer expires (`revalidate_after_seconds`), and fresh before every privileged remote operation. "Not permitted" and "HCG unavailable" are separate states.
- Account page **Feature access** card with HCG's reason and what to do: HCG Store product link only when a licence is missing; "ask your administrator" for staff and organisation access; "not enabled yet" for features HCG has not enabled.
- `--hcg-session features` and `--hcg-session require <feature>` (exit 0/3/8/9); `--ship-to-store` enforces the same check.

### Fixed
- Review of PR #11: the Help & Support page no longer crashes when HCG refuses or cannot confirm a request at the moment of sending (it says why, and offers Submit again only when HCG was unavailable). An answer that arrives after another account signed in or another organisation was chosen is discarded instead of shown. HCG being unreachable while renewing the session is "HCG unavailable", not "sign in". Only HCG's own `not_org_member` 403 means "not a member"; any other 403 is unknown. An HCG Store timeout is "HCG Store did not answer", not a crash. Large Linux ZIPs (hundreds of managed DLLs) are still recognised as Linux. After an outage HCG is asked again after a minute. Cached HCG Store decisions name the Store they came from.
- Command line: "choose an organisation" is exit code **10** (was 8, "not permitted"); `--organisation <slug|id>` chooses one for `features` and `require`.
- Ship to Store declared a Linux portable ZIP (no `.exe`, a Linux program inside) as a **Windows** ZIP, and HCG Store accepted it. It is now refused before upload: HCG Store takes Linux apps only as `.deb`, `.rpm` or `.AppImage`. Found by shipping Executable Maker's own Linux package to a TEST HCG Store.
- Executable Maker's own packages (`tools/self-package`, CI "Package") reported commit "unknown": the build environment removes `HCG_*` variables, so `HCG_SOURCE_REVISION` never reached the build. The commit now travels as `build-revision.txt` in the imported copy, and `--version` names the exact commit a package was built from.

### Changed
- The Ecosystem API client reads HCG's confirmed shapes only (`user`, `org_role`, features map, `licences`, `layer`/`reason`); anything else, or an answer about another organisation, product or feature, is "access unknown". Edition licences for other editions never count for the desktop app.
- Support requests and HCG AI are enforced in their services (UI and command line alike); the undeployed App Access route no longer decides anything.
- MOCK HCG (`tools/hcg-mock/mock-hcg.mjs`) serves the confirmed contract, with `checks` and `outage` scenarios.

### Verification
- Unit tests 813 passed (11 skipped); command line against the MOCK HCG; UI driver Account flow; Ship to Store end to end 26/26 against the merged HCG Store.
- **Live HCG (no account):** connection check with the built-in production client passed every identity step, including client registration; the Ecosystem API answers 401 as deployed. No real sign-in or real-account upload: see docs/FEATURE-ACCESS.md.

## [Unreleased] — Ship to Shop: Ship to Store from a build, file kinds, safe retry

### Added
- **"Ship to Store…" on a successful build** (New Build result and Mobile Build Studio artifacts): opens a new release with that build's verified output files and version; folders and unverified files are listed as not offered, with the reason.
- **File kinds and install labels**: installer, portable app, iOS device build, iOS Simulator build, jailbreak package — from the file's content, checked again by HCG Store. iOS device builds are labelled from their signature and embedded provisioning profile (CMS signature verified; development, ad hoc, in-house, App Store, expired, none). Unsigned, Simulator and jailbreak files are never labelled installable; Simulator builds (folder, IPA or ZIP) are refused with the reason. Signature presence in Windows programs (Authenticode table), MSIX, IPA and macOS app ZIPs is read on any OS and an absent signature is reported as unsigned.
- Unsupported file types are refused with what to ship instead and the accepted list.
- `--ship-to-store` prints each file's kind and install label and HCG Store's label after upload.

### Changed
- **No duplicate releases on retry**: creating a draft is idempotent in HCG Store; a lost answer is retried once, and shipping again with the same details continues the existing draft (with or without `--release`).

### Verification
- `tools/ci/ship-to-store-e2e.sh`: 26 checks, including a real New Build (HelloAvalonia → Windows x64 ZIP) shipped from its result through the headless app (UI driver `--ship-build`), approver draft review with byte-identical download, unauthorised uploads, kill-and-resume without the release ID, Simulator refusal. 5 new unit tests (16 for Ship to Store).

## [Unreleased] — Ship to Store (HCG Store release publishing)

### Added
- **Ship to Store** page (sidebar) and "Ship to Store…" on every artifact: product (or draft product), edition, version, build number, notes, compatibility, distribution, requested eligibility, HCG Repo delivery for `.deb`; one file per platform (Windows EXE/MSI/MSIX/ZIP, macOS app ZIP/DMG/PKG, iOS/iPadOS IPA, Linux DEB/RPM/AppImage, jailbreak DEB) detected from content, SHA-256, minimum OS, package fields, and local signature/notarisation checks (codesign, spctl, stapler, pkgutil; Authenticode on Windows).
- Uploads to Store-issued, file-scoped URLs with resume, bounded retries, confirmed-bytes progress, server-side SHA-256 and format verification, Retry on the same draft, submit for manual review, review link. The app never approves or publishes.
- `HCGExecutableMaker --ship-to-store` (same code) and `tools/ci/ship-to-store-e2e.sh` (TEST Store + mock HCG Connect, 16 checks). UI driver `--ship-page`. 11 unit tests. See docs/SHIP-TO-STORE.md.

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

## [Unreleased] — CI: iOS porting job, compile stall and Simulator stages

### Fixed
- **A finished command could be held until its timeout.** `ProcessRunner` waited for the command's output pipes to close, not just for the command to exit. When a background process the command had started kept those pipes open (for example xcodebuild's build service), a build that had already succeeded waited out its full timeout (45 min for Porting Studio's Simulator build) and was then reported as timed out.
  - Now the runner waits for the process itself, then gives the output 10 s to close.
  - If the output stays open, it says so in the log and uses the command's real exit code.
  - A command that really hangs still times out and is killed.

- **A timed-out command's process tree could be left stopped.** .NET's `Process.Kill(entireProcessTree: true)` sends each process SIGSTOP and then SIGKILL (confirmed with strace), so a tree kill that fails part-way leaves processes stopped.
  - CI run 101 (repeat 2): after a toolchain probe timed out, four `bash` processes stayed stopped under launchd in the UI driver's process group, and the driver died of SIGHUP two seconds later.
  - HCG now collects the tree first and sends only SIGKILL (`ProcessTree`). The step wrapper reports a signal death and any stopped processes.

### Changed
- **iOS Simulator launch** runs as timed stages (boot, install, launch, ready), each with its own budget, logged as it happens.
- **"Ready" is decided by the app, not by a screenshot.** The launched app is ready when:
  - the process simctl launched is the one launchd lists as the app (not a relaunch, not exited);
  - when a readiness signal is expected, the app reported its first screen for **this** launch, with the expected screen and element values;
  - the same process is still alive 5 s later.
- **Readiness signal (test-only).** The HelloMac and TallyPackage fixtures write `Documents/hcg-ready.json` from their first screen's `onAppear`, but only when launched with `HCG_READY_SIGNAL=<nonce>`. HCG passes that nonce through `SIMCTL_CHILD_` and reads the file from the app's data container. The code is part of the fixtures' own source, so it is ported and compiled with the generated app.
- **Screenshots are diagnostics only.** The running app's screen is compared with the screen it leaves when terminated. A screenshot that is missing, unreadable or timed out is reported as a capture failure, never as "0 % changed".
- **Apps without a signal** (e.g. the web port) are reported as "launched, UI readiness not verified by the app".
- **The Simulator can be chosen explicitly** (by UDID). The app is terminated after the check.
- **The UI driver's porting stages** (assessment, generation, validation, compilation, launch):
  - each runs within its own budget (`--budget-STAGE`);
  - each logs its UTC start and end and its outcome;
  - a progress line every 30 s names the active stage and the last output line;
  - all build and simctl output is echoed live;
  - on a timeout, diagnostics are collected before the stage is cancelled. The stage then fails; nothing is retried.
- **CI `port-ios`** gets its own Simulator per job, with an explicitly chosen runtime and device type, deleted at the end. Each step runs under `tools/ci/run-stage.sh`:
  - its own process group, with output kept in a file so no orphaned process can hold the step open;
  - a time limit, and a heartbeat with stage, memory, swap, disk and process tree;
  - a resource guard;
  - diagnostics (`tools/ci/diagnose.sh`: stacks, open pipes, Simulator state and log, crash reports, build logs) before anything is stopped.
  - Logs and diagnostics are uploaded, then only this job's processes and Simulator are cleaned up.
  - The job's overall timeout drops from 75 to 60 min.
- **Repeat mode:** `workflow_dispatch` with `port_ios_repeat` runs only the iOS porting job several times in parallel. Normal push and PR runs are unchanged.

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
- **Build route choice for a folder with an Xcode iOS app and a Theos project** (HCG Nexus with `HCGNexus.xcodeproj`
  next to the tweak's Makefile): the inspection attaches the Theos projects to the Xcode-led analysis (instead of
  "Also found files for: C / C++. These are not part of this build."), and New Build's Inspect step offers *iOS app
  (IPA, Xcode)* (Mobile Build Studio, `XcodePlanner`) and *Jailbreak package (.deb, Theos)* (the Theos panel and
  `TheosBuildPlanner`, unchanged). The routes never mix: Theos builds refuse IPA/archive formats and mobile options;
  mobile builds refuse Theos options and the `.deb` format. Tests: `BuildRoutesTests` with the fixture
  `tests/Fixtures/Theos/NexusWithXcode`; UI driver `--native --build-route theos-deb|xcode-ipa`.
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
