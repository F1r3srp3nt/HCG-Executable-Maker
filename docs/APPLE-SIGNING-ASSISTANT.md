# Apple Signing Assistant

Mobile Build Studio decides how a native Xcode iOS project is signed, before anything compiles. You should not have to
open Xcode and configure every imported project first.

1. **Read the effective build settings.** It runs `xcodebuild -showBuildSettings -json` for the destination and
   configuration, so xcconfig files and `Config/Local.xcconfig` count as Xcode applies them. From the app target it
   reads:
   - `DEVELOPMENT_TEAM` and `CODE_SIGN_STYLE`;
   - `PRODUCT_BUNDLE_IDENTIFIER`;
   - `CODE_SIGN_ENTITLEMENTS`;
   - `TARGETED_DEVICE_FAMILY` and `SWIFT_ACTIVE_COMPILATION_CONDITIONS`.
2. **Read what Maker knows:**
   - the project's signing profile in Signing Vault (team, mode, profile, intended devices);
   - this Mac's identities, with their private keys;
   - this Mac's verified provisioning profiles.
3. **Decide the signing settings for this destination.** They are passed to xcodebuild as build settings for this
   build only. **The imported project, its xcconfig files and Config/Local.xcconfig are never modified.**

## Destinations

| Destination | Configuration (default) | Signing |
|---|---|---|
| iOS Simulator (Debug) | Debug | None needed. If the project has no team, the build is signed to run locally (ad hoc: `CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY=-`, entitlements kept), so Xcode's "requires a development team" never stops a Simulator build. |
| Physical iPhone (Debug) / Physical iPad (Debug) | Debug | Apple Development: a team, an Apple Development identity with its private key, and a development profile for the bundle identifier. Xcode-managed provisioning can provide the profile only when you authorize it; it then also registers connected devices (`-allowProvisioningUpdates -allowProvisioningDeviceRegistration`). An iPad build of an iPhone-only app is flagged. |
| iOS distribution archive | Release | Apple Distribution, or unsigned for inspection. |
| TestFlight / App Store | Release | Apple Distribution with an App Store profile. The App Store Connect .ipa is exported locally. **Nothing is uploaded.** |
| macOS development / macOS Developer ID | (macOS Xcode provider) | See SIGNING-VAULT.md and NATIVE-MACOS-XCODE.md. |

**Debug and Release.** Configuration is chosen explicitly in Mobile Build Studio. Simulator and device builds default
to Debug, so development features a project limits to Debug stay available; Unimatch's Development Run is one.
Distribution destinations (archive, TestFlight, App Store) must be Release:
- A Debug distribution build is refused before compiling.
- So is a "Release" configuration that still compiles with `DEBUG`.

**Team.** Maker uses, in order:
1. the team chosen for the build;
2. the team saved in the project's Signing Vault profile;
3. the project's own team (for example from `Config/Local.xcconfig`, which is then not overridden).

A team you type or choose ("Use team …", offered for each team that has a usable identity on this Mac) is saved in
the project's signing profile, so later builds reuse it. Only references are stored (Team ID, certificate SHA-1,
profile UUID): private keys stay in this Mac's keychain.

**Bundle identifier for one build.** If the project builds its identifier from a variable, for example
`PRODUCT_BUNDLE_IDENTIFIER = $(UNIMATCH_BUNDLE_ID)`, Maker overrides that variable, so derived identifiers
(`….uitests`) follow.

## What the build is (verified before compiling)

| Check | How | A development build | A distribution build |
|---|---|---|---|
| Source commit | The imported copy's commit (`import-revision.json`), compared with the original folder | Refused if stale; the optional expected commit must match | Same |
| Configuration | Chosen explicitly; development destinations default to Debug | Debug | Release |
| Compilation flags | `SWIFT_ACTIVE_COMPILATION_CONDITIONS` of this configuration, and of the other one | `DEBUG` and the development-only flags (Debug minus Release, e.g. `UNIMATCH_DEVELOPMENT_RUN`) are reported active; the optional expected flags must be active | None of the development-only flags may be active |

The build record shows the source commit, configuration, compilation flags and development-only flags, and so does
Mobile Build Studio ("What this build is").

**Why Unimatch's Development Run was missing.** Before the iOS device build existed, a device install went through
an archive or .ipa, and those were built Release. Unimatch enables `UNIMATCH_DEVELOPMENT_RUN` only for Debug, so the
skip button was correctly absent. Now:
- **iOS device build → iPhone → Debug** (also a development-signed .ipa) compiles with `DEBUG UNIMATCH_DEVELOPMENT_RUN`;
- public Release archives never contain them.

**Install and open.** After a development build, **Install on a connected device** lists paired iPhones and iPads
(devicectl), installs after you confirm, and opens the app. The device is remembered in the project's Signing Vault
profile, so later builds check its registration.

## When signing is missing: Resolve Signing

- **Missing before the build:** if a team, certificate, profile or device registration is missing, the build stops
  before compiling. The problem is named, with the fix, and the build record carries `HCG-SIGNING-RESOLVE`.
- **Rejected by Xcode during the build:** if Xcode still rejects signing, for example with
  - "Signing for "X" requires a development team",
  - "No Accounts",
  - "No profiles for …",
  - a certificate missing from the profile, or
  - an unregistered device,

  the error line is recognized and explained in the same way.

Mobile Build Studio then shows **Signing needs attention**. You can:
- pick a team, signing mode or Xcode-managed provisioning in place;
- or press **Resolve Signing**, which opens Signing & Security with the project selected;
- then start the build again. **The project does not need to be imported again.**

## Unimatch

Unimatch has:
- automatic signing, with `Config/Base.xcconfig: DEVELOPMENT_TEAM =` (empty);
- `#include? "Local.xcconfig"`;
- `PRODUCT_BUNDLE_IDENTIFIER = $(UNIMATCH_BUNDLE_ID)`;
- `SWIFT_ACTIVE_COMPILATION_CONDITIONS[config=Debug] += UNIMATCH_DEVELOPMENT_RUN`.

Unimatch's repository is private, so CI uses `tests/Fixtures/SigningProbe`, which has the same shape:

| CI check (macOS job "Mobile — iOS") | Expected |
|---|---|
| Simulator Debug, no team | Builds, ad hoc signed, and contains the Debug-only Development Run text |
| iPhone Debug, no team | Stops before compiling: "requires a development team" (Resolve Signing) |
| iPhone Debug, a team but no Apple account on the runner | xcodebuild's signing error becomes Resolve Signing ("Xcode could not sign the app") |
| Debug distribution archive | Refused: "must be a Release build" |
| Release archive (unsigned) | Builds and contains no Development Run text |

**On your Mac**, the physical-device step:
1. Mobile Build Studio → Unimatch → **iOS device build** → iPhone → Debug → Signing: Xcode-managed.
2. **Use team …**, or type your Team ID.
3. If no development profile exists yet, tick **Allow Xcode to update provisioning**.
4. Optionally enter **Expected source commit** `74229fe` and **Flags that must be active**
   `DEBUG, UNIMATCH_DEVELOPMENT_RUN`.
5. **Start Mobile Build.** "What this build is" shows commit 74229fe…, configuration Debug and
   `DEBUG UNIMATCH_DEVELOPMENT_RUN`.
6. **Install and open** on your iPhone. The welcome screen shows "Skip — Development Run Only".

The log shows the effective settings, every signing check and the build settings passed. The .app is installable on
the registered iPhone, for example with HCG Impactor.
