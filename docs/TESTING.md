# Testing HCG Executable Maker 0.1.0

## Get a build

Ready-to-run builds are produced by HCG Executable Maker itself (it imports this repository, inspects it, and builds
and packages itself through its own engine):

```bash
dotnet run tools/self-package/self-package.cs -- . ../hcg-self-builds
```

This creates, under `../hcg-self-builds/` (keep it outside the repository, which is what gets imported):

| Folder | Contents |
|---|---|
| `HCG Executable Maker-0.1.0-osx-arm64/` | `HCG Executable Maker.app` + `…app.zip` (Apple Silicon Macs) |
| `HCG Executable Maker-0.1.0-osx-x64/` | `HCG Executable Maker.app` + `…app.zip` (Intel Macs) |
| `HCG Executable Maker-0.1.0-win-x64/` | `…win-x64.zip` (Windows 10/11, x64) |

Each folder includes `SHA256SUMS.txt`. Transfer the **ZIP** files (they preserve macOS executable permissions).

## Prerequisite on the test machine

Install the **.NET 10 SDK** (https://dotnet.microsoft.com/download/dotnet/10.0). The app runs without it, but needs it
to build the projects you import. Without it, Toolchains shows *.NET SDK — Missing* and builds are blocked.

## First launch

**macOS** — the app is unsigned and not notarized (planned for M0.4), so Gatekeeper blocks it the first time:

```bash
xattr -cr "/Applications/HCG Executable Maker.app"
```

Then right-click the app → **Open**. If it is still blocked: System Settings → Privacy & Security → **Open Anyway**.

**Windows** — Extract All, then run `HCGExecutableMaker.exe`. If SmartScreen appears: **More info → Run anyway**.
The `.exe` file icon is generic when built on a non-Windows host; the window icon shows the logo.

## Test script (≈10 minutes)

Use `tests/Fixtures/HelloAvalonia` zipped (or any trusted .NET desktop project).

1. **Toolchains** — .NET SDK *Installed*. macOS: Xcode CLT / codesign / hdiutil detected. Windows: Windows SDK / signtool.
2. **New Build → Import ZIP archive** — choose the test project ZIP.
3. **Inspect** — expect *Avalonia desktop application · Supported*.
4. **Configure** — choose your own platform so you can run the result locally.
5. **Package** — macOS: tick **.dmg** (first real-hardware test of DMG creation). Windows: **Portable ZIP**.
6. **Build** — tick the trust box, start, watch the live log.
7. **Results** — Open Output Folder, launch the built app (window: *Built by HCG Executable Maker*).
8. Also try: **Cancel** during a build, **Build History → Repeat Build**, **Artifacts → Export**, **Settings → theme**.

## Report back

Screenshots of anything broken or unpolished, plus any error text. Logs are in Settings → Data locations → *Open Logs
Folder*; each build's full log is available from Build History → *View Log*.
