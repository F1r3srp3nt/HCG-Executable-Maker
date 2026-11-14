# HCG Signing Vault — Apple signing in Executable Maker

Signing & Security › **Apple Signing** shows and manages the Apple signing material on this Mac:
- identities and provisioning profiles;
- per-project signing settings;
- a pre-build check;
- the status of the optional HCG account backup.

**Status at a glance**

| Part | State |
|---|---|
| Keychain detection, `.p12` / `.cer` / profile import, duplicate detection, local removal | **Implemented** (macOS). Automated tests use synthetic certificates and an in-memory keychain. A real-keychain test (synthetic identity, deleted afterwards) runs on macOS with `HCG_KEYCHAIN_TESTS=1` (CI step on the macOS test job). |
| Provisioning profiles read from CMS-verified contents | **Implemented**. |
| Project signing configuration (automatic / manual, routes, per-target profiles) | **Implemented**. Stored locally, non-secret. |
| Pre-build validation | **Implemented**, in Signing & Security ("Check signing") and before every iOS archive/IPA build with Apple signing. |
| Account metadata (fingerprints, UUIDs, team, type, expiry; project preferences) | **Implemented in the client and in the website drop-in package (migration, routes, tests). Not deployed.** Until HCG deploys the routes, the app shows account backup as *unavailable*. |
| Encrypted private-key vault | **Design and contract only (below). Not implemented.** The app shows it as *not available*; nothing pretends to upload a key. |
| App Store Connect API keys | **Separate boundary**: a reference type and validation. Not required for signing. |

## Using it

1. **Open Signing & Security.** The Apple Signing card lists:
   - **Signing identities.** For each certificate: name, type, Team ID, validity, SHA-1 and SHA-256, whether the matching **private key** is in this Mac's keychain, revocation as far as macOS reported it, and where it is (this Mac / recorded in the HCG account / on another Mac).
   - **Provisioning profiles.** For each profile: name, UUID, distribution type and devices, App ID (bundle identifier pattern), Team, platforms, expiry, allowed certificates, entitlements with values, signature status and location.
2. **Import.**
   - **Import .p12…** asks for the password in a dialog and imports the certificate *and* its private key into the login keychain.
   - **Import .cer…** imports a certificate alone and explains that it cannot sign without its private key: export a `.p12` on the Mac that created it.
   - **Import profile…** verifies the profile and installs it as `<UUID>.mobileprovision` or `<UUID>.provisionprofile` in `~/Library/Developer/Xcode/UserData/Provisioning Profiles`.
   - Duplicates (same SHA-1 / same profile UUID) change nothing. Expired, malformed, tampered or non-Apple-signed profiles are refused with the reason.
3. **Choose where imports go.**
   - **Import to this Mac** is the default.
   - **Also back up to my HCG account** is an explicit opt-in. It is enabled only when you are signed in with HCG and HCG's signing-metadata service answers, and it records public metadata only.
4. **Configure a project** (Project Signing):
   - Choose the project, the platform and the mode: **Xcode-managed automatic signing**, or **Manual**.
   - Choose the route: iOS development, Ad Hoc, App Store/TestFlight, In-house; macOS development, Developer ID, Mac App Store.
   - Enter the Team ID. In Manual mode, also choose the certificate, the installer certificate (Mac routes) and the profile.
   - Optionally enter device UDIDs to check them against the profile.
   - **Save settings** stores references only.
   - **Check signing** runs the validation and lists every check, with a remedy for each failure. Each check says whether it was made locally or needs Apple's services.
5. **Build.** Mobile Build Studio builds iOS archives and IPAs with Apple automatic or manual signing. They run the same validation before `xcodebuild` starts, and a failure stops the build with the specific problems. Simulator, unsigned and "project default" builds are not affected.

**Apple Account sign-in stays in Xcode.** HCG never asks for Apple Account passwords. Automatic signing uses the account signed in to Xcode, and `-allowProvisioningUpdates` is passed only when you enable it in Mobile Build Studio.

## Three different removals

| Action | What it does | What it does **not** do |
|---|---|---|
| **Remove HCG record** | Deletes HCG's account record (metadata) of the asset. | Does not touch this Mac's keychain, other Macs, or Apple. |
| **Delete from this Mac** | `security delete-identity -Z <SHA-1>` (certificate **and private key**) or `security delete-certificate` (certificate only). Removing a profile deletes its installed file. | The certificate stays valid at Apple and on other Macs. Unless another copy exists, a deleted private key is gone: Apple keeps no copy. |
| **Revoke with Apple** | Only on developer.apple.com or in Xcode, by an account holder or admin. HCG explains the effects and opens the page. | HCG never revokes. Revocation invalidates the certificate everywhere, breaks the profiles that include it, and for Developer ID can stop distributed software from launching. |

## Routes and what each needs

| Route | Certificate | Provisioning profile | After signing (separate operations) |
|---|---|---|---|
| iOS development | Apple Development | Development (device list) | Install on registered devices |
| iOS Ad Hoc | Apple Distribution | Ad Hoc (device list) | Export IPA (release-testing) |
| iOS App Store / TestFlight | Apple Distribution | App Store | Export IPA; uploading is separate, never automatic |
| iOS In-house | Apple Distribution (Enterprise team) | In-house (all devices) | Export IPA (enterprise) |
| macOS development | Apple Development | **Only for restricted entitlements** | Runs on this Mac |
| macOS Developer ID | Developer ID Application (+ Developer ID Installer for a `.pkg`) | **Only for restricted entitlements** (Developer ID profile) | Notarize separately (notarytool), staple; publish separately |
| Mac App Store | Apple Distribution / 3rd Party Mac Developer Application + Mac Installer Distribution | Mac App Store | Signed `.pkg`; uploading is separate |

Restricted (profile-backed) entitlements on macOS:
- `com.apple.developer.*`
- `keychain-access-groups`
- `aps-environment`
- application identifier
- `com.apple.security.application-groups`

App Sandbox and hardened-runtime keys (`com.apple.security.*`) need no profile.

## What the pre-build check verifies

**Checked locally, from verified data:**
- The route fits the platform.
- Signing tools are available: `codesign`, `security`, and Xcode for iOS and `productbuild` for `.pkg`. These are reported as **Tools**, never confused with missing assets.
- The Team ID is set and valid.
- **Certificate:**
  - its type fits the route;
  - its private key is present;
  - it is in its validity period (warns within 30 days of expiry);
  - macOS trust problems are reported;
  - revocation is shown only when macOS reported it;
  - its team matches the project.
- **Provisioning profile, per target, including extensions and other embedded targets with their own bundle IDs:**
  - it is installed on this Mac (an account record never counts);
  - its CMS signature is Apple's;
  - it has not expired;
  - its platform and distribution type fit the route;
  - its team matches;
  - it includes the selected certificate;
  - the bundle ID matches the App ID, under Apple's wildcard rules;
  - a wildcard App ID is not used with capabilities;
  - every requested restricted entitlement, and its value, is allowed (`get-task-allow` is not used with distribution profiles);
  - the chosen devices are in the profile.
- Embedded targets' bundle identifiers are prefixed by the app's.
- Entitlements are read from each target's `<Target>.entitlements` file.

**Needs Apple's services. Reported as *Not verified*, never as passed:**
- certificate revocation (HCG never says "not revoked");
- profile invalidation;
- the App ID's enabled capabilities;
- with automatic signing: whether Xcode can create identities and profiles.

## Technical boundaries

### Keychain integration (`MacAppleKeychain`)

- **Reading.**
  - `security find-identity` (all policies, without `-v`) lists every identity with the problem macOS reports.
  - `security find-certificate -a -p` lists certificates. Those without an identity have no private key here.
  - The only command arguments are fixed verbs and SHA-1 fingerprints, which are validated as 40 hex characters.
- **Importing.**
  - The `.p12` password is checked against the file's integrity MAC (`Pkcs12Info.VerifyMac`) before anything is written. A wrong password changes nothing.
  - The file is then opened in-process, and the certificate and key are added through the keychain API (`X509Store`, CurrentUser/My, i.e. the login keychain).
  - The password is never on a command line, in a file, in the environment, in settings or in a log.
  - No `-A`/`-T` access grants are made. The first `codesign` use of the key shows macOS's own access prompt, and declining any prompt counts as cancelled.
- **Secret redaction.** Diagnostic text goes through `SecretRedactor`, which masks private-key blocks and password assignments.

### Profiles (`ProvisioningProfileReader`, `ProvisioningProfileStore`)

- The CMS envelope is decoded with `SignedCms`. The signature must match the contents, and the signer must chain to a trusted root, which must be Apple's when the system store is used.
- Only then are the plist fields used.

### Project settings (`IProjectSigningStore`)

- Settings are stored as JSON in the local settings database, under `signing-project:<project id>`.
- The record type (`ProjectSigningConfiguration`) has no field for keys, passwords or Apple Account credentials.

### Pre-build (`SigningPrebuildCheck`)

- It is injected into `MobileBuildService`.
- It applies to `IosArchive` / `IosIpa` with `AppleAutomatic` / `AppleManual`.
- Manual signing without a saved certificate picks one from the profile's certificates, as Xcode does.

## Account metadata (step 3)

The client is `SigningAccountService`. The contract is `hcg.executable-maker/1`, under `/api/public/executable-maker/v1`:

| Method & path | Body / result |
|---|---|
| `GET /signing/assets` | `{assets:[{id, assetType, sha1, sha256, profileUuid, displayName, teamId, certificateKind, profileKind, bundleIdPattern, platforms, expiresAt, deviceId, deviceLabel, updatedAt}]}` |
| `POST /signing/assets` | One record (the same fields without `id`). Idempotent per fingerprint or profile UUID and device. Any other key is **refused** with 400: `privateKey`, `p12`, `password`, device lists… |
| `DELETE /signing/assets/{id}` | `{removed:true}`. Removes HCG's record only. |
| `GET /signing/projects/{key}` | `{preferences}`, or `null`. |
| `PUT /signing/projects/{key}` | `{preferences:{platform, mode, route, teamId, identitySha1, installerIdentitySha1, profileUuid, targets:[{targetName, bundleIdentifier, profileUuid}]}}`. Device UDIDs and entitlements stay on the Mac. |

**Server side.** The implementation is in [`integration/hcg-website`](../integration/hcg-website), migration `20261113230000_executable_maker_signing_metadata.sql`.
- **Authorization:** the caller's own token through the app's registered client, plus ERP App Access to `executable-maker`. Revoked access answers `403 revoked`.
- **Tenant isolation:** every row has `owner_subject`. RLS is on with no grants, so the tables can be reached only through `SECURITY DEFINER` functions that filter by the caller. Another account's record answers 404.
- **Privacy:** no storage bucket and no public URLs. Every record and removal is audited.

**Tests.**
- `test/signing.test.mjs` runs on PostgreSQL 16 and covers:
  - cross-account access rejection, through the API and directly against the database;
  - other apps, website sessions, outsiders and revoked accounts;
  - refusal of secret fields;
  - idempotency and removal semantics;
  - project-preference validation.
- `HcgContractTests.Signing_metadata_round_trips_through_the_handler_and_migration` runs the C# client against the real handler and migration.

**Not yet available:**
- **Deployment.** Until HCG applies the migration and publishes the routes, the app gets 404 → `NotDeployed` and shows "HCG account backup is unavailable".
- **Organisation (team) sharing.** HCG's ERP has no organisation-membership contract yet (`exm.organisations` returns none), so records are per account only.

## Encrypted private-key vault (step 4) — design contract, not implemented

The `.p12` password is **not** the vault design. A vault must meet every point below before the app offers private-key backup. `EncryptedSigningVault.Available` is `false`, and the UI says so.

1. **Client-side encryption.** Each exported identity (PKCS #12 re-wrapped in memory) is encrypted on the Mac before upload, with an authenticated cipher from a vetted library: XChaCha20-Poly1305 (libsodium) or AES-256-GCM (platform CryptoKit / .NET `AesGcm`), with a random 256-bit **data key** per item. The asset ID, owner and version are bound as associated data. A modified ciphertext or a moved item fails to decrypt.
2. **Vault key that HCG never has.** Data keys are wrapped by a **vault key** generated on the first approved device. It is stored in that Mac's keychain (Secure Enclave–protected where available) and never sent in plaintext. HCG stores only wrapped keys and ciphertext.
3. **Device approval.**
   - A new Mac creates a device key pair (P-256 in the Secure Enclave) and sends its public key.
   - An already-approved device, or the recovery key, approves it by wrapping the vault key to that public key (HPKE, RFC 9180).
   - Approval is shown on both devices with a short comparison code.
4. **Recovery.**
   - A separate **recovery key** (≥ 128 bits, shown once as words or a QR code) wraps the vault key. It is not derived from the HCG account password and never stored by HCG.
   - **Losing every approved device and the recovery key makes the vault unrecoverable.** HCG account password recovery cannot recover it. The UI must say this before the first backup.
5. **Explicit export only.** HCG never exports keychain keys automatically. The user selects each identity and authorizes the export, and macOS shows its own export prompt.
   - Non-exportable keys (for example a Secure Enclave key, or a key whose access control forbids export) are listed as "cannot be backed up" with the reason.
6. **Server.**
   - Per-object authorization (owner, and later team).
   - Private storage with no public URLs; short-lived signed transfer URLs bound to the caller.
   - Size limits, an audit trail, and tenant isolation tests.
   - Uploads are resumable and idempotent. An interrupted upload leaves no partial item visible; an interrupted download is retried.
7. **Deletion and retention.**
   - Deleting a vault item removes its ciphertext and wrapped keys at once, and backups age out within the stated retention (to be set by HCG, e.g. 30 days).
   - **Removing a device revokes its future access** by re-wrapping the vault key and rotating it on the next change. It **does not** revoke certificates with Apple, and it does not erase secrets the device already decrypted or exported.
8. **Tests required before release:**
   - cross-account access rejection;
   - encryption/decryption round trip;
   - tamper rejection (ciphertext, associated data, wrapped key);
   - wrong recovery key;
   - interrupted upload and download;
   - deletion behaviour;
   - device removal semantics.

## App Store Connect API keys (separate boundary)

App Store Connect API keys authorize App Store Connect API requests: ES256-signed JWTs with `aud` `appstoreconnect-v1`. **They are not app-signing keys and are not needed to sign locally.** This summary is from Apple's documentation, "Creating API Keys for App Store Connect API" and "Generating Tokens for API Requests":

- **Team keys:**
  - created by an Admin, with a role per key, and access to all apps according to that role;
  - the JWT uses `iss` = Issuer ID;
  - usable with notarytool.
- **Individual keys:**
  - carry the access of their user;
  - use `sub: "user"` and no Issuer ID;
  - **cannot use the Provisioning endpoints or notarytool**.
- The `.p8` private key can be downloaded **once**; Apple keeps no copy. Tokens live ≤ 20 minutes, except long-lived scoped GET tokens.

HCG keeps only references: the Key ID and Issuer ID in Settings › Apple credentials, and an optional path or keychain item name (`AppStoreConnectApiKeyReference`). `AppStoreConnectKeys.Validate` checks:
- the Key ID format;
- that team keys have an Issuer ID;
- that individual keys do not.

Nothing is uploaded automatically.

## Verification status

| What | How | Result |
|---|---|---|
| Import, duplicates, wrong password, cancel, `.cer` without key, expiry, local deletion, account merge | `SigningImportTests` (synthetic identities, in-memory keychain) | Automated, passing |
| Profile parsing, type classification, malformed/tampered/untrusted, import/remove, wildcard rules | `ProvisioningProfileTests` (CMS-signed synthetic profiles) | Automated, passing |
| Team, bundle ID, certificate, entitlement, device, platform, type mismatches; no-profile routes; multiple targets | `SigningValidationTests`, `SigningPrebuildCheckTests` | Automated, passing |
| No password in processes, messages or settings; argument validation; redaction; metadata allow-list | `SigningSecretHandlingTests` | Automated, passing |
| Account metadata server | `test/signing.test.mjs` (PostgreSQL 16), `HcgContractTests` | Automated, passing |
| UI | `hcg-ui-driver --signing` (headless Avalonia, Linux) | Renders; checks pass |
| Real keychain import/delete | `SigningKeychainIntegrationTests` with `HCG_KEYCHAIN_TESTS=1` on macOS | **Not run here** (no Mac). The CI step exists, but no CI was started for this branch. |
| Real signing with a real Apple certificate, Keychain access prompts, `codesign` with an imported key | — | **Needs your Mac.** Not exercised. |
