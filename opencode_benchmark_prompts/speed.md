# Benchmark Task 1 — Speed / Runtime Optimization

You are a senior .NET engineer. Create a small, runnable .NET solution in the current directory.

## Optimization goal
Correctness is mandatory. Among correct solutions, the winner is the implementation with the lowest runtime for the benchmark command.

## Required output contract
You must create this exact project path:

- `src/SpeedBench/SpeedBench.csproj`

It must run with:

```bash
dotnet run -c Release --project src/SpeedBench -- --domains 5000 --subdomains 8 --urls 25 --seed 123 --out score.json
```

The program must write `score.json` with this exact shape:

```json
{
  "task": "speed",
  "domains": 5000,
  "subdomains": 40000,
  "urls": 1000000,
  "confirmedUrls": 1000000,
  "findings": 120000,
  "checksum": "<lowercase 64-char sha256 hex>",
  "elapsedMsInsideProcess": 0
}
```

`elapsedMsInsideProcess` can be any non-negative integer. External benchmarking will use wall-clock time.

## Deterministic workload
Generate a simulated event-driven recon pipeline entirely in memory. Do not perform network I/O.

For domain index `d` from `0` to `domains - 1`:

- Domain value: `target-{d}.example.com`
- Generate `subdomains` subdomains per domain.
- Subdomain value: `s{s}.target-{d}.example.com`
- Generate `urls` URLs per subdomain.
- URL value rules:
  - If `u % 10 == 0`: `https://{subdomain}/admin/{u}`
  - Else if `u % 10 == 1`: `https://{subdomain}/api/v1/{u}`
  - Else if `u % 25 == 0`: `https://{subdomain}/.env`
  - Else: `https://{subdomain}/path/{u}`

Every URL is confirmed.
A finding is created when the URL contains `/admin`, `/api/`, or `.env`.
For the required benchmark arguments, this produces exactly:

- 5,000 domains
- 40,000 subdomains
- 1,000,000 URLs
- 1,000,000 confirmed URLs
- 120,000 findings

## Checksum requirement
Compute `checksum` as lowercase SHA-256 hex over the UTF-8 bytes of this deterministic stream, in generation order:

For every generated URL, append:

```text
URL|{url}\n
```

For every generated finding, immediately after its URL line append:

```text
FINDING|{url}|HighValueUrl\n
```

The evaluator will recompute the checksum independently.

## Engineering requirements
- Use .NET 10 if available; otherwise use the latest installed stable .NET SDK.
- Use immutable event records for at least:
  - `DomainAssetCreated`
  - `SubdomainAssetCreated`
  - `UrlAssetCreated`
  - `UrlAssetConfirmed`
  - `FindingCreated`
- Simulate event handling without RabbitMQ because this is a pure runtime benchmark, but design the code so an event bus could replace the in-process dispatcher.
- Avoid unnecessary allocations.
- Avoid storing all URLs or all events if not needed.
- Include tests under `tests/SpeedBench.Tests`.
- `dotnet test -c Release` must pass.
- Do not ask questions. Make reasonable assumptions.

## Scoring
1. Build succeeds.
2. Tests pass.
3. `score.json` matches all required counts and checksum.
4. Runtime is measured externally. Lower is better.
