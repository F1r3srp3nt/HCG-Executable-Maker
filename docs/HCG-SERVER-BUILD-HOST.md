# HCG Server Build Host provider

HCG Server's Build Host compiles and tests HCG applications on the owner's own computer, inside its isolation sandbox
(Seatbelt on macOS, Landlock on Linux). Executable Maker uses it as one of three interchangeable build providers. The
contract is `hcg.buildhost/1`, documented in HCG Server's `docs/BUILD-HOST.md`.

| Provider (`IBuildProvider.Id`) | Where it runs | Who approves running the project's code | CI run in the evidence |
| --- | --- | --- | --- |
| `executable-maker-local` | this computer, through `IBuildWorker` / `BuildService` (unchanged) | you, with the trust acknowledgement | no |
| `github-actions` | a GitHub Actions run; you record run ID, artifact and run repository (unchanged) | the workflow | yes |
| `hcg-server-build-host` | HCG Server's Build Host (`HcgBuildHostProvider`) | **the owner, on HCG Server** | **no, never** |

The ids are HCG Repo's `build.provider` values (`BuildProviderIds`).

## What Executable Maker does and does not do

- **Submit.** It submits a job for a registered project, a full 40-hex commit and predefined operations
  (`resolve`, `build`, `test`, `package`), with options such as products, targets and configuration.
  - It never asks for `signing` or `device`. The Build Host builds unsigned.
  - Signing stays in Executable Maker, with its own approval (Unleashify signs with ldid).
- **Approval.** Every job starts awaiting approval. Executable Maker shows
  `Waiting for approval on HCG Server (hcg-agent build approve <job> --confirm <job>).` and keeps polling.
  - It has no route to approve, and its build-client token is refused on the admin API.
  - Stopping the build in Executable Maker cancels the job on HCG Server.
- **Follow.** It polls the job (`queued`, `running`, then `succeeded`, `failed`, `cancelled` or `rejected`) and streams
  the redacted log using `X-HCG-Next-Offset`.
- **Download.** Each artifact is written to `builds/<id>/buildhost-artifacts/` only after three checks agree:
  - the job record's SHA-256 and size;
  - the server's `X-HCG-SHA256` header and `Content-Length`;
  - the SHA-256 and size of the bytes actually received.

  Otherwise the partial file is deleted and the build fails.
- **Provenance.** The provenance is verified before it is used:
  - algorithm `ES256-ASN1`;
  - a P-256 key in canonical PKIX DER;
  - `keyFingerprint` equal to the lowercase hex SHA-256 of that DER (HCG Server's `identity.PublicKeyFingerprint`);
  - the ASN.1 ECDSA signature over SHA-256 of the exact payload bytes;
  - the payload's job ID and commit equal to the ones requested;
  - its artifact list equal to the job record's.

  Optionally, `HCG_BUILDHOST_KEY_FINGERPRINT` or `--pin` pins HCG Server's device key. `hcg-agent` prints the
  fingerprint in groups of four; separators are ignored.
- **Validation.** The Build Host's validation report becomes build messages and each artifact's
  "Build Host validation" check. The build succeeds only when all of these hold:
  - the job succeeded;
  - the report is `passed`;
  - the provenance verified;
  - every download verified.

## Mapping into Executable Maker's records

| Build Host | Executable Maker |
| --- | --- |
| job state, first non-zero step exit code, `detail` | `BuildRecord.Status`, `ExitCode`, `FailureReason` |
| `toolchains[]` (job and provenance) | `ToolchainVersions["buildhost:<tool>"]` |
| job ID, approver, device, key fingerprint, isolation | `ToolchainVersions["buildhost-job" / "-approved-by" / "-device" / "-key" / "-isolation"]`, `["build-provider"] = "hcg-server-build-host"` |
| `artifacts[]` {name, size, sha256} | `ArtifactRecord` (`Format = BuildHostOutput`, `SizeBytes`, `Sha256`) with checks Checksum, Signed provenance, Build Host validation, Code signature (unsigned) |
| validation `checks[]` | build messages (`Code = "buildhost"`) |
| tests | an Info or Error message with passed, failed and skipped counts |

## Evidence for HCG Repo

The signed provenance carries a `submission` block. `BuildHostSubmission.FromProvenance` turns it into
`BuildHostEvidence`, and refuses in these cases:

- the job did not succeed;
- the tree was dirty;
- the commit differs from the one requested;
- the provider is not `hcg-server-build-host`;
- the signing method is not `unsigned`.

The evidence fills these fields of HCG Repo's artifact submission (`artifact-submission-v1.schema.json`):

```json
"source": { "repository": "F1r3srp3nt/HCG-LifeOS", "commit": "<40 hex>", "dirty": false },
"build":  { "provider": "hcg-server-build-host", "toolchain": "Swift version 6.0.3 (…), HCG Executable Maker 0.4.0", "builtAt": "2026-11-27T11:47:57Z" }
```

`build` never has `runId`, `runUrl`, `artifactName` or `runRepository`. HCG Repo refuses those for non-GitHub
providers, and Executable Maker never invents a CI run.

In Unleashify, `PackageProvenance.FromBuildHost(evidence)` replaces the CI fields. `Validate()` refuses a provenance
that mixes Build Host evidence with CI fields. The candidate handoff then contains:

- a native `submission.json`. Its `source` and `build` come from the evidence. Package, artifact, compatibility,
  dependencies, maintainer scripts, validation and ldid `signing` are measured from the `.deb`.
- a `build` block in `provenance.json` with the job and key fingerprint.
- no `import-request.toml`. Submit with `bin/hcgrepo release submit --handoff <folder>`. After the owner approves the
  SHA-256, HCG Repo's `release prepare` writes the `local-submission` import request.

GitHub Actions handoffs are unchanged: a CI run with an artifact still produces a pinned `github-artifact` import request.

## Settings and the token

- **Base URL.** The default is `http://127.0.0.1:7600/buildhost/v1`. You can override it with `HCG_BUILDHOST_URL` or
  `--url`. Only loopback hosts are accepted (127.0.0.0/8, ::1, localhost). Requests never use a proxy and never follow
  redirects.
- **Token.** The owner creates the build-client token with `hcg-agent build client add executable-maker`.
  - `HCGExecutableMaker --build-host token set` reads it from standard input and stores it in the OS credential store
    (`ISecureCredentialStore`, account `buildhost-client-token`). It is never written to a configuration file or log.
  - Where no OS store exists (Linux without Secret Service), it is kept for the current run only.
  - `--token-stdin` passes it for one run without storing it.

## Command line

```bash
HCGExecutableMaker --build-host status
```

Prints the contract, isolation, toolchains, providers and registered projects.

```bash
HCGExecutableMaker --build-host run --project lifeos --commit <40-hex> --ops build --targets LifeOSCore --artifact build.log --out report.json
```

Submits the job, waits for the owner's approval, then downloads, verifies and prints the HCG Repo fields. It exits
with 0 only when the build succeeded and the evidence verified.

## Verified

`BuildHostTests` run against a fake Build Host on every platform. They cover:

- version refusal;
- loopback-only URLs;
- a refused token;
- checksum, header and size mismatches, each of which deletes the download;
- a tampered payload, a wrong or unpinned key;
- the waiting-for-approval message, with no approve request;
- the mapping into the build and artifact records;
- no CI fields in the submission.

`RootlessAppDebTests` checks the Build Host `submission.json`.

A live run against a real Build Host (Linux, Landlock) on 2026-11-27 ran as follows:

1. Job `bj-20261127-114716-e922eb6f9e` built LifeOS commit `21326ef…` (`swift build --target LifeOSCore`).
2. It waited for approval, and the owner approved it with `hcg-agent build approve`.
3. It succeeded. `build.log` was downloaded and verified (SHA-256 `ad1a4a21…`, 24,963 bytes).
4. The provenance verified with key `c51552b0…05a4`, and the submission fields above were produced.
