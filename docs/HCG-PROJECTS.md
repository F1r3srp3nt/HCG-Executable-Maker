# HCG Nexus, HCG Mail iOS and HCG Sentinel in HCG Executable Maker

How Maker inspects and builds these three HCG projects, what was wrong before, and what is verified where. Maker never
installs tools, never installs packages on devices, never resprings, never uploads to TestFlight or App Store Connect.

## States

Every build route reports one of these, in New Build (C/C++ and Theos panel) and in Mobile Build Studio:

| State | Meaning |
|---|---|
| **Recognized project** | The project type is known; choose what to build. |
| **Project implementation incomplete** | The project itself lacks what a build needs (no app target, no Theos product, no `control` file). |
| **Build adapter unavailable** | Maker has no route for this kind of project (e.g. Meson, Bazel). |
| **Required toolchain missing** | A tool is missing or the wrong version on this computer; each one is listed with the exact fix. Maker installs nothing. |
| **Configuration required** | Something the user chooses is missing or invalid (signing identity, a configuration the project refuses, a repository imported only in part). |
| **Ready to build** | Every required item is present. |
| **Build failed** | The first actionable error is shown in full (wrapped, copyable); the complete log and `.xcresult` bundles can be exported. |
| **Artifact verified** | The build produced artifacts and every one passed verification. |

Before a build, Mobile Build Studio and the New Build summary show the project, the adapter, the toolchain (Xcode
version and path, SDK, deployment range, .NET SDK and workload, or Theos, make, ldid and the iOS clang), the
architecture and the output type. Stages stay separate: compile, archive, signature check, export and verification
are reported on their own, and an IPA is never reported when only an archive exists.

## HCG Nexus — Theos jailbreak packages

**What the project is.** `Makefile` builds the tweak `HCGNexus` (a dynamic library injected into SpringBoard) and,
through `SUBPROJECTS += Prefs`, the preference bundle `HCGNexusPrefs`. `PreviewHarness/` is a separate Theos project
and package: an application packaged as a `.deb`, not an IPA. The Makefile defines the configurations:

| `HCG_ENV` | Scheme | TARGET | Installs under | Architecture field |
|---|---|---|---|---|
| `rootless` (default) | rootless | `iphone:clang:16.5:15.0` → iOS 15.0+, SDK 16.5 | `/var/jb` | `iphoneos-arm64` |
| `rootful` | rootful | `iphone:clang:14.5:14.0` → iOS 14.0+, SDK 14.5 | `/` | `iphoneos-arm` |
| `roothide` | — | refused by the project (`$(error …)`), shown verbatim | — | — |

`ARCHS` come from `HCG_ARCHS ?= arm64 arm64e`. Maker shows all of this, takes it from the Makefiles as text, and never
changes it.

**Detection.** Theos Makefiles (those including `$(THEOS)/makefiles/…`) are recognized before any generic Make
classification. Every Theos project with its own package is listed; directories named in another project's
`SUBPROJECTS` belong to that package. Products are typed from `TWEAK_NAME`, `BUNDLE_NAME` (a preference bundle when it
installs to `PreferenceBundles` or links the Preferences framework), `APPLICATION_NAME`, `TOOL_NAME`, `LIBRARY_NAME`,
`FRAMEWORK_NAME`. The `control` file gives the package id, name, version and dependencies; maintainer scripts are
listed because they run as root on installation.

**Two routes in one folder.** When the imported folder also holds an iOS app project (HCG Nexus with
`HCGNexus.xcodeproj` at the root, next to the tweak's `Makefile`, `control`, `Prefs/` and `PreviewHarness/`), the Xcode
app still leads the inspection and the Theos projects are attached to it. New Build then shows **Build route** on the
Inspect step:

- *iOS app (IPA, Xcode)* — the Xcode project, archived and exported as an IPA in Mobile Build Studio (`XcodePlanner`).
  It never packages a `.deb`: Mobile Build Studio refuses settings that carry Theos options or a `.deb` format.
- *Jailbreak package (.deb, Theos)* — the same Theos analysis and panel as a Theos-only folder (configurations from
  `HCG_ENV`, `PreviewHarness/` as its own package, the `.deb` verification with the arm64e ABI check), built by
  `TheosBuildPlanner`. It never produces or exports an IPA: a Theos build with an IPA/archive format or mobile options is
  refused.

Import the repository root, not a subfolder: the Makefiles reach shared sources through `..`. An inspection stored
before this route choice existed shows only the Xcode route; press *Re-inspect* once.

**Tools.** The Toolchains page has a *Jailbreak packages (Theos)* group, each item reported on its own: Theos (`THEOS`,
`~/theos` or `/opt/theos`, with the SDKs in `$THEOS/sdks`), GNU make 4+ (`gmake` on macOS; Apple's make 3.81 is
reported as too old), ldid, perl, and the iOS clang (Xcode's on a Mac; `$THEOS/toolchain` on Linux). The SDK a
configuration's TARGET names (e.g. iPhoneOS 14.5 for rootful) is checked separately. Windows hosts are told to build on
macOS or Linux.

**Build.** After the trust confirmation, Maker copies the imported repository (the whole repository, because Nexus'
Makefiles use `ROOT := ..`) into a folder for this build and this configuration — rootless and rootful never share a
folder — and runs `make -C <project> package FINALPACKAGE=1 THEOS_PACKAGE_DIR=<folder> HCG_ENV=<value>`, as an argument
list. Theos' makefiles cannot handle spaces in paths, so when Maker's own folder has one (as `~/Library/Application
Support` does) the build runs in a private temporary folder. Never `make install`, never `make do`, never a device.

**Verification of the .deb** (Maker reads the `ar` container itself; lzma/xz payloads, which Theos' `dm.pl` writes,
are unpacked with the system `tar`): format 2.0; Package and Version match the project's control file; the Architecture
field matches the scheme; every file is under `/var/jb` (rootless) or none is (rootful); the expected files exist (the
tweak dylib and its filter plist, the bundle binary under its install path, the app binary); each binary's slices match
`ARCHS`, arm64e slices carry the versioned pointer-authentication ABI (required for arm64e processes on iOS 14+, so an
old-ABI slice fails a build whose deployment target is 14 or later), the platform is iOS and the minimum iOS matches the
TARGET; maintainer scripts are flagged. The result is *Jailbreak package (.deb, Theos)* — never an IPA or archive.

**Obtaining the package.** The output folder holds the `.deb`, `INSTALL.txt` (copy to the device; install with Sileo,
Zebra, Filza or `dpkg -i`; respring yourself) and `PACKAGE-REPORT.txt` (control fields, payload, binary slices).

## HCG Mail — iOS (`iOS App Store Submission/`)

**What it is.** A real .NET for iOS application: `src/HCG.Mail.iOS/HCG.Mail.iOS.csproj` (`net10.0-ios`,
`OutputType Exe`, Avalonia.iOS 11.3.22, iOS 16.0+, bundle id `com.hakmicapitalgroup.hcgmail`) with the mobile UI in
`src/HCG.Mail.Mobile`, sharing the mail engine in `../Windows-executable-Application/src/` (Core, Email,
Infrastructure, Presentation). There is deliberately no `.xcodeproj`. The iOS workload is pinned in
`scripts/workload-rollback.json` (`26.2.10233/10.0.100`, which requires Xcode 26.3); the SDK in `global.json`.

**Before.** Imported as the repository, the iOS head was listed as an "Avalonia desktop application" (it would have gone
through desktop publishing). Imported as the submission folder alone, it was "Unsupported" with "net10.0-ios requires
Apple platform workloads (not supported in M0.1)": the static project reader did not follow `<Import>` with
`$(MSBuildThisFileDirectory)`, so the version, shared props and shared project references could not be resolved, and
Maker had no non-MAUI .NET iOS adapter.

**Detection now.** Maker's static reader follows the project's `Directory.Build.props` / `Directory.Packages.props`
chain through `<Import>` in document order, substituting `$(MSBuildThisFileDirectory)` per file (nothing is executed),
and never reads outside the imported folder — it names such files instead. A project whose target frameworks are all
`net*-ios` with `OutputType Exe` and no MAUI is a *.NET for iOS application*; its references are followed transitively
from the repository root.

* Imported at the **repository root**: New Build keeps the desktop app as its project (desktop support unchanged) and the
  iOS head is attached for Mobile Build Studio with its evaluated id, title, version (0.1.0 from the shared props),
  build number, minimum iOS, Avalonia version, entitlements, workload pin and all five shared projects.
* Imported as **only the submission folder**: *Configuration required*, naming each file outside the import
  (`../Windows-executable-Application/Directory.Build.props`, the shared projects) and asking to import the repository
  root. Maker never builds a partial copy of a repository.

**Readiness.** Mac with Xcode; the selected Xcode's iOS (Simulator) SDK; .NET SDK; the .NET iOS workload, compared with
the project's pin (a different installed version is *Required toolchain missing* with
`dotnet workload install ios --from-rollback-file "<pin file>"` — Maker never runs it); project references resolved;
for signed output a certificate in the keychain and an installed provisioning profile.

**Build adapter** (Mobile Build Studio, working inside the whole imported repository):

| Output | Command | Notes |
|---|---|---|
| iOS Simulator build | `dotnet build <csproj> -c Debug -f net10.0-ios -r iossimulator-arm64` (`-x64` on Intel) | Runs only in the Simulator. |
| iOS Archive | `dotnet publish … -c Release -r ios-arm64 -p:ArchiveOnBuild=true -p:ArchiveDir=<HCG build folder> -p:BuildIpa=false -p:CodesignKey=<certificate> -p:CodesignProvision=<profile>` | Signed only: the .NET iOS SDK refuses an unsigned archive ("Code signing must be enabled to create an Xcode archive"), so Maker never offers one and blocks the archive until a certificate is in the keychain. |
| iOS IPA | the signed archive above → archive signature check → `xcodebuild -exportArchive` with Maker's export options | Separate stages; the IPA exists only after export. Never uploaded. |

## HCG Sentinel — archive failure

**Report.** `xcodebuild archive` (scheme HCGSentinel) exited with code 65; the visible line was
`…/source/HCGSentinel.xcodeproj: error: The iOS deployment target 'IPHONEOS_DEPLOYMENT_TARGET' …`, truncated.

**What the project defines** (checked): one Xcode project, generated by `Tools/generate_xcodeproj.py` and committed;
scheme `HCGSentinel` archives target HCGSentinel with `IPHONEOS_DEPLOYMENT_TARGET = 14.7` (Release); scheme
`HCGSentinelModern` archives target HCGSentinelModern at 15.0; one local Swift package (`SentinelCore`, iOS 14); no
xcconfig, no entitlements, automatic signing without a team. Maker's archive command adds no deployment setting
(verified in `XcodePlanner` and by a test). Sentinel's own CI archives the 14.7 scheme with Xcode 16.4 and 26.0–26.6.

**Root cause.** The message is Xcode's deployment-range diagnostic: the Xcode used on that Mac supports a range of iOS
deployment targets (in its iPhoneOS SDK's `SDKSettings.json`) that does not include 14.7. That is a property of the
selected Xcode, not of the project, and lowering or raising the project's target is not the fix. The defect was in
**Maker's integration**:

1. Maker's mobile output parser did not recognize project-level xcodebuild diagnostics (`/path/App.xcodeproj: error: …`,
   no line number), so the error never became a build message and the failure read only "failed with exit code 65".
2. Maker did not compare the archived target's deployment target with the selected Xcode's supported range before
   building, so the problem surfaced only as an opaque exit code.
3. Long lines were cut off in the log view, and no `.xcresult` was kept.

**Fix (Maker).**

* *Preflight:* Maker reads the selected Xcode's iPhoneOS `SDKSettings.json` (Minimum/MaximumDeploymentTarget) and, for
  the targets the chosen scheme archives (from the scheme's BuildAction, with project-level settings inherited), blocks
  the build before `xcodebuild` runs when a target is outside it — with the exact message xcodebuild would give and the
  two routes: select an Xcode that supports iOS 14.7 in Toolchains (explicit, for Maker only) or choose scheme
  `HCGSentinelModern` (iOS 15.0; does not install on iOS 14). Maker never changes the deployment target.
* *First actionable error:* project-level diagnostics are parsed; a failed step's reason carries the first error in
  full plus the paths of the complete log and the result bundle; Mobile Build Studio shows it prominently, wrapped and
  copyable, with *Export full log…* (log + every `.xcresult`) and *Open log folder*. Log lines wrap.
* *Result bundles:* every `xcodebuild` build, archive and export writes `-resultBundlePath` into the build folder,
  kept with the log, also on failure.

**Sentinel itself.** No source change is required: the 14.7 target is intended (iPhone X on iOS 14.7) and preserved; the
generator, project and schemes are consistent. Its `docs/MAKER.md` already proposed the Maker changes now implemented.

**Supported route.** Select an Xcode whose iPhoneOS SDK range includes 14.7 (verified in Sentinel's CI: 16.4, 26.0–26.6)
in Toolchains › Xcode and archive scheme `HCGSentinel`; with only a newer Xcode, archive `HCGSentinelModern` (iOS 15+).
**Up to 0.3.0 the second route could not be taken in Mobile Build Studio** — see the next section.

## HCG Sentinel — scheme selection on an iOS 15 Xcode (fixed in 0.3.1)

**Report.** Scheme HCGSentinel selected; deployment target iOS 14.7; the selected Xcode supports iOS 15.0 and later; Maker
recommends HCGSentinelModern but every output stays unavailable; the state shows "Ready" and "Required toolchain missing"
at once. Earlier changes did not alter this on the Mac.

**Root cause (Maker).** The deployment preflight above was scheme-aware only when called with a scheme. The two callers
that decide what the user can do called it without one:

1. `MobileCapabilities.Evaluate` (which outputs are available) built its options without `Scheme`, so the preflight fell
   back to the first scheme named after an app target: `HCGSentinel` (14.7). With a 15.0-minimum Xcode every iOS output
   was unavailable, whatever scheme was chosen.
2. Mobile Build Studio created the scheme list only after an output was selected (`RebuildConfiguration`), so with no
   available output there was no scheme picker; there was no handler for a scheme change (readiness and summary kept the
   old scheme); and every output change reset the scheme to the first one.
3. `MobileBuildService` re-checked availability the same way before building, so even an explicit
   `Scheme = HCGSentinelModern` was refused with HCGSentinel's reason (reproduced by
   `SentinelSchemeSelectionTests.Engine_builds_the_modern_scheme_it_was_given_…` against the unfixed code:
   “iOS Simulator build is not available: HCGSentinel (scheme HCGSentinel) needs iOS 14.7, …”).
4. The no-output state used `forPlatform[0].Reason` under the fixed tag "Required toolchain missing", and the stage
   heading defaulted to "Ready".

The existing unit test passed because it called `MobileReadiness.Evaluate` directly with the scheme; CI's Sentinel job
used hosted Xcodes, which all accept 14.7, and a Sentinel commit (810c6f9) 40 commits behind `main`. Sentinel itself is
correct: at `main` (03bc6e5) `HCGSentinel` builds `HCGSentinel.app` at 14.7 (intended, for iOS 14.7 devices) and
`HCGSentinelModern` builds `HCGSentinelModern.app` and `HCGSentinelModernTests` at 15.0; Sentinel's own CI builds both.

**Fix (Maker 0.3.1).** The scheme is chosen first (section 2), labelled with what the selected Xcode accepts, saved per
project, and used by output availability, readiness, the project summary, the engine gate, the build log and
`xcodebuild -scheme`. "Use scheme HCGSentinelModern" is offered when the selected scheme is blocked by its deployment
target — never applied automatically; deployment targets are never changed and the compatibility check is not bypassed.
The state reads **Blocked — deployment target incompatible**; a missing toolchain, an installed but incompatible or
unusable one, signing and configuration are named separately; a **Before you can build** list includes the trust
checkbox; the previous build is reported separately from the current selection. The imported copy is compared with its
original and can be refreshed in place; a diagnostic report establishes what is running and what would be built.

### On your Mac

1. **Install 0.3.1 or later** and check it is what runs: the sidebar footer or Settings › About shows the version and
   commit, or in Terminal:
   `"/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker" --version`.
   An older copy elsewhere (Downloads, a DMG still mounted) may be the one Finder opens — the printed path tells which.
2. **Collect the diagnostic report** (before changing anything, if you want a record of the current state):
   Mobile Build Studio › select HCG Sentinel › *Copy diagnostic report*, or
   `"/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker" --diagnose-mobile --reproduce`.
   It lists the Maker build, HCG's selected Xcode and its iOS range, where the imported copy came from and whether the
   original changed since, the SHA-256 of `project.pbxproj` and both schemes (compare with Sentinel's `docs/MAKER.md`),
   each scheme's targets, the saved scheme, readiness per scheme × output, and — with `--reproduce` — xcodebuild run
   directly on the same copy with the same Xcode. The file is saved in HCG's data folder under `diagnostics/`.
3. **Bring the copy up to date:** if "Imported copy" says the original changed (or the hashes differ from Sentinel
   `main`), update your Sentinel folder or download a fresh ZIP of `main`, then *Refresh source* (or *Refresh from…* for
   a new location). The project, its history and the scheme choice are kept. Re-importing as a new project also works.
4. **Choose scheme HCGSentinelModern** in section 2 (or press *Use scheme HCGSentinelModern*). The Simulator build and
   unsigned archive become available; the IPA needs your Apple Development certificate and team (Signing).
5. **Tick the trust checkbox** in section 5 (it is per build) and start. The build log's first lines name the scheme and
   its targets, and the xcodebuild command shows `-scheme HCGSentinelModern`.

### Verified (CI run 43629669974, macOS 26, Xcode 26.6 with its iOS SDK minimum edited to 15.0 on the disposable runner)

Hosted Xcodes all accept 14.7, so the runner's iPhoneOS/iPhoneSimulator `SDKSettings` minimum was set to 15.0 (the
reported Mac's minimum). Xcode 26.6 itself kept treating 14.7 as acceptable (both schemes build outside Maker with
`** BUILD SUCCEEDED **` and no deployment diagnostic), so this shows Maker's behaviour against that range, not Xcode's
own refusal, which only a newer Xcode produces.

| Step | Result |
| --- | --- |
| Maker before the fix (c1c587a), Sentinel 810c6f9 | Every output unavailable with HCGSentinel's 14.7 reason; scheme list empty (`FAIL Scheme HCGSentinelModern offered ()`); state `[Required toolchain missing]` — the reported screen. |
| Outside Maker, Sentinel 03bc6e5 | Schemes HCGSentinel, HCGSentinelModern (+ SentinelCore). Only target `HCGSentinel` resolves to 14.7 (Debug/Release × Simulator/device); `HCGSentinelModern` 15.0 everywhere. Simulator builds of both schemes succeed with plain xcodebuild. |
| Maker after the fix (UI driver) | Arrival: `[Blocked — deployment target incompatible]`, “Use scheme HCGSentinelModern” offered, not selected. Original moved to 03bc6e5 → copy reported as changed → Refresh source → same project, copy matches 03bc6e5. Use scheme → Simulator available, `[Ready to build]`, only the trust prerequisite left; scheme kept after leaving/reopening; build `[Artifact verified]` (`HCGSentinelModern.app`), log shows `-scheme HCGSentinelModern`; previous build reported separately. |
| Unsigned device archive, HCGSentinelModern | `[Artifact verified]`, `HCGSentinelModern.xcarchive`, `com.hcg.sentinel 0.1.0 (1)`. |
| Legacy HCGSentinel | Archive unavailable, “needs iOS 14.7”, `[Blocked — deployment target incompatible]`; nothing changed or bypassed. |
| IPA, HCGSentinelModern | Unavailable: “No Apple Development certificate in the keychain” (CI has no signing identity). Not attempted. |
| Packaged app (self-packaged `.app`, osx-arm64) | `HCG Executable Maker 0.3.1 (commit ee27fa87b8f1, …)`; `--diagnose-mobile --reproduce` on the UI run's data: saved scheme HCGSentinelModern, revision 03bc6e5, copy matches original, hashes equal Sentinel `main`, outside-Maker Simulator build of HCGSentinelModern exit 0. |

The first run (43628150245) failed at the packaged-app step: self-packaged apps reported `commit unknown` (fixed in
the same release, see CHANGELOG).

`HCGSentinel` (iOS 14.7) remains for the iPhone X on iOS 14.7 and needs an Xcode that accepts 14.7 (selected for HCG in
Toolchains › Xcode). The app from `HCGSentinelModern` does not install on iOS 14.

## Verification

See docs/STATUS.md for what ran where. In short: fixture tests on Linux (detection, readiness, plans, .deb
verification with real lzma packages, parser, preflight); the real wizard and Mobile Build Studio on Linux for states
and blocking; real builds on macOS CI: the three Nexus `.deb` packages (*Artifact verified*, confirmed by Nexus' own
checker), Mail's iOS Simulator app (*Artifact verified*), Sentinel archives of both schemes with Xcode 16.4 and 26.0.1–26.6
(*Artifact verified*). A signed archive or IPA (Mail, Sentinel) and anything on a physical device need certificates,
provisioning profiles and devices that CI does not have.
