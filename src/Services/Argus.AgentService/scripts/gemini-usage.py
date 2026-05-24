#!/usr/bin/env python3
"""
Reports Gemini quota status by scanning /tmp/gemini-client-error-*.json files.
These files are written by the Gemini CLI when a quota-exhaustion error occurs.
Parses the human-readable reset duration from the error message, checks whether
the quota has already reset, and outputs the appropriate usage window JSON.
Outputs {} when quota is not exhausted or cannot be determined.
"""
import glob
import json
import os
import re
import sys
import time
from datetime import datetime, timedelta, timezone

MAX_FILE_AGE_SECONDS = 86400  # ignore files older than 24 hours

error_files = sorted(
    glob.glob("/tmp/gemini-client-error-*.json"),
    key=os.path.getmtime,
    reverse=True,
)

for path in error_files:
    try:
        mtime = os.path.getmtime(path)
        if time.time() - mtime > MAX_FILE_AGE_SECONDS:
            continue

        with open(path) as f:
            data = json.load(f)

        message = data.get("error", {}).get("message", "")
        if "exhausted" not in message.lower() and "quota" not in message.lower():
            continue

        # Parse "reset after Xh Ym Zs" from the error message.
        m = re.search(r"reset after\s+((?:\d+h)?(?:\d+m)?(?:\d+s)?)", message)
        if not m or not m.group(1):
            # No parseable duration — if the file is very recent, assume still exhausted.
            if time.time() - mtime < 3600:
                print(json.dumps({"fiveHour": {"remaining": 0, "used": 0, "source": "gemini-error-file"}}))
            else:
                print("{}")
            sys.exit(0)

        duration_str = m.group(1)
        hours = int(re.search(r"(\d+)h", duration_str).group(1)) if "h" in duration_str else 0
        minutes = int(re.search(r"(\d+)m", duration_str).group(1)) if "m" in duration_str else 0
        seconds = int(re.search(r"(\d+)s", duration_str).group(1)) if "s" in duration_str else 0
        delta = timedelta(hours=hours, minutes=minutes, seconds=seconds)

        file_time = datetime.fromtimestamp(mtime, tz=timezone.utc)
        reset_time = file_time + delta
        now = datetime.now(timezone.utc)

        if reset_time <= now:
            # Quota has already reset — treat as available.
            print("{}")
            sys.exit(0)

        print(json.dumps({
            "fiveHour": {
                "remaining": 0,
                "used": 0,
                "resetsAt": reset_time.isoformat(),
                "source": "gemini-error-file",
            }
        }))
        sys.exit(0)

    except Exception:
        continue

print("{}")
