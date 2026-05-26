#!/usr/bin/env python3
"""
OpenCode .NET LLM benchmark runner.

Runs the same benchmark prompts against multiple OpenCode models in isolated workspaces,
staggering starts to reduce rate-limit pressure. Optionally evaluates completed outputs
with deterministic build/test/run checks and emits machine-readable metrics.

Usage examples:
  # List NVIDIA models first, then paste exact IDs into --models
  opencode models nvidia

  # Run all three tasks against five models, staggered by 3 seconds
  python opencode_dotnet_llm_benchmark.py run \
    --models nvidia/model-a,nvidia/model-b,nvidia/model-c,nvidia/model-d,nvidia/model-e \
    --tasks speed,resource,maintainability \
    --stagger-seconds 3 \
    --max-concurrency 5 \
    --evaluate

  # Only generate prompt files
  python opencode_dotnet_llm_benchmark.py write-prompts --out prompts

Notes:
  - Do not hardcode API keys. Set NVIDIA_API_KEY in your shell or use OpenCode /connect.
  - The script assumes OpenCode is installed and authenticated locally.
  - Exact model IDs vary by configured provider; `opencode models nvidia` is the source of truth.
"""

from __future__ import annotations

import argparse
import asyncio
import csv
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import textwrap
import time
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple

try:
    import psutil  # type: ignore
except Exception:  # pragma: no cover - optional dependency
    psutil = None


PROMPTS: Dict[str, str] = {
    "speed": r'''
# Benchmark Task 1 — Speed / Runtime Optimization

You are a senior .NET engineer. Create a small, runnable .NET solution in the current directory.

## Optimization goal
Correctness is mandatory. Among correct solutions, the winner is the implementation with the lowest runtime for the benchmark command.

## Required output contract
You must create this exact project path:

- `src/SpeedBench/SpeedBench.csproj`

It must run with:

```bash
dotnet run -c Release --project src/SpeedBench -- --domains 5000 --subdomains 8 --urls 25 --seed 123 --out score.json
```

The program must write `score.json` with this exact shape:

```json
{
  "task": "speed",
  "domains": 5000,
  "subdomains": 40000,
  "urls": 1000000,
  "confirmedUrls": 1000000,
  "findings": 120000,
  "checksum": "<lowercase 64-char sha256 hex>",
  "elapsedMsInsideProcess": 0
}
```

`elapsedMsInsideProcess` can be any non-negative integer. External benchmarking will use wall-clock time.

## Deterministic workload
Generate a simulated event-driven recon pipeline entirely in memory. Do not perform network I/O.

For domain index `d` from `0` to `domains - 1`:

- Domain value: `target-{d}.example.com`
- Generate `subdomains` subdomains per domain.
- Subdomain value: `s{s}.target-{d}.example.com`
- Generate `urls` URLs per subdomain.
- URL value rules:
  - If `u % 10 == 0`: `https://{subdomain}/admin/{u}`
  - Else if `u % 10 == 1`: `https://{subdomain}/api/v1/{u}`
  - Else if `u % 25 == 0`: `https://{subdomain}/.env`
  - Else: `https://{subdomain}/path/{u}`

Every URL is confirmed.
A finding is created when the URL contains `/admin`, `/api/`, or `.env`.
For the required benchmark arguments, this produces exactly:

- 5,000 domains
- 40,000 subdomains
- 1,000,000 URLs
- 1,000,000 confirmed URLs
- 120,000 findings

## Checksum requirement
Compute `checksum` as lowercase SHA-256 hex over the UTF-8 bytes of this deterministic stream, in generation order:

For every generated URL, append:

```text
URL|{url}\n
```

For every generated finding, immediately after its URL line append:

```text
FINDING|{url}|HighValueUrl\n
```

The evaluator will recompute the checksum independently.

## Engineering requirements
- Use .NET 10 if available; otherwise use the latest installed stable .NET SDK.
- Use immutable event records for at least:
  - `DomainAssetCreated`
  - `SubdomainAssetCreated`
  - `UrlAssetCreated`
  - `UrlAssetConfirmed`
  - `FindingCreated`
- Simulate event handling without RabbitMQ because this is a pure runtime benchmark, but design the code so an event bus could replace the in-process dispatcher.
- Avoid unnecessary allocations.
- Avoid storing all URLs or all events if not needed.
- Include tests under `tests/SpeedBench.Tests`.
- `dotnet test -c Release` must pass.
- Do not ask questions. Make reasonable assumptions.

## Scoring
1. Build succeeds.
2. Tests pass.
3. `score.json` matches all required counts and checksum.
4. Runtime is measured externally. Lower is better.
'''.strip(),

    "resource": r'''
# Benchmark Task 2 — Resource Usage / Memory Optimization

You are a senior .NET engineer. Create a small, runnable .NET solution in the current directory.

## Optimization goal
Correctness is mandatory. Among correct solutions, the winner is the implementation with the lowest peak memory usage during the benchmark command.

## Required output contract
You must create this exact project path:

- `src/ResourceBench/ResourceBench.csproj`

It must run with:

```bash
dotnet run -c Release --project src/ResourceBench -- --events 1000000 --duplicate-rate 0.35 --seed 123 --out score.json
```

The program must write `score.json` with this exact shape:

```json
{
  "task": "resource",
  "processedEvents": 1000000,
  "uniqueAssets": 650000,
  "duplicateAssets": 350000,
  "confirmedAssets": 650000,
  "findings": 130000,
  "checksum": "<lowercase 64-char sha256 hex>",
  "peakWorkingSetBytesInsideProcess": 0
}
```

`peakWorkingSetBytesInsideProcess` should be populated using `Process.GetCurrentProcess().PeakWorkingSet64` if possible. External benchmarking will independently monitor memory too.

## Deterministic workload
Generate and process a stream of synthetic asset events. Do not perform network I/O. Do not require Postgres, Redis, or RabbitMQ to be running for the benchmark.

For event index `i` from `0` to `events - 1`:

- Let `uniqueCount = events - floor(events * duplicateRate)`.
- Let `assetIndex = i % uniqueCount`.
- Asset value: `https://s{assetIndex % 1000}.target-{assetIndex / 1000}.example.com/{path}` using integer division.
- Path rule:
  - If `assetIndex % 5 == 0`: `admin/{assetIndex}`
  - Else: `path/{assetIndex}`

The first time an asset value appears:

- Count it as a unique asset.
- Count it as confirmed.
- If its path begins with `admin/`, create one finding.

Repeated asset values count as duplicate assets and must not create duplicate findings.

For the required benchmark arguments:

- `uniqueAssets` must be 650,000
- `duplicateAssets` must be 350,000
- `confirmedAssets` must be 650,000
- `findings` must be 130,000

## Checksum requirement
Compute `checksum` as lowercase SHA-256 hex over the UTF-8 bytes of this deterministic stream, in first-seen unique asset order only:

For every first-seen unique asset, append:

```text
ASSET|{assetValue}\n
```

For every finding, immediately after its asset line append:

```text
FINDING|{assetValue}|AdminSurface\n
```

The evaluator will recompute the checksum independently.

## Architecture requirements
This benchmark simulates a resource-conscious distributed worker. Include abstractions or types for:

- `IEventConsumer`
- `IAssetDeduplicator`
- `IAssetStore`
- `IFindingStore`
- `IWorkerCheckpointStore`

They may be in-process/file-backed implementations. The benchmark should not need real infrastructure to run.

## Resource requirements
- Do not hold all events in memory.
- Do not hold full duplicate event payloads in memory.
- Favor streaming, compact structures, pooled buffers, or disk-backed state when useful.
- Include clear comments explaining your memory strategy.
- Include tests under `tests/ResourceBench.Tests`.
- `dotnet test -c Release` must pass.
- Do not ask questions. Make reasonable assumptions.

## Scoring
1. Build succeeds.
2. Tests pass.
3. `score.json` matches all required counts and checksum.
4. Peak RSS / working set is measured externally. Lower is better.
'''.strip(),

    "maintainability": r'''
# Benchmark Task 3 — Maintainability / Minimal Implementation Size

You are a principal .NET engineer. Create a small, runnable .NET solution in the current directory.

## Optimization goal
Correctness is mandatory. Among correct solutions, the winner is the implementation with the fewest non-blank, non-comment lines of production C# code under `src/`.

This is not code golf. Solutions are disqualified if they are unreadable, use generated code to hide implementation, put production code in tests, or use intentionally obfuscated formatting. Keep line length reasonable.

## Required output contract
You must create this exact project path:

- `src/MaintenanceBench/MaintenanceBench.csproj`

It must run with:

```bash
dotnet run -c Release --project src/MaintenanceBench -- --scenario standard --out score.json
```

The program must write `score.json` with this exact shape:

```json
{
  "task": "maintainability",
  "targets": 3,
  "domains": 3,
  "subdomains": 9,
  "urls": 18,
  "confirmedUrls": 18,
  "findings": 6,
  "eventsPublished": 57,
  "eventsConsumed": 57,
  "idempotencySkips": 4,
  "checksum": "<lowercase 64-char sha256 hex>"
}
```

## Required behavior
Implement a tiny event-driven recon workflow with clean boundaries and minimal code.

For scenario `standard`, process these target domains:

- `alpha.example.com`
- `beta.example.com`
- `gamma.example.com`

For each domain:

- Create one domain asset.
- Create three subdomain assets:
  - `www.{domain}`
  - `api.{domain}`
  - `admin.{domain}`
- Create two URL assets per subdomain:
  - `https://{subdomain}/`
  - `https://{subdomain}/admin`
- Confirm every URL.
- Create one finding for each URL containing `/admin`.

## Required event types
Use strongly typed C# event contracts for at least:

- `TargetSubmitted`
- `AssetCreated`
- `AssetConfirmed`
- `FindingCreated`

Each event must carry:

- `EventId`
- `OccurredAtUtc`
- `CorrelationId`
- `CausationId`
- `SchemaVersion`

## Idempotency requirement
The program must intentionally publish four duplicate events during the scenario and skip them through an inbox/idempotency mechanism. The output must report `idempotencySkips: 4`.

## Event count requirement
For the standard scenario, report:

- `eventsPublished`: 57
- `eventsConsumed`: 57

Count duplicate events as published and consumed, even when skipped by idempotency.

## Checksum requirement
Compute `checksum` as lowercase SHA-256 hex over the UTF-8 bytes of this deterministic stream, sorted ordinal by line before hashing:

```text
ASSET|{assetType}|{assetValue}\n
FINDING|{url}|HighValueUrl\n
```

Include every unique asset and every unique finding exactly once.

## Design requirements
- Keep the solution compact and readable.
- Use separate concepts for domain model, event contracts, event bus/dispatcher, handlers, and stores, even if they live in few files.
- Use dependency injection if it does not add excessive code.
- Include tests under `tests/MaintenanceBench.Tests`.
- `dotnet test -c Release` must pass.
- Do not ask questions. Make reasonable assumptions.

## Scoring
1. Build succeeds.
2. Tests pass.
3. `score.json` matches all required counts and checksum.
4. Count non-blank, non-comment production C# lines under `src/`. Fewer is better.
'''.strip(),
}


@dataclass
class RunResult:
    model: str
    task: str
    workspace: str
    prompt_path: str
    stdout_path: str
    stderr_path: str
    exit_code: int
    elapsed_seconds: float
    started_at: str
    finished_at: str
    evaluation_path: Optional[str] = None
    evaluation_passed: Optional[bool] = None


def slugify(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", value).strip("_")[:120]


def utc_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def write_prompts(out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    for task, prompt in PROMPTS.items():
        (out / f"{task}.md").write_text(prompt + "\n", encoding="utf-8")


def ensure_opencode() -> None:
    if shutil.which("opencode") is None:
        raise SystemExit("opencode was not found on PATH. Install/configure OpenCode first, then rerun this script.")


def parse_models(raw: str) -> List[str]:
    models = [m.strip() for m in raw.split(",") if m.strip()]
    if not models:
        raise SystemExit("At least one model is required. Example: --models nvidia/model-a,nvidia/model-b")
    return models


def parse_tasks(raw: str) -> List[str]:
    if raw.strip().lower() == "all":
        return list(PROMPTS)
    tasks = [t.strip().lower() for t in raw.split(",") if t.strip()]
    bad = [t for t in tasks if t not in PROMPTS]
    if bad:
        raise SystemExit(f"Unknown task(s): {bad}. Valid: {', '.join(PROMPTS)}")
    return tasks


def sha256_lines(lines: Iterable[str], sort_lines: bool = False) -> str:
    data = list(lines) if sort_lines else lines
    h = hashlib.sha256()
    for line in (sorted(data) if sort_lines else data):
        h.update(line.encode("utf-8"))
    return h.hexdigest()


def expected_speed(domains: int, subdomains: int, urls: int) -> Dict[str, Any]:
    finding_count = 0

    def lines() -> Iterable[str]:
        nonlocal finding_count
        for d in range(domains):
            domain = f"target-{d}.example.com"
            for s in range(subdomains):
                sub = f"s{s}.{domain}"
                for u in range(urls):
                    if u % 10 == 0:
                        url = f"https://{sub}/admin/{u}"
                    elif u % 10 == 1:
                        url = f"https://{sub}/api/v1/{u}"
                    elif u % 25 == 0:
                        url = f"https://{sub}/.env"
                    else:
                        url = f"https://{sub}/path/{u}"
                    yield f"URL|{url}\n"
                    if "/admin" in url or "/api/" in url or ".env" in url:
                        finding_count += 1
                        yield f"FINDING|{url}|HighValueUrl\n"

    checksum = sha256_lines(lines())
    return {
        "task": "speed",
        "domains": domains,
        "subdomains": domains * subdomains,
        "urls": domains * subdomains * urls,
        "confirmedUrls": domains * subdomains * urls,
        "findings": finding_count,
        "checksum": checksum,
    }


def expected_resource(events: int, duplicate_rate: float) -> Dict[str, Any]:
    unique_count = events - int(events * duplicate_rate)
    findings = 0

    def lines() -> Iterable[str]:
        nonlocal findings
        for asset_index in range(unique_count):
            path = f"admin/{asset_index}" if asset_index % 5 == 0 else f"path/{asset_index}"
            asset = f"https://s{asset_index % 1000}.target-{asset_index // 1000}.example.com/{path}"
            yield f"ASSET|{asset}\n"
            if path.startswith("admin/"):
                findings += 1
                yield f"FINDING|{asset}|AdminSurface\n"

    checksum = sha256_lines(lines())
    return {
        "task": "resource",
        "processedEvents": events,
        "uniqueAssets": unique_count,
        "duplicateAssets": events - unique_count,
        "confirmedAssets": unique_count,
        "findings": findings,
        "checksum": checksum,
    }


def expected_maintainability() -> Dict[str, Any]:
    domains = ["alpha.example.com", "beta.example.com", "gamma.example.com"]
    lines: List[str] = []
    for domain in domains:
        lines.append(f"ASSET|Domain|{domain}\n")
        for prefix in ["www", "api", "admin"]:
            sub = f"{prefix}.{domain}"
            lines.append(f"ASSET|Subdomain|{sub}\n")
            for suffix in ["/", "/admin"]:
                url = f"https://{sub}{suffix}"
                lines.append(f"ASSET|Url|{url}\n")
                if "/admin" in url:
                    lines.append(f"FINDING|{url}|HighValueUrl\n")
    return {
        "task": "maintainability",
        "targets": 3,
        "domains": 3,
        "subdomains": 9,
        "urls": 18,
        "confirmedUrls": 18,
        "findings": 6,
        "eventsPublished": 57,
        "eventsConsumed": 57,
        "idempotencySkips": 4,
        "checksum": sha256_lines(lines, sort_lines=True),
    }


def read_json(path: Path) -> Dict[str, Any]:
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def compare_expected(actual: Dict[str, Any], expected: Dict[str, Any]) -> Tuple[bool, List[str]]:
    errors = []
    for key, expected_value in expected.items():
        actual_value = actual.get(key)
        if actual_value != expected_value:
            errors.append(f"{key}: expected {expected_value!r}, got {actual_value!r}")
    return not errors, errors


def count_src_loc(workspace: Path) -> int:
    total = 0
    src = workspace / "src"
    if not src.exists():
        return 0
    in_block = False
    for file in src.rglob("*.cs"):
        if "/obj/" in file.as_posix() or "/bin/" in file.as_posix():
            continue
        for raw in file.read_text(encoding="utf-8", errors="ignore").splitlines():
            line = raw.strip()
            if not line:
                continue
            if in_block:
                if "*/" in line:
                    in_block = False
                    line = line.split("*/", 1)[1].strip()
                    if not line:
                        continue
                else:
                    continue
            if line.startswith("/*"):
                if "*/" not in line:
                    in_block = True
                    continue
                line = line.split("*/", 1)[1].strip()
                if not line:
                    continue
            if line.startswith("//"):
                continue
            total += 1
    return total


def run_monitored(cmd: List[str], cwd: Path, stdout_path: Path, stderr_path: Path, timeout_seconds: int) -> Dict[str, Any]:
    started = time.perf_counter()
    peak_rss = 0
    with stdout_path.open("wb") as out, stderr_path.open("wb") as err:
        proc = subprocess.Popen(cmd, cwd=str(cwd), stdout=out, stderr=err)
        ps_proc = psutil.Process(proc.pid) if psutil else None
        try:
            while proc.poll() is None:
                if ps_proc:
                    try:
                        rss = ps_proc.memory_info().rss
                        peak_rss = max(peak_rss, rss)
                        for child in ps_proc.children(recursive=True):
                            try:
                                peak_rss = max(peak_rss, child.memory_info().rss)
                            except Exception:
                                pass
                    except Exception:
                        pass
                if time.perf_counter() - started > timeout_seconds:
                    proc.kill()
                    return {
                        "exit_code": -9,
                        "elapsed_seconds": time.perf_counter() - started,
                        "peak_rss_bytes": peak_rss or None,
                        "timed_out": True,
                    }
                time.sleep(0.1)
        finally:
            try:
                proc.wait(timeout=5)
            except Exception:
                pass
    return {
        "exit_code": proc.returncode,
        "elapsed_seconds": time.perf_counter() - started,
        "peak_rss_bytes": peak_rss or None,
        "timed_out": False,
    }


def evaluate(workspace: Path, task: str, timeout_seconds: int) -> Dict[str, Any]:
    eval_dir = workspace / "_evaluation"
    eval_dir.mkdir(exist_ok=True)

    build = run_monitored(
        ["dotnet", "build", "-c", "Release"],
        workspace,
        eval_dir / "build.stdout.txt",
        eval_dir / "build.stderr.txt",
        timeout_seconds,
    )
    tests = run_monitored(
        ["dotnet", "test", "-c", "Release", "--no-build"],
        workspace,
        eval_dir / "test.stdout.txt",
        eval_dir / "test.stderr.txt",
        timeout_seconds,
    )

    score_path = workspace / "score.json"
    if score_path.exists():
        score_path.unlink()

    if task == "speed":
        run_cmd = [
            "dotnet", "run", "-c", "Release", "--no-build", "--project", "src/SpeedBench", "--",
            "--domains", "5000", "--subdomains", "8", "--urls", "25", "--seed", "123", "--out", "score.json",
        ]
        expected = expected_speed(5000, 8, 25)
    elif task == "resource":
        run_cmd = [
            "dotnet", "run", "-c", "Release", "--no-build", "--project", "src/ResourceBench", "--",
            "--events", "1000000", "--duplicate-rate", "0.35", "--seed", "123", "--out", "score.json",
        ]
        expected = expected_resource(1000000, 0.35)
    elif task == "maintainability":
        run_cmd = [
            "dotnet", "run", "-c", "Release", "--no-build", "--project", "src/MaintenanceBench", "--",
            "--scenario", "standard", "--out", "score.json",
        ]
        expected = expected_maintainability()
    else:
        raise ValueError(task)

    bench = run_monitored(
        run_cmd,
        workspace,
        eval_dir / "benchmark.stdout.txt",
        eval_dir / "benchmark.stderr.txt",
        timeout_seconds,
    )

    actual: Dict[str, Any] = {}
    score_matches = False
    score_errors = ["score.json was not created"]
    if score_path.exists():
        try:
            actual = read_json(score_path)
            score_matches, score_errors = compare_expected(actual, expected)
        except Exception as exc:
            score_errors = [f"could not parse score.json: {exc}"]

    production_loc = count_src_loc(workspace) if task == "maintainability" else None
    passed = build["exit_code"] == 0 and tests["exit_code"] == 0 and bench["exit_code"] == 0 and score_matches
    result = {
        "task": task,
        "passed": passed,
        "build": build,
        "tests": tests,
        "benchmark": bench,
        "expected": expected,
        "actual": actual,
        "score_matches": score_matches,
        "score_errors": score_errors,
        "production_loc": production_loc,
        "metric": {
            "speed_runtime_seconds": bench["elapsed_seconds"] if task == "speed" and passed else None,
            "resource_peak_rss_bytes": bench["peak_rss_bytes"] if task == "resource" and passed else None,
            "maintainability_src_loc": production_loc if task == "maintainability" and passed else None,
        },
    }
    (eval_dir / "evaluation.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


async def run_opencode_one(
    index: int,
    model: str,
    task: str,
    root: Path,
    stagger_seconds: float,
    timeout_seconds: int,
    evaluate_outputs: bool,
) -> RunResult:
    await asyncio.sleep(index * stagger_seconds)
    task_prompt = PROMPTS[task]
    workspace = root / task / slugify(model)
    workspace.mkdir(parents=True, exist_ok=True)
    prompt_path = workspace / "prompt.md"
    prompt_path.write_text(task_prompt + "\n", encoding="utf-8")
    stdout_path = workspace / "opencode.stdout.txt"
    stderr_path = workspace / "opencode.stderr.txt"

    cmd = [
        "opencode",
        "run",
        "--model", model,
        "--format", "json",
        "--dir", str(workspace),
        "--file", str(prompt_path),
        "Implement the benchmark task exactly as specified in prompt.md. Create all files in the current working directory. Run the tests before finishing.",
    ]

    started_at = utc_now()
    start = time.perf_counter()
    with stdout_path.open("wb") as out, stderr_path.open("wb") as err:
        proc = await asyncio.create_subprocess_exec(*cmd, stdout=out, stderr=err, cwd=str(workspace))
        try:
            await asyncio.wait_for(proc.wait(), timeout=timeout_seconds)
        except asyncio.TimeoutError:
            proc.kill()
            await proc.wait()
    elapsed = time.perf_counter() - start
    finished_at = utc_now()

    evaluation_path = None
    evaluation_passed = None
    if evaluate_outputs:
        eval_result = await asyncio.to_thread(evaluate, workspace, task, timeout_seconds)
        evaluation_path = str(workspace / "_evaluation" / "evaluation.json")
        evaluation_passed = bool(eval_result.get("passed"))

    return RunResult(
        model=model,
        task=task,
        workspace=str(workspace),
        prompt_path=str(prompt_path),
        stdout_path=str(stdout_path),
        stderr_path=str(stderr_path),
        exit_code=proc.returncode or 0,
        elapsed_seconds=elapsed,
        started_at=started_at,
        finished_at=finished_at,
        evaluation_path=evaluation_path,
        evaluation_passed=evaluation_passed,
    )


async def run_all(args: argparse.Namespace) -> None:
    ensure_opencode()
    models = parse_models(args.models)
    tasks = parse_tasks(args.tasks)
    root = Path(args.out).resolve() / time.strftime("%Y%m%d_%H%M%S")
    root.mkdir(parents=True, exist_ok=True)
    write_prompts(root / "_prompts")

    queue: List[Tuple[str, str]] = [(m, t) for t in tasks for m in models]
    sem = asyncio.Semaphore(args.max_concurrency)
    results: List[RunResult] = []

    async def guarded(i: int, model: str, task: str) -> None:
        async with sem:
            print(f"[{utc_now()}] starting task={task} model={model}", flush=True)
            result = await run_opencode_one(
                i, model, task, root, args.stagger_seconds, args.timeout_seconds, args.evaluate
            )
            print(
                f"[{utc_now()}] finished task={task} model={model} exit={result.exit_code} "
                f"eval={result.evaluation_passed} elapsed={result.elapsed_seconds:.1f}s",
                flush=True,
            )
            results.append(result)

    await asyncio.gather(*(guarded(i, model, task) for i, (model, task) in enumerate(queue)))

    json_path = root / "summary.json"
    csv_path = root / "summary.csv"
    json_path.write_text(json.dumps([asdict(r) for r in results], indent=2), encoding="utf-8")
    with csv_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(asdict(results[0]).keys()) if results else [])
        writer.writeheader()
        for result in results:
            writer.writerow(asdict(result))
    print(f"\nWrote summary: {json_path}")
    print(f"Wrote CSV:     {csv_path}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Run OpenCode benchmark prompts against multiple models.")
    sub = parser.add_subparsers(dest="cmd", required=True)

    wp = sub.add_parser("write-prompts", help="Write benchmark prompt markdown files.")
    wp.add_argument("--out", default="prompts")

    run = sub.add_parser("run", help="Run OpenCode benchmark prompts.")
    run.add_argument("--models", required=True, help="Comma-separated OpenCode model IDs, e.g. nvidia/model-a,nvidia/model-b")
    run.add_argument("--tasks", default="all", help="Comma-separated: speed,resource,maintainability or all")
    run.add_argument("--out", default="bench_runs")
    run.add_argument("--stagger-seconds", type=float, default=3.0)
    run.add_argument("--max-concurrency", type=int, default=5)
    run.add_argument("--timeout-seconds", type=int, default=3600)
    run.add_argument("--evaluate", action="store_true", help="After each model run, build/test/benchmark the generated solution.")

    args = parser.parse_args()
    if args.cmd == "write-prompts":
        write_prompts(Path(args.out))
        print(f"Wrote prompts to {Path(args.out).resolve()}")
    elif args.cmd == "run":
        asyncio.run(run_all(args))


if __name__ == "__main__":
    main()
