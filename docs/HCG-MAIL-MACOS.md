# HCG Mail for macOS (Apple Silicon) with HCG Executable Maker

HCG Mail is a .NET application. The macOS Apple Silicon release goes through Maker's .NET desktop path:

1. **New Build:** macOS, arm64, `.app` / ZIP / PKG.
2. **Distribution:** Developer ID signing.
3. **Notarize** on the build result.
4. **Release Passport.**
5. **Ship to Store:** a private draft.

The table states what exists today, from the code, before anything is added.

| Step | State | Where | Notes |
|---|---|---|---|
| Application bundle inspection | Implemented | `MacAppFacts` (Info.plist: identifier, version, build, minimum macOS, architectures) | Read from the built `.app` |
| Architecture verification | Implemented | `MacAppFacts.Architectures` (the main executable's Mach-O slices), `osx-arm64` target | Apple Silicon only: build with `osx-arm64`; a universal build needs both RIDs |
| Developer ID Application signing | Implemented | `MacCodeSigner.SignDeveloperIdAsync` (.NET bundles in `NativeAppPackagers`) | Identity chosen by SHA-1 from the keychain; confirmed entitlements |
| Hardened Runtime | Implemented | `codesign --options runtime --timestamp` in Developer ID signing | A .NET app under the Hardened Runtime typically needs `com.apple.security.cs.allow-jit`. Maker does not add entitlements on its own: confirm the set in Configure › Distribution, then check the signed app with the Signing Doctor and a launch |
| ZIP application packaging | Implemented, partly | Portable ZIP (`ditto`) during the build | The ZIP is made **before** notarization. After Notarize staples the `.app`, re-create the ZIP so it contains the stapled app: `ditto -c -k --keepParent "HCG Mail.app" HCG-Mail-<v>-osx-arm64.zip` (Maker does not rewrite build artifacts afterwards) |
| PKG installer generation | Implemented | `MacPkgPackager` (`pkgbuild` + `productbuild`, no scripts, installs into /Applications) | Unsigned for local tests, or signed with Developer ID Installer |
| Developer ID Installer signing | Implemented | `productbuild --sign <Installer SHA-1>` | Needs the Developer ID Installer certificate in the keychain |
| Apple notarization | Implemented | `NotarizationService`: `xcrun notarytool submit --keychain-profile … --wait`; the notary log is saved on rejection | Credentials only as a keychain profile (`notarytool store-credentials`); Maker never sees the password |
| Ticket stapling | Implemented (.app, .dmg, .pkg) | `xcrun stapler staple` + `stapler validate` | A ZIP cannot be stapled itself; staple the `.app`, then re-zip (above) |
| Gatekeeper verification | Implemented | `spctl -a -vv` (`MacSigningDoctor`, after notarization) | "accepted (Notarized Developer ID)" is the target |
| SHA-256 generation | Implemented | Every artifact, recorded at build time and recomputed by HCG Store | |
| Release Passport | Implemented through the CLI for .NET builds | `HCGExecutableMaker --release-passport --app "HCG Mail.app" --file <zip|pkg> --source-repo <HCG Mail checkout>` | Xcode and Windows builds write passports automatically; for .NET macOS builds run this after notarization so the passport describes the final bytes. Source is HCG Mail's own git commit (unknown without git; never guessed) |
| Ship to Store (private draft) | Implemented | Distribution Studio → HCG Mail → Add New Update, or Add to Listed Product → macOS arm64 | Creates a new private draft. The published Windows release is not touched |
| Developer ID certificates, notary credentials | Setup on the Mac | Xcode › Settings › Accounts; `xcrun notarytool store-credentials` | Not created or stored by Maker |

**Not done by Maker:** creating certificates, App Store Connect API keys stored by Maker, approving or publishing in
HCG Store, and making the macOS release current.

## Order for the first HCG Mail macOS release

1. **Build:** New Build → HCG Mail desktop project → macOS → arm64 → formats `.app`, ZIP, PKG → Distribution:
   Developer ID (Application certificate, Installer certificate, entitlements confirmed).
2. **Notarize:** on the build result, **Notarize** (keychain profile). It runs submit, then staple, then validate,
   then a Gatekeeper check for the `.app` and the `.pkg`.
3. **Re-zip** the stapled `.app` with `ditto` (above), and compute its SHA-256.
4. **Passports:** `--release-passport` for the ZIP and the PKG, from the HCG Mail checkout.
5. **Ship to Store:** Distribution Studio → HCG Mail → Add New Update (or Add to Listed Product).
   - Version and build of the macOS release, channel beta, eligibility private.
   - Add the ZIP and the PKG, then **Upload private draft**.
6. **Publish:** review, approval and publication happen in HCG Store, by a person.
