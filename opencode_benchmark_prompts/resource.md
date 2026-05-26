# Benchmark Task 2 — Resource Usage / Memory Optimization

You are a senior .NET engineer. Create a small, runnable .NET solution in the current directory.

## Optimization goal
Correctness is mandatory. Among correct solutions, the winner is the implementation with the lowest peak memory usage during the benchmark command.

## Required output contract
You must create this exact project path:

- `src/ResourceBench/ResourceBench.csproj`

It must run with:

```bash
dotnet run -c Release --project src/ResourceBench -- --events 1000000 --duplicate-rate 0.35 --seed 123 --out score.json
```

The program must write `score.json` with this exact shape:

```json
{
  "task": "resource",
  "processedEvents": 1000000,
  "uniqueAssets": 650000,
  "duplicateAssets": 350000,
  "confirmedAssets": 650000,
  "findings": 130000,
  "checksum": "<lowercase 64-char sha256 hex>",
  "peakWorkingSetBytesInsideProcess": 0
}
```

`peakWorkingSetBytesInsideProcess` should be populated using `Process.GetCurrentProcess().PeakWorkingSet64` if possible. External benchmarking will independently monitor memory too.

## Deterministic workload
Generate and process a stream of synthetic asset events. Do not perform network I/O. Do not require Postgres, Redis, or RabbitMQ to be running for the benchmark.

For event index `i` from `0` to `events - 1`:

- Let `uniqueCount = events - floor(events * duplicateRate)`.
- Let `assetIndex = i % uniqueCount`.
- Asset value: `https://s{assetIndex % 1000}.target-{assetIndex / 1000}.example.com/{path}` using integer division.
- Path rule:
  - If `assetIndex % 5 == 0`: `admin/{assetIndex}`
  - Else: `path/{assetIndex}`

The first time an asset value appears:

- Count it as a unique asset.
- Count it as confirmed.
- If its path begins with `admin/`, create one finding.

Repeated asset values count as duplicate assets and must not create duplicate findings.

For the required benchmark arguments:

- `uniqueAssets` must be 650,000
- `duplicateAssets` must be 350,000
- `confirmedAssets` must be 650,000
- `findings` must be 130,000

## Checksum requirement
Compute `checksum` as lowercase SHA-256 hex over the UTF-8 bytes of this deterministic stream, in first-seen unique asset order only:

For every first-seen unique asset, append:

```text
ASSET|{assetValue}\n
```

For every finding, immediately after its asset line append:

```text
FINDING|{assetValue}|AdminSurface\n
```

The evaluator will recompute the checksum independently.

## Architecture requirements
This benchmark simulates a resource-conscious distributed worker. Include abstractions or types for:

- `IEventConsumer`
- `IAssetDeduplicator`
- `IAssetStore`
- `IFindingStore`
- `IWorkerCheckpointStore`

They may be in-process/file-backed implementations. The benchmark should not need real infrastructure to run.

## Resource requirements
- Do not hold all events in memory.
- Do not hold full duplicate event payloads in memory.
- Favor streaming, compact structures, pooled buffers, or disk-backed state when useful.
- Include clear comments explaining your memory strategy.
- Include tests under `tests/ResourceBench.Tests`.
- `dotnet test -c Release` must pass.
- Do not ask questions. Make reasonable assumptions.

## Scoring
1. Build succeeds.
2. Tests pass.
3. `score.json` matches all required counts and checksum.
4. Peak RSS / working set is measured externally. Lower is better.
