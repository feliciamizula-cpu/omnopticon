# Benchmark Task 3 — Maintainability / Minimal Implementation Size

You are a principal .NET engineer. Create a small, runnable .NET solution in the current directory.

## Optimization goal
Correctness is mandatory. Among correct solutions, the winner is the implementation with the fewest non-blank, non-comment lines of production C# code under `src/`.

This is not code golf. Solutions are disqualified if they are unreadable, use generated code to hide implementation, put production code in tests, or use intentionally obfuscated formatting. Keep line length reasonable.

## Required output contract
You must create this exact project path:

- `src/MaintenanceBench/MaintenanceBench.csproj`

It must run with:

```bash
dotnet run -c Release --project src/MaintenanceBench -- --scenario standard --out score.json
```

The program must write `score.json` with this exact shape:

```json
{
  "task": "maintainability",
  "targets": 3,
  "domains": 3,
  "subdomains": 9,
  "urls": 18,
  "confirmedUrls": 18,
  "findings": 6,
  "eventsPublished": 57,
  "eventsConsumed": 57,
  "idempotencySkips": 4,
  "checksum": "<lowercase 64-char sha256 hex>"
}
```

## Required behavior
Implement a tiny event-driven recon workflow with clean boundaries and minimal code.

For scenario `standard`, process these target domains:

- `alpha.example.com`
- `beta.example.com`
- `gamma.example.com`

For each domain:

- Create one domain asset.
- Create three subdomain assets:
  - `www.{domain}`
  - `api.{domain}`
  - `admin.{domain}`
- Create two URL assets per subdomain:
  - `https://{subdomain}/`
  - `https://{subdomain}/admin`
- Confirm every URL.
- Create one finding for each URL containing `/admin`.

## Required event types
Use strongly typed C# event contracts for at least:

- `TargetSubmitted`
- `AssetCreated`
- `AssetConfirmed`
- `FindingCreated`

Each event must carry:

- `EventId`
- `OccurredAtUtc`
- `CorrelationId`
- `CausationId`
- `SchemaVersion`

## Idempotency requirement
The program must intentionally publish four duplicate events during the scenario and skip them through an inbox/idempotency mechanism. The output must report `idempotencySkips: 4`.

## Event count requirement
For the standard scenario, report:

- `eventsPublished`: 57
- `eventsConsumed`: 57

Count duplicate events as published and consumed, even when skipped by idempotency.

## Checksum requirement
Compute `checksum` as lowercase SHA-256 hex over the UTF-8 bytes of this deterministic stream, sorted ordinal by line before hashing:

```text
ASSET|{assetType}|{assetValue}\n
FINDING|{url}|HighValueUrl\n
```

Include every unique asset and every unique finding exactly once.

## Design requirements
- Keep the solution compact and readable.
- Use separate concepts for domain model, event contracts, event bus/dispatcher, handlers, and stores, even if they live in few files.
- Use dependency injection if it does not add excessive code.
- Include tests under `tests/MaintenanceBench.Tests`.
- `dotnet test -c Release` must pass.
- Do not ask questions. Make reasonable assumptions.

## Scoring
1. Build succeeds.
2. Tests pass.
3. `score.json` matches all required counts and checksum.
4. Count non-blank, non-comment production C# lines under `src/`. Fewer is better.
