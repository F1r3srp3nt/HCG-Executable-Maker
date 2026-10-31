# Test fixtures

Standalone projects used by integration tests. They are **not** part of `HCG.ExecutableMaker.slnx` and do not
inherit the repository's build settings (see the empty `Directory.Build.props` / `Directory.Packages.props` here).

- `HelloAvalonia` — minimal Avalonia 12 desktop app (net10.0, WinExe). The end-to-end test copies it to a temp
  directory, imports it, inspects it, and publishes it for linux-x64, win-x64 and osx-arm64.
