# Building iOS apps

iOS apps can only be built on **macOS with Xcode** — Apple's compilers, SDKs and code signing do not run on Windows
or Linux. On other hosts every iOS output is shown as unavailable with that reason; Xcode projects can still be
imported and inspected.

## Requirements

| Tool | Detection |
|---|---|
| Xcode (selected with `xcode-select`) | `xcodebuild -version` |
| iOS SDKs (device and Simulator) | `xcodebuild -showsdks -json` |
| Code-signing identities (for IPAs) | `security find-identity -v -p codesigning` (names only; no keys are read) |
| Provisioning profiles | count of installed profiles |

## Detection (static)

`project.pbxproj` is parsed as an OpenStep property list (never executed): targets and product types, bundle
identifiers, deployment targets, `DEVELOPMENT_TEAM`, `CODE_SIGN_STYLE` (Release configuration preferred), build
configurations, Swift package references, shared schemes (`xcshareddata/xcschemes`), workspaces, and CocoaPods
(a `Podfile` without a workspace is reported: run `pod install` first). Library-only projects have no app target and
are not offered for building.

## Outputs

| Output | Command (structured arguments, no shell) | Distributable? |
|---|---|---|
| Simulator app | `xcodebuild -project/-workspace … -scheme … -configuration Debug -sdk iphonesimulator -destination "generic/platform=iOS Simulator" -derivedDataPath <build>/DerivedData build` | **No** — runs only in the Simulator |
| Archive | `xcodebuild … -destination "generic/platform=iOS" -archivePath <build>/<Scheme>.xcarchive archive` (+ signing settings) | Only if signed with an Apple certificate |
| IPA | archive, then `xcodebuild -exportArchive -archivePath … -exportPath … -exportOptionsPlist …` | Depends on the export method and profile |

### Signing

- **Project signing configuration** — the project's own settings and the certificates already in the keychain.
- **Unsigned** — `CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY=` for an inspection-only archive.
- **Automatic** — `CODE_SIGN_STYLE=Automatic DEVELOPMENT_TEAM=<10-character team ID>`. `-allowProvisioningUpdates`
  (Xcode may contact Apple using the account signed in to Xcode) is added **only** when explicitly enabled.
- **Manual** — `CODE_SIGN_STYLE=Manual`, team and an installed provisioning profile name.

Team IDs, schemes, configurations and profile names are validated before use. HCG never creates certificates or
profiles, never asks for Apple ID credentials, and never bypasses code signing.

### IPA export

After archiving, HCG inspects the archive. If it is **not signed with an Apple development or distribution
certificate**, export is refused with that message (Xcode could not export it either). Otherwise HCG writes
`ExportOptions.plist` with the chosen method (`app-store-connect`, `release-testing`, `enterprise`, `debugging`),
**`destination = export`**, `uploadSymbols = false`, team and signing style (plus the bundle-ID → profile mapping for
manual signing), runs the export, and deletes the options file afterwards. The `app-store-connect` method only produces
a file suitable for upload — HCG never uploads to TestFlight or App Store Connect.

## Verified in CI

The *Mobile — iOS* job builds `tests/Fixtures/HelloIOS` on GitHub's macOS runner: Simulator build, unsigned archive,
the refusal to export an unsigned archive, and the honest failure of a signed archive without a development team
(CI has no Apple certificates). See `docs/STATUS.md` for the latest result.
