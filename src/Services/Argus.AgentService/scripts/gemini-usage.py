#!/usr/bin/env python3
"""
Reports Gemini CLI / Code Assist quota status.

Primary source: Google Code Assist retrieveUserQuota, using Gemini CLI OAuth
credentials. Fallback source: recent Gemini CLI quota-exhaustion error files.
"""
import glob
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone


MAX_FILE_AGE_SECONDS = 86400
CODE_ASSIST_ENDPOINT = os.environ.get("CODE_ASSIST_ENDPOINT", "https://cloudcode-pa.googleapis.com")
CODE_ASSIST_VERSION = os.environ.get("CODE_ASSIST_API_VERSION", "v1internal")
OAUTH_CLIENT_ID = os.environ.get("GEMINI_OAUTH_CLIENT_ID")
OAUTH_CLIENT_SECRET = os.environ.get("GEMINI_OAUTH_CLIENT_SECRET")


def emit(value):
    print(json.dumps(value, separators=(",", ":")))


def request_json(url, token, payload):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        headers={
            "authorization": f"Bearer {token}",
            "content-type": "application/json",
            "user-agent": "argus-provider-usage",
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=6) as response:
        return json.loads(response.read().decode("utf-8") or "{}")


def code_assist_url(method):
    return f"{CODE_ASSIST_ENDPOINT.rstrip('/')}/{CODE_ASSIST_VERSION}:{method}"


def credential_paths():
    explicit = os.environ.get("GEMINI_OAUTH_CREDS")
    if explicit:
        yield explicit
    home = os.path.expanduser("~")
    yield os.path.join(home, ".gemini", "oauth_creds.json")
    if os.environ.get("GOOGLE_APPLICATION_CREDENTIALS"):
        yield os.environ["GOOGLE_APPLICATION_CREDENTIALS"]


def read_credentials():
    env_token = os.environ.get("GOOGLE_CLOUD_ACCESS_TOKEN")
    if env_token:
        return {"access_token": env_token}

    for path in credential_paths():
        try:
            with open(path) as f:
                data = json.load(f)
            if data.get("access_token") or data.get("refresh_token"):
                return data
        except Exception:
            continue
    return None


def refresh_access_token(creds):
    client_id = creds.get("client_id") or OAUTH_CLIENT_ID
    client_secret = creds.get("client_secret") or OAUTH_CLIENT_SECRET
    refresh_token = creds.get("refresh_token")
    if not client_id or not client_secret or not refresh_token:
        raise RuntimeError("Gemini OAuth refresh requires GEMINI_OAUTH_CLIENT_ID and GEMINI_OAUTH_CLIENT_SECRET")

    payload = urllib.parse.urlencode({
        "client_id": client_id,
        "client_secret": client_secret,
        "refresh_token": refresh_token,
        "grant_type": "refresh_token",
    }).encode("utf-8")
    req = urllib.request.Request(
        "https://oauth2.googleapis.com/token",
        data=payload,
        headers={"content-type": "application/x-www-form-urlencoded"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=6) as response:
        return json.loads(response.read().decode("utf-8")).get("access_token")


def get_access_token(creds):
    token = creds.get("access_token")
    expiry = creds.get("expiry_date")
    if token and (not expiry or int(expiry) / 1000 > time.time() + 60):
        return token
    if creds.get("refresh_token"):
        try:
            return refresh_access_token(creds)
        except Exception:
            return token
    return token


def load_code_assist(token, project_id):
    metadata = {
        "ideType": "IDE_UNSPECIFIED",
        "platform": "PLATFORM_UNSPECIFIED",
        "pluginType": "GEMINI",
    }
    if project_id:
        metadata["duetProject"] = project_id

    payload = {
        "metadata": metadata,
    }
    if project_id:
        payload["cloudaicompanionProject"] = project_id

    return request_json(code_assist_url("loadCodeAssist"), token, payload)


def parse_quota(quota, load_response, project_id):
    buckets = quota.get("buckets") or []
    details = []
    known_windows = []

    for bucket in buckets:
        model_id = bucket.get("modelId")
        fraction = bucket.get("remainingFraction")
        if not model_id or fraction is None:
            continue

        try:
            fraction = float(fraction)
        except (TypeError, ValueError):
            continue

        amount = bucket.get("remainingAmount")
        estimated = False
        if amount is not None:
            try:
                remaining = int(amount)
            except (TypeError, ValueError):
                continue
            limit = round(remaining / fraction) if fraction > 0 else remaining
        else:
            estimated = True
            limit = 100
            remaining = round(fraction * 100)

        if limit <= 0:
            continue

        used = max(0, limit - remaining)
        resets_at = bucket.get("resetTime")
        pct = round(max(0, min(100, remaining / limit * 100)), 1)
        known_windows.append({
            "limit": limit,
            "used": used,
            "remaining": remaining,
            "resetsAt": resets_at,
            "source": "gemini-code-assist" + (" estimated" if estimated else ""),
            "percent": pct,
        })
        details.append({
            "key": f"model:{model_id}",
            "label": model_id,
            "value": f"{remaining}/{limit} requests ({pct:g}% remaining)" + (" estimated" if estimated else ""),
            "source": "gemini-code-assist",
            "resetsAt": resets_at,
        })

    result = {}
    if known_windows:
        # Route conservatively on the most constrained model bucket.
        daily = min(known_windows, key=lambda item: item["percent"])
        result["daily"] = {k: v for k, v in daily.items() if k != "percent"}

    tier = (load_response.get("paidTier") or load_response.get("currentTier") or {})
    tier_name = tier.get("name") or tier.get("id")
    if tier_name:
        details.insert(0, {
            "key": "tier",
            "label": "Gemini quota tier",
            "value": str(tier_name),
            "source": "gemini-code-assist",
        })
    if project_id:
        details.insert(0, {
            "key": "project",
            "label": "Code Assist project",
            "value": project_id,
            "source": "gemini-code-assist",
        })

    if details:
        result["details"] = details

    return result


def read_code_assist_quota():
    creds = read_credentials()
    if not creds:
        return None
    token = get_access_token(creds)
    if not token:
        return None

    project_id = os.environ.get("GOOGLE_CLOUD_PROJECT") or os.environ.get("GOOGLE_CLOUD_PROJECT_ID")
    try:
        load_response = load_code_assist(token, project_id)
        companion_project = load_response.get("cloudaicompanionProject")
        if not project_id and isinstance(companion_project, str):
            project_id = companion_project
        if not project_id and isinstance(companion_project, dict):
            project_id = companion_project.get("id")
        if not project_id:
            return parse_quota({}, load_response, None) or None
        quota = request_json(code_assist_url("retrieveUserQuota"), token, {"project": project_id})
        return parse_quota(quota, load_response, project_id)
    except Exception:
        return None


def read_error_files():
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

            match = re.search(r"reset after\s+((?:\d+h)?(?:\d+m)?(?:\d+s)?)", message)
            if not match or not match.group(1):
                if time.time() - mtime < 3600:
                    return {"daily": {"remaining": 0, "used": 100, "limit": 100, "source": "gemini-error-file"}}
                return None

            duration = match.group(1)
            hours = int(re.search(r"(\d+)h", duration).group(1)) if "h" in duration else 0
            minutes = int(re.search(r"(\d+)m", duration).group(1)) if "m" in duration else 0
            seconds = int(re.search(r"(\d+)s", duration).group(1)) if "s" in duration else 0
            reset_time = datetime.fromtimestamp(mtime, tz=timezone.utc) + timedelta(hours=hours, minutes=minutes, seconds=seconds)

            if reset_time <= datetime.now(timezone.utc):
                return None

            return {
                "daily": {
                    "remaining": 0,
                    "used": 100,
                    "limit": 100,
                    "resetsAt": reset_time.isoformat(),
                    "source": "gemini-error-file",
                }
            }
        except Exception:
            continue

    return None


def main():
    for reader in (read_code_assist_quota, read_error_files):
        result = reader()
        if result:
            emit(result)
            return 0
    emit({})
    return 0


if __name__ == "__main__":
    sys.exit(main())
