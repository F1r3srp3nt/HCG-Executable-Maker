# HCG Server for Windows in Executable Maker

HCG Server (`F1r3srp3nt/HCG-Server`, main at `d36efba`) declares how its Windows x64 package is built in
`windows/WindowsPackaging.json` (schema `hcg-windows-packaging/1`). Executable Maker follows that contract. It does
not substitute its own installer, naming or passport.

## Build the package

```
HCGExecutableMaker --windows-contract <HCG Server checkout>                         # review only; nothing runs
HCGExecutableMaker --windows-contract <HCG Server checkout> --confirm <sha256> --out <folder>
```

1. **Review.** Executable Maker prints the contract, its SHA-256, the tools it requires and every step exactly as it
   will run (argument lists, never a shell). Nothing runs until you confirm that SHA-256. If the file changes after you
   confirmed it, the run is refused.
2. **Tools.** Build Readiness's resolver finds each declared tool (`dotnet >= 10.0`, `go >= 1.25`, `makensis >= 3.0`,
   `python3 >= 3.9`) and probes its version (Go with `GOTOOLCHAIN=local`, so an older Go that would download a newer
   toolchain counts as the older one). Executable Maker installs nothing; a missing tool stops the run with the reason.
3. **Steps.** They run in a copy of the checkout (the original is not written to), with Executable Maker's build
   environment (secrets removed) and `${STAGE}` set to a fresh folder:
   1. `go build` of `hcg-agent.exe` for windows/amd64;
   2. `dotnet publish` of the app (`HCG Server.exe`, self-contained, single file);
   3. `scripts/package-windows.sh`: the portable ZIP, the NSIS installer, `SHA256SUMS` and a Release Passport build
      half per package, written by HCG Server's own script.
4. **Independent verification** of what the contract produced:
   - every package's SHA-256 against `SHA256SUMS`;
   - each Release Passport: schema, Executable Maker's build-half rules, and that it describes these exact bytes
     (file name, size, SHA-256) and the declared architecture;
   - the portable ZIP holds the installed files (`HCG Server.exe`, `hcg-agent.exe`, `README-WINDOWS.txt`), no path
     leaves the folder, and every executable is PE x64;
   - the installer is a Windows executable (its NSIS bootstrapper is x86; the programs it installs are the ones
     checked in the ZIP).

Nothing is signed (the contract says code signing is not configured), uploaded or published. Ship to Store takes the
files with their passports separately, as a private draft that a person approves.

The plain .NET route (`dotnet publish -r win-x64` of `windows/HCGServerWindows/HCGServerWindows.csproj`) also yields
`HCG Server.exe` with `hcg-agent.exe` next to it, because the project builds the agent (it needs Go). For a release, use
the contract: it is the package HCG Server defines (installer, ZIP, checksums and passports).

## Verified (Linux, 28 November 2026)

HCG Server at `d36efba`, Go 1.25.4, .NET SDK 10.0.114, NSIS 3.09, Python 3.13:

| File | Size | SHA-256 |
|---|---|---|
| `HCG-Server-0.1.0-windows-x64-portable.zip` | 54,804,610 | `c0a763c8c617dffba345d876b0251d1126189951cbeeb7c1bbe8d46c87f21932` |
| `HCG-Server-0.1.0-windows-x64-setup.exe` | 52,757,016 | `63f65ed626609b8d2804e46501384ade1e32fb506039d556cee9432de8eeb864` |

- Both passports validate and state source commit `d36efba8ee8cc0782f8979e3d8b6ffd8db117f19`, not dirty.
- Both executables in the ZIP are PE x64; the installer is a PE x86 bootstrapper; all unsigned.
- Rebuilding gives different hashes (timestamps inside the packages): the hashes above identify that run's files only.

Two defects were found and fixed while doing this:
- Executable Maker's build environment removed `GIT_CONFIG_KEY_n` but kept `GIT_CONFIG_COUNT` and `GIT_CONFIG_VALUE_n`,
  so git refused to run inside builds and the passports had no commit. Git's environment configuration is now removed
  as a whole.
- Build Readiness did not look in Go's own toolchain cache (`~/go/pkg/mod/golang.org/toolchain@*/bin/go`) on Linux or
  Windows (it did on macOS).

**Not verified:** installing, launching or uninstalling on real Windows; the optional Windows service. HCG Server's
own `scripts/windows-smoke.ps1` covers those and has not run on Windows yet either.

## Build Host on Windows

HCG Server for Windows has no isolation backend yet (planned: AppContainer + Job Objects), so its Build Host refuses
every job: it never runs build steps unconfined. Executable Maker shows that reason in the Build Host panel and the
provider check, and does not submit jobs to it. Build locally, or use HCG Server on a Mac (Seatbelt) or Linux (Landlock).
