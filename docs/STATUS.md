# Status — Milestone 0.1 (Functional Foundation)

## Complete and verified

Verification was done in a Linux x64 development container (see `ENVIRONMENT-REPORT.md`).

| M0.1 requirement | Evidence |
|---|---|
| Working Avalonia desktop app, HCG Future theme, sidebar navigation | App launched under Xvfb; every page rendered and exercised; screenshots in `docs/screenshots/` |
| Dashboard from real local data | Counts, recent projects/builds, host and toolchains come from SQLite and live detection; empty states shown when empty |
| ZIP import, directory import, previously imported projects | ZIP imported through the real GTK file picker in the running app; 19 importer + 19 path-guard tests incl. traversal/symlink/limits |
| .NET project inspection | 31 inspection tests; fixture correctly analysed in-app (Avalonia 12.1.4, .NET 10, entry point, icon) |
| Toolchain detection | Real detection test on host (.NET SDK 10.0.114, Git 2.43.0, disk space); macOS/Windows tools report *Not on this host* |
| Functional .NET build with live logs, cancel, timeout | GUI build of the fixture for osx-arm64 succeeded (21.5 s, 4 artifacts); GUI cancellation mid-build terminated the process tree and recorded *Cancelled* |
| Packaging + verification + checksums | End-to-end test builds the fixture for linux-x64, osx-arm64 and win-x64: ELF, Mach-O arm64 (signed ad-hoc) and PE x64 verified, `.app` Info.plist/icon/permissions checked, SHA-256 recorded |
| Build history, artifacts, export | History/Artifacts pages show real records; repeat build, view log, delete, export with overwrite confirmation |
| Automated tests | `dotnet test`: **154 passed, 0 failed, 0 skipped** (includes real `dotnet` builds) |
| HCG Executable Maker itself publishes for macOS & Windows | `dotnet publish -r osx-arm64` / `-r win-x64 --self-contained` succeed with native SQLite/Skia/HarfBuzz libraries |

## Implemented but NOT verified on a real target

- `DmgPackager` (needs macOS + `hdiutil`): offered only on Mac hosts with hdiutil detected.
- macOS/Windows toolchain probes (Xcode, codesign, notarytool, Windows SDK, signtool, vswhere/MSBuild, WiX, Inno Setup).
- Running HCG Executable Maker on macOS and Windows desktops; launching produced `.app`/`.exe` files.

## Not implemented (by design labelled *Planned* / *Not implemented* in the UI)

- Windows installer `.exe`, `.msi`, `.msix` (M0.2) · Windows `.exe` icon embedding when cross-building (needs Windows host)
- macOS `.pkg`, Mac App Store packaging (M0.3/M0.4)
- Authenticode, Developer ID signing, notarization, Store signing (M0.4)
- Linux AppImage/DEB/RPM, Android, non-.NET build pipelines (M0.5+)
- Isolated/sandboxed build workers (the `IBuildWorker` abstraction exists; only local execution is implemented)
- Official HCG logo (placeholder in use — asset needed) and application window icon
- HCG Classic theme is a token-level *preview* (palette only)

## Known limitations

- Secret scrubbing is name-based (see `SECURITY.md`).
- Projects needing authenticated NuGet feeds will fail to restore (credentials are intentionally not forwarded).
- Inspection is static: properties computed by MSBuild logic (custom targets, imported `.props` outside the workspace)
  are reported as not statically evaluable; the build itself remains the source of truth.
- Folder-type artifacts' checksum is a SHA-256 over a sorted per-file manifest (saved next to the artifact), not over
  an archive.

## Continuation instructions

```bash
git checkout feature/m0.1-foundation
dotnet build HCG.ExecutableMaker.slnx && dotnet test     # expect 154 passing
dotnet run --project src/HCG.ExecutableMaker.App
```

Suggested next steps, in order:

1. **Founder review on real hardware:** run the app on a Mac and a Windows PC; build `tests/Fixtures/HelloAvalonia`
   for each, launch the outputs manually, and try `.dmg` creation on the Mac.
2. **Provide the official HCG logo** (`Assets/Branding/hcg-logo.png`) and approve an app icon.
3. **M0.2 Windows packaging:** add `InnoSetupPackager` (`IPackager`, uses detected `ISCC.exe`) and a WiX MSI packager;
   flip their `PackagingCatalog` entries to `Available` when the tool is detected; add installer metadata
   (publisher, version, shortcuts, uninstall) from `AppIdentity`; add Windows-host CI.
4. **M0.3 macOS:** universal (arm64+x64) bundles via `lipo`, `.pkg` via `productbuild`, Mac-host CI for `DmgPackager`.
5. **M0.4 signing:** credentials from OS keychain/certificate store only, per-operation founder approval, never inside
   untrusted builds; notarization with `notarytool` + stapling; release manifests.
6. Generalize `BuildService` into `IBuildPipeline` per ecosystem before adding non-.NET toolchains (M0.5).

Nothing has been pushed to GitHub. Local commits are on branch `feature/m0.1-foundation`.
