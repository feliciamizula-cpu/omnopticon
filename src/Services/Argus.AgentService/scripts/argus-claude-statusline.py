#!/usr/bin/env python3
"""
Claude Code status-line sink for Argus.

Configure as a Claude Code statusLine command to capture the rate_limits fields
without scraping terminal UI:
  python3 /path/to/argus-claude-statusline.py
"""
import json
import os
import sys
import tempfile
from pathlib import Path


def main():
    raw = sys.stdin.read()
    try:
        data = json.loads(raw)
    except Exception:
        return 0

    output = os.environ.get("ARGUS_CLAUDE_STATUS_PATH", "/tmp/argus-claude-status.json")
    path = Path(output)
    path.parent.mkdir(parents=True, exist_ok=True)

    fd, temp_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=str(path.parent), text=True)
    try:
        with os.fdopen(fd, "w") as temp_file:
            json.dump(data, temp_file, separators=(",", ":"))
        os.replace(temp_name, path)
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass

    return 0


if __name__ == "__main__":
    sys.exit(main())
