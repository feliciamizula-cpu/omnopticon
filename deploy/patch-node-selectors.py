#!/usr/bin/env python3
"""Patch aspirate-generated Deployment manifests for GKE staging deploys.

Run from repo root after `aspirate generate` completes.
Workers go to argus-workers pool; everything else goes to argus-core.
"""
import os
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

        template = doc.setdefault("spec", {}).setdefault("template", {})
        metadata = template.setdefault("metadata", {})
        annotations = metadata.setdefault("annotations", {})
        deploy_sha = os.environ.get("DEPLOY_SHA")
        if deploy_sha and annotations.get("argus.dev/deploy-sha") != deploy_sha:
            annotations["argus.dev/deploy-sha"] = deploy_sha
            modified = True

        pod_spec = template.setdefault("spec", {})
        if pod_spec.get("nodeSelector") != selector:
            pod_spec["nodeSelector"] = selector
            modified = True

        # Worker pool has a NoSchedule taint for argus/workload=worker so that
        # only opted-in workloads (with the matching toleration) can land
        # there. Without it spot nodes would attract no pods.
        if pool == "argus-workers":
            tolerations = pod_spec.setdefault("tolerations", [])
            toleration = {
                "key":      "argus/workload",
                "operator": "Equal",
                "value":    "worker",
                "effect":   "NoSchedule",
            }
            if toleration not in tolerations:
                tolerations.append(toleration)
                modified = True

        for container in pod_spec.get("containers", []):
            if container.get("imagePullPolicy") != "Always":
                container["imagePullPolicy"] = "Always"
                modified = True

            # Modest defaults so workers can pack onto e2-standard-2 nodes
            # (2 vCPU / 8 GiB). Without requests the scheduler treats pods as
            # zero-cost and we'd overcommit nodes.
            if pool == "argus-workers":
                resources = container.setdefault("resources", {})
                if "requests" not in resources:
                    resources["requests"] = {"cpu": "50m", "memory": "128Mi"}
                    modified = True
                if "limits" not in resources:
                    resources["limits"] = {"cpu": "500m", "memory": "512Mi"}
                    modified = True
            else:
                resources = container.setdefault("resources", {})
                if "requests" not in resources:
                    resources["requests"] = {"cpu": "100m", "memory": "256Mi"}
                    modified = True
                if "limits" not in resources:
                    resources["limits"] = {"cpu": "1000m", "memory": "1Gi"}
                    modified = True

            # Wire OTel exporters at the in-cluster dashboard ClusterIP so
            # logs/traces/metrics show up automatically.
            env = container.setdefault("env", [])
            otlp_env = {
                "OTEL_EXPORTER_OTLP_ENDPOINT": "http://aspire-dashboard-otlp:4317",
                "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
                "OTEL_SERVICE_NAME":           name,
            }
            existing_names = {e.get("name") for e in env if isinstance(e, dict)}
            for k, v in otlp_env.items():
                if k not in existing_names:
                    env.append({"name": k, "value": v})
                    modified = True

            # Inject ARGUS_DASHBOARD_URL into the web app so the topbar can
            # link to the Aspire dashboard.
            if name == "argus-web":
                dashboard_url = os.environ.get("DASHBOARD_URL")
                if dashboard_url and "ARGUS_DASHBOARD_URL" not in existing_names:
                    env.append({"name": "ARGUS_DASHBOARD_URL", "value": dashboard_url})
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

    print(f"Deployment manifests patched in {total} file(s).")


if __name__ == "__main__":
    main()
