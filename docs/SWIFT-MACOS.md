# Swift (macOS) apps

HCG Executable Maker builds **Swift Package Manager** packages into macOS apps on a Mac:

| Product | Output |
|---|---|
| SwiftUI / AppKit executable | `Name.app` (Info.plist, PkgInfo, executable, icon, SwiftPM resource bundles, ad-hoc signature); optionally the `.app` as ZIP (`ditto`) and DMG |
| Command-line executable | Folder with the executable (+ resource bundles) and/or ZIP with the executable bit recorded |

Reference projects: [Oslo-Sprint](https://github.com/F1r3srp3nt/Oslo-Sprint): `distil/` (product `Distil`, target `TokenLabApp` in the
package named `TokenLab`, shipped as **Distil.app**) and `relay/` (products `Relay`, a SwiftUI app, and `relay-runner`, a command-line tool).

## Which build am I running?

Settings › About this build shows the version, the git commit (`+uncommitted` if the tree had changes), the build
time, the executable and the `.app` path. The sidebar footer shows the version and commit. From a terminal,
`HCGExecutableMaker --version` prints the same without opening a window.

The message "Detected a Swift / Xcode project… support is planned for a later milestone (M0.5+)" comes from builds
before Swift support. It exists up to commit `b1a9e43`, which includes the v0.1.0 release.

To build this checkout as an app on your Mac, run `bash tools/build-local-mac.sh`. It prints the exact `.app` to
open. Add `--verify <Oslo-Sprint path>` to also run the Distil/Relay checks below on your Mac.

## Workflow in the New Build wizard

1. **Import** the repository as a folder or ZIP. A ZIP whose content sits in a wrapping folder (GitHub's
   `Repo-main/…`) is handled.
2. **Inspect** (passive). Every `Package.swift` is listed, for example `distil/` and `relay/`. If there are several,
   **none is pre-selected**; you choose one. These directories are skipped: `.build`, `.swiftpm`, `build`, `dist`,
   `DerivedData`, `Pods`, `Carthage`, `checkouts`, `SourcePackages`, `node_modules` and `xcuserdata`.
   `Package.swift` is read **as text only**. The scan collects:
   - the tools version and the declared macOS platform;
   - whether products are declared conditionally;
   - per executable target: SwiftUI/AppKit entry points and privacy-sensitive APIs, such as `AVAudioRecorder` →
     `NSMicrophoneUsageDescription`.
3. **Evaluate package on this Mac** (requires the trust checkbox). `Package.swift` is Swift code, so HCG only runs it
   after you confirm trust. It runs as `swift package --scratch-path <HCG data>/swiftpm/… dump-package`, through the same
   build worker as builds:
   - scrubbed environment;
   - cancellable;
   - 5-minute limit;
   - scratch space outside the project.

   SwiftPM's JSON is the authoritative product list. The packages declare their GUI products under `#if os(macOS)`, so
   the list is the real one for this Mac. Executable products are listed separately from library products and test
   targets. When a package has several executable products (Relay: `Relay` and `relay-runner`), none is pre-selected.
   The evaluation result is stored with the project. **Re-inspect** discards it.
4. **Configure**:
   - Display name, e.g. *Distil*. The executable keeps the product name (Distil's product is `Distil`; its package is
     still named `TokenLab`, so never derive the product from the package name).
   - Bundle identifier, version (`CFBundleShortVersionString`) and build number (`CFBundleVersion`).
   - Minimum macOS: defaults to the package's `platforms:` value; a lower value is refused.
   - App category.
   - Architecture: Apple Silicon, Intel or Universal.
   - A required text for every detected privacy key; the build is blocked while one is empty.
   - App vs command-line delivery: detected from the sources; can be overridden, with a warning.

   .NET-only options are hidden.
5. **Package**:
   - GUI: `.app`, the `.app` as ZIP, DMG.
   - CLI: folder, ZIP.

   Other combinations are shown disabled, with the reason.
6. **Build**. Trust is confirmed again, then:
   - `swift package resolve`
   - `swift build -c release --product <P> --scratch-path <per-build> --arch …`
   - `--show-bin-path`

   Then HCG copies the executable and any `*.bundle` resource bundles, assembles and signs the bundle, verifies it,
   writes checksums and records the build in history. Errors from `swift build` appear in the log and in the
   warning/error counters.

## What is different from .NET apps (`MacAppBundlePackager`)

`SwiftAppBundlePackager` is used whenever the build is a Swift GUI app; `MacAppBundlePackager` (.NET) is never applied
to Swift builds:

- `Contents/MacOS` contains **only** the executable.
- `LSMinimumSystemVersion` comes from the configured or package minimum. Without one, it comes from the deployment
  target the linker recorded in the executable (`LC_BUILD_VERSION`). The .NET default of 11.0/10.15 is not used.
- No `NSPrincipalClass`. Privacy usage descriptions and `LSApplicationCategoryType` are written.
- `Contents` is signed ad hoc (`codesign --force --sign - --timestamp=none`).

### SwiftPM resources

SwiftPM's generated `Bundle.module` accessor looks for a resource bundle at
`Bundle.main.bundleURL/<Package>_<Target>.bundle`, which is the **root of the .app**. If the bundle is not there, it
falls back to an absolute path inside the build folder. It never looks inside `Contents/` (see SwiftPM's
`SwiftModuleBuildDescription.generateResourceAccessor`). This holds for library and app targets alike.

HCG therefore:
1. signs `Contents` first;
2. places each `*.bundle` at the app root, so `Bundle.module` finds it on any Mac;
3. verifies the signature of `Contents` on a copy without those bundles.

**Trade-off:** macOS code signing seals only `Contents`, so a whole-bundle `codesign --verify` reports "unsealed contents
present in the bundle root". Gatekeeper rejects a *downloaded* copy of such an app. Local use works. For
distribution, load resources from `Bundle.main` (`Contents/Resources`) or build with Xcode, which generates a
different accessor.

Packages without resources (Distil, Relay) are unaffected: their apps are fully sealed. Putting the bundles in
`Contents/Resources`, as the Oslo-Sprint scripts would if a target declared resources, makes `Bundle.module` fail at
launch.

### Preserving the project's configured identity

When a package has its own packaging script (e.g. `scripts/build-macos.sh` writing an Info.plist), HCG reads it as
text, never running it, and prefills from it:
- the display name, bundle id, version and build number;
- the minimum macOS version and the category;
- the privacy texts.

It only uses a script that packages the selected product, and the Configure step says which script it read. Values
the script computes at build time are not reproduced, and the wizard says so. Examples are `DistilGitCommit` and
`RelayBuildDate`.

### Tests

HCG builds the selected product only. Test targets are neither compiled nor run, and every build log says so. XCTest
is detected and reported separately as a *test* prerequisite. It is missing with only the Command Line Tools, in which
case `swift test` fails with "no such module 'XCTest'". A missing XCTest never marks a package as unbuildable.

### Working copies

Generated folders are never imported: `.build`, `.swiftpm` and `DerivedData`, from folders and from ZIPs.
Discovery ignores these too, along with `build`, `dist` and dependency checkouts. A working copy that has already been
built with the project's own script (`.build/`, `dist/Distil.app`, Xcode's `.swiftpm/` workspace) is recognised as the
Swift package it is.

### Verification (per build)

- Info.plist keys; `CFBundleExecutable` equals the product.
- No stray files in `Contents/MacOS`.
- Mach-O slices match the chosen architecture (both slices for Universal).
- Executable bit is set.
- `LSMinimumSystemVersion` ≥ the executable's deployment target.
- Every configured privacy key is present.
- Icon and resource bundles are present.
- `codesign --verify --strict --deep` passes.
- App ZIP: extracted again with `ditto -x -k` and the signature re-verified.
- DMG: `hdiutil verify`.
- Launch test: **not** performed by the app (it would execute the project). CI launches the reference apps; see below.

## Toolchain checks

The Toolchains page and the Inspect step report:
- the macOS version;
- the selected developer directory (Xcode or Command Line Tools);
- the Swift compiler version, compared with the package's tools version;
- the macOS SDK version, compared with the package's minimum macOS;
- `codesign` and `hdiutil`.

Every missing item comes with the exact fix, e.g. `xcode-select --install`.

**Xcode** (Toolchains › Xcode) lists installed Xcode apps from `/Applications`, `~/Applications`, Spotlight and
*Browse…*. *Select Xcode* applies one to HCG only: `DEVELOPER_DIR` is set for HCG's own process, so inspection, SDK
discovery, builds, signing and tests all use it. `xcode-select` is never changed. The page distinguishes:
- not installed;
- installed but not selected;
- license not accepted;
- first-launch setup needed;
- no SDKs;
- validation failed.

It also has *Open Xcode*, *Recheck* and *Use system setting*. The Command Line Tools remain enough for Swift package
builds. See [MACOS-DISTRIBUTION.md](MACOS-DISTRIBUTION.md#xcode-selection).

The statuses are distinct:
- *Supported* — buildable now.
- *Missing toolchain* — a supported project on a Mac that lacks tools.
- *Needs another host* — Windows/Linux. Nothing is offered: not evaluation, not configuration, not packaging.
- *Unsupported* — e.g. a macOS Xcode project alone.

HCG never installs tools.

## Not implemented in this version

- **macOS Xcode projects/workspaces** (`.xcodeproj` / `.xcworkspace` whose targets are macOS-only) are recognized,
  and the wizard says they are not built (schemes, signing settings and asset catalogs are a separate capability). If
  the repository also has a `Package.swift`, choose that. iOS Xcode projects belong to Mobile Build Studio, which does
  not produce macOS apps.
- Developer ID signing, hardened runtime, entitlements, notarization, `.pkg` and Mac App Store preparation are now
  implemented: see [MACOS-DISTRIBUTION.md](MACOS-DISTRIBUTION.md). Ad hoc remains the default.
- Building Swift macOS apps on Windows/Linux, and Swift on Linux/Windows targets.
- Fully sealed apps that also use SwiftPM resources through `Bundle.module` (see "SwiftPM resources" above).

## Verification in CI (`swift-macos` job)

[`tools/ui-driver`](../tools/ui-driver) runs the **real** app on Avalonia's headless platform with Skia rendering:
`App`, `MainWindow`, `BuildWizardView` and `BuildWizardViewModel`. It presses the same commands the buttons are bound
to and saves rendered screenshots of every step. Only the OS file picker and modal dialogs are scripted. CI
(macOS, Xcode) clones Oslo-Sprint at the commit pinned in `OSLO_COMMIT` (`.github/workflows/build.yml`) and:

1. **Distil.app**: package `distil/`, product `Distil`, display name *Distil*, `dev.tokenlab.prototype` 0.5.0 (5),
   developer-tools category, arm64. Produces app + ZIP + DMG + pkg.
2. **Relay.app**: import from a ZIP with a wrapping folder, package `relay/`, product `Relay`, universal binary, the
   required microphone text. Produces app + ZIP.
3. **relay-runner**: the command-line product, as folder + ZIP.

Then `tools/ui-driver/verify-macos-app.sh` re-checks each app independently:
- `plutil`, `codesign --verify` / `-dv`, `lipo`, `vtool`;
- launches it with `open`, confirms it is still running, and takes a screenshot;
- repeats the launch for Relay extracted from its ZIP;
- mounts the DMG and checks the app inside and the Applications link;
- runs the CLI.

Local Linux check: `dotnet run --project tools/ui-driver -- --source <Oslo-Sprint> --package relay --expect-blocked-host`
shows the wizard refusing to evaluate or build, and why.
