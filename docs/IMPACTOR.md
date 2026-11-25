# HCG Impactor

HCG Impactor is the iOS and iPadOS deployment workspace in HCG Executable Maker. It inspects an IPA, signs it (or
re-signs it) with your own Apple Development or Apple Distribution identity, finds your iPhone or iPad, installs the
app on it, and explains what went wrong when something does.

**Main flow:** Drop IPA → Inspect → Sign → Select device → Install. Open it from the sidebar (**HCG Impactor**), or
`HCGExecutableMaker --page impactor`.

Advanced options are kept in separate places:

- **Signing Studio › Advanced options:** a new bundle identifier, removing unsupported entitlements, the output folder.
- **The other sections:** Device Manager, Deployment Queue, Certificates, Provisioning, Signing Monitor, Installation
  History and Diagnostics.

HCG Impactor uses only Apple's supported tools and rules:

- It uses `codesign` with your Keychain identity (the private key never leaves the Keychain).
- It uses `ditto`, and Xcode's `devicectl` (CoreDevice) for devices.
- It does not bypass code signing, provisioning profiles, device registration, Developer Mode or pairing.
- It never removes an app from a device.
- It never revokes or replaces certificates.
- It never signs in to your Apple Account itself.
- It never uploads anything to App Store Connect, TestFlight or the App Store.

## Status

| | Status |
|---|---|
| IPA Inspector (all platforms) | Implemented · automated tests on synthetic IPAs (Windows, Linux, macOS) · real Xcode-built IPA on macOS CI |
| Signing / re-signing (macOS) | Implemented · macOS CI signs a real Xcode-built app with a **synthetic** identity and profile, then verifies it with Apple's `codesign` |
| Device discovery, installation, launch (macOS, `devicectl`) | Implemented · macOS CI runs the real `devicectl` (no device attached) · **physical-device installation: to be validated by you on your Mac** (steps below) |
| Wireless installation | Uses the same `devicectl` path when CoreDevice reports a network connection · not validated yet (USB first) |
| Windows / Linux | Inspection and signing plans only. Signing and installation need macOS; equivalent signing elsewhere is **not implemented or claimed** |

## Workspace sections

| Section | What it does |
|---|---|
| **Dashboard** | Drop zone. What this computer can do (inspect/sign/devices/install). Counts of devices, identities, profiles and signed apps. Signing reminders. HCG ecosystem status. |
| **IPA Inspector** | See [What the inspector reads](#what-the-inspector-reads). The original file is never changed. |
| **Signing Studio** | Identity (automatic, or one of yours), profile (automatic per bundle, or chosen), distribution type (Development, Ad Hoc, Enterprise, App Store export). Shows the plan for every bundle, innermost first, with its profile and entitlements, plus readiness findings. **Sign and verify** writes a new IPA. |
| **Device Manager** | Connected and paired iPhones/iPads: name, model, iOS version and build, CPU, connection (USB / Wi-Fi), pairing, Developer Mode, developer services, CoreDevice capabilities, and which installed profiles include the device's UDID. Step-by-step guidance for unlocking, Trust, pairing and Developer Mode. Copy the device record or open it in HCG System Tester. |
| **Deployment Queue** | Signed IPA plus the devices ticked in Device Manager (several devices = a batch, run one after another). **Check** runs the pre-installation validation without touching the device. **Install** runs per-device progress, cancellation and the result, with an optional launch. |
| **Certificates** | Apple Development and Apple Distribution identities: team, expiry, private key present, trust problems. |
| **Provisioning** | Profiles from Xcode's folders, verified (CMS signature and Apple chain): type, App ID, expiry, devices, certificates, and whether each profile covers the loaded app. **Add profile…** installs a profile into Xcode's folder (verified, never overwrites) or uses it for this session only. |
| **Signing Monitor** | Every app HCG signed: signing date, identity, profile, method, devices it was installed on, and the date it stops launching. Reminders 14 days before expiry. **Re-sign** and **Reinstall** are always manual: Apple does not allow permanent signing or background re-signing. |
| **Installation History** | Every attempt (installed, failed, blocked, cancelled) with device, iOS version, connection, duration and the diagnosis. |
| **Diagnostics** | The last failure explained, with the original output (redacted). Paste any tool's output to have it explained. **Export diagnostics** writes `report.json` (`hcg.diagnostic-report/1`) and `report.md`. |

## What the inspector reads

| Item | Source |
|---|---|
| Name, version, build, bundle ID, minimum iOS, device families, required capabilities | `Info.plist` (binary or XML) |
| Architectures, platform (device / Simulator / Mac), minimum OS | Each binary's Mach-O slices (`LC_BUILD_VERSION`) |
| App Store encryption (FairPlay) | `LC_ENCRYPTION_INFO(_64)`. Encrypted binaries are blocked; HCG does not decrypt apps. |
| Signature: ad hoc or certificate, identifier, team, hash type, signer, chain, signing time | The embedded signature (`LC_CODE_SIGNATURE`: CodeDirectory, CMS) |
| Entitlements | The embedded signature |
| Embedded profiles | Each bundle's `embedded.mobileprovision`: CMS signature checked, Apple chain checked, type, App ID, expiry, devices, certificates |
| Extensions, watch apps, App Clips, frameworks, loose dylibs | Every bundle and Mach-O file inside the app, each inspected the same way |
| Package integrity | Archive structure (no paths outside the package, no unsafe symlinks, decompression check), SHA-256, and each bundle's sealed resources (`_CodeSignature/CodeResources`) re-hashed: modified, missing and unsealed files |
| Device compatibility | The app compared with a chosen device: iOS version, iPhone/iPad family, architecture, platform |

A `.deb` is refused with a pointer to the separate jailbreak workflow (Mobile Build Studio › Unleashify, HCG Repo).

## How signing works (macOS)

1. **Plan** (portable, unit-tested).
   - The identity must be in each profile's certificate list and have its private key in the Keychain.
   - Each app, extension, watch app and App Clip gets a profile whose App ID covers its bundle ID (an exact App ID
     beats a wildcard), from the same team.
   - The profile must be valid now and, when a device is chosen, must list its UDID.
   - **Entitlements:** the app's own requested entitlements, moved to the new team and bundle ID. Each must be
     granted by the profile. The identity entitlements always come from the profile: `application-identifier`, team
     ID, `get-task-allow` and `aps-environment`.
   - An entitlement the profile does not grant **blocks**, unless you choose to remove it; every removal is reported.
2. **Sign** (`MacAppSigner`).
   - `ditto` extracts a private copy.
   - `PlistBuddy` changes the bundle ID, only if you asked for that.
   - Each bundle gets its `embedded.mobileprovision`.
   - Bundles are signed innermost first: `codesign --force --sign <SHA-1> --entitlements … --generate-entitlement-der`
     (frameworks and dylibs without entitlements).
   - The result is checked with `codesign --verify --deep --strict`, then packaged with `ditto` as a **new** IPA. HCG
     never overwrites a file.
   - The new IPA is inspected again.
3. **Warnings before you sign or install.**
   - A changed bundle ID installs as a separate app, without the original's data.
   - A different team cannot update an installed copy.
   - App Store profiles are not installable on devices.
   - Expiry within 14 days.

## How installation works (macOS)

Installation uses Xcode's `devicectl` with JSON output. HCG Impactor uses the Xcode selected in Toolchains.

1. **Discover:** `devicectl list devices`.
2. **Validate:** device compatibility, plus the signing facts the device will enforce:
   - a real signature;
   - a certificate and profile that are valid now;
   - an installable profile type;
   - the device in the profile;
   - team and App ID matching.

   It also checks whether the app is already installed (`devicectl device info apps`). Blockers stop here and the
   device is never touched. Warnings need confirmation (`--yes` on the command line).
3. **Install:** `devicectl device install app` with the signed `.app`, showing progress. Cancelling stops HCG's part;
   an installation already handed to the device may still finish.
4. **Launch (optional):** `devicectl device process launch`.
5. **Record** the result in Installation History and Signing Monitor.

A failed installation never removes the existing app.

## Installation diagnostics

| Problem | Recognised from (examples) |
|---|---|
| Invalid signature | `0xe8008019`, `0xe800801c`, "does not have a valid signature", "No code signature found" |
| Files changed after signing | `0xe8008017`, "A signed resource has been added, modified, or deleted" |
| Missing / unsuitable provisioning profile | `0xe8008015`, "A valid provisioning profile for this executable was not found" |
| Expired or revoked certificate | `0xe8008018`, "identity used to sign the executable is no longer valid", `CSSMERR_TP_CERT_EXPIRED` |
| Unregistered device | "device … not registered / not included in the provisioning profile" |
| Incompatible iOS version | "requires a newer version of iOS", `OSVersionTooLow` |
| Unsupported architecture | `IncompatibleArchitecture`, "bad CPU type" |
| Entitlement mismatch | `0xe8008016`, "signed with invalid entitlements" |
| Bundle ID / team mismatch with the installed app | `MismatchedApplicationIdentifierEntitlement`, "application-identifier entitlement … does not match" |
| Device locked | `0xe80000e2`, "passcode protected", "must be unlocked" |
| Developer Mode required | "Developer Mode is disabled / not enabled" |
| Device not paired | "not paired", `InvalidHostID`, "Trust this computer" |
| Xcode compatibility | "developer disk image", "not supported by this version of Xcode", missing `devicectl` |
| Insufficient storage | "not enough storage / space", `ENOSPC` |
| Device not found | "device … was not found", "No devices found" |

Unrecognised output is reported as **Unknown** with the original text; HCG never guesses.

Exports redact the following:

- credentials (Executable Maker's `SecretRedactor`);
- UDIDs (shown as `0000…001E`) and ECIDs;
- serial numbers;
- e-mail addresses;
- user names in home-folder paths.

## Command line

```text
HCGExecutableMaker --impactor inspect <ipa|app> [--json]
HCGExecutableMaker --impactor identities
HCGExecutableMaker --impactor profiles [--ipa <ipa>] [--profile-file <path>]…
HCGExecutableMaker --impactor devices [--json] [--include-udid]
HCGExecutableMaker --impactor plan <ipa> [signing options]
HCGExecutableMaker --impactor sign <ipa> [--out <new.ipa>] [signing options]
HCGExecutableMaker --impactor check <signed.ipa> --device <id|name|usb>
HCGExecutableMaker --impactor install <ipa> --device <id|name|usb>… [--launch] [--yes] [--sign [signing options]]
HCGExecutableMaker --impactor diagnose <file|->
HCGExecutableMaker --impactor history | monitor
HCGExecutableMaker --impactor open-in-system-tester --device <id|name|usb>
```

**Signing options:**

- `--identity <SHA-1>`
- `--profile <UUID>`
- `--profile-file <path>`
- `--extension-profile <path>=<UUID>`
- `--method development|adhoc|enterprise|appstore`
- `--bundle-id <id>`
- `--device <…>`
- `--drop-unsupported`
- `--report <folder>`

**Exit codes:** 0 done · 1 blocked or failed · 2 usage.

From the app bundle: `"/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker" --impactor devices`.

## Physical device validation (your MacBook Pro, Xcode 27.2)

CI has no iPhone, so only you can confirm installation on a real device. Do this once per release with one of your own
HCG apps.

1. **Mac.**
   - Xcode 27.2 is installed and opened once.
   - HCG Executable Maker › Toolchains shows that Xcode.
   - Xcode › Settings › Accounts shows your Apple Account with an Apple Development certificate.
2. **Device.**
   - Connect the iPhone by USB, unlock it, and tap **Trust**.
   - Turn on Developer Mode: Settings › Privacy & Security › Developer Mode, restart, confirm.
   - Open Xcode › Window › Devices and Simulators once and wait for "Preparing device" to finish.
3. **IPA.**
   - Use an IPA of your own app: Xcode › Product › Archive › Distribute App › **Development** (or Ad Hoc) › Export.
   - The profile must include the iPhone. Xcode-managed signing does that when the device is connected while you
     archive.
4. **Check before changing anything:**

   ```bash
   M="/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker"
   "$M" --impactor devices            # the iPhone: USB · Paired · Developer Mode on · install yes
   "$M" --impactor identities         # your Apple Development identity: Ready
   "$M" --impactor profiles --ipa MyApp.ipa
   "$M" --impactor plan MyApp.ipa --device usb --report ~/Desktop/impactor-reports
   ```

5. **Sign and install:**

   ```bash
   "$M" --impactor sign MyApp.ipa --device usb --out ~/Desktop/MyApp-hcg.ipa
   "$M" --impactor install ~/Desktop/MyApp-hcg.ipa --device usb --launch --report ~/Desktop/impactor-reports
   ```

   Or use the workspace: drop the IPA, sign it in Signing Studio, tick the device in Device Manager, then Install.
6. **Record** the result in `docs/STATUS.md`:
   - Mac model, macOS, Xcode version;
   - device model and iOS version;
   - app, method, USB or Wi-Fi;
   - result, the `report.md` path, and whether the app launched.

   Also try one expected failure, for example an IPA whose profile does not list the device, and confirm the
   explanation matches.

Until this is recorded, physical-device installation is **implemented but not validated**.

## HCG System Tester

System Tester (separate repository, Swift) discovers USB/Thunderbolt devices with IOKit, and its Apple Device
Intelligence is being expanded. To avoid two incompatible device-identity systems:

- **Shared device record `hcg.apple-device/1`.** `HCG.Impactor.Devices.AppleDevice` / `AppleDeviceContract`: a JSON
  document `{schema, generatedAt, producer, devices: [...]}`. Each device has:
  - `identifier`: the CoreDevice UUID, which identifies the device on this Mac;
  - `udid`: what profiles list, redacted unless explicitly included;
  - `name`, `productType`, `marketingName`, `deviceClass`, `platform`, `osVersion`, `osBuild`, `cpuArchitecture`;
  - `connection` (`usb` / `network` / `unavailable`), `transport`, `pairing`, `developerMode`,
    `developerServicesAvailable`, `tunnelState`, `bootState`, `isPhysical`;
  - `capabilities`: CoreDevice feature IDs;
  - `source`.

  Values the tool did not report are omitted, never guessed. A matching Swift `Codable` struct in System Tester
  (proposed for its `HCGCore/Model`) gives both apps the same identity for a device: the UDID, plus the CoreDevice
  identifier on a given Mac.
- **Open in System Tester.** Device Manager writes the device record to `<data>/impactor/system-tester-device.json`,
  copies it to the clipboard, and opens `hcgsystemtester://open/connected-systems`. That route exists today: one
  segment, no query. **Proposed** for System Tester: a device-specific route, or importing an `hcg.apple-device/1`
  record, so it can open the device directly.
- **Reports:** Impactor exports `hcg.diagnostic-report/1`, the format System Tester already exports and Device Link
  carries.

Both apps stay independently functional. Neither needs the other installed.

## HCG ecosystem

| Product | Integration | Status |
|---|---|---|
| HCG System Tester | Shared device record; open Connected Systems with the device | Available (navigation); device route proposed |
| HCG Sentinel | Diagnostics export in `hcg.diagnostic-report/1` (signing and integrity findings) | Available (file) |
| HCG Terminal | Deployment diagnostics export; Terminal's deep links are navigation-only | Export available |
| HCG Connect | Impactor runs inside Executable Maker with its existing sign-in and licence; no new credentials | Existing |
| HCG Store | Inspect an IPA downloaded from the Store. Authorized retrieval needs the Store's download API, which is not deployed | Pending Store API |
| HCG Server | Authenticated macOS build host for signing from Windows/Linux | Planned, not implemented |
| HCG Repo | Jailbreak `.deb` packages: separate workflow, never mixed with IPA installation | Separate by design |

## Architecture

`src/HCG.Impactor` is a self-contained module with no UI. Executable Maker's workspace
(`ViewModels/ImpactorViewModel.cs`, `Views/Pages/ImpactorView.axaml`) and command line (`--impactor`) are thin layers
over it, so a standalone HCG Impactor app can reference it unchanged.

| Part | Portable | Notes |
|---|---|---|
| `Inspection/` (`IpaInspector`, `MachOCodeSignature`), `Model/` | yes | Reuses Executable Maker's `PlistReader`, `ProvisioningProfileReader`, `AppleMachO` |
| `Signing/` (`SigningPlanner`, `EntitlementComposer`, `PlistXmlWriter`, `SigningInventorySource`) | yes | Identities and profiles come from Executable Maker's Signing Vault (`AppleSigningAssetService`) |
| `Devices/` (`AppleDevice`, `DeviceCompatibility`, `InstallReadiness`) | yes | The shared device record |
| `Diagnostics/` (`InstallDiagnostics`, `ImpactorReport`) | yes | |
| `MacOS/` (`MacAppSigner`, `MacDeviceService`, `DevicectlJson`) | macOS backend | Behind `IAppSigner` / `IDeviceService`; a remote (HCG Server) backend can implement the same interfaces |
| `Storage/ImpactorStore` | yes | JSON in `<data>/impactor/` (history, signed apps); no secrets, UDIDs redacted |
| `ImpactorService`, `Cli/ImpactorCli` | yes | The workflow the UI and command line share |

## Tests

| Kind | Where | What |
|---|---|---|
| Automated, synthetic | `tests/…/ImpactorTests.cs`, `ImpactorFixtures.cs` (all platforms) | Synthetic IPAs with real-format Mach-O, embedded signatures (CodeDirectory, entitlements, CMS), sealed resources and profiles signed by the test root. Covers: inspection; tampering; encrypted, Simulator and 32-bit binaries; unsafe archives; the signing planner (identity and profile choice, entitlements, unregistered devices, expiry, App Store, bundle ID changes); device compatibility; install readiness; devicectl JSON; every diagnosis kind; redaction; the deployment workflow with fake devices (blocked, warnings, batch, history); Signing Monitor reminders; the report format; the command line |
| Real Apple tools, synthetic assets | CI job `impactor` (macOS) | Xcode-built HelloIOS device app → inspect → synthetic identity (temporary keychain) and synthetic profile → plan → `codesign` → Apple's `codesign --verify` / `-d` confirm authority, team, entitlements and embedded profile → tamper check → real `devicectl` list and error translation → the UI signs through Signing Studio (screenshots) |
| Physical device | You (above) | Installation and launch on your iPhone/iPad with your real Apple identity |

Synthetic identities and profiles are not Apple-issued, and HCG says so. No device would accept apps signed with them.
