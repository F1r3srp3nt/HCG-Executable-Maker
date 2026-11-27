# AgentHelperApp — HCG Server-shaped Build Readiness fixture

A small SwiftPM macOS app that ships a Go helper, laid out like HCG Server (which CI cannot clone: it is private):

- `go.mod` at the root (`go 1.25.0`), the agent in `cmd/hcg-agent`.
- `scripts/build-agent-helper.sh`: builds the agent with `CGO_ENABLED=0` for arm64 and amd64 and joins them with `lipo`
  (the same shape as HCG Server's script; it checks `command -v go` and `command -v lipo`).
- `macos/AgentHelperApp/AppPackaging.json`: requires `go >= 1.25`, runs the script from the repository root and places
  the universal helper at `Contents/Helpers/hcg-agent` (`verify: ["version"]`).
- The app runs `Contents/Helpers/hcg-agent version` when it launches and writes the result to
  `$TMPDIR/hcg-agent-helper-probe.txt`, so CI can prove the packaged app found and ran its helper.
