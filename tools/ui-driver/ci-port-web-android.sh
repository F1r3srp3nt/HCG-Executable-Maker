#!/usr/bin/env bash
# Runs inside the Android emulator (CI): the conversion workspace packages tests/Fixtures/WebViteDesktop (Vite + React)
# with Capacitor: inspect → choose Android → generate → validate (npm, web build, cap add android) → compile (debug APK
# through Mobile Build Studio's engine) → install and launch on the running emulator, which must show the app's heading.
set -euo pipefail
adb devices -l
adb shell getprop ro.build.version.release
dotnet tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll --source tests/Fixtures/WebViteDesktop --port "web:" --path WebToAndroid \
  --name "Field Notes" --bundle-id com.hakmicapital.fieldnotes --expect-capability GenerateTarget \
  --validate --expect-validate Passed --compile --expect-compile Passed --launch --expect-text "Field Notes" --expect-launch Passed \
  --output "$OUT/fieldnotes" --screenshots "$OUT/screens" --prefix web-android --window-height 3600
