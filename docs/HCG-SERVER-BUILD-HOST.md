# HCG Server Build Host provider

HCG Server's Build Host compiles and tests HCG applications on the owner's own computer, inside its isolation sandbox
(Seatbelt on macOS, Landlock on Linux). The contract is `hcg.buildhost/1`, documented in HCG Server's
`docs/BUILD-HOST.md` (HCG-Server commit 417a9cd).

In the app it is chosen in the New Build wizard: **Build provider › HCG Server Build Host**
([BUILD-PROVIDERS.md](BUILD-PROVIDERS.md)). From the command line it is `--build-host`.

In Executable Maker it is an `IBuildHost` of kind `HcgServerHost` (`HcgServerBuildHost`), next to `LocalBuildHost`
([BUILD-HOSTS.md](BUILD-HOSTS.md)). It is the second provider of the native build-provider contract
([contracts/NATIVE-BUILD-PROVIDER.md](contracts/NATIVE-BUILD-PROVIDER.md)).

| `build.provider` (HCG Repo, `BuildProviderIds`) | Where it runs | Who approves running the project's code | CI run in the evidence |
| --- | --- | --- | --- |
| `executable-maker-local` | this computer (`LocalBuildHost`, `BuildService`; unchanged) | you, with the trust acknowledgement | no |
| `github-actions` | a GitHub Actions run; you record run ID, artifact and run repository (unchanged) | the workflow | yes |
| `hcg-server-build-host` | HCG Server's Build Host (`HcgServerBuildHost`) | **the owner, on HCG Server** | **no, never** |

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
  - its artifact list equal to the job record's;
  - the payload's own `buildHost.keyFingerprint` equal to the signing key's (HCG Server 417a9cd and later; an empty
    one is refused).

  Optionally, `HCG_BUILDHOST_KEY_FINGERPRINT` or `--pin` pins HCG Server's device key. `hcg-agent` prints the
  fingerprint in groups of four; separators are ignored.
- **Validation.** The Build Host's validation report becomes build messages and each artifact's
  "Build Host validation" check. The build succeeds only when all of these hold:
  - the job succeeded;
  - the report is `passed`;
  - the provenance verified;
  - every download verified.

## Files the build writes (`builds/<id>/`)

The same files the local Xcode provider writes:

- `native-build-request.json` (`hcg.native-build-request` v1):
  - provider `hcg-server-build-host`, `dependsOnGitHubActions: false`, the job and the key fingerprint;
  - approval: the HCG Server owner's, from the signed provenance;
  - scope: build, test and package only (no signing, notarization, upload or publication);
  - source: from the provenance;
  - project: kind and targets;
  - actions;
  - signing: `unsigned`;
  - limits and `isolation`: from the provenance.

  It is written only once the provenance has verified: a rejected job has no request.
- `native-build-result.json` (`hcg.native-build-result` v1):
  - status, the SHA-256 of the request, and the steps with argv, exit code and duration;
  - the Build Host's toolchains;
  - the artifacts and their passports;
  - `buildHost` (job, key, validation, tests).
- `buildhost-provenance.json`: the signed envelope exactly as received. `buildhost-validation.json`: the validation
  report.
- `buildhost-artifacts/<name>`: the verified downloads.
  - A distributable output (not a log or test report) also gets `<name>.release-passport.json`, the Release Passport
    build half: `build.provider = "hcg-server-build-host"`, `run_id = null`, unsigned.
  - Ship to Store reads provider, commit and toolchain from it like any other build.

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
`BuildHostEvidence` (Domain), and refuses in these cases:

- the job did not succeed;
- the tree was dirty;
- the commit differs from the one requested;
- the provider is not `hcg-server-build-host`;
- the signing method is not `unsigned`.

Unleashify's `SubmissionManifest` (the same `submission.json` every handoff carries) fills these fields of HCG Repo's
artifact submission from the evidence (`artifact-submission-v1.schema.json`):

```json
"source": { "repository": "F1r3srp3nt/HCG-LifeOS", "commit": "<40 hex>", "dirty": false },
"build":  { "provider": "hcg-server-build-host", "toolchain": "Swift version 6.0.3 (…), HCG Executable Maker 0.4.0", "builtAt": "2026-11-27T11:47:57Z" }
```

`build` never has `runId`, `runUrl`, `artifactName` or `runRepository`. HCG Repo refuses those for non-GitHub
providers, and Executable Maker never invents a CI run.

In Unleashify, `PackageProvenance.FromBuildHost(evidence)` replaces the CI and local-build fields. `Validate()` refuses
a provenance that mixes Build Host evidence with either. The candidate handoff then contains:

- `submission.json`: its `source` and `build` come from the evidence. Everything else is measured from the `.deb`, as
  for every handoff.
- `release-passport.json` with the same provider.
- a `build_host` block in `provenance.json` with the job and key fingerprint.
- no `import-request.toml`. Like a local build, it goes through **Ship to Repo** (`release submit`). After the owner
  approves the SHA-256, HCG Repo's `release prepare` writes the `local-submission` import request.

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
- a tampered payload, a wrong or unpinned key, a payload that does not name its key;
- the waiting-for-approval message, with no approve request;
- a rejected job, which gets no native request;
- the mapping into the build and artifact records;
- the native-build request and result, validated against the schemas;
- the Release Passport of a distributable output, as Ship to Store reads it;
- the `IBuildHost` description.

`RootlessAppDebTests` checks a Build Host handoff: `submission.json` and `release-passport.json` with provider
`hcg-server-build-host` and no CI fields.

A live run against a real Build Host (HCG-Server 417a9cd, Linux, Landlock) on 2026-11-27 ran as follows:

1. Job `bj-20261127-120658-014c8753f4` built LifeOS commit `21326ef…` (`swift build --target LifeOSCore`).
2. It waited for approval, and the owner approved it with `hcg-agent build approve`.
3. It succeeded. `build.log` was downloaded and verified (SHA-256 `0cb30125…`, 24,964 bytes).
4. The provenance verified with key `c51552b0…05a4`, and the payload named the same key.
5. `native-build-request.json` and `native-build-result.json` were written. Both validate against the schemas
   (Python `jsonschema`), and the result's `request` equals the SHA-256 of the request file.

An earlier run against an agent built before 417a9cd was refused, as intended, because its payload did not name its
key.
