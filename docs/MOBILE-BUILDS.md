# Mobile Build Studio (Milestone 0.5)

Mobile Build Studio builds **Android** (`.apk`, `.aab`) and **iOS** (Simulator `.app`, `.xcarchive`, `.ipa`) artifacts
from real mobile projects with their own toolchains, then verifies the result. It is a page in the app (sidebar →
*Mobile Build Studio*) and is also available as a command-line tool for automation (`tools/mobile-build`).

## What it is — and what it is not

| Supported | Not supported (and never pretended) |
|---|---|
| Native Android Gradle projects (Java/Kotlin, Groovy or Kotlin DSL, version catalogs, flavors) | Converting a desktop `.exe`/`.app` into an iOS or Android app |
| Native iOS Xcode projects/workspaces (SwiftUI/UIKit, Swift packages, CocoaPods workspaces) | Renaming a file to another extension |
| Flutter projects (Android and iOS) — macOS/Linux hosts | APK → AAB conversion (an App Bundle cannot be reconstructed from an APK) |
| .NET MAUI projects (Android; iOS Simulator and IPA) | React Native, Unity, Electron/Capacitor mobile builds (recognized, reported as unsupported) |
| AAB → APK set with Google's bundletool (universal or device-targeted) | Uploading to Google Play, TestFlight or App Store Connect |
| Verifying any `.apk`, `.aab`, `.apks`, `.ipa`, `.xcarchive`, `.app` | Creating certificates, provisioning profiles or production keystores |

## Workflow

1. **Project** — import a folder, a ZIP, or a single artifact (`.apk/.aab/.apks/.ipa`). Imports are copied into an
   isolated workspace; artifacts are copied unextracted, and a file whose content is not a ZIP package is rejected
   (renaming does not make a file an APK). Inspection is static: nothing from the project runs.
2. **Target platform** — Android or iOS, as far as the project actually targets them.
3. **Build output** — every output is listed; unavailable ones show the concrete reason
   (e.g. *“iOS builds require a Mac with Xcode”*, *“The Android SDK was not found (set ANDROID_HOME)”*,
   *“bundletool was not found”*). The single source of truth is `MobileCapabilities`.
4. **Build configuration** — module, variant/configuration, scheme, signing mode, Apple team/profile, IPA export
   method, APK-set mode, output folder.
5. **Build status** — explicit trust confirmation (per build), then the live log, stage, warnings/errors, cancel.
6. **Artifacts** — every output is copied into `<output>/<Name>-<version>-<kind>/`, verified, checksummed
   (`SHA256SUMS.txt`, per-file manifests for bundles) and recorded in build history.

## Output matrix

| Output | Native Android | Native iOS | Flutter | .NET MAUI | Imported `.aab` |
|---|---|---|---|---|---|
| Debug APK | `:app:assemble<Variant>` | — | `flutter build apk --debug` | `dotnet build -f *-android` | — |
| Release APK | `:app:assemble<Variant>` | — | `flutter build apk --release` | `dotnet publish -f *-android` | — |
| App Bundle | `:app:bundle<Variant>` | — | `flutter build appbundle` | `dotnet publish … AndroidPackageFormats=aab` | — |
| AAB → APK | bundle + `bundletool build-apks` | — | appbundle + bundletool | publish + bundletool | `bundletool build-apks` |
| Simulator app | — | `xcodebuild -sdk iphonesimulator` | `flutter build ios --simulator` | `dotnet build -f *-ios` (simulator RID) | — |
| Archive | — | `xcodebuild archive` | `flutter build ipa [--no-codesign]` | not offered (MAUI publishes an IPA) | — |
| IPA | — | archive → `xcodebuild -exportArchive` | `flutter build ipa --export-method …` | `dotnet publish -f *-ios` | — |

## Signing modes

| Mode | Meaning |
|---|---|
| Project signing configuration | Whatever the project configures. HCG never supplies credentials (see *Security*). |
| Unsigned | Android: unsigned release (refused if the module defines its own release signing, because HCG does not edit project files). iOS: `CODE_SIGNING_ALLOWED=NO` archive for inspection only. |
| HCG local debug key | Android only. A debug keystore created once in HCG's data folder with the conventional public debug alias/password; installable for testing, never accepted by Google Play. |
| Xcode automatic | `CODE_SIGN_STYLE=Automatic` + `DEVELOPMENT_TEAM`; `-allowProvisioningUpdates` only when explicitly enabled. |
| Xcode manual | `CODE_SIGN_STYLE=Manual` + team + an installed provisioning profile name. |

## Command line

```bash
# Inspect only (no build)
dotnet run --no-cache tools/mobile-build/mobile-build.cs -- path/to/project /tmp/out --inspect-only

# Build (requires --trust: it runs the project's build scripts)
dotnet run --no-cache tools/mobile-build/mobile-build.cs -- path/to/project /tmp/out --output android-debug-apk --trust
dotnet run --no-cache tools/mobile-build/mobile-build.cs -- app-release.aab /tmp/out --output aab-to-apk --trust
dotnet run --no-cache tools/mobile-build/mobile-build.cs -- path/to/ios /tmp/out --output ios-ipa \
    --signing automatic --team ABCDE12345 --export release-testing --trust
```

Kinds: `android-debug-apk`, `android-release-apk`, `android-aab`, `aab-to-apk`, `ios-simulator`, `ios-archive`, `ios-ipa`.
The CLI uses a throw-away data folder unless `HCG_EXECUTABLE_MAKER_HOME` is set.

See also: [ANDROID-BUILDING.md](ANDROID-BUILDING.md), [IOS-BUILDING.md](IOS-BUILDING.md),
[MOBILE-ARTIFACT-VERIFICATION.md](MOBILE-ARTIFACT-VERIFICATION.md).
