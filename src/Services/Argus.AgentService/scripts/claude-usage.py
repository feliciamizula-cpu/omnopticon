#!/usr/bin/env python3
"""
Reports Claude Code subscription usage.

Preferred source: Claude Code status-line JSON captured by argus-claude-statusline.py.
Fallback source: a tiny authenticated Messages API request using Claude Code OAuth
credentials, then parsing Claude Code's unified subscription rate-limit headers.
"""
import glob
import json
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone


STATUS_MAX_AGE_SECONDS = int(os.environ.get("ARGUS_CLAUDE_STATUS_MAX_AGE_SECONDS", "3600"))
MODEL = os.environ.get("ARGUS_CLAUDE_USAGE_MODEL", "claude-haiku-4-5-20251001")
API_URL = os.environ.get("ARGUS_CLAUDE_API_URL", "https://api.anthropic.com/v1/messages")


def emit(value):
    print(json.dumps(value, separators=(",", ":")))


def iso_from_epoch(value):
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    if number > 10_000_000_000:
        number = number / 1000
    return datetime.fromtimestamp(number, tz=timezone.utc).isoformat()


def window_from_percent(label, used_percent, resets_at, source):
    try:
        used_percent = float(used_percent)
    except (TypeError, ValueError):
        return None

    if used_percent <= 1:
        used_percent *= 100

    used_percent = max(0, min(100, used_percent))
    return {
        "limit": 100,
        "used": round(used_percent, 1),
        "remaining": round(100 - used_percent, 1),
        "resetsAt": resets_at,
        "source": source,
    }


def read_statusline_file():
    explicit = os.environ.get("ARGUS_CLAUDE_STATUS_PATH")
    paths = [explicit] if explicit else []
    paths.extend(glob.glob("/tmp/argus-claude-status*.json"))
    paths.extend(glob.glob("/tmp/argus-claude-usage*.json"))
    paths = [p for p in paths if p]
    if not paths:
        return None

    paths = sorted(paths, key=lambda p: os.path.getmtime(p) if os.path.exists(p) else 0, reverse=True)
    now = time.time()
    for path in paths:
        try:
            if now - os.path.getmtime(path) > STATUS_MAX_AGE_SECONDS:
                continue
            with open(path) as f:
                data = json.load(f)
        except Exception:
            continue

        rate_limits = data.get("rate_limits") or data.get("rateLimits") or data
        result = {}
        details = []

        five_hour = rate_limits.get("five_hour") or rate_limits.get("fiveHour") or rate_limits.get("5h")
        seven_day = rate_limits.get("seven_day") or rate_limits.get("sevenDay") or rate_limits.get("weekly") or rate_limits.get("7d")

        if isinstance(five_hour, dict):
            result["fiveHour"] = window_from_percent(
                "5 hour",
                five_hour.get("used_percentage") or five_hour.get("usedPercent") or five_hour.get("utilization"),
                iso_from_epoch(five_hour.get("resets_at") or five_hour.get("resetsAt")),
                "claude-statusline",
            )

        if isinstance(seven_day, dict):
            result["weekly"] = window_from_percent(
                "weekly",
                seven_day.get("used_percentage") or seven_day.get("usedPercent") or seven_day.get("utilization"),
                iso_from_epoch(seven_day.get("resets_at") or seven_day.get("resetsAt")),
                "claude-statusline",
            )

        result = {k: v for k, v in result.items() if v}
        model = (data.get("model") or {}).get("display_name") if isinstance(data.get("model"), dict) else None
        if model:
            details.append({"key": "model", "label": "Last Claude model", "value": str(model), "source": "claude-statusline"})
        if details:
            result["details"] = details
        if result:
            return result

    return None


def read_credentials():
    env_token = os.environ.get("CLAUDE_CODE_OAUTH_TOKEN")
    if env_token:
        return {
            "accessToken": env_token,
            "subscriptionType": os.environ.get("CLAUDE_CODE_SUBSCRIPTION_TYPE", ""),
            "rateLimitTier": os.environ.get("CLAUDE_CODE_RATE_LIMIT_TIER", ""),
        }

    roots = []
    if os.environ.get("CLAUDE_CONFIG_DIR"):
        roots.append(os.environ["CLAUDE_CONFIG_DIR"])
    if os.environ.get("CLAUDE_HOME"):
        roots.append(os.environ["CLAUDE_HOME"])
    home = os.path.expanduser("~")
    roots.append(os.path.join(home, ".claude"))

    for root in roots:
        path = os.path.join(root, ".credentials.json")
        try:
            with open(path) as f:
                data = json.load(f)
            oauth = data.get("claudeAiOauth") or {}
            if oauth.get("accessToken"):
                return oauth
        except Exception:
            continue

    return None


def probe_headers(token):
    body = json.dumps({
        "model": MODEL,
        "max_tokens": 1,
        "messages": [{"role": "user", "content": "quota"}],
        "metadata": {"user_id": "argus-provider-usage"},
    }).encode("utf-8")
    headers = {
        "authorization": f"Bearer {token}",
        "anthropic-version": "2023-06-01",
        "anthropic-beta": "oauth-2025-04-20",
        "content-type": "application/json",
        "user-agent": "argus-provider-usage",
    }
    request = urllib.request.Request(API_URL, data=body, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=6) as response:
            return response.headers
    except urllib.error.HTTPError as exc:
        return exc.headers


def header_value(headers, name):
    return headers.get(name) or headers.get(name.lower())


def read_header_probe():
    creds = read_credentials()
    if not creds or not creds.get("accessToken"):
        return None

    try:
        headers = probe_headers(creds["accessToken"])
    except Exception:
        return None

    result = {}
    details = []
    source_parts = ["claude-oauth-probe"]
    if creds.get("subscriptionType"):
        source_parts.append(str(creds["subscriptionType"]))
    if creds.get("rateLimitTier"):
        source_parts.append(str(creds["rateLimitTier"]))
    source = " ".join(source_parts)

    for output_key, header_key in [("fiveHour", "5h"), ("weekly", "7d")]:
        used = header_value(headers, f"anthropic-ratelimit-unified-{header_key}-utilization")
        reset = header_value(headers, f"anthropic-ratelimit-unified-{header_key}-reset")
        if used is None:
            continue
        result[output_key] = window_from_percent(output_key, used, iso_from_epoch(reset), source)

    status = header_value(headers, "anthropic-ratelimit-unified-status")
    claim = header_value(headers, "anthropic-ratelimit-unified-representative-claim")
    overage = header_value(headers, "anthropic-ratelimit-unified-overage-status")
    for key, label, value in [
        ("status", "Claude usage status", status),
        ("claim", "Active Claude limit", claim),
        ("overage", "Claude overage", overage),
    ]:
        if value:
            details.append({"key": key, "label": label, "value": value, "source": source})

    result = {k: v for k, v in result.items() if v}
    if details:
        result["details"] = details
    return result or None


def read_legacy_disabled_reason():
    try:
        with open(os.path.expanduser("~/.claude.json")) as f:
            data = json.load(f)
    except Exception:
        return None

    disabled_reason = data.get("cachedExtraUsageDisabledReason")
    if not disabled_reason:
        return None
    return {
        "fiveHour": {
            "remaining": 100,
            "used": 0,
            "limit": 100,
            "source": "claude-json",
        },
        "details": [{
            "key": "extraUsage",
            "label": "Claude extra usage",
            "value": str(disabled_reason),
            "source": "claude-json",
        }],
    }


def main():
    for reader in (read_statusline_file, read_header_probe, read_legacy_disabled_reason):
        result = reader()
        if result:
            emit(result)
            return 0
    emit({})
    return 0


if __name__ == "__main__":
    sys.exit(main())
