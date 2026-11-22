# HCG Mail current (fixture)

The build files of F1r3srp3nt/HCG-Mail at `13d93a8` (14 Nov 2026), copied unchanged: the iOS head resolves its shared
projects through `HcgSharedDir` (conditional properties + `$([MSBuild]::NormalizeDirectory(…))`) and imports guarded by
`HcgSharedRootFound`, and the iOS toolchain pins live in `iOS App Store Submission/toolchain.props`. Source files are not
included: the fixture is for project evaluation, reference resolution and Build Doctor, not for compiling.
