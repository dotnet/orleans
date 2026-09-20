# Microsoft Orleans Durable Messaging

This project supplies the durable messaging protocol and handler-routing contracts:

- `DurableEnvelope` identifies a message by sender and message ID and carries its
  destination, route, correlation key, reply destination, and creation timestamp.
- `DurableEnvelopeBuilder` serializes the body and each request-context value into
  an envelope buffer. `DurableEnvelopeData` supports deferred typed reads and raw
  byte access, preserving declared type metadata across serialization and copying.
- `HierarchicalKey` provides escaped, slash-separated correlation keys with
  segment-aware parent, child, and ancestor comparisons.
- `IDurableInbox`, `IDurableOutbox`, and `IDurableInboxExtension` define message
  registration, inspection, enqueue, and delivery operations. `DeliveryResult` and
  `DeliveryStatus` describe delivery outcomes.
- `IPreparedOutboxBatch` is an opaque, activation-local disposable handle returned
  by `IDurableOutbox.PrepareSendAsync`. `Send(batch)` synchronously stages its prepared
  outgoing intents.
- `IInboxHandler` and `IInboxHandler<TMessage>` define metadata selection and
  `PrepareAsync`, which returns a `ValueTask<Action>` for synchronous application.
  `RouteKeyHandler`, `RoutePrefixHandler`, and `CorrelationHandler` match exact ordinal
  routes, route prefixes at segment boundaries, and correlation
  hierarchies. `IInboxHandlerContext` exposes the envelope and outbound-message helpers.
- `DurableInboxOptions` supplies defaults and validates capacity, retry, retention,
  and batch limits, including an outbox retry age shorter than the deduplication window.

Handlers perform validation, asynchronous I/O, and envelope serialization using local
values, then await `context.Outbox.PrepareSendAsync(messages, cancellationToken)`.
Preparation copies and validates the envelope collection and confirms a viable durable
self-wakeup before shared business or journaled mutations. An empty batch is a valid
no-op and requires no new wakeup. Durable state determines the work for duplicate and
orphan wakeups; wakeups defer while local preparation or persistence is unresolved.

After revalidating prepared results, handlers return a non-null synchronous action
which applies business changes and calls `context.Send(batch)` or
`context.Outbox.Send(batch)`. Messaging invokes that action once for the prepared attempt
and stages inbox completion in the same turn before ordinary journal persistence.
Each applied effect is safe to commit with shared pending changes. External dispatch
starts after the corresponding captured intents are acknowledged.

The handler runtime tracks preparations from their start and owns resulting batches
through attempt completion, including late results after cancellation or failure. Keep
the batch alive for the returned action. Ordinary callers must await every preparation
operation and dispose each successfully returned batch. Their disposal scope spans
synchronous business mutations, `Send(batch)`, and the ordinary journal-write await.
Disposal abandons unstaged preparation; staged wakeup and intent ownership stays with
its pending/captured/acknowledged cohort through the actual persistence outcome. When
a caller cancels its wait, owned scheduling continues and releases the unclaimed batch
after its actual outcome. Activation retirement cleans up remaining owned preparation.

Repeatedly sending the same live already-staged batch has no additional effect within
a valid current scope. Every call requires the owning activation and scope; handler
sends require their matching current action. Outside-scope, stale, wrong-attempt,
disposed, or foreign handles are rejected before mutation.
Envelope identity/equivalence checks retain the existing routing, payload and declared-type
metadata semantics.

This intermediate project is non-packable while the runtime and hosting layers are
assembled into `Microsoft.Orleans.DurableMessaging`.
