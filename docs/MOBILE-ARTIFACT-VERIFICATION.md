# Mobile artifact verification

Every mobile artifact HCG produces — and any `.apk`, `.aab`, `.apks`, `.ipa`, `.xcarchive` or `.app` you choose with
*Verify an .apk / .aab / .ipa…* — is inspected **without executing or extracting it**. Results are stored with the build
in history (Artifacts and Build History pages).

> Passing these checks means the package is structurally sound and tells you how it is signed. It does **not** mean
> Google Play or the App Store will accept it; store review is a separate process.

## Static checks (all hosts)

| Check | APK | AAB | APK set | IPA | xcarchive / .app |
|---|---|---|---|---|---|
| Archive integrity: every entry decompressed, CRC-32 recomputed; traversal/absolute names rejected; 8 GB / 200 000-entry limits | ✓ | ✓ | ✓ | ✓ | — |
| Manifest: binary XML (APK) or aapt2 protobuf (AAB) decoded with bounds checks — package, versionCode/Name, min/targetSdk, `debuggable` | ✓ | ✓ | — | — | — |
| Code present (`classes*.dex`, `base/dex/`) | ✓ | ✓ | — | — | — |
| `BundleConfig.pb`, modules | — | ✓ | — | — | — |
| ABIs (`lib/<abi>/`) | ✓ | ✓ | — | — | — |
| Signature schemes (v1 JAR, APK Signing Block v2/v3/v3.1) and signer certificate (debug vs release) | ✓ | v1 | — | — | — |
| `Payload/<App>.app`, Info.plist (XML or binary): bundle ID, version, build, minimum iOS | — | — | — | ✓ | ✓ |
| Executable: Mach-O thin/universal architectures | — | — | — | ✓ | ✓ |
| Platform: device vs **Simulator** (Simulator builds are flagged as not distributable) | — | — | — | ✓ | ✓ |
| Code signature presence, embedded provisioning profile (team, development/ad hoc/enterprise/App Store, expiry) | — | — | — | ✓ | ✓ |
| Exportability of an archive (unsigned archives cannot become IPAs) | — | — | — | — | ✓ |
| SHA-256 checksum (file, or per-file manifest for bundles) | ✓ | ✓ | ✓ | ✓ | ✓ |

The signing state is one of: *Unsigned*, *DebugSigned*, *ReleaseSigned* (Android), *AdHocSigned*,
*DevelopmentSigned*, *DistributionSigned* (iOS). An artifact is marked *distributable* only when it is release- or
distribution-signed (and, on Android, not debuggable).

Signature checks establish **presence and signer identity**; cryptographic validity is checked by the official tools:

## Tool-backed checks (when installed)

| Tool | Used for |
|---|---|
| `apksigner verify --print-certs --verbose` (Android build-tools, run as `java -jar lib/apksigner.jar`) | Cryptographic verification of signed APKs; reports the verified schemes |
| `bundletool validate --bundle=…` | Structural validation of App Bundles |
| `codesign --verify --deep --strict` (macOS) | Code-signature validity of `.app` bundles and archived apps |

## Hardening

Parsers are bounds-checked and depth-limited (binary XML, protobuf, binary/XML plists without DTD processing, PKCS#7
via `System.Formats.Asn1` — never a content-sniffing certificate loader), entries are read with size caps, and nothing
is written to disk during inspection. Corrupt input produces a failed *Structure* check instead of an exception. All of
this is covered by synthetic-artifact tests (`MobileArtifactTests`), including corrupt ZIPs, CRC mismatches,
traversal entry names, truncated manifests, fat Mach-O binaries, expired profiles and a cross-check against a real
`jarsigner` signature.
