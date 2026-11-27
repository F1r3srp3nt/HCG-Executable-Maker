# HCG Mail for macOS (Apple Silicon) with HCG Executable Maker

HCG Mail is a .NET (Avalonia) application. Its CI builds and qualifies the macOS `.app` unsigned. Executable Maker
signs, packages, notarizes, staples and checks that `.app` **as built**, on **Sign an existing app**. It does not
rebuild the app.

1. **Sign an existing app:** choose `HCG Mail.app` and the Developer ID identities, confirm the entitlements, then
   notarize, staple, make the ZIP, PKG and passports.
2. **Ship to Store:** a private draft.

The table states what exists in the code.

| Step | State | Where | Notes |
|---|---|---|---|
| Application bundle inspection | Implemented | `MacAppFacts` (Info.plist: identifier, version, build, minimum macOS; Mach-O architectures of every file) | Read from the `.app` itself |
| Architecture verification | Implemented | `MacAppFacts.Architectures` (the main executable's slices) | Apple Silicon: publish `osx-arm64`; the passport records `arm64` |
| Layout check | Implemented | `MacBundleLayout.DataInMacOS` | `Contents/MacOS` must hold code only, so publish as a single file (`PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract=false`), as HCG Mail does. A data file there can only be signed in extended attributes, and ZIPs and installers drop those |
| Copy (the input is never changed) | Implemented | `ExistingAppDistribution` | `ditto` to the output folder, then `xattr -cr`. Maker refuses an output folder inside the app, or one that would overwrite the input or an existing signed copy |
| Developer ID Application signing | Implemented | `ExistingAppDistribution` → `MacCodeSigner.SignHardenedAsync` | Inside out: every Mach-O (the native libraries next to the single-file host) and every nested bundle first, then the `.app` with the entitlements. The identity is chosen by SHA-1 from the keychain. Private keys never leave it |
| Hardened Runtime and entitlements | Implemented | `--options runtime --timestamp` | Entitlements come from HCG Mail's own `packaging/macos/HCGMail.entitlements` and are signed exactly as written. You confirm them first. For a .NET app without `com.apple.security.cs.allow-jit`, Maker warns that the app will stop at start-up. Maker refuses `get-task-allow` and non-boolean values |
| Signature verification | Implemented | `MacCodeSigner.VerifyDeveloperIdAsync` and nested checks | Checks `codesign --verify --deep --strict`, the Developer ID certificate and team, the hardened runtime, the secure timestamp, the exact entitlements, and the hardened runtime and team of every nested Mach-O |
| Apple notarization | Implemented | `NotaryClient` (also used by a Developer ID build's **Notarize**) | `xcrun notarytool submit --keychain-profile … --wait`. The notary log is saved on rejection. Credentials are a keychain profile only (`notarytool store-credentials`); Maker never sees the password |
| Ticket stapling | Implemented (.app, .pkg) | `xcrun stapler staple` + `stapler validate` | The `.app` is stapled **before** it is zipped |
| ZIP application packaging | Implemented | `ditto -c -k --sequesterRsrc --keepParent` | `HCG Mail.app.zip` is made from the stapled app |
| PKG installer generation | Implemented | `MacPkgPackager.PackageAppAsync` (`pkgbuild` + `productbuild`, no scripts, installs into /Applications, not relocatable) | Package version = the release version (for example `1.0.0-beta.2`) |
| Developer ID Installer signing | Implemented | `productbuild --sign <Installer identity> --timestamp` | Same team as the application identity. Without an installer identity the package is unsigned (local testing) and not notarized |
| Gatekeeper verification | Implemented | `spctl --assess` (execute for the `.app`, install for the `.pkg`) | After notarization, "accepted (Notarized Developer ID)" is required |
| SHA-256 generation | Implemented | `SHA256SUMS.txt` in the output folder | HCG Store computes it again on upload |
| Release Passport | Implemented, written automatically | `MacReleasePassportWriter`: `<file>.release-passport.json` for the ZIP and the PKG | Written after stapling, so it describes the final bytes. **Release version** (for example `1.0.0-beta.2`) is recorded when given; its numeric part must equal `CFBundleShortVersionString` (`1.0.0`). Distribution Studio then shows no version discrepancy. Source is HCG Mail's git commit when the checkout is chosen (unknown otherwise; never guessed) |
| Ship to Store (private draft) | Implemented | **Ship to Store (private draft)…** on the page hands the ZIP and the PKG to Distribution Studio, with their passports next to them | Creates a new private draft. The published Windows release is not touched |
| Signing Doctor | Implemented, also in the UI | The **Signing Doctor** card on the page (and `--mac-signing-doctor`) | Read-only |
| Developer ID certificates, notary credentials | Setup on the Mac | Xcode › Settings › Accounts; `xcrun notarytool store-credentials` | Not created or stored by Maker |

**Not done by Maker:** creating certificates, exporting private keys, storing notary passwords or App Store Connect
keys, approving or publishing in HCG Store, and making the macOS release current.

## Order for the first HCG Mail macOS release

1. **Get the qualified build:** download HCG Mail's CI `.app` (unsigned, `osx-arm64`, single file), or the artifact
   of the release candidate.
2. **Sign an existing app** (side bar):
   - **App:** `HCG Mail.app`, and an output folder outside it.
   - **Signing:** Developer ID. Choose *Developer ID Application* directly, or fill it from a project's saved
     signing (Signing & Security › Project signing › macOS Developer ID).
   - **Entitlements:** HCG Mail's `packaging/macos/HCGMail.entitlements` (`com.apple.security.cs.allow-jit`). Tick
     *I reviewed these entitlements*.
   - **Package:** `.pkg` with *Developer ID Installer*.
   - **Notarize and staple now:** with the keychain profile from Settings › Apple credentials.
   - **Release version** `1.0.0-beta.2`, **build number**, and the HCG Mail checkout as **source**.
   - Press **Sign**. Each step is reported on its own; the first failure stops the run, and the input app is never changed.
3. **Check:** *Signing Doctor on the signed app* should report Developer ID, notarized, ticket stapled, and Gatekeeper
   accepted.
4. **Ship to Store (private draft)…:** Distribution Studio opens with `HCG Mail.app.zip` and
   `HCG Mail-1.0.0-beta.2-osx-arm64.pkg` and their passports. Choose HCG Mail → Add New Update (or Add to Listed
   Product) → macOS arm64, channel beta, eligibility private, then **Upload private draft**.
5. **Publish:** review, approval and publication happen in HCG Store, by a person.

The same run without the UI (for a build machine):

```bash
HCGExecutableMaker --mac-sign-existing --app "HCG Mail.app" --out signed \
  --identity "<Developer ID Application SHA-1>" --entitlements HCGMail.entitlements --confirm-entitlements \
  --pkg --installer-identity "<Developer ID Installer SHA-1>" --notary-profile HCG-Notary \
  --version 1.0.0-beta.2 --build-number <N> --source-repo "<HCG Mail checkout>"
```

`--test` instead of `--identity` makes an ad-hoc test build. It uses the same layout and signature checks, has no
certificate, and is never notarized. Its files are named `…-adhoc-TEST-not-for-distribution`, and Gatekeeper rejects
them on other Macs. The macOS CI job `macos-sign-existing` runs exactly this against a .NET fixture app, then
launches the signed app.

`--release-passport` also takes `--version` (and `--build-number`), for an archive that was signed elsewhere.

## Building HCG Mail with Maker instead

The New Build wizard can also sign a .NET GUI app for distribution. Choose macOS → Configure › **Single file** →
Package › macOS distribution › **Developer ID**. The app is signed inside out with the hardened runtime, and
`allow-jit` is proposed for the .NET runtime. A saved Project signing route (macOS Developer ID) is the starting
choice. Notarize afterwards on the build result. HCG Mail's release path uses **Sign an existing app**, so the bytes CI
qualified are the bytes that are signed.
