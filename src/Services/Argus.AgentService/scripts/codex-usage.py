#!/usr/bin/env python3
"""
Reports Codex subscription usage through the experimental Codex app-server.

The app-server exposes account/rateLimits/read over JSON-RPC. The dashboard
expects a small provider-window JSON shape, so this maps Codex's percent-used
windows onto a 0-100 percent scale.
"""
import json
import os
import select
import subprocess
import time
from datetime import datetime, timezone


TIMEOUT_SECONDS = 12
ENV_ALIASES = {
    "fiveHour": ("FIVE_HOUR", "5H"),
    "twentyFourHour": ("TWENTY_FOUR_HOUR", "24H"),
    "weekly": ("WEEKLY", "7D"),
    "monthly": ("MONTHLY", "30D"),
}


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


def window_from_codex(name, payload, source):
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
        "source": source,
    }
    if reset is not None:
        window["resetsAt"] = reset
    if payload.get("windowDurationMins") is not None:
        window["windowDurationMins"] = payload.get("windowDurationMins")
    if name:
        window["label"] = name
    return window


def env_value(window, name):
    for alias in ENV_ALIASES[window]:
        value = os.environ.get(f"CODEX_{alias}_{name}")
        if value not in (None, ""):
            return value
    return None


def decimal_env(window, name):
    value = env_value(window, name)
    if value is None:
        return None
    try:
        return float(value)
    except ValueError:
        return None


def env_window(window, label):
    limit = decimal_env(window, "LIMIT")
    used = decimal_env(window, "USED")
    remaining = decimal_env(window, "REMAINING")
    resets_at = env_value(window, "RESETS_AT") or env_value(window, "RESET_AT")
    if limit is None or limit <= 0:
        return None

    if used is None and remaining is None:
        remaining = limit

    result = {
        "limit": round(limit, 4),
        "source": "configuration",
        "label": label,
    }
    if used is not None:
        result["used"] = round(max(0.0, used), 4)
    if remaining is not None:
        result["remaining"] = round(max(0.0, remaining), 4)
    if resets_at:
        result["resetsAt"] = resets_at
    return result


def fallback_from_env():
    output = {}
    for key, label in [
        ("fiveHour", "5 hour"),
        ("twentyFourHour", "24 hour"),
        ("weekly", "weekly"),
        ("monthly", "monthly"),
    ]:
        window = env_window(key, label)
        if window is not None:
            output[key] = window

    if output:
        output["details"] = [{
            "key": "usageSource",
            "label": "Codex usage source",
            "value": "Configured CODEX_* usage values; app-server quota probe was unavailable.",
            "source": "configuration",
        }]
    return output


def emit_fallback():
    print(json.dumps(fallback_from_env()))


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
            emit_fallback()
            return

        send(process, 2, "account/rateLimits/read")
        response = read_json_line(process, 2, deadline)
        if response is None or "error" in response:
            emit_fallback()
            return

        result = response.get("result") or {}
        buckets = result.get("rateLimitsByLimitId") or {}
        codex = buckets.get("codex") or result.get("rateLimits") or {}
        plan_type = codex.get("planType")
        source = f"codex-app-server ({plan_type})" if plan_type else "codex-app-server"

        output = {}
        primary = window_from_codex("5 hour", codex.get("primary"), source)
        secondary = window_from_codex("weekly", codex.get("secondary"), source)
        if primary is not None:
            output["fiveHour"] = primary
        if secondary is not None:
            output["weekly"] = secondary

        details = []
        credits = codex.get("credits")
        if isinstance(credits, dict):
            remaining = credits.get("remaining") or credits.get("remainingCredits")
            total = credits.get("total") or credits.get("limit") or credits.get("totalCredits")
            if remaining is not None or total is not None:
                value = f"{remaining if remaining is not None else '?'}"
                if total is not None:
                    value = f"{value}/{total}"
                details.append({
                    "key": "credits",
                    "label": "Codex credits",
                    "value": str(value),
                    "source": source,
                })
        if plan_type:
            details.append({
                "key": "planType",
                "label": "Codex plan",
                "value": str(plan_type),
                "source": source,
            })
        if codex.get("rateLimitReachedType"):
            details.append({
                "key": "rateLimitReachedType",
                "label": "Codex rate limit",
                "value": str(codex.get("rateLimitReachedType")),
                "source": source,
            })
        if details:
            output["details"] = details

        print(json.dumps(output))
    except Exception:
        emit_fallback()
    finally:
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()


if __name__ == "__main__":
    main()
