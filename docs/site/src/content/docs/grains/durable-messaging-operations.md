---
title: Operate durable messaging
description: Configure capacity and retention, diagnose delivery, replay dead letters, and measure throughput.
ms.date: 10/09/2026
ms.topic: how-to
---

# Operate durable messaging

Use this guide to configure and operate the inbox/outbox commit protocol described
in [Durable messaging](durable-messaging.md). For business replay policy, see
[Idempotency and hierarchical keys](durable-messaging-idempotency.md).

## Configure storage and execution

Configure shared Journaling storage and Durable Jobs storage before enabling
messaging. Persist business state, result-query state, and messaging state through the
same grain-scoped journal manager. Keep state names stable and application-owned;
the `__orleans.durable-messaging.*` names belong to the messaging implementation.
The messaging journal uses the `orleans-binary` format so envelope bytes recover
exactly as opaque application payloads. Applications bind exact subjects to their
typed serializers or an explicit raw encoding.

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
| `DeduplicationWindow` | 7 days | Retains completed command IDs. Budget records by processing rate multiplied by retention. |
| `BackpressureRetryDelay` | 1 second | Base retry delay with bounded exponential backoff, up to 64 times the base. |
| `MaxProcessingAttempts` | 5 | Moves repeated handler-preparation failures to inbox dead letters. |
| `MaxDeliveryAttempts` | 100 | Bounds attempted remote delivery before an outbox dead letter. |
| `MaxOutboxRetryAge` | 1 day | Bounds retry age and must be shorter than the deduplication window. |
| `DeadLetterRetentionPeriod` | 30 days | Bounds automatic dead-letter retention by age. |
| `MaxRetainedDeadLetters` | 1,000 | Bounds each endpoint's retained dead-letter count. |
| `InboxBatchSize` | 32 | Bounds handler work admitted by a pump attempt. |
| `OutboxBatchSize` | 32 | Bounds outgoing work admitted by a pump attempt. |
| `OutboxIdleRetirementGracePeriod` | 100 milliseconds | Retains an empty outbox's acknowledged recovery job across short bursts. Zero selects immediate retirement. |

Size completion retention across the entire producer fleet. Include reconstructed
submissions, outage recovery, and authorized operator replay in the supported
resubmission horizon. Expiry permits another execution of the same ID. Retain
business result-query and audit state according to its own application lifetime.

Use bounded business ingress when the outbox's depth grows. Inbox capacity supplies
receiver backpressure, while sender admission belongs to the application. Establish
per-tenant payload and submission limits; bytes and serialization cost matter as well
as message count. Send and admission validate command IDs against 1,024 UTF-8 bytes
and 32 segments and subjects against 256 UTF-8 bytes before durable mutation.

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
| `Accepted` | The receiver committed inbox admission. Await the original durable reply or query business state by the same command ID. |
| `Duplicate` | The receiver has the exact command ID pending or completed within retention, across senders and subjects. The outbox can finish that delivery; business results come from a reply or query. |
| `Backpressured` | The sender retains the envelope and retries according to its policy. Examine pending depth, oldest age, and handler preparation latency. |
| `HandlerNotFound` | No inbox handler is registered at the receiver. `DeliveryResult.HandlerNotFound()` reports `No inbox handler is registered.`. Check activation registration and deployment skew; delivery failures follow retry/dead-letter policy. |
| `DeadLettered` | The remote outcome completes the outbox delivery as a terminal receiver outcome. Inspect the receiver's diagnostics and business status. |

An ordinary sender's successful journal write establishes durability of its outgoing
intent. Receiver acceptance establishes durability of admission. Handler journal
acknowledgement establishes durability of business processing. Use an explicitly
correlated reply for end-to-end business confirmation.

## Diagnose failures by boundary

| Symptom | Boundary and remedy |
| --- | --- |
| Repeated handler errors | Validate subject registration, body compatibility, command ID, response destination, and preparation dependencies. Business rejections are completed outcomes; persistent preparation errors reach dead letters. |
| Conflicting pending command reuse | Preserve the original command ID, subject, destination, and encoded body. Authorize a distinct ID for a genuinely new command. |
| Successful return without `Complete()` | A handler contract error retires the owner. Ensure every successful branch, including no-effect outcomes, calls `Complete()` and returns synchronously. |
| Handler error after `Complete()` | The runtime persists the staged completion and output, cleans up, then reports the error. Diagnose the original error using the committed business state. |
| `JournaledStatePreCommitException` | A persistence prerequisite failed before storage. Complete changes remain staged. Choose explicit persistence retry or retire the owner and reconcile fresh replay. |
| Storage failure or ambiguous append | The owner is fenced and deactivated. A new activation replays the actual committed journal outcome, including any acknowledged ownership and message cohort. |
| `JournaledStatePostCommitException` | Storage and state acknowledgement succeeded. Reconcile the failed post-persistence action from committed state. |
| Caller cancellation | An admitted write or delivery continues through its actual outcome. Query durable business status or retry with the same command ID. |
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
command IDs, subjects, and enough immutable request data to reconcile business outcomes.
Authorize replay and diagnostic access separately from ordinary submissions.

For a replay:

1. Repair the preparation dependency, handler registration, or application-payload compatibility issue.
2. Query participant business state and external provider outcomes using the original
   command ID.
3. Submit the recovered request through an authorized grain method. Keep that key
   for reconciliation; use a new revision key only for an intentionally new effect.
4. Record replay disposition and stage removal with
   <xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.RemoveInboxDeadLetter*>
   or <xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.RemoveOutboxDeadLetter*>.
   Persist the disposition, removal, and any outgoing replay intent together.

Replay within completion retention recognizes the original command. Replay after
expiry permits execution again, so reconcile the original business outcome before
authorizing that replay. An external provider's retention and query protocol also
govern whether its original side effect can safely be retried.

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
Log canonical command IDs and subjects for incident investigation, and keep
high-cardinality tenant/order keys in logs or traces rather than metric dimensions.

Capacity includes pending envelopes, opaque serialized bodies, completion
records, business-query outcomes, and retained dead letters. Snapshot and storage
costs depend on these retained sets. Measure activation replay time and storage
throughput alongside steady-state handling.

### Budget allocations and owned memory

Payloads are owned Arc slices. Typed encoding rents reusable Arc encoders from a
private shared pool. These encoders can
pack small messages into disjoint regions of shared pages using `ConsumeSlice`.
Measure retained pages and their occupancy alongside logical payload length: several
live messages can share a page, and a retained slice can keep that page alive after
other slices are released. Page retention depends on message sizes, encoder reuse,
concurrency, and the overlap between durable state, readers, and delivery operations.

Outbox staging independently retains the caller's payload pin. Generated RPC request
copying retains another pin, while ordinary persistence serialization is non-consuming.
Serialization and invocation hold active uses of that request owner; terminal
callbacks release it after the final active use finishes. Caller cancellation
ends the wait while actual admission, serialization, and invocation retain their
own pins.
The handler borrows its context envelope through actual method completion. Release
application-local envelopes after staging and decoded packages after use; use explicit
`Retain()` when crossing those lifetimes. Typed send/reply helpers dispose their
temporary envelopes internally; independently owned slices keep their pages alive
after pooled encoder reuse. Application-owned raw encoders have explicit scope disposal.
See [Payload ownership](durable-messaging.md#own-and-borrow-payload-slices) for each
borrow/retain/release boundary.

Track allocation rate and retained memory separately. A processing-rate budget
expressed as bytes per acknowledged message describes how much garbage the
workload produces. Live-memory capacity also includes retained business outcomes,
command completion facts, pending deliveries, serialization metadata, and storage buffers.

| Budget | Measurement |
| --- | --- |
| Processing allocations | Managed bytes allocated across the process divided by actually acknowledged messages, after warm-up. |
| Pending work | Inbox/outbox depth, active preparation and delivery counts, and serialized payload bytes. |
| Retained data | Completed command IDs and query/audit outcomes over their configured retention, plus append history and snapshots. |
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

## Evolve application payload records

Keep application subjects, command IDs, and serialization
identifiers stable. Typed payload records use `GenerateSerializer` and stable `Id`
members; preserve readers for previously encoded application records. Explicitly
version business semantics and subjects when an incompatible
payload or operation policy changes.

Deploy compatible typed bindings and subject routes before new producers emit a
subject. Retire it after pending messages, retained dead letters, and authorized
replays have been handled. Query recorded workflow state during the transition.
Coordinate canonical-key encoding changes so existing commands keep their identity.

Derive tenant ownership and authorized destinations from trusted ingress. Validate
claimed envelope command IDs, subjects, and body response destinations against
that trusted ownership before preparing external effects or changing
business state. Limit serialized payload sizes and keep secrets out of bodies, keys, and
diagnostic logs.

## Measure sequential throughput

The repository's `DurableMessaging.Sequential` benchmark measures a single chain
through 2, 4, or 8 interacting grains on one silo. It performs 1,024 sequential
durable deliveries per invocation and awaits actual journal acknowledgement of every
handler step. A keyed typed binding decodes `SequentialMessage` for a static
handler delegate with the grain as its state argument. Typed outbox `Send` encodes
and stages each next hop before the grain updates its business counters. Each invocation assigns a root once;
every hop uses a fixed-depth `runs/{invocation}/hops/{hop}` command ID with invariant
numeric formatting. Retries preserve that hop's ID. Normal inbox/outbox pumps and
real time drive progress. Typed encoding rents internal pooled encoders and
releases temporary output owners after staging. The payload serializer borrows
bytes during serialization, so journal capture leaves owners intact. The journal hook
observes actual acknowledgements independently of handler
return, and cleanup checks exact total and per-grain business-effect counts.

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
