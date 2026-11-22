# HCG Executable Maker in Xcode (macOS, Apple Silicon)

Open `HCGExecutableMaker.xcodeproj`, choose the **HCG Executable Maker** scheme, press **Run** (⌘R): Xcode builds the
real HCG Executable Maker — the .NET / Avalonia application with Dashboard, New Build, Mobile Build Studio (Build
Doctor, Unleashify), Porting Studio, Projects, Build History, Artifacts, Ship to Store, Toolchains, Signing & Security,
AI assistance, Settings and HCG Connect — as `HCG Executable Maker.app` and starts it.

Nothing is rewritten or simulated. Xcode cannot compile C#/Avalonia, so the project wraps the repository's own build:

| Target | Kind | What it does |
|---|---|---|
| **HCG Executable Maker (.NET)** | External Build System | Runs `Scripts/build-app.sh`, which runs `tools/self-package/self-package.cs` — the same pipeline CI uses for releases: import → inspection → `dotnet restore/build/publish -r osx-arm64 --self-contained` → `.app` bundle (Info.plist, .NET runtime, resources, `AppIcon.icns`) → verification → SHA-256. The script then validates the bundle and copies it to Xcode's build products folder. |
| **Run HCG Executable Maker** | macOS command-line tool (Swift, `Launcher/main.swift`) | Depends on the target above. When Xcode runs it, it replaces itself (`execv`) with `HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker`, so the process Xcode runs **is** the real app: its output is in Xcode's console and **Stop** quits it. |

## Requirements

| | |
|---|---|
| Mac | Apple Silicon (arm64). The app is built for `osx-arm64`. |
| macOS | 13 Ventura or later to build (the built app's minimum is macOS 11). |
| Xcode | 16 or later (the project format is Xcode 14-compatible). CI builds it with Xcode 26.3. Only the command-line tools part is used for the launcher; no Apple Developer account is needed for Debug. |
| .NET SDK | **.NET 10 SDK** for Apple Silicon (`global.json`: 10.0.100, roll forward to the latest 10.0 feature band), installed from https://dotnet.microsoft.com/download/dotnet/10.0 to `/usr/local/share/dotnet`. No .NET workloads are needed for the desktop app. |
| Network | The first build restores NuGet packages (nuget.org). |

The build script finds `dotnet` in `/usr/local/share/dotnet`, `~/.dotnet`, Homebrew or `$DOTNET_ROOT`, because Xcode
does not use your shell's `PATH`.

## Open, build, run

1. Clone the repository and open **`macOS Xcode Project/HCGExecutableMaker.xcodeproj`** (double-click, or
   `open "macOS Xcode Project/HCGExecutableMaker.xcodeproj"`).
2. In the toolbar's scheme menu choose **HCG Executable Maker** and the destination **My Mac**.
   (**HCG Executable Maker (Release)** runs the optimized Release build.)
3. **Build** (⌘B). The first build takes a few minutes (NuGet restore, publish, packaging); the Report navigator
   (⌘9) shows each step. A build with no source changes since the last one is skipped ("up to date").
4. **Run** (⌘R). HCG Executable Maker opens. Its log lines appear in Xcode's console; **Stop** (⌘.) quits it.

Command line equivalent:

```bash
xcodebuild -project "macOS Xcode Project/HCGExecutableMaker.xcodeproj" -scheme "HCG Executable Maker" -configuration Debug build
"$(xcodebuild -project "macOS Xcode Project/HCGExecutableMaker.xcodeproj" -scheme "HCG Executable Maker" -showBuildSettings | awk '/ BUILT_PRODUCTS_DIR =/{print $3}')/RunHCGExecutableMaker"
```

## Where the app is

`HCG Executable Maker.app` is in Xcode's build products folder:
`~/Library/Developer/Xcode/DerivedData/HCGExecutableMaker-<id>/Build/Products/<Debug|Release>/`
(**Product › Show Build Folder in Finder**). Next to it:

- `HCGExecutableMaker-build-logs/<Configuration>.log` — the full build log;
- Release only: `HCG-Executable-Maker-<version>-osx-arm64.app.zip` and its `.sha256`.

The build folder must be Xcode's default Derived Data location (outside the repository), because the build imports the
repository; the script stops with an explanation otherwise.

You can copy the `.app` to `/Applications`. It is ad hoc signed (not notarized), and a copy built on your own Mac is
not quarantined, so it opens without Gatekeeper prompts.

## Debug and Release

| | Debug (scheme **HCG Executable Maker**) | Release (scheme **HCG Executable Maker (Release)**) |
|---|---|---|
| .NET configuration | `Debug` (symbols, no optimization, developer logging) | `Release` (optimized) |
| Output | the `.app` | the `.app`, `…-osx-arm64.app.zip` and its SHA-256 |
| Signing | the .NET SDK's ad hoc signature (runs on this Mac) | ad hoc, or your Developer ID when `HCG_CODESIGN_IDENTITY` is set (see below) |
| Publishing | none | none — no GitHub release, HCG Store, HCG Repo, TestFlight or App Store upload, and no Git tag |

**Version:** always the `<Version>` in the repository's `Directory.Build.props` (0.4.0 on `main`). The Xcode project
does not contain a second copy; the build checks that the app's `CFBundleShortVersionString` and `--version` match it.
The **bundle identifier** is `com.hakmicapital.executablemaker`, the identifier the app's packaging has always used.

**Optional Release signing:** sign with a Developer ID Application identity from your keychain by setting the
environment variable for Xcode's build, e.g. from Terminal:

```bash
HCG_CODESIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
  xcodebuild -project "macOS Xcode Project/HCGExecutableMaker.xcodeproj" -scheme "HCG Executable Maker (Release)" -configuration Release build
```

The script signs with the hardened runtime and verifies the signature. Notarization is a separate, manual step (Maker's
Signing & Security page, or `xcrun notarytool`); nothing is notarized or uploaded automatically.

## When something fails

Errors appear in Xcode's Issue navigator (⌘5) with the cause; the full log is in the Report navigator and in
`HCGExecutableMaker-build-logs/<Configuration>.log`.

| Message | Cause and fix |
|---|---|
| `.NET SDK not found` | Install the .NET 10 SDK for Apple Silicon (link above), then build again. |
| `does not satisfy global.json` | An older SDK only. Install the .NET 10 SDK; the message lists the installed SDKs. |
| `build folder … is inside the repository` | Xcode › Settings › Locations › Derived Data: **Default**, and File › Project Settings › Derived Data: **Default Location**. |
| `The .NET build or packaging failed` | The first `ERROR:` line above it is the compiler or restore error (e.g. no network for NuGet). |
| `Bundle validation: …` | The produced bundle is incomplete (Info.plist, identifier, version, executable, arm64, .NET runtime, icon, signature or start-up check). The line says which. |
| `… was not found. Build the "HCG Executable Maker" scheme first` (when running) | The .NET target did not produce the app; build and read the first error. |
| The app starts and quits | Its output is in Xcode's console. Also run `"…/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker" --version` in Terminal: it prints the version, commit and HCG Connect registration without opening a window. |
| Resources or icon missing | The validation step checks `AppIcon.icns`; Avalonia's own assets are compiled into the assemblies. A missing-resource exception is printed in the console with the resource name. |

Logs never contain HCG sign-in tokens or other secrets: the build runs with Maker's scrubbed build environment, and the
app redacts authentication data from its logs and diagnostic exports.

## Debugging the C# code

Xcode's debugger (LLDB) debugs native code and would stop on the signals the .NET runtime uses internally, so the
schemes run the app without it. To debug HCG Executable Maker's C# code:

- **Visual Studio Code** (C# Dev Kit) or **JetBrains Rider**: open the repository folder, start
  `src/HCG.ExecutableMaker.App` (`dotnet run --project src/HCG.ExecutableMaker.App`) with the debugger, or attach to
  the running `HCGExecutableMaker` process started from Xcode (Debug configuration: symbols are included).
- Unit tests: `dotnet test tests/HCG.ExecutableMaker.Tests`.
- The headless UI driver (`tools/ui-driver`) drives the real views without a window, as CI does.

## Verification

The `xcode-project` CI job (`.github/workflows/build.yml`) on an Apple Silicon macOS runner: `xcodebuild -list`, a
Debug build and a Release build of the scheme with `xcodebuild`, the bundle checks above, the Release checksum, and
running the **Run HCG Executable Maker** product exactly as Xcode's Run does, with a screenshot of the window that opens.
Static checks of the project file and schemes run on every platform (`XcodeProjectFileTests`).
