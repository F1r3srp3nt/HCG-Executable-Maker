# Build Readiness Center

Before anything compiles, HCG Executable Maker checks every tool the build needs. The check runs on
**Toolchains › Build Readiness** (any imported project) and in the **New Build** wizard (when **Start Build** is pressed).
It is also available from the command line as `--build-readiness`.

## Where requirements come from

Requirements are read from the project's files only. Nothing in the project is run to find them.

| Source | Example |
|---|---|
| Packaging contract (`AppPackaging.json`) | `"requires": ["go >= 1.25"]` in a prebuild step |
| Scripts the contract runs | `command -v lipo` in `scripts/build-agent-helper.sh` (listed as optional) |
| Universal helpers | `"architectures": ["arm64", "x86_64"]` → `lipo` required |
| `go.mod` | `go 1.25.0` raises the minimum; `toolchain go1.25.4` is shown as a note |
| `Package.swift` | `// swift-tools-version:5.9` |
| `package.json` | `engines.node` |
| `Cargo.toml` / `pyproject.toml` / `CMakeLists.txt` / `global.json` | `rust-version`, `requires-python`, `cmake_minimum_required`, `sdk.version` |
| The build chosen in HCG | Swift → swift, Xcode project → Xcode, Electron → Node.js + npm, C/C++ → CMake or make, .NET → .NET SDK |

The catalogue covers Go, Swift, Xcode, the .NET SDK, Node.js, npm, Python, Rust and Cargo, Java (JDK), CMake, Ninja,
Clang, GCC, make, lipo, Git, Docker and pkg-config. The Android SDK and NDK are checked in Mobile Build Studio. A tool a
project declares that is not in the catalogue is looked up on PATH and probed with `--version`. No install action is
offered for it.

## How a tool is found

Tools are looked for in this order:
1. The location chosen in the project's **toolchain profile**.
2. The location chosen **for all projects**.
3. PATH.
4. Where installers put it: Homebrew on Apple Silicon (`/opt/homebrew`) and Intel (`/usr/local`, `/usr/local/opt`),
   the official Go installer (`/usr/local/go`), `~/sdk/go*`, rustup, nvm, Volta, pyenv, asdf, mise, MacPorts, Docker
   Desktop and the JDK installers.

Apps opened from Finder do not inherit the shell's PATH, which is why the install locations are searched as well.

- **Versions:** each version is read with a harmless probe outside the project. Go is probed with `GOTOOLCHAIN=local`,
  so the probe never downloads a toolchain.
- **Several installations:** the first one that meets the project's minimum is used. An older installation that
  another project relies on is left as it is.
- **Locations you choose** are used as chosen. If the location no longer works, the build stops rather than silently
  using something else.
- **Apple's `/usr/bin` shims** (swift, clang, lipo, git, python3, make) are never run on a Mac without the developer
  tools, because running one would open Apple's install dialog.

## When something is missing

Each row explains:
- what is missing;
- which file requires it;
- the required version;
- every installation that was found.

Each row offers these actions:
- **Install…** runs only after you approve the exact command. Only two kinds of command can run:
  - a Homebrew formula from HCG's catalogue (`/opt/homebrew/bin/brew install go`), run as an argument list with
    `HOMEBREW_NO_INSTALL_UPGRADE=1` and `HOMEBREW_NO_INSTALLED_DEPENDENTS_CHECK=1`;
  - Apple's `xcode-select --install`.

  HCG never installs Homebrew, never runs installer scripts and never upgrades a formula that is already installed
  (other projects may use it).
- **Choose for this project… / Choose for all projects…** pick the executable or its folder. The location is
  verified (right tool, it runs, new enough) before it is saved. Your PATH and shell profiles are not changed.
- **Official download**, **Copy command** and **Use automatic detection**.
- **Refresh Toolchains** detects everything again.

After a fix, press **Start Build** again: the wizard keeps the configuration, so nothing needs to be imported again.

## Toolchain profiles

A profile is a named, reusable set of tool locations. Choosing a location "for this project" creates the project's
profile automatically. Profiles can be shared by several projects, so two projects can use different Go, Node.js or
SDK versions side by side. A profile only points at installations; it never installs or replaces one.

## Remote build hosts

When a required tool is missing here, Build Readiness lists any authorized remote build host that reports a
compatible version. These are the registered `IBuildHost` implementations other than this computer; the HCG Server
Build Host implements the provider-neutral native build-provider contract.

The host is only listed. Nothing is sent: the HCG Server Build Host checks out the repository commit its owner
approved, and every job needs the owner's approval on HCG Server. Until the Build Host integration is merged, no remote
host is registered and none is shown.

## HCG Server

HCG Server's `macos/HCGServerApp/AppPackaging.json` builds the host agent (Go) as a universal helper:
- `requires: ["go >= 1.25"]`;
- `go.mod` declares `go 1.25.0` and `toolchain go1.25.4`;
- the helper must contain arm64 and x86_64, so `lipo` is required.

CI checks the same contract shape with `tests/Fixtures/AgentHelperApp` (HCG Server is private) in the
`build-readiness-go` job. The job:
1. runs the readiness check with no Go reachable: the build is blocked, and the official download and Homebrew are
   offered;
2. chooses Go for all projects through the CLI;
3. presses Start Build in the wizard, which stops before compiling;
4. chooses Go for the project (its toolchain profile);
5. builds the universal app;
6. checks the helper: both slices (`lipo`), `hcg-agent version` and `codesign`;
7. launches the app and checks that it ran its bundled agent;
8. installs Go with Homebrew, first refused without `--approve`, then installed with it, and checks that it is
   discovered in `/opt/homebrew`.

## Command line

```
HCGExecutableMaker --build-readiness <project folder> [--json report.json]
    [--tool-path go=/usr/local/go/bin/go] [--clear-tool-path go] [--install go [--approve]]
```

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Ready |
| 5 | Something required is missing |
| 4 | `--install` needs `--approve` (the command is printed, not run) |
| 6 | Install failed or the location was rejected |
| 2 | Usage |
