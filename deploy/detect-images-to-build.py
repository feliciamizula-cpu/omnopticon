#!/usr/bin/env python3
"""Compatibility wrapper for the service-map driven incremental detector.

Older workflows called `deploy/detect-images-to-build.py`. Keep that path working,
but delegate to the maintained detector in `scripts/ci/detect-impacted-services.py`.
"""
from __future__ import annotations

import runpy
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "ci" / "detect-impacted-services.py"

if __name__ == "__main__":
    runpy.run_path(str(SCRIPT), run_name="__main__")
