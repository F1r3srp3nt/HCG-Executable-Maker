# Unleashify (Mobile Build Studio): an iOS app → a rootless jailbreak app → HCG Repo Candidate handoff

Unleashify takes the iOS app an imported Xcode project builds and packages it, **unchanged**, as a rootless jailbreak
app (`/var/jb/Applications`), validates the package, and exports the files HCG Repo Candidate needs to import it. It
reuses Mobile Build Studio's archive engine (`MobileBuildService`) and the rootless packager, validator and handoff
(`RootlessAppWorkflow`, see [ROOTLESS-PACKAGING.md](ROOTLESS-PACKAGING.md)). It installs nothing, uploads nothing and
publishes nothing; publication to HCG Repo stays a manual step there.

## Where to click (HCG Sentinel)

1. **New Build › Import folder** (or Import ZIP) → choose the HCG-Sentinel checkout. Maker records the source revision
   (Git commit and branch, or a GitHub ZIP's commit) in the imported workspace (`import-revision.json`).
2. **Mobile Build Studio** → select **HCGSentinel** under *Imported mobile projects* → **Unleashify…** (under the
   support summary). After a device archive/IPA build, **Unleashify this build…** opens the page with that build instead.
3. **Unleashify** page:
   - *Source* — the imported commit and whether the copy is **Current**, **Stale** (the original folder has moved on:
     re-import before building), **From archive** or **Unknown**.
   - *Mode* — **Jailbreak App** (available), **Jailbreak Tweak** and **App + Tweak** (shown, not available, with the
     reason). Build is offered only for an available mode.
   - *Input* — **Build from the Xcode project** (scheme: **HCGSentinelModern**; the target, bundle ID and deployment
     target are shown) or **Use an existing device build** (`.app`, `.xcarchive`, `.ipa`). Simulator builds are refused:
     "Simulator build refused: …".
   - *Package* — package ID `com.hakmicapitalgroup.hcgsentinel`, version `0.1.0~beta1`, display name, bundle ID (kept),
     deployment target (kept), output folder.
   - *Test devices and installed builds* — each test device's compatibility, and the check for another installed build of
     the app (paste the phone's `uicache -l` / `dpkg -l`, or confirm none is installed).
   - *Candidate and provenance* — source repository and commit, CI run/artifact, changelog, known issues, and the owner's
     approval of this version as a free candidate demo (empty until given).
   - Confirm trust → **Build** (archive → inspect → package → validate) → **Export Candidate Handoff**.

The same path is driven headlessly by `tools/ui-driver --unleashify` in CI (screenshots of every step).

## What "ready for device testing" means

The page and the handoff (`provenance.json` → `unleashify.ready_for_device_testing`, README) say **ready** only when:
the package validated, the source copy is not stale, the first test device can run it, and the installed-build check is
**clear** — from the phone's list (`uicache -l`, `dpkg -l`, `dpkg-query -W`) or the tester's explicit confirmation.
A build with the same bundle ID elsewhere (sideloaded from Xcode/an IPA, or another package) is a **conflict**; a newer
installed version of the package is a downgrade conflict. Without either input the check is **not checked** and the
package is **not ready**; Maker never assumes the phone is clean.

## The iPhone 8 on iOS 13.3

Not a packaging problem. Every Mach-O in HCGSentinelModern records iOS 15.0 as its minimum (the project's deployment
target), the package declares `firmware (>= 15.0)`, and rootless jailbreaks (`/var/jb`) exist only for iOS 15 and later.
Changing package metadata would not make the binaries load on iOS 13.3. Supporting it needs a separate app build whose
deployment target and code support iOS 13 (a source change in Sentinel), packaged for that device's jailbreak — a
separate compatibility target. The page shows this for the device.

## The handoff and HCG Repo

`<package>_<version>-candidate-handoff/`: the `.deb`, `SHA256SUMS`, byte size, `provenance.json` (app source commit,
the CI run that built it, Xcode/SDK, signing, every check, and the `unleashify` block: mode, scheme, source revision,
installed-build check, devices, readiness), `package.toml`, `CHANGELOG.md`, `KNOWN-ISSUES.md`, `icon.png`,
`import-request.toml` (HCG Repo's format: `[request]` package/version/architecture/channel = candidate; `[source]`
github-artifact, repository, run_id, commit, artifact, sha256), `DEVICE-TEST.md`, `README.md`.

**Access.** HCG Repo Candidate serves only public builds, and an HCG package is public only by the owner's recorded,
per-version approval (`[access] exception` + `exception_versions`, HCG Repo `docs/ACCESS.md`). Maker writes the
production level (`connect` when undecided) and writes the exception **only** from an approval it was given; otherwise
the exception stays commented out and the README lists it as missing.

**Dry run against HCG Repo.** `tools/unleashify/hcgrepo-dry-run.py` copies an HCG Repo checkout to a temporary folder,
adds the handoff's `packages/<id>/` files and `imports/<id>.toml`, checks the request with HCG Repo's `load_request()`
and runs `import_deb(dry_run=True)` pinned to the request's package, version, architecture and SHA-256 — on the exact
bytes. It reports the access HCG Repo would apply, and with `--hypothetical-approval` repeats the run with a labelled
placeholder exception (in the throwaway copy only) so the full public-pool and candidate checks run too:

```
PYTHONPATH=<hcg-repo>/tools python3 tools/unleashify/hcgrepo-dry-run.py --repo <hcg-repo> --handoff <handoff> --hypothetical-approval
```

Verdicts: `VALIDATED` (importable as exported), `BYTES VALIDATED, NOT YET IMPORTABLE` (only the owner's approval is
missing), `REJECTED`. Nothing is written to HCG Repo.

With the owner's approval recorded, `--require-genuine-approval` (never combined with `--hypothetical-approval`) requires the
approval in the handoff's own `package.toml` — an uncommented `[access] exception` that is not a placeholder, with
`exception_versions` naming only this version — and requires `VALIDATED`.

**Recorded approvals** live in `tools/unleashify/approvals/<package>_<version>.txt` (the non-comment line is what is entered
in the page's *Owner's approval* field). HCG Sentinel 0.1.0~beta1: approved by the owner on 21 Nov 2026 as a free
demonstration candidate for controlled testing — preparation and validation only; publication, public distribution,
production deployment and installation on the owner's devices each need separate authorization.

## CI

- `unleashify-sentinel` (macos-26): HCG-Sentinel at a pinned commit; a Simulator build (outside Maker) refused in the UI;
  Import → Unleashify → HCGSentinelModern → Build → Export through the real views; the existing-device-build input gives
  the same bytes; the CLI (`--rootless-deb`) gives the same bytes; `--rootless-deb-validate` for iOS 16.5; control,
  contents, architectures, minimum iOS and bundle ID read with `dpkg-deb`, `lipo`, `vtool`, `plutil`; HCG Repo's
  `bin/hcgrepo inspect` and the importer dry run at a pinned HCG Repo commit. Uploads the handoff's `.deb` as `hcg-sentinel-unleashify-deb` (the artifact the import request names).
- `unleashify-artifact-roundtrip` (ubuntu): downloads that artifact, checks it holds one `.deb` with the request's
  SHA-256, runs the importer dry run on the downloaded bytes, and installs, reinstalls, removes and purges them with
  real `dpkg` in a sandbox (on Linux: macOS refuses dpkg's backup hard links inside an installed `.app` bundle).

## Not verified here

Installation, launch and removal on a real device (no phone in CI): follow the handoff's `DEVICE-TEST.md` (detection of existing
installations, snapshot, install of the exact file, verification, launch, removal, rollback, result record).
