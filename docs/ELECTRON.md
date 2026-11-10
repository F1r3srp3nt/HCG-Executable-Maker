# Electron desktop apps

HCG Executable Maker builds Electron apps that are packaged with **electron-builder**. It follows the project's own
packaging script and configuration. It does not invent a configuration for a project that has none.

## What the inspection decides (static, nothing is run)

Inspection reads `package.json`, lockfiles and electron-builder data files as text. It never runs npm, a project script
or a JavaScript configuration file. Every `package.json` in the workspace is listed, including nested ones such as
`apps/studio/`. Copies inside build output (`dist/`, `release/`, `out/`, `node_modules/`, …) are ignored.

| Kind | Recognized by | Desktop build |
|---|---|---|
| **Electron desktop app** | `electron` in (dev)dependencies | Yes, when nothing below is missing |
| **Web app (browser only)** | Vite, Next.js, Angular, Vue, Svelte, webpack, … or `index.html` | No: HCG lists what is missing |
| **Node.js service / CLI** | Express, Fastify, Koa, NestJS, …, a `bin` entry or a `start` script | No: it has no window |
| **Library** | none of the above | No |

**An Electron app still needs:**
- **A main entry.** `package.json` `"main"` (or electron-builder `extraMetadata.main`) must point to the main process.
  If the file is produced by a compile step, as with `desktop-dist/main.mjs` in Presentation Studio, inspection accepts
  it. The build then checks that the compile step really produced it.
- **A supported packager: electron-builder.**
  - Electron Forge and `@electron/packager` are recognized. HCG explains that it runs only electron-builder in this
    version.
  - A project without a packager gets the exact list of what to add.
- **A configuration HCG can follow.** It is read from, in this order:
  1. `--config FILE` in the packaging script;
  2. `electron-builder.{yml,yaml,json,json5,toml}` or `electron-builder.config.*`;
  3. `package.json` `"build"`.

  JavaScript configs are recognized but not run during inspection. electron-builder runs them during the build.
- **npm.** yarn, pnpm and bun projects are explained as not supported yet. Running another package manager would
  ignore the project's lockfile. Without `package-lock.json`, HCG uses `npm install` and notes that versions may
  differ.
- **A Node.js that satisfies `engines.node`,** and an npm that satisfies `engines.npm` and the `packageManager` pin.

For a **web-only** project, such as the original Presentation Studio, HCG lists each gap:
- no Electron dependency;
- no main-process entry;
- no electron-builder configuration;
- the app runs from a development server;
- a separate backend (Express) that would have to start inside the main process.

## Browser apps: "Prepare desktop app" (Vite + React)

A browser app is not packaged as it is. The project is labelled a web app, and its missing desktop parts are listed as **project configuration**, separately from tools missing on this computer. For a supported project the wizard offers **Prepare desktop app**:

1. **Analysis (static, nothing runs).** It reads `package.json`, `vite.config.*`, `index.html` and the app's source folder (`src/`, else `app/` or `lib/`; tests excluded).
   - **Supported:** Vite + React with npm and a script that runs `vite build`.
   - **Refused with the exact reason:**
     - SSR frameworks (Next.js, Nuxt, Remix, SvelteKit, Astro, Angular SSR, Gatsby) and non-React Vite apps;
     - pnpm, Yarn or Bun projects; npm workspaces; `file:`, `link:` or `workspace:` dependencies;
     - an existing Electron app (use the normal Electron path);
     - an existing `hcg-desktop/`, `electron-builder.*` or `desktop:*` script;
     - no `vite build` script; an absolute `http(s)` base; an invalid app id or name;
     - relative `/api` calls when the project also contains a server framework.
   - **Warnings (backend and sign-in, shown with file and line):**
     - relative `/api` calls, a dev-server proxy, `localhost` URLs and service workers;
     - redirect sign-in (Supabase, Firebase, Auth0, OIDC, MSAL, Clerk, NextAuth, Amplify);
     - real `.env` files (never copied).

   Arbitrary websites are not claimed to work: a backend the app calls must be reachable from the desktop app, and redirect sign-in needs a desktop redirect URI registered with that provider.
2. **Preview.** Every file is shown before anything is written:
   - `hcg-desktop/main.cjs` (the Electron entry);
   - `hcg-desktop/verify-build.cjs` (fails the build if `dist/index.html` is missing or still loads the Vite dev server);
   - `electron-builder.yml` (arm64 `.app`, zip and dmg, ad-hoc signed; Linux app folder);
   - `package.json` as a diff: `main`; scripts `desktop:build`, `desktop:dist:mac` and `desktop:dist:linux`; `electron` 45.2.3 and `electron-builder` 26.18.0; runtime dependencies moved to devDependencies, because Vite bundles them, so the app ships no `node_modules`;
   - `package-lock.json`.

   The app name and app id are editable. A change requires an updated preview before anything is written.
3. **Create desktop workspace.** This writes a **separate copy**; the imported project is never modified. The copy leaves out `node_modules`, the build output, `release/`, VCS data, symlinks and `.env` files with values. It then runs `npm install --package-lock-only --ignore-scripts` with the inspected Node.js and the scrubbed build environment, so no project or package script runs. The workspace is imported as a new HCG project and builds through the existing Electron stages: `npm ci` → `npm run desktop:build` → electron-builder.

**The packaged window:**
- It serves the production build from a privileged `app://bundle` scheme: no web server, no dev server, no network for the app's own files.
- Asset paths follow Vite's `base`. Paths outside `dist/` are refused.
- Any extension-less path is answered with `index.html`, so `BrowserRouter` deep links and reloads work.
- `contextIsolation`, `sandbox` and `webSecurity` are on, and `nodeIntegration` is off.
- Permission requests are denied. `window.open` and navigation to other sites go to the user's browser (https and mailto only), and `<webview>` is refused.

**Verified:**
- Unit tests, including refusal cases.
- **web-hcg-store** (F1r3srp3nt/store), locally on Linux: wizard → workspace → `npm ci`, `desktop:build`, electron-builder. The packaged app was launched with Playwright and **no dev server**: home and `/apps` after a hard reload rendered, the security preferences were checked, and traversal probes returned no files.
- The fixture `tests/Fixtures/WebViteDesktop` through the real wizard (UI driver `--prepare-desktop`), plus `tools/electron-launch-check/launch-check.cjs`.
- **CI job `web-desktop-macos`:** the fixture as an arm64 `.app`, zip and dmg. It checks codesign, lipo, hdiutil and asar contents, launches the app with no dev server (Playwright, plus LaunchServices `open`), and takes a window screenshot.

## Packaging scripts

HCG splits scripts that call electron-builder at `&&`. For example:

```
"desktop:dist:mac": "node scripts/build-desktop.mjs && electron-builder --mac --arm64"
```

becomes a **compile step** and the electron-builder flags:
- The compile step is `npm run desktop:build`, because another script runs exactly that command.
- Otherwise, the compile step is `npm run X` or `node FILE ARGS`.

HCG never builds a shell string:
- A compile command with pipes, environment assignments or other programs is refused. HCG tells you to move it into
  its own npm script.
- Commands after electron-builder are refused.

For each platform, HCG follows:
1. the script whose electron-builder call names that platform (`--mac`);
2. otherwise, a script with no platform flag (`desktop:pack` on Linux).

## Build stages

Each stage is shown separately and streams its output:

| Stage | Command (argument list, run with the inspected Node.js) |
|---|---|
| Install dependencies | `npm ci --no-audit --no-fund` (no lockfile: `npm install`) |
| Compile | the script's compile steps, e.g. `npm run desktop:build` |
| Package | `node node_modules/electron-builder/cli.js --mac zip dmg --arm64 --publish never --config.directories.output=<HCG build folder>` |

How the stages behave:
- **Before Package,** HCG checks that electron-builder is installed and that the main entry exists. A failing check
  names the step that should have produced the file.
- **Failures:**
  - A failing command fails the build with its exit code.
  - npm `error`, electron-builder `⨯` and `tsc` errors are listed as build messages.
- **Cancel** stops the running process tree.
- **Environment:**
  - The build gets HCG's scrubbed environment: no tokens or `CSC_*` secrets, and no `GITHUB_*`/`GH_TOKEN`.
  - The inspected Node.js is first on PATH, so a Finder launch builds with the same Node as Terminal.
- **Publishing** is always disabled (`--publish never`).
- **Output** goes into HCG's own build folder. The project's `release/` or `dist/` folder is never written.

## Artifacts and verification

HCG looks for the output where electron-builder puts it:
- `mac-arm64/<productName>.app` (`mac/` for x64);
- the zip and the dmg;
- `linux-unpacked/<executable>`.

If something is missing, the build fails and says what the output folder contained.

| Artifact | Checks |
|---|---|
| `.app` | Info.plist and `CFBundleExecutable`, Electron Framework, executable bit, Mach-O architecture, **app.asar contains package.json and its main entry**, `codesign --verify --deep --strict` (ad hoc reported as such) |
| zip | archive integrity, contains `<App>.app/Contents/Info.plist` and the executable |
| dmg | `hdiutil verify` |
| Linux folder | ELF architecture, executable bit, app.asar contents |

How signing is handled:
- **On the Mac:** electron-builder signs as `mac.identity` says (`"-"` = ad hoc), and HCG only verifies the result. HCG's
  own Developer ID and notarization options are not offered for Electron builds.
- **On Apple Silicon:** an unsigned app fails verification, because the system will not run it.

## Offered combinations

| Host | Targets | Formats |
|---|---|---|
| macOS | macOS arm64, macOS x64 | `.app`, zip, dmg |
| Linux | Linux (host architecture) | unpacked app folder |
| Windows | none in this version | (explained) |

Other electron-builder targets are listed as not run by HCG:
- pkg, mas;
- AppImage, deb, rpm;
- Linux zip.

## Verification with Presentation Studio

Presentation Studio was built from Oslo-Sprint commit `49b4016`, after its desktop conversion.

**Linux (this development container):**
- The wizard was driven headlessly by `tools/ui-driver --electron`: import → inspect → configure → package → build.
- Stages run: `npm ci`, then `npm run desktop:build`, then `electron-builder --linux dir --x64`.
- Verification passed: ELF x64 and app.asar with `desktop-dist/main.mjs`.
- The project's own packaged-app test passed **15/15** against the app HCG built:
  `STUDIO_APP=<HCG artifact> xvfb-run -a npm run test:desktop`.
  It covers launch with no development server, the native dialogs, `.pptx` export checked with python-pptx and
  LibreOffice, a second launch, and quitting with no processes left.
- The web-only version (the-last-sprint `presentation-studio/`) is reported as a browser app. Its five missing items
  are listed, and Configure is blocked.

**macOS arm64:** the `electron-macos` CI job runs the same wizard flow:
1. builds the `.app`, zip and dmg;
2. checks them with `codesign`, `lipo`, `plutil`, `hdiutil` and `unzip`;
3. launches the app through LaunchServices;
4. runs the project's `npm run test:desktop` against the HCG-built app.

This development container has no Mac, so the macOS result comes from that CI job only.

## Running it yourself

```sh
# Real build of an Electron project folder (needs network for npm and Electron downloads):
HCG_ELECTRON_PROJECT=/path/to/presentation-studio dotnet test tests/HCG.ExecutableMaker.Tests --filter ElectronEndToEndTests
# The wizard, headless, with screenshots:
dotnet run --project tools/ui-driver -- --electron --source /path/to/presentation-studio --arch arm64 --formats app,zip,dmg \
  --output out/electron --screenshots out/electron/shots
```
