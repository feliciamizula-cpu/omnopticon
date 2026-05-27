#!/usr/bin/env python3
"""Detect impacted Argus deployables for incremental CI/CD.

The detector is intentionally conservative:
- Direct service path changes select that service only.
- Shared project changes select all services that transitively reference the shared project.
- Build-global files select all deployable services.
- Topology files request full topology validation, but do not automatically rebuild every image
  unless they also match a build-global rule.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

try:
    import yaml
except ImportError as exc:  # pragma: no cover - handled in CI by installing pyyaml
    raise SystemExit(
        "PyYAML is required. Install it with: python -m pip install pyyaml"
    ) from exc

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SERVICE_MAP = ROOT / ".ci" / "service-map.yml"


def norm(path: str | Path) -> str:
    return str(path).replace("\\", "/").lstrip("./")


def run(command: list[str]) -> str:
    return subprocess.check_output(command, cwd=ROOT, text=True).strip()


def load_yaml(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as stream:
        return yaml.safe_load(stream)


def changed_files(base_sha: str | None, head_sha: str | None) -> tuple[list[str], bool]:
    env_files = os.environ.get("CHANGED_FILES")
    if env_files:
        return [norm(line.strip()) for line in env_files.splitlines() if line.strip()], False

    head = head_sha or os.environ.get("HEAD_SHA") or "HEAD"
    base = base_sha or os.environ.get("BASE_SHA") or ""

    if not base or set(base) == {"0"}:
        base = f"{head}~1"

    try:
        merge_base = run(["git", "merge-base", base, head])
    except subprocess.CalledProcessError:
        merge_base = base

    try:
        output = run(["git", "diff", "--name-only", merge_base, head])
    except subprocess.CalledProcessError:
        print(f"::warning::Could not diff {merge_base}..{head}; selecting all services.")
        return [], True

    return [norm(line) for line in output.splitlines() if line.strip()], False


def matches_any(path: str, patterns: list[str]) -> bool:
    return any(fnmatch.fnmatch(path, norm(pattern)) for pattern in patterns)


def project_references(project_path: str) -> set[str]:
    """Return normalized project references for one csproj."""
    project_file = ROOT / project_path
    if not project_file.exists():
        return set()

    try:
        tree = ET.parse(project_file)
    except ET.ParseError:
        return set()

    refs: set[str] = set()
    for elem in tree.iter():
        if elem.tag.endswith("ProjectReference"):
            include = elem.attrib.get("Include")
            if not include:
                continue
            target = (project_file.parent / include.replace("\\", "/")).resolve()
            try:
                refs.add(norm(target.relative_to(ROOT)))
            except ValueError:
                refs.add(norm(target))
    return refs


def build_reference_graph(config: dict[str, Any]) -> dict[str, set[str]]:
    projects: set[str] = set()

    for item in config.get("services", {}).values():
        projects.add(norm(item["project"]))
    for item in config.get("sharedProjects", {}).values():
        projects.add(norm(item["project"]))
    for item in config.get("nonDeployedProjects", {}).values():
        projects.add(norm(item["project"]))

    # Include all src projects so shared-project transitive references are accurate.
    for project in ROOT.glob("src/**/*.csproj"):
        projects.add(norm(project.relative_to(ROOT)))

    return {project: project_references(project) for project in sorted(projects)}


def transitive_refs(project: str, graph: dict[str, set[str]]) -> set[str]:
    visited: set[str] = set()
    stack = list(graph.get(project, set()))
    while stack:
        ref = stack.pop()
        if ref in visited:
            continue
        visited.add(ref)
        stack.extend(graph.get(ref, set()) - visited)
    return visited


def all_service_keys(config: dict[str, Any]) -> set[str]:
    return set(config.get("services", {}).keys())


def service_matrix_entry(key: str, service: dict[str, Any], reasons: list[str]) -> dict[str, Any]:
    return {
        "key": key,
        "name": service.get("image", key),
        "kind": service.get("kind", "service"),
        "project_path": service["project"],
        "assembly_name": service["assembly"],
        "image": service.get("image", key),
        "compose_service": service.get("composeService", key),
        "kubernetes_deployment": service.get("kubernetesDeployment", key),
        "kubernetes_container": service.get(
            "kubernetesContainer", service.get("kubernetesDeployment", key)
        ),
        "tests": service.get("tests", []),
        "health_url": service.get("healthUrl", "/health"),
        "reasons": reasons,
    }


def detect(config: dict[str, Any], files: list[str], diff_failed: bool, force_all: bool) -> dict[str, Any]:
    impacted: set[str] = set()
    reasons: dict[str, list[str]] = {}
    full_topology = False
    build_global = False
    graph = build_reference_graph(config)

    services = config.get("services", {})
    shared_projects = config.get("sharedProjects", {})
    global_paths = config.get("globalPaths", {})

    def add_service(key: str, reason: str) -> None:
        if key not in services:
            return
        impacted.add(key)
        reasons.setdefault(key, []).append(reason)

    def add_all(reason: str) -> None:
        for service_key in all_service_keys(config):
            add_service(service_key, reason)

    if force_all or diff_failed:
        build_global = True
        full_topology = True
        add_all("forced-all" if force_all else "diff-failed")
    else:
        for changed in files:
            if matches_any(changed, global_paths.get("fullTopology", [])):
                full_topology = True

            if matches_any(changed, global_paths.get("allApps", [])):
                build_global = True
                full_topology = True
                add_all(f"global-build:{changed}")
                continue

            for key, service in services.items():
                if matches_any(changed, service.get("paths", [])):
                    add_service(key, changed)

            for shared_key, shared in shared_projects.items():
                if not matches_any(changed, shared.get("paths", [])):
                    continue

                affects = shared.get("affects", "referenced-by")
                shared_project = norm(shared.get("project", ""))

                if affects == "all-runnable":
                    add_all(f"{shared_key}:{changed}")
                    continue

                selected = False
                for key, service in services.items():
                    service_project = norm(service["project"])
                    refs = transitive_refs(service_project, graph)
                    if shared_project in refs or shared_project == service_project:
                        add_service(key, f"{shared_key}:{changed}")
                        selected = True

                # Conservative fallback if graph parsing missed the project.
                if not selected:
                    add_all(f"{shared_key}:{changed}:fallback-all")

    matrix = [
        service_matrix_entry(key, services[key], reasons.get(key, []))
        for key in sorted(impacted)
    ]

    # App-code changed means the CD workflow should do something. A topology-only
    # change may only regenerate/apply manifests; a service change builds and patches images.
    ignored_globs = [
        "docs/**",
        "**/*.md",
        ".gitignore",
        ".editorconfig",
        "LICENSE",
        "tools/**",
        "context.md",
        "aspire_coordinate.txt",
    ]
    non_ignored_changes = [path for path in files if not matches_any(path, ignored_globs)]
    app_code_changed = bool(matrix or full_topology or build_global or non_ignored_changes)

    reason = "none"
    if force_all or diff_failed or build_global:
        reason = "all"
    elif matrix:
        reason = "selected"
    elif full_topology:
        reason = "topology-only"

    return {
        "matrix": {"include": matrix},
        "count": len(matrix),
        "build_required": bool(matrix),
        "reason": reason,
        "changed_count": len(files),
        "changed_files": files,
        "full_topology": full_topology,
        "manifests_changed": full_topology,
        "app_code_changed": app_code_changed,
        "web_changed": any(item["key"] == "argus-web" for item in matrix),
        "build_global": build_global,
    }


def write_github_outputs(result: dict[str, Any]) -> None:
    output_path = os.environ.get("GITHUB_OUTPUT")
    if not output_path:
        return

    matrix_json = json.dumps(result["matrix"], separators=(",", ":"))
    selected_names = ",".join(item["name"] for item in result["matrix"]["include"])

    outputs = {
        "matrix": matrix_json,
        "count": str(result["count"]),
        "build_required": str(result["build_required"]).lower(),
        "reason": result["reason"],
        "changed_count": str(result["changed_count"]),
        "full_topology": str(result["full_topology"]).lower(),
        "manifests_changed": str(result["manifests_changed"]).lower(),
        "app_code_changed": str(result["app_code_changed"]).lower(),
        "web_changed": str(result["web_changed"]).lower(),
        "selected_names": selected_names,
    }

    with open(output_path, "a", encoding="utf-8") as output:
        for key, value in outputs.items():
            output.write(f"{key}={value}\n")


def print_summary(result: dict[str, Any]) -> None:
    print(json.dumps(result, indent=2))
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if not summary:
        return

    with open(summary, "a", encoding="utf-8") as stream:
        stream.write("## Incremental deployment impact\n\n")
        stream.write(f"- Mode: `{result['reason']}`\n")
        stream.write(f"- Changed files: `{result['changed_count']}`\n")
        stream.write(f"- Full topology validation: `{str(result['full_topology']).lower()}`\n")
        stream.write(f"- Selected services: `{result['count']}`\n\n")
        if result["matrix"]["include"]:
            stream.write("| Service | Kind | Reason |\n")
            stream.write("|---|---|---|\n")
            for item in result["matrix"]["include"]:
                reason = "<br>".join(item.get("reasons", []))
                stream.write(f"| `{item['key']}` | `{item['kind']}` | {reason} |\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--service-map", default=str(DEFAULT_SERVICE_MAP))
    parser.add_argument("--base-sha", default=os.environ.get("BASE_SHA"))
    parser.add_argument("--head-sha", default=os.environ.get("HEAD_SHA"))
    parser.add_argument(
        "--force-all",
        action="store_true",
        default=os.environ.get("FORCE_REBUILD_ALL", "").lower() == "true",
    )
    args = parser.parse_args()

    config = load_yaml(Path(args.service_map))
    files, diff_failed = changed_files(args.base_sha, args.head_sha)
    result = detect(config, files, diff_failed, args.force_all)
    write_github_outputs(result)
    print_summary(result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
