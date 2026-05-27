#!/usr/bin/env python3
"""Substitute Aspire placeholders and normalise passwords in aspirate-output.

`aspirate generate --disable-secrets` leaves some placeholders unresolved and
embeds randomly-generated passwords. This script:
  1. Replaces every known {placeholder} pattern in deployment YAML files.
  2. Patches kustomization.yaml configMapGenerator literals to:
     a. resolve remaining {placeholder} patterns, and
     b. override Aspire-generated random passwords with deterministic CI values
        so services use stable credentials across deploys.

Passwords come from environment variables (POSTGRES_PASSWORD, REDIS_PASSWORD,
EVENTBUS_PASSWORD); hosts/ports use the K8s Service names Aspirate emits.
"""
from __future__ import annotations

import os
import re
import sys
from pathlib import Path
from urllib.parse import quote

import yaml

MANIFEST_DIR = Path("src/Argus.AppHost/aspirate-output")


def load_passwords() -> dict[str, str]:
    required = ("POSTGRES_PASSWORD", "REDIS_PASSWORD", "EVENTBUS_PASSWORD")
    missing = [k for k in required if not os.environ.get(k)]
    if missing:
        print(f"::error::Missing required secret env vars: {', '.join(missing)}")
        sys.exit(1)
    pg = os.environ["POSTGRES_PASSWORD"]
    rd = os.environ["REDIS_PASSWORD"]
    eb = os.environ["EVENTBUS_PASSWORD"]
    return {
        "pg":     pg,
        "rd":     rd,
        "eb":     eb,
        "pg_enc": quote(pg, safe=""),
        "rd_enc": quote(rd, safe=""),
        "eb_enc": quote(eb, safe=""),
    }


def build_replacements(pw: dict) -> dict[str, str]:
    pg, rd, eb = pw["pg"], pw["rd"], pw["eb"]
    pg_enc, rd_enc, eb_enc = pw["pg_enc"], pw["rd_enc"], pw["eb_enc"]
    pg_conn      = f"Host=postgres;Port=5432;Username=postgres;Password={pg}"
    pg_argus_db  = f"{pg_conn};Database=argusdb"
    redis_conn   = f"redis:6379,password={rd}"
    eventbus_uri = f"amqp://guest:{eb_enc}@eventbus:5672"
    # Order matters: more-specific patterns before shorter prefixes
    return {
        "{argusdb.connectionString}":                            pg_argus_db,
        "{postgres.connectionString}":                           pg_conn,
        "{eventbus.connectionString}":                           eventbus_uri,
        "{redis.connectionString}":                              redis_conn,
        "{postgres-password.value}":                             pg,
        "{redis-password.value}":                                rd,
        "{eventbus-password.value}":                             eb,
        "{eventbus-password-uri-encoded.value}":                 eb_enc,
        "{postgres-password-uri-encoded.value}":                 pg_enc,
        "{redis-password-uri-encoded.value}":                    rd_enc,
        "{postgres.bindings.tcp.host}":                          "postgres",
        "{postgres.bindings.tcp.port}":                          "5432",
        "{redis.bindings.tcp.host}":                             "redis",
        "{redis.bindings.tcp.port}":                             "6379",
        "{eventbus.bindings.tcp.host}":                          "eventbus",
        "{eventbus.bindings.tcp.port}":                          "5672",
        "{cond-redis-bindings-tcp-tlsenabled-d148d83a.connectionString}": "",
    }


def kustomization_overrides(pw: dict) -> list[tuple[str, str]]:
    """Regex rules that override Aspire-generated random passwords with ours.

    Aspirate resolves {xxx-password.value} to random strings during generate.
    We replace those with deterministic CI-derived values so deploys are stable.
    """
    pg, rd, eb = pw["pg"], pw["rd"], pw["eb"]
    pg_enc, rd_enc, eb_enc = pw["pg_enc"], pw["rd_enc"], pw["eb_enc"]
    pg_argusdb  = f"Host=postgres;Port=5432;Username=postgres;Password={pg};Database=argusdb"
    pg_conn     = f"Host=postgres;Port=5432;Username=postgres;Password={pg}"
    redis_conn  = f"redis:6379,password={rd}"
    eb_uri      = f"amqp://guest:{eb_enc}@eventbus:5672"
    pg_jdbc_uri = f"postgresql://postgres:{pg_enc}@postgres:5432/argusdb"
    rd_uri      = f"http://:{rd_enc}@redis:6379"
    return [
        (r"(?m)^(    - POSTGRES_PASSWORD=).*$",           rf"\g<1>{pg}"),
        (r"(?m)^(    - REDIS_PASSWORD=).*$",              rf"\g<1>{rd}"),
        (r"(?m)^(    - RABBITMQ_DEFAULT_PASS=).*$",       rf"\g<1>{eb}"),
        (r"(?m)^(    - EVENTBUS_PASSWORD=).*$",           rf"\g<1>{eb}"),
        (r"(?m)^(    - ARGUSDB_PASSWORD=).*$",            rf"\g<1>{pg}"),
        (r"(?m)^(    - ConnectionStrings__argusdb=).*$",  rf"\g<1>{pg_argusdb}"),
        (r"(?m)^(    - ConnectionStrings__postgres=).*$", rf"\g<1>{pg_conn}"),
        (r"(?m)^(    - ConnectionStrings__eventbus=).*$", rf"\g<1>{eb_uri}"),
        (r"(?m)^(    - ConnectionStrings__redis=).*$",    rf"\g<1>{redis_conn}"),
        (r"(?m)^(    - ARGUSDB_URI=).*$",                 rf"\g<1>{pg_jdbc_uri}"),
        (r"(?m)^(    - REDIS_URI=).*$",                   rf"\g<1>{rd_uri}"),
        (r"(?m)^(    - EVENTBUS_URI=).*$",                rf"\g<1>{eb_uri}"),
    ]


def patch_kustomization(path: Path, pw: dict, replacements: dict[str, str]) -> bool:
    content = path.read_text()
    new_content = content
    for placeholder, value in replacements.items():
        new_content = new_content.replace(placeholder, value)
    for pattern, repl in kustomization_overrides(pw):
        new_content = re.sub(pattern, repl, new_content)
    if new_content != content:
        path.write_text(new_content)
        print(f"  patched: {path}")
        return True
    return False


def substitute(value: str, replacements: dict[str, str]) -> tuple[str, bool]:
    new = value
    for placeholder, secret in replacements.items():
        new = new.replace(placeholder, secret)
    return new, new != value


def patch_deployment(path: Path, replacements: dict[str, str]) -> bool:
    docs = list(yaml.safe_load_all(path.read_text()))
    modified = False
    for doc in docs:
        if not isinstance(doc, dict) or doc.get("kind") not in ("Deployment", "StatefulSet"):
            continue
        pod_spec = doc.get("spec", {}).get("template", {}).get("spec", {})
        if not pod_spec:
            continue
        for container in pod_spec.get("containers", []):
            for env in container.get("env") or []:
                raw = env.get("value")
                if not isinstance(raw, str):
                    continue
                new_val, changed = substitute(raw, replacements)
                if changed:
                    env["value"] = new_val
                    modified = True
            for attr in ("args", "command"):
                items = container.get(attr)
                if isinstance(items, list):
                    for i, item in enumerate(items):
                        if not isinstance(item, str):
                            continue
                        new_val, changed = substitute(item, replacements)
                        if changed:
                            items[i] = new_val
                            modified = True
    if modified:
        with path.open("w") as f:
            yaml.dump_all(docs, f, default_flow_style=False, allow_unicode=True)
        print(f"  patched: {path}")
    return modified


def main() -> None:
    if not MANIFEST_DIR.exists():
        print(f"Manifest directory not found: {MANIFEST_DIR}")
        sys.exit(1)

    pw = load_passwords()
    replacements = build_replacements(pw)
    patched_files = 0

    for yaml_file in sorted(MANIFEST_DIR.rglob("*.yaml")):
        if "kustomization" in yaml_file.name:
            if patch_kustomization(yaml_file, pw, replacements):
                patched_files += 1
        else:
            if patch_deployment(yaml_file, replacements):
                patched_files += 1

    print(f"Aspire placeholders substituted in {patched_files} file(s).")


if __name__ == "__main__":
    main()
