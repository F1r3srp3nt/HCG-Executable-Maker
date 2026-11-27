# HCG Release Passport in Executable Maker

The HCG Release Passport (`hcg.release-passport`, format version 1) is HCG's shared record of one released build.
HCG Repo defines it in `docs/RELEASE_PASSPORT.md` and `docs/schemas/release-passport-v1.schema.json`. Executable Maker
pins that schema byte for byte:

| | |
|---|---|
| Pinned from | `F1r3srp3nt/HCG-repo` @ `3bf53c7bb2b6e67a2f66f3a7e1490fc0c1f41481` |
| File | `docs/schemas/release-passport-v1.schema.json` |
| SHA-256 | `2eb1a9138fed6a9a349fd329c1c9206299ee8255de59a9dc447ce59b7644d65e` |
| In Maker | `src/HCG.ExecutableMaker.Packaging/Distribution/release-passport-v1.schema.json` (embedded resource) |

## Who writes what

A passport has two halves.

| Part | Written by | Fields |
|---|---|---|
| **Build half** | Executable Maker, from what it measured | `artifact` (file name, SHA-256, size), `package`, `version`, `architecture`, `platform`, `source` (repository, commit, branch), `build` (provider, toolchain, built at, CI run and artifact only for CI builds), `code_signing`, Maker's `validation` evidence |
| **Authority half** | The distribution authority (HCG Repo; HCG Store for its own releases) | `distribution` (channel, access), `approvals`, `publication`, `device_evidence`, the device levels of `validation`, `compatibility_status`, **`repository_signing`** |

The build half leaves the authority fields empty and says so in their `note` and `status`. It never fills them with
assumptions. Maker refuses to write a build half that states any of these:
- a repository signing key or its configuration;
- an approval;
- device evidence or a device level other than `not-recorded`;
- a publication status, or a channel or access level.

## `repository_signing` and `code_signing` are different things

| | `code_signing` | `repository_signing` |
|---|---|---|
| What is signed | The Mach-O code inside the package (ldid pseudo-signatures, NuGet/Xcode ad hoc signatures, Apple certificates) | The repository metadata (`Release`, `InRelease`, `Release.gpg`) of the channel that serves the package |
| Who knows it | Anyone holding the bytes. Maker reads every signature and verifies each page hash | Only the distribution authority, from its signing configuration (HCG Repo `config/repo.toml [signing]`) |
| In Maker's build half | `method_claimed`, `binaries`, `all_signed`, `entitlements` (HCG Repo's shape: path → keys), plus `entitlements_exact`, `signed_by_builder` and per-binary `details` (signer, verified pages, identifier, cdhash, team, hardened runtime, library validation) | `scope` and a `status` saying the builder does not state it. No `configured`, no `fingerprint` |
| Shown by Maker | Under "Code signing (Mach-O code inside the package)" | Under "Repository signing (channel metadata, not the app)", **only** from HCG Repo's own passport (`bin/hcgrepo passport`) |

## Where Maker uses it

- **Unleashify handoff:** `release-passport.json` (the build half), next to `submission.json`. It is checked against
  the pinned schema and against Maker's build-half rules before it is written. HCG Repo accepts the extra file in a
  submission folder.
- **Ship to Repo** (`--ship-to-repo submit|status`, and the Unleashify page):
  - Before the release is in HCG Repo's lock, Maker shows the build half and "Repository signing: not stated: only
    the distribution authority states it".
  - Once the owner has prepared the release, Maker reads HCG Repo's passport with `bin/hcgrepo passport
    <package> <version>`. It validates it (version first, then schema) and reconciles it with the build half:
    - a different SHA-256, size, file name or release identity is an **error**;
    - a different source commit, provider, Mach-O count, signatures, entitlements or signing method is a **warning**.

    HCG Repo's `repository_signing` is shown verbatim, and its record wins for everything it decides. Maker fills only
    fields HCG Repo left empty, such as `build.toolchain` and `build.built_at`, and lists them under
    `filled_from_build_half`.
- **Ship to Store** (`--ship-to-store`, and the Ship to Store page): HCG Store v2 (store `main` 3749fe6) records, per
  file, the source commit, build provider, toolchain and the uploader's validation report, and needs them before a
  release can be submitted (`409 provenance_required`).
  - Maker fills them only from a passport whose artifact SHA-256 and size are the file's: `<file>.release-passport.json`,
    the folder's `release-passport.json` (an Unleashify handoff), or `--passport <file>`.
  - Provider mapping: `executable-maker-local` → `local`; `github-actions` and `github-actions-private` →
    `github-actions`; `hcg-server-build-host` → `hcg-server-build-host`. Any other provider is not reported.
  - The validation report names the six levels and the code signing of the application code. Repository signing is
    not sent: it is not HCG Store's to record for a file.
  - Without a matching passport the fields stay empty and the page and CLI say why. Unknown values are omitted from
    the request, so HCG Store servers without these fields still accept it.

## Version compatibility

- `format` must be `hcg.release-passport`. Anything else is "not a passport".
- `formatVersion` 1 is read with the pinned schema.
  - A higher version is refused with "newer than this HCG Executable Maker understands; update Executable Maker". It is
    never half-read with version-1 rules.
  - A version below 1, or one that is not an integer, is invalid.
- Fields Maker does not know are accepted in a version-1 record, as the schema allows. Additions on HCG Repo's side
  (for example `compatibility_status`) do not break Maker.
- Schema drift: with `HCG_REPO_MAIN_CHECKOUT`, a test compares HCG Repo's current schema with the pinned one and fails
  with the new SHA-256 when they differ, so a change is reviewed and re-pinned deliberately.

## Validation

`JsonSchemaLite` evaluates the draft 2020-12 keywords HCG's schemas use, and reports any other keyword as unsupported
instead of skipping it. Tests check that:
- it agrees with the reference implementation (Python `jsonschema`, `Draft202012Validator`) on valid and mutated
  passports;
- it accepts every passport HCG Repo issues for its lock (`bin/hcgrepo passport --out`).

## Proposal for HCG Store (for the founder's review, not implemented in HCG Store)

HCG Store's artifact declaration is a strict object, so Maker cannot send a passport today. Maker sends the passport's
provenance in the Store's existing fields. A later HCG Store version could accept the build half itself, as an optional
`passport` (`hcg.release-passport` v1, build part) on `POST /api/v1/publishing/releases/{id}/artifacts`:
- validated against the same schema;
- its `artifact.sha256` must equal the declared and received SHA-256;
- recorded as REPORTED.

HCG Store would then add its own authority half (edition, `central_id`, approval, publication), as
`docs/RELEASE_PASSPORT.md` in HCG Repo foresees. It would not state repository signing for files it serves outside an
APT repository.
