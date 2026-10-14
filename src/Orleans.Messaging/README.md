# Microsoft Orleans Messaging

`Microsoft.Orleans.Messaging` provides grain-scoped durable inboxes and outboxes
built on Orleans Journaling and Durable Jobs. A grain sends a message by staging it
in its outbox; the message persists with the grain's other journaled state and is
delivered to the receiver's inbox, which processes it at least once and deduplicates
it by message ID. A message a grain sends to itself skips the outbox: its own inbox
accepts it in the same journal write.

## Setup

Configure Journaling and Durable Jobs storage, then call `AddMessaging` on the silo
builder or service collection. The optional `DurableMessagingOptions` callback configures
capacity, batch sizes, retries, deduplication, and dead-letter retention; options are
validated at startup. Register typed subjects with `AddMessageType<T>(subject)`.

Messaging grains derive from `DurableGrain` or implement `IMessagingGrain`, and
constructor-inject `IInbox` and `IOutbox`. During activation, the messaging grain-type
configurator installs the grain's messaging extension. Stateless worker grains are
rejected; reentrant and interleaving grains are supported, and the grain's code is
responsible for correctness when calls interleave.

## Messages

`Envelope` carries:

- `MessageId`: an application-supplied `HierarchicalKey` wrapped in a `MessageId`, at
  most 1,024 UTF-8 bytes. It identifies one command at the receiver.
- `Payload`: opaque bytes; an empty payload is valid.
- `Subject`: an optional ordinal name, at most 256 UTF-8 bytes, that selects a typed
  handler.
- `Headers`: optional named byte arrays with nonempty keys and non-null values.
- `DeliverAt`: an optional time on the Durable Jobs clock. The receiver accepts the
  message immediately and processes it once that clock reaches `DeliverAt`. A message
  with `DeliverAt` does not count toward `MaxCapacity`, including after it becomes due.

`OutboxMessage` pairs an envelope with its receiver. `InboxMessage` carries the same
envelope at the receiver. Keep published payload and header arrays unchanged.

`MessageId` wraps a `HierarchicalKey`. Create one with `new MessageId(key)` or
`MessageId.Create(segments)`, and read the key through `Key`. `HierarchicalKey` wraps
its canonical escaped path, and equality and hashing of both types compare that path
ordinally. Use `HierarchicalKey.Create`, `CreateChildKey`, and `Append` to build keys,
and `GetParent`, `IsParentOf`, `IsChildOf`, `IsAncestorOf`, and the segment enumerator
to navigate them.

## Handle messages

`IInbox.Register(IInboxHandler)` adds a handler. Each message goes to the first
registered handler whose `CanHandle(InboxMessage)` returns `true`. Selection reads
only the message; the selected handler finishes it in `HandleAsync`. Handlers can be
registered at any time; registering during construction or activation makes them
available to recovered messages.

`InboxExtensions.Register` binds a `MessageType<T>` subject to a typed method:

```csharp
inbox.Register(reserve, HandleReserve);
inbox.Register(reserve, this, static (request, grain, context) => grain.HandleReserve(request, context));
```

Overloads accept synchronous or asynchronous delegates, with or without a `TArg`
argument for static, allocation-free delegates.

A handler finishes each message with `IInboxHandlerContext.Complete()` or
`Fail(reason)`:

- `Complete()` stages the message's removal and completion record.
- `Fail(reason)` moves the message to the inbox dead letters and records its ID as
  processed.
- A handler exception before either call leaves the outcome unknown, so the message
  retries with exponential backoff until `InboxRetryTimeout` (6 hours) after its
  first failure, then moves to the dead letters.
- Returning without either call, or calling both, deactivates the grain.

The inbox persists each batch's outcomes, business changes, and outgoing messages in
one journal write. Perform asynchronous and fallible preparation with local values,
then apply state changes, send messages, and complete in one synchronous block.

## Send messages

`IOutbox.Send(OutboxMessage)` stages a message. `OutboxExtensions` adds typed helpers:

- `Send(type, messageId, receiver, body)` encodes and stages one message; a batch
  overload stages several.
- `SendReply(type, context, destination, body)` stages a reply whose ID is the
  received message ID's `result` child, sent to a destination the application
  carries in its request.

`MessageType<T>.Create` and `OutboxMessage.ToInboxMessage()` build messages for direct
inbox delivery. Sending equivalent content with a pending ID coalesces with the
original message; different content throws `InvalidOperationException`.

When the receiver is the sending grain, `Send` stages the message's acceptance in
the grain's own inbox instead of the outbox, applying the inbox's identity rules: an
ID that is pending with equivalent content or completed within `DeduplicationWindow`
is a duplicate, and a pending ID with different content throws
`InvalidOperationException`. The acceptance persists in the caller's next journal
write, atomically with its other staged state, and the inbox processes the message
once that write completes. Because `Send` is synchronous, the grain's inbox admits the
message regardless of `MaxCapacity` and of handler registration; an unscheduled
self-sent message still occupies capacity for other senders until it completes, and
processing waits for a handler.

## Journal payloads once

When a payload already lives in the grain's own journaled state, an outbox source or
an inbox sink keeps that state as its only journaled copy. Register both during
construction or activation setup, before journal recovery.

- `IOutbox.Register(IOutboxMessageSource)` adds a source, and
  `IOutbox.Send(messageId)` stages a reference entry with the message ID and delivery
  state. The first source whose `TryGetMessage` returns the message owns it; the
  outbox calls `TryGetMessage` to validate the send and before each delivery attempt,
  and the source returns the same `OutboxMessage` while it provides the message. Every
  delivery rule above applies. A source withdraws a pending message by returning
  `false`: the outbox removes the entry before its next attempt, without delivery or
  `OnRetired`, and an attempt already in flight still completes. When the message
  retires, the outbox calls `OnRetired` with
  `OutboxMessageRetirement.Delivered` (`Accepted` or `Duplicate`) or `DeadLettered`
  (receiver-reported or outbox dead-lettering) while it stages the removal, so the
  source's changes commit in the same journal write. An outbox dead letter stores the
  materialized message. After the send, a source that throws or returns an invalid
  message, such as one with a different ID, dead-letters that entry alone in the
  same journal write as its `DeadLettered` retirement; the grain keeps processing its
  other messages. That dead letter carries only the message ID, with an empty payload
  and a default receiver, and its reason records the failure. Sending a pending
  reference again coalesces; an ID pending as an ordinary message, or one which no
  source provides, throws `InvalidOperationException`, and an exception or invalid
  message from the source during `Send` propagates without staging anything. A source
  message addressed to the sending grain throws `ArgumentException`. A recovered
  reference fails the activation when no source is registered.
- `IInbox.Register(IInboxMessageSink)` adds a sink. After validation and
  deduplication, the inbox offers each message, including self-sent and scheduled
  messages, to the sinks in order. The first sink whose `TryAccept` returns `true`
  reports the `DeliveryResult`:
  - `Accepted`: the sink staged the message in its own state. The inbox journals
    only the processed record in the same write and returns `Accepted` once it is
    durable.
  - `Duplicate`: the sink already holds the message.
  - `Backpressured`: the sink is at capacity, so the sender retries. A self-sent
    message fails its `Send` instead.
  - `DeadLettered`: the inbox dead-letters the message with the sink's reason. A
    sink that throws or reports another status is treated the same way. The processed
    record keeps the reason, so a duplicate within `DeduplicationWindow`, such as a
    retry whose first answer was lost, receives the same rejection.

  Sink-owned messages never occupy capacity and have no inbox retry lifecycle; the
  sink honors their `DeliverAt`. Declined messages follow the handler path once
  `DeliverAt` passes. A message is offered again after its processed record leaves
  `DeduplicationWindow`, so sinks must be idempotent.
- `IInbox.Register(IInboxWorkSource)` adds a work source: a component, such as a
  sink, that holds durable work in the grain's journal. `GetWakeupDelay` returns how
  soon a deactivated grain must wake to resume that work, or `null` when none is
  pending. Every journal write that leaves work pending schedules the messaging
  wake-up before it is stored, and the wake-up reactivates the grain until the work
  clears. The source's owner resumes its work on activation.

## Delivery and processing

Staged outgoing messages are delivered only after the sender's journal write
completes. Accepted inbox messages are processed only after the receiver's journal
write completes. Each receiver returns a `DeliveryResult`:

- `Accepted`: the message is durable in the inbox, or, for a message an inbox sink
  accepted, in the sink's state together with its processed record.
- `Duplicate`: the message ID is pending or completed within `DeduplicationWindow`.
  A completed message which an inbox sink rejected is answered with its original
  `DeadLettered` result instead.
- `Backpressured`: the inbox holds `MaxCapacity` pending messages without `DeliverAt`.
  The inbox admits scheduled messages and messages its grain sends to itself
  regardless of `MaxCapacity`.
- `HandlerNotFound`: the receiver has no registered handlers.
- `DeadLettered`: the receiver moved the message to its dead-letter store.

`Accepted`, `Duplicate`, and `DeadLettered` are terminal: the outbox removes the
message. Any other outcome schedules a durable retry with exponential backoff,
and later batches deliver other messages meanwhile. The outbox retries
`Backpressured` until the receiver takes the message. It logs `HandlerNotFound` as
an error. Other failures, including calls which fail without an answer during an
outage, dead-letter the message after `OutboxRetryTimeout` (6 hours) from the first
failed attempt since the last `Backpressured` answer. Each attempt runs through the
`DeliveryResiliencePipeline` Polly pipeline, which can add timeouts, retries, or a
circuit breaker. Delivery order is unspecified.

Each activation runs one messaging pump on its scheduler, processing inbox batches
and delivering outbox batches. One durable job per grain wakes the pump while either
queue or a registered work source holds work. Journaled state determines which
messages are due, so each wake-up processes exactly the ready work.

## State

The inbox and the outbox are event-sourced journal states named
`orleans.messaging.inbox` and `orleans.messaging.outbox`, and work with the JSON and
Orleans binary journal formats. They persist alongside the grain's other journaled
state. After the grain's state is deleted, both start empty and continue working.

Completed message IDs are retained for `DeduplicationWindow`. Dead letters are
retained up to `DeadLetterRetentionPeriod` and `MaxRetainedDeadLetters` for each queue.

## Diagnostics

`IMessagingDiagnostics` exposes `InboxDeadLetters` and `OutboxDeadLetters`, with each
record's message, reason, attempt count, and dead-letter time.
`RemoveInboxDeadLetter` and `RemoveOutboxDeadLetter` remove records, durably at the
grain's next journal write.

The `Microsoft.Orleans` meter publishes counters for received, processed, sent, and
delivered messages tagged by grain type and status; histograms for processing and
delivery duration; and `orleans-messaging-inbox-depth` and
`orleans-messaging-outbox-depth` up-down counters for pending work. A message a grain
sends to itself counts as sent and as received by its inbox, and never contributes
to outbox delivery or depth. Reference sends count as sent; a reference entry whose
source fails to provide its message counts as delivered `source_error`. A message an
inbox sink accepts counts as received `accepted` and processed `success`; one which
the sink dead-letters counts as received and processed `dead_lettered`.
