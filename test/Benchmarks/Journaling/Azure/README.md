# Azure journal provider benchmarks

`Journaling.Azure` compares durable append, checkpoint replacement, byte recovery,
and catalog traversal through `IJournalStorage` and `IJournalStorageCatalog`.
`Journaling.Azure.Bdn` runs the same operations under BenchmarkDotNet. The
[Durable Jobs playground](../../../../playground/DurableJobsJournaling/README.md)
exercises grain scheduling and workflow latency with the same backend choices.

## Backends and authentication

| Backend | Storage implementation | Account verification |
| --- | --- | --- |
| `AzuriteBlob` | Append-blob WAL and block-blob checkpoints | Emulator, loopback only |
| `AzuriteTable` | Table journal headers and data generations | Emulator, loopback only |
| `StandardBlob` | Append-blob WAL and block-blob checkpoints | `GetAccountInfoAsync`: standard SKU and compatible account kind |
| `PremiumBlob` | The same Blob provider on a premium account | `GetAccountInfoAsync`: `BlockBlobStorage` and premium SKU |
| `Table` | Azure Table journal storage | Requested backend; account tier is unverified |

Premium **BlockBlobStorage accounts support append blobs**. The account tier is
the comparison axis; both Blob cases execute the same WAL and checkpoint code.
Place the benchmark host and accounts in the same Azure region, use equivalent
redundancy settings, and keep compute, payload, history, concurrency, and
catalog distribution constant between runs. Record account configuration and
service-side metrics alongside each report.

**Real Azure runs incur storage, transaction, and possible network charges.**
They require `--allow-azure true`. Use existing test accounts and an Entra
identity with Storage Blob Data Contributor or Storage Table Data Contributor,
including permission to create and delete the isolated container/table.
`DefaultAzureCredential` supports local Azure CLI sign-in and managed identity.
Configure service endpoints through environment variables:

```powershell
# Sign in using your organization's normal Azure CLI procedure.
az login
$env:JOURNAL_BENCHMARK_BLOB_ENDPOINT = 'https://yourstandardaccount.blob.core.windows.net'
$env:JOURNAL_BENCHMARK_TABLE_ENDPOINT = 'https://yourtableaccount.table.core.windows.net'
```

Azure endpoints use HTTPS with Entra authentication. The runner accepts endpoint
settings from the environment and exports only backend, verified kind/SKU, and
generated resource identity. Exception reporting preserves phase, exception
type, HTTP status, operation index, and benchmark validation code. Credential
values, connection strings, URI queries, and SDK exception messages stay out of
reports. Account-info permission or tier mismatches fail before resource creation.

## Build and emulator smoke

Run commands from the repository root with the SDK selected by `global.json`.

```powershell
dotnet build test\Benchmarks\Benchmarks.csproj -c Release -f net10.0

# In a separate terminal, start an installed Azurite on its default loopback ports.
azurite --skipApiVersionCheck --location .\.azurite-journal-bench

# In the benchmark terminal:
$run = Join-Path $env:TEMP ('journal-bench-' + [guid]::NewGuid().ToString('N'))
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 --no-build -- `
  Journaling.Azure --backend AzuriteBlob --workload DurableAppend --output $run
```

`UseDevelopmentStorage=true` supplies the default emulator connection.
For a separately configured loopback emulator, set
`JOURNAL_BENCHMARK_AZURITE_CONNECTION_STRING` in the environment. All resolved
emulator service endpoints must be loopback addresses.

Exercise the two implementations and five operations with metadata enabled:

```powershell
foreach ($backend in 'AzuriteBlob', 'AzuriteTable') {
  foreach ($workload in 'DurableAppend', 'CheckpointReplace', 'RecoveryReplay', 'CatalogBounded', 'CatalogUnbounded') {
    $run = Join-Path $env:TEMP ("$backend-$workload-" + [guid]::NewGuid().ToString('N'))
    dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 --no-build -- `
      Journaling.Azure --backend $backend --workload $workload --operations 3 --concurrency 2 `
      --due-journals 3 --future-journals 8 --metadata true --output $run
    if ($LASTEXITCODE -ne 0) { throw "Benchmark failed; inspect $run.json and console output." }
  }
}
```

Repeat with `--metadata false` to compare projection and stored-metadata overhead.
Emulator results establish correctness and a local baseline. Measure Azure
account performance on the intended Azure compute and storage environment.

## Fixed-work comparisons and scale

Each invocation selects one backend and workload. For example:

```powershell
$run = Join-Path $env:TEMP ('standard-append-' + [guid]::NewGuid().ToString('N'))
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 --no-build -- `
  Journaling.Azure --backend StandardBlob --allow-azure true --workload DurableAppend `
  --operations 100 --concurrency 8 --payload-bytes 1024 --batch-size 8 `
  --history-batches 16 --checkpoint-bytes 65536 --metadata true --output $run
```

For the premium comparison, point `JOURNAL_BENCHMARK_BLOB_ENDPOINT` at the premium
BlockBlobStorage account and select `PremiumBlob`. For Table, set
`JOURNAL_BENCHMARK_TABLE_ENDPOINT` and select `Table`. Sweep concurrency, batch
size, and history in separate runs, retaining the full configuration with each
result. Increasing operations and concurrency supplies a bounded, closed-loop
saturation workload: each worker starts its next operation after the previous
one finishes.

| Option | Default | Meaning / hard limit |
| --- | --- | --- |
| `--operations` | 8 | Total measured operations, 1-10,000 |
| `--concurrency` | 2 | Fixed workers, at most operations and 256 |
| `--payload-bytes` | 256 | Bytes per fixed-length payload record |
| `--batch-size` | 4 | Records per append; product with payload bytes at most 2 MiB |
| `--history-batches` | 4 | Append batches following each seeded checkpoint, 0-10,000 |
| `--checkpoint-bytes` | 4096 | Complete replacement payload, 1 byte-32 MiB |
| `--due-journals` | 16 | Catalog due range, 0-100,000 identities |
| `--future-journals` | 64 | Catalog future tail, 0-100,000 identities |
| `--metadata` | false | Store two caller metadata properties and project catalog metadata |
| `--seed` | 42 | Deterministic payload seed |
| `--max-work` | 100,000 | Estimated logical/setup/traversal work cap, at most 10,000,000 |
| `--max-bytes` | 268,435,456 | Estimated payload/metadata processing cap, at most 8 GiB |
| `--setup-timeout-seconds` | 120 | Setup + seed + warmup deadline; also the separate verification deadline |
| `--timeout-seconds` | 60 | Whole measurement deadline |
| `--cleanup-timeout-seconds` | 60 | Independent cleanup deadline |
| `--output` | azure-journal-results | Prefix for new JSON and CSV files; parent directory must exist |

Work estimates include warmup and verification. They bound requested logical
work and payload processing; Azure SDK retries and storage wire overhead add
service-side work. The clients use at most two SDK retries and a 10-second
network timeout within the enclosing phase deadline.

## Operation contracts

Setup creates deterministic buffers, a new resource, and the baseline before
measurement. Each mutation gets an independent journal and one writer. All
journals begin with one checkpoint and the requested append history. One
additional journal supplies warmup. This gives every measured append or
replacement the same initial state and bounds WAL growth.

| Workload | One measured operation | Verification |
| --- | --- | --- |
| `DurableAppend` | Await one atomic `AppendAsync` batch commit | Fresh-handle read after measurement checks checkpoint + history + appended batch |
| `CheckpointReplace` | Await one `ReplaceAsync`, including default obsolete-generation cleanup | Fresh-handle read checks only the replacement |
| `RecoveryReplay` | Create a fresh storage handle and consume the complete checkpoint/WAL byte stream | Every chunk is consumed; ordered bytes, byte count, SHA-256, format and `IsCompleted` are checked inside the operation |
| `CatalogBounded` | Fully enumerate the prefix and inclusive due-id lower/upper bounds | Exact due membership, count, uniqueness and requested metadata |
| `CatalogUnbounded` | Fully enumerate the same prefix/lower bound with an open upper bound | Exact due + future membership, count, uniqueness and requested metadata |

The storage format is registered as `benchmark-bytes-v1` with
`application/octet-stream`. Its input consists of deterministic fixed-length
byte records. This measures provider I/O plus validation; the existing
`Journaling` dispatch supplies the JSON/binary durable-state codec benchmarks.
Recovery read callbacks guarantee a consistent format; complete caller metadata
and ETags are checked using the storage metadata API outside measurement.
Catalog metadata is checked in the listing operation.

Catalog setup also creates an expired identity below the lower bound and an
unrelated identity outside the prefix. Raising the future tail above 5,000
Blob entries or 1,000 Table entries exercises service paging. Full enumeration
and membership checks ensure the benchmark measures retrieval and traversal.
Set `--due-journals 0` for an empty due range with a future tail, or set both
catalog counts to zero for an empty selected namespace.

## Reports and units

Schema version 1 JSON contains:

| Field | Meaning |
| --- | --- |
| `Configuration` | Backend, workload, seed, sizes, work caps, requested concurrency/count, deadlines, and opt-in |
| `Resource`, `Ownership`, `Cleanup` | Generated resource identity and lifecycle outcome |
| `AccountVerification`, `AccountKind`, `AccountSku` | Emulator, data-plane-verified Blob tier, or requested-unverified Table |
| `Completed`, `Failed`, `Cancelled`, `NotStarted` | Mutually exclusive outcomes summing to the requested operation count |
| `Operations` | Index, outcome, per-operation `Stopwatch` latency in milliseconds, successful payload bytes and items |
| `SuccessfulLatency`, `FailedLatency`, `CancelledLatency` | Count, arithmetic mean, nearest-rank p50/p95/p99, separately by outcome |
| `ElapsedSeconds`, `CompletedOperationsPerSecond` | Measurement window and completed count divided by that window |
| `CompletedPayloadBytes`, `PayloadBytesPerSecond` | Successfully committed or replayed caller bytes and bytes/window-second; catalog contributes zero |
| `CompletedItems`, `ItemUnit` | Append records, replacements, checkpoint + replayed records, or catalog entries |
| `Failures`, `Phase`, `Success` | Partial-result failure diagnostics and whole-run outcome, including verification/cleanup |
| `ProviderMetrics` | Bounded-tag counters and duration summaries from the provider's exact DI `Microsoft.Orleans` meter |

An append with eight records is **one** timed operation. Its p99 is the p99 of
eight-record batch commits. Recovery and catalog latency include their complete
stream consumption and correctness checks. Setup, warmup, post-write
verification, cleanup, and export are outside latency, throughput, and provider
metric windows. Small sample counts produce coarse percentiles; increase
operations for tail-latency analysis.

CSV has one summary row, invariant-culture numeric values, quoted fields, and
the same configuration, raw operations, failures, and provider metrics in JSON
columns. Use `Import-Csv` to read it and `ConvertFrom-Json` for nested columns.
The JSON includes runtime/OS and explicit source/unit descriptions. Existing
output files are preserved by create-new writes; export failure produces a
nonzero exit and console diagnostics.

Provider counters cover logical outcomes, SDK calls/pages, payload bytes, items,
and explicit provider retries. Histogram summaries use fixed-memory logarithmic
buckets with 10% widths starting at 0.001 ms; `P50UpperBound`, `P95UpperBound`,
and `P99UpperBound` are bucket upper bounds. `Sum` is the counter total or
duration sum, and `Observations` is the number of measurements received.
Correlate SDK invocations/pages with Azure transaction metrics for cost
accounting: SDK-internal retries and upload chunking can produce multiple
transport requests per invocation.

## Bounded BenchmarkDotNet adapter

Default parameters select the two emulator backends, 256-byte payloads, and
five workloads: ten cases. Every iteration sets up a fresh resource and executes
one guarded invocation, then verifies and cleans up with synchronous lifecycle
bridges. Job mutators enforce one warmup and three measured iterations,
including when CLI toolchain/job selection replaces the default monitoring job.
The `Dry` smoke preset also retains the three-iteration cap. BDN's reported
operation is one provider call or complete traversal.
Its mean describes iteration samples; use the fixed-work runner's independently
timed operation distribution for request-tail analysis.

```powershell
# Correctness-only smoke uses the already built assembly in process.
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 --no-build -- `
  Journaling.Azure.Bdn --filter '*AzureJournalBenchmarks*' --job Dry --inProcess `
  --noOverwrite --artifacts "$env:TEMP\journal-bdn-smoke" > "$env:TEMP\journal-bdn-smoke.log" 2>&1
```

Use the normal isolated-process toolchain for comparisons (omit `--inProcess`);
allow sufficient `--buildTimeout` seconds for this project's dependency graph:

```powershell
# Run on the intended benchmark host with an existing standard Azure account.
$env:JOURNAL_BENCHMARK_BLOB_ENDPOINT = 'https://yourstandardaccount.blob.core.windows.net'
$env:JOURNAL_BENCHMARK_ALLOW_AZURE = 'true'
$env:JOURNAL_BENCHMARK_BDN_BACKENDS = 'StandardBlob'
$env:JOURNAL_BENCHMARK_BDN_PAYLOAD_BYTES = '256,4096'
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 --no-build -- `
  Journaling.Azure.Bdn --filter '*AzureJournalBenchmarks*DurableAppend*' --buildTimeout 600 `
  --noOverwrite --artifacts "$env:TEMP\journal-bdn-standard" > "$env:TEMP\journal-bdn-standard.log" 2>&1
```

Select backends using `JOURNAL_BENCHMARK_BDN_BACKENDS` (comma-separated, at most
five) and sizes using `JOURNAL_BENCHMARK_BDN_PAYLOAD_BYTES` (at most three).
`JOURNAL_BENCHMARK_BDN_OPTIONS` accepts space-separated fixed-runner options
for history, checkpoint, catalog, metadata, and deadlines. The adapter fixes
operations/concurrency to one. `JOURNAL_BENCHMARK_ALLOW_AZURE=true` supplies
explicit cloud opt-in for BDN; endpoints remain in the same environment settings.
Use a filter selecting one workload and only the relevant backend/size values
to keep case count and service work intentional.

## Resource ownership and failures

Each run creates a GUID-named `journalbench...` container/table using the service
**create** operation before starting the provider lifecycle. A collision fails
setup and retains the existing resource. Cleanup deletes only a resource whose
creation was acknowledged to this run, after all workers join. Ctrl+C cancels
setup/measurement/verification while cleanup retains its own finite deadline.
An operation failure stops further scheduling, joins active workers, and exports
partial results with a nonzero exit.

If creation's response is lost, `creation-unconfirmed` and
`manual-check-required` identify the resource to inspect. A final HTTP 409 also
requires inspection: it can mean a pre-existing resource or an SDK retry after
this run's successful create response was lost. Both outcomes fail setup and
preserve the resource until ownership is established. Cleanup errors retain
the resource name and `failed` outcome for operator follow-up. Abrupt process
termination can also leave a run resource; use the generated identity to inspect
and remove that individual test resource after confirming ownership.
The resource identity is printed to standard error before creation so it is
available even when the process stops before report export.
