# Development environment report (M0.1)

Captured at the start of Milestone 0.1 development, 2026-10-31.

| Item | Finding |
|---|---|
| Operating system | Ubuntu 24.04.5 LTS (Linux 6.18, x86_64), 4 vCPU, 15 GB RAM — a cloud development container, **not** a macOS or Windows machine |
| Repository | `F1r3srp3nt/HCG-Executable-Maker` already existed (remote `origin`), branch `main`, single "Initial commit" with a README only. No unrelated code; no other HCG product touched. Work happens on `feature/m0.1-foundation`. |
| .NET SDK | None preinstalled. `builds.dotnet.microsoft.com` is blocked by the network policy; **.NET SDK 10.0.114** was installed from Ubuntu's archive (`dotnet-sdk-10.0`, source-built). |
| NuGet | `api.nuget.org` reachable (package restore works); the NuGet *search* endpoints are blocked, so `dotnet new install` needs an explicit `.nupkg`. |
| UI framework | Avalonia **12.1.4** (current stable), CommunityToolkit.Mvvm 8.4.2, SkiaSharp 3.119.4 |
| Other tools | Git 2.43.0, ImageMagick, Xvfb (used to run and screenshot the real UI). No Xcode, codesign, hdiutil, signtool, WiX or Inno Setup (not applicable on Linux). |

## Compatibility findings that shaped M0.1

1. **Cross-RID publishing works from Linux.** The source-built SDK downloads runtime packs from NuGet and produces
   real `win-x64` PE32+ and `osx-arm64` Mach-O app hosts.
2. **macOS arm64 app hosts are ad-hoc signed by the SDK even off-Mac** (`LC_CODE_SIGNATURE` present). Without this,
   Apple Silicon refuses to run a binary at all. HCG verifies it and reports it — it is *not* a Developer ID signature.
3. **Unix permissions must be carried in ZIPs.** Directories copied via Windows lose the executable bit; HCG writes ZIPs
   with explicit modes and Unix "made-by" headers so `.app` bundles survive the trip to a Mac.
4. **Windows `.exe` icon embedding requires a Windows build host** (the SDK copies Win32 resources into the app host only
   on Windows). Cross-built Windows apps still get a generated `.ico`, and the UI says so.
5. **DMG creation and launch testing require macOS.** These are reported as *Needs macOS host* / *Not verifiable here*.

## What could not be verified on this host

- Running `HCGExecutableMaker` on real macOS and Windows desktops (it was run and exercised on Linux/Xvfb; macOS and
  Windows self-contained publishes of the app were produced and include all native libraries).
- `DmgPackager` (needs `hdiutil`), Windows/macOS toolchain probes, launching generated `.app`/`.exe` artifacts.
