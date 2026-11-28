# .NET for iOS in Mobile Build Studio (HCG Mail)

## What was wrong

Symptom on Xcode 27.2, .NET SDK 10.0.403, iOS workload 27.2.10000: "dotnet publish succeeded but created no .xcarchive,
so there is nothing to export". This applied to both the iOS Archive and the iOS IPA.

There were two defects in Maker's .NET iOS adapter (`DotNetIosPlanner`).

1. **Wrong archive location.**
   - Maker passed `-p:ArchiveDir=<work>/archives` and then searched that folder for a `*.xcarchive`.
   - The SDK's Archive task (dotnet/macios `Archive.CreateArchiveDirectory`) uses a given `ArchiveDir` **verbatim as the
     archive's root**. It wrote `Info.plist` and `Products/Applications/HCGMail.iOS.app` straight into `archives/`.
   - No folder there was named `*.xcarchive`. Because `ArchiveDir` was set, nothing went to `~/Library/Developer/Xcode/Archives` either.
2. **The SDK's own IPA was switched off.**
   - Maker passed `-p:BuildIpa=false`, which disabled the .NET iOS SDK's own IPA packaging (`CreateIpa`).
   - It then tried to export the IPA with Xcode's `xcodebuild -exportArchive`, the Swift/Xcode route. .NET apps do not need that step.

Also fixed:
- .NET projects were never checked by the Signing Vault's route and entitlement validator.
- `$(AppIdentifierPrefix)` and `$(CFBundleIdentifier)` in an entitlements file were compared without being resolved.
- A manually named profile was accepted when it was for another App ID of the same team.
- The archive was always judged as "unsigned" for availability, which .NET refuses, so it showed as unavailable.
- There was no signed device-app route for .NET.

## Routes

Each route has its own folder under the build's work directory, so only files made by this build are found.

| Output | Command (by the .NET iOS SDK) | Configuration | Signing | Result |
|---|---|---|---|---|
| iOS Simulator | `dotnet build -r iossimulator-arm64 -o work/simulator` | Debug (or Release) | none | `.app` for the Simulator; **Launch in Simulator** |
| Signed device app | `dotnet build -r ios-arm64 -o work/device` + CodesignKey/CodesignProvision | Debug (or Release) | Apple Development + development profile (a compatible wildcard is fine) | signed `.app`; **Install on device** |
| XCArchive | `dotnet publish -r ios-arm64 -p:ArchiveOnBuild=true -p:ArchiveDir=work/archive/<App>.xcarchive -p:BuildIpa=false` | Release (or Debug) | signed (the SDK refuses unsigned) | `.xcarchive`; **Open in Xcode Organizer** |
| IPA | the same plus `-p:BuildIpa=true -p:IpaPackagePath=work/ipa/<App>.ipa` | Release (Debug only for a development IPA) | the certificate and profile decide the IPA type (below) | the SDK's `.ipa` and the archive; **Open in HCG Impactor**, **Install on device** (development) |

Device routes also set `OutputPath=work/device/`. If packaging fails after compiling and signing, the failure names the
signed `.app`, which is kept. Maker never renames a Simulator app or invents an archive.

| IPA type (Export method) | Certificate | Profile |
|---|---|---|
| Development (Debugging) | Apple Development | Development, for this bundle ID or a compatible wildcard; lists the devices |
| Ad hoc (Release testing) | Apple Distribution | Ad hoc for this bundle ID; lists the devices |
| App Store | Apple Distribution | App Store for **exactly** this bundle ID (never a wildcard). HCG never uploads. |
| Enterprise | Enterprise distribution | In-house |

## Before compiling

1. **Effective MSBuild properties.** Maker runs `dotnet msbuild <project> -getProperty:…`. This evaluates only; no target runs and nothing is written.
   - It uses the build's own configuration, `RuntimeIdentifier=ios-arm64` and global properties.
   - The build log lists the properties it reads, including OutputType, ApplicationId, versions, RuntimeIdentifier(s),
     ArchiveOnBuild, ArchiveDir, BuildIpa, IpaPackagePath, OutputPath, CodesignKey, CodesignProvision, CodesignEntitlements and EnableCodeSigning.
   - The build stops, and says why, if:
     - the project is not an Exe;
     - ArchiveOnBuild or BuildIpa does not evaluate to true for its route;
     - ArchiveDir is not the `.xcarchive`;
     - code signing is disabled;
     - project-default signing has no CodesignKey.
2. **Readiness** (required items):
   - the certificate type the route needs;
   - the named profile is installed, unexpired, of the right type (development / ad hoc / App Store) and covers the bundle ID.
3. **Signing Vault validation** (`SigningPrebuildCheck` with the .NET target):
   - the identity is in the profile;
   - the profile matches the App ID and the platform;
   - every entitlement in `CodesignEntitlements` is granted by the profile, after resolving `$(AppIdentifierPrefix)` and `$(CFBundleIdentifier)` for that profile's team.

## HCG Mail on your Mac (Xcode 27.2, workload 27.2.10000)

1. In Mobile Build Studio, choose HCG Mail, iOS.
2. Choose the output:
   - **iOS Simulator build**: Debug → Start → **Launch in Simulator**.
   - **iOS device build**: configuration Debug, signing "Named certificate and provisioning profile".
     - Certificate: your `Apple Development: … (TEAMID)`.
     - Profile: your wildcard development profile.
     - Start, then **Install on device** for your connected, trusted iPhone with Developer Mode on.
   - **iOS Archive (.xcarchive)**: Release, the same certificate and profile → **Open in Xcode Organizer**.
   - **iOS IPA**: Release, export method "Debugging" for a development IPA → **Open in HCG Impactor**, or **Install on device**.
     - For the App Store you need an Apple Distribution certificate and the App Store profile for `com.hakmicapitalgroup.hcgmail`.
3. **Inspect signature and entitlements** on any app or archive shows codesign's Authority, TeamIdentifier and the signed entitlements.

Nothing is uploaded to TestFlight or HCG Store.

## Verified

- **Unit tests:**
  - the routes and arguments;
  - ArchiveDir is the `.xcarchive`;
  - BuildIpa and IpaPackagePath;
  - a distribution IPA is Release only;
  - the property probe and its checks;
  - profile selection by route and bundle ID, never by team alone, and no wildcard for the App Store;
  - entitlement variables resolved per team;
  - the service with a scripted SDK that writes into ArchiveDir: the archive and IPA are found, no xcodebuild runs, and a packaging failure keeps and names the signed app.
- **Release Passport:** every device IPA gets `<file>.ipa.release-passport.json` (build half), next to it, where Ship to Store finds it. It records:
  - the IPA's SHA-256 and size, bundle ID, version, build number and architecture;
  - the source commit as Git states it, and Xcode, the .NET SDK and the iOS workload;
  - `code_signing`: every Mach-O and whether it is signed, the signed executable's entitlements, the embedded profile (name, team, type, expiry) and the verdict of Apple's `codesign --verify` (on a Mac).

  Device installation and launch stay `not-recorded`: the builder never claims them. A development IPA is platform `ios`; only an App Store profile makes it `ios-app-store`.
- **Maker's own verifier** runs Apple's `codesign --verify --deep --strict` on device apps, archives and (new) the app inside an IPA, on a Mac. The service tests replace codesign with a recording runner because their scripted bundles carry synthetic signatures; the production verifier is unchanged and its real verdict on HCG Mail is asserted in CI.
- **CI** (`hcg-mail-ios-signed`, `xcode-27`, `tools/ci/hcg-mail-ios-e2e.sh`), on the real HCG Mail at `6ddd667` through Mobile Build Studio's own UI:
  - a Simulator launch;
  - a signed Debug device app, an XCArchive and the SDK's IPA, signed with a **synthetic** Apple Development identity and wildcard profile;
  - codesign, plutil, `security cms` and unzip check the bundle ID, platform, signature, resolved entitlements and the embedded profile;
  - Maker's own codesign verdict for the device app, archive and IPA, and the IPA's Release Passport (hash, size, bundle ID, version, source commit, profile, entitlements, codesign verdict, nothing claimed about devices).
- **Not verified in CI:**
  - signing with your real Apple certificate and profile;
  - installation and launch on a physical iPhone;
  - App Store compatibility (an Apple Distribution certificate and an App Store profile; HCG never uploads).

  A Simulator build is never evidence for any of these.

  Both need your Mac, certificate and device.
