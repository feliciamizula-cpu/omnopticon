#!/usr/bin/env python3
"""Patch aspirate-generated Deployment manifests with nodeSelector for GKE pool routing.

Run from repo root after `aspirate generate` completes.
Workers go to argus-workers pool; everything else goes to argus-core.
"""
from pathlib import Path
import yaml

MANIFEST_DIR = Path("src/Argus.AppHost/aspirate-output")

WORKERS = {
    "amass-worker",
    "asset-scoring-worker",
    "asset-storage-worker",
    "dns-resolver-worker",
    "finding-deduper-worker",
    "fingerprint-worker",
    "headless-spider-worker",
    "html-dom-spider-worker",
    "http-probe-worker",
    "http-worker",
    "js-extractor-worker",
    "regex-scanner-worker",
    "subfinder-worker",
    "validation-worker",
    "wordlist-discovery-worker",
}


def patch_file(path: Path) -> bool:
    content = path.read_text()
    docs = list(yaml.safe_load_all(content))
    modified = False

    for doc in docs:
        if not isinstance(doc, dict) or doc.get("kind") != "Deployment":
            continue

        name = doc.get("metadata", {}).get("name", "")
        pool = "argus-workers" if name in WORKERS else "argus-core"
        selector = {"cloud.google.com/gke-nodepool": pool}

        pod_spec = doc.setdefault("spec", {}).setdefault("template", {}).setdefault("spec", {})
        if pod_spec.get("nodeSelector") != selector:
            pod_spec["nodeSelector"] = selector
            modified = True

    if modified:
        with open(path, "w") as f:
            yaml.dump_all(docs, f, default_flow_style=False, allow_unicode=True)
        print(f"  patched: {path}")

    return modified


def main():
    if not MANIFEST_DIR.exists():
        print(f"Manifest directory not found: {MANIFEST_DIR}")
        raise SystemExit(1)

    total = 0
    for yaml_file in sorted(MANIFEST_DIR.rglob("*.yaml")):
        if "kustomization" in yaml_file.name:
            continue
        if patch_file(yaml_file):
            total += 1

    print(f"Node selectors patched in {total} file(s).")


if __name__ == "__main__":
    main()
