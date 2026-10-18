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
- `IInbox.Register(IInboxMessageSink)` adds a sink. The inbox offers each message,
  including self-sent and scheduled messages, to the sinks in order. The first sink
  whose `TryAccept` returns `true` reports the `DeliveryResult`:
  - `Accepted`: the sink staged the message in its own journaled state.
  - `Duplicate`: the sink already holds the message.
  - `Backpressured`: the sink is at capacity. A self-sent message fails its `Send`.
  - `DeadLettered`: the sink rejects the message with a reason. A sink that throws
    or reports another status dead-letters the message.

  A sink that takes a scheduled message honors its `DeliverAt`. Sinks are idempotent.

## Delivery results

Each receiver returns a `DeliveryResult`:

- `Accepted`: the message is durable in the inbox, or, for a message an inbox sink
  accepted, in the sink's state together with its processed record.
- `Duplicate`: the message ID is pending or completed within `DeduplicationWindow`.
- `Backpressured`: the inbox holds `MaxCapacity` pending messages without `DeliverAt`.
  The inbox admits scheduled messages regardless of `MaxCapacity`.
- `HandlerNotFound`: the receiver has no registered handlers.
- `DeadLettered`: the message moved to the receiver's dead letters.

`DurableMessagingOptions` configures capacity, batch sizes, retries, deduplication,
and dead-letter retention.
