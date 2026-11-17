# Rootless packages: an iOS app from a build → a jailbreak .deb → HCG Repo Candidate

Executable Maker packages an iOS app that a build **already produced** (a device `.app`, an `.xcarchive`, or the
`Payload/*.app` of an `.ipa`, extracted, never renamed) as a rootless jailbreak application package, validates it,
and exports a handoff for HCG Repo's candidate channel (`https://repo.hakmicapitalgroup.com/repo/candidate/`).

It does **not** change the app: no code changes, no added entitlements (privileged ones are refused), no SpringBoard
injection, no helper. It installs nothing, uploads nothing and publishes nothing. A validated package is not a device
test.

## Workflow (one implementation, UI and command line)

`RootlessAppWorkflow` (`src/HCG.ExecutableMaker.Packaging/Jailbreak/`):

1. **Inspect** — Info.plist (bundle ID, executable, versions, `MinimumOSVersion`, device families, Xcode/SDK) and every
   Mach-O (architecture, platform, minimum iOS, linked libraries, rpaths, code signature, entitlements, encryption).
   Refused: Simulator builds, macOS binaries, encrypted App Store binaries, no arm64 slice, privileged entitlements.
   Missing prerequisite reported: `ldid` when the build's Mach-O files are unsigned.
2. **Package** — a staged copy at `/var/jb/Applications/<Name>.app`; unsigned Mach-O files pseudo-signed with `ldid -S`
   (nested code first; the project's own entitlements file only, if given); a deterministic Debian 2.0 archive with
   **gzip** `control.tar.gz` / `data.tar.gz`, root:wheel, sorted entries. Control: `Architecture: iphoneos-arm64`,
   `Depends: firmware (>= MinimumOS), uikittools`. `postinst`/`postrm` only run `uicache -p` / `uicache -u` for the app
   (HCG Repo's documented convention, `docs/ROOTLESS_ROOTFUL.md`).
3. **Validate** — the .deb's own bytes: container and members, complete gzip decompression, complete extraction with
   size accounting, control fields, Depends, maintainer scripts, every path under `/var/jb`, bundle ID, executable mode,
   every Mach-O (arm64, iOS device, signed, not encrypted, entitlements), every library resolved (system or inside the
   bundle; rootful jailbreak paths refused), minimum iOS vs the test device, version immutability and ordering (dpkg
   rules), SHA-256 and size.
4. **Handoff** — `<package>_<version>-candidate-handoff/`: the .deb, `SHA256SUMS`, `provenance.json` (app source repo
   and commit, CI run/artifact, Xcode/SDK, ldid-signed files, every check), `control.txt`, `VALIDATION.txt`,
   `CHANGELOG.md`, `KNOWN-ISSUES.md`, `package.toml` (HCG Repo format; Beta; free public demo in Candidate; production
   access unchanged), `icon.png`, `import-request.toml` (only when pinned to a CI run artifact, otherwise
   `import-request.INCOMPLETE.toml`), `DEVICE-TEST.md` (Zebra install, launch, uninstall, recovery).

## Where

- **UI:** *Rootless package* page (sidebar), or **Rootless .deb (jailbreak)…** on a device archive/IPA result in Mobile
  Build Studio. Shows each stage, the build log, every check and the resulting files.
- **CLI:** `HCGExecutableMaker --rootless-deb <app|xcarchive|ipa> --package <id> --package-version <v> --name <n>
  --description <text> [--device-ios 16.5] [--repository … --commit … --ci-run … --ci-artifact … --ci-repository …
  --ci-commit …] [--production-access public|connect|paid|undecided] [--out <dir>]`;
  `HCGExecutableMaker --rootless-deb-validate <file.deb> [--package …] [--device-ios …] [--published <v>=<sha256>]`.
  Exit: 0 done, 2 usage, 4 inspection/prerequisite, 5 packaging, 6 validation.

## Verification

- Unit tests (`RootlessAppDebTests`, synthetic Mach-O with real load commands): packaging and validation; determinism;
  Debian's own `dpkg-deb --info/--contents/--extract`; **real `dpkg` install → upgrade → remove → purge in a sandbox**
  (files replaced and removed, `uicache` called); ldid signing order and the missing-ldid prerequisite; Simulator,
  macOS and encrypted binaries refused; minimum iOS vs 16.5; unresolved and rootful libraries; privileged entitlements;
  published-version immutability; corrupt gzip and lzma members; IPA extraction; handoff and import-request pinning.
- CI `rootless-app-deb` (macOS): HCG Sentinel archived from source in Mobile Build Studio (UI), packaged on the Rootless
  package page (UI), packaged again with `--rootless-deb` (CLI) — byte-identical — re-validated, and installed/removed
  with real `dpkg` in a sandbox. The `.deb` is uploaded as `hcg-sentinel-rootless-deb` for HCG Repo's importer.
- **Not verified:** installation and launch on a device.
