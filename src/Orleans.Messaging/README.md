# Microsoft Orleans Messaging

`Microsoft.Orleans.Messaging` provides grain-scoped durable inboxes and outboxes
for Microsoft Orleans. A grain sends a message by staging it in its outbox; the
receiver's inbox processes it at least once and deduplicates it by message ID.

This package defines the messaging contracts: envelopes, hierarchical message
identities, inbox and outbox interfaces, handler registration, typed message
bindings, delivery results, and options.

## Messages

`Envelope` carries:

- `MessageId`: a `HierarchicalKey` supplied by the application, at most 1,024 UTF-8
  bytes. It identifies one command at the receiver.
- `Payload`: opaque bytes; an empty payload is valid.
- `Subject`: an optional ordinal name, at most 256 UTF-8 bytes, that selects a typed
  handler.
- `Headers`: optional named byte arrays with nonempty keys and non-null values.

`OutboxMessage` pairs an envelope with its receiver. `InboxMessage` carries the same
envelope at the receiver. Keep published payload and header arrays unchanged.

`HierarchicalKey` wraps its canonical escaped path. Equality and hashing compare that
path ordinally. Use `Create`, `CreateChildKey`, and `Append` to build keys, and
`GetParent`, `IsParentOf`, `IsChildOf`, `IsAncestorOf`, and the segment enumerator to
navigate them.

## Handle messages

`IInbox.Register(IInboxHandler)` adds a handler. Each message goes to the first
registered handler whose `CanHandle` returns `true`.

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

## Delivery results

Each receiver returns a `DeliveryResult`:

- `Accepted`: the message is durable in the inbox.
- `Duplicate`: the message ID is pending or completed within `DeduplicationWindow`.
- `Backpressured`: the inbox holds `MaxCapacity` pending messages.
- `HandlerNotFound`: the receiver has no registered handlers.
- `DeadLettered`: the message moved to the receiver's dead letters.

`InboxOptions` configures capacity, batch sizes, retries, deduplication, and
dead-letter retention.
