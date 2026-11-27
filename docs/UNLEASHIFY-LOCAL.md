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
| HCG Repo handoff | `submission.json` (HCG Repo's `hcg.repo.artifact-submission` v1), with the `signing` claim: method `ldid`, the files Maker signed, each binary's exact entitlements. `package.toml` draft with the same entitlements under `[review.entitlements."<path>"]` and **no `[access] exception`**. Submit with **Ship to Repo**; HCG Repo's Release Manager writes the import request itself. | **Export Candidate Handoff** |
| Owner approval | Given in HCG Repo, not in Maker: `release approve` (exact SHA-256), then `release prepare --free-demo-approval` for a free Candidate demo. Maker shows these commands; it never runs them. | HCG Repo checkout (commands shown under **Ship to Repo**) |
| Import preview | HCG Repo's own importer (dry run) on the exact bytes, in a throwaway copy of your HCG Repo checkout. | Build output › **HCG Repo import preview** |

The HCG Repo side is its Candidate Release Manager, under HCG Repo's Executable Maker submission contract
(`docs/EXECUTABLE_MAKER_SUBMISSION.md` in HCG Repo).
- `release submit --handoff <folder> --result-json <file>` reviews the exact bytes in a private incoming store (Maker
  runs this).
- `release status <id>` gives the result as JSON: state, the six evidence levels, findings, requirements, next step
  (Maker runs this).
- `release approve` records your approval of the exact SHA-256 (**you** run this; Maker never does).
- `release prepare` imports into `pool/`. With `--via local-file`, it instead stages the .deb under `imports/local/`
  with its own `local-file` request, so the Prepare packages workflow imports it in CI (**you** run this).

The six levels are shown separately: static inspection, maintainer script review (read, not run), script-free dpkg
lifecycle, device installation, application launch, runtime verification. A passed script-free lifecycle means dpkg
handled the payload with the maintainer scripts removed. It is not evidence that the scripts, an installation on a
device or the app work.

Only the binary package, the repository name and the commit reach HCG Repo. Your source stays private and no token is
needed.

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
   1. Click **Export Candidate Handoff**. The handoff folder holds `submission.json`, `provenance.json`,
      `VALIDATION.txt`, `DEVICE-TEST.md`, `package.toml`, `icon.png` and the .deb.
   2. HCG Repo import preview: choose your HCG Repo `main` checkout and click **Preview import**. HCG Repo's Release
      Manager reviews the handoff in a throwaway incoming store, which is then deleted. The expected verdict is
      "Validated by HCG Repo · awaiting the owner's checksum approval", with the six levels listed separately.
   3. **Ship to Repo**: HCG Repo's Candidate Release Manager, on your HCG Repo `main` checkout.
      1. **Submit**: HCG Repo validates the exact .deb into its private incoming store, outside Git. It checks the
         handoff's `submission.json` (including the signing and entitlement claims) against the package, then runs
         static checks, secrets, scripts, a script-free dpkg lifecycle and the repository's own rules. Maker shows
         the state, the SHA-256, the six levels, the findings and the owner's next commands.
      2. **You approve, in your HCG Repo checkout**: run the `bin/hcgrepo release approve <id> --sha256 <sha> --by …
         --record …` line Maker shows, with your name and your words. HCG Repo refuses any other SHA-256.
      3. **You prepare, in your HCG Repo checkout**: run the `bin/hcgrepo release prepare <id> …` line Maker shows. It
         already carries `--create-package-metadata` for a new package and `--free-demo-approval "<your words>"` when
         the package is not public. Add `--via local-file` to stage the .deb under `imports/local/` for HCG Repo's
         **Prepare packages** workflow instead of importing into `pool/` here.
      4. **Refresh status** in Maker shows HCG Repo's state (approved, prepared).
      5. Review the changes, commit them on a branch and open a pull request. Publishing is HCG Repo's manual
         **Publish repository** workflow; Maker never approves, prepares, commits, pushes or publishes.

### The same from the command line

```bash
M="dotnet src/HCG.ExecutableMaker.App/bin/Release/net10.0/HCGExecutableMaker.dll"
$M --rootless-deb <SignalContractor.xcarchive> --package com.hakmicapitalgroup.signalcontractor --package-version 0.1.0~beta1 \
   --name "HCG Signal Contractor" --description "…" --device-ios 17.4 --production-access undecided \
   --repository F1r3srp3nt/HCG-Signal-Contractor --commit a5b3c2322f0de4fbbe8d10df179872c590aa1d75 \
   --local --source-visibility private --host "MacBook Pro M4 Max" --hcg-repo ~/src/HCG-repo --out ~/Desktop/signal-contractor
H=~/Desktop/signal-contractor/com.hakmicapitalgroup.signalcontractor_0.1.0~beta1-candidate-handoff
$M --ship-to-repo submit --hcg-repo ~/src/HCG-repo --handoff "$H"   # state, SHA-256, six levels, your next commands
$M --ship-to-repo status --hcg-repo ~/src/HCG-repo --handoff "$H"   # after you approve or prepare in HCG Repo
# Then, yourself, in ~/src/HCG-repo (the exact lines are printed by submit/status):
#   bin/hcgrepo release approve <id> --sha256 <sha> --by "<you>" --record "<your approval, verbatim>"
#   bin/hcgrepo release prepare <id> --create-package-metadata --free-demo-approval "<your words>"
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
  - `submission.json` with no CI fields, and no request of Maker's own;
  - an unpinned or dirty source is refused;
  - a local handoff never writes the owner's approval (no `[access] exception`); Maker has no approve or prepare
    operation;
  - `signing` claims only the files Maker signed, with each binary's exact entitlements, matching the
    `[review.entitlements]` tables of the draft;
  - the lifecycle: script calls, failing `uicache`, upgrade, removal;
  - pinned checkout: a clean copy at the commit, the source repository unchanged, option-like commits refused;
  - no computer or user name in the host label, no credentials taken from remote URLs.
- **With an HCG Repo `main` checkout (`HCG_REPO_MAIN_CHECKOUT`):** HCG Repo's Release Manager validates each
  handoff. Maker submits and reads status; the test then plays the owner and runs the printed approve (a wrong
  SHA-256 is refused by HCG Repo) and prepare commands, into `pool/` and via local-file (HCG Repo's loader accepts the
  request it wrote). The preview leaves nothing behind.
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
