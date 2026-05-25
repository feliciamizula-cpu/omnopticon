#!/usr/bin/env python3
"""
Reports Codex subscription usage through the experimental Codex app-server.

The app-server exposes account/rateLimits/read over JSON-RPC. The dashboard
expects a small provider-window JSON shape, so this maps Codex's percent-used
windows onto a 0-100 percent scale.
"""
import json
import select
import subprocess
import sys
import time
from datetime import datetime, timezone


TIMEOUT_SECONDS = 12


def iso_from_unix(value):
    if not isinstance(value, (int, float)) or value <= 0:
        return None
    return datetime.fromtimestamp(value, timezone.utc).isoformat()


def read_json_line(process, request_id, deadline):
    while time.monotonic() < deadline:
        timeout = max(0.0, deadline - time.monotonic())
        readable, _, _ = select.select([process.stdout], [], [], timeout)
        if not readable:
            break

        line = process.stdout.readline()
        if not line:
            break

        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue

        if message.get("id") == request_id:
            return message

    return None


def send(process, request_id, method, params=None):
    payload = {"jsonrpc": "2.0", "id": request_id, "method": method}
    if params is not None:
        payload["params"] = params
    process.stdin.write(json.dumps(payload) + "\n")
    process.stdin.flush()


def window_from_codex(name, payload):
    if not isinstance(payload, dict):
        return None

    used_percent = payload.get("usedPercent")
    if not isinstance(used_percent, (int, float)):
        return None

    used = max(0.0, min(100.0, float(used_percent)))
    reset = iso_from_unix(payload.get("resetsAt"))

    window = {
        "limit": 100,
        "used": round(used, 1),
        "remaining": round(max(0.0, 100.0 - used), 1),
        "source": "codex-app-server",
    }
    if reset is not None:
        window["resetsAt"] = reset
    if payload.get("windowDurationMins") is not None:
        window["windowDurationMins"] = payload.get("windowDurationMins")
    if name:
        window["label"] = name
    return window


def main():
    process = None
    try:
        process = subprocess.Popen(
            ["codex", "app-server", "--listen", "stdio://"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )

        deadline = time.monotonic() + TIMEOUT_SECONDS
        send(
            process,
            1,
            "initialize",
            {
                "clientInfo": {
                    "name": "argus-provider-usage",
                    "title": "Argus Provider Usage",
                    "version": "0.1.0",
                },
                "capabilities": None,
            },
        )
        init = read_json_line(process, 1, deadline)
        if init is None or "error" in init:
            print("{}")
            return

        send(process, 2, "account/rateLimits/read")
        response = read_json_line(process, 2, deadline)
        if response is None or "error" in response:
            print("{}")
            return

        result = response.get("result") or {}
        buckets = result.get("rateLimitsByLimitId") or {}
        codex = buckets.get("codex") or result.get("rateLimits") or {}

        output = {}
        primary = window_from_codex("5 hour", codex.get("primary"))
        secondary = window_from_codex("weekly", codex.get("secondary"))
        if primary is not None:
            output["fiveHour"] = primary
        if secondary is not None:
            output["weekly"] = secondary

        credits = codex.get("credits")
        if isinstance(credits, dict):
            output["credits"] = credits
        if codex.get("planType"):
            output["planType"] = codex.get("planType")
        if codex.get("rateLimitReachedType"):
            output["rateLimitReachedType"] = codex.get("rateLimitReachedType")

        print(json.dumps(output))
    except Exception:
        print("{}")
    finally:
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()


if __name__ == "__main__":
    main()
