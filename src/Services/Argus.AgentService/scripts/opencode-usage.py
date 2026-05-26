#!/usr/bin/env python3
"""
Reports OpenCode Go usage limits.

OpenCode publishes Go limits in dollar value and currently exposes live usage in
the console. Until a quota endpoint is available, Argus accepts optional
OPENCODE_GO_* environment values copied from the console and always returns the
documented limits as details.
"""
import json
import os
import sys


DOCUMENTED_LIMITS = {
    "fiveHour": ("5 hour", 12),
    "weekly": ("weekly", 30),
    "monthly": ("monthly", 60),
}

ENV_ALIASES = {
    "fiveHour": ("FIVE_HOUR", "5H"),
    "weekly": ("WEEKLY", "7D"),
    "monthly": ("MONTHLY", "30D"),
}


def emit(value):
    print(json.dumps(value, separators=(",", ":")))


def env_value(window, name):
    for alias in ENV_ALIASES[window]:
        for prefix in ("OPENCODE_GO", "OPENCODE"):
            value = os.environ.get(f"{prefix}_{alias}_{name}")
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


def window_from_env(window):
    label, documented_limit = DOCUMENTED_LIMITS[window]
    limit = decimal_env(window, "LIMIT") or documented_limit
    used = decimal_env(window, "USED")
    remaining = decimal_env(window, "REMAINING")
    resets_at = env_value(window, "RESETS_AT") or env_value(window, "RESET_AT")

    if used is None and remaining is None:
        return None

    result = {
        "limit": round(limit, 4),
        "source": "opencode-go-console",
    }
    if used is not None:
        result["used"] = round(max(0, used), 4)
    if remaining is not None:
        result["remaining"] = round(max(0, remaining), 4)
    if resets_at:
        result["resetsAt"] = resets_at
    if label:
        result["label"] = label
    return result


def main():
    result = {}
    details = []

    for window, (label, limit) in DOCUMENTED_LIMITS.items():
        usage_window = window_from_env(window)
        if usage_window is not None:
            result[window] = usage_window

        details.append({
            "key": f"{window}:limit",
            "label": f"OpenCode Go {label} limit",
            "value": f"${limit:g}",
            "source": "opencode-go-docs",
        })

    details.append({
        "key": "usageSource",
        "label": "OpenCode Go usage source",
        "value": "Set OPENCODE_GO_* usage values from the console until a quota API is available.",
        "source": "opencode-go-console",
    })

    result["details"] = details
    emit(result)
    return 0


if __name__ == "__main__":
    sys.exit(main())
