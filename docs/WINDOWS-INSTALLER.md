# Windows installer (Setup.exe) — Milestone 0.2

HCG Executable Maker turns a self-contained Windows build into a standard **Setup.exe** using
[Inno Setup 6](https://jrsoftware.org/isinfo.php), a maintained, widely used installer framework.

## What the installer does

| Feature | How |
|---|---|
| Application name, publisher, version | From *Configure* (`AppName`, `AppPublisher`, `AppVersion`; numeric file version derived when possible) |
| Application icon | The icon chosen in *Configure* is converted to `.ico` and used for the setup wizard; the uninstall entry uses the app's own `.exe` icon |
| Installation directory | `{autopf}\<folder>` — `%LOCALAPPDATA%\Programs\<folder>` for **current user** (default, no administrator rights) or `Program Files\<folder>` for **all users** (administrator rights). The user can change it in the wizard |
| Start-menu shortcut | Always created |
| Desktop shortcut | Optional checkbox in the setup wizard (can be offered checked/unchecked by default, or not offered) |
| Launch after install | Optional checkbox on the final page |
| Uninstall | Registered in *Settings → Apps* (and *Programs and Features*); removes the files, both shortcuts, the registration and the folder (if the app left nothing behind) |
| Upgrades | A stable `AppId` is derived from the application identifier, so a newer version replaces the installed one |
| Bundled files | Every file of the self-contained publish output, recursively |
| Verification | PE format, payload size, Inno Setup data block, Authenticode presence, SHA-256 in `SHA256SUMS.txt` |
| Logs | The ISCC compiler output is part of the build log; compile errors fail the build with the reason. The generated `.iss` script is kept next to the installer for review |

The installer is **unsigned** (Windows SmartScreen: *More info → Run anyway*). Authenticode signing is planned for M0.4.

## Requirements and availability

The *Windows installer (.exe)* option in the New Build wizard is enabled only when **all** of these hold:

1. Target platform is **Windows** (x64 or ARM64).
2. **Self-contained** is on (otherwise the installed app could not start on PCs without .NET).
3. The build host is **Windows** — Inno Setup's compiler `ISCC.exe` runs only on Windows.
4. **Inno Setup 6** is installed (detected in `Program Files (x86)\Inno Setup 6`, `Program Files\Inno Setup 6`,
   `%LOCALAPPDATA%\Programs\Inno Setup 6` or on `PATH`). HCG never installs it for you.

Otherwise the option stays disabled with the exact reason ("runs only on Windows", "Inno Setup 6 was not detected",
"Enable Self-contained").

## Building installers from a Mac or Linux machine

HCG does not simulate Windows tooling. Two supported routes:

- **A Windows PC** (or VM) with HCG Executable Maker and Inno Setup 6 — build normally.
- **The Windows CI runner**: `.github/workflows/build.yml` installs Inno Setup on `windows-latest`. The
  `windows-installer` job builds HCG's own Setup.exe on every push and tests it; the `package` job (main, `v*` tags,
  manual runs) adds `*-win-x64-Setup.exe` / `*-win-arm64-Setup.exe` to the packages. To build another project there,
  run `tools/self-package/self-package.cs` or the app's engine the same way on a Windows runner.

## How it is tested

| Test | Where |
|---|---|
| Script generation, escaping/injection resistance, stable AppId, versions, architectures, options | `InnoSetupScriptTests` (all hosts) |
| Availability rules (host, tool, self-contained), compiler error handling, verification | `WindowsInstallerPackagingTests`, `WindowsInstallerProjectConditionTests` |
| **Real install → launch → uninstall** of the HelloAvalonia fixture's Setup.exe | `WindowsInstallerEndToEndTests` — Windows + Inno Setup + `HCG_INSTALLER_TESTS=1` (set in CI) |
| **Real install → launch → uninstall** of HCG Executable Maker's own Setup.exe | `tools/installer-test/Test-Installer.ps1` in the `windows-installer` CI job |

Both real tests install for the current user, check every published file, the uninstaller, the Start-menu and desktop
shortcuts (and their targets), and the uninstall registration (name, version, publisher); launch the installed app and
confirm it keeps running; uninstall silently; and confirm the folder, shortcuts and registration are gone.

Run the script yourself on a test PC:

```powershell
./tools/installer-test/Test-Installer.ps1 -Setup .\MyApp-1.0.0-win-x64-Setup.exe -AppName "My App" -ExeName MyApp.exe -Version 1.0.0
```

## Security notes

- The generated script contains no `[Code]` section; every project/user value is escaped (`{` → `{{`, `"` → `""`,
  control characters removed), so a hostile app name cannot inject directives or Pascal code.
- ISCC runs with structured arguments and the same secret-scrubbed environment as builds.
- Per-user installation is the default; all-users installs require the user to approve elevation in the setup itself.

## Next: MSI (M0.2b)

MSI generation with the **WiX Toolset** (v5+, `wix build`) is a separate, subsequent milestone: an `IPackager` for
`PackagingFormat.WindowsMsi`, harvested file components, per-machine scope, upgrade code derived like the AppId, and
the same install/uninstall verification on the Windows runner. Until then the MSI option is shown as *Planned*.
