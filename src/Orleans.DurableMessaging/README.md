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
  registration, inspection, enqueue, and delivery operations. `IDurableOutbox.Send(envelope)`
  synchronously stages a fully built envelope alongside business mutations. `DeliveryResult` and
  `DeliveryStatus` describe delivery outcomes.
- `IPreparedOutboxBatch` is an opaque, activation-local disposable handle returned
  by `IDurableOutbox.PrepareSendAsync`. `Send(batch)` synchronously stages its prepared
  outgoing intents.
- `IInboxHandler` and `IInboxHandler<TMessage>` define metadata selection and
  `HandleAsync`, which returns a `ValueTask` for the handler method outcome.
  `RouteKeyHandler`, `RoutePrefixHandler`, and `CorrelationHandler` match exact ordinal
  routes, route prefixes at segment boundaries, and correlation
  hierarchies. `IInboxHandlerContext` exposes the envelope and outbound-message helpers.
- `DurableInboxOptions` supplies defaults and validates capacity, retry, retention,
  and batch limits, including an outbox retry age shorter than the deduplication window.

Handlers perform asynchronous I/O, validation, envelope construction, and cancellation
checks using local values before the first shared business or journaled mutation.
From that first shared mutation through method completion, execute synchronously with
no awaits. Apply complete safe-to-commit changes, stage outgoing envelopes or optional
prepared batches, call `context.Complete()`, and return without further awaits.
This mutation boundary is the handler implementation's trusted responsibility.

`Complete()` synchronously stages inbox completion and deduplication in that same turn.
The runtime awaits `HandleAsync`'s outcome and then owns actual journal persistence,
acknowledgement, and resource cleanup. A valid final block completes even if attempt
cancellation arrives after shared updates have begun; check cancellation beforehand.
Repeated completion in the same still-active completed attempt coalesces after attempt
identity validation. Wrong-attempt and retired completion calls are rejected. Sending
and further preparation end at completion; metadata remains inspectable.

The runtime outbox supplies a final journal capture hook which establishes the durable
self-wakeup after ordinary before callbacks and directly before capture. It covers
messages staged during asynchronous prerequisite work. Captured messages and their
owner are acknowledged together before dispatch becomes eligible. Prerequisite failure
retains safe pending business state and messages for an explicit write retry or owner
retirement; post-commit hook failure reports an already completed persistence operation.

`PrepareSendAsync(messages, cancellationToken)` remains available when an application
needs the wakeup established earlier, before business mutation. It copies and validates
the envelope collection and returns an activation-local batch for synchronous staging.
An empty batch is a valid no-op and requires no new wakeup. Durable state determines the
work for duplicate and orphan wakeups; unresolved preparation or persistence defers them.

The handler runtime tracks preparations from their start and owns resulting batches
through attempt completion, including late results after cancellation or failure. Keep
the batch alive through the handler method and subsequent owned persistence. Ordinary callers must await every preparation
operation and dispose each successfully returned batch. Their disposal scope spans
synchronous business mutations, `Send(batch)`, and the ordinary journal-write await.
Disposal abandons unstaged preparation; staged wakeup and intent ownership stays with
its pending/captured/acknowledged cohort through the actual persistence outcome. When
a caller cancels its wait, owned scheduling continues and releases the unclaimed batch
after its actual outcome. Activation retirement cleans up remaining owned preparation.

Repeatedly sending the same live already-staged batch has no additional effect within
a valid current scope. Every call requires the owning activation and scope; handler
sends require their matching active attempt before completion. Outside-scope, stale, wrong-attempt,
disposed, or foreign handles are rejected before mutation.
Envelope identity/equivalence checks retain the existing routing, payload and declared-type
metadata semantics.

This intermediate project is non-packable while the runtime and hosting layers are
assembled into `Microsoft.Orleans.DurableMessaging`.
