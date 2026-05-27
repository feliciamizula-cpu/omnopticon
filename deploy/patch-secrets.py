#!/usr/bin/env python3
"""Substitute Aspire placeholders that --disable-secrets leaves unresolved.

When `aspirate generate --disable-secrets` runs, password-bearing placeholders
like `{eventbus-password.value}` and any composite placeholders that reference
them (connection strings, AMQP URIs) are left as literal text. Services then
fail at startup because their connection strings are unresolvable.

This script substitutes every known Aspire placeholder pattern with concrete
values. Passwords come from environment variables (POSTGRES_PASSWORD,
REDIS_PASSWORD, EVENTBUS_PASSWORD); hosts/ports use the K8s Service names that
Aspirate emits.
"""
from __future__ import annotations

import os
import sys
from pathlib import Path
from urllib.parse import quote

import yaml

MANIFEST_DIR = Path("src/Argus.AppHost/aspirate-output")


def load_replacements() -> dict[str, str]:
    required = ("POSTGRES_PASSWORD", "REDIS_PASSWORD", "EVENTBUS_PASSWORD")
    missing = [k for k in required if not os.environ.get(k)]
    if missing:
        print(f"::error::Missing required secret env vars: {', '.join(missing)}")
        sys.exit(1)

    pg = os.environ["POSTGRES_PASSWORD"]
    rd = os.environ["REDIS_PASSWORD"]
    eb = os.environ["EVENTBUS_PASSWORD"]
    eb_enc = quote(eb, safe="")

    pg_conn      = f"Host=postgres;Port=5432;Username=postgres;Password={pg}"
    pg_argus_db  = f"{pg_conn};Database=argusdb"
    redis_conn   = f"redis:6379,password={rd}"
    eventbus_uri = f"amqp://guest:{eb_enc}@eventbus:5672"

    # Order matters: more-specific patterns must come BEFORE shorter prefixes
    # of the same family so a substring rule doesn't fire first.
    return {
        # Argusdb (composed from postgres) — must be before postgres.connectionString
        "{argusdb.connectionString}":                pg_argus_db,
        "{postgres.connectionString}":               pg_conn,
        "{eventbus.connectionString}":               eventbus_uri,
        "{redis.connectionString}":                  redis_conn,
        # Password tokens
        "{postgres-password.value}":                 pg,
        "{redis-password.value}":                    rd,
        "{eventbus-password.value}":                 eb,
        "{eventbus-password-uri-encoded.value}":     eb_enc,
        # Host/port bindings (Aspirate maps these to the K8s Service name+port)
        "{postgres.bindings.tcp.host}":              "postgres",
        "{postgres.bindings.tcp.port}":              "5432",
        "{redis.bindings.tcp.host}":                 "redis",
        "{redis.bindings.tcp.port}":                 "6379",
        "{eventbus.bindings.tcp.host}":              "eventbus",
        "{eventbus.bindings.tcp.port}":              "5672",
        # Conditional connection-string fragment Aspire emits for Redis TLS;
        # we disable TLS, so it collapses to empty.
        "{cond-redis-bindings-tcp-tlsenabled-d148d83a.connectionString}": "",
    }


def substitute(value: str, replacements: dict[str, str]) -> tuple[str, bool]:
    new = value
    for placeholder, secret in replacements.items():
        new = new.replace(placeholder, secret)
    return new, new != value


def patch_doc(doc: dict, replacements: dict[str, str]) -> bool:
    if not isinstance(doc, dict):
        return False
    if doc.get("kind") not in ("Deployment", "StatefulSet"):
        return False

    pod_spec = doc.get("spec", {}).get("template", {}).get("spec", {})
    if not pod_spec:
        return False

    modified = False
    for container in pod_spec.get("containers", []):
        for env in container.get("env") or []:
            raw = env.get("value")
            if not isinstance(raw, str):
                continue
            new_val, changed = substitute(raw, replacements)
            if changed:
                env["value"] = new_val
                modified = True

        args = container.get("args")
        if isinstance(args, list):
            for i, arg in enumerate(args):
                if not isinstance(arg, str):
                    continue
                new_val, changed = substitute(arg, replacements)
                if changed:
                    args[i] = new_val
                    modified = True

        cmd = container.get("command")
        if isinstance(cmd, list):
            for i, c in enumerate(cmd):
                if not isinstance(c, str):
                    continue
                new_val, changed = substitute(c, replacements)
                if changed:
                    cmd[i] = new_val
                    modified = True
    return modified


def main() -> None:
    if not MANIFEST_DIR.exists():
        print(f"Manifest directory not found: {MANIFEST_DIR}")
        sys.exit(1)

    replacements = load_replacements()
    patched_files = 0

    for yaml_file in sorted(MANIFEST_DIR.rglob("*.yaml")):
        if "kustomization" in yaml_file.name:
            continue
        docs = list(yaml.safe_load_all(yaml_file.read_text()))
        any_changed = False
        for doc in docs:
            if patch_doc(doc, replacements):
                any_changed = True
        if any_changed:
            with yaml_file.open("w") as f:
                yaml.dump_all(docs, f, default_flow_style=False, allow_unicode=True)
            patched_files += 1
            print(f"  patched: {yaml_file}")

    print(f"Aspire placeholders substituted in {patched_files} file(s).")


if __name__ == "__main__":
    main()
