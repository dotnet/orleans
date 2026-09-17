# Microsoft Orleans Durable Messaging

This project supplies the durable messaging protocol, handler routing, and journaled inbox runtime:

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

The inbox accepts a message after DurableJobs confirms scheduling and the journal
commits the envelope together with its ownership generation and exact returned job
handle. Recovery restores that pair and repairs an absent owner for pending work.
Callbacks validate generation and physical job identity before processing.
Delivery requires a nonempty message ID, a nondefault sender, and an envelope data
container before duplicate lookup or admission. Serialized null message bodies remain
valid payloads. Empty-owner clearing shares the inbox admission gate with delivery,
so direct interleaved delivery proceeds after the clear's durable outcome.

Handlers execute sequentially inside an admitted journal operation's preparation.
The messaging observer prepares the inbox handler before the outbox prerequisites,
then synchronously finalizes their state. Journaled handler effects, outgoing intents,
inbox completion, and `(SenderId, MessageId)` deduplication are captured together;
other queued writes wait for that operation. Handlers complete fallible work using
local values before staging safe application effects. Expected preparation failures
produce bounded retry or dead-letter accounting in the admitted operation.

Acceptance and ownership repair retain local proposals until scheduling is acknowledged
and a healthy journal operation admits them. Its finalizer applies the complete envelope
and ownership pair. A journal failure permanently fences the activation, signals pending
preparations and callbacks, and faults its waiters. A fresh activation replays the actual
durable outcome, including commits whose acknowledgement failed.
A delivery caller can cancel its wait while the owned operation retains admission
through completion. Activation shutdown drains that operation, and delivery failures
are logged and observed even after the caller has left.

Retained duplicates return `Duplicate`; expiry permits
acceptance again. Capacity limits return `Backpressured` before persistence.
`CanHandle` implementations are pure metadata predicates: the handler keeps grain
state and injected durable state unchanged until `HandleAsync`. The selection
context enforces access to metadata and grain identity; its outbound-message APIs
throw during selection. Journal observers reject explicit write/delete requests
inside selection and handling, preserving the runtime's completion commit.
A route miss preserves the grain's staged state for its next journal write.
Journal deletion requires completed delivery operations, released inbox gates, and
idle pump leases. Interleaved control calls observe that quiescence boundary; the
active handler retains its own logical persistence-request guard.
Superseded queued pump executions release their retained result and cancellation
registration. Inbox shutdown, terminal failure, and quiescent deletion clear only
inbox execution entries; subsequent work recovers through the durable wakeup path.

Processed-record maintenance starts at the earliest tracked expiry and amortizes
subsequent maintenance cycles to at most once per quarter of the deduplication window.
It joins admitted writes while the inbox remains busy; durable pump maintenance and
fresh activation also remove due records. A maintenance-only write requires expired
records. Delivery still evaluates each duplicate against the exact retention boundary.

Exact route registration retains the original handler instance and takes precedence
over generic handler selection. Operational diagnostics expose retained dead letters
and stage their removal for the next journal write.

This intermediate project remains non-packable. Receiver tests compose the runtime
with existing Journaling and DurableJobs services using test-only registration;
outbound dispatch and public hosting composition are assembled in later layers.
