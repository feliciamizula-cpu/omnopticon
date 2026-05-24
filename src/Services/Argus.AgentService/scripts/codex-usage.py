#!/usr/bin/env python3
"""
Reports Codex subscription usage from ~/.codex/state_5.sqlite.
Timestamps in that DB are Unix seconds. Outputs used-token counts over the
last 5h, 7d, and 30d windows. Source is tagged "subscription" so routing
score ignores these windows — subscription accounts have no hard token cap.
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

    result: dict = {}
    if used_5h  > 0: result["fiveHour"]      = {"used": used_5h,  "source": "subscription"}
    if used_24h > 0: result["twentyFourHour"] = {"used": used_24h, "source": "subscription"}
    if used_7d  > 0: result["weekly"]         = {"used": used_7d,  "source": "subscription"}
    if used_30d > 0: result["monthly"]        = {"used": used_30d, "source": "subscription"}

    print(json.dumps(result) if result else "{}")

except Exception:
    print("{}")
