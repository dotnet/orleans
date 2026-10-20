---
title: Operate messaging
description: Configure capacity and retention, diagnose delivery, replay dead letters, and measure throughput.
ms.date: 10/10/2026
ms.topic: how-to
---

# Operate messaging

Use this guide to configure and operate the inbox and outbox described in
[Messaging](messaging.md). For business replay policy, see
[Idempotency and hierarchical keys](messaging-idempotency.md).

## Configure storage and execution

Configure Journaling storage and Durable Jobs storage before enabling messaging.
Business state, result-query state, and messaging state persist through the same
grain journal. The inbox and outbox register the journal states
`orleans.messaging.inbox` and `orleans.messaging.outbox`; give application states
other names. Messaging uses the grain's configured journal format, JSON or Orleans
binary.

Each activation runs one messaging pump on its scheduler, processing inbox messages
one at a time. A handler that awaits during preparation delays that grain's next
message while other grains progress independently. Partition workloads by business
ownership, such as tenant/account, tenant/SKU, or order. A small hot grain set bounds
sequential throughput; adding silos adds capacity when there are independent owners
to distribute.

For storage configuration, see [Journaling configuration](journaling/configuration.md),
[Azure Storage providers](journaling/azure-storage.md), and
[Durable Jobs migration](journaling/durable-jobs-migration.md).

## Capacity, retries, and retention

<xref:Orleans.Messaging.Configuration.DurableMessagingOptions> configures both inbox and outbox
behavior through `AddMessaging`, and is validated at startup.

| Setting | Default | Operational effect |
| --- | --- | --- |
| `MaxCapacity` | 1,000 | Bounds pending inbox messages. Every pending message, including a scheduled message waiting for its `DeliverAt` time, counts from its acceptance until it completes. New messages receive `Backpressured` at capacity; repeats of existing messages receive `Duplicate`. Messages a grain sends to itself are admitted regardless of the limit and count until they complete. |
| `DeduplicationWindow` | 7 days | Retains completed identities until their latest observed `SentAt` is at or below the durable admission floor. The floor advances from observed Durable Jobs time minus this period; acknowledged snapshots and recovery reclaim eligible records. |
| `RetryInitialInterval` | 1 second | Delay before the first inbox processing retry or outbox delivery retry. Each retry doubles the delay, up to 64 times this interval. |
| `InboxRetryTimeout` | 6 hours | Moves a message to the inbox dead letters when its handler attempts keep failing for this long after the first failure. The last attempt runs when the timeout elapses, and the retrying message keeps its place in `MaxCapacity`. `TimeSpan.Zero` dead-letters at the first failure. |
| `OutboxRetryTimeout` | 6 hours | Bounds attempts until `SentAt + OutboxRetryTimeout`, including backpressure. The sending write journals `SentAt`, and every attempt and recovery preserves it. Must be shorter than `DeduplicationWindow`. |
| `DeadLetterRetentionPeriod` | 30 days | Removes dead letters older than this age, using the grain's clock for their timestamps and retention. |
| `MaxRetainedDeadLetters` | 1,000 | Bounds the inbox and the outbox dead letters separately, removing the oldest first. |
| `InboxBatchSize` | 32 | Bounds the inbox messages processed per pump pass. |
| `OutboxBatchSize` | 32 | Bounds the delivery attempts in flight for one grain. Each attempt completes on its own and frees its slot, so a slow receiver holds one slot without delaying messages to other receivers. |

Choose the retry budget for tolerated outages and backpressure, and the admission
retention period for retained-history cost. Each attempt carries the
original journaled <xref:Orleans.Messaging.InboxMessage.SentAt>. The receiver
deduplicates tracked identities first. A later send time extends a tracked
pending or completed record, and the extension is durable before the answer.
An untracked send time at or below the floor returns `Expired`.
The floor only advances and survives recovery. Clock jumps can expire new work
early or retain more history, while the send time and durable floor preserve
admission decisions for the original send.

Dead-letter retention applies when an activation starts and whenever a new dead
letter is added. A snapshot persists its admission floor and compacted history
atomically. In-memory completion records remain until the floor is acknowledged.
Budget history by processing rate and the configured retention period, including
the additional history retained by clock motion and later resubmissions.

Inbox capacity supplies receiver backpressure; bound sender admission in the
application when outbox depth grows. Set per-tenant payload and submission limits:
bytes and serialization cost matter as well as message count. Sending and delivery
reject message IDs longer than 1,024 UTF-8 bytes and subjects longer than 256 UTF-8
bytes before any durable change.

One durable job per grain is crash insurance for the live pump, which processes due
work as soon as it is durable. Work which is already due resumes within about a second
of recovery, and work which becomes due later wakes the grain at its retry or
`DeliverAt` time. A write schedules a job only when no tracked job is due within that
second, so a busy grain performs no Durable Jobs operation per write. The pump job
retries indefinitely with capped backoff while activation fails, and every activation
re-arms the wake-up from recovered state. Recovery timing combines the retry
schedule, Durable Jobs polling, and activation latency.

Scheduled messages stay in the receiver's journal and snapshots until they are due,
and occupy `MaxCapacity` meanwhile, so budget their capacity, storage, and recovery
cost alongside other pending messages. The inbox depth counter includes them.

## Interpret delivery and completion

| Delivery status | Meaning and response |
| --- | --- |
| `Accepted` | The receiver journaled the message. Await the durable reply or query business state by the same command ID. |
| `Duplicate` | The receiver has this message ID pending or completed within retention. The outbox removes the message; business results come from a reply or query. A repeat of a message which an inbox sink rejected receives the original `DeadLettered` result instead. |
| `Backpressured` | The receiver is at capacity. The outbox retries. Examine receiver depth, oldest message age, and handler latency. |
| `HandlerNotFound` | The receiver has no registered handlers. The outbox logs an error and retries. Check handler registration during activation and deployment skew. |
| `DeadLettered` | The receiver durably rejected the message, with a reason and an optional application-defined code. The outbox removes the message, and a source-provided message retires as `Rejected` with that reason and code. |
| `Expired` | The send time is at or below the receiver's durable admission floor, for example after delivery was delayed past `SentAt + DeduplicationWindow` or the receiver's clock jumped forward. Earlier acceptance is unknown. The outbox keeps a dead letter and retires a source-provided message as `DeadLettered`. Reconcile through the application before authorizing another send. |
| Any other status | The sender can't interpret the status, for example one from a newer receiver, so the outcome is unknown. The outbox retries until `OutboxRetryTimeout`. |

`Accepted`, `Duplicate`, `DeadLettered`, and `Expired` are the terminal statuses.

A sender's journal write establishes durability of its outgoing message. Receiver
acceptance establishes durability of the inbox entry. The handler's journal write
establishes durability of business processing. Use a correlated reply for
end-to-end business confirmation.

## Diagnose failures by boundary

| Symptom | Boundary and remedy |
| --- | --- |
| Repeated handler errors | Validate subject registration, body compatibility, command ID, response destination, and preparation dependencies. Handler exceptions retry until `InboxRetryTimeout` and hold a capacity slot meanwhile, so reserve them for transient faults. Dead-letter permanently invalid input with `Fail(reason)`, or send a typed rejection reply and call `Complete()`. |
| Messages dead-lettered with "No handler is registered" | A handler is registered, but none accepts the subject. The message retried until `InboxRetryTimeout`. Register a handler for every subject the grain receives. |
| Conflicting pending command reuse | The sender or receiver throws `InvalidOperationException`. Keep the original command ID, subject, destination, and payload; use a distinct ID for a new command. |
| Grain deactivates after a handler fails | The handler staged journaled changes, then threw or returned without calling `Complete()` or `Fail(reason)`. Recovery from the journal discards those changes and the message runs again. Move fallible work before the first change. |
| Grain deactivates after a handler returns | The handler called `Complete()` and `Fail(reason)` with conflicting outcomes. Ensure every branch stages exactly one outcome. |
| Handler error logged after `Complete()` | The staged completion and outgoing messages persist. Diagnose the error using committed state. |
| Grain deactivates after a storage or staging failure | Messaging vetoes every later journal write of the activation, and the grain deactivates. The next activation recovers the committed journal and resumes pending work. |
| Outbox dead letter whose reason states that the retry timeout elapsed | No terminal answer was recorded within the original send's `OutboxRetryTimeout`. Check receiver availability and handler registration, then reconcile the possibly accepted command before authorizing replay. |
| Sender dead-letters with `Expired` and an unknown receiver outcome | Delivery was delayed past `SentAt + DeduplicationWindow`, or a forward receiver clock jump advanced its durable admission floor. Reconcile business state through the application, check sender/receiver clocks, and size `DeduplicationWindow` for tolerated delivery delays and clock variation. Keep `OutboxRetryTimeout` shorter than that window. |
| Caller cancellation | An accepted write or delivery continues through its actual outcome. Query durable business status or retry with the same command ID. |

Handlers follow a two-phase pattern: all asynchronous and fallible preparation uses
local values, and the final phase sends messages, applies business changes, and
calls `Complete()` in one synchronous block. Any journal write on the activation
captures the grain's staged changes, so review the handler's mutation boundary when
investigating unexpected partial effects.

## Inspect and replay dead letters

Expose application-authorized diagnostic methods that read
<xref:Orleans.Messaging.IMessagingDiagnostics.InboxDeadLetters>
and <xref:Orleans.Messaging.IMessagingDiagnostics.OutboxDeadLetters>.
Each record includes its message, attempt count, failure reason, and dead-letter time.
An inbox dead letter which an inbox message sink rejected also carries the sink's
application-defined `Code`. An outbox dead letter for a source-provided message whose
<xref:Orleans.Messaging.IOutboxMessageSource> failed to provide it carries only the
message ID, with an empty payload and a default receiver; its reason records the
source's failure.

<xref:Orleans.Messaging.IInboxHandlerContext.Fail*> creates an inbox dead letter
for a diagnosed permanent failure and records the message ID as processed, so the
processed-message counter reports `dead_lettered` and repeats return `Duplicate`
while the record is retained. Normal business rejections are typed application
replies committed with `Complete()`; the
[inventory recipe](messaging-recipes.md#reserve-inventory-once-per-order-line)
models accepted and rejected reservation outcomes explicitly.

For remote inspection, expose a grain method that copies the record's identity,
subject, reason, timestamps, and relevant payload fields into an application DTO.
Dead letters hold the original payload and header arrays; copy the bytes when the
application needs a mutable buffer.

Retained failure records provide bounded operational evidence: identify
incompatible subjects or payloads, reconcile external effects, repair the producer or
handler, and authorize a corrected submission. Capture the evidence before retention
removes it, and authorize replay and diagnostic access separately from ordinary
submissions.

### Guarantee application follow-up

Use an <xref:Orleans.Messaging.IOutboxMessageSource> when terminal delivery
outcomes require application action. In
<xref:Orleans.Messaging.IOutboxMessageSource.OnRetired*>, synchronously stage the
terminal outcome and a self-addressed follow-up message in application journaled
state, together with payload release. The outbox removal, outcome, and follow-up
acceptance commit atomically. Recovery before commit retries the pending entry and
callback; recovery after commit resumes the follow-up through the inbox's durable
wake-up. The follow-up handler can query participants, reconcile unknown effects,
and commit the application's next decision.

`DeadLettered` retirements cover retry exhaustion, admission fencing, and source
failure. Earlier acceptance remains unknown; `Rejected` identifies a durable
receiver rejection. Keep source ownership registered through recovery. A callback
exception fails the activation and preserves the last committed pending entry.
Application outcome and work retention follow the application's business policy;
the diagnostic dead-letter collections follow their configured capacity and age.

The receiver records every inbox dead letter's message ID as processed, so the
original send receives `Duplicate` while the completion is retained and `Expired`
after its send time is excluded by the floor and its record compacted. An outbox dead letter can
represent earlier acceptance with a lost answer. Replay follows reconciliation.

To replay an outbox dead letter:

1. Repair the receiver availability, handler registration, or payload compatibility
   issue, and query participant business state by the original command ID.
2. Reconcile the original outcome. A replay of the original logical send preserves
   its ID, destination, subject, payload, and `SentAt`. Authorize a new revision ID
   only when the business protocol permits another effect. Record its relationship
   to the original send. A fresh send time extends a tracked record, and can
   create a new admission opportunity after that identity's retention ends.
3. Record the replay disposition and remove the record with
   <xref:Orleans.Messaging.IMessagingDiagnostics.RemoveOutboxDeadLetter*>. The
   disposition, removal, and replayed message persist in the next journal write.

To replay an inbox dead letter:

1. Repair the preparation dependency, handler, or payload compatibility issue.
2. Query participant business state and external provider outcomes keyed by the
   original message ID, and reconcile any effects that the failed attempts applied.
3. Resubmit the command under a new revision ID, such as a child key of the
   original, for example `{original}/replay/1`, through an authorized grain method.
   Record the relationship between the revision and the original ID.
4. Record the replay disposition and remove the record with
   <xref:Orleans.Messaging.IMessagingDiagnostics.RemoveInboxDeadLetter*>. The
   disposition, removal, and outgoing revision persist in the next journal write.

An external provider's retention and query protocol govern whether the original side
effect can be retried safely; reconcile it with the original key before authorizing
a revision.

## Observe progress and size the system

The `Microsoft.Orleans` meter exposes these messaging instruments. Every instrument
carries a `grain_type` tag; counters with outcomes also carry a `status` tag.

| Instrument | Type | Interpretation |
| --- | --- | --- |
| `orleans-messaging-inbox-messages-received` | Counter | Delivery outcomes at the receiver: `accepted`, `duplicate`, `backpressured`, `handler_not_found`, or `expired` (the send time is too old). Messages a grain sends to itself record `accepted` or `duplicate` when sent. Messages an inbox sink takes record `accepted`, or `dead_lettered` when the sink dead-letters them. |
| `orleans-messaging-inbox-messages-processed` | Counter | Handler outcomes: `success`, `retry`, or `dead_lettered`. A sink acceptance records `success`; a sink dead-letter records `dead_lettered`. |
| `orleans-messaging-outbox-messages-sent` | Counter | Messages staged with `Send`, including messages a grain sends to itself, which bypass the outbox. |
| `orleans-messaging-outbox-messages-delivered` | Counter | Delivery attempt outcomes at the sender: `accepted`, `duplicate`, `deadlettered` (a durable receiver rejection), `expired` (the send time is too old), `backpressured`, `handler_not_found`, or `error` (the call failed without an answer). Messages which retire without an attempt record `withdrawn` (a source withdrew the message), `expired` (the retry timeout elapsed), `source_error` (the owning source failed to provide the message), or `unprovided` (no registered source provides a recovered message). A status the sender can't interpret records its lowercase name. |
| `orleans-messaging-inbox-processing-duration` | Histogram, ms | Handler processing duration. |
| `orleans-messaging-outbox-delivery-duration` | Histogram, ms | Delivery duration. |
| `orleans-messaging-inbox-depth` | UpDownCounter | Pending inbox messages across active grains. |
| `orleans-messaging-outbox-depth` | UpDownCounter | Pending outbox messages across active grains. |

The depth instruments report changes as messages are added and removed, and release
an activation's contribution when its messaging stops. Their sum is the current
pending work across active grains.

The `Orleans.Messaging` diagnostic listener, `MessagingEvents`, reports transitions
synchronously on the activation which makes them, with its `IGrainContext`:

| Event | Raised |
| --- | --- |
| `InboxMessageReceiving` | A new delivery passed deduplication and the admission fence, before the inbox stages message effects. An observer's exception fails the attempt like a sink exception, so the sender retries it. |
| `OutboxRetryScheduled` | A failed delivery attempt was rescheduled, with its attempt count, error, and next attempt time. |
| `OutboxMessageRetired` | A message retired as delivered, rejected, or dead-lettered, with its retirement. |

Tests subscribe to `MessagingEvents.AllEvents`, or use `DiagnosticEventCollector`, to
wait for a transition instead of polling, and inject delivery faults from an
`InboxMessageReceiving` observer.

The outbox logs its delivery passes, delivered messages, and backpressure at `Debug`;
expired and unprovided messages and statuses it can't interpret at `Warning`; and
`HandlerNotFound` answers, delivery errors, and source failures at `Error`.

Export metrics with the application's telemetry pipeline. Alert on sustained depth
growth, oldest pending age from application diagnostics, repeated failure outcomes,
and storage latency. Combine them with Journaling and Durable Jobs health signals.
Log canonical command IDs and subjects for incident investigation, and keep
high-cardinality tenant and order keys in logs or traces.

Capacity includes pending messages, serialized bodies, completion records,
business-query outcomes, and retained dead letters. Snapshot and storage costs
depend on these retained sets. Measure activation replay time and storage throughput
alongside steady-state handling.

<a id="budget-allocations-and-owned-memory"></a>

### Budget allocations and managed memory

Each message holds a payload array and optional named header arrays, so message size
and rate contribute directly to the managed allocation rate. Budget pending encoded
bytes, array overhead, and GC activity alongside messaging records and application
results. Apply payload-size and submission limits at ingress according to the
journal capacity and latency budget.

Outbox state, inbox state, handler contexts, and delivery operations reference the
payload arrays while they use them. Removing a message makes its arrays eligible for
collection after other references end. See
[Payload and header arrays](messaging.md#use-gc-owned-payload-arrays).

| Budget | Measurement |
| --- | --- |
| Processing allocations | Managed bytes allocated across the process divided by acknowledged messages, after warm-up. |
| Pending work | Inbox and outbox depth, active delivery count, and serialized payload bytes. |
| Retained data | Completed message IDs and query/audit outcomes over their configured retention, plus journal history and snapshots. |
| Supporting memory | Serialization-metadata cardinality, serialization-buffer capacity, and in-flight operation references. |
| Persistence and recovery work | Journal appends and snapshots, and Durable Jobs wake-up scheduling. |

Measure a sequential chain for latency and a many-destination workload for
pending-work capacity. Allocation-stack traces attribute costs to their producing
paths. Compare warmed runs with the same payload, chain length, providers, and
retention settings, and use untraced runs for latency comparisons.

## Evolve application payload records

Keep application subjects, command IDs, and serialization identifiers stable. Typed
payload records use `GenerateSerializer` and stable `Id` members; keep readers for
previously encoded records. Version business semantics and subjects explicitly when
an incompatible payload or operation policy changes.

Deploy handlers for a new subject before producers emit it. Retire a subject after
pending messages, retained dead letters, and authorized replays have been handled.
Coordinate canonical-key encoding changes so existing commands keep their identity.

Derive tenant ownership and authorized destinations from trusted ingress. Validate
claimed command IDs, subjects, and body response destinations against that trusted
ownership before preparing external effects or changing business state. Limit
serialized payload sizes and keep secrets out of bodies, keys, and diagnostic logs.

## Measure sequential throughput

The repository's `Messaging.Sequential` benchmark measures a single chain through 2,
4, or 8 interacting grains on one silo. It performs 1,024 sequential durable
deliveries per invocation and awaits the journal acknowledgement of every handler
step. Each grain registers a typed handler in its constructor with a static delegate
that receives the grain as its argument. The handler stages the next hop with typed
`Send`, updates its counters, and completes the message. Each hop uses a
`runs/{invocation}/hops/{hop}` command ID with invariant numeric formatting. A
journal state observes acknowledgements, and cleanup checks exact total and
per-grain business-effect counts.

Run from the repository root:

```powershell
dotnet run --project test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0 -- Messaging.Sequential --filter "*SequentialMessagingBenchmark*" --job Dry --buildTimeout 600 --noOverwrite
dotnet run --project test\Benchmarks\Benchmarks.csproj --configuration Release --framework net10.0 --no-build -- Messaging.Sequential --filter "*SequentialMessagingBenchmark*" --buildTimeout 600 --noOverwrite
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
limits and their memory and replay tradeoff.
