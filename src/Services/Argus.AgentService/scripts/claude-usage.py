#!/usr/bin/env python3
"""
Reports Claude quota status by reading ~/.claude.json.
Outputs JSON with a fiveHour window if the extra-usage quota is exhausted,
otherwise outputs {} so all windows remain unknown (routable).
"""
import json
import os
import sys

claude_json = os.path.expanduser("~/.claude.json")

try:
    with open(claude_json) as f:
        data = json.load(f)
except Exception:
    print("{}")
    sys.exit(0)

disabled_reason = data.get("cachedExtraUsageDisabledReason")
if disabled_reason:
    print(json.dumps({
        "fiveHour": {
            "remaining": 0,
            "used": 0,
            "source": "claude-json"
        }
    }))
else:
    print("{}")
