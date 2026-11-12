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

## Browser apps: "Create desktop application" (Vite + React)

A browser app is not packaged as it is, and it is not dismissed with "No runnable application project found" either.
Every web app in the workspace, nested folders included, is listed under **Project to build** with what HCG can do
with it. HCG never claims that every React app can become a static desktop app.

### 1. Inspection: static, nothing runs

HCG reads `package.json`, the lockfile, `vite.config.*`, `index.html` and the app's source folder (`src/`, else
`app/` or `lib/`; tests are not scanned). No npm command, project script or JavaScript configuration runs. Each Node.js
project is classified (`DesktopEligibilityRules`):

| Category | When | What HCG does |
| --- | --- | --- |
| **Browser app** | Vite + React (`react` and `react-dom`), npm, a script that runs `vite build` | Offers **Create desktop application** |
| **Electron app without packaging** | Electron, an existing main process, Vite, no electron-builder / Forge / packager | Adds packaging only and keeps the main process. It warns, with file and line, when the main process turns off a renderer protection or loads a dev server |
| **Existing Electron app** | electron-builder (or Forge/packager) configuration, or a desktop project HCG generated | Builds it as it is; nothing is generated |
| **Needs a server** | Next.js, Nuxt, Remix, SvelteKit, Astro, Angular SSR, Gatsby, React Router framework mode, TanStack Start, Vike; or Express, Fastify, Koa, Hono, NestJS, hapi or restify under `dependencies` | Explains why; does not convert |
| **Not supported / ambiguous** | Not Vite + React; pnpm, Yarn or Bun (their lockfile would be ignored); a monorepo root; `file:`, `link:` or `workspace:` dependencies or a sibling monorepo package; no `vite build` script | Gives the exact reason; does not convert |

**npm workspace members:** a project without its own lockfile is pinned by the nearest ancestor lockfile. With npm's
`package-lock.json`, the desktop project's lockfile starts from the monorepo's pinned versions: hoisted packages, plus
the member's own nested ones, minus the other members and workspace links. pnpm or Yarn monorepos are refused.

**Analysis rows:** "Recommended outputs" are listed only when something in the workspace can produce them. For a
convertible app they read "… — after Create desktop application". The support card ("HCG cannot build this project")
is hidden while a conversion is available.

### 2. The Create desktop application panel

The panel holds the selected web project and its category, and these choices:
- **Product name and application identifier.** Defaults come from `index.html` and `package.json`; the version comes
  from `package.json`.
- **Icon.** The default is the largest square PNG of at least 512×512 found in the project (`public/`, `build/`,
  `assets/`, …), or an `.icns`. Too small, non-square, ICO and SVG files are refused with the reason. Without an icon,
  the app uses Electron's default icon.
- **Target platform and architecture.** Only what this computer can package is offered: macOS arm64 or x64 on a Mac,
  and the Linux app folder on Linux. Windows packaging of Electron apps is not implemented, so it is not offered. An
  Electron package is never labelled as an iOS or Android app.
- **Package formats:** `.app` (always built), ZIP and DMG on a Mac; the unpacked app folder on Linux.
- **Output folder.**
- **Signing and notarization, as separate operations:**
  - Signing defaults to **ad-hoc**, labelled *not signed for distribution*.
  - A *Developer ID Application* identity found in the keychain can be chosen. electron-builder then signs with the
    hardened runtime and the JIT entitlement only. This path has no CI coverage, because CI has no certificate.
  - Notarization is never run during the build. It is shown as a separate step (`xcrun notarytool submit … --wait`,
    then `xcrun stapler staple`).
- **Public settings.** The `VITE_` values that the project's production `.env` files give the web build (`.env`,
  `.env.local`, `.env.production`, `.env.production.local`) are copied into the desktop build. Names that look like
  credentials (`SECRET`, `TOKEN`, `PASSWORD`, `PRIVATE`, `SERVICE_ROLE`, `CREDENTIAL`, `API_KEY`, `ACCESS_KEY`) are
  never copied, and non-`VITE_` variables (server-side) are never copied. The `.env` files themselves are never copied.

**Findings** carry file and line:
- relative `/api` calls, a dev-server proxy, `localhost` URLs, service workers;
- redirect sign-in (Supabase, Firebase, Auth0, OIDC, MSAL, Clerk, NextAuth, Amplify) and **HCG Connect**, explained
  below;
- the HTTPS hosts the code calls. Remote services stay remote, and HCG bundles no server and no server credentials;
- history routing and lazy chunks, and how each is served.

**Preview:** every file to be added, changed or removed is shown before anything is written. `package.json` is shown
as a diff, and `.env` values are not displayed.

### 3. The generated desktop project: a separate copy

**Create desktop application** plans again with the current options, then writes a **separate desktop project**. The
imported project, and the website's own build, scripts and hosting files, are never modified. The copy leaves out
`node_modules`, the build output, `release/`, VCS data, symlinks and `.env` files.

| File | Purpose |
| --- | --- |
| `hcg-desktop/main.cjs` | Electron main process (wrap mode only; see the loading strategy below) |
| `hcg-desktop/verify-build.cjs` | Runs after the web build. Fails if `dist/index.html` or **any script, stylesheet or icon it references** is missing, or the build loads the Vite dev server |
| `electron-builder.yml` | App id, name, icon, mac targets and architecture, signing identity (`"-"` = ad-hoc), `publish: null`, Linux `dir` |
| `hcg-desktop/icon.png` / `.icns` | The chosen icon |
| `hcg-desktop/entitlements.mac.plist` | Only with Developer ID signing (`com.apple.security.cs.allow-jit`) |
| `.env.production.local` | Only when public `VITE_` settings exist |
| `package.json` | `main`, `desktop:build`, `desktop:dist:mac`, `desktop:dist:linux`, `electron` 45.3.0, `electron-builder` 26.18.0. In wrap mode, runtime dependencies move to devDependencies because Vite bundles them, so the app ships no `node_modules` |
| `package-lock.json` | `npm install --package-lock-only --ignore-scripts`, starting from the project's (or monorepo's) lockfile, so no script runs |
| `hcg-desktop/README.md`, `hcg-desktop/conversion.json` | The record: source project, source fingerprint, options, every generated file with its SHA-256, and the original `package.json` |

Before writing, HCG checks that the chosen Node.js satisfies the project's `engines.node` **and** Electron 45's
`>=22.12.0`. After the lockfile is resolved, it checks the resolved `vite`, `electron` and `electron-builder` engines
against it.

**Converting again never accumulates files:**
- From the original web project, it **updates the desktop project generated earlier**. That project's source is
  swapped (`ProjectService.ReplaceSourceAsync`) and its build history is kept; no second project is added.
- In a folder that already holds HCG's output (for example, committed back into a repository), the recorded files are
  replaced, files no longer needed are removed, and `package.json` is rebuilt from the recorded original. The same
  options produce the same files.
- A generated file **edited** afterwards is never overwritten: HCG stops and names it.

The desktop project is imported (or updated) and opens at the **Build** step with the chosen target, formats and output
folder. After the usual trust confirmation, it builds through the Electron stages, each shown separately:
install dependencies (`npm ci`) → compile the web app (`npm run desktop:build`) → package (electron-builder) → verify
(locate the app; check its structure, architecture, payload and signature; copy) → checksums.

Cancellation stops the running process. An incomplete output folder is removed. Exit codes are recorded, and build log
lines and messages pass through `SecretRedactor`. An ad-hoc build is logged as *UNSIGNED for distribution and NOT
notarized*.

### Loading strategy: the packaged window

- The production build is served from a privileged, standard, secure `app://bundle` scheme: no web server, no dev
  server, no network for the app's own files.
  - Absolute asset paths (`/assets/…`), lazy-loaded chunks and their assets resolve exactly as on the web host.
  - Any extension-less path is answered with `index.html` (the web host's rewrite). `BrowserRouter` navigation, deep
    links and **reloads** therefore work.
  - Vite's `base` is honoured, and paths outside `dist/` are refused.

  `loadFile`/`file://` alone was not used, because history routes break on reload under `file://`.
- `contextIsolation`, `sandbox` and `webSecurity` are on; `nodeIntegration` (also in workers and subframes) is off;
  there is no preload script, because the page needs no native operation.
- HTML responses carry a content security policy: scripts from the app only, `connect-src 'self' https: wss:` so remote
  APIs stay reachable, `object-src 'none'`, and `nosniff`.
- Permission requests and checks are denied. `<webview>` is refused, and a second launch focuses the first window.
- `window.open` and navigation (including redirects) outside the app are refused. Only `https:` and `mailto:` links are
  handed to the user's browser or mail program; every other scheme, `file:` included, is refused.
- If the start page itself fails to load, the window shows an error page. Failed remote requests are logged with their
  host; the page shows its own connection errors.

### HCG Connect (central sign-in) in a desktop app

HCG Store is prepared for HCG Connect: `VITE_HCG_CONNECT_*` settings, a redirect URI on the origin that serves the web
build. A desktop app has no such origin. Desktop sign-in needs a **separate public desktop client registered centrally
by HCG**, with:
- a loopback redirect (`http://127.0.0.1:<port>/…`);
- PKCE S256 and the system browser (how HCG Executable Maker itself signs in; see `docs/hcg-connect/`).

Until HCG registers that client and the app implements the loopback flow, sign-in stays unavailable in the desktop
app. HCG Executable Maker does not register clients, invent a login service or put a client secret in the app.

### HCG Store: importing it and generating its macOS app

1. In HCG Executable Maker on a Mac (Apple silicon or Intel), open **Toolchains** and press **Refresh**. You need
   Node.js **22.12 or later**: HCG Store requires `>=22.12`, and Electron 45 requires `>=22.12.0`.
2. **New Build › Import folder…** and choose the **repository root** of HCG Store (the folder that contains
   `web-hcg-store/`). A ZIP of the repository works too.
3. On **Inspect**, the analysis reads *Web app (browser only) — desktop conversion available*. Under
   **Project to build**, choose `hcg-store-web · Browser app — can become a desktop app` (`web-hcg-store/package.json`).
4. In **Create desktop application**:
   - Check *HCG Store* and `com.hakmicapitalgroup.hcg-store-web`, or change them.
   - Icon: `public/brand/icon-512.png`, chosen automatically.
   - Target: *macOS · Apple silicon (arm64)* (or Intel).
   - Formats: `.app`, ZIP and DMG; choose the output folder.
   - Signing: Ad-hoc, or your Developer ID Application identity.
   - Review the findings. HCG Connect sign-in is not available in the desktop app yet (see above). The catalogue is
     the local seed catalogue, because no `.env` sets `VITE_CATALOGUE_PROVIDER`.
5. Press **Create desktop application**. The desktop project is created, separate from the import, and opens at
   **Build**.
6. Tick the trust confirmation, then **Start Build**. The stages run in order: `npm ci` → `npm run desktop:build`, which
   runs HCG Store's own `npm run build` (catalogue validation, `tsc -b`, `vite build`, the bundle-secrets check) and
   then the asset verification → electron-builder → verify.
7. **Results** lists `HCG Store.app`, the ZIP and the DMG, with their checks and SHA-256. Open the `.app` (it needs no
   Node.js and no dev server). Ad-hoc builds run on the Mac that built them; for other Macs, sign with Developer ID and
   notarize separately.

Converting again later (step 4 onwards from the original import) updates the same desktop project.

**Verified:**
- **Unit tests** (`WebDesktopConversionTests`, `ProjectServiceTests`):
  - nested detection, as in the HCG Store layout;
  - classification of every category;
  - npm workspace lockfile seeding; pnpm and sibling refusal;
  - findings and file/line;
  - credential-like settings never copied;
  - the shell's security settings and CSP;
  - icon, targets and signing written to the configuration; unusable icons refused;
  - idempotent re-conversion; edited files never overwritten;
  - Electron app with packaging untouched; Electron app without packaging keeps its main process;
  - engines checks; redacted npm output;
  - source replacement without a second project.
- **Linux, locally (12 Nov 2026), through the real wizard (UI driver `--prepare-desktop --convert-again`):**
  - The fixture `tests/Fixtures/WebViteDesktop` and the real **HCG Store** (`F1r3srp3nt/store` at `c7270f3`,
    repository root imported, app in `web-hcg-store/`) went through: picker → options → preview → create → Build step →
    `npm ci` → `desktop:build` → electron-builder → verify. Converting again updated the same project (no extra
    project, no leftover files).
  - Both packaged apps were launched with **no dev server**:
    - the page loaded from `app://bundle/`;
    - a deep route survived a reload (`/notes/ridge`, HCG Store `/apps/hcg-mail`);
    - for the fixture, the lazy `/archive/2024` chunk and its image loaded from the app, also after a reload; an
      https link opened outside the app and a `file:` link was refused;
    - the CSP was present, the window security settings were on, no Node.js was in the page, traversal probes failed,
      and no app request failed.
- **macOS:** see the CI job `web-desktop-macos`. It runs the same wizard flow on the fixture and produces an arm64
  `.app`, zip and dmg, which are checked with codesign (ad-hoc), lipo, `hdiutil verify` and `unzip -t`; it also checks
  the icon in the bundle. The app is launched with the checks above and through LaunchServices. HCG Store itself is
  not built in CI: its repository is not available to this repository's CI.

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
