#!/bin/bash
# Fixture helper: builds Debug and OPENS the app. HCG Executable Maker must never run this (it calls xcodebuild itself).
set -euo pipefail
cd "$(dirname "$0")"
touch "$PWD/INSTALL-COMMAND-WAS-RUN"
xcodebuild -project DeskLoop.xcodeproj -scheme DeskLoop -configuration Debug -derivedDataPath build build
open build/Build/Products/Debug/DeskLoop.app
