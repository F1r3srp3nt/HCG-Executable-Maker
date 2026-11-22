# Installing HCG Executable Maker on a Mac (Apple Silicon)

This is a **developer pre-release**: built by GitHub Actions from the tagged commit, ad hoc signed (not signed with a
Developer ID) and **not notarized**. macOS therefore asks once before opening it.

## Requirements

- A Mac with Apple Silicon (M1–M4). The download is `osx-arm64`.
- macOS 11 or later.
- To **build** projects with the app: the .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0) and, for
  iOS and macOS apps, Xcode. The app itself needs nothing installed; its Toolchains page shows what each project needs.

## Install

1. Download `HCG-Executable-Maker-<version>-osx-arm64.app.zip` (or the `.dmg`) and `SHA256SUMS.txt` from the release.
2. Check the download (optional, recommended) in Terminal, in the download folder:
   `shasum -a 256 -c SHA256SUMS.txt --ignore-missing`
3. Double-click the `.app.zip` (or open the `.dmg`) and drag **HCG Executable Maker** to **Applications**.
4. Allow it once, because it is not notarized: in Terminal run
   `xattr -cr "/Applications/HCG Executable Maker.app"`
   then right-click the app › **Open** › **Open**. (Or: System Settings › Privacy & Security › **Open Anyway**.)
5. Confirm the build: the Account page footer, or in Terminal
   `"/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker" --version`
   prints the version, the commit and the built-in HCG Connect registration.

## Upgrading from an earlier version

Projects, build history, settings and workspaces are stored outside the app, in
`~/Library/Application Support/HCG/ExecutableMaker/`; build outputs in `~/Documents/HCG Executable Maker/Output`; your
HCG sign-in in the macOS Keychain. Quit the old version, optionally copy `~/Library/Application Support/HCG/ExecutableMaker`
as a backup, replace the app in Applications, and open the new one. Its database is migrated in place.

## Building it yourself in Xcode

Open `macOS Xcode Project/HCGExecutableMaker.xcodeproj` from the same commit, choose the **HCG Executable Maker** scheme
and press Run (see `macOS Xcode Project/README.md`).
