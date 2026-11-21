#!/usr/bin/env python3
"""HCG Repo importer dry-run on the exact bytes of a Maker Candidate handoff.

Copies an HCG Repo checkout to a temporary folder, adds what the handoff proposes for the repository
(packages/<id>/package.toml, CHANGELOG.md and icon.png, and imports/<name>.toml), then runs HCG Repo's own
importer: load_request() checks the import request's format, and import_deb(dry_run=True) validates the .deb
together with the existing pool, pinned to the request's package, version, architecture and SHA-256.

Nothing is written to the HCG Repo checkout, nothing is fetched, and nothing is published: the dry run stops
before the pool, the lock file and the provenance file. Publication stays a manual step in HCG Repo.

Usage: PYTHONPATH=<hcg-repo>/tools python3 hcgrepo-dry-run.py --repo <hcg-repo> --handoff <folder>
       [--deb <the .deb to import, default the handoff's>] [--product-key hcg-sentinel] [--hypothetical-approval] [--report out.json]

HCG Repo Candidate serves only public builds, and an HCG package is public only by the owner's recorded approval of that
version ([access] exception + exception_versions, HCG Repo docs/ACCESS.md). Maker never writes an approval it was not
given, so a handoff without one validates but is "not yet importable"; --hypothetical-approval runs HCG Repo's full
public-pool and candidate checks on the same bytes with a labelled placeholder that exists only in the throwaway copy.
"""
import argparse
import hashlib
import json
import shutil
import sys
import tempfile
import tomllib
from pathlib import Path


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--repo", required=True, type=Path, help="HCG Repo checkout (left unchanged)")
    ap.add_argument("--handoff", required=True, type=Path, help="Maker's Candidate handoff folder")
    ap.add_argument("--deb", type=Path, help="the exact .deb to import (default: the one in the handoff)")
    ap.add_argument("--product-key", help="config/products.toml product the package belongs to, if HCG Repo does not list it yet")
    ap.add_argument("--hypothetical-approval", action="store_true",
                    help="also run with a labelled, hypothetical [access] exception in the throwaway copy (never an approval)")
    ap.add_argument("--report", type=Path, help="write the result as JSON")
    a = ap.parse_args()

    handoff = a.handoff.resolve()
    if (handoff / "import-request.INCOMPLETE.toml").exists() and not (handoff / "import-request.toml").exists():
        print("REFUSED: the handoff's import request is incomplete (see its README): it has no CI run, artifact or commit to pin.")
        return 2
    debs = sorted(handoff.glob("*.deb"))
    if len(debs) != 1:
        print(f"REFUSED: the handoff must hold exactly one .deb, found {len(debs)}")
        return 2
    deb = (a.deb or debs[0]).resolve()
    request_text = (handoff / "import-request.toml").read_text(encoding="utf-8")
    request = tomllib.loads(request_text)
    req, src = request["request"], request["source"]
    actual = sha256(deb)
    sums = (handoff / "SHA256SUMS").read_text(encoding="utf-8").split()
    print(f"package      {req['package']} {req['version']} {req['architecture']} (channel {req.get('channel', 'stable')})")
    print(f"source       {src['kind']} {src['repository']} run {src.get('run_id')} artifact {src.get('artifact')} commit {src['commit']}")
    print(f"deb          {deb} ({deb.stat().st_size} bytes)")
    print(f"sha256       {actual}")
    problems = []
    if actual != src.get("sha256", "").lower():
        problems.append(f"the .deb's SHA-256 {actual} differs from the import request's {src.get('sha256')}")
    if sums[0] != actual:
        problems.append(f"the .deb's SHA-256 differs from the handoff's SHA256SUMS ({sums[0]})")
    if deb != debs[0].resolve() and sha256(debs[0]) != actual:
        problems.append("the .deb given differs from the handoff's .deb")

    from hcgrepo import importer  # noqa: E402 (from the HCG Repo checkout on PYTHONPATH)
    from hcgrepo.config import load_config, load_lock, load_package_meta

    def run(hypothetical_approval: bool) -> dict:
        with tempfile.TemporaryDirectory(prefix="hcgrepo-dry-run-") as tmp:
            root = Path(tmp) / "repo"
            shutil.copytree(a.repo, root, ignore=shutil.ignore_patterns(".git", "__pycache__", "public", "dist"))
            pkg_dir = root / "packages" / req["package"]
            existing = pkg_dir.exists()
            pkg_dir.mkdir(parents=True, exist_ok=True)
            added = []
            for name in ("package.toml", "CHANGELOG.md", "icon.png"):
                if (handoff / name).exists() and not (existing and (pkg_dir / name).exists()):
                    shutil.copyfile(handoff / name, pkg_dir / name)
                    added.append(f"packages/{req['package']}/{name}")
            if hypothetical_approval:
                # Only in this throwaway copy, clearly labelled: lets HCG Repo run its full public-pool and candidate checks
                # on the same bytes. It is not an approval and is never written to the handoff or to HCG Repo.
                toml = (pkg_dir / "package.toml").read_text(encoding="utf-8")
                lines = [ln for ln in toml.splitlines() if not ln.startswith(("exception =", "exception_versions ="))]
                at = lines.index("[access]") + 1
                while at < len(lines) and (lines[at].startswith("#") or lines[at].startswith("level")):
                    at += 1
                lines[at:at] = ['exception = "HYPOTHETICAL APPROVAL FOR A DRY RUN ONLY: not an owner approval; never imported or published."',
                                f'exception_versions = ["{req["version"]}"]']
                (pkg_dir / "package.toml").write_text("\n".join(lines) + "\n", encoding="utf-8")
                added.append(f"packages/{req['package']}/package.toml: [access] exception (HYPOTHETICAL, this dry run only)")
            (root / "imports").mkdir(exist_ok=True)
            req_name = f"{req['package']}-{req['version'].replace('~', '-').replace(':', '-')}.toml"
            (root / "imports" / req_name).write_text(request_text, encoding="utf-8")
            added.append(f"imports/{req_name}")
            if a.product_key:
                products = root / "config" / "products.toml"
                text = products.read_text(encoding="utf-8")
                prod = next((p for p in tomllib.loads(text).get("products", []) if p.get("key") == a.product_key), None)
                if prod is None:
                    problems.append(f"config/products.toml has no product {a.product_key!r}")
                elif req["package"] not in prod.get("package_ids", []):
                    line = text.index("package_ids", text.index(f'key = "{a.product_key}"'))
                    ids = prod.get("package_ids", []) + [req["package"]]
                    text = text[:line] + "package_ids = [" + ", ".join(json.dumps(i) for i in ids) + "]" + text[text.index("\n", line):]
                    products.write_text(text, encoding="utf-8")
                    added.append(f"config/products.toml: {a.product_key} package_ids += {req['package']}")

            loaded = importer.load_request(root / "imports" / req_name)  # HCG Repo's own format check
            cfg = load_config(root)
            access = load_package_meta(cfg, req["package"]).access_for(req["version"])
            lock_before = json.dumps(load_lock(cfg), sort_keys=True)
            expect = {"package": req["package"], "version": req["version"], "architecture": req["architecture"], "sha256": src["sha256"]}
            provenance = {"source": "maker-candidate-handoff (dry run)", "repository": src["repository"], "commit": src["commit"],
                          "run_id": src.get("run_id"), "artifact_name": src.get("artifact"), "request": loaded["id"]}
            status, rep = importer.import_deb(cfg, deb, provenance, dry_run=True, expect=expect, channel=req.get("channel", "stable"))
            written = json.dumps(load_lock(cfg), sort_keys=True) != lock_before or any(root.rglob(deb.name))
            channel = req.get("channel", "stable")
            servable = access == "public" or channel == "stable"
            return {"hypothetical_approval": hypothetical_approval, "status": status, "access": access, "channel": channel,
                    "bytes_validated": status == "validated", "servable_by_channel": servable, "files_written": written,
                    "request_format": f"{loaded['id']}: accepted by load_request()", "proposed_files": added,
                    "report": [{"level": lvl, "code": code, "message": msg} for lvl, code, msg in rep.items], "render": rep.render()}

    results = [run(False)]
    if a.hypothetical_approval:
        results.append(run(True))
    for r in results:
        title = "WITH A HYPOTHETICAL APPROVAL (dry-run copy only)" if r["hypothetical_approval"] else "AS EXPORTED"
        print(f"--- {title}")
        print("proposed     " + "\n             ".join(r["proposed_files"]))
        print(f"request      {r['request_format']}")
        print(f"importer     import_deb(dry_run=True, expect=package/version/architecture/sha256, channel={r['channel']}) → {r['status']}")
        print(f"access       HCG Repo would apply access '{r['access']}' to {req['version']}"
              + ("" if r["servable_by_channel"] else f": HCG Repo Candidate serves only public builds, so this version would be recorded as protected, not offered in Candidate, until the owner's approval is recorded"))
        if r["render"]:
            print(r["render"])
        if r["files_written"]:
            problems.append("the dry run wrote to the pool or the lock file")
    exported, hypo = results[0], (results[1] if len(results) > 1 else None)
    result = {
        "package": req["package"], "version": req["version"], "architecture": req["architecture"], "channel": req.get("channel", "stable"),
        "sha256": actual, "size": deb.stat().st_size, "source": src, "problems": problems,
        "bytes_validated": exported["bytes_validated"] and (hypo is None or hypo["bytes_validated"]),
        "importable_as_exported": exported["bytes_validated"] and exported["servable_by_channel"],
        "blocked_only_by_owner_approval": exported["bytes_validated"] and not exported["servable_by_channel"] and hypo is not None and hypo["bytes_validated"],
        "runs": [{k: v for k, v in r.items() if k != "render"} for r in results],
        "hcg_repo_files_written": False,
    }
    for p in problems:
        print("PROBLEM      " + p)
    if problems:
        verdict = "REJECTED"
    elif result["importable_as_exported"]:
        verdict = "VALIDATED: HCG Repo's importer accepts these exact bytes for the request's channel (dry run; nothing imported or published)"
    elif result["blocked_only_by_owner_approval"]:
        verdict = ("BYTES VALIDATED, NOT YET IMPORTABLE: HCG Repo's importer accepts these exact bytes and the request; the one thing "
                   "missing is the owner's approval of this version as a free public candidate demo (package.toml [access] exception)")
    else:
        verdict = "REJECTED"
    print("RESULT       " + verdict)
    result["verdict"] = verdict
    if a.report:
        a.report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return 0 if not verdict.startswith("REJECTED") else 1


if __name__ == "__main__":
    sys.exit(main())
