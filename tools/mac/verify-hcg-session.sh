#!/bin/bash
# Verifies HCG sign-in and session persistence in the PACKAGED macOS app (HCG Executable Maker.app) against the MOCK
# HCG (tools/hcg-mock/mock-hcg.mjs): sign in, restart (new process, restored from the real login Keychain), restart
# again, check the GUI after a restart, sign out, restart. MOCK identity server, never HCG.
#
# Usage: verify-hcg-session.sh <path/to/HCG Executable Maker.app> <work dir> [window-id helper]
set -euo pipefail
APP="$1"; WORK="$2"; WINDOW_ID="${3:-}"
EXE="$APP/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist")"
SERVICE=com.hakmicapitalgroup.executablemaker.hcg
mkdir -p "$WORK/home" "$WORK/screens"
export HCG_EXECUTABLE_MAKER_HOME="$WORK/home"
fail() { echo "FAIL: $*"; exit 1; }
pass() { echo "PASS: $*"; }

echo "App: $APP"
echo "Executable: $EXE"
"$EXE" --version
codesign -dv "$APP" 2>&1 | grep -E "Identifier|Signature" || true

# 1. Mock HCG on 127.0.0.1 and an hcg-connect.json that points at it (the app labels it a local test server).
node "$(dirname "$0")/../hcg-mock/mock-hcg.mjs" "$WORK/mock.json" > "$WORK/mock.log" 2>&1 &
MOCK_PID=$!
trap 'kill $MOCK_PID 2>/dev/null || true; pkill -f "$EXE" 2>/dev/null || true' EXIT
for _ in $(seq 1 40); do [ -f "$WORK/mock.json" ] && break; sleep 0.25; done
PORT=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['port'])" "$WORK/mock.json")
python3 - "$WORK/mock.json" "$WORK/home/hcg-connect.json" <<'EOF'
import json, sys
i = json.load(open(sys.argv[1]))
iss = i["issuer"]
json.dump({"contractVersion": "hcg.executable-maker.connect/1", "clientId": i["clientId"], "issuer": iss,
           "authorizationEndpoint": iss + "/oauth/authorize", "tokenEndpoint": iss + "/oauth/token",
           "userinfoEndpoint": iss + "/oauth/userinfo", "jwksEndpoint": iss + "/.well-known/jwks.json",
           "apiBaseUrl": i["apiBaseUrl"]}, open(sys.argv[2], "w"), indent=2)
EOF
state() { curl -s "http://127.0.0.1:$PORT/__mock/state"; }
item() { security find-generic-password -s "$SERVICE" -a session >/dev/null 2>&1 && echo present || echo absent; }
security delete-generic-password -s "$SERVICE" -a session >/dev/null 2>&1 || true

run() { # $1 = label, $2 = verb, $3 = expected exit code
  echo "===== $1: \"$EXE\" --hcg-session $2 (new process)"
  set +e; "$EXE" --hcg-session "$2" | tee "$WORK/$2.out"; code=${PIPESTATUS[0]}; set -e
  echo "exit=$code"
  [ "$code" = "$3" ] || fail "$1: exit $code, expected $3"
}

run "Sign in" sign-in 0
grep -q "Credential store: macOS Keychain" "$WORK/sign-in.out" || fail "the packaged app did not use the macOS Keychain"
grep -q "HCG account: SignedIn — Signed in as founder@example.test\.$" "$WORK/sign-in.out" || fail "not signed in, or the session was not stored"
grep -q "Plan: Pro" "$WORK/sign-in.out" || fail "plan not read from the (mock) central API"
[ "$(item)" = present ] || fail "no Keychain item after sign-in"
pass "signed in through the browser flow (PKCE S256, state, nonce, ES256 ID token); session stored in the login Keychain ($SERVICE / session)"
echo "mock: $(state)"

run "Restart 1" status 0
grep -q "HCG account: SignedIn" "$WORK/status.out" || fail "restart 1: session not restored"
run "Restart 2" status 0
grep -q "HCG account: SignedIn" "$WORK/status.out" || fail "restart 2: session not restored"
S=$(state); echo "mock: $S"
# Two authorization requests: the app's preflight (asks the identity service whether it accepts the client, without a
# browser; never redeemed) and the browser's. One code exchange = one sign-in.
python3 -c "import json,sys;s=json.loads(sys.argv[1]);assert s['codeExchanges']==1 and s['refreshes']==2 and s['authorizations']==2,s" "$S" \
  || fail "expected one sign-in (preflight + browser authorization, one code exchange) and one refresh per restart"
pass "session restored after two restarts without the browser (one sign-in, one rotated refresh per start)"

# 2. The GUI of the packaged app after a restart: Account page shows the restored session and plan.
open -n "$APP" --args --page account
for _ in $(seq 1 60); do
  python3 -c "import json,sys;sys.exit(0 if json.loads(sys.argv[1])['refreshes']>=3 else 1)" "$(state)" && break
  sleep 1
done
sleep 4 # let the Account page render the restored session and plan
GUI_PID=$(pgrep -nf "$EXE" || true)
if [ -n "$WINDOW_ID" ] && [ -n "$GUI_PID" ] && WID=$("$WINDOW_ID" "$GUI_PID"); then
  screencapture -x -o -l "$WID" "$WORK/screens/hcg-session-01-account-after-restart.png" && pass "screenshot of the packaged app's Account page after a restart"
else
  echo "NOTE: no window screenshot (window not found)"
fi
S=$(state); echo "mock after GUI start: $S"
python3 -c "import json,sys;s=json.loads(sys.argv[1]);assert s['codeExchanges']==1 and s['refreshes']>=3,s" "$S" || fail "the GUI did not restore the session"
pass "the packaged GUI restored the session at start (no new sign-in)"
osascript -e 'tell application id "com.hakmicapital.executablemaker" to quit' >/dev/null 2>&1 || kill "$GUI_PID" 2>/dev/null || true
sleep 3

run "Sign out" sign-out 0
[ "$(item)" = absent ] || fail "Keychain item still present after sign-out"
run "Restart after sign-out" status 3
grep -q "HCG account: SignedOut" "$WORK/status.out" || fail "still signed in after sign-out"
pass "sign-out removed the Keychain item; the next start is signed out"
echo "SUMMARY: packaged app sign-in, restart persistence (2 restarts + GUI start) and sign-out verified against the MOCK HCG identity server and central API."
