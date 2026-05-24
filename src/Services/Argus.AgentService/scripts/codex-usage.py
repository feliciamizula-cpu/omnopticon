#!/usr/bin/env python3
"""
Reports Codex subscription usage from ~/.codex/state_5.sqlite.
Timestamps in that DB are Unix seconds. Outputs used-token counts over the
last 5h, 24h, 7d, and 30d windows. If explicit CODEX_*_LIMIT env vars are
configured, the matching windows include limits and remaining counts.
"""
import json
import os
import sqlite3
import sys
import time

db_path = os.path.expanduser("~/.codex/state_5.sqlite")

if not os.path.exists(db_path):
    print("{}")
    sys.exit(0)

try:
    now = int(time.time())
    five_h   = now - 5  * 3600
    twenty_four_h = now - 24 * 3600
    seven_d  = now - 7  * 86400
    thirty_d = now - 30 * 86400

    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    cur = con.cursor()

    def sum_tokens(since: int) -> int:
        row = cur.execute(
            "SELECT COALESCE(SUM(tokens_used), 0) FROM threads WHERE created_at >= ?",
            (since,),
        ).fetchone()
        return int(row[0]) if row else 0

    used_5h   = sum_tokens(five_h)
    used_24h  = sum_tokens(twenty_four_h)
    used_7d   = sum_tokens(seven_d)
    used_30d  = sum_tokens(thirty_d)
    con.close()

    def window(used: int, env_name: str) -> dict | None:
        raw_limit = os.environ.get(env_name, "").strip()
        limit = int(raw_limit) if raw_limit.isdigit() and int(raw_limit) > 0 else 0
        if used <= 0 and limit <= 0:
            return None

        payload = {"used": used, "source": "subscription"}
        if limit > 0:
            payload["limit"] = limit
            payload["remaining"] = max(0, limit - used)
            payload["source"] = "configured-plan"
        return payload

    result: dict = {}
    for key, value in {
        "fiveHour": window(used_5h, "CODEX_FIVE_HOUR_LIMIT"),
        "twentyFourHour": window(used_24h, "CODEX_TWENTY_FOUR_HOUR_LIMIT"),
        "weekly": window(used_7d, "CODEX_WEEKLY_LIMIT"),
        "monthly": window(used_30d, "CODEX_MONTHLY_LIMIT"),
    }.items():
        if value is not None:
            result[key] = value

    print(json.dumps(result) if result else "{}")

except Exception:
    print("{}")
