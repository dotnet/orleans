# Sequential durable messaging throughput

`SequentialMessagingBenchmark` circulates one message chain through **2, 4, or 8
non-reentrant grains on one in-process silo**. Each handler applies one journaled
counter increment, stages the next envelope, and calls `Complete()` synchronously.
Journal acknowledgement releases the outgoing intent for the next hop.

Each invocation performs **1,024 delivered and acknowledged handler steps**. A
journal hook observes actual successful acknowledgement for every distinct hop.
The driver awaits all 1,024 acknowledgements; cleanup verifies exact total and
per-grain business-effect counts. The measured path includes envelope construction
and ordinary application serialization, scheduling, inbox acceptance, handler execution, journal writes,
dispatch, receiver-local command deduplication, and the completion observer. It excludes cluster
startup, activation, ring configuration, warm-up, validation queries, and shutdown.

The benchmark uses the production messaging and Durable Jobs implementations with
volatile journal storage and in-memory job storage. This measures the framework's
single-chain throughput with low-latency local storage. Persistent provider latency,
cross-silo networking, and concurrent independent chains are separate comparison
axes.

Acknowledged intents and completed remote deliveries wake local, non-interleaving
pump turns immediately. Durable Jobs supplies recovery, retry deadlines, and
empty-owner retirement. The benchmark uses volatile storage's default snapshot
limits: **100 appends or 1 MiB**, whichever is reached first.

The outbox retains an idle recovery handle for the default 100 ms grace, amortizing
job-provider work over a burst. Local delivery wakes remain immediate. Activation
pumps reuse timer/state infrastructure, one-message delivery uses scalar
accounting, and built-in volatile storage retains independently pinned journal
pages. Record the grace and storage settings when comparing earlier builds.

Iteration setup recreates the cluster and warms one complete chain. Each iteration
contains one measured invocation, keeping journal and deduplication history bounded
and comparable. The benchmark uses real time and normal pumps. BDN controls
iteration repetition; there is no polling or manually driven pump.

<a id="opaque-encoding-and-memory"></a>

## Typed subjects, command identity, and memory

The dispatcher decodes `SequentialMessage` using a keyed
`DurableMessageType<SequentialMessage>` binding for `benchmarks.sequential-hop.v1`.
Its registration stores the grain as state and invokes a static synchronous
delegate. Typed outbox `Send` encodes and stages the next hop before business
counter updates, then disposes its temporary owner internally. Each envelope contains a command ID, subject, sender,
receiver, and read-only Arc payload. Normal inbox/outbox delivery traverses the
production pipeline, and the journal hook observes every actual acknowledgement.

Each invocation assigns a run ID once. Hop IDs are fixed-depth
`runs/{run-id}/hops/{hop-number}` keys; numeric components use invariant formatting.
All 1,024 hops have distinct command IDs, and retries retain the same hop ID. The
next hop constructs its key from the run and hop rather than extending a parent
chain. The warm-up invocation uses its own run ID.

Typed encoding rents `ArcBufferWriter` instances from a private shared pool.
`ConsumeSlice` returns
owned, disjoint slices of freshly encoded messages; small messages can occupy shared
pages. The helper disposes each temporary envelope after staging, and the durable outbox
dictionary retains its own pin. Handler context envelopes are borrowed until the
actual handler method ends. Independently retained slices keep their pages alive
after encoder reuse.
Ordinary payload serialization is non-consuming, so journal capture and repeated
sends preserve the owning pins. Generated RPC request copying separately retains
the request clone for the extension to release on every path.

Budget logical payload bytes, retained pages and their occupancy, and overlapping
state/reader/delivery lifetimes separately. Page retention depends on message sizes,
encoder reuse, and live slices. Journal storage and transient serialization buffers
have independent lifetimes and remain part of the retained-memory budget.

Compare revisions on the same machine with matching workload and retention settings.

## Run

From the repository root:

```powershell
dotnet build test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0
dotnet run --project test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0 --no-build -- DurableMessaging.Sequential --filter "*SequentialMessagingBenchmark*" --job Dry --buildTimeout 600 --noOverwrite
dotnet run --project test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0 --no-build -- DurableMessaging.Sequential --filter "*SequentialMessagingBenchmark*" --buildTimeout 600 --noOverwrite
```

The build timeout covers BDN's isolated rebuild of the benchmark project's complete
dependency graph, including the Dashboard frontend. Measurement begins after build
and setup complete.

Use `--job Short` for development measurements. Read BDN's `*-report-github.md`
and compare the grain-count rows on the same machine/runtime. `OperationsPerInvoke`
normalizes `Mean` to one acknowledged message: **messages/second = 1 / Mean in
seconds**. Multiply `Mean` by 1,024 for chain duration. Dry output validates
execution and is excluded from performance conclusions.

Add `--memory` to measure managed allocations per acknowledged message. Compare
the same grain count, chain length, job preset, and snapshot thresholds. The
retained-state budget includes command completion history, application state,
and the bounded append history between snapshots.

Keep invocation length fixed for comparisons: the warm chain contributes to the
retained state captured by later snapshots.

Record commit, runtime, hardware, storage configuration, and job preset with
measurements. Since setup and cleanup have a fixed cost per iteration, the default
run takes longer than its reported measurement time.
