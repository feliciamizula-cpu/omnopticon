#!/usr/bin/env python3
"""Validate `.ci/service-map.yml` so incremental deployments fail fast."""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

try:
    import yaml
except ImportError as exc:  # pragma: no cover
    raise SystemExit("PyYAML is required. Install it with: python -m pip install pyyaml") from exc

ROOT = Path(__file__).resolve().parents[2]
SERVICE_MAP = ROOT / ".ci" / "service-map.yml"


def norm(path: str | Path) -> str:
    return str(path).replace("\\", "/")


def load_map() -> dict[str, Any]:
    with SERVICE_MAP.open("r", encoding="utf-8") as stream:
        return yaml.safe_load(stream)


def solution_projects() -> set[str]:
    solution = ROOT / "eShop.slnx"
    if not solution.exists():
        return set()
    tree = ET.parse(solution)
    projects: set[str] = set()
    for elem in tree.iter():
        if elem.tag.endswith("Project") and elem.attrib.get("Path"):
            projects.add(norm(elem.attrib["Path"]))
    return projects


def main() -> int:
    config = load_map()
    errors: list[str] = []
    warnings: list[str] = []

    services = config.get("services", {})
    shared = config.get("sharedProjects", {})
    non_deployed = config.get("nonDeployedProjects", {})

    if not services:
        errors.append("service-map.yml must define at least one deployable service.")

    seen_images: set[str] = set()
    seen_compose: set[str] = set()
    seen_deployments: set[str] = set()

    for key, service in services.items():
        project = ROOT / service.get("project", "")
        if not project.exists():
            errors.append(f"{key}: project does not exist: {service.get('project')}")

        for required in ("assembly", "image", "composeService", "kubernetesDeployment", "kubernetesContainer"):
            if not service.get(required):
                errors.append(f"{key}: missing required field `{required}`")

        image = service.get("image")
        if image in seen_images:
            errors.append(f"{key}: duplicate image name `{image}`")
        seen_images.add(image)

        compose = service.get("composeService")
        if compose in seen_compose:
            errors.append(f"{key}: duplicate compose service `{compose}`")
        seen_compose.add(compose)

        deployment = service.get("kubernetesDeployment")
        if deployment in seen_deployments:
            errors.append(f"{key}: duplicate kubernetesDeployment `{deployment}`")
        seen_deployments.add(deployment)

        for test_project in service.get("tests", []):
            if not (ROOT / test_project).exists():
                errors.append(f"{key}: test project does not exist: {test_project}")

    for key, project_config in shared.items():
        project = ROOT / project_config.get("project", "")
        if not project.exists():
            errors.append(f"shared project {key}: project does not exist: {project_config.get('project')}")

    for key, project_config in non_deployed.items():
        project = ROOT / project_config.get("project", "")
        if not project.exists():
            warnings.append(f"non-deployed project {key}: project does not exist: {project_config.get('project')}")

    sln_projects = solution_projects()
    mapped_projects = {norm(svc["project"]) for svc in services.values()}
    mapped_projects |= {norm(item["project"]) for item in shared.values()}
    non_deployed_projects = {norm(item["project"]) for item in non_deployed.values()}
    represented_projects = mapped_projects | non_deployed_projects

    for project in sorted(mapped_projects):
        if project not in sln_projects:
            warnings.append(f"{project} is mapped but not listed in eShop.slnx")

    # Alert on runnable-looking projects that are neither deployable nor explicitly excluded.
    for project in sorted(ROOT.glob("src/**/*.csproj")):
        rel = norm(project.relative_to(ROOT))
        if rel not in represented_projects and "Argus.AppHost" not in rel and "Tools/" not in rel:
            warnings.append(f"{rel} is not represented in service-map.yml")

    for warning in warnings:
        print(f"::warning::{warning}")
    for error in errors:
        print(f"::error::{error}")

    if errors:
        return 1

    print(f"Validated {len(services)} deployable services and {len(shared)} shared projects.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
