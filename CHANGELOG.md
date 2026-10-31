# Changelog

All notable changes to HCG Executable Maker. Format based on [Keep a Changelog](https://keepachangelog.com/).

## [0.1.0] — Milestone 0.1 "Functional Foundation" — unreleased

### Added
- Modular .NET 10 solution: Domain, Core, Security, Infrastructure, ProjectInspection, Packaging, BuildEngine, App, Tests.
- Avalonia 12 desktop application with the **HCG Future** design-token theme, persistent sidebar, Dashboard, six-step
  New Build wizard, Projects, Build History, Artifacts, Toolchains, Signing & Security and Settings pages.
- **HCG Classic** theme as a token-level preview; runtime theme switching.
- Secure project import from ZIP archives and folders into isolated workspaces (traversal/symlink/duplicate/bomb
  protection, configurable limits, original archive preserved, atomic commit, progress and cancellation).
- Static .NET project inspection (sln/slnx/csproj/fsproj/vbproj, Directory.Build.props, central package management,
  global.json roll-forward, TFMs, RIDs, references, entry points, icons, UI-framework and platform detection).
- Recognition (without build support) of Rust, Node/Electron, Swift/Xcode, Python, Java, Kotlin/Android, Flutter, C/C++.
- Toolchain detection for .NET SDK, Git, host OS/CPU, disk space, macOS and Windows packaging/signing tools.
- Build engine: structured `dotnet restore/build/publish`, Release/Debug, win/osx/linux × x64/arm64, self-contained,
  single-file, live log with warning/error parsing, cancellation (process-tree kill), timeouts, explicit trust gate,
  secret-scrubbed build environment, `IBuildWorker` isolation seam.
- Packaging: portable folder, portable ZIP with Unix permissions, macOS `.app` bundle (+ transfer ZIP off-Mac),
  `.dmg` on macOS hosts.
- Artifact verification (format/architecture, permissions, bundle structure, signature presence, archive integrity),
  SHA-256 checksums, per-file manifests, `SHA256SUMS.txt`, `.hcg-build.json` output marker.
- Icon engine: PNG/ICO/ICNS inspection and aspect-preserving ICO/ICNS generation.
- SQLite persistence with versioned migrations; settings store; JSON-lines application log.
- 154 automated tests including real end-to-end builds of an Avalonia fixture for linux-x64, osx-arm64 and win-x64.
- Documentation: architecture, security model, environment report, status/continuation, HCG Mail validation workflow.

### Branding
- Installed the founder-provided temporary official HCG logo: sidebar wordmark, About card, window and dialog icons,
  Windows application icon. The original artwork is preserved unmodified in `branding/`; derived assets (transparent
  wordmark, square app icon, `.ico`) are reproducible with `tools/branding/make_logo.py`.

### Testing & distribution
- `tools/self-package/self-package.cs`: HCG Executable Maker builds and packages **itself** through its own engine
  (macOS arm64/x64 `.app` + transfer ZIPs, Windows x64 ZIP), verified and checksummed.
- GitHub Actions workflow *Build & Release*: tests on Linux/macOS/Windows, self-packaging on each real OS (`.dmg` on
  macOS, embedded `.exe` icon on Windows), artifacts on every push, GitHub pre-release on `v*` tags only.
- `self-package.cs` options: `--targets`, `--version`, `--dist`; `.dmg` produced automatically on Mac hosts.
- `DmgPackager` retries `hdiutil create` (intermittent "Resource busy" failures).
- Tests are portable to Windows (platform-appropriate absolute paths; Windows MSBuild diagnostics covered).
- `docs/TESTING.md`: how to get a build, first launch of the unsigned app on macOS/Windows, and a 10-minute test script.

### Fixed during M0.1 verification
- Path containment check mishandled file-system roots (`/`), breaking disk-space detection.
- Cancelling a build during packaging left a partial output folder that blocked later builds; HCG now claims the folder
  immediately and removes its own incomplete output.
