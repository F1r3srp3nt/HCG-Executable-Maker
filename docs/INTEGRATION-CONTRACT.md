# Integration contract `hcg.executable-maker.integration/1`

A versioned, documented way for **another HCG application on the same computer** to use an installed HCG Executable
Maker as its build engine, without reading or modifying Executable Maker's internal files, database or workspaces.
The first client is **HCG Executable Maker Unleashed** (`F1r3srp3nt/HCG-Executable-Maker-Unleashed`).

The contract is a command-line interface of the installed application's own executable:

| Host | Executable |
|---|---|
| macOS | `HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker` |
| Windows | `HCGExecutableMaker.exe` in the installation or portable folder |
| Linux | `HCGExecutableMaker` in the portable folder |

**Discovery marker.** Versions that do not know `--integration` (0.4.0 before this contract) treat it as a normal
start and open their window. Before running anything, a client checks that the contract name
`hcg.executable-maker.integration/1` occurs (UTF-16, as .NET stores string literals) in `HCGExecutableMaker.dll` next
to the executable, or in the executable itself for single-file builds. This read-only check is part of the contract and
stays valid for every version 1 build. Then the client reads `--version`, calls `--integration describe`, and treats
anything that is not a JSON document with `"contract": "hcg.executable-maker.integration/1"` as **contract not
available**.

## `--integration describe`

Prints one JSON document and exits 0. Read-only: it detects toolchains exactly as the Toolchains page does.

| Field | Meaning |
|---|---|
| `contract` | `hcg.executable-maker.integration/1` |
| `product`, `version`, `commit`, `buildIdentity`, `executable` | Which Executable Maker answered |
| `host` | `platform` (`macos`/`windows`/`linux`), `architecture`, `os`, `runtime` |
| `operations` | `describe`, `build` |
| `cancellation` | `signal`, `cancelFile` |
| `buildProviders[]` | `id` (`dotnet-desktop` in version 1), `available`, `toolchain`, `detail` |
| `targets[]` | per platform: `architectures`, `formats[]` with `format` (the `PackagingFormat` name), `availability` (`Available`, `MissingTool`, `RequiresDifferentHost`, `NotImplemented`, `NotApplicable`, `SetupRequired`, …) and `reason` — the same answer `PackagingCatalog` gives the UI |
| `notOfferedThroughThisContract` | Features of Executable Maker that version 1 does not expose (mobile, Swift, Electron, C/C++, porting, Impactor, signing, Ship to Store/Repo) |
| `releasePassport` | The Release Passport format the build half is written in |
| `toolchains[]` | `id`, `name`, `category`, `status`, `version`, `available` |

## `--integration build --request <request.json> [--result <result.json>]`

### Request

```json
{
  "contract": "hcg.executable-maker.integration/1",
  "requestId": "client-chosen id, echoed back",
  "client": { "name": "HCG Executable Maker Unleashed", "version": "0.1.0" },
  "source": { "kind": "directory", "path": "/absolute/folder/with/the/project" },
  "project": "relative/path/App.csproj",
  "target": { "platform": "macos", "architecture": "arm64" },
  "configuration": "Release",
  "identity": { "name": "HCG NoteDesk", "id": "com.hakmicapital.notedesk", "version": "1.0.0", "publisher": "Hakmi Capital Group", "description": "", "icon": null },
  "formats": ["MacAppBundle"],
  "outputDirectory": "/absolute/output/folder",
  "allowOverwriteOutput": false,
  "timeoutMinutes": 30,
  "trust": { "acknowledged": true, "statement": "why the client trusts this source" },
  "cancelFile": "/absolute/path/that/cancels/the/build/when/created",
  "sourceRevision": { "repository": null, "commit": null },
  "releasePassport": true
}
```

- `project` is optional (the inspection's primary project is used).
- `trust.acknowledged` must be `true`. Building runs the project's MSBuild code with the user's permissions, exactly
  as in the UI; the client is responsible for obtaining that acknowledgement from the person.
- The source folder is **imported** (copied into Executable Maker's own workspace with the usual limits and path checks),
  inspected statically and built from the copy. It is never modified.
- Each format must be `Available` for the target on this computer; otherwise the request is refused with the reason.

### Events (stdout, one JSON object per line)

| `event` | Fields |
|---|---|
| `accepted` | `requestId`, `project`, `runtimeIdentifier` |
| `stage` | `stage` (`Preparing`, `Restoring`, `Compiling`, `Publishing`, `VerifyingOutput`, `Packaging`, `Checksumming`, `Completed`, …), `description` |
| `log` | `kind` (`Output`, `Warning`, `Error`, `Info`, …), `text` |
| `result` | see below; always the last line |

### Result (last event, and the `--result` file)

`status` (`Succeeded`, `Failed`, `Cancelled`, `TimedOut`, or `Refused` when nothing was built), `failureReason`,
`buildId`, `builtBy`, `startedAt`, `endedAt`, `runtimeIdentifier`, `project`, `logPath`, `warningCount`, `errorCount`,
`toolchains`, `diagnostics[]` (`severity`, `text`, `code`, `file`, `line`), `artifacts[]` (`format`, `kind`, `path`,
`sizeBytes`, `sha256`, `verification`, `checks[]`) and `releasePassport` (`path`, `sha256`) or `null`.

Exit codes: **0** succeeded · **1** failed, cancelled or timed out · **2** usage · **4** refused (nothing built).

`Succeeded` has the meaning it has everywhere in Executable Maker: every command exited 0, the app host exists and no
artifact verification check failed. It does **not** mean the application was launched or tested: launch tests stay
`NotVerifiable`.

### Release Passport

For a succeeded build, the build half of an HCG Release Passport (`hcg.release-passport` v1, the schema pinned in
[RELEASE-PASSPORT.md](RELEASE-PASSPORT.md)) is written next to the artifacts as `release-passport.json`
(`ReleasePassport.DesktopBuildHalf`). It is validated against the schema and the build-half rules before it is written:
provider `executable-maker-local`, no signing method claimed (the artifact's own code-signature check is quoted), every
device level `not-recorded`, no approvals, channel or publication.

### Cancellation

SIGINT or SIGTERM, or creating the request's `cancelFile`: the build service terminates the build's process tree and
the result is `Cancelled`.

## Compatibility rules

- A client must refuse to use an Executable Maker whose `describe` names another contract.
- Fields may be added within version 1; clients ignore fields they do not know. Removing or changing the meaning of a
  field requires `hcg.executable-maker.integration/2`.
- Version 1 exposes only `.NET` desktop builds. Requests for other project types are refused, never approximated.

## Verification

- `tests/…/DesktopReleasePassportTests.cs`: the desktop build half validates and claims nothing it did not measure.
- `tools/ci/integration-contract-check.sh <HCGExecutableMaker>`: `describe`, a refused request, and a real build of
  `tests/Fixtures/HelloAvalonia` for this host through the contract (artifacts and SHA-256 checked).
