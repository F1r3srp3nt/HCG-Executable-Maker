# HCG native build-provider contract (v1) — proposal

Status: **implemented by HCG Executable Maker's local Xcode provider. Proposed to HCG Server** (an HCG Server build
host does not exist yet; this is for founder review, not adopted central policy).

This contract describes a native build twice:

- what the build provider was **authorized** to do (`hcg.native-build-request`);
- what it **did** (`hcg.native-build-result`).

Every provider writes the same two files. Ship to Store, Ship to Repo and the Release Passport then read one shape,
whether the build ran on the owner's Mac or on an HCG Server build host. No provider depends on GitHub Actions.

| File | Format | Schema |
|---|---|---|
| `native-build-request.json` | `hcg.native-build-request` v1 | `native-build-request-v1.schema.json` |
| `native-build-result.json` | `hcg.native-build-result` v1 | `native-build-result-v1.schema.json` |
| `xcode-diagnostics.json` | `hcg.build-diagnostics` v1 | `build-diagnostics-v1.schema.json` |
| `<artifact>.release-passport.json` | HCG Release Passport v1, build half | pinned from HCG Repo (`docs/RELEASE-PASSPORT.md`) |

## Request: what was approved

- `provider`: `kind` is `executable-maker-local` now; `hcg-server-build-host` is proposed for HCG Server. It also
  carries the host label and `dependsOnGitHubActions: false`.
- `approval`: who approved and when, plus `trustAcknowledged` (the person confirmed they trust this source). `scope`
  states what the approval covers: build, test and package. It never covers signing with a new identity,
  notarization, upload or publication. Each of those is a separate, explicitly approved operation.
- `source`: repository, commit, branch, dirty flag and the basis for them. These come from Git (read only, with no
  credentials) or from an archive comment. They are empty when unknown, never guessed.
- `project`: kind (project or workspace), container, scheme, configuration, architecture and app target.
- `actions`: an ordered subset of `discover`, `resolve-packages`, `build`, `test`, `archive`, `package`.
- `signing.mode`: `project` (Xcode signs as the project defines) or `ad-hoc-local` (chosen in HCG for this Mac).
- `outputs`: the packaging formats requested.
- `limits`:
  - the timeout;
  - `isolatedWorkspace: true` (a copy of the source in a per-build folder);
  - network use (Swift package resolution only);
  - CPU and memory;
  - `projectScriptsRun: false`.

  The local provider states that CPU and memory are not limited. A build host states its own limits.

## Result: what happened

- `status`: `succeeded`, `failed`, `cancelled` or `timed-out`. It is never `succeeded` when any step failed.
- `request`: the SHA-256 of the request file, which binds the result to what was approved.
- `steps`: each step's title, command, exit code and duration.
- `diagnostics`: error, warning and test-failure counts, and the failed targets. The full report is in
  `xcode-diagnostics.json`.
- `artifacts`: file, SHA-256, size, kind and passport file. A failed build lists none.

## What an HCG Server build host would add (proposal)

These are not built. They need founder approval and HCG Server work.

1. **Intake:** accept a request whose source is a commit of a repository the host may read. Maker sends the request.
   It never sends source code or credentials in the request.
2. **Approval:** run only after the owner approves that exact request (by SHA-256) in HCG Server. Maker never approves
   on the owner's behalf.
3. **Isolation:** one clean macOS workspace per build, destroyed afterwards, with enforced CPU, memory, disk and time
   limits (stated in `limits`). Network only for package resolution, to an allowlist.
4. **Output:** the same result, diagnostics and passport files. In the passport, `build.provider` is
   `hcg-server-build-host` (HCG Store v2's provider id). Artifacts stay private until the owner ships them, and they
   only become private drafts.
5. **No signing or notarization** in the build itself. Those stay explicit, separately approved operations with
   credentials held by the host. They are never sent by Maker.

## Versioning

Readers accept extra fields. A reader refuses a `formatVersion` newer than it knows and says why. Changing the meaning
of an existing field needs a new version.
