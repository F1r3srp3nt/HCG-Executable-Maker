# SigningProbe — Apple Signing Assistant fixture (Unimatch shape)

A minimal SwiftUI iOS app laid out like Unimatch:

- `CODE_SIGN_STYLE = Automatic`.
- No team committed (`Config/Base.xcconfig`: `DEVELOPMENT_TEAM =`), with `#include? "Local.xcconfig"` for each
  developer's own settings.
- The bundle identifier comes from a variable: `PRODUCT_BUNDLE_IDENTIFIER = $(PROBE_BUNDLE_ID)`.
- A Development Run compiled only into Debug builds:
  `SWIFT_ACTIVE_COMPILATION_CONDITIONS[config=Debug] += PROBE_DEVELOPMENT_RUN`.
  Its marker text `SIGNINGPROBE-DEVELOPMENT-RUN` must be in Debug binaries and never in Release ones.

Building this project for a device with plain `xcodebuild` fails with "Signing for "SigningProbe" requires a development
team", the error Unimatch showed. CI uses it to check that HCG Executable Maker:

1. builds the Simulator Debug app without any team or profile;
2. stops a physical-device Debug build before compiling, with Resolve Signing, when no team is set;
3. refuses a Debug distribution archive;
4. archives Release without the Development Run.
