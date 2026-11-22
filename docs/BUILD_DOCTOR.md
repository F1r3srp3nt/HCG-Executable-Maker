# Build Doctor (Mobile Build Studio)

Build Doctor diagnoses why a mobile build cannot run and proposes repairs that keep the application's behaviour
unchanged. It runs automatically whenever a project or output is selected in Mobile Build Studio (it is read-only), and
again with **Run Build Doctor** / **Recheck build readiness** after toolchains or files change.

## What it checks

For .NET for iOS apps it follows the whole project graph (the app, every project it references, transitively):

| Area | Check |
|---|---|
| Import scope | References, imports and files that resolve outside the imported folder (monorepos) |
| Project references | Missing targets; for a broken relative path, whether exactly one candidate exists |
| MSBuild properties | Properties a path depends on that cannot be evaluated, and where each is defined |
| MSBuild imports | Imports whose condition is true but whose file is missing |
| Source files | Explicit Compile/AvaloniaResource/EmbeddedResource/… items that do not exist |
| SDK / target framework | global.json vs installed SDKs (roll-forward rules); TFM vs installed SDK majors |
| Workload | The .NET iOS workload, and the version the project pins (rollback file or toolchain props) |
| Xcode | The Xcode the project is built with (e.g. `HcgRequiredXcode`) vs the selected one |
| Packages | Whether NuGet restore has run; restore on approval surfaces missing packages |
| Runtime identifier / architecture | Fixed RIDs that do not fit the output; Intel Simulator RIDs on Apple silicon |
| Bundle identifier / entitlements | Missing or invalid ApplicationId; missing entitlements file |
| Signing / provisioning | From Mobile Build Studio's readiness (keychain identities, profiles) for device outputs |
| Host | iOS needs macOS: reported once, not per tool |

Each finding has: problem, root cause (where established), affected file, severity, recommended repair, whether the
repair is automatic, requires approval or is manual, and the verification result after a repair.

### How properties are evaluated

Build Doctor reads project files the way MSBuild evaluates them, without running anything: properties in document
order; property and import conditions (`==`, `!=`, `Exists()`, `!`, `And`, `Or`); an undefined property is the empty
string; MSBuild's path functions (`$([MSBuild]::NormalizeDirectory(…))`, `NormalizePath`, `EnsureTrailingSlash`,
`$([System.IO.Path]::Combine(…))`, `GetFullPath`); imports guarded by property conditions. The process environment is
never read: a property MSBuild could take from an environment variable (e.g. `HCG_GOOGLE_CLIENT_SECRET`) is listed as an
optional build input, and its value is never shown. Anything that cannot be decided statically is reported as such.

**Evaluate with MSBuild** (after the trust confirmation) runs `dotnet msbuild -getProperty/-getItem` on every project in
the graph — no targets run — and confirms each reference exists: the authoritative check.

## Repairs

Nothing that changes files, imports, installs or runs project code happens without approval:

1. **Review** shows exactly what will change (a diff, the command, or the folder to import).
2. **Approve and apply**: file edits are backed up first to `<workspace>/doctor/backups/<time>-<repair>/` with a
   `repair.json` manifest, and are refused if the file changed since review; files outside the workspace source are never touched.
3. Build Doctor runs again; each finding the repair addressed is marked **Verified** or **Still present**.
4. **Roll back the last repair** restores the backed-up files.

| Repair | Mode |
|---|---|
| Import the repository folder (monorepo) — imports the repository root as a new project so every relative path resolves; the current import stays | Requires approval |
| Reconnect a broken relative project reference when exactly one target exists | Requires approval |
| Restore NuGet packages (`dotnet restore`, runs restore targets) | Requires approval + trust |
| Install the .NET iOS workload, pinned to the project's version (`dotnet workload install ios --from-rollback-file …`) | Requires approval + trust |
| Select Xcode, configure signing, install an SDK | Manual (HCG never installs Xcode or SDKs, and never changes signing without approval) |

Build Doctor never creates placeholder projects or source files, never downloads binaries of its own, and runs commands
with the scrubbed build environment (no tokens or secrets).

## HCG Mail: root cause (13d93a8)

The iOS head references the shared projects as `$(HcgSharedSource)HCG.Mail.Core/HCG.Mail.Core.csproj`, valid MSBuild.
`HcgSharedSource` = `$(HcgSharedDir)src/`, and `HcgSharedDir` is set by three conditional assignments
(`-p:HcgSharedRoot`, `HCG_SHARED_ROOT`, default `../Windows-executable-Application/`) followed by
`$([MSBuild]::NormalizeDirectory('$(HcgSharedDir)'))`; the shared `Directory.Build.props`/`Directory.Packages.props` are
imported only when `HcgSharedRootFound` is true.

1. **Maker defect (fixed):** Maker's static reader deferred conditional properties to the end of a file, did not
   evaluate property functions and only followed `Exists()` imports, so `HcgSharedDir` never resolved and all four shared
   references were reported missing — even with the whole repository imported. Real MSBuild evaluation of the same files
   resolves them.
2. **Import scope:** importing only `iOS App Store Submission` (folder or ZIP) leaves `../Windows-executable-Application`
   out of the workspace; the build would stop with HCG001. Build Doctor reports each reference with that cause and, for a
   folder import, proposes importing the repository root (it finds the `.git`/`hcg-apps.json` root).
3. **Toolchain pins:** the iOS workload pin moved from `scripts/workload-rollback.json` to `toolchain.props`
   (`HcgIosWorkloadManifest` 26.2.10233, `HcgRequiredXcode` 26.3); Maker now reads it and shows the exact pinned install command.

The projects themselves are present and well-formed in the repository; nothing was missing from HCG Mail.
