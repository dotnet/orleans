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
| `MaxCapacity` | 1,000 | Bounds pending inbox messages without a `DeliverAt` time. New messages receive `Backpressured` at capacity; repeats of existing messages receive `Duplicate`. Scheduled messages are always admitted and never count, including after they become due. Messages a grain sends to itself are always admitted; unscheduled ones count until they complete. |
| `DeduplicationWindow` | 7 days | Retains completed message IDs. Budget records by processing rate multiplied by retention. |
| `RetryInitialInterval` | 1 second | Delay before the first inbox processing retry or outbox delivery retry. Each retry doubles the delay, up to 64 times this interval. |
| `InboxRetryTimeout` | 6 hours | Moves a message to the inbox dead letters when its handler attempts keep throwing for this long after the first failure. The last attempt runs when the timeout elapses, and the retrying message keeps its place in `MaxCapacity`. `TimeSpan.Zero` dead-letters at the first failure. |
| `OutboxRetryTimeout` | 6 hours | Moves a message to the outbox dead letters after its delivery attempts fail for this long, such as during an outage or while the receiver answers `HandlerNotFound`. Each `Backpressured` answer restarts the timeout, so backpressured messages retry until the receiver takes them. Must be shorter than `DeduplicationWindow`. |
| `DeliveryResiliencePipeline` | `ResiliencePipeline.Empty` | Polly pipeline which runs each outbox delivery attempt and can add timeouts, in-attempt retries, or a circuit breaker. It makes one receiver call at a time. |
| `DeadLetterRetentionPeriod` | 30 days | Removes dead letters older than this age. |
| `MaxRetainedDeadLetters` | 1,000 | Bounds the inbox and the outbox dead letters separately, removing the oldest first. |
| `InboxBatchSize` | 32 | Bounds the inbox messages processed per pump pass. |
| `OutboxBatchSize` | 32 | Bounds the outbox messages delivered concurrently, withdrawn, or dead-lettered per pump pass. |

Dead-letter retention applies when an activation starts and whenever a new dead
letter is added. Expired completed IDs are removed from storage when the journal
writes a snapshot, and duplicate checks apply the window exactly.

Size completion retention across the entire producer fleet. Include reconstructed
submissions, outage recovery, and authorized operator replay in the supported
resubmission horizon. After a completion record expires, the same ID is processed
again.

Inbox capacity supplies receiver backpressure; bound sender admission in the
application when outbox depth grows. Set per-tenant payload and submission limits:
bytes and serialization cost matter as well as message count. Sending and delivery
reject message IDs longer than 1,024 UTF-8 bytes and subjects longer than 256 UTF-8
bytes before any durable change.

One durable job per grain wakes the activation while either queue holds work and
reschedules itself for the next retry time or scheduled `DeliverAt` time. Each
wake-up processes the journaled messages that are due, so recovery timing combines
the retry schedule, Durable Jobs polling, and activation latency.

Scheduled messages stay in the receiver's journal and snapshots until they are due,
so budget their storage and recovery cost alongside other pending messages. The
inbox depth counter includes them.

## Interpret delivery and completion

| Delivery status | Meaning and response |
| --- | --- |
| `Accepted` | The receiver journaled the message. Await the durable reply or query business state by the same command ID. |
| `Duplicate` | The receiver has this message ID pending or completed within retention. The outbox removes the message; business results come from a reply or query. |
| `Backpressured` | The receiver is at capacity. The outbox retries. Examine receiver depth, oldest message age, and handler latency. |
| `HandlerNotFound` | The receiver has no registered handlers. The outbox retries. Check handler registration during activation and deployment skew. |
| `DeadLettered` | The outbox removes the message as a terminal receiver outcome. |

A sender's journal write establishes durability of its outgoing message. Receiver
acceptance establishes durability of the inbox entry. The handler's journal write
establishes durability of business processing. Use a correlated reply for
end-to-end business confirmation.

## Diagnose failures by boundary

| Symptom | Boundary and remedy |
| --- | --- |
| Repeated handler errors | Validate subject registration, body compatibility, command ID, response destination, and preparation dependencies. Send typed business rejection replies and call `Complete()`; use `Fail(reason)` for diagnosed permanent failures. Handler exceptions follow the retry policy. |
| Messages dead-lettered with "No handler is registered" | A handler is registered, but none accepts the subject. Register a handler for every subject the grain receives. |
| Conflicting pending command reuse | The sender or receiver throws `InvalidOperationException`. Keep the original command ID, subject, destination, and payload; use a distinct ID for a new command. |
| Grain deactivates after a handler returns | The handler returned without calling `Complete()` or `Fail(reason)`, or called both with conflicting outcomes. Ensure every successful branch calls exactly one. |
| Handler error logged after `Complete()` | The staged completion and outgoing messages persist. Diagnose the error using committed state. |
| Grain deactivates after a storage failure | Messaging stops and the grain deactivates. The next activation replays the committed journal and resumes pending work. |
| Caller cancellation | An accepted write or delivery continues through its actual outcome. Query durable business status or retry with the same command ID. |

Handlers follow a two-phase pattern: all asynchronous and fallible preparation uses
local values, and the final phase applies business changes, sends messages, and
calls `Complete()` without awaiting. Any journal write on the activation captures the
grain's staged changes, so review the handler's mutation boundary when investigating
unexpected partial effects.

## Inspect and replay dead letters

Expose application-authorized diagnostic methods that read
<xref:Orleans.Messaging.IMessagingDiagnostics.InboxDeadLetters>
and <xref:Orleans.Messaging.IMessagingDiagnostics.OutboxDeadLetters>.
Each record includes its message, attempt count, failure reason, and dead-letter time.
An outbox dead letter for a source-provided message whose
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

For a replay:

1. Repair the preparation dependency, handler registration, or payload
   compatibility issue.
2. Query participant business state and external provider outcomes using the original
   command ID.
3. Submit the recovered request through an authorized grain method. Keep that key
   for reconciliation; use a new revision key only for an intentionally new effect.
4. Record the replay disposition and remove the record with
   <xref:Orleans.Messaging.IMessagingDiagnostics.RemoveInboxDeadLetter*>
   or <xref:Orleans.Messaging.IMessagingDiagnostics.RemoveOutboxDeadLetter*>.
   The disposition, removal, and any outgoing replay message persist in the next
   journal write.

Replay within completion retention returns `Duplicate` for the original command ID.
Replay after expiry processes the command again, so reconcile the original business
outcome first. An external provider's retention and query protocol also govern
whether its original side effect can be retried safely.

## Observe progress and size the system

The `Microsoft.Orleans` meter exposes these messaging instruments. Every instrument
carries a `grain_type` tag; counters with outcomes also carry a `status` tag.

| Instrument | Type | Interpretation |
| --- | --- | --- |
| `orleans-messaging-inbox-messages-received` | Counter | Delivery outcomes at the receiver: `accepted`, `duplicate`, `backpressured`, or `handler_not_found`. Messages a grain sends to itself record `accepted` or `duplicate` when sent. Messages an inbox sink takes record `accepted`, or `dead_lettered` when the sink dead-letters them. |
| `orleans-messaging-inbox-messages-processed` | Counter | Handler outcomes: `success`, `retry`, or `dead_lettered`. A sink acceptance records `success`; a sink dead-letter records `dead_lettered`. |
| `orleans-messaging-outbox-messages-sent` | Counter | Messages staged with `Send`, including messages a grain sends to itself, which bypass the outbox. |
| `orleans-messaging-outbox-messages-delivered` | Counter | Delivery outcomes at the sender: `accepted`, `duplicate`, `deadlettered`, `backpressured`, `handler_not_found`, `error`, `withdrawn` for a source-provided message which its source withdrew, or `source_error` for a source-provided message which its source failed to provide and the outbox dead-lettered. |
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
| `InboxMessageReceiving` | A delivery attempt reached the inbox and isn't a duplicate, before the inbox stages anything for it. An observer's exception fails the attempt like a sink exception, so the sender retries it. |
| `OutboxRetryScheduled` | A failed delivery attempt was rescheduled, with its attempt count, error, and next attempt time. |
| `OutboxMessageRetired` | A message retired as delivered, rejected, or dead-lettered, with its retirement. |

Tests subscribe to `MessagingEvents.AllEvents`, or use `DiagnosticEventCollector`, to
wait for a transition instead of polling, and inject delivery faults from an
`InboxMessageReceiving` observer.

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
| Persistence and recovery work | Journal appends and snapshots, and durable job scheduling per completed workflow. |

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
