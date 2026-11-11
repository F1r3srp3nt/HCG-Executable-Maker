# Desktop → mobile conversion workspace

The conversion workspace is part of **Porting Studio** (sidebar ▸ Porting Studio). It is not a separate app: it uses
HCG's project import, inspectors, porters and Mobile Build Studio's build engine.

```
Import source project → Inspect compatibility → Choose target → Review conversion plan → Generate → Validate → Build / export
```

Each step reports on its own:
- A successful inspection is not a conversion.
- A generated project is not a verified mobile build.
- A build is not a device test.

HCG never publishes anything, never uploads to TestFlight and never submits to an app store.

## 1. Inspect compatibility (read-only)

Project files and source are read as text. Nothing runs. The compatibility report lists the following.

**Sources:** framework, language, folder, entry point and evidence. Convertible sources come from the porters. These
frameworks are recognized and explained, but not converted:
- Electron;
- other web frameworks;
- server-rendered web apps;
- Node.js servers and command-line tools;
- WinForms/WPF, Avalonia and .NET console apps;
- Python, Go, Rust and Java.

**Features**, each in exactly one class, with a plain-language explanation and the file and line:

| Class | Meaning |
|---|---|
| Reusable | Carries over unchanged (code, assets, routing, build). |
| Requires adaptation | Changed by HCG, shown in the diff (for example the viewport, touch and safe-area styles, platform guards), or changed by hand (for example sign-in callbacks, tokens in web storage, file pickers, notifications, permissions). |
| Requires a backend or companion computer | Works only with a server or a computer: requests to the app's own server, localhost URLs, dev-server proxies, server frameworks, desktop workloads. The calls are kept. **The server side is never generated or faked.** |
| Unsupported by the current converter | No supported adaptation, for example AppKit panels, GTK, Electron APIs, Web Bluetooth/USB/Serial, or a secret compiled into the app. **Never stubbed or silently removed.** Nothing is generated while one exists. |

**Options per source and target** (iOS / iPadOS and Android): the conversion mode, whether it is implemented, planned or
not planned, and the exact requirements of what is missing. This includes tools missing on this computer.

**An executable alone is refused.** If an import contains only compiled programs (.exe, .app, ELF or Mach-O files and
so on), the report says so and offers nothing. A binary lacks the source code, project files and resources a mobile app
is built from.

## 2. Support matrix

From `src/HCG.ExecutableMaker.Porting/ConversionCatalog.cs`, shown in full on the page (Support matrix):

| Source | Target | Mode | Status |
|---|---|---|---|
| SwiftUI app (Swift package or macOS Xcode project) | iOS / iPadOS | Mobile adaptation | **Implemented** |
| SwiftUI app (Swift package or macOS Xcode project) | Android | Mobile adaptation | Not planned |
| AppKit / Objective-C macOS app | iOS / iPadOS | Mobile adaptation | Not planned |
| AppKit / Objective-C macOS app | Android | Mobile adaptation | Not planned |
| C/C++ program (CMake, POSIX) | Android | Mobile adaptation | **Implemented** |
| C/C++ program (CMake, POSIX) | iOS / iPadOS | Mobile adaptation | Planned |
| Web app (Vite + React) | Android | Web-based mobile packaging | **Implemented** |
| Web app (Vite + React) | iOS / iPadOS | Web-based mobile packaging | **Implemented** |
| Web app (Vue, Svelte, Angular, other Vite apps) | Android | Web-based mobile packaging | Planned |
| Web app (Vue, Svelte, Angular, other Vite apps) | iOS / iPadOS | Web-based mobile packaging | Planned |
| Server-rendered web app (Next.js, Nuxt, Remix, SvelteKit, Astro) | Android | Mobile companion | Planned |
| Server-rendered web app (Next.js, Nuxt, Remix, SvelteKit, Astro) | iOS / iPadOS | Mobile companion | Planned |
| Electron desktop app | Android | Web-based mobile packaging | Planned |
| Electron desktop app | iOS / iPadOS | Web-based mobile packaging | Planned |
| Node.js server or command-line tool | Android | Mobile companion | Planned |
| Node.js server or command-line tool | iOS / iPadOS | Mobile companion | Planned |
| HCG Server (desktop agent and workloads) | Android | Mobile companion | Planned |
| HCG Server (desktop agent and workloads) | iOS / iPadOS | Mobile companion | Planned |
| .NET Avalonia desktop app | Android | Mobile adaptation | Planned |
| .NET Avalonia desktop app | iOS / iPadOS | Mobile adaptation | Planned |
| .NET Windows desktop app (WinForms, WPF) | Android | Mobile adaptation | Not planned |
| .NET Windows desktop app (WinForms, WPF) | iOS / iPadOS | Mobile adaptation | Not planned |
| .NET console or server app | Android | Mobile companion | Planned |
| .NET console or server app | iOS / iPadOS | Mobile companion | Planned |
| C/C++ desktop app (GTK, Qt, SDL) | Android | Mobile adaptation | Not planned |
| C/C++ desktop app (GTK, Qt, SDL) | iOS / iPadOS | Mobile adaptation | Not planned |
| Python application | Android | Mobile companion | Planned |
| Python application | iOS / iPadOS | Mobile companion | Planned |
| Go or Rust application | Android | Mobile companion | Planned |
| Go or Rust application | iOS / iPadOS | Mobile companion | Planned |
| Java desktop application (Swing, JavaFX) | Android | Mobile adaptation | Not planned |
| Java desktop application (Swing, JavaFX) | iOS / iPadOS | Mobile adaptation | Not planned |

Planned rows are visible in the app with their precise requirements, and nothing is generated for them.

### Companion mode and HCG Server (planned)

Companion mode is for server tools and desktop-only workloads. The phone gets an interface, and the computer keeps
running the work. HCG Server is the intended first use: phones manage connected computers and open permitted files,
while the desktop agents keep running the workloads. It is **not implemented**. It needs:
- a documented, versioned HTTP API on the desktop service (OpenAPI). The interface is generated from that contract, and no endpoints are invented;
- pairing through the signed-in HCG account (HCG Connect), never a shared password or an embedded key;
- authorization enforced on the desktop service and by HCG's central API. The app keeps only the user's session, in the OS secure store;
- HTTPS through an HCG relay or the local network, with the computer's identity verified;
- no server secrets in the mobile app;
- for HCG Server: the agent API (computers, workloads, file listing and transfer with per-path permissions), with file access limited to folders the owner shares.

## 3. Conversion modes implemented

| Path | Mode | Output | Since |
|---|---|---|---|
| macOS SwiftUI → iOS / iPadOS | Mobile adaptation | New Xcode project (iOS 17+) | Porting Studio (see [PORTING.md](PORTING.md)) |
| Linux C/C++ (CMake) → Android | Mobile adaptation | Android Gradle project, NDK console host | Porting Studio |
| **Vite + React → Android** | Web-based mobile packaging | Capacitor 8.5.3 Android project | This workspace |
| **Vite + React → iOS / iPadOS** | Web-based mobile packaging | Capacitor 8.5.3 iOS project (Swift Package Manager) | This workspace |

### Web-based mobile packaging (Vite + React → Capacitor)

**Generate** is pure: it copies files and writes new ones, and runs nothing. It writes a separate folder,
`<output>/<project>-android-mobile-<time>` or `…-ios-mobile-<time>`, and the original is never modified:
- a copy of the project, without `node_modules`, the build output, VCS data or `.env` files with values;
- `package.json`: `@capacitor/core`, `@capacitor/<platform>` and `@capacitor/cli` pinned exactly, plus scripts `mobile:build` and `mobile:sync`;
- `capacitor.config.json`: app id, name and `webDir`. **There is no `server.url`**, so the app always uses its packaged files;
- `index.html`: a phone viewport with `viewport-fit=cover`, and a link to `public/hcg-mobile.css`;
- `public/hcg-mobile.css`: safe-area padding, 44-point touch targets on touch screens, no tap delay, no text inflation, a visible focus outline. Review it with your design.

**Identity and secrets:**
- Build variables whose names suggest a secret (`SECRET`, `SERVICE_ROLE`, `PRIVATE_KEY`, `PASSWORD`, `ADMIN_KEY`) are **blockers**, because every `VITE_` value is readable inside the app.
- Browser sign-in redirects are listed with the manual registration they need. The identity provider registers a mobile client; HCG's own sign-in needs a separate public mobile client from HCG. The web client is never reused.
- Tokens in web storage are flagged for the Keychain/Keystore.

**Validate:**
- Without trust: checks that the original project and HCG's imported copy are unchanged (SHA-256 fingerprints), that every planned file exists, the configuration, the pinned versions, and that no env files were copied.
- After you confirm trust, it also:
  - runs `npm install`;
  - runs the project's own web build, and checks that it is a production build (no Vite dev-server references);
  - runs Capacitor's CLI (`cap add android|ios`), which creates the native project from Capacitor's template and copies the build into it;
  - checks the result.

**Compile:**
- Mobile Build Studio's engine builds the project through the new **Capacitor** framework: `npm ci` → web build → `cap sync`, then Gradle (debug APK) or `xcodebuild` (Simulator app).
- Mobile Build Studio also recognizes imported Capacitor apps.
- iOS needs a Mac.

**Launch:**
- Android: `adb install`, start `<appId>/.MainActivity`, check the process is still running and did not crash, take a screenshot, and read the visible text through `uiautomator` (WebView content included). Optionally a given text must appear.
- iOS: Simulator boot, install, launch and screenshot.
- A physical device is recorded as a device smoke test only.

## 4. Migration report and reproducibility

Every generated project has `HCG-PORT/`:
- `MIGRATION.md` lists:
  - the status of each level;
  - every added and modified file;
  - the features by class;
  - the **unresolved** items (blockers, backend/companion, adapt by hand);
  - the **manual steps**: backend deployment and CORS, identity-provider registration, secure storage, permissions, icons, signing and store steps.
- `conversion.json` records:
  - the source revision: the Git commit and branch, read from `.git` as files, and a SHA-256 over the converted files and over the original folder;
  - the HCG version, path, mode and target;
  - the app name, id, version and draft flag;
  - the pinned versions.

  The same inputs regenerate the same plan.
- `PLAN.md`, `changes.diff`, `report.json`, `validate.log` and `launch.log` complete the set.

## 5. What has been verified

| Check | Where | Result |
|---|---|---|
| Compatibility detection: Vite + React, backend/sign-in/storage, a server secret, executable-only, Electron, Node server, WinForms, C/CMake; matrix integrity | Unit tests (`MobileConversionTests`) | Pass |
| Generation for Android and iOS: separate copy, source untouched, pinned versions, no `server.url`, viewport and styles, diff | Unit tests | Pass |
| Migration report, conversion record, Git revision, fingerprint changes on edit | Unit tests | Pass |
| Validation: without trust (nothing runs), with trust (scripted npm/CLI), failures (build fails, old Node, edited source) | Unit tests | Pass |
| **Real conversion of `tests/Fixtures/WebViteDesktop`** (Linux, Node 24): real npm install, Vite build, `cap add android` → a Capacitor Android project with `com.example.fieldnotes.MainActivity` and the build in its assets | Opt-in test `MobileConversionRealTests`, and the real Porting Studio page through the UI driver | **Validated: Passed** |
| Compile on that computer | Same run | **Not possible here**: no Android SDK (reported as such; HCG installs no SDKs) |
| Android debug APK + emulator launch showing "Field Notes" | CI job `port-web-android` | Prepared; runs when the branch is pushed |
| iOS Simulator build + launch | CI job `port-ios`, step "Web app … → iOS with Capacitor" | Prepared; runs when the branch is pushed |
| Physical devices, TestFlight, stores | — | Not performed (manual by design) |

## 6. Setup and running it yourself

**Requirements by step:**

| Step | Needs |
|---|---|
| Inspect, generate | Nothing beyond HCG |
| Validate (web) | Node.js 22+ with npm (Capacitor 8), network access to the npm registry |
| Compile Android | JDK 21 and the Android SDK (platform 36). Android Studio installs both; choose them in Toolchains if they are elsewhere |
| Compile iOS | A Mac with Xcode 16+ and the iOS platform (Xcode ▸ Settings ▸ Components) |
| Launch | adb plus an emulator (Android Studio ▸ Device Manager) or a USB-debugging device; an iOS Simulator |

**In the app:**
1. Porting Studio ▸ Import Folder. The report appears.
2. In "Choose a target", press **Choose** on an implemented option.
3. Review the plan inputs, fill in name and app id, choose an output folder, then **Generate port**.
4. Review the plan and diff.
5. Confirm trust ▸ **Validate** ▸ **Compile** ▸ **Launch in simulator / emulator**.
6. Read `HCG-PORT/MIGRATION.md` for the manual steps.

**Headless (the same page, through the UI driver):**

```bash
dotnet build tools/ui-driver -c Release
dotnet tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll --source tests/Fixtures/WebViteDesktop --port "web:" --path WebToAndroid \
  --name "Field Notes" --bundle-id com.example.fieldnotes --validate --expect-validate Passed --compile [--launch --expect-text "Field Notes"] \
  --output /tmp/fieldnotes --screenshots /tmp/fieldnotes/shots
```

**Opt-in real test:**

```bash
HCG_MOBILE_CONVERSION_OUT=/tmp/hcg-mobile dotnet test tests/HCG.ExecutableMaker.Tests --filter MobileConversionRealTests
```
