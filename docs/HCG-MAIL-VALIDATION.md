# First real-world validation: HCG Mail

HCG Mail is the first production-oriented project to be packaged with HCG Executable Maker. **The HCG Mail repository
is never modified by this process and was not accessed during M0.1.** This document is the prepared workflow.

## Targets (each validated independently)

| Target | Possible today (M0.1)? | Needs |
|---|---|---|
| Windows direct distribution — portable self-contained ZIP | ✅ yes (from any host) | — |
| Windows direct distribution — installer (.exe/.msi) | ❌ | M0.2 (Inno Setup / WiX), Authenticode signing (M0.4) |
| Microsoft Store (MSIX) | ❌ | M0.2 packaging + M0.4 signing, Partner Center account |
| macOS direct distribution — `.app` (+ `.dmg` on a Mac) | ✅ unsigned (.NET) | Developer ID signing + notarization are implemented for Swift apps only (docs/MACOS-DISTRIBUTION.md) |
| Mac App Store | ❌ (.NET) | Incompatible for .NET apps in this version; Swift apps: see docs/MACOS-DISTRIBUTION.md |

## Procedure (M0.1)

1. **Obtain a trusted copy.** Export a clean copy of HCG Mail at a known commit (e.g. `git archive --format=zip
   -o hcg-mail-<commit>.zip <commit>`). Record the commit hash. Do not point HCG Executable Maker at the live working
   copy — it imports a snapshot anyway, but the exported ZIP gives a reproducible input.
2. **Check prerequisites** on the build machine: Toolchains page shows a .NET SDK that satisfies HCG Mail's target
   framework and any `global.json`.
3. **Import** the ZIP (New Build → Import ZIP archive). Confirm no import warnings that indicate missing files.
4. **Inspect.** Verify the detected application project is the HCG Mail desktop app (not a library/test project), the
   framework and Avalonia version are as expected, and *Missing requirements* is empty. Resolve anything flagged
   (e.g. private NuGet feeds — credentials are intentionally **not** passed to builds; see below).
5. **Configure:** Application name `HCG Mail`, publisher `Hakmi Capital Group`, version = release version, identifier
   e.g. `com.hakmicapital.hcgmail` (must stay stable forever once shipped), the official HCG Mail icon (1024×1024 PNG).
6. **Build per target:**
   - macOS: `macOS` + `ARM64` (and separately `x64` if Intel Macs are supported) → `.app bundle` + `ZIP`; on a Mac also `.dmg`.
   - Windows: `Windows` + `x64` (and `ARM64` if desired) → `Portable ZIP`. Build on a Windows host to embed the `.exe` icon.
7. **Verify:** Results page shows no failed checks; record SHA-256 values (also in `SHA256SUMS.txt`); keep the build log.
8. **Manual launch test** on real target machines (HCG never launches built apps). On macOS, an unsigned app needs
   right-click → Open the first time — expected until M0.4.

## Known risks to check with HCG Mail specifically

- **Private package feeds / secrets:** if restore needs authenticated feeds, the scrubbed build environment will make it
  fail. M0.1 does not pass credentials into untrusted builds by design; a vetted, explicit mechanism is planned.
- **Native dependencies** (e.g. platform crypto, keychain/credential-store access, WebView): confirm they are included
  in the self-contained output for each RID.
- **App identity**: the bundle identifier must match any existing entitlements, keychain groups or Store records.
