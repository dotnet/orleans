# Concurrent hash-range collection benchmarks

These benchmarks compare `ConcurrentDictionary<GrainId, int>` with
`ConcurrentHashRangeDictionary<GrainId, int, GrainIdUniformHashComparer>`.
Both use identical seeded grain identities and values. The native map's concrete
struct comparer supplies consistent hashes and key equality.

## Concurrent throughput

`HashRangeWorkloadBenchmarks` uses 1, 8, and 32 persistent worker threads over
262,144 entries. Both maps grow from their default initial sizes, allowing their
normal lock-array growth. Each invocation completes 1,048,576 logical work items
across all workers, amortizing two barrier rendezvous while keeping total work
constant across concurrency levels. Reported operations per second represent
aggregate work-item throughput.

| Profile | Work |
| --- | --- |
| ReadHeavy | 31 lookups per idempotent refresh write |
| Mixed | Three lookups per refresh write over the complete population |
| HotMixed | The same mix shared across 64 hot keys |
| Recovery | Mixed point traffic plus 32 selective range scans per invocation, distributed evenly across workers |
| Churn | Three lookups per conditional remove-and-reinsert lifecycle update; workers own disjoint keys during each batch |

Point traffic rotates over the population. Setup compares worker checksums with
an independent model and verifies the complete populations. Worker exceptions
propagate to the controller. Threads start before measurement and are joined
during cleanup. Threading diagnostics include lock contention. Allocation
measurements for construction live in the point benchmark class, since worker
allocations span multiple threads.

## Supporting comparisons

`HashRangePointBenchmarks` covers hits, misses, prehashed lookup, conditional
update/removal, and construction with and without presizing at 16,384 and 262,144
entries. Construction reports cumulative allocation, including resizing.

`HashRangeScanBenchmarks` separates traversal checksums, result materialization,
and remove/refill lifecycles. Bounded profiles cover empty, tiny, sparse, moderate,
full, wrapped, concentrated-prefix, and colliding hashes. Large profiles contain
262,144 entries; the deliberate all-collision profile contains 1,024 entries.
Each destructive invocation restores the complete starting population.

## Running on .NET 10

Build the benchmark assembly, then invoke its `suite` entry point directly to
keep repository test-runner arguments separate from BenchmarkDotNet arguments.
`BuildExternalAssets=false` scopes collection measurements to managed code when
BenchmarkDotNet rebuilds its harness.

```powershell
$env:BuildExternalAssets = 'false'
dotnet build test\Benchmarks\Benchmarks.csproj -c Release -f net10.0
dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll suite --filter '*HashRangeWorkloadBenchmarks*' --job Dry --buildTimeout 600 --noOverwrite
dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll suite --filter '*HashRangeWorkloadBenchmarks*' --job Short --outliers DontRemove --buildTimeout 600 --noOverwrite
```

`--inProcess` supports rapid comparisons using the already-built assembly; record
that toolchain with the results. Out-of-process runs give each case a separate
process. Inspect reports for successful execution: BenchmarkDotNet can exit
successfully after a harness build failure and produce a report containing `NA`.

## Recorded results and interpretation

Development measurements on September 8, 2026 used .NET 10.0.11, SDK 10.0.400,
BenchmarkDotNet 0.15.6, and its in-process emit toolchain on Windows 11 / Hyper-V
(AMD EPYC 7763, 32 logical processors). Outliers were retained. The initial matrix
covered all three concurrency levels. A separate repeat used two launches,
five warmups, and ten measured iterations per launch at 32 workers. After
improving initial sizing, the final Short job used three warmups and three
measured iterations.

Final 32-worker results, in millions of logical work items per second:

| Profile | ConcurrentDictionary | Native | Time standard deviation, baseline/native |
| --- | ---: | ---: | ---: |
| ReadHeavy | 111.5 | 99.3 | 1.018 / 0.677 ns |
| Mixed | 80.1 | 87.1 | 0.215 / 0.589 ns |
| HotMixed | 116.0 | 123.0 | 0.093 / 0.096 ns |
| Recovery | 19.1 | 84.6 | 1.620 / 0.347 ns |
| Churn | 59.3 | 58.9 | 0.460 / 0.498 ns |

The 32-worker recovery improvement repeated at approximately 4.4-4.7x.
Point-only results varied around the baseline: read-heavy native throughput was
about 2-11% lower across these runs, while mixed results changed direction.
The earlier eight-worker read-heavy comparison showed about 20% lower native
throughput. These tradeoffs matter for point-dominated workloads.

For 262,144 entries, cumulative construction allocation was approximately
54.7 MB native versus 38.4 MB baseline when growing from default capacity.
Initial sizing headroom reduced presized native allocation from 37.6 MB to
20.0 MB. Baseline presized allocation varied from 18.1 MB to 38.3 MB between runs.
These figures include obsolete nodes and tables created during growth.

Range traversal visits intersecting hash-prefix buckets. Concentrated prefixes
and identical hashes can produce long chains and contention; full scans and
construction have their own costs. The default BCL baseline and native map use
different hash/comparer configurations, so these are storage-configuration
comparisons rather than an isolated attribution to bucket arithmetic.

The host is a shared VM. Retain outliers, report dispersion, and repeat
representative high-concurrency cases in separate runs. Keep agent-initiated
builds and tests outside measured intervals. Treat small differences with
overlapping uncertainty as inconclusive. Reproduce deployment-representative
workloads on the target hardware before interpreting these microbenchmarks as
application-level throughput gains.
