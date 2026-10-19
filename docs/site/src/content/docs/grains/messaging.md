---
title: Messaging
description: Understand the inbox and outbox guarantees, execution model, and operating limits.
ms.date: 10/10/2026
ms.topic: conceptual
---

# Messaging

The `Microsoft.Orleans.Messaging` package gives each grain a durable inbox and
outbox built on Orleans Journaling and Durable Jobs. A grain commits received
messages, business changes, and outgoing messages in its own journal, so handler
effects and pending sends survive activation loss.

This page describes the message, commit, and execution model. Continue with:

- [Idempotency and hierarchical operation keys](messaging-idempotency.md)
  for application command identities, retained completion facts, and external effects.
- [Practical recipes](messaging-recipes.md) for inventory reservation,
  payment-provider reconciliation, out-of-order projections, and notification fan-out.
- [Operations and throughput](messaging-operations.md) for configuration,
  diagnostics, dead-letter replay, upgrades, and sequential messaging benchmarks.

## Message and routing model

Each <xref:Orleans.Messaging.Envelope> carries an application-supplied
<xref:Orleans.Messaging.MessageId>, which wraps a
<xref:Orleans.Messaging.HierarchicalKey>, a required `byte[]` payload,
an optional ordinal subject, optional named byte-array headers, and an optional
`DeliverAt` time. Empty payloads are valid.

An <xref:Orleans.Messaging.OutboxMessage> pairs the envelope with its destination
(<xref:Orleans.Runtime.GrainId>). Delivery passes an
<xref:Orleans.Messaging.InboxMessage> carrying the same envelope to that grain's inbox.
The envelope's ID identifies the logical command. Reply destinations, domain
versions, and other business data travel in the application payload.

Sending and delivery validate each message before any durable change:

- The message ID is set and is at most 1,024 UTF-8 bytes in its canonical form.
- The payload is non-null.
- A supplied subject is nonempty and at most 256 UTF-8 bytes.
- Header keys are nonempty, well-formed UTF-16 strings, and header values are non-null.
- An outgoing message has a destination.

Retries and reconstructed submissions reuse the same ID, subject, destination, body,
and `DeliverAt` time.

### Register handlers

Inject <xref:Orleans.Messaging.IInbox> into the grain and add handlers with
<xref:Orleans.Messaging.IInbox.Register*>. An
<xref:Orleans.Messaging.IInboxHandler> has two members:
<xref:Orleans.Messaging.IInboxHandler.CanHandle*> receives the
<xref:Orleans.Messaging.InboxMessage> and selects the messages the handler
accepts, and <xref:Orleans.Messaging.IInboxHandler.HandleAsync*> processes one message.
The inbox dispatches each message to the first registered handler whose `CanHandle`
returns `true`, in registration order. The selected handler stages the message's
terminal outcome from `HandleAsync`.

Typed handlers register through the
<xref:Orleans.Messaging.InboxExtensions.Register*> extension methods. Each overload
takes a <xref:Orleans.Messaging.MessageType`1> binding and a delegate, matches the
binding's subject ordinally, decodes the payload, and passes the decoded body and the
handler context to the delegate:

| Delegate | Use |
| --- | --- |
| `Action<T, IInboxHandlerContext>` | Synchronous handling. |
| `Func<T, IInboxHandlerContext, CancellationToken, ValueTask>` | Asynchronous preparation before the final update. |
| `Action<T, TArg, IInboxHandlerContext>` | Synchronous handling with an explicit argument, for `static` delegates. |
| `Func<T, TArg, IInboxHandlerContext, CancellationToken, ValueTask>` | Asynchronous handling with an explicit argument, for `static` delegates. |

The overloads that take a `TArg` argument let a `static` lambda receive the grain or
other state without a closure allocation. Synchronous typed handlers observe the
processing cancellation token immediately before the delegate runs. `Register`
returns the inbox, so registrations chain.

Handlers can be registered at any time. Register them during grain construction or
in <xref:Orleans.Grain.OnActivateAsync*>, so they're available when the inbox
starts processing recovered messages. Messages already in the inbox wait until a
handler is registered.

A receiver with no registered handlers answers delivery with
<xref:Orleans.Messaging.DeliveryStatus.HandlerNotFound>, and the sender retries the
message. When handlers are registered and none accepts the message, processing
reports a handler failure for that message, which follows the retry and dead-letter
policy.

### Encode ordinary application values

Message bodies are ordinary Orleans-serializable types:

:::code source="../snippets/compiled/Grains/MessagingSnippets.cs" id="messaging_payload" language="csharp":::

A <xref:Orleans.Messaging.MessageType`1> binds a subject to the
<xref:Orleans.Serialization.Serializer`1> for its body type.
<xref:Orleans.Messaging.MessageType`1.Decode*> verifies the envelope's subject
before reading its payload. Define an application's bindings in one class which
constructs them from the <xref:Orleans.Serialization.Serializer> service, register
that class as a singleton, and inject it into grains:

:::code source="../snippets/compiled/Grains/MessagingSnippets.cs" id="messaging_messages" language="csharp":::

<xref:Orleans.Hosting.MessageTypeExtensions.AddMessageType*> registers a single
binding as a keyed singleton for `FromKeyedServices` injection.

Subjects use exact ordinal spelling, for example `inventory.reserve.v1`. Separate
subjects can bind the same CLR type. When a subject uses a base type, define the
supported polymorphism in the serialization contract.

<xref:Orleans.Messaging.OutboxExtensions.Send*> takes a binding, a stable command
ID, a destination, and a body. The destination is a <xref:Orleans.Runtime.GrainId>
or an <xref:Orleans.Runtime.IAddressable> grain reference. The helper encodes the
body and stages the outgoing message. Serialization completes before staging, so a
serialization error leaves the outbox unchanged. A batch overload encodes every
message before staging any, so an encoding failure stages nothing, then stages the
messages in order.

<xref:Orleans.Messaging.OutboxExtensions.SendReply*> takes a binding, the handler
context, a destination, and a result body. It derives the reply ID by appending the
literal `result` segment to the received command ID. The application supplies the
reply destination, typically from a field of the request body such as
`ResponseDestination`.

<xref:Orleans.Messaging.MessageType`1.Create*> builds an outgoing message from a
command ID, a destination, and a body. Use it to deliver a message explicitly
through its <xref:Orleans.Messaging.OutboxMessage.ToInboxMessage*> view.

<a id="own-and-borrow-payload-slices"></a>

<a id="use-gc-owned-payload-arrays"></a>

<a id="use-payload-and-header-slices"></a>

### Use payload and header arrays

An envelope is a readonly value holding its payload array, subject, and optional
header dictionary. Struct assignment shares those objects; RPC copying and
deserialization produce independent copies. Prepare the payload and headers before
sending and keep their contents unchanged afterward: outbox state, delivery
retries, handler contexts, and diagnostics read the same arrays.

For a raw protocol, assign the encoded payload and the headers the protocol uses.
Receivers look up headers by ordinal key and decode them with the agreed encoding.
This example encodes a shipment payload and two application headers with ordinary
serializers:

:::code source="../snippets/compiled/Grains/MessagingSnippets.cs" id="messaging_byte_payload" language="csharp":::

Headers travel with the payload through persistence, retries, recovery, and
dead-letter inspection. A repeated submission of a pending or staged command keeps
the first submission's headers. Place command-defining parameters in the payload
and subject, and use headers for accompanying context such as trace identifiers.

<a id="carry-independently-encoded-items"></a>

### Carry related values in one message

Use a serializable record for a composite message. Fields can contain other
application records, collections, and binary data. This shipment carries a
reservation request and a `byte[]` manifest in one body:

:::code source="../snippets/compiled/Grains/MessagingSnippets.cs" id="messaging_shipment" language="csharp":::

The typed `Send` helper serializes the entire `Shipment`, and a typed handler
receives the decoded record. For serialization outside messaging, use
<xref:Orleans.Serialization.Serializer`1.SerializeToArray*> and
<xref:Orleans.Serialization.Serializer`1.Deserialize*>.

### Schedule processing

Set <xref:Orleans.Messaging.Envelope.DeliverAt> to hold a message in the receiving
inbox until a time on the Durable Jobs clock. The single-message typed
<xref:Orleans.Messaging.OutboxExtensions.Send*> helpers and
<xref:Orleans.Messaging.MessageType`1.Create*> accept an optional `deliverAt`
argument. Compute `deliverAt` from the Durable Jobs clock, which a grain injects as
the keyed <xref:System.TimeProvider> service named
`DurableJobTimeProviderNames.DurableJobs`. The outbox delivers the message
immediately; the receiver accepts, deduplicates, and journals it, and its handler
runs once the message is due. A grain that schedules a message for itself commits
its acceptance in the sending journal write. Scheduled messages stay in the
receiver's journal and snapshots until they are processed, and count toward
`MaxCapacity` from their acceptance until they complete.

### Keep payloads in application state

By default, a sent payload is journaled by the outbox and a received payload by the
inbox. When the application's own journaled state already holds a payload, an outbox
source or an inbox sink keeps that state as the only journaled copy:

- An <xref:Orleans.Messaging.IOutboxMessageSource>, registered with
  <xref:Orleans.Messaging.IOutbox.Register*>, provides messages sent by ID with
  <xref:Orleans.Messaging.IOutbox.Send*>. The outbox journals the message ID and
  delivery state and applies every delivery guarantee of an ordinary message. It
  materializes the message with
  <xref:Orleans.Messaging.IOutboxMessageSource.TryGetMessage*> to validate the send and
  before each delivery attempt; the source returns the same message while it provides
  it, and returns `false` for a message that isn't its own. The first source that
  returns the message owns it.
  - **Withdraw.** <xref:Orleans.Messaging.IOutbox.Withdraw*> removes a pending message,
    for example one the application timed out. Stage it in the same journal write in
    which the source releases its payload. The outbox makes no further attempt and
    doesn't call `OnRetired`; an attempt already in flight can still deliver the
    message.
  - **Retire.** When the message retires, the outbox calls
    <xref:Orleans.Messaging.IOutboxMessageSource.OnRetired*> while it stages the
    removal, so the source's release commits in the same journal write. The
    <xref:Orleans.Messaging.OutboxMessageRetirement> struct carries a `Status`, a
    `Reason`, and a `Code`. Its status is `Delivered` when the receiver accepted the
    message or reported it as a duplicate, `Rejected` when the receiver dead-lettered
    it, and `DeadLettered` when the outbox dead-lettered it after its retry timeout or
    a source failure. `Reason` is the dead letter's reason, and `Code` is the
    application-defined code that the receiver reported with a rejection. An outbox
    dead letter receives its own copy of the materialized message first, so the
    source can release its payload in `OnRetired`.
  - **Fail.** After the send, when the owning source throws, returns an invalid
    message, or returns `false` for its message, the outbox dead-letters only that
    entry and retires it as `DeadLettered` in the same journal write; the grain keeps
    processing its other messages. That dead letter carries only the message ID, with
    an empty payload and a default receiver, and its reason records the failure. A
    recovered entry which no registered source provides is dead-lettered the same
    way, without an owner to notify.

  During `Send`, a source exception or an invalid message propagates to the caller
  and nothing is staged. Sending a pending ID again coalesces; an ID pending as an
  ordinary message throws <xref:System.InvalidOperationException>. A source-provided
  message can't be addressed to the sending grain.
- An <xref:Orleans.Messaging.IInboxMessageSink>, registered with
  <xref:Orleans.Messaging.IInbox.Register*>, receives each deduplicated message before
  the inbox accepts it, including scheduled messages and messages a grain sends to
  itself. A sink that takes the message returns `true` from
  <xref:Orleans.Messaging.IInboxMessageSink.TryAccept*> with the delivery result:
  - `Accepted`: the sink staged the message's effects. The inbox journals only the
    processed record in the same write and returns the result once the write
    completes.
  - `Duplicate`: the sink already holds the message.
  - `Backpressured`: the sink is at capacity, so the sender retries later.
  - `DeadLettered`: created with `DeliveryResult.DeadLettered(reason, code)`, the only
    permanent rejection. The inbox moves the message to its dead letters with the
    reason and the optional application-defined
    <xref:Orleans.Messaging.DeliveryResult.Code>, such as `orders/duplicate-order`,
    which appears as `DeadLetter<T>.Code` and as the sender's
    `OutboxMessageRetirement.Code`. The processed record keeps the reason and code,
    so a duplicate of the message receives the same rejection.

  A sink exception, or any other status, leaves the outcome unknown: the inbox stages
  nothing and fails the delivery, so the sender's outbox retries it until
  `OutboxRetryTimeout`. A message a grain sends to itself fails its `Send` when a
  sink throws or reports `Backpressured`. A sink-owned message occupies no inbox
  capacity, and the sink honors its `DeliverAt` time. Messages that every sink
  declines follow the ordinary inbox path.
- An <xref:Orleans.Messaging.IInboxWorkSource>, registered with
  <xref:Orleans.Messaging.IInbox.Register*>, reports durable work that a component
  such as a sink holds in the grain's journal.
  <xref:Orleans.Messaging.IInboxWorkSource.GetWakeupDelay*> returns how soon a
  deactivated grain must wake to resume the work, or `null` when none is pending.
  The messaging wake-up then reactivates the grain until the work clears, and the
  source's owner resumes its work on activation.

Register sources, sinks, and work sources during construction or activation setup, before journal
recovery. A recovered source-provided message fails the activation when no source is
registered. A sink is offered a message again once its processed record
leaves the deduplication window, so sinks must be idempotent.

## Commit and delivery guarantees

- <xref:Orleans.Messaging.IOutbox.Send*> stages an outgoing message in the grain's
  journaled state. The next journal write persists it together with the grain's
  other staged changes. The outbox delivers a message only after that write
  completes.
- A message addressed to the sending grain never enters the outbox. `Send` stages its
  acceptance in the grain's own inbox, so the same journal write persists the
  acceptance with the grain's other staged changes, and the inbox processes the
  message only after that write completes. When the write fails before it captures
  the staged changes, such as when scheduling its wake-up fails, the acceptance stays
  staged with them and a retried write persists it. A failed storage write discards
  it with the rest of the staged changes, and the next activation recovers the
  committed state.
- Repeating a send with the same ID and an equivalent message (same destination,
  subject, payload, and `DeliverAt` time) keeps the original message. Reusing the
  ID with different content throws <xref:System.InvalidOperationException>.
- The receiving inbox journals an accepted message, or the processed record of a
  message an inbox sink accepts, before returning
  <xref:Orleans.Messaging.DeliveryStatus.Accepted>, and processes it only after that
  acceptance is durable.
- A handler's business changes, outgoing messages, and inbox completion are staged
  together and persist in one journal write.
- Transport is **at-least-once**. If the sender loses its activation after the
  receiver accepts a message and before the outbox records the delivery, the outbox
  sends the message again. Effects apply once per message ID within
  <xref:Orleans.Messaging.Configuration.DurableMessagingOptions.DeduplicationWindow>;
  a handler can execute more than once for the same message, so its preparation must
  tolerate repetition.
- The receiver deduplicates by exact `MessageId`. A repeat of a pending message
  returns <xref:Orleans.Messaging.DeliveryStatus.Duplicate> when its subject,
  payload, and `DeliverAt` time match, and fails when they differ. A repeat of a
  completed message returns `Duplicate` while the completion record is within
  `DeduplicationWindow`, or the original `DeadLettered` reason and code when an
  inbox sink rejected it and no later completion of that ID is recorded. After the
  window ends, the same ID is accepted and processed again.
- A message with <xref:Orleans.Messaging.Envelope.DeliverAt> is delivered and
  accepted immediately, then processed once its acceptance is durable and the
  Durable Jobs clock reaches `DeliverAt`. A message the grain sends to itself is
  accepted in the sending write, so a grain can schedule durable work for itself
  together with its other journaled changes in one write.
- Delivery is **unordered**. Applications that need ordering carry sequence numbers
  and apply messages in application-defined order.

## Execution model

Each activation runs one messaging pump, which starts when activation completes. The
pump processes ready inbox messages, then delivers ready outbox messages, and
repeats while work remains. All pump work, including handler code, runs on the
activation's single-threaded scheduler. A live activation processes due work as soon
as it is durable: journal write completions and finished deliveries signal the pump
directly.

The inbox processes up to
<xref:Orleans.Messaging.Configuration.DurableMessagingOptions.InboxBatchSize> durable, due
messages per pass, one at a time, then writes the journal once for the batch. The
outbox keeps up to
<xref:Orleans.Messaging.Configuration.DurableMessagingOptions.OutboxBatchSize> delivery
attempts in flight for the grain. Each attempt completes on its own and frees its slot
for the next due message, so a slow or unresponsive receiver holds one slot and
messages to other receivers continue. The outbox never holds messages addressed to
the grain itself; a handler that sends one stages its acceptance with the batch's
write, and a later pass processes it.

Deliveries to the grain, through
<xref:Orleans.Messaging.IMessagingExtension.DeliverAsync*>, interleave with the
grain's other requests. A delivery validates, deduplicates, and stages the message's
acceptance synchronously, then waits for that journal write; concurrent deliveries
share journal writes. Inbox message sinks run synchronously within the delivery, so a
sink can run while a request is suspended at an `await`, as handlers can.

One durable job per grain, named `orleans.messaging.pump`, is crash insurance for
work the live pump performs. It wakes the grain while either queue or a registered
work source holds work which the activation can perform, at the earliest retry time,
`DeliverAt` time, or work-source delay. Work which is already due resumes within about
a second of recovery. A journal write schedules a job only when no tracked job is due
within that slack, so a busy grain performs no Durable Jobs operation per write. When
activation fails, Durable Jobs retries the pump job indefinitely with capped backoff
on its clock; `AddMessaging` configures this retry policy for the pump job only.
Every activation re-arms the wake-up at the real due time from recovered state, so
committed work always keeps a wake-up.

Grain state is authoritative. Each pass derives its work from the journaled
messages, their retry times, and their `DeliverAt` times, so early, late, and
duplicate wake-ups are harmless.

The pump keeps the activation alive while a pass processes queued messages and while
outbox deliveries are in flight. Messages that become due later, at their `DeliverAt`
or retry time, leave the activation collectable; the durable job reactivates the grain
when they are due.

### Interleaving

Handlers run as activation work outside grain requests, so handler code interleaves
with grain calls at each `await`, on reentrant and non-reentrant grains alike.
Deliveries and inbox message sinks interleave the same way. Design handlers, sinks,
and grain methods to stay correct under that interleaving. Stateless worker grains
are rejected at activation.

To apply a handler's commit phase exclusively on a non-reentrant grain, run it as a grain
request with <xref:Orleans.GrainBaseExtensions.RunRequestAsync*>. The request waits for
the current grain call to finish and excludes other calls while it runs:

:::code source="../snippets/compiled/Grains/MessagingSnippets.cs" id="messaging_exclusive_commit" language="csharp":::

## Handler outcomes

The handler context, <xref:Orleans.Messaging.IInboxHandlerContext>, exposes the
received <xref:Orleans.Messaging.IInboxHandlerContext.Message> and two terminal
operations:

- <xref:Orleans.Messaging.IInboxHandlerContext.Complete*> stages the message's
  removal and its completion record.
- <xref:Orleans.Messaging.IInboxHandlerContext.Fail*> moves the message to the inbox
  dead letters with a nonblank reason and records its ID as processed.

Each successful handler calls exactly one of them. Repeated `Complete()` calls, or
repeated `Fail` calls with the same reason, coalesce. The context belongs to one
processing attempt and rejects use after the handler returns.

The terminal operation stages its changes immediately, and any journal write on the
activation can capture them. Structure each handler in two phases:

1. **Prepare.** Perform asynchronous I/O, validation, decoding, and fallible
   computation using local values. Observe cancellation before the final phase.
2. **Commit.** In one synchronous block, with no `await` after the first change,
   send typed outgoing messages, apply the business update, call `Complete()` or
   `Fail(reason)`, and return.

This ordering keeps business changes, outgoing messages, and completion in the same
journal write. The inbox persists the batch after its handlers return.

Route each failure by whether it can ever succeed:

- **Business rejection.** Send a typed rejection reply and call `Complete()`.
- **Permanently invalid input**, such as a command which can never become valid.
  Call `Fail(reason)`, or send a typed rejection reply and call `Complete()` when the
  protocol has a reply. The message leaves the inbox at once and frees its capacity
  slot.
- **Transient fault**, such as an unavailable dependency. Throw. The message retries
  with backoff until `InboxRetryTimeout` and keeps its capacity slot meanwhile.

The following outcomes cover the remaining cases:

| Handler outcome | Result |
| --- | --- |
| Exception, or return without a terminal operation | The outcome is unknown: the message retries with backoff until `InboxRetryTimeout`, then moves to the dead letters. |
| Exception or return without a terminal operation after staging journaled changes | The grain deactivates, recovery from the journal discards the staged changes, and the message runs again. That attempt doesn't count toward the retry policy. |
| Exception after a terminal operation | The exception is logged and the staged outcome persists. |
| Conflicting terminal operations | Contract error: messaging stops and the grain deactivates. |

The inbox detects staged changes through the journal's staged change count.

## Backpressure, retries, and dead letters

When the inbox holds
<xref:Orleans.Messaging.Configuration.DurableMessagingOptions.MaxCapacity> pending
messages, it answers new messages with `Backpressured`, and the sender retries them
later. Every pending message, including a scheduled message waiting for its
`DeliverAt` time, counts toward this limit from its acceptance until it completes.
Messages a grain sends to itself are admitted regardless of the limit, because `Send`
is synchronous, and count toward it until they complete. Likewise, the grain's inbox
accepts them before a handler is registered and processes them once one is.

A failed handler attempt schedules a retry after
<xref:Orleans.Messaging.Configuration.DurableMessagingOptions.RetryInitialInterval> multiplied
by an exponential factor that caps at 64. Retries continue until
<xref:Orleans.Messaging.Configuration.DurableMessagingOptions.InboxRetryTimeout> (6 hours by
default) after the first failure, so they span transient faults such as outages and
deployments that fix a bug. The last attempt runs when the timeout elapses; if it fails,
the message moves to the inbox dead letters and its ID is recorded as processed. Call
`Fail(reason)` to dead-letter a message at once.

The outbox removes a message when the receiver reports `Accepted`, `Duplicate`, or
`DeadLettered`. Any other outcome, including a status the sender can't interpret, such
as one from a newer receiver, leaves the outcome unknown and schedules a durable retry
with the same backoff while other messages continue:

- `Backpressured` retries until the receiver takes the message.
- `HandlerNotFound` logs an error, and retries give a deployment time to register the handler.
- Other failures, such as calls which fail without an answer during an infrastructure outage,
  retry like `HandlerNotFound`.

<xref:Orleans.Messaging.Configuration.DurableMessagingOptions.OutboxRetryTimeout> starts
when the message is sent, with its expiration journaled together with the send, and
restarts at the start of each attempt which the receiver answers with `Backpressured`.
No attempt starts after the timeout elapses; the outbox then moves the message to its
dead letters with a reason stating that the retry timeout elapsed. A receiver checks its
deduplication records before answering, and each attempt carries its
<xref:Orleans.Messaging.InboxMessage.RedeliveryDeadline>: the time its retries end
plus the grain call response timeout. A receiver accepts a new message only when its
`DeduplicationWindow` covers that deadline, so every redelivery reaches it while it
still recognizes the message, including when a sender fails to record a delivery and
recovers late. Otherwise the receiver records nothing and answers `DeadLettered` with
<xref:Orleans.Messaging.DeliveryResult.DeduplicationWindowExceededCode>, and the
outbox keeps the message as a dead letter and retires it as `Rejected`. Configure
`OutboxRetryTimeout` shorter than `DeduplicationWindow`, uniformly across the cluster,
with margin for the response timeout; the check assumes clock skew between silos is
much smaller than the window. The Durable Jobs clock governs retry times and timeouts.

Inspect dead letters through <xref:Orleans.Messaging.IMessagingDiagnostics> and remove
handled records with <xref:Orleans.Messaging.IMessagingDiagnostics.RemoveInboxDeadLetter*>
or <xref:Orleans.Messaging.IMessagingDiagnostics.RemoveOutboxDeadLetter*>. Removal
becomes durable with the next journal write. Retention settings bound both
collections; see [Capacity, retries, and retention](messaging-operations.md#capacity-retries-and-retention).

Duplicate checks apply the deduplication window exactly. Expired processed IDs are
dropped from storage when the journal writes a snapshot.

## State, recovery, and deletion

The inbox and the outbox are each one event-sourced journaling state machine,
registered as `orleans.messaging.inbox` and `orleans.messaging.outbox`. They use the
grain's configured journal format; both the JSON and the Orleans binary formats are
supported.

A fresh activation replays the journal and resumes pending work from the recorded
attempt counts and retry times. When messaging fails, for example because staging or
persistence fails, it vetoes every later journal write of that activation and the
grain deactivates; the next activation recovers the committed outcome from the
journal.

After the grain deletes its journal with `DeleteStateAsync`, the inbox and outbox
start empty and continue to accept and send messages.

## Deployment requirements

Configure Durable Jobs storage and Journaling storage, then enable messaging with
<xref:Orleans.Hosting.MessagingExtensions.AddMessaging*> on the silo builder or
service collection. The optional callback configures
<xref:Orleans.Messaging.Configuration.DurableMessagingOptions>, which are validated at startup.
Registration adds Durable Jobs and uses an application-supplied
<xref:System.TimeProvider> when one is registered.

For development, configure in-memory storage and enable messaging:

:::code source="../snippets/compiled/Grains/MessagingSnippets.cs" id="messaging_registration" language="csharp":::

Grains select messaging by implementing <xref:Orleans.Messaging.IMessagingGrain> on
the grain class, an application base class, or a grain interface. Grains deriving
from <xref:Orleans.Journaling.DurableGrain> receive the same setup. During
activation, the messaging grain-type configurator binds the grain's scoped inbox
as its delivery extension, and journal recovery restores messaging state with the
grain's other journaled state.

The following grain registers a typed handler during activation. The handler
dead-letters a notification without text with `Fail(reason)`, because that input can
never become valid. For a valid notification, it computes an optional reply, sends
it, updates its count, and completes the message before returning:

:::code source="../snippets/compiled/Grains/MessagingSnippets.cs" id="messaging_grain" language="csharp":::

An ordinary grain method stages a typed send with a stable command ID and persists it
with one journal write. A client retry with the same command ID stages the same
message: the outbox keeps one entry while the message is pending, and the receiver
recognizes it as a duplicate after delivery:

:::code source="../snippets/compiled/Grains/MessagingSnippets.cs" id="messaging_send" language="csharp":::

Use shared, production-grade storage for multi-silo deployments. In-memory Durable
Jobs and journal storage support development and tests.

Monitor inbox and outbox depth, retry and dead-letter outcomes, and the age of
pending messages. Keep the deduplication window longer than the outbox retry timeout.
For metric names and diagnostics, see [Operate messaging](messaging-operations.md).
