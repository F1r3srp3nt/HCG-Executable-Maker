# macOS distribution: signing, installer packages, notarization, Mac App Store

This page covers what HCG Executable Maker does after it builds a macOS app, which Apple requirements each step
follows, and what is verified where. It applies to Swift (SwiftPM) GUI apps. .NET apps get the unsigned installer
package only (see [Limits](#limits)).

## Apple requirements this follows

Checked against Apple's documentation (fetched November 2026):

| Topic | Source |
|---|---|
| Container formats, installer identities, `productbuild --sign … --component App.app /Applications`, `ditto -c -k --keepParent`, signing order | [Packaging Mac software for distribution](https://developer.apple.com/documentation/xcode/packaging-mac-software-for-distribution) |
| Notarization requirements (Developer ID certificate, hardened runtime, secure timestamp, no `get-task-allow`) | [Notarizing macOS software before distribution](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution), [Resolving common notarization issues](https://developer.apple.com/documentation/security/resolving-common-notarization-issues) |
| `notarytool submit --keychain-profile --wait`, `store-credentials`, accepted containers (UDIF DMG, signed flat pkg, ZIP), stapling, ZIPs can't be stapled | [Customizing the notarization workflow](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow) |
| Hardened-runtime resource entitlements (`device.audio-input`, `device.camera`, `personal-information.*`, `automation.apple-events`) | [Hardened Runtime](https://developer.apple.com/documentation/security/hardened-runtime) |
| App Sandbox entitlements (`network.client/server`, `device.microphone/camera/bluetooth/usb`, `print`, `files.user-selected.*`, `files.downloads.*`) | [App Sandbox](https://developer.apple.com/documentation/security/app-sandbox), [Entitlements](https://developer.apple.com/documentation/bundleresources/entitlements) |
| Provisioning profiles, `Contents/embedded.provisionprofile`, restricted entitlements need profile authorization | [TN3125: Inside code signing: provisioning profiles](https://developer.apple.com/documentation/technotes/tn3125-inside-code-signing-provisioning-profiles) |
| Mac App Store: sandbox mandatory, Xcode technologies only, updates only via the store (2.4.5) | [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/#2.4.5) |

## Three signing levels, kept apart

| Level | Certificate | Used for | Result |
|---|---|---|---|
| **Ad hoc** (default) | none (`codesign --sign -`) | testing on this Mac | Other Macs show a Gatekeeper warning |
| **Developer ID** | *Developer ID Application* (app, DMG) and *Developer ID Installer* (.pkg) | direct distribution | Notarize, then Gatekeeper accepts it |
| **Mac App Store** | *Apple Distribution* (app) and *Mac Installer Distribution* / `3rd Party Mac Developer Installer` (.pkg) + Mac App Store profile | App Store Connect | You upload it manually |

HCG reads identities with `security find-identity -v`, without `-p codesigning`, so installer identities are listed
as well. It classifies each identity by its certificate name and passes the identity to `codesign` by SHA-1, so a
duplicate name can't select the wrong certificate. Private keys stay in the keychain, and HCG never reads them.

## Capability states

Every macOS format in *New Build › Package* has one of these states, each with its reason:

- **Ready**: the pipeline exists and all prerequisites are present.
- **Setup required**: a certificate, profile, credential or tool is missing. The reason gives the exact setup step.
- **Incompatible with this project**: for example, a command-line tool, root-level SwiftPM resource bundles that
  cannot be sealed, or App Sandbox blockers found in the sources.
- **Failed**: reading the keychain failed. The reason is the tool's error.
- **Needs another host**: Windows or Linux.

No macOS format is a milestone placeholder any more.

## Entitlements come from evidence

HCG scans the product's sources as text and records evidence. Examples:

- `AVAudioRecorder` → microphone
- `URLSession` → outgoing network
- `NSSavePanel` → user-selected files
- `Process()` → review item
- `AXIsProcessTrusted` or Sparkle → sandbox blocker

From this evidence HCG proposes:

- **Developer ID**: only the hardened-runtime entitlements the sources need. For example, Relay gets
  `com.apple.security.device.audio-input`. Outgoing network access needs no entitlement outside the sandbox.
- **Mac App Store**: `com.apple.security.app-sandbox` plus the sandbox entitlements the sources need, for example
  `device.microphone` and `network.client`. Review items and blockers are listed separately. HCG never adds a
  temporary-exception entitlement.

You confirm the list ("I reviewed these entitlements") before anything is signed. If you uncheck an entitlement, the
build summary lists the feature as disabled. HCG never adds an entitlement without evidence. After signing,
verification compares the signed entitlements with the confirmed set exactly.

## Pipelines

### Developer ID app (Swift)
`codesign --force --timestamp --options runtime --entitlements <confirmed> --sign <SHA-1> App.app`. The checks are
reported separately:

- signature (`--verify --strict --deep`);
- certificate type and team;
- hardened runtime;
- secure timestamp;
- entitlements, including no `get-task-allow`;
- Gatekeeper. Before notarization, `spctl` reports "Unnotarized Developer ID", which is shown as expected.

SwiftPM resource bundles must sit at the app root, outside the seal (see SWIFT-MACOS.md), so Developer ID and Mac App
Store signing are **incompatible** with packages that have them. HCG says so before building; it does not strip them.

### DMG
`hdiutil create` from a staging folder containing the app and an Applications link. For Developer ID builds the DMG
is then signed with the Developer ID Application identity and a timestamp.

Every DMG is mounted read-only and the app inside is compared byte for byte with the built app: content, permissions,
`_CodeSignature` and resources. The Applications link is checked too.

### Installer package (.pkg)
1. Copy the app with `ditto` into a staging root.
2. Write a component plist ourselves: `BundleIsRelocatable` false, so the app lands at `/Applications/Name.app`.
3. Run `pkgbuild --install-location /Applications --identifier <bundle id> --version <version>`, with no `--scripts`.
4. Write `distribution.xml` ourselves and build with `productbuild --distribution`. The distribution:
   - sets `customize="never"` and `require-scripts="false"`;
   - restricts installs to the app's architectures (`hostArchitectures`);
   - checks `LSMinimumSystemVersion`;
   - allows *all users* (`/Applications`, needs admin) or *current user* (`~/Applications`).
5. Signing:
   - **Unsigned**: for local testing only, and labelled that way.
   - **Signed**: `--sign "Developer ID Installer: …"`. This needs a Developer ID-signed app; HCG refuses to sign an
     installer around an ad-hoc app because it could never be notarized.

Inspection runs without installing:

- `pkgutil --check-signature` must match the expected signer;
- `pkgutil --expand-full`: exactly one component, no Scripts, and PackageInfo identifier/version/location as expected
  and not relocatable;
- the payload app must be byte-identical to the built app;
- `spctl -t install` for signed packages.

### Mac App Store package
Blockers are checked first:

- certificates;
- profile type, App ID match, expiry, and whether it includes the chosen certificate;
- confirmed entitlements;
- sandbox blockers;
- root resource bundles.

Then:
1. Copy the built app (`ditto --noextattr --noqtn`) and run `xattr -cr`. App Store apps must not carry
   `com.apple.quarantine`.
2. Embed the profile as `Contents/embedded.provisionprofile`.
3. Sign with the Apple Distribution identity. The entitlements are the sandbox set plus the confirmed set plus
   `com.apple.application-identifier` (`TEAMID.bundle-id`) and `com.apple.developer.team-identifier` from the profile.
4. Run `productbuild --component App.app /Applications --sign "3rd Party Mac Developer Installer: …"`.

The checks then cover:

- signature and certificate;
- sandbox and entitlements;
- application identifier;
- profile;
- no quarantine attribute;
- installer signature;
- payload equality.

The `.app` artifact keeps its own (ad-hoc or Developer ID) signature; only the store copy is store-signed.

## Explicit steps after a build (Results page)

Each step asks for confirmation and reports every sub-step on its own.

- **Notarize with Apple…** (Developer ID builds only)
  - Each container is submitted with
    `xcrun notarytool submit <file> --keychain-profile <name> --wait --output-format json`: the app (zipped with
    `ditto`), the DMG and the Developer ID-signed .pkg.
  - On *Invalid*, the notary log is saved to the logs folder.
  - On *Accepted*: `xcrun stapler staple`, `stapler validate`, then `spctl`.
  - A ZIP can't be stapled, so the `.app.zip` is recreated from the stapled app.
  - Checksums are updated.
- **Test install…** (.pkg)
  - `installer -pkg … -target CurrentUserHomeDirectory`: no administrator rights, installs to `~/Applications`.
  - The installed app is compared with the built app, then its signature is checked.
  - It is launched only if you tick *Launch after test install*, because launching runs the project's code.
  - Finally HCG removes the app and forgets the receipt.
  - It refuses to run if an app with that name already exists there.
- **Validate with App Store Connect…** (Mac App Store .pkg)
  - Runs `xcrun altool --validate-app -f <pkg> -t macos --apiKey <id> --apiIssuer <id>`. Nothing is uploaded.
  - Upload (Transporter or `altool --upload-app`) and submission for review stay manual.

## Credentials (Settings › Apple credentials)

- **Notarization**
  - Run `xcrun notarytool store-credentials "<profile>" --apple-id "<id>" --team-id "<team>"` in Terminal. notarytool
    asks for the app-specific password and stores it in the login keychain.
  - HCG stores only the profile name. *Check profile* runs `notarytool history`.
- **App Store Connect**
  - HCG stores the API Key ID and Issuer ID.
  - The `.p8` stays in `~/.appstoreconnect/private_keys/AuthKey_<KeyID>.p8`.
- HCG never writes passwords, keys or certificates to project files, its database or logs.

## Xcode selection

Toolchains › Xcode lists the Xcode apps it finds in /Applications, ~/Applications and through Spotlight. You can also
add one with *Browse…*. Selecting one (*Select Xcode*) sets `DEVELOPER_DIR` for HCG's own process. Every tool HCG
starts (probes, `swift`, `codesign`, `productbuild`, `notarytool`, `altool`) uses that Xcode. The system setting
(`xcode-select`) is never changed. *Use system setting* removes the choice.

The page tells these states apart, each with its exact fix:

- not installed;
- installed but not selected;
- license not accepted;
- first-launch setup needed;
- no SDKs;
- validation failed (with the tool's error).

The Command Line Tools stay sufficient for ad-hoc Swift builds. The build log records the developer directory used.

## What is verified where

| Step | Linux (unit/UI tests here) | macOS CI job (prepared, runs on push) | Needs your Mac + Apple account |
|---|---|---|---|
| Pipelines implemented, parsers, states, entitlement planning, pkg XML, payload/DMG comparison | ✅ | ✅ | — |
| Unsigned .pkg generated + inspected | — | ✅ | — |
| .pkg installed (`sudo installer -target /` on the disposable runner, and the app's own Test install into `~/Applications`) + launched | — | ✅ | — |
| Xcode selected for HCG only; build used it; `xcode-select` unchanged | — | ✅ | — |
| Developer ID signing, signed DMG/.pkg, notarization, stapling | — | — | ✅ (certificates + notary profile) |
| Mac App Store package, App Store Connect validation | — | — | ✅ (distribution certificates, profile, API key) |

## Limits

- .NET apps: unsigned `.pkg` only. Developer ID and the Mac App Store are *incompatible in this version*: the hardened
  runtime needs .NET-specific exceptions, and the sandbox needs a review that HCG does not perform.
- Command-line tools are not packaged as `.pkg` or signed for distribution.
- Packages with SwiftPM resource bundles cannot be Developer ID or App Store signed (see above).
- Developer ID provisioning profiles, needed only for restricted entitlements such as iCloud, are not embedded. HCG
  proposes no restricted entitlements.
