#!/usr/bin/env bash
# Build the host agent (Go) as the app's helper executable (same shape as HCG Server's script).
#   scripts/build-agent-helper.sh --output <path> [--arch universal|arm64|x86_64]
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=""; ARCH=universal
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output) OUT="$2"; shift 2 ;;
    --arch) ARCH="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$OUT" ]] || { echo "usage: build-agent-helper.sh --output <path>" >&2; exit 2; }
command -v go >/dev/null || { echo "error: Go is required to build the hcg-agent helper (https://go.dev/dl/, 1.25 or newer)." >&2; exit 1; }
mkdir -p "$(dirname "$OUT")"
TMP=$(mktemp -d "${TMPDIR:-/tmp}/hcg-agent-helper.XXXXXX")
trap 'rm -rf "$TMP"' EXIT
build() { CGO_ENABLED=0 GOOS=darwin GOARCH="$1" go build -trimpath -o "$2" ./cmd/hcg-agent; }
case "$ARCH" in
  universal)
    command -v lipo >/dev/null || { echo "error: lipo is required for a universal helper (install Xcode or run xcode-select --install)." >&2; exit 1; }
    build arm64 "$TMP/arm64"; build amd64 "$TMP/amd64"
    lipo -create -output "$OUT" "$TMP/arm64" "$TMP/amd64" ;;
  arm64) build arm64 "$OUT" ;;
  x86_64) build amd64 "$OUT" ;;
  *) echo "unknown --arch $ARCH" >&2; exit 2 ;;
esac
chmod 755 "$OUT"
echo "built helper $OUT ($ARCH) with $(go version)"
