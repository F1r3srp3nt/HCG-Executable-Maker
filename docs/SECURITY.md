# Security model

## Trust boundary

| Phase | Executes project code? | Notes |
|---|---|---|
| Import | **No** | Files are copied/extracted only. |
| Inspection | **No** | XML/JSON/text parsing; DTDs and external entities are disabled (XXE-safe). |
| Build (restore/build/publish) | **Yes** | MSBuild targets, analyzers, source generators and package scripts from the project run with the current user's permissions. **Not sandboxed in M0.1.** |
| Packaging / verification | **No** | Files are copied, zipped and inspected. Built apps are never launched automatically. |

Before every build the user must tick *"I trust this project's source…"*. `BuildService` refuses to run without it
(enforced in the engine, not only in the UI). The UI and logs state the isolation level verbatim
(`Local execution — not sandboxed. Trusted projects only.`). `IBuildWorker` is the seam for a future isolated worker.

## Import hardening (`Infrastructure/Import/ProjectImporter.cs`)

- Corrupt/truncated archives are rejected (`InvalidDataException` → `ImportRejectedException`).
- Entry names are validated host-independently: absolute paths, drive letters, UNC prefixes, `..` segments,
  backslash traversal, NUL/control characters, reserved characters and trailing dots/spaces are rejected.
- Symbolic-link entries and other special Unix file types are rejected.
- Duplicate entries (case-insensitive) are rejected rather than overwriting.
- Limits (configurable in Settings): total bytes, per-file bytes, file count, compression ratio. Limits are enforced on
  **bytes actually written**, not on attacker-controlled header values.
- Extraction targets a fresh staging directory, uses `FileMode.CreateNew`, and is committed atomically; on any failure
  or cancellation the staging directory is deleted. setuid/setgid/sticky bits are never applied.
- Folder imports copy a snapshot (excluding `bin`, `obj`, `.git`, …); symlinks pointing outside the project are
  rejected and reported; HCG's own data directory cannot be imported.
- The original archive or folder is never modified. Removing a project deletes only the HCG workspace, and refuses if
  the path is not inside HCG's workspace directory or overlaps the original source.

## Secrets

- `BuildEnvironmentPolicy` builds the environment for untrusted processes: variables whose names look like secrets
  (`*TOKEN*`, `*SECRET*`, `*PASSWORD*`, `*_KEY`, `*CREDENTIAL*`, `*CERT*`, `*SIGNING*`, `*NOTARY*`, …) or carry
  cloud/CI/vendor prefixes (`HCG_`, `AWS_`, `AZURE_`, `GITHUB_`, `NUGET_`, `APPLE_`, `SSH_`, …) are removed. Only the
  **names** of removed variables are logged, never values.
- The SQLite database stores metadata only; a test asserts the schema has no password/key/secret/certificate/token
  columns.
- Nothing is uploaded anywhere. There is no telemetry; .NET CLI and Avalonia build telemetry are disabled for builds.

## Signing and notarization

Not implemented in M0.1 (planned M0.4). The Signing & Security page detects tool availability (signtool, MakeAppx,
codesign, notarytool, productbuild) and labels every capability *Not implemented*. Artifacts are reported as unsigned;
macOS app hosts carry only the ad-hoc signature the .NET SDK applies (required for Apple Silicon), which the verifier
reports as *not Developer ID signed and not notarized*. HCG never creates certificates or identities, and future signing
will require explicit founder approval per operation, with credentials kept in the OS keychain/certificate store —
never in HCG's database or passed into an untrusted build.

## Mobile builds (M0.5)

- **Same trust boundary.** Gradle wrappers and build scripts, Xcode run-script phases, Flutter/Dart hooks and MSBuild
  targets run with the user's permissions; Mobile Build Studio asks for trust confirmation before **every** build
  and the CLI requires `--trust`. The Gradle wrapper's distribution URL and wrapper-jar SHA-256 are logged; insecure or
  unofficial distribution hosts and missing `distributionSha256Sum` are flagged.
- **No shell, no interpolation.** Every tool (java + wrapper jar, xcodebuild, bundletool, flutter, dotnet, keytool) is
  started with an argument list. Variants, schemes, configurations, team IDs and profile names are validated.
- **No credential harvesting.** Secret-like variables are removed; `GRADLE_USER_HOME` and `ANDROID_USER_HOME` point to
  HCG-managed folders so `~/.gradle/gradle.properties` and `~/.android` keystores are not read; proxy settings are
  passed as host/port only.
- **Signing.** HCG never creates production keystores, certificates or provisioning profiles and never asks for Apple
  ID or keystore passwords. The only key HCG creates is a local Android *debug* keystore (public conventional
  password, stored under HCG's data folder, never in a repository) used solely when the user picks *HCG local debug
  key* or AAB → APK. Password-like arguments are masked in logs. Xcode's `-allowProvisioningUpdates` is off unless
  explicitly enabled. IPA export of an archive without an Apple certificate is refused; `ExportOptions.plist` always
  uses `destination=export` and is deleted after use.
- **No uploads.** Nothing is sent to Google Play, TestFlight or App Store Connect.
- **Artifact inspection** never executes or extracts artifacts (bounded, depth-limited parsers; traversal names,
  CRC errors and decompression bombs are detected). Imported artifacts must be real ZIP packages.

## Known limitations

- Mobile builds are local and not isolated, exactly like desktop builds.

- Local builds are not isolated. Treat "Start Build" like running the project's own build script.
- The secret filter is name-based; a secret stored under an innocuous variable name would not be detected. Prefer
  launching HCG from a clean session when building third-party code.
