# Microsoft Orleans Durable Messaging

This project supplies the durable messaging protocol, handler routing, and journaled inbox and outbox runtime:

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

`IDurableMessagingGrain` is a local capability which selects durable messaging activation
setup. Implement it on a grain class, an application base class, or an application grain
interface. Existing `DurableGrain` implementations receive the same setup automatically.
Selection is cached with the concrete grain type, and each activation reuses its scoped
inbox, outbox, and registered journaled messaging states.

Setup validates the grain's execution model after the runtime assigns the constructed
grain instance and before lifecycle startup, journal initialization, or replay. Supported
activations use a single, noninterleaving grain execution model. Grain construction and
local state registration precede validation. The standard state manager enrolls in the
grain lifecycle during grain-bound construction; messaging registers its actual
persisted states with that enrolled manager. A scoped factory using an explicit
`JournalId` enrolls its manager in the grain lifecycle before returning it. Standalone managers have
caller-owned initialization and disposal.

The inbox accepts a message after DurableJobs confirms scheduling and the journal
commits the envelope together with its ownership generation and exact returned job
handle. Admission counts acknowledged pending work in constant time from inbox and
provisional-acceptance counts. Recovery restores that pair and repairs an absent owner
for pending work.
Callbacks validate generation and physical job identity before processing.
Delivery requires a nonempty message ID, a nondefault sender, and an envelope data
container before duplicate lookup or admission. Serialized null message bodies remain
valid payloads. Empty-owner clearing shares the inbox admission gate with delivery,
so direct interleaved delivery proceeds after the clear's durable outcome.

Handlers prepare local values asynchronously and return a non-null synchronous action.
Messaging invokes the action once for its prepared attempt and stages inbox completion
and `(SenderId, MessageId)` deduplication in the same uninterrupted activation turn.
The action can stage outgoing messages using `Send`. The handler context and its
outbox view permit sending only during that attempt's synchronous action. Preparation
can build envelopes and inspect pending output. A preparation-time send violation
retains the original error and prevents the returned action from running, even when
the handler catches the rejection. An action-time violation stops capture and recovers
through a fresh activation. The registered outbox state prepares durable wakeup
prerequisites inside the serialized journal operation before capture.
Readiness is rechecked after asynchronous preparation, so late staged work joins a
capture only when its prerequisites are ready. Expected handler preparation failures
produce bounded retry or dead-letter accounting.
An accepted message whose handler is absent on a later activation completes immediately
into dead-letter storage. Its processed marker suppresses duplicates through the
configured deduplication window.

Acceptance and ownership repair retain local proposals until scheduling is acknowledged
before synchronously staging the complete envelope and ownership pair. Journaled
state callbacks encode the staged changes and acknowledge only the captured cohort.
An unexpected apply failure is latched before yielding; journal readiness raises the
original failure before capture. A journal failure permanently fences the activation,
signals pending
preparations and callbacks, and faults its waiters. A fresh activation replays the actual
durable outcome, including commits whose acknowledgement failed. A persistence-request
rejection after staging stops inbox admission and requests a fresh activation. The
manager can remain healthy after rejecting a request; a later admitted write observes
the inbox's latched failure before capture.
A delivery caller can cancel its wait while the owned operation retains admission
through completion. Activation shutdown drains that operation, and delivery failures
are logged and observed even after the caller has left.

Retained duplicates return `Duplicate`; expiry permits
acceptance again. Capacity limits return `Backpressured` before persistence.
`CanHandle` implementations are pure metadata predicates: the handler keeps grain
state and injected durable state unchanged until the action returned by `PrepareAsync`
runs. The selection context enforces access to metadata and grain identity; its outbound-message APIs
throw during selection. The inbox state rejects explicit write/delete requests
inside selection, preparation and apply, preserving the runtime's completion commit.
A route miss preserves the grain's staged state for its next journal write.
The grain owner quiesces delivery and pumping for the full journal deletion operation,
and resumes delivery after awaiting successful deletion. Journal deletion validates
completed delivery operations, released inbox gates, and idle pump leases. Once deletion starts, admission stays closed until the persisted
states reset. Recovery and deletion bookkeeping use the existing state streams.
Interleaved control calls observe that quiescence boundary; the
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

The builder encodes the body and request context into an envelope buffer which the
outbox reuses as a local pending intent with the owning grain as sender. Journal
codecs serialize envelopes and framework records during admitted application and
capture. `Count`, `Messages`, and `TryGetMessage` include local intents and
journaled messages once per ID. `Count` and depth metrics combine the journaled count
with the number of local intents awaiting finalization in constant time. Finalized
intents remain delivery-fenced until their exact capture is acknowledged. Repeated
equivalent enqueues preserve their original message, enqueue time, and commit status.
Direct envelopes require a nonempty message ID, an owning sender, a nondefault receiver,
and envelope data before intent admission.
Serialized null bodies remain valid; route selection supplies delivery and dead-letter
outcomes. Conflicting IDs fail; equivalence includes routing, timestamps, body and
context bytes, and declared type metadata.

The selected activation setup installs one messaging observer with explicit inbox and outbox
endpoints. After inbox handler preparation, outbox preparation seals the capture's
intent set and obtains scheduler acknowledgement for any required owner. This also
repairs an absent owner for already-nonempty recovered work before capture. Healthy
owners retain their exact handles. Synchronous finalization applies the complete
ownership generation, returned DurableJob, and prepared envelopes. Acknowledgement
releases exactly that capture's messages; later intents remain pending for the next
admitted operation.

Delivery computes outcomes locally across awaits. Its admitted operation validates
the physical owner, activation generation, and message eligibility before applying
message removal, retry, and dead-letter changes synchronously. Loopback calls use
the local inbox; remote batches yield between timer turns and retain the durable
attempt's cancellation lifetime. Callbacks coalesce by logical ownership and perform
idempotent terminal cleanup. Obsolete timer turns release their matching waiting
results and cancellation registrations; stale polls retire only that run's completed
result. Outbox stop, fault, and deletion clear only outbox result entries. Diagnostics
expose retained outbox dead letters and stage their removal for the next journal write.

A terminal journal fault stops outbox preparation and callbacks while preserving the
failed activation's journaled objects. An ownership-repair request veto before
admission requests grain deactivation, allowing a fresh activation to retry the
persisted work. Admitted failures retain the journal manager's terminal-fault path.
Fresh instances replay the actual durable outcome, including ambiguous append
acknowledgements. Canceling a caller's wait leaves an admitted write running through
capture and acknowledgement. Deletion
requires quiescent messaging operations and clears pending intents after success.

This intermediate project remains non-packable. Receiver tests compose the inbox
with existing Journaling and DurableJobs services and a journaled test outbox for
isolation. Bootstrap tests compose the real outbox and verify marker-selected and
legacy grains through atomic capture, fresh replay, remote delivery, and deduplication.
Outbox component tests construct the actual runtime through test-only reflection and
public interfaces. Public `AddDurableMessaging` hosting composition,
full-cluster sender/receiver integration, package publishing, and documentation-site
wiring are assembled in the final consumer layer.
