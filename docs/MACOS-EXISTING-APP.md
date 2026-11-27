# Sign & Notarize (macOS): an app built elsewhere

Use **Sign & Notarize (macOS)** in the sidebar for a macOS app that was already built and qualified somewhere else,
for example a CI run's unsigned signing input. Executable Maker:
- never rebuilds the app;
- never changes the original file;
- signs a copy with your Developer ID;
- has Apple notarize it, and staples the ticket;
- makes the ZIP, PKG and DMG from the stapled app;
- checks every file with Gatekeeper;
- writes Release Passports;
- hands the files to Ship to Store, which uploads a **private draft**.

Publishing happens in HCG Store, by a person. The same engine runs from the command line with
`HCGExecutableMaker --mac-distribute` (see the end of this page).

## What each step does

| Step | Button | What happens | Leaves this Mac? |
|---|---|---|---|
| Inspect | **Inspect** | <ul><li>Compares the ZIP's SHA-256 with the published checksum list.</li><li>Extracts a copy and reads Info.plist: identifier, version, build, minimum macOS.</li><li>Reads the main executable's architectures.</li><li>Checks that Contents/MacOS holds code only.</li><li>Lists the nested code.</li><li>Reads the signature as received.</li></ul> | No |
| Sign | **1 · Sign** | <ul><li>Checks again that the ZIP matches its published checksum.</li><li>Checks version, build, bundle identifier and architectures against the release you typed.</li><li>Finds both identities in the login keychain (kind and team).</li><li>Copies the app into a workspace and an output folder.</li><li>Signs every nested library and bundle, deepest first: Developer ID Application, hardened runtime, secure timestamp, no entitlements.</li><li>Signs the app with the confirmed entitlements.</li><li>Verifies: `codesign --verify --deep --strict`, then the Signing Doctor (Developer ID, team, runtime, timestamp, every nested item), the entitlements, and Gatekeeper (expected *Unnotarized* at this point).</li><li>Confirms the original is unchanged.</li></ul> | Only the timestamp request to Apple's timestamp server |
| Notarize | **2 · Notarize, staple and package** | <ul><li>`notarytool submit --wait` for the app, then `stapler staple` and `stapler validate`, then Gatekeeper.</li><li>From the **stapled** app it makes:<ul><li>the ZIP (`ditto --keepParent`), extracted again to check its ticket and Gatekeeper;</li><li>the PKG (`pkgbuild` + `productbuild --sign` Developer ID Installer; installs exactly `/Applications/<App>.app`, no scripts);</li><li>the DMG (signed with Developer ID Application).</li></ul></li><li>The PKG and DMG are notarized, stapled and checked by Gatekeeper.</li><li>Writes `SHA256SUMS.txt`.</li><li>On a rejection, Apple's notary log is saved and its path is shown.</li></ul> | Yes: the app, the PKG and the DMG go to Apple's notary service |
| Passports | **3 · Write Release Passports** | One `<file>.release-passport.json` per file. Each records:<ul><li>SHA-256, identity, architectures and signature;</li><li>notarization and Gatekeeper results;</li><li>the signing input's SHA-256 and origin;</li><li>the source checkout's own Git commit, never guessed. With an expected commit, a checkout at another commit is refused.</li></ul> | No |
| Ship | **4 · Ship to Store…** | Opens Ship to Store with the job's files. Only notarized files that Gatekeeper accepted are offered. | Only when you press **Upload private draft** there |

Each run is recorded in Build History. Its files appear in Artifacts and in Ship to Store › **Add build**.

## HCG Mail for macOS 1.0.0-beta.2, build 5

| Value | |
|---|---|
| Signing input | HCG Mail run **46007763971**, artifact **15201589154** (`hcg-mail-macos-signing-input-1.0.0-beta.2`), commit `6ddd667d9dc189e24f5d31a313a9ae96299d2dfa` (HCG Mail `main`) |
| Inside the artifact | <ul><li>`signing-input/HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip`</li><li>`signing-input/SHA256SUMS-signing-input.txt`</li><li>`signing-input/bundle-audit.txt`</li><li>`package.log`</li></ul> |
| Release | version `1.0.0-beta.2`; Info.plist `1.0.0` (build `5`); arm64 |
| Entitlements | exactly `com.apple.security.cs.allow-jit` (`HCG Mail Project/Desktop/packaging/macos/HCGMail.entitlements`) |
| Files | `HCG-Mail-1.0.0-beta.2-macos-arm64.zip`, `HCG-Mail-1.0.0-beta.2-macos-arm64.pkg` |
| Store | channel beta, eligibility private |

### Once on the Mac

1. **Executable Maker** built from Maker `main` with this page: the macOS package of that commit's Build & Release run,
   or `dotnet run --project src/HCG.ExecutableMaker.App -c Release` from a checkout.
2. **Xcode or the Command Line Tools.** They provide `codesign`, `xcrun notarytool`, `xcrun stapler`, `pkgbuild`, `productbuild`, `hdiutil` and `spctl`.
3. **Certificates.** Open Signing & Security › **Apple Signing** › **Import .p12…**:
   - import the *Developer ID Application* identity;
   - import the *Developer ID Installer* identity.

   The password is checked before anything is written and is never stored. If you created the certificates with Xcode
   on this Mac, they are already in the keychain; press **Refresh**.
4. **Notarization profile.** Maker never sees the key.
   1. In Terminal:
      ```bash
      xcrun notarytool store-credentials hcg-notary --key AuthKey_<KEYID>.p8 --key-id <KEYID> --issuer <ISSUER UUID>
      ```
   2. In Settings › **Apple credentials** › Notarization: enter **PROFILE NAME** `hcg-notary`.
   3. Press **Check profile**, then **Save Settings**.

### The release

1. **Download** artifact 15201589154 from the run page (GitHub › HCG-Mail › Actions › run 46007763971 › Artifacts).
   - Unzip the artifact itself, which gives `signing-input/…`.
   - Do **not** unzip `HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip`: Maker reads it as it is.
2. Open **Sign & Notarize (macOS)**.
3. **1. The app as built**
   1. **Choose ZIP…** → `HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip`.
   2. **Choose…** (published checksums) → `SHA256SUMS-signing-input.txt`.
   3. Where it came from: `HCG Mail run 46007763971, artifact 15201589154, commit 6ddd667`.
   4. Press **Inspect**. Expected:
      - four *Passed* checks: Published checksum, Input, Bundle, Contents/MacOS;
      - "Nested code signed first (3): …libAvaloniaNative.dylib, …libHarfBuzzSharp.dylib, …libSkiaSharp.dylib";
      - the signature as received: ad hoc.
4. **2. Release**
   - Release version `1.0.0-beta.2`.
   - Build number `5` (prefilled).
   - Architecture `arm64`.
   - The bundle identifier is prefilled.
5. **3. Developer ID identities**
   - Choose your *Developer ID Application* and *Developer ID Installer* identities.
   - If either is missing, import it first (above), then press **Refresh**.
6. **4. Entitlements**
   1. **Load .entitlements…** → `HCG Mail Project/Desktop/packaging/macos/HCGMail.entitlements` from an HCG Mail checkout.
   2. The box must show exactly `com.apple.security.cs.allow-jit`.
   3. Tick **I confirm these entitlements**.
7. **5. Distribution files**
   - **ZIP** and **PKG** ticked; DMG optional.
   - File name `HCG-Mail-1.0.0-beta.2-macos-arm64`.
   - Choose an output folder.
   - The notarization profile must show `hcg-notary`.
8. **1 · Sign** → confirm. Every check must be *Passed*, except Gatekeeper, which shows *Warning: Unnotarized* (expected before notarization).
9. **2 · Notarize, staple and package** → confirm. This sends the app to Apple and usually takes a few minutes. Expected:
   - **The app:** Submit, Notarization, Staple and Staple validation all *Passed*. Gatekeeper *Passed* ("accepted … source=Notarized Developer ID").
   - **The ZIP:** Stapled ticket *Passed*, Gatekeeper *Passed* (checked on an extracted copy).
   - **The PKG:** PKG checks, Submit, Notarization, Staple and Gatekeeper (install) all *Passed*.
   - **The original input:** *Passed*, unchanged.
10. **3 · Write Release Passports**
    - Source checkout: your HCG Mail checkout **at `6ddd667`**.
    - Expected commit: `6ddd667d9dc189e24f5d31a313a9ae96299d2dfa`.
11. **Optional, as a customer would.** HCG Mail's own installer and launch check:
    ```bash
    "HCG Mail Project/Desktop/scripts/macos/verify-distribution.sh" <out>/HCG-Mail-1.0.0-beta.2-macos-arm64.zip verify/zip --expect developer-id
    "HCG Mail Project/Desktop/scripts/macos/verify-distribution.sh" <out>/HCG-Mail-1.0.0-beta.2-macos-arm64.pkg verify/pkg --expect developer-id --install
    ```
12. **4 · Ship to Store…** opens Ship to Store with the ZIP and the PKG and their passports.
    1. Choose **HCG Mail**, then **Add New Update** (or **Add to Listed Product** › macOS arm64).
    2. Version `1.0.0-beta.2`, build `5`, channel **beta**, eligibility **private**.
    3. **Upload private draft**.

    The published Windows release is not touched. Review, approval and publication happen in HCG Store, by a person.

## Not done by this page (stated, not implied)

- **Downloading the CI artifact.** Download it from GitHub yourself. Maker verifies it against the published `SHA256SUMS-signing-input.txt`.
- **GitHub's build-provenance attestation of the signing input.** Maker does not verify it. You can check it with `gh attestation verify <zip> --repo F1r3srp3nt/HCG-Mail`, if the run produced one; that step was allowed to fail.
- **Changing the version, build number or architectures.** Info.plist must already match. Changing anything inside the app would need a new signing input. There is no universal (arm64 + x86_64) merge.
- **Non-boolean entitlements.** Values such as arrays or strings, and embedded provisioning profiles, are not supported here. Developer ID apps that need restricted entitlements are out of scope; HCG Mail needs none.
- **Installing the PKG and launching the app.** The wizard's *Test install* action covers builds made in the wizard. For this page, use HCG Mail's `verify-distribution.sh` or install the PKG by hand.
- **Creating certificates or notary keys, publishing in HCG Store, and making the macOS release current.**

## Verified so far

- Unit tests (`ExistingMacAppTests`):
  - the nested-code order;
  - each library signed without entitlements, then the bundle with them;
  - the checksum, version, build and architecture checks;
  - data files in Contents/MacOS refused;
  - the `.entitlements` parsing;
  - refusals before any signature: no Mac, entitlements not confirmed, the wrong or a missing identity, a PKG without an Installer identity;
  - the original never changed;
  - the job recorded with its input's SHA-256 and origin.
- The page in the real app (headless driver, `tools/ui-driver --mac-distribute`): an HCG-Mail-shaped signing input inspected, the checksum matched, 3 nested libraries found.
- macOS CI (`existing-mac-app` job, `tools/ci/existing-mac-app-e2e.sh`):
  - a real compiled app (app host + 3 libraries);
  - a synthetic, self-signed "Developer ID Application" identity;
  - `codesign` confirms each library and the app carry that identity, the hardened runtime and a timestamp;
  - the seal is strict and the entitlements are exactly allow-jit;
  - the original ZIP is unchanged.
- **Not verified here: notarization, stapling and Gatekeeper acceptance.** They need a real Developer ID and Apple's notary service. The first real run is the owner's, on their Mac.

## Command line

```bash
HCGExecutableMaker --mac-distribute --list-identities
HCGExecutableMaker --mac-distribute --inspect --input HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip --checksums SHA256SUMS-signing-input.txt
HCGExecutableMaker --mac-distribute --input HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip --checksums SHA256SUMS-signing-input.txt \
  --origin "HCG Mail run 46007763971, artifact 15201589154" --release-version 1.0.0-beta.2 --build-number 5 --arch arm64 \
  --identity <Developer ID Application SHA-1> --installer <Developer ID Installer SHA-1> \
  --entitlements-file "HCG Mail Project/Desktop/packaging/macos/HCGMail.entitlements" --confirm-entitlements \
  --formats zip,pkg --out ~/HCG-Mail-release --name HCG-Mail-1.0.0-beta.2-macos-arm64 \
  --notarize --notary-profile hcg-notary --source-repo <HCG Mail checkout> --expected-commit 6ddd667d9dc189e24f5d31a313a9ae96299d2dfa
```

Exit codes:
- 0: ready for Ship to Store;
- 2: usage or a missing setting;
- 6: a check failed, nothing distributable;
- 7: notarization or Gatekeeper failed.

Without `--notarize`, it stops after signing and verification.
