# Porting Studio — source-based cross-platform ports

Porting Studio converts **source code** to another platform, within explicitly supported languages and frameworks.
It is not a universal converter, and it never converts a compiled app or relabels a file.

| Path | Supported in this version | Output |
|---|---|---|
| **macOS → iOS** | SwiftUI apps in Swift: a Swift package executable target, or a macOS Xcode project with a single app target | A new iPhone/iPad Xcode project (iOS 17+) |
| **Linux → Android** | C/C++ command-line programs built with CMake, using the standard libraries and POSIX APIs available in Android's bionic libc | An Android Gradle project (API 24+, arm64-v8a + x86_64), built with the NDK, with a console screen |

Everything else is reported as **not supported**, with the reason. That includes:
- Objective-C/AppKit storyboards and multi-target Xcode projects;
- Makefile/Autotools/Meson-only projects;
- Rust, Go, Python, .NET, Qt, GTK and SDL apps.

## Four capabilities, kept distinct

For every source found in a project, the assessment says exactly one of:

- **Build existing target**: the project already has an iOS or Android target. Nothing is generated; build it in
  Mobile Build Studio.
- **Can generate**: HCG can create a new target, and every change is a *supported adaptation*.
- **Blocked**: the source is in scope, but specific code needs manual porting. Each blocker is listed with its file,
  line, evidence and guidance.
- **Not supported**: outside the path's scope.

An option appearing in the UI never means it works. The capability comes from the assessment of the actual source.

## Assessment

The assessment is read-only: nothing is executed (`Package.swift` and CMake files are only read). It reports:

- **Reusable code**, per target/library: files and lines, how many carry over unchanged, how many are adapted, and
  how many contain blockers.
- **Platform APIs**, **UI adaptations**, **lifecycle**, **dependencies**, **build system**, **file system** and
  **permissions** findings, each marked *Compatible*, *Adapted*, *Review* or *Blocker*.
- **Required toolchains**, checked against this computer:
  - iOS: Xcode and the iOS SDK.
  - Android: JDK 17, Android SDK, NDK, SDK CMake and adb.
- **Signing requirements**: Simulator builds and debug APKs need nothing; devices and stores need your own team or
  keystore. HCG never creates certificates or keys.
- The **scope** of the path.

Conditional compilation is evaluated, so already-guarded code is not reported:
- **Swift:** `#if os(macOS)` and `canImport(AppKit)` are false for iOS.
- **C:** `__linux__` is true on Android; `__GLIBC__`, `_WIN32` and `__APPLE__` are false.

### macOS → iOS adaptations (the complete list)

| Construct | Adaptation |
|---|---|
| `import AppKit` / `import Cocoa` | Made conditional (`#if canImport(AppKit)`), plus `import UIKit` where an adaptation needs it |
| `NSPasteboard.general.clearContents()` + `setString(x, forType: .string)` | Kept for macOS; `UIPasteboard.general.string = x` added for iOS |
| `Settings { … }`, `Window(…) { … }`, `MenuBarExtra` scenes | Kept for macOS only. **Review:** their content is not reachable on iOS until you present it |
| Window modifiers (`.windowStyle`, `.windowResizability`, `.defaultSize`, …) | Kept for macOS only |
| App delegates that only set the activation policy / quit after the last window | Kept for macOS only (SwiftPM GUI shim; iOS apps are always foreground) |
| `Package.swift` without an iOS platform | `.iOS(.v17)` added |
| Info.plist privacy keys (microphone, camera, …) | Your text is written to the iOS target |

Every other AppKit API is a **blocker** with guidance. That includes panels, alerts, `NSView`/`NSWindow`,
`NSWorkspace`, `HSplitView`, `Process`, macOS-only frameworks, XIBs/storyboards and Objective-C.

Adaptations add the iOS code *next to* the macOS code behind platform guards, so the copy still builds for macOS.
Behaviour differences are review items. For example, recording audio on iOS needs an `AVAudioSession`.

### Linux → Android: how the generated app works

- The original project is copied unchanged into `program/`. `android/app/src/main/cpp/CMakeLists.txt` includes it with
  `add_subdirectory` and compiles the executable target's sources, include directories, definitions and libraries into
  `libhcgport.so`.
- **Console host (UI adaptation).** The program's own `main()` runs unchanged in-process:
  - standard output and error appear on screen and in logcat (tag `HCGPort`);
  - an input field feeds standard input;
  - the command-line arguments are editable;
  - `exit()` is intercepted with `-Wl,--wrap=exit`, so the app shows the exit code instead of closing;
  - `HOME`/`TMPDIR` point to the app's own folders;
  - ANSI colour codes are stripped.

  The program runs once per app launch, because its global state is not reset.
- `pthread`/`rt` link libraries are dropped (they are part of bionic), and `find_package(Threads)` is repeated.
- **Blockers:**
  - GUI toolkits: GTK, Qt, X11, SDL, curses, desktop OpenGL;
  - desktop services: D-Bus, ALSA, PulseAudio, systemd;
  - System V IPC and pthread cancellation;
  - glibc-only headers and `backtrace`;
  - third-party native libraries that need an Android build, and other `find_package`/`pkg-config` dependencies.
- **Review items:**
  - absolute paths such as `/tmp` and `/etc`;
  - `system()`/`popen()`;
  - user-database lookups;
  - terminal control;
  - terminal signals;
  - APIs that need API level 28+.

The generated build pins the detected NDK and CMake versions, so Gradle does not download SDK components. HCG does
not install them either.

## Generation: a separate copy, a reviewable plan and diff

- The port is written to `<output folder>/<project>-ios-port-<time>` or `…-android-port-<time>`. Output folders inside
  (or around) the original project or HCG's workspace copy are refused.
- `HCG-PORT/PLAN.md` holds the migration plan, assessment, verification table, signing notes and scope.
  `HCG-PORT/changes.diff` holds the unified diff of every added or modified file. `HCG-PORT/report.json` is the
  machine-readable report.
- In the app, the plan lists every added and modified file. Selecting one shows its diff (Myers algorithm, 3 lines
  of context).
- **Blocked sources** are not generated unless you ask for an explicit **draft**. In a draft, every blocker site
  gets a compile-time error marker for the target platform (`#error("HCG port blocker: …")`), so the draft cannot be
  mistaken for a working port. Unsupported functionality is never replaced with stubs.

## Verification — reported separately

| Level | How | Notes |
|---|---|---|
| Generated | The output exists; files added/modified/unchanged | |
| Compiled | The port is imported and built by **Mobile Build Studio's engine**: iOS Simulator app (`xcodebuild`), Android debug APK (Gradle + NDK). Logs, history and artifact verification are the usual ones | iOS needs a Mac. Drafts are never compiled |
| Launched in simulator/emulator | iOS: `simctl` boot → install → launch, then checks the app is still running and takes a screenshot. Android: `adb install`, `am start --ez hcg.autorun true`, checks the process and the program's logcat output (exit code), takes a screenshot | Needs trust confirmation; runs the app |
| Tested on a physical device | Android only: a connected **physical** device gets the same automated smoke run, recorded as such | Functional testing stays manual. iOS device testing requires your team/profile and is manual. HCG never claims device testing it did not perform |

Publishing, TestFlight/Play upload and store submission are never performed.

## Verified in CI

The `port-ios` and `port-android` jobs in `.github/workflows/build.yml` run the real Porting Studio page through
`tools/ui-driver` (Avalonia headless):

- **TallyPackage** (a SwiftPM SwiftUI app in Distil/Relay's layout) and **HelloMac** (a macOS Xcode project): both
  first build as Mac apps, then are assessed, generated, compiled for the iOS Simulator and launched in an iPhone
  simulator, with screenshots.
- **Oslo-Sprint Relay** is blocked by `NSSavePanel` at `AppModel.swift:469` (1 blocker; 6 supported adaptations).
  Its draft fails to compile for iOS only on HCG's marker. **Distil** is blocked by 24 macOS-only API sites (NSOpenPanel,
  NSAlert, HSplitView, NSWindow…).
- **LinuxPrimes** (C11, CMake, pthreads, getopt, stdin, `exit()`): built and run natively, then ported, compiled with the
  NDK and launched on an Android emulator; the program's output and exit code are read back from logcat.
- **LinuxGtkHello** is blocked by GTK and the `gtk+-3.0` pkg-config module.

See `docs/STATUS.md` for the run that produced this evidence.
