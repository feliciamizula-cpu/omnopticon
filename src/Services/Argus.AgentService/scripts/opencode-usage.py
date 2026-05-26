#!/usr/bin/env python3
"""
Reports OpenCode Go usage from the local opencode database.

Queries the opencode SQLite database (~/.local/share/opencode/opencode.db)
for per-session cost data and computes rolling-window usage against the
documented subscription limits ($12/5h, $30/7d, $60/30d).

Falls back to OPENCODE_GO_* environment variables when the database
is unavailable.
"""
import json
import os
import sqlite3
import sys
from datetime import datetime, timezone, timedelta


DOCUMENTED_LIMITS = {
    "fiveHour": ("5 hour", 12.0, timedelta(hours=5)),
    "weekly": ("weekly", 30.0, timedelta(days=7)),
    "monthly": ("monthly", 60.0, timedelta(days=30)),
}

ENV_ALIASES = {
    "fiveHour": ("FIVE_HOUR", "5H"),
    "weekly": ("WEEKLY", "7D"),
    "monthly": ("MONTHLY", "30D"),
}


def emit(value):
    print(json.dumps(value, separators=(",", ":")))


def _db_candidates():
    explicit = os.environ.get("OPENCODE_DB_PATH")
    if explicit:
        yield explicit

    xdg = os.environ.get("XDG_DATA_HOME")
    if xdg:
        yield os.path.join(xdg, "opencode", "opencode.db")

    home = os.path.expanduser("~")
    yield os.path.join(home, ".local", "share", "opencode", "opencode.db")


def _open_db():
    for path in _db_candidates():
        if os.path.exists(path):
            try:
                return sqlite3.connect(f"file:{path}?mode=ro", uri=True)
            except Exception:
                continue
    return None


def _iso_from_ms(value_ms):
    if value_ms is None:
        return None
    return datetime.fromtimestamp(value_ms / 1000, tz=timezone.utc).isoformat()


def read_db_windows():
    db = _open_db()
    if db is None:
        return None

    try:
        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        result = {}

        for window_key, (label, limit, duration) in DOCUMENTED_LIMITS.items():
            cutoff_ms = now_ms - int(duration.total_seconds() * 1000)

            row = db.execute(
                """
                SELECT COALESCE(SUM(cost), 0.0), MIN(time_created)
                FROM session
                WHERE json_extract(model, '$.providerID') = 'opencode-go'
                  AND time_created > ?
                """,
                (cutoff_ms,),
            ).fetchone()

            used = round(float(row[0] or 0.0), 4)
            oldest_ms = row[1]
            remaining = round(max(0.0, limit - used), 4)

            # The window "resets" when the oldest session in the window falls out.
            resets_at = _iso_from_ms(oldest_ms + int(duration.total_seconds() * 1000)) if oldest_ms else None

            window = {
                "limit": limit,
                "used": used,
                "remaining": remaining,
                "label": label,
                "source": "opencode-db",
            }
            if resets_at:
                window["resetsAt"] = resets_at
            result[window_key] = window

        return result

    except Exception:
        return None
    finally:
        db.close()


def _env_value(window, name):
    for alias in ENV_ALIASES[window]:
        for prefix in ("OPENCODE_GO", "OPENCODE"):
            value = os.environ.get(f"{prefix}_{alias}_{name}")
            if value not in (None, ""):
                return value
    return None


def _decimal_env(window, name):
    value = _env_value(window, name)
    if value is None:
        return None
    try:
        return float(value)
    except ValueError:
        return None


def read_env_windows():
    result = {}
    for window_key, (label, limit, _) in DOCUMENTED_LIMITS.items():
        env_limit = _decimal_env(window_key, "LIMIT") or limit
        used = _decimal_env(window_key, "USED")
        remaining = _decimal_env(window_key, "REMAINING")
        resets_at = _env_value(window_key, "RESETS_AT") or _env_value(window_key, "RESET_AT")

        if used is None and remaining is None:
            continue

        window = {"limit": round(env_limit, 4), "label": label, "source": "opencode-go-console"}
        if used is not None:
            window["used"] = round(max(0.0, used), 4)
        if remaining is not None:
            window["remaining"] = round(max(0.0, remaining), 4)
        if resets_at:
            window["resetsAt"] = resets_at
        result[window_key] = window

    return result or None


def main():
    result = read_db_windows()
    source_note = None

    if result is None:
        result = read_env_windows() or {}
        if result:
            source_note = "Set OPENCODE_GO_* usage values from the console; local database was unavailable."

    details = []
    if source_note:
        details.append({
            "key": "usageSource",
            "label": "OpenCode Go usage source",
            "value": source_note,
            "source": "opencode-go-console",
        })
    for window_key, (label, limit, _) in DOCUMENTED_LIMITS.items():
        details.append({
            "key": f"{window_key}:limit",
            "label": f"OpenCode Go {label} limit",
            "value": f"${limit:g}",
            "source": "opencode-go-docs",
        })
    result["details"] = details

    emit(result)
    return 0


if __name__ == "__main__":
    sys.exit(main())
