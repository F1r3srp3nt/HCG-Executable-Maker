# Test fixtures

Standalone projects used by integration tests. They are **not** part of `HCG.ExecutableMaker.slnx` and do not
inherit the repository's build settings (see the empty `Directory.Build.props` / `Directory.Packages.props` here).

- `HelloAndroid` — minimal Java Android app (AGP 8.7.3, Gradle 8.14.3 wrapper, no third-party dependencies). Built
  for real in CI (Android SDK on the Ubuntu runner): debug APK, release APK, AAB, AAB→APK via bundletool.
- `HelloIOS` — minimal SwiftUI app with a hand-written Xcode project and shared scheme. Built for real on the CI macOS
  runner: Simulator build, unsigned device archive, and IPA-export signing diagnostics.
- `HelloAvalonia` — minimal Avalonia 12 desktop app (net10.0, WinExe). The end-to-end test copies it to a temp
  directory, imports it, inspects it, and publishes it for linux-x64, win-x64 and osx-arm64.
