# Sequential durable messaging throughput

`SequentialMessagingBenchmark` circulates one message chain through **2, 4, or 8
non-reentrant grains on one in-process silo**. Each handler applies one journaled
counter increment, stages the next envelope, and calls `Complete()` synchronously.
The next hop starts only after the current outgoing intent is acknowledged.

Each invocation performs **128 delivered and acknowledged handler steps**. A
journal hook observes actual successful acknowledgement for every distinct hop.
The driver awaits all 128 acknowledgements; cleanup verifies exact total and
per-grain business-effect counts. The measured path includes envelope construction
and serialization, scheduling, inbox acceptance, handler execution, journal writes,
dispatch, transport deduplication, and the completion observer. It excludes cluster
startup, activation, ring configuration, warm-up, validation queries, and shutdown.

The benchmark uses the production messaging and Durable Jobs implementations with
volatile journal storage and in-memory job storage. This measures the framework's
single-chain throughput with low-latency local storage. Persistent provider latency,
cross-silo networking, and concurrent independent chains are separate comparison
axes.

Iteration setup recreates the cluster and warms one complete chain. Each iteration
contains one measured invocation, keeping journal and deduplication history bounded
and comparable. The benchmark uses real time and normal pumps. BDN controls
iteration repetition; there is no polling or manually driven pump.

## Run

From the repository root:

```powershell
dotnet build test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0
dotnet run --project test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0 --no-build -- DurableMessaging.Sequential --filter "*SequentialMessagingBenchmark*" --job Dry --buildTimeout 600 --noOverwrite
dotnet run --project test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0 --no-build -- DurableMessaging.Sequential --filter "*SequentialMessagingBenchmark*" --buildTimeout 600 --noOverwrite
```

The build timeout covers BDN's isolated rebuild of the benchmark project's complete
dependency graph, including the Dashboard frontend. It changes build admission time,
not measured messaging time.

Use `--job Short` for development measurements. Read BDN's `*-report-github.md`
and compare the grain-count rows on the same machine/runtime. `OperationsPerInvoke`
normalizes `Mean` to one acknowledged message: **messages/second = 1 / Mean in
seconds**. Multiply `Mean` by 128 for chain duration. Dry output validates
execution and is excluded from performance conclusions.

Record commit, runtime, hardware, storage configuration, and job preset with
measurements. Since setup and cleanup have a fixed cost per iteration, the default
run takes longer than its reported measurement time.
