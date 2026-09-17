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
- `IInboxHandler` and `IInboxHandler<TMessage>` define metadata selection and
  `PrepareAsync`, which returns a `ValueTask<Action>` for synchronous application.
  `RouteKeyHandler`, `RoutePrefixHandler`, and `CorrelationHandler` match exact ordinal
  routes, route prefixes at segment boundaries, and correlation
  hierarchies. `IInboxHandlerContext` exposes the envelope and outbound-message helpers.
- `DurableInboxOptions` supplies defaults and validates capacity, retry, retention,
  and batch limits, including an outbox retry age shorter than the deduplication window.

The inbox accepts a message after DurableJobs confirms scheduling and the journal
commits the envelope together with its ownership generation and exact returned job
handle. Recovery restores that pair and repairs an absent owner for pending work.
Callbacks validate generation and physical job identity before processing.

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
