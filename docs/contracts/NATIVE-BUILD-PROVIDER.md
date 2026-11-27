# HCG native build-provider contract (v1)

Status: **implemented by two providers.**

- **HCG Executable Maker's local Xcode provider**, with provider kind `executable-maker-local`.
- **HCG Server's Build Host**, with provider kind `hcg-server-build-host`. It uses the contract `hcg.buildhost/1`
  (HCG-Server commit 417a9cd). Executable Maker's `HcgServerBuildHost` writes these files from each job's verified,
  signed provenance. See [../HCG-SERVER-BUILD-HOST.md](../HCG-SERVER-BUILD-HOST.md).

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

- `provider`: `kind` is `executable-maker-local` or `hcg-server-build-host`. It also carries the host label and
  `dependsOnGitHubActions: false`. The Build Host adds:
  - `contract` (`hcg.buildhost/1`);
  - `endpoint`;
  - `job`;
  - `keyFingerprint`: the HCG Server device key that signed the provenance.
- `approval`: who approved and when, plus `trustAcknowledged` (the person confirmed they trust this source). `scope`
  states what the approval covers: build, test and package. It never covers signing with a new identity,
  notarization, upload or publication. Each of those is a separate, explicitly approved operation.
  - For the Build Host, the approval is the HCG Server owner's: `approvedBy` and `approvedAt` come from the signed
    provenance, and `basis` names the signing key.
  - The request is therefore written once the job has ended and its provenance has verified. A rejected job has no
    request.
- `source`: repository, commit, branch, dirty flag and the basis for them. These come from Git (read only, with no
  credentials) or from an archive comment. They are empty when unknown, never guessed. For the Build Host they come
  from the signed provenance: the pinned commit it checked out and verified, and its tree.
- `project`: kind, container, scheme, configuration, architecture and app target.
  - The local provider writes `xcode-project` or `xcode-workspace`.
  - The Build Host writes its provider as `xcode-project`, `xcode-workspace`, `swift-package`, `dotnet-project`,
    `go-module`, `node-package` or `python-project`. It adds `buildHostProject` and `buildHostProvider`, and puts the
    products or targets in `appTarget`.
- `actions`: an ordered subset of `discover`, `resolve-packages`, `build`, `test`, `archive`, `package`. The Build
  Host's `resolve` is written as `resolve-packages`.
- `signing.mode` is one of:
  - `project`: Xcode signs as the project defines;
  - `ad-hoc-local`: chosen in HCG for this Mac;
  - `unsigned`: the Build Host, which never signs.
- `outputs`: the packaging formats requested.
- `limits`:
  - the timeout;
  - `isolatedWorkspace: true` (a copy of the source in a per-build folder);
  - network use (Swift package resolution only);
  - CPU and memory;
  - `projectScriptsRun: false`.

  The local provider states that CPU and memory are not limited. The Build Host states its own limits:
  - per-command timeouts and a memory limit on the whole process tree;
  - `projectScriptsRun: true`: repository code runs, but inside the sandbox;
  - `isolation`: the backend, rules and gaps from the signed provenance.

  It omits `timeoutMinutes`, which the client does not set. `timeoutMinutes` is therefore no longer required.

## Result: what happened

- `status`: `succeeded`, `failed`, `cancelled` or `timed-out`. It is never `succeeded` when any step failed.
- `request`: the SHA-256 of the request file, which binds the result to what was approved.
- `steps`: each step's title, command, exit code and duration.
- `diagnostics`: error, warning and test-failure counts, and the failed targets. The full report is in
  `xcode-diagnostics.json`.
- `artifacts`: file, SHA-256, size, kind and passport file. A failed build lists none.

- The Build Host's result adds `buildHost`:
  - `job` and `state`;
  - `keyFingerprint`;
  - the files `buildhost-provenance.json` (the signed envelope as received) and `buildhost-validation.json`;
  - `validationOverall` and `tests`.

  `toolchain` lists the Build Host's toolchains. A distributable output (not a log or test report) gets a Release
  Passport build half with `build.provider = "hcg-server-build-host"` and `run_id = null`.

## HCG Server Build Host: what exists and what is still proposed

HCG Server's Build Host (`hcg.buildhost/1`) implements points 2, 3, 4 and 5 below for builds on the owner's own
computer:

- the owner approves each job on HCG Server, and Maker never approves;
- pinned commits from owner-approved remotes;
- the Seatbelt or Landlock sandbox with limits;
- private artifacts with SHA-256 and signed provenance;
- no signing.

Intake runs over the loopback client API with a build-client token. Approval binds to the job ID, not yet to the
SHA-256 of the request. The remote variant (a host registered with HCG Server over the network, a clean macOS VM per
build) is still a proposal.

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

Version 1 was widened without changing any meaning when the Build Host was added:

- more `project.kind` values;
- `signing.mode` `unsigned`;
- `limits.timeoutMinutes` optional;
- `limits.isolation`.

Readers accept extra fields. A reader refuses a `formatVersion` newer than it knows and says why. Changing the meaning
of an existing field needs a new version.
