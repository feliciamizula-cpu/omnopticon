#!/usr/bin/env python3
"""
OpenCode LLM Benchmark - Simple, Fast, Single-Task

Scores models on: completion time, peak memory, and code brevity.

Usage:
    python bench.py run                        # use config.json
    python bench.py run --model opencode/big-pickle
    python bench.py write-config               # generate sample config
"""

import argparse
import asyncio
import csv
import json
import os
import random
import resource
import subprocess
import sys
import time
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Optional

CONFIG_FILE = Path(__file__).parent / "config.json"
DEFAULT_TASK = "json-archive"
PROMPT = '''\
# Benchmark Task: JSON Archive Processor

You are a skilled Python developer. Write a Python script that:

1. Generates 100,000 JSON objects in memory with fields: id (0-99999), data (random 100-char string), timestamp (ISO format), tags (5 random words)
2. Validates each is valid JSON
3. Writes them to a compressed archive file: output.json.gz
4. Reads the archive back and validates all 100,000 objects
5. Writes a summary.json with: object_count, total_bytes_in, total_bytes_out, duration_seconds

Requirements:
- Single Python file: solution.py
- Use gzip for compression
- Handle errors gracefully
- Run with: python3 solution.py
- Print progress every 10,000 objects

Output files:
- output.json.gz (compressed archive)
- summary.json (metrics)

Scoring (lower is better):
- execution_time: measured in seconds
- peak_memory_mb: maximum RSS during execution
- code_lines: non-blank, non-comment lines in solution.py (fewer is better)

Do not ask questions. Make reasonable assumptions.'''

@dataclass
class Result:
    model: str
    success: bool
    execution_time: float
    peak_memory_mb: float
    code_lines: int
    error: str
    summary_path: str


def load_config() -> dict:
    if CONFIG_FILE.exists():
        with open(CONFIG_FILE) as f:
            return json.load(f)
    return {}


def save_config(cfg: dict) -> None:
    with open(CONFIG_FILE, "w") as f:
        json.dump(cfg, f, indent=2)


def write_sample_config() -> None:
    cfg = {
        "model": "opencode/big-pickle",
        "timeout_seconds": 600,
        "max_code_lines": 500,
    }
    save_config(cfg)
    print(f"Wrote sample config to {CONFIG_FILE}")


async def run_agent(model: str, workspace: Path, timeout: int) -> tuple[int, str, str]:
    prompt_path = workspace / "prompt.md"
    prompt_path.write_text(PROMPT)

    cmd = [
        "opencode", "run",
        "--model", model,
        "--format", "json",
        "--dir", str(workspace),
        "--file", str(prompt_path),
        "--dangerously-skip-permissions",
        "Write solution.py implementing the task in prompt.md. Run it to completion."
    ]

    stdout_path = workspace / "stdout.txt"
    stderr_path = workspace / "stderr.txt"

    started = time.time()
    try:
        with open(stdout_path, "w") as out, open(stderr_path, "w") as err:
            proc = await asyncio.create_subprocess_exec(
                *cmd, stdout=out, stderr=err, cwd=str(workspace)
            )
            try:
                await asyncio.wait_for(proc.wait(), timeout=timeout)
            except asyncio.TimeoutError:
                proc.kill()
                await proc.wait()
        elapsed = time.time() - started
        return proc.returncode or 0, stdout_path.read_text(), stderr_path.read_text()
    except Exception as e:
        return -1, "", str(e)


def count_code_lines(workspace: Path) -> int:
    sol = workspace / "solution.py"
    if not sol.exists():
        return 9999
    count = 0
    in_block = False
    for line in sol.read_text().splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        if in_block:
            if "*/" in stripped:
                in_block = False
                stripped = stripped.split("*/", 1)[1].strip()
                if not stripped:
                    continue
            else:
                continue
        if stripped.startswith("/*"):
            if "*/" not in stripped:
                in_block = True
                continue
            stripped = stripped.split("*/", 1)[1].strip()
            if not stripped:
                continue
        if stripped.startswith("//"):
            continue
        count += 1
    return count


def run_solution(workspace: Path, timeout: int) -> tuple[bool, float, float, dict]:
    start = time.time()
    peak_mb = 0.0

    try:
        proc = subprocess.Popen(
            ["python3", "solution.py"],
            cwd=str(workspace),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )

        try:
            while proc.poll() is None:
                if time.time() - start > timeout:
                    proc.kill()
                    return False, 0, 0, {}
                time.sleep(0.1)

                try:
                    p = subprocess.run(
                        ["ps", "-o", "rss=", "-p", str(proc.pid)],
                        capture_output=True, text=True, timeout=1
                    )
                    rss = int(p.stdout.strip() or "0") / 1024
                    peak_mb = max(peak_mb, rss)
                except:
                    pass

            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
            return False, 0, 0, {}

        elapsed = time.time() - start
        summary_path = workspace / "summary.json"

        if summary_path.exists():
            summary = json.loads(summary_path.read_text())
            return True, elapsed, peak_mb, summary
        return proc.returncode == 0, elapsed, peak_mb, {}

    except Exception as e:
        return False, 0, 0, {}


async def run_benchmark(model: str, cfg: dict) -> Result:
    timeout = cfg.get("timeout_seconds", 600)
    workspace = Path("/tmp/bench_runs") / model.replace("/", "_")
    workspace.mkdir(parents=True, exist_ok=True)

    print(f"[{model}] Starting...")
    exit_code, stdout, stderr = await run_agent(model, workspace, timeout)

    if exit_code != 0:
        print(f"[{model}] Agent failed with exit {exit_code}")
        return Result(
            model=model,
            success=False,
            execution_time=0,
            peak_memory_mb=0,
            code_lines=0,
            error=f"exit {exit_code}: {stderr[-500:]}",
            summary_path="",
        )

    code_lines = count_code_lines(workspace)
    print(f"[{model}] Agent done. Code lines: {code_lines}")

    success, exec_time, peak_mem, summary = run_solution(workspace, timeout)
    print(f"[{model}] Solution ran: success={success}, time={exec_time:.1f}s, mem={peak_mem:.1f}MB")

    return Result(
        model=model,
        success=success,
        execution_time=exec_time,
        peak_memory_mb=peak_mem,
        code_lines=code_lines,
        error="",
        summary_path=str(workspace / "summary.json"),
    )


def compute_score(r: Result) -> float:
    if not r.success:
        return 999999
    t = r.execution_time
    m = r.peak_memory_mb
    l = r.code_lines
    return t * 0.5 + m * 0.3 + l * 0.01


async def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="cmd")
    sub.add_parser("write-config", help="Write sample config.json")
    run_parser = sub.add_parser("run", help="Run benchmark")
    run_parser.add_argument("--model", default=None)
    args = parser.parse_args()

    if args.cmd == "write-config":
        write_sample_config()
        return

    cfg = load_config()
    model = args.model or cfg.get("model", "opencode/big-pickle")

    print(f"=== OpenCode Benchmark ===")
    print(f"Model: {model}")
    print(f"Config: {cfg}")
    print()

    result = await run_benchmark(model, cfg)

    score = compute_score(result)
    print()
    print(f"=== Results ===")
    print(f"Model:           {result.model}")
    print(f"Success:         {result.success}")
    print(f"Execution Time:  {result.execution_time:.2f}s")
    print(f"Peak Memory:     {result.peak_memory_mb:.1f}MB")
    print(f"Code Lines:      {result.code_lines}")
    print(f"Score:           {score:.2f}")
    if result.error:
        print(f"Error:           {result.error}")

    results = [asdict(result)]
    with open("results.json", "w") as f:
        json.dump(results, f, indent=2)

    with open("results.csv", "w", newline="") as f:
        if results:
            writer = csv.DictWriter(f, fieldnames=results[0].keys())
            writer.writeheader()
            writer.writerows(results)

    print()
    print("Wrote results.json and results.csv")


if __name__ == "__main__":
    asyncio.run(main())