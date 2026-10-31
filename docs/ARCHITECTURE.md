# Architecture

## Solution layout

```
HCG.ExecutableMaker.slnx
src/
  HCG.ExecutableMaker.Domain             Models only: projects, analysis, builds, artifacts, toolchains, platforms
  HCG.ExecutableMaker.Core               Abstractions + app services: process/paths/repositories/settings,
                                         PathGuard, Checksums, ToolchainService, ProjectService
  HCG.ExecutableMaker.Security           BuildEnvironmentPolicy (secret scrubbing), TrustPolicy, SigningCapabilityCatalog
  HCG.ExecutableMaker.Infrastructure     ProcessRunner, SQLite (migrations + repositories), ProjectImporter,
                                         ToolchainDetector, FileLoggerProvider
  HCG.ExecutableMaker.ProjectInspection  IProjectInspector, SourceIndex, DotNetProjectInspector (+ MSBuild/sln/slnx/
                                         global.json parsers), EcosystemSignatureInspector (recognize-only)
  HCG.ExecutableMaker.Packaging          IPackager, PackagingCatalog, packagers (portable folder/zip, .app, .dmg),
                                         icon engine (SkiaSharp), BinaryInspector, verification checks
  HCG.ExecutableMaker.BuildEngine        DotNetBuildPlanner, MsBuildOutputParser, IBuildWorker, BuildService
  HCG.ExecutableMaker.App                Avalonia 12 UI (MVVM, CommunityToolkit.Mvvm, Microsoft.Extensions.DI)
tests/
  HCG.ExecutableMaker.Tests              xUnit: unit + integration (real dotnet builds)
  Fixtures/HelloAvalonia                 Standalone Avalonia app used by end-to-end tests
```

Dependency direction (no cycles, no UI types below the App):

```
Domain ← Core ← Security ← BuildEngine → Packaging → Core
              ← Infrastructure
              ← ProjectInspection
App → everything (composition root: src/HCG.ExecutableMaker.App/Composition.cs)
```

## Data flow

```
ZIP / folder ──ProjectImporter──▶ workspace/<id>/source  (+ original/<archive>)
                                   │   no code executed; limits & path checks; fails closed
                                   ▼
                     ProjectInspectionService ── picks highest-scoring IProjectInspector
                                   │   static XML/text parsing only
                                   ▼
                     ProjectAnalysis (persisted as JSON on the project row)
                                   │
           user configures BuildSettings (identity, target, packaging) + acknowledges trust
                                   ▼
BuildService:  validate → icons → DotNetBuildPlanner (restore/build/publish ProcessSpecs)
               → IBuildWorker (scrubbed env) → verify app host exists → IPackager(s)
               → SHA-256 + manifests + SHA256SUMS.txt + .hcg-build.json → BuildRecord (SQLite)
```

Builds run in `builds/<buildId>/publish` (HCG-owned staging). Packagers copy the result into
`<output>/<App>-<version>-<rid>/`; only that folder is ever replaced, and only if it carries HCG's marker file.

## Key rules encoded in the code

- **Success means proven success.** `BuildService` marks `Succeeded` only if every command exits 0, the expected
  app host exists in the publish output, and no artifact verification check fails.
- **No shell.** `ProcessRunner` uses `ProcessStartInfo.ArgumentList`; executables must be absolute paths;
  MSBuild property values are escaped (`DotNetBuildPlanner.EscapeMsBuild`).
- **Untrusted builds get an explicit environment.** `LocalProcessBuildWorker` refuses a `ProcessSpec` without one.
- **Honest capability reporting.** `PackagingCatalog` is the single source of truth for which formats exist and why
  one is unavailable (`NotImplemented`, `MissingTool`, `RequiresDifferentHost`, `NotApplicable`).

## Extension points

| To add… | Implement | Register in |
|---|---|---|
| A language/framework inspector (Rust, Swift, …) | `IProjectInspector` (static only) — replace the matching `EcosystemSignatureInspector` | `Composition.cs` |
| A build pipeline for another toolchain | a planner like `DotNetBuildPlanner` + branch in `BuildService` (M0.5 will generalize this into `IBuildPipeline`) | `Composition.cs` |
| A packaging format (MSI, PKG, AppImage…) | `IPackager`; flip its `PackagingCatalog` entry to `Available` when the tool is detected | `Composition.cs` |
| Isolated builds (container/VM/remote) | `IBuildWorker` with `IsolationLevel.Isolated` | `Composition.cs` |
| A toolchain probe | `ToolchainDetector` + an id in `ToolchainIds` | — |
| A schema change | append a `Migration` to `Persistence/Migrations.cs` (never edit released ones) | — |
| A theme | a `Themes/Tokens.<Name>.axaml` with the same keys + entry in `ThemeService.Themes` | — |

## UI structure

- `MainWindow` — sidebar (brand mark, navigation, engine/environment footer) + header + `TransitioningContentControl`.
- Pages are `PageViewModel` singletons (a running build survives navigation); views are resolved by the explicit,
  reflection-free `ViewLocator`.
- Styling uses only `DynamicResource` design tokens (`Hcg.Brush.*`, `Hcg.Radius.*`, …) from
  `Themes/Tokens.HcgFuture.axaml`; `Themes/Controls.axaml` holds control styles; `Themes/Icons.axaml` holds glyphs.
- Views contain no business logic; code-behind is limited to view behaviour (e.g. auto-scrolling the live log).
