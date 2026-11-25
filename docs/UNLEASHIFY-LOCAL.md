# Unleashify on your Mac (local builds, no GitHub Actions)

Unleashify can now build, package, validate and prepare a rootless jailbreak package entirely on your Mac. It uses the
same packager, validator and Candidate handoff as CI ([UNLEASHIFY.md](UNLEASHIFY.md), [ROOTLESS-PACKAGING.md](ROOTLESS-PACKAGING.md)).
There is no second packager. Nothing is installed, uploaded, committed or published.

| Step | What Maker does | Where |
|---|---|---|
| Pinned source | Clones your trusted local repository into a new folder (`git clone --no-checkout`), checks out exactly the commit you name (detached), verifies HEAD and that nothing changed, imports it and records the commit. Your repository is only read. | Unleashify › Source › **Check out and import** |
| Project and scheme | Reads the Xcode project's schemes, targets, bundle IDs and deployment targets. | Unleashify › Input |
| Native Release build | `xcodebuild archive` with your Xcode (Release, `generic/platform=iOS`, unsigned), the same engine as Mobile Build Studio. | **Build** |
| Bundle inspection | Info.plist, every Mach-O slice (arm64, iOS platform, minimum iOS), frameworks, signatures; Simulator, Mac and App Store-encrypted builds are refused. | Build › Inspect |
| Rootless packaging | `/var/jb/Applications/<App>.app`, `iphoneos-arm64`, `firmware (>= minimum iOS)`, `uikittools`; `ldid -S` pseudo-signing with the app's own entitlements. Privileged entitlements are refused. | Build › Package |
| Validation | Debian format 2.0, gzip members, full extraction, metadata, architecture, scripts, install paths, linkage, entitlements, minimum iOS vs the test device, SHA-256. | Build › Validate |
| Lifecycle | install → configure → upgrade → remove in a throwaway folder: payload stays under `/var/jb`; Maker's own `postinst`/`postrm` run with a recording stand-in for `uicache` (also once with a failing one, which must not fail the install); removal leaves nothing. Scripts that are not exactly Maker's are never run. No privileges are used. | Build › Validate (checks named "Lifecycle · …") |
| Provenance | Built by (Maker version and commit), toolchain read from the app itself (`DTXcode`, `DTXcodeBuild`, `DTSDKName`, e.g. "Xcode 27.2 (…)"), build time, host label (operating system and architecture only: no computer or user name), source visibility, validation summary. **No CI run or artifact is recorded for a local build.** | Candidate and provenance |
| HCG Repo handoff | `kind = "local-file"` import request, plus `hcg-repo/` holding the files at their HCG Repo paths (`imports/<id>-<version>.toml`, `imports/local/<file>.deb`, `packages/<id>/…`). | **Export Candidate Handoff** |
| Owner approval | Counts only for the exact SHA-256 you approve. If the bytes change, the approval no longer applies; the handoff says so and leaves the exception out. | Candidate and provenance › approval + approved SHA-256 |
| Import preview | HCG Repo's own importer (dry run) on the exact bytes, in a throwaway copy of your HCG Repo checkout. | Build output › **HCG Repo import preview** |

The HCG Repo side is the `local-file` request kind (HCG Repo PR #20, branch `maker/local-file-imports`). Prepare packages
checks the committed .deb's SHA-256, package, version and architecture, runs every repository validation, records the
provenance and removes the `imports/local/` copy once the package is in `pool/`. Only the binary package, the repository
name and the commit reach HCG Repo. Your source stays private and no token is needed.

## Build and launch the updated Executable Maker

On your MacBook (Xcode 27.2 installed and opened once; `brew install ldid`; .NET 10 SDK):

```bash
git clone https://github.com/F1r3srp3nt/HCG-Executable-Maker.git && cd HCG-Executable-Maker   # or: git pull on main
dotnet run --project src/HCG.ExecutableMaker.App -c Release
```

## In the app

1. **Open HCG Impactor**: sidebar › **HCG Impactor**.
2. **Create HCG Mail's missing profile.**
   1. Impactor › **Signing Doctor**: bundle ID `com.hakmicapitalgroup.hcgmail`, your iPhone connected and unlocked.
      Use **Diagnose** to see why each installed profile cannot sign it.
   2. Choose your team. Tick **Register the connected device** only if the iPhone is not registered yet. It is off by
      default.
   3. Click **Create profile with Xcode…**. The confirmation names the App ID, the device registration (or that there is
      none) and the profile. Nothing happens until you approve. Nothing is revoked, and no installed app is touched.
   4. Repeat for `com.hakmicapitalgroup.hcgmail.unleashed` if you build that variant.
3. **Build and install HCG Mail.**
   1. Mobile Build Studio › HCG Mail. The Build Doctor must show no blocking findings.
   2. Choose Output **iOS device build (.ipa)** and Signing **Apple automatic** (your team), then click **Build**.
   3. Back in HCG Impactor, drop the IPA. Signing Studio shows it is already signed with the new profile; re-signing is
      not needed.
   4. In Device Manager, tick the iPhone, then click **Install** (optionally **Launch**). If HCG Mail with the same bundle ID
      is already installed, Impactor shows "Already installed" and installs nothing until you confirm. It never removes an
      app or its data.
4. **Open Unleashify**: Mobile Build Studio › **Unleashify…**, or the Unleashify page directly.
5. **Build Signal Contractor.**
   1. Source › repository folder: your local `HCG-Signal-Contractor` checkout.
   2. Commit: `a5b3c2322f0de4fbbe8d10df179872c590aa1d75`, then **Check out and import**. The status shows the commit,
      its branches (`hcg/unleashify-0.1.0-beta1` if you have fetched it) and the repository name.
   3. Scheme **SignalContractor**. Confirm trust.
6. **Produce its rootless DEB.**
   1. Package ID `com.hakmicapitalgroup.signalcontractor`, version `0.1.0~beta1`, name `HCG Signal Contractor`, and
      the description.
   2. Test devices: `iPhone 11 Pro Max (Dopamine, rootless), 17.4`.
   3. Candidate and provenance: production access `undecided`, **Built on this computer** ticked, source visibility
      **private**, build host as you like (e.g. "MacBook Pro M4 Max"). Leave the CI fields empty.
   4. Click **Build**. The result line shows the .deb path, **SHA-256** and **byte size**. The Validate checks include
      "Minimum iOS vs iOS 17.4" and the "Lifecycle · …" checks. The toolchain line shows the Xcode that compiled the
      app.
7. **Prepare it for HCG Repo Candidate.**
   1. Click **Export Candidate Handoff**. The handoff folder holds `import-request.toml` (`kind = "local-file"`),
      `provenance.json`, `VALIDATION.txt`, `DEVICE-TEST.md` and `hcg-repo/`.
   2. HCG Repo import preview: choose your HCG Repo checkout (with PR #20) and click **Preview import**. Before your
      approval, the expected verdict is "Blocked only by the owner's approval".
   3. When you approve, type your approval and the **exact SHA-256** you approve, then export again and preview again.
      The verdict becomes "Importable as exported".
   4. **Ship to Repo**: HCG Repo's Candidate Release Manager (Distribution v2), on your HCG Repo `main` checkout.
      1. **Submit**: HCG Repo validates the exact .deb into its private incoming store, outside Git. It checks the
         handoff's `submission.json` against the package, then runs static checks, secrets, scripts, a simulated dpkg
         lifecycle and the repository's own rules.
      2. **Approve exact SHA-256…**: give your name ("Approved by"), your words ("Owner's approval") and the SHA-256 you
         approve. Maker refuses a SHA-256 that is not the reviewed package's. HCG Repo records the approval.
      3. **Prepare Candidate…**: HCG Repo imports the approved bytes into the checkout's `pool/`, plus
         `repo-lock.json`, `imports/<id>.toml` and `submissions/<id>/`. For a new package, tick "create
         packages/<id>/" to use the handoff's draft.
      4. Review the changes, commit them on a branch and open a pull request. Publishing is HCG Repo's manual
         **Publish repository** workflow; Maker never commits, pushes or publishes.

      The older `hcg-repo/` route (`kind = "local-file"`, HCG Repo PR #20) still works: copy `hcg-repo/` onto a
      branch, and **Prepare packages** imports it.

### The same from the command line

```bash
M="dotnet src/HCG.ExecutableMaker.App/bin/Release/net10.0/HCGExecutableMaker.dll"
$M --rootless-deb <SignalContractor.xcarchive> --package com.hakmicapitalgroup.signalcontractor --package-version 0.1.0~beta1 \
   --name "HCG Signal Contractor" --description "…" --device-ios 17.4 --production-access undecided \
   --repository F1r3srp3nt/HCG-Signal-Contractor --commit a5b3c2322f0de4fbbe8d10df179872c590aa1d75 \
   --local --source-visibility private --host "MacBook Pro M4 Max" --hcg-repo ~/src/HCG-repo --out ~/Desktop/signal-contractor
# after your approval of the printed SHA-256:
#  … --owner-approval "<who> on <date>: '<words>'" --approved-sha256 <sha>
H=~/Desktop/signal-contractor/com.hakmicapitalgroup.signalcontractor_0.1.0~beta1-candidate-handoff
$M --ship-to-repo submit  --hcg-repo ~/src/HCG-repo --handoff "$H"
$M --ship-to-repo approve --hcg-repo ~/src/HCG-repo --handoff "$H" --approved-sha256 <sha> --by "<name>" --record "<approval, verbatim>"
$M --ship-to-repo prepare --hcg-repo ~/src/HCG-repo --handoff "$H" --create-package-metadata --free-demo-approval "<owner's words>"
```

## Icon and device-test instructions

- **icon.png** is always a standard PNG, so browsers, Sileo, Zebra and HCG Repo's pages can show it.
  - Xcode stores app icons as Apple's CgBI PNG variant (BGRA, premultiplied alpha, raw deflate), which only Apple's
    frameworks decode. Unleashify converts such an icon to a standard RGBA PNG.
  - Every icon must be decoded by a standard decoder, be square and be at least 60 pixels.
  - When the app has no loose icon (icons only in `Assets.car`), or its icon cannot be made valid, the **approved
    artwork** is used. Set it under Package › Icon artwork, or with `--icon-artwork`. "Use the project's AppIcon"
    offers the largest image of the project's own AppIcon set, but only when you click it.
  - `provenance.json` records the icon's source, size and SHA-256. The package's own files are never changed.
- **DEVICE-TEST.md** names the actual product: its name, bundle ID, package ID, version and install path.
  - Detection searches for the product's own family terms, e.g. `sentinel`, `hcgmail` or `signalcontractor`.
  - Snapshot files are named after the package, e.g. `/var/mobile/hcgmail-unleashed-test-before-dpkg.txt`.
  - A variant such as HCG Mail Unleashed notes that the standard app (`com.hakmicapitalgroup.hcgmail`) is a
    different app ID and can stay installed.
- **submission.json** is HCG Repo's Distribution v2 manifest (`hcg.repo.artifact-submission` v1).
  - It records only measured facts: package, the exact file, SHA-256 and size, minimum iOS, the declared maintainer
    scripts, dependencies, source commit, and provider (`executable-maker-local`, or `github-actions` with its run).
  - It names no jailbreaks until a device test records one.


Exit codes: 0 done, 4 inspection or prerequisite, 5 packaging, 6 validation or lifecycle, 7 import preview rejected.

## What is verified where

- **Unit tests (all platforms):**
  - the local-file request and the `hcg-repo/` layout, with no CI fields;
  - an unpinned or dirty source is refused;
  - an approval applies to its exact SHA-256 only;
  - the lifecycle: script calls, failing `uicache`, upgrade, removal;
  - pinned checkout: a clean copy at the commit, the source repository unchanged, option-like commits refused;
  - no computer or user name in the host label, no credentials taken from remote URLs.
- **With an HCG Repo checkout (`HCG_REPO_CHECKOUT`):** HCG Repo's real importer accepts the request and is blocked
  only by the approval; with the SHA-bound approval it validates.
- **macOS CI (Unleashify job):**
  - a real Xcode archive goes through `--rootless-deb --local`: toolchain from `DTXcode`, lifecycle passed, no
    run/artifact fields;
  - the bytes are identical to the CI-mode path;
  - HCG Repo's importer from PR #20 runs a dry run;
  - the HCG Repo checkout is unchanged afterwards.
- **Not verified here:**
  - Xcode 27.2: hosted runners have up to Xcode 26.x, so only your Mac can build Signal Contractor as specified;
  - installation on the iPhone 11 Pro Max with Dopamine;
  - HCG Mail's real profile creation, which needs your Apple Account.

  Signal Contractor's SHA-256, size and provenance exist once you run step 6. They are not invented here.
