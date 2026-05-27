#!/usr/bin/env python3
"""Emit a GitHub Actions matrix for service images that need rebuilding."""
import json
import os
import subprocess
from pathlib import Path

SERVICES = [
    ("agent-service", "src/Services/Argus.AgentService/Argus.AgentService.csproj", "Argus.AgentService"),
    ("amass-worker", "src/Workers/Argus.Workers.Amass/Argus.Workers.Amass.csproj", "Argus.Workers.Amass"),
    ("argus-api-gateway", "src/Argus.ApiGateway/Argus.ApiGateway.csproj", "Argus.ApiGateway"),
    ("argus-web", "src/Argus.Web/Argus.Web.csproj", "Argus.Web"),
    ("artifact-service", "src/Services/Argus.ArtifactService/Argus.ArtifactService.csproj", "Argus.ArtifactService"),
    ("asset-scoring-worker", "src/Workers/Argus.Workers.AssetScoring/Argus.Workers.AssetScoring.csproj", "Argus.Workers.AssetScoring"),
    ("asset-service", "src/Services/Argus.AssetService/Argus.AssetService.csproj", "Argus.AssetService"),
    ("asset-storage-worker", "src/Workers/Argus.Workers.AssetStorage/Argus.Workers.AssetStorage.csproj", "Argus.Workers.AssetStorage"),
    ("dns-resolver-worker", "src/Workers/Argus.Workers.DnsResolver/Argus.Workers.DnsResolver.csproj", "Argus.Workers.DnsResolver"),
    ("event-router-service", "src/Services/Argus.EventRouterService/Argus.EventRouterService.csproj", "Argus.EventRouterService"),
    ("finding-deduper-worker", "src/Workers/Argus.Workers.FindingDeduper/Argus.Workers.FindingDeduper.csproj", "Argus.Workers.FindingDeduper"),
    ("finding-service", "src/Services/Argus.FindingService/Argus.FindingService.csproj", "Argus.FindingService"),
    ("fingerprint-worker", "src/Workers/Argus.Workers.Fingerprint/Argus.Workers.Fingerprint.csproj", "Argus.Workers.Fingerprint"),
    ("headless-spider-worker", "src/Workers/Argus.Workers.HeadlessSpider/Argus.Workers.HeadlessSpider.csproj", "Argus.Workers.HeadlessSpider"),
    ("html-dom-spider-worker", "src/Workers/Argus.Workers.HtmlDomSpider/Argus.Workers.HtmlDomSpider.csproj", "Argus.Workers.HtmlDomSpider"),
    ("http-probe-worker", "src/Workers/Argus.Workers.HttpProbe/Argus.Workers.HttpProbe.csproj", "Argus.Workers.HttpProbe"),
    ("http-worker", "src/Workers/Argus.Workers.Http/Argus.Workers.Http.csproj", "Argus.Workers.Http"),
    ("js-extractor-worker", "src/Workers/Argus.Workers.JsExtractor/Argus.Workers.JsExtractor.csproj", "Argus.Workers.JsExtractor"),
    ("program-scope-service", "src/Services/Argus.ProgramScopeService/Argus.ProgramScopeService.csproj", "Argus.ProgramScopeService"),
    ("proxy-registry-service", "src/Services/Argus.ProxyRegistryService/Argus.ProxyRegistryService.csproj", "Argus.ProxyRegistryService"),
    ("rate-limit-service", "src/Services/Argus.RateLimitService/Argus.RateLimitService.csproj", "Argus.RateLimitService"),
    ("realtime-service", "src/Services/Argus.RealtimeService/Argus.RealtimeService.csproj", "Argus.RealtimeService"),
    ("regex-scanner-worker", "src/Workers/Argus.Workers.RegexScanner/Argus.Workers.RegexScanner.csproj", "Argus.Workers.RegexScanner"),
    ("scan-orchestrator-service", "src/Services/Argus.ScanOrchestratorService/Argus.ScanOrchestratorService.csproj", "Argus.ScanOrchestratorService"),
    ("subfinder-worker", "src/Workers/Argus.Workers.Subfinder/Argus.Workers.Subfinder.csproj", "Argus.Workers.Subfinder"),
    ("task-service", "src/Services/Argus.TaskService/Argus.TaskService.csproj", "Argus.TaskService"),
    ("validation-worker", "src/Workers/Argus.Workers.Validation/Argus.Workers.Validation.csproj", "Argus.Workers.Validation"),
    ("wordlist-discovery-worker", "src/Workers/Argus.Workers.WordlistDiscovery/Argus.Workers.WordlistDiscovery.csproj", "Argus.Workers.WordlistDiscovery"),
]

REBUILD_ALL_PREFIXES = (
    ".github/workflows/cd-gcp.yml",
    "deploy/Dockerfile.service",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    "nuget.config",
    "src/Argus.ServiceDefaults/",
    "src/BuildingBlocks/",
    "src/Contracts/",
)


def run(args: list[str]) -> str:
    return subprocess.check_output(args, text=True).strip()


def service_entry(service: tuple[str, str, str]) -> dict[str, str]:
    name, project_path, assembly_name = service
    return {
        "name": name,
        "project_path": project_path,
        "assembly_name": assembly_name,
    }


def changed_files() -> tuple[list[str], bool]:
    head = os.environ.get("HEAD_SHA", "HEAD")
    base = os.environ.get("BASE_SHA", "")
    if not base or set(base) == {"0"}:
        base = f"{head}~1"

    try:
        merge_base = run(["git", "merge-base", base, head])
    except subprocess.CalledProcessError:
        merge_base = base

    try:
        output = run(["git", "diff", "--name-only", merge_base, head])
    except subprocess.CalledProcessError:
        print(f"Could not diff {merge_base}..{head}; rebuilding all images.")
        return [], True

    return [line for line in output.splitlines() if line], False


def main() -> None:
    force = os.environ.get("FORCE_REBUILD_ALL", "").lower() == "true"
    changes, diff_failed = changed_files()

    if force or diff_failed or any(path.startswith(REBUILD_ALL_PREFIXES) for path in changes):
        selected = [service_entry(service) for service in SERVICES]
        reason = "all"
    else:
        selected = []
        for service in SERVICES:
            _, project_path, _ = service
            project_dir = str(Path(project_path).parent) + "/"
            if any(path == project_path or path.startswith(project_dir) for path in changes):
                selected.append(service_entry(service))
        reason = "selected"

    output_path = os.environ["GITHUB_OUTPUT"]
    with open(output_path, "a", encoding="utf-8") as output:
        output.write(f"matrix={json.dumps({'include': selected}, separators=(',', ':'))}\n")
        output.write(f"build_required={str(bool(selected)).lower()}\n")
        output.write(f"reason={reason}\n")
        output.write(f"changed_count={len(changes)}\n")

    print(f"Changed files: {len(changes)}")
    print(f"Image rebuild mode: {reason}; images selected: {len(selected)}")
    for item in selected:
        print(f"  {item['name']}")


if __name__ == "__main__":
    main()
