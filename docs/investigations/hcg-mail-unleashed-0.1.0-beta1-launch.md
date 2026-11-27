# HCG Mail Unleashed 0.1.0~beta1 closes at launch: packaging audit

**Status:** packaging audited; **no packaging defect demonstrated**. Cause of the exit **not determined**: it needs
the device's crash report. Unleashify's signing policy is **unchanged**.

**Package:** `com.hakmicapitalgroup.hcgmail.unleashed_0.1.0~beta1_iphoneos-arm64.deb`, 43,663,718 bytes, SHA-256
`ac4b31b10dbdf9f060b6e02274c3123c4e95045b8ba3b458f77f99387563099a`. It was built by HCG Mail's `unleashed.yml` (run
45580748239) with Executable Maker `--rootless-deb`. The audit read a copy and never changed it. The SHA-256 was
checked before and after. A fixed build is a new version (`0.1.0~beta2`), never new bytes for this one.

**Device report (HCG Repo `docs/device-tests/hcg-mail-unleashed-0.1.0-beta1.md`):**
- The package installed, the app was registered and its icon appeared.
- The app "immediately closes after attempting to launch it".
- Model, iOS version and the crash report are not yet recorded.

## What the package contains

The audit used Executable Maker's validator (`--rootless-deb-validate`, with the new Signature integrity check). It
was cross-checked with an independent script that recomputes every page hash and special slot.

| Mach-O | Signer | Page hashes | Special slots | Identifier · cdhash |
|---|---|---|---|---|
| `HCGMail.iOS` (main, arm64, iOS ≥ 16.0) | ad hoc, ldid-style (SHA-1 + SHA-256 CodeDirectories, no flags, no CMS) | 17,459/17,459 SHA-1 and 17,459/17,459 SHA-256 verify | Requirements, Entitlements, DER entitlements match; Info.plist and CodeResources not bound | `HCGMail.iOS` · `77ceb18c026ec4d96cb3601ad05fa978d020b969` |
| `Frameworks/libSkiaSharp.framework/libSkiaSharp` (arm64, iOS ≥ 11.0) | ad hoc (SkiaSharp NuGet package's own signature, kept as built) | 1,440/1,440 SHA-256 verify | Info.plist, Requirements, CodeResources match | `com.microsoft.libSkiaSharp` · `af0fc3349d6e1d97dee1cd5ad041292e37c558c9` |
| `Frameworks/libHarfBuzzSharp.framework/libHarfBuzzSharp` (arm64, iOS ≥ 11.0) | ad hoc (HarfBuzzSharp NuGet package's own signature, kept as built) | 347/347 SHA-256 verify | Info.plist, Requirements, CodeResources match | `com.microsoft.libHarfBuzzSharp` · `5823d1ce1310ffe866892337ff9ce813c53b7487` |

Also checked:
- **Encryption, architecture, platform:** no binary is encrypted; every binary has an arm64 slice built for the
  iOS platform. The highest minimum is iOS 16.0.
- **Dependencies:** every library resolves. That is 32 system libraries (`/usr/lib`, `/System/Library`) and the two
  frameworks, through `@rpath`. The main executable's `LC_RPATH` is `@executable_path/Frameworks`, which is where both
  frameworks are.
- **Rootless paths:** all 232 entries are under `/var/jb`; the app is at `/var/jb/Applications/HCGMail.iOS.app`. No
  binary links a rootful absolute path.
- **Entitlements:** only `keychain-access-groups = [com.hakmicapitalgroup.hcgmail.unleashed]`, in the main
  executable. That is exactly HCG Mail's `Entitlements.Unleashed.ldid.plist` and the set HCG Repo reviewed. Nothing
  privileged.
- **Code-signing flags:**
  - The main executable requests neither library validation (`0x200`) nor the hardened runtime (`0x10000`).
  - Its exec segment is marked as the main binary.
  - All three signatures are ad hoc, without a team identifier. So no team-ID comparison between the executable and
    the frameworks exists that could fail.
- **Maintainer scripts:** only `uicache -p/-u` for the app path. HCG Repo's script-free lifecycle passed.

## Is the mixed signing (ldid main executable, NuGet-signed frameworks) the defect?

**No evidence says so**, and one piece of evidence says it is not:

1. **Every signature in the package is intact.**
   - Each one verifies page by page against the bytes on disk, and its bound resources match.
   - A jailbreak trusts an ad hoc signature by its cdhash. An ldid signature and a linker or NuGet ad hoc signature
     are the same kind of signature for that purpose.
   - Re-signing the frameworks with ldid would replace one valid ad hoc signature with another valid one.
2. **HCG Terminal Unleashed 0.2.0~rc1 closed the same way.**
   - Its device record (HCG Repo `docs/device-tests/hcg-terminal-unleashed-0.2.0-rc1.md`; iPhone 11 Pro Max,
     iOS 17.4, Dopamine rootless) says it "closed about a second after launch".
   - Every Mach-O in that package is ldid-signed, including its helper and its embedded dylib.
   - A uniformly ldid-signed package failed the same way, so mixed signing does not explain both exits.
3. **Changing the signing policy would change the bytes of every future package to test a hypothesis nothing
   supports.** It would also lose the frameworks' own resource seals. That is why the policy was not changed: only
   unsigned Mach-O files are signed, and existing signatures are kept as built.

No entitlement was added, and no protection was disabled.

## What could still explain the exit (unverified, ranked by what we know)

| Hypothesis | Why it is plausible | What would confirm it |
|---|---|---|
| A failure in the device's fully AOT-compiled .NET code | HCG Mail's 2.2-second Simulator launch runs with the JIT; the device build is full AOT (`ios-arm64`, Release, trimmed). A trimming/AOT-only failure appears only on a device | Crash report: `EXC_CRASH (SIGABRT)` with `mono_`, `xamarin_` or `System.` frames in the crashed thread; HCG Mail's StartupTrace output (HCG Mail PR #22) |
| A failure common to Dopamine on this device for these apps | Terminal Unleashed rc1 (Swift, all-ldid) also closed within a second | Crash reports of both apps. A small control app packaged by Unleashify launching on the same phone |
| Code-signing refusal | Ruled out by the package as far as it can be checked off the device; the device's trust state is not visible here | `"namespace" : "CODESIGNING"` in the termination reason |
| A library not loaded | Ruled out off the device (every dependency resolves) | `"namespace" : "DYLD"`, "Library not loaded" |
| Watchdog | Unlikely for an exit within about a second | `FRONTBOARD`, `0x8badf00d` |

## Next evidence (no code change needed to collect it)

1. **The crash report** of the failed launch. DEVICE-TEST.md now has a section, "If the app closes at launch",
   with the commands: `ls -t /var/mobile/Library/Logs/CrashReporter/ | grep -i -e '^HCGMail.iOS' -e jetsam`,
   then the newest file. Its `termination.namespace` and crashed thread select the row above.
2. **HCG Mail's StartupTrace build** (HCG Mail PR #22), packaged as `0.1.0~beta2`. It records how far start-up got.
3. **A control:** a minimal .NET iOS app with the same Release/AOT settings, packaged by Unleashify and launched on
   the same phone. It separates the .NET runtime on this jailbreak from HCG Mail's own code.

## What changed in Executable Maker

- **Validator: "Signature integrity".**
  - Every slice's CodeDirectories are recomputed against the file. A page mismatch, an inconsistent
    Requirements/Entitlements slot, or a main executable with library validation or hardened runtime loading an ad
    hoc or differently signed framework fails the package.
  - A changed Info.plist or CodeResources seal is a warning.
  - VALIDATION.txt lists each binary's signer, verified pages, identifier and cdhash.
- **DEVICE-TEST.md:** the launch-evidence section above, with this product's executable name.
- **Regression tests (`DotNetBundleSigningTests`):** a bundle shaped like this one (unsigned .NET main executable,
  SkiaSharp and HarfBuzzSharp frameworks with real ad hoc signatures, AOT data, managed assemblies, CgBI icon). They
  check that:
  - only the main executable is signed, and the frameworks stay byte-identical;
  - a framework changed after signing is refused;
  - a changed framework resource is a warning;
  - library validation with ad hoc frameworks is refused;
  - a missing framework is refused;
  - the dotted executable name is handled in the handoff;
  - the submission claims only what Maker signed, with the exact entitlements.
  An opt-in test (`HCG_MAIL_UNLEASHED_DEB`) re-verifies the published package itself.

## Recorded in the Release Passport

Every Unleashify handoff now carries the build half of its HCG Release Passport (`release-passport.json`,
[RELEASE-PASSPORT.md](../RELEASE-PASSPORT.md)). Its `code_signing.details` records, for each Mach-O:
- the signer;
- whether Executable Maker signed it (`signed_by_builder`);
- the verified page hashes, identifier, cdhash, team, hardened runtime and library validation.

For a bundle shaped like this one, the main executable is "ad hoc, ldid-style" and signed by the builder. The SkiaSharp
and HarfBuzzSharp frameworks are "ad hoc", keep their own signatures, and are all verified
(`DotNetBundleSigningTests.The_release_passport_build_half_records_each_binarys_signature_and_no_repository_signing`).

HCG Repo's passport for the published 0.1.0~beta1 reports:
- `code_signing`: 3 Mach-O, all signed, `keychain-access-groups` only;
- `repository_signing`: not configured ("unsigned: no production repository signing key configured yet").

Repository signing concerns the channel's `Release` metadata and has no bearing on whether the app launches.

