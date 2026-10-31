# HCG Executable Maker

**Hakmi Capital Group · Software Engineering & Distribution Infrastructure**

> One project in. A distributable application out.

HCG Executable Maker imports a project (ZIP archive or folder), inspects it **without executing any of its code**,
builds it with the real toolchain, packages it for distribution, and verifies every artifact with SHA-256 checksums.

**Status: Milestone 0.1 — Functional Foundation.** .NET projects (including Avalonia desktop apps) can be built and
packaged for **Windows**, **macOS** and **Linux (portable preview)**. See [`docs/STATUS.md`](docs/STATUS.md) for exactly
what is and isn't implemented.

![Dashboard](docs/screenshots/01-dashboard.png)

## What works today (M0.1)

| Area | Implemented |
|---|---|
| Import | ZIP archives and folders into isolated workspaces; rejects corrupt archives, path traversal, symlinks, duplicate entries, decompression bombs and oversize/over-count archives; preserves the original archive; never modifies your source |
| Inspection | Static .NET analysis: `.sln`, `.slnx`, `.csproj/.fsproj/.vbproj`, `Directory.Build.props`, central package versions, `global.json` roll-forward, target frameworks, output types, RIDs, package/project references, entry points, icons, Avalonia/WPF/WinForms/MAUI/web detection, platform compatibility |
| Other ecosystems | Rust, Node/Electron, Swift/Xcode, Python, Java, Kotlin/Android, Flutter, C/C++ are **recognized and reported as unsupported** — never faked |
| Build | `dotnet restore` → `build` → `publish` with structured arguments (no shell), Release/Debug, runtime selection (win/osx/linux × x64/arm64), self-contained, single-file, live log, warning/error parsing, cancellation, timeouts |
| Packaging | Portable folder · portable ZIP (Unix permissions preserved) · macOS `.app` bundle (Info.plist, `.icns`, exec bits) · `.dmg` on macOS hosts with `hdiutil` |
| Verification | Executable present, PE/Mach-O/ELF format and CPU architecture, executable bit, bundle structure, code-signature presence, archive integrity, SHA-256 (+ per-file manifests for folders) |
| Icons | PNG/ICO/ICNS inspection (size, aspect, transparency), aspect-preserving ICO/ICNS generation, original never modified |
| History | SQLite with migrations: projects, builds, settings, messages, artifacts, checksums, toolchain versions; repeat builds, view logs, delete history, remove workspaces |
| Toolchains | .NET SDK, Git, OS/CPU, disk space; macOS (Xcode CLT, Xcode, codesign, hdiutil, notarytool, productbuild); Windows (Windows SDK, signtool, MakeAppx, MSBuild, WiX, Inno Setup). Never installs anything |
| Security | Explicit trust confirmation before every build; secrets scrubbed from the build environment; no credentials stored; signing capabilities reported honestly as **not implemented** |

**Not yet implemented** (shown in the UI as *Planned*, never as working): Windows installers (.exe/.msi/.msix — M0.2),
macOS `.pkg`/App Store packaging (M0.3/M0.4), code signing and notarization (M0.4), Linux AppImage/DEB/RPM and
Android (M0.5+), sandboxed build workers.

## Requirements

- **.NET 10 SDK** (to build HCG Executable Maker and to build the projects it imports)
- Windows 10+, macOS 11+ or a modern Linux desktop
- Network access to NuGet for package restore

## Build, run, test

```bash
dotnet build HCG.ExecutableMaker.slnx
dotnet run --project src/HCG.ExecutableMaker.App

# Unit tests only (fast, ~1 s)
HCG_SKIP_INTEGRATION=1 dotnet test

# Everything, including real end-to-end builds of tests/Fixtures/HelloAvalonia (~1–2 min, needs NuGet access)
dotnet test
```

Publish HCG Executable Maker itself:

```bash
dotnet publish src/HCG.ExecutableMaker.App -c Release -r osx-arm64 --self-contained   # or win-x64, osx-x64, linux-x64
```

App data lives in the per-user application-data folder (`HCG/ExecutableMaker`), or in
`$HCG_EXECUTABLE_MAKER_HOME` when that variable is set.

## Security model in one paragraph

Importing and inspecting are safe: nothing from the project runs. **Building is not sandboxed**: `dotnet restore/build`
executes MSBuild targets, analyzers and source generators from the project with your user's permissions. The app
explains this and requires an explicit acknowledgement before each build; credential-like environment variables are
removed from the build process. Only build projects you trust. Details: [`docs/SECURITY.md`](docs/SECURITY.md).

## Documentation

- [`docs/STATUS.md`](docs/STATUS.md) — what is complete, what is not, verification evidence, continuation instructions
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — solution structure, data flow, extension points
- [`docs/SECURITY.md`](docs/SECURITY.md) — trust boundary, import hardening, secrets, signing roadmap
- [`docs/ENVIRONMENT-REPORT.md`](docs/ENVIRONMENT-REPORT.md) — the development environment M0.1 was built and verified in
- [`docs/HCG-MAIL-VALIDATION.md`](docs/HCG-MAIL-VALIDATION.md) — workflow for the first production validation (HCG Mail)
- [`CHANGELOG.md`](CHANGELOG.md)

## Branding

No official HCG logo asset has been provided yet. The sidebar shows a clearly-marked text placeholder; drop the
approved logo at `src/HCG.ExecutableMaker.App/Assets/Branding/hcg-logo.png` and it is used automatically
(see [`Assets/Branding/README.md`](src/HCG.ExecutableMaker.App/Assets/Branding/README.md)).

© Hakmi Capital Group.
