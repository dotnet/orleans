# Microsoft Orleans Messaging

`Microsoft.Orleans.Messaging` provides durable, grain-scoped inbox processing for
Microsoft Orleans, built on Orleans Journaling and Durable Jobs, together with the
messaging contracts for inboxes and outboxes. A grain's inbox accepts messages durably,
persists them with the grain's other journaled state, processes each at least once,
and deduplicates them by message ID.

## Setup

Configure Journaling and Durable Jobs storage, then call `AddMessaging` on the silo
builder or service collection. The optional `DurableMessagingOptions` callback configures
capacity, batch sizes, retries, deduplication, and dead-letter retention; options are
validated at startup. Register typed subjects with `AddMessageType<T>(subject)`.

Messaging grains derive from `DurableGrain` or implement `IMessagingGrain`, and
constructor-inject `IInbox`. During activation, the messaging grain-type
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

The inbox persists each batch's outcomes and business changes in one journal write.
Perform asynchronous and fallible preparation with local values, then apply state
changes and complete in one synchronous block.

## Send messages

`IOutbox.Send(OutboxMessage)` stages a message. `OutboxExtensions` adds typed helpers:

- `Send(type, messageId, receiver, body)` encodes and stages one message; a batch
  overload stages several.
- `SendReply(type, context, destination, body)` stages a reply whose ID is the
  received message ID's `result` child, sent to a destination the application
  carries in its request.

`MessageType<T>.Create` and `OutboxMessage.ToInboxMessage()` build messages for direct
inbox delivery.

## Journal payloads once

When a payload already lives in the grain's own journaled state, an outbox source or
an inbox sink keeps that state as its only journaled copy. Register both during
construction or activation setup, before journal recovery.

- `IOutbox.Register(IOutboxMessageSource)` adds a source, and
  `IOutbox.Send(messageId)` stages a reference entry with the message ID and delivery
  state. The first source whose `TryGetMessage` returns the message owns it, and
  returns the same `OutboxMessage` while it provides the message. A source withdraws
  a pending message by returning `false`. When the message retires, `OnRetired`
  reports `OutboxMessageRetirement.Delivered` or `DeadLettered`. After the send, a
  source that throws or returns an invalid message dead-letters that entry alone,
  with `OnRetired(DeadLettered)` in the same journal write; an exception during
  `Send` propagates without staging anything.
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
    sink that throws or reports another status is treated the same way.

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

`IMessagingExtension.DeliverAsync` delivers a message to a grain's inbox. Accepted inbox
messages are processed only after the receiver's journal write completes. Each
receiver returns a `DeliveryResult`:

- `Accepted`: the message is durable in the inbox, or, for a message an inbox sink
  accepted, in the sink's state together with its processed record.
- `Duplicate`: the message ID is pending or completed within `DeduplicationWindow`.
- `Backpressured`: the inbox holds `MaxCapacity` pending messages without `DeliverAt`.
  The inbox admits scheduled messages regardless of `MaxCapacity`.
- `HandlerNotFound`: the receiver has no registered handlers.

Each activation runs one messaging pump on its scheduler, processing inbox batches.
One durable job per grain wakes the pump while the inbox or a registered work source
holds work. Journaled state determines which messages are due, so each wake-up
processes exactly the ready work.

## State

The inbox is an event-sourced journal state named `orleans.messaging.inbox`, and works
with the JSON and Orleans binary journal formats. It persists alongside the grain's
other journaled state. After the grain's state is deleted, it starts empty and
continues working.

Completed message IDs are retained for `DeduplicationWindow`. Dead letters are
retained up to `DeadLetterRetentionPeriod` and `MaxRetainedDeadLetters`.

## Diagnostics

`IMessagingDiagnostics` exposes `InboxDeadLetters`, with each record's message,
reason, attempt count, and dead-letter time. `RemoveInboxDeadLetter` removes a record,
durably at the grain's next journal write.

The `Microsoft.Orleans` meter publishes counters for received and processed messages
tagged by grain type and status, a histogram for processing duration, and the
`orleans-messaging-inbox-depth` up-down counter for pending work.
