---
title: Operate durable messaging
description: Configure capacity and retention, diagnose delivery, replay dead letters, and measure throughput.
ms.date: 10/08/2026
ms.topic: how-to
---

# Operate durable messaging

Use this guide to configure and operate the inbox/outbox commit protocol described
in [Durable messaging](durable-messaging.md). For business replay policy, see
[Idempotency and hierarchical keys](durable-messaging-idempotency.md).

## Configure storage and execution

Configure shared Journaling storage and Durable Jobs storage before enabling
messaging. Persist business state, operation ledgers, and messaging state through the
same grain-scoped journal manager. Keep state names stable and application-owned;
the `__orleans.durable-messaging.*` names belong to the messaging implementation.
The messaging journal uses the `orleans-binary` format so envelope bytes recover
with their declared body and context types.

Use non-reentrant grains. Activation validates execution properties, and grain
pumps run as non-interleaving timer turns. A handler preparing asynchronously can
delay that grain's next handler; other grains' pumps progress independently.
Partition workloads by business ownership, such as tenant/account, tenant/SKU,
or order. A small hot grain set bounds sequential throughput; adding silos provides
capacity when there are independent owners to distribute.

For storage configuration, see [Journaling configuration](journaling/configuration.md),
[Azure Storage providers](journaling/azure-storage.md), and
[Durable Jobs migration](journaling/durable-jobs-migration.md).

## Capacity, retries, and retention

<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions> configures both
inbox and outbox behavior through `AddDurableMessaging`.

| Setting | Default | Operational effect |
| --- | --- | --- |
| `MaxCapacity` | 1,000 | Bounds pending inbox messages. New deliveries receive `Backpressured` at capacity; duplicates still refer to existing work. |
| `DeduplicationWindow` | 7 days | Retains processed transport identities. Budget records by processing rate multiplied by retention. |
| `BackpressureRetryDelay` | 1 second | Base retry delay with bounded exponential backoff, up to 64 times the base. |
| `MaxProcessingAttempts` | 5 | Moves repeated handler-preparation failures to inbox dead letters. |
| `MaxDeliveryAttempts` | 100 | Bounds attempted remote delivery before an outbox dead letter. |
| `MaxOutboxRetryAge` | 1 day | Bounds retry age and must be shorter than the deduplication window. |
| `DeadLetterRetentionPeriod` | 30 days | Bounds automatic dead-letter retention by age. |
| `MaxRetainedDeadLetters` | 1,000 | Bounds each endpoint's retained dead-letter count. |
| `InboxBatchSize` | 32 | Bounds handler work admitted by a pump attempt. |
| `OutboxBatchSize` | 32 | Bounds outgoing work admitted by a pump attempt. |
| `OutboxIdleRetirementGracePeriod` | 100 milliseconds | Retains an empty outbox's acknowledged recovery job across short bursts. Zero selects immediate retirement. |

Size retention across the entire producer fleet. Include outage duration and operator
replay policy in your business-ledger retention. Keep long-term outcomes separately
from the transport's processed-record window.

Use bounded business ingress when the outbox's depth grows. Inbox capacity supplies
receiver backpressure, while sender admission belongs to the application. Establish
per-tenant payload and submission limits; bytes and serialization cost matter as well
as message count.

The outbox's idle grace amortizes recovery-job scheduling across short bursts.
Acknowledged outgoing work and completed remote deliveries request an immediate
local pump turn. Recovered committed work also requests a local turn as soon as
the activation is ready. The grace applies to retirement of an empty owner,
while delivery and retry deadlines keep their own schedules.

An idle durable job requests execution at its idle-start time plus the grace.
Provider polling and activation latency also contribute to recovery timing.
Choose the grace alongside the deployment's recovery budget and job-provider
transaction rate. The supported range is zero through 4,294,967,294 milliseconds.

## Interpret delivery and completion

| Observed outcome | Meaning and response |
| --- | --- |
| `Accepted` | The receiver committed the inbox admission. Await a correlated application result or query its operation ledger for business completion. |
| `Duplicate` | The receiver already has the transport identity pending or processed within retention. The outbox can finish that delivery. |
| `Backpressured` | The sender retains the envelope and retries according to its policy. Examine pending depth, oldest age, and handler preparation latency. |
| `RouteNotFound` | The receiver's current registrations cannot select a handler. Examine deployment skew and route compatibility; delivery failures follow the configured retry/dead-letter policy. |
| `DeadLettered` | The remote outcome completes the outbox delivery as a terminal receiver outcome. Inspect the receiver's diagnostics and business status. |

An ordinary sender's successful journal write establishes durability of its outgoing
intent. Receiver acceptance establishes durability of admission. Handler journal
acknowledgement establishes durability of business processing. Use an explicitly
correlated reply for end-to-end business confirmation.

## Diagnose failures by boundary

| Symptom | Boundary and remedy |
| --- | --- |
| Repeated handler errors | Validate body type, route, and local preparation dependencies. Business rejections should be recorded outcomes; persistent preparation errors reach dead letters. |
| Successful return without `Complete()` | A handler contract error retires the owner. Ensure every successful branch, including no-effect outcomes, calls `Complete()` and returns synchronously. |
| Handler error after `Complete()` | The runtime persists the staged completion and output, cleans up, then reports the error. Diagnose the original error using the committed business state. |
| `JournaledStatePreCommitException` | A persistence prerequisite failed before storage. Complete changes remain staged. Choose explicit persistence retry or retire the owner and reconcile fresh replay. |
| Storage failure or ambiguous append | The owner is fenced and deactivated. A new activation replays the actual committed journal outcome, including any acknowledged ownership and message cohort. |
| `JournaledStatePostCommitException` | Storage and state acknowledgement succeeded. Reconcile the failed post-persistence action from committed state. |
| Caller cancellation | An admitted write or delivery continues through its actual outcome. Query durable operation status or retry with the same business key. |
| Increasing orphan-reclamation counter | Recovery is cleaning schedule-before-commit remnants. Correlate spikes with ambiguous scheduling, activation failures, and storage incidents. |

The trusted handler contract is important during diagnosis: all asynchronous or
fallible preparation precedes shared mutation, and the first shared mutation through
method return forms one synchronous final block. Journaled state already staged by
a nonconforming handler can be captured by a shared writer. Review the handler's
mutation boundary when investigating unexpected partial effects.

## Inspect and replay dead letters

Expose application-authorized diagnostic methods which inspect
<xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.InboxDeadLetters>
and <xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.OutboxDeadLetters>.
Each record includes its envelope, attempt count, failure reason, and terminal time.

Capture the evidence before age/count retention removes it. Preserve original
operation keys and enough immutable request data to reconcile business outcomes.
Authorize replay and diagnostic access separately from ordinary submissions.

For a replay:

1. Repair the preparation dependency, route, or payload compatibility issue.
2. Query participant ledgers and external provider outcomes using the original
   business-operation key.
3. Submit the recovered request through an authorized grain method. Keep that key
   for reconciliation; use a new revision key only for an intentionally new effect.
4. Record replay disposition and stage removal with
   <xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.RemoveInboxDeadLetter*>
   or <xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.RemoveOutboxDeadLetter*>.
   Persist the disposition, removal, and any outgoing replay intent together.

Transport record expiry makes business-ledger retention particularly important for
late replay. An external provider's retention and query protocol also govern whether
its original side effect can safely be retried.

## Observe progress and size the system

The `Microsoft.Orleans` meter exposes these messaging instruments:

| Instrument | Interpretation |
| --- | --- |
| `orleans-durable-messaging-inbox-messages-received` | Receipts grouped by grain type and status, including duplicates and backpressure. |
| `orleans-durable-messaging-inbox-messages-processed` | Handler outcomes grouped by grain type and status. |
| `orleans-durable-messaging-outbox-messages-sent` | Admitted outgoing intents by grain type. |
| `orleans-durable-messaging-outbox-messages-delivered` | Delivery outcomes by grain type and status. |
| `orleans-durable-messaging-inbox-processing-duration` | Handler processing duration, in milliseconds. |
| `orleans-durable-messaging-outbox-delivery-duration` | Remote delivery duration, in milliseconds. |
| `orleans-durable-messaging-inbox-depth` and `orleans-durable-messaging-outbox-depth` | Aggregate pending work across active endpoints. |
| `orleans-durable-messaging-orphaned-jobs-reclaimed` | Terminal orphan cleanup, grouped by grain type and job name. |

Export metrics with the application's telemetry pipeline. Alert on sustained depth
growth, oldest pending age from application diagnostics, repeated failure outcomes,
and storage latency. Combine them with Journaling and Durable Jobs health signals.
Log correlation and operation keys for incident investigation, and keep
high-cardinality tenant/order keys in logs or traces rather than metric dimensions.

Capacity includes pending envelopes, opaque serialized bodies, processed transport
records, business-ledger outcomes, and retained dead letters. Snapshot and storage
costs depend on these retained sets. Measure activation replay time and storage
throughput alongside steady-state handling.

### Budget allocations and owned memory

Track allocation rate and retained memory separately. A processing-rate budget
expressed as bytes per acknowledged message describes how much garbage the
workload produces. Live-memory capacity also includes retained business outcomes,
transport identities, pending deliveries, serialization metadata, and storage buffers.

| Budget | Measurement |
| --- | --- |
| Processing allocations | Managed bytes allocated across the process divided by actually acknowledged messages, after warm-up. |
| Pending work | Inbox/outbox depth, active preparation and delivery counts, and serialized payload bytes. |
| Retained data | Processed identities and operation ledgers over their configured retention, plus append history and snapshots. |
| Supporting memory | Serialization-metadata cardinality, reusable pump state, buffer capacity, and outstanding reader/operation ownership. |
| Persistence and recovery work | Journal appends/snapshots and durable job scheduling, retries, and retirement per completed workflow. |

Measure a sequential chain for latency and a many-destination workload for
pending-work capacity. Include cancellation, shutdown, and snapshot
replacement while work or readers remain active. The ownership protocol keeps
their resources live until the actual operation or read finishes; capacity
planning includes that overlapping lifetime.

Allocation-stack traces attribute costs to their producing paths. Compare warmed
runs with the same payload, chain length, providers, and retention settings.
Use untraced runs for latency comparisons and keep trace collection overhead
separate from the benchmark result.

## Deploy compatible message contracts

Keep route names and serialization identifiers stable. Serializable bodies use
`GenerateSerializer` and stable `Id` members; retain readers for messages written
before a rolling upgrade. Explicitly version business semantics and route names
when an incompatible payload or operation policy changes.

Deploy compatible receiving handlers before new producers emit a route. Retire a
route after pending messages, retained dead letters, and authorized replays have
been handled. Query old workflow ledgers during the transition. Coordinate
idempotency-key encoding changes so equivalent requests keep their identity.

Derive tenant ownership and authorized destinations from trusted ingress. Envelope
correlation and request-context values are application metadata. Validate them against
that trusted ownership before preparing external effects or changing business state.
Limit serialized payload sizes and keep secrets out of bodies, keys, and diagnostic
logs.

## Measure sequential throughput

The repository's `DurableMessaging.Sequential` benchmark measures a single chain
through 2, 4, or 8 interacting grains on one silo. It performs 1,024 sequential
durable deliveries per invocation and awaits actual journal acknowledgement of every
handler step. Normal inbox/outbox pumps and real time drive progress.

Run from the repository root:

```powershell
dotnet run --project test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0 -- DurableMessaging.Sequential --filter "*SequentialMessagingBenchmark*" --job Dry --buildTimeout 600 --noOverwrite
dotnet run --project test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0 --no-build -- DurableMessaging.Sequential --filter "*SequentialMessagingBenchmark*" --buildTimeout 600 --noOverwrite
```

The build timeout accommodates the isolated build of the benchmark dependency
graph. The Dry run validates execution. The measured report normalizes `Mean` to one
acknowledged message: throughput is `1 / Mean` with `Mean` expressed in seconds.
The benchmark recreates and warms its cluster outside each measured iteration to
bound retained history. Its volatile journal and in-memory jobs isolate local
framework overhead. Use the deployment's persistent providers, cross-silo placement,
and independent concurrent chains for production capacity measurements. Record
hardware, runtime, commit, provider, and benchmark preset with every comparison.

Use `--memory` to report managed bytes allocated per acknowledged message across
the process. Compare the same grain count, chain length, and storage thresholds.
Volatile storage defaults to 100 appends or 1 MiB between snapshots; see
[Development storage](journaling/configuration.md#development-storage) for both
limits and their memory/replay tradeoff.
