HCG Nexus' layout with both build routes in one folder: the Theos tweak (root Makefile with `HCG_ENV ?= rootless` and
its `ifeq ($(HCG_ENV),rootless)` / `else ifeq ($(HCG_ENV),rootful)` branches, `TWEAK_NAME = HCGNexus`,
`SUBPROJECTS += Prefs`, `control`, the separately packaged `PreviewHarness/`) next to an iOS app project
`HCGNexus.xcodeproj` (`App/`). The Theos files are the same as `../NexusLike` (Makefiles verbatim from HCG-Nexus); the
Xcode project is a minimal SwiftUI app target. Sources are stubs: these tests inspect and plan, they never compile.
