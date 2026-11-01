# Status — Milestone 0.2 (Windows installer) — complete on branch, awaiting founder review

Branch `feature/m0.2-windows-installer` (from `main` 0.1.0; not merged). Evidence: CI run #13
(<https://github.com/F1r3srp3nt/HCG-Executable-Maker/actions/runs/41299757852>) — all jobs green.

## Verified on a real Windows machine (GitHub `windows-latest`, Inno Setup 6 installed by CI)

| What | Result |
|---|---|
| `WindowsInstallerEndToEndTests`: HelloAvalonia fixture built for win-x64 → portable ZIP + **Setup.exe** (icon, name, publisher, version) → SHA-256 recorded in `SHA256SUMS.txt` → silent per-user install → every published file + `unins000.exe` present → Start-menu and desktop shortcuts present → uninstall registration (name 1.4.2, publisher, UninstallString) → **installed app launched and kept running** → silent uninstall → folder, both shortcuts and registration removed | **Passed** (1 min 39 s) |
| `windows-installer` job: HCG Executable Maker builds **its own** `HCG-Executable-Maker-0.2.0-win-x64-Setup.exe` with `self-package.cs --installer`, then `Test-Installer.ps1`: install (252 files), shortcuts exist and point to `HCGExecutableMaker.exe`, registration `{8ABCE15B-…}_is1` "HCG Executable Maker 0.2.0 — Hakmi Capital Group", app launched and running after 8 s, uninstaller exit 0, folder/shortcuts/registration removed | **All checks passed** |
| Portable folder / ZIP for Windows (desktop end-to-end test) | Unchanged, passing |
| Full test suite on Windows / macOS / Linux | 179 tests; Windows 169 passed, 10 skipped (Unix-only tests) |

Local (Linux container): 175 passed, 4 skipped (3 integration tests skipped with `HCG_SKIP_INTEGRATION=1`, plus the
Windows-only installer test).

## Not verified

- Clicking through the New Build wizard on a Windows desktop to produce a Setup.exe (CI exercises the same
  `BuildService` + `InnoSetupPackager` path headlessly; the wizard option is disabled on this Linux host, by design).
- All-users (administrator) installation — implemented (`PrivilegesRequired=admin`), not exercised in CI.
- ARM64 installers — produced by the `package` job on main/tags only; not installed on ARM64 hardware.
- Installing on a clean consumer PC (SmartScreen prompt, since the installer is unsigned).

## Not implemented

MSI (WiX) — next milestone M0.2b; MSIX; Authenticode signing (M0.4).

---

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
| Automated tests | `dotnet test`: **162 passed, 0 failed, 0 skipped** (includes real `dotnet` builds) — green in CI on **Linux, macOS 26 (Apple Silicon) and Windows** |
| Real-OS packaging (CI run #5) | HCG Executable Maker packaged itself on GitHub's runners: macOS → `.app` + **`.dmg` (arm64, x64), `hdiutil verify` passed**; Windows → x64/ARM64 ZIPs built on Windows (icon embedded in `.exe`); Linux → x64/ARM64 ZIPs |
| HCG Executable Maker itself publishes for macOS & Windows | `dotnet publish -r osx-arm64` / `-r win-x64 --self-contained` succeed with native SQLite/Skia/HarfBuzz libraries |

## Implemented but NOT verified on a real target

- Running HCG Executable Maker's **GUI** on macOS and Windows desktops (CI exercises the engine, not the UI).
- **Launching** produced `.app`/`.exe` files (never done automatically — it would execute project code).
- macOS/Windows toolchain probes on a desktop (CI runs them only inside the test suite's host-detection test).

## Not implemented (by design labelled *Planned* / *Not implemented* in the UI)

- Windows installer `.exe`, `.msi`, `.msix` (M0.2) · Windows `.exe` icon embedding when cross-building (needs Windows host)
- macOS `.pkg`, Mac App Store packaging (M0.3/M0.4)
- Authenticode, Developer ID signing, notarization, Store signing (M0.4)
- Linux AppImage/DEB/RPM, Android, non-.NET build pipelines (M0.5+)
- Isolated/sandboxed build workers (the `IBuildWorker` abstraction exists; only local execution is implemented)
- **Final** HCG logo: the founder-provided *temporary* official logo is installed (sidebar, About, window/app icon); replace it when the final artwork is approved
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
2. **Final branding:** when the final HCG logo is approved, replace `branding/hcg-logo-temporary-original.png` and rerun
   `tools/branding/make_logo.py` (see `branding/README.md`); approve the derived square app icon.
3. **M0.2 Windows packaging:** add `InnoSetupPackager` (`IPackager`, uses detected `ISCC.exe`) and a WiX MSI packager;
   flip their `PackagingCatalog` entries to `Available` when the tool is detected; add installer metadata
   (publisher, version, shortcuts, uninstall) from `AppIdentity`; add Windows-host CI.
4. **M0.3 macOS:** universal (arm64+x64) bundles via `lipo`, `.pkg` via `productbuild`, Mac-host CI for `DmgPackager`.
5. **M0.4 signing:** credentials from OS keychain/certificate store only, per-operation founder approval, never inside
   untrusted builds; notarization with `notarytool` + stapling; release manifests.
6. Generalize `BuildService` into `IBuildPipeline` per ecosystem before adding non-.NET toolchains (M0.5).

Nothing has been pushed to GitHub. Local commits are on branch `feature/m0.1-foundation`.
