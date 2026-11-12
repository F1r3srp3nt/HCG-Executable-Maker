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
| iOS Archive | `dotnet publish … -c Release -r ios-arm64 -p:ArchiveOnBuild=true -p:ArchiveDir=<HCG build folder> -p:BuildIpa=false` | Unsigned: `-p:EnableCodeSigning=false` (inspection only, never exported). Signed: `-p:CodesignKey=<certificate> -p:CodesignProvision=<profile>`. |
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

## Verification

See docs/STATUS.md for what ran where. In short: fixture tests on Linux (detection, readiness, plans, .deb
verification with real lzma packages, parser, preflight); the real wizard and Mobile Build Studio on Linux for states
and blocking; the real builds (Theos `.deb` for Nexus, .NET iOS Simulator build and unsigned archive for Mail, Sentinel
archives with every installed Xcode) in the `hcg-nexus-theos`, `hcg-mail-ios` and `hcg-sentinel-archive` CI jobs on
macOS. Signed IPAs and anything on a physical device need certificates, profiles and devices that CI does not have.
