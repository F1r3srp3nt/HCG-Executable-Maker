#!/usr/bin/env bash
# Smoke test of hcg.executable-maker.integration/1 (docs/INTEGRATION-CONTRACT.md) against a built Executable Maker.
# Usage: tools/ci/integration-contract-check.sh <path to HCGExecutableMaker executable>
set -euo pipefail
maker="$1"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
export HCG_EXECUTABLE_MAKER_HOME="$work/home" AVALONIA_TELEMETRY_OPTOUT=1
case "$(uname -s)" in Darwin) platform=macos; format=MacAppBundle;; Linux) platform=linux; format=PortableFolder;; *) platform=windows; format=PortableFolder;; esac
case "$(uname -m)" in arm64|aarch64) arch=arm64;; *) arch=x64;; esac

"$maker" --integration describe > "$work/describe.json"
python3 -c "import json,sys; d=json.load(open(sys.argv[1])); assert d['contract']=='hcg.executable-maker.integration/1', d; print('describe:', d['buildIdentity'])" "$work/describe.json"

cat > "$work/refused.json" <<JSON
{"contract":"hcg.executable-maker.integration/1","requestId":"refused","source":{"kind":"directory","path":"$repo/tests/Fixtures/HelloAvalonia"},
 "target":{"platform":"$platform","architecture":"$arch"},"identity":{"name":"Hello","id":"com.example.hello","version":"1.0.0"},
 "formats":["$format"],"outputDirectory":"$work/out","trust":{"acknowledged":false}}
JSON
set +e; "$maker" --integration build --request "$work/refused.json" --result "$work/refused-result.json" > /dev/null; code=$?; set -e
[ "$code" = 4 ] || { echo "expected exit 4 for an unacknowledged request, got $code"; exit 1; }
python3 -c "import json,sys; r=json.load(open(sys.argv[1])); assert r['status']=='Refused', r; print('refused:', r['failureReason'][:60])" "$work/refused-result.json"

sed 's/"acknowledged":false/"acknowledged":true/; s/"requestId":"refused"/"requestId":"build"/' "$work/refused.json" > "$work/build.json"
"$maker" --integration build --request "$work/build.json" --result "$work/result.json" > "$work/events.jsonl"
python3 - "$work/result.json" <<'PY'
import json, sys, hashlib, os
r = json.load(open(sys.argv[1]))
assert r["status"] == "Succeeded", r
assert r["artifacts"], r
p = r["releasePassport"]["path"]
assert hashlib.sha256(open(p, "rb").read()).hexdigest() == r["releasePassport"]["sha256"]
for a in r["artifacts"]:
    if a["kind"] == "File":
        assert hashlib.sha256(open(a["path"], "rb").read()).hexdigest() == a["sha256"], a
    print("artifact:", a["format"], a["verification"], os.path.basename(a["path"]))
print("integration contract: OK")
PY
