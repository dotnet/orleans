# Microsoft Orleans Messaging

`Microsoft.Orleans.Messaging` defines the contracts for grain-scoped durable
inboxes and outboxes in Microsoft Orleans: envelopes, hierarchical message
identities, inbox and outbox interfaces, handler, sink, and source registration,
typed message bindings, delivery results, and options.

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
  with `DeliverAt` counts toward `MaxCapacity` from its acceptance until it completes.

`OutboxMessage` pairs an envelope with its receiver. `InboxMessage` carries the same
envelope and the logical send's `SentAt` time at the receiver. Keep published payload
and header arrays unchanged.

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
argument for static, allocation-free delegates. Register typed subjects with
`AddMessageType<T>(subject)`.

A handler finishes each message with `IInboxHandlerContext.Complete()` or
`Fail(reason)`:

- `Complete()` stages the message's removal and completion record.
- `Fail(reason)` moves the message to the inbox dead letters and records its ID as
  processed.

## Send messages

`IOutbox.Send(OutboxMessage)` stages a message. `OutboxExtensions` adds typed helpers:

- `Send(type, messageId, receiver, body)` encodes and stages one message; a batch
  overload stages several.
- `SendReply(type, context, destination, body)` stages a reply whose ID is the
  received message ID's `result` child, sent to a destination the application
  carries in its request.

`MessageType<T>.Create` and `OutboxMessage.ToInboxMessage(sentAt)` build messages for
direct inbox delivery.

## Journal payloads once

When a payload already lives in the grain's own journaled state, an outbox source or
an inbox sink keeps that state as its only journaled copy. Register both during
construction or activation setup, before journal recovery.

- `IOutbox.Register(IOutboxMessageSource)` adds a source, and
  `IOutbox.Send(messageId)` stages a reference entry with the message ID and delivery
  state. The first source whose `TryGetMessage` returns the message owns it, and
  returns the same `OutboxMessage` while it provides the message. `TryGetMessage`
  returns `false` for a message that isn't the source's. A source withdraws a pending
  message with `IOutbox.Withdraw(messageId)`, staged in the same journal write that
  releases its payload. When the message retires, `OnRetired` reports an
  `OutboxMessageRetirement` whose `Status` is `Delivered`, `Rejected` (with the
  receiver's reason and code), or `DeadLettered` (with the outbox's reason). After the
  send, a source that throws, returns an invalid message, or no longer provides the
  message dead-letters that entry alone, with `OnRetired` in the same journal write; an
  exception during `Send` propagates without staging anything. A recovered entry that
  no source provides is dead-lettered without an owner to notify. `OnRetired` runs
  once per entry in the current activation. If the activation fails before the
  retirement write commits, recovery restores the pending entry and the source's
  prior journaled state, and retiring the recovered entry invokes the callback
  again. Stage durable callback effects in the same journal write as retirement;
  make effects outside that write idempotent across repeated calls.
- `IInbox.Register(IInboxMessageSink)` adds a sink. The inbox offers each message,
  including self-sent and scheduled messages, to the sinks in order. The first sink
  whose `TryAccept` returns `true` reports the `DeliveryResult`:
  - `Accepted`: the sink staged the message in its own journaled state.
  - `Duplicate`: the sink already holds the message.
  - `Backpressured`: the sink is at capacity. A self-sent message fails its `Send`.
  - `DeadLettered`: the sink rejects the message with a reason and an optional
    application-defined `Code`. This is the only permanent rejection.

  A sink that throws or reports another status leaves the outcome unknown: the inbox
  stages nothing and fails the delivery, so the sender retries it.

  A sink that takes a scheduled message honors its `DeliverAt`. Sinks are idempotent.

## Delivery results

Each receiver returns a `DeliveryResult`:

- `Accepted`: the message is durable in the inbox, or, for a message an inbox sink
  accepted, in the sink's state together with its processed record.
- `Duplicate`: the message ID is pending or has a retained processed record. A
  duplicate of a message that a sink rejected receives that rejection instead.
- `Backpressured`: the inbox holds `MaxCapacity` pending messages.
- `HandlerNotFound`: the receiver has no registered handlers.
- `DeadLettered`: the receiver durably rejected the message and moved it to its
  dead letters, with a reason and an optional `Code`.
- `Expired`: the message was sent before the receiver's deduplication window and
  the receiver retains no outcome for it. This attempt stages no message effects;
  an earlier attempt can have been accepted, so the delivery outcome is unknown.

`DurableMessagingOptions` configures capacity, batch sizes, retries, deduplication,
and dead-letter retention.

## Deduplication window

The outbox journals each send's `SentAt` time; every delivery attempt and recovery
reuses it. `OutboxRetryTimeout`, six hours by default, bounds delivery attempts
after that time, including while the receiver is backpressured.

The inbox retains each processed record for `DeduplicationWindow`, seven days by
default, after the message's `SentAt`, and then removes it. Before removing records,
the inbox journals an admission floor: its clock minus `DeduplicationWindow`, which
only advances. Pending and processed records determine known outcomes first. A
message the inbox no longer tracks whose `SentAt` is at or below the floor receives
`Expired` before any sink or handler runs. Recovery and migration preserve the floor.
The inbox never stores a `SentAt` later than its clock plus `DeduplicationWindow`.
It rejects such a delivery with `ArgumentOutOfRangeException`, staging nothing, when
the message is untracked or the time would extend its record, so the sender retries
it within its retry budget. A tracked message with an equal or earlier `SentAt`
still receives its recorded outcome, including after the receiver's clock steps
backward. Every record therefore becomes eligible for removal within twice the
window.

A forward clock jump can expire undelivered work early; a backward jump can delay
cleanup and prolong retries. The journaled floor keeps removed messages expired in
both cases. Direct callers reuse a command's `SentAt` across retries.
`OutboxRetryTimeout` must be shorter than `DeduplicationWindow`, so the retry
budget ends within the receiver's retention window.

## Recoverable terminal outcomes

Applications which need to respond durably to expired or rejected sends register
an `IOutboxMessageSource` and stage those sends through `IOutbox.Send(messageId)`.
In `OnRetired`, stage the application's terminal outcome and any pending follow-up
work in journaled state, alongside releasing the source's payload. That work commits
atomically with outbox retirement and is recovered with the grain after a crash.
The application resumes pending follow-up work from its recovered state.

`Delivered` means the receiver durably accepted the message or recognized it as a
duplicate. `Rejected` carries the receiver's durable rejection. `DeadLettered`
means the outbox stopped delivering and the receiver's earlier outcome is unknown,
including when it received `Expired`. Reconcile uncertain outcomes using the
application's idempotency protocol before initiating a new send. Reuse the
original `SentAt` for retries of the same logical send.

Dead-letter retention limits control diagnostic history. The application-owned
outcome and follow-up work have the lifetime the application gives them.
