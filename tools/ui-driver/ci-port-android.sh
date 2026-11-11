#!/usr/bin/env bash
# Runs inside the Android emulator (CI): Porting Studio ports tests/Fixtures/LinuxPrimes, compiles the debug APK with the
# NDK through Mobile Build Studio's engine, then installs and launches it on the running emulator.
set -euo pipefail
adb devices -l
adb shell getprop ro.build.version.release
dotnet tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll --source tests/Fixtures/LinuxPrimes --port "cmake::primes" \
  --name "Primes" --bundle-id com.hakmicapital.primes --args "-n 100000 -l -t 2" \
  --expect-capability GenerateTarget --compile --expect-compile Passed --launch --expect-launch Passed \
  --output "$OUT/primes" --screenshots "$OUT/screens" --prefix primes-android --window-height 3600
