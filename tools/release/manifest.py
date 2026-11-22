"""Build manifest for one platform's distributable files (used by the Package job; published with a release).

Usage: manifest.py <dist dir> <version> <commit> <platform> <run id> <run number>
Lists every file with its size and SHA-256 and states what the files are and are not (signing, notarization).
"""
import hashlib
import json
import os
import sys
from datetime import datetime, timezone

dist, version, commit, platform, run_id, run_number = sys.argv[1:7]

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()

files = []
for name in sorted(os.listdir(dist)):
    path = os.path.join(dist, name)
    if os.path.isfile(path) and not name.startswith(("build-manifest-", "SHA256SUMS")):
        files.append({"name": name, "bytes": os.path.getsize(path), "sha256": sha256(path)})

print(json.dumps({
    "product": "HCG Executable Maker",
    "bundleIdentifier": "com.hakmicapital.executablemaker",
    "version": version,
    "commit": commit,
    "platform": platform,
    "builtAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "builtBy": f"GitHub Actions run {run_number} ({run_id}): tools/self-package/self-package.cs",
    "signing": {
        "macos": "ad hoc (the .NET SDK's signature on the app host); not signed with a Developer ID; not notarized",
        "windows": "not Authenticode-signed",
        "linux": "not signed",
    }.get(platform, "not signed"),
    "files": files,
}, indent=2))
