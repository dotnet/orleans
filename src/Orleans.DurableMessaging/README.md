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

Handlers consume preparation results and handle or propagate failures before returning
their action. Retrieving a failed result counts as consumption even when it throws;
status inspection leaves it unconsumed. The runtime rejects unfinished preparations
and failed or canceled returned completions which remain unconsumed. Its raw-task
cleanup retains acquisition ownership independently of that caller-consumption check.
Task conversion such as `AsTask()` retrieves the value-task result on behalf of a
caller-owned task. The caller awaits or handles that task before returning the action;
the runtime tracks consumption of the returned value task, and application code owns
the converted task's outcome.

`IDurableMessagingGrain` is a local capability which selects durable messaging activation
setup. Implement it on a grain class, an application base class, or an application grain
interface. Existing `DurableGrain` implementations receive the same setup automatically.
Selection is cached with the concrete grain type, and each activation reuses its scoped
inbox, outbox, and registered journaled messaging states.

Setup validates the grain's execution model after the runtime assigns the constructed
grain instance and before lifecycle startup, journal initialization, or replay. Supported
activations use a single, noninterleaving grain execution model. Validation uses
the resolved grain properties which configure runtime interleaving and the runtime's
resolved placement strategy, including custom metadata and keyed placement aliases.
Grain construction and local state registration precede validation. The standard state manager enrolls in the
grain lifecycle during grain-bound construction. Standard `IDurableStateManager`
and `IJournaledStateManager` services alias that same scoped manager. Application
code uses the typed named-state API and ordinary writes; messaging uses the journal
owner for state-machine registration and persistence. Shared setup resolves the
complete messaging graph before recovery closes registration.

The inbox uses eight canonical standard `IDurableDictionary` and `IDurableValue`
states under the existing stream names. Keyed Journaling registrations bind them
to the actual advanced owner, selected write format and activation or explicit
standalone dependency scope before initialization. Standard dictionaries encode
commands as they change; standard values encode dirty values at capture. The
journal manager owns atomic persistence and the captured buffer's lifetime.

The inbox completes asynchronous preparation and feature preconditions before
synchronous safe-to-commit updates. Independent writes can persist previously staged
valid state while another operation prepares local values. Each captured cohort
retains its own acknowledgement. Capture, replay, reset and acknowledgement remain
the journal state protocol.

`IJournaledStateManagerFactory.CreateStandalone` creates an owner for an explicit
`JournalId`. Its caller constructs and registers the state machines before
initialization and owns their dependency lifetimes. Initialization and disposal
remain caller-owned; a grain factory deliberately enrolls such an owner in the
lifecycle when integrating it with activation startup. Full journal deletion uses
the advanced owner and the existing quiescence boundary. A manually composed owner
initializes the manager before starting the inbox and outbox lifecycle. Inbox
startup then initializes its recovered ownership and retention caches.

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

The handler context's outbox permits preparation during that attempt's `PrepareAsync`
call and sending its own batches during the returned action. Every call checks the
current attempt and phase before applying repeated-send semantics. A caught or replaced
scope violation retains its original cause and prevents a successful completion commit.
An action-time failure ends the inbox operation and requests a fresh activation.

Preparation keeps business state, outgoing intents, and inbox completion unchanged.
Independent journal writes can persist previously staged changes while preparation
awaits. The runtime revalidates inbox ownership before applying business effects,
staging prepared output, and recording `(SenderId, MessageId)` deduplication in one
synchronous turn. Scheduling failures during preparation follow the ordinary bounded
retry/dead-letter policy; handlers can catch them and prepare a safe alternative outcome.
Each started acquisition remains owned through its actual result. Attempt cleanup
drains outstanding acquisitions and disposes unused results, including preparations
which user code failed to await. Successfully acquired handles remain owned through
the attempt's persistence outcome.
An accepted message whose handler is absent on a later activation completes immediately
into dead-letter storage. Its processed marker suppresses duplicates through the
configured deduplication window.

Acceptance and ownership repair retain local proposals until scheduling is acknowledged
before synchronously staging the complete envelope and ownership pair. The inbox
awaits the ordinary write which follows that synchronous staging, then acknowledges
only the immutable acceptance or ownership facts of that operation. Ownership-changing
operations retain the inbox gate through this acknowledgement.
Application code completes fallible checks before applying shared changes, so every
staged mutation is safe to commit. Inbox processing requests persistence after
successful staging. A failed preparation or apply operation surfaces directly and
releases owned preparation resources. Genuine journal failures
remain subject to the manager's internal failure fence and waiter completion; the
inbox catches its failed write and stops local processing with the original observed
cause. Operation lifetime tracking keeps shutdown waiting for actual writes and
preparation retirement. A fresh activation replays
the actual durable outcome, including commits whose acknowledgement failed.
A delivery caller can cancel its wait while the owned operation retains admission
through completion. Activation shutdown drains that operation, and delivery failures
are logged and observed even after the caller has left.
Shutdown logs application cancellation-callback failures and completes inbox pump,
result-registration, metric, and cancellation-source cleanup. An existing terminal
failure remains the cause reported to operation waiters.

Retained duplicates return `Duplicate`; expiry permits
acceptance again. Capacity limits return `Backpressured` before persistence.
`CanHandle` implementations are pure metadata predicates: the handler keeps grain
state and injected durable state unchanged until the action returned by `PrepareAsync`
runs. The selection context enforces access to metadata and grain identity; its outbound-message APIs
throw during selection. The returned synchronous action stages prepared business
changes and outgoing messages, and the inbox adds completion before its ordinary write.
A route miss preserves the grain's staged state for its next journal write.
For full journal deletion, the owner stops and drains its inbox and outbox through
their existing lifecycle, awaits the advanced owner's actual `DeleteStateAsync`
operation, then disposes or deactivates that owner. Caller wait cancellation leaves
this owned workflow running. Stopped admission remains closed through reset and
deletion; subsequent work uses a fresh owner. Reset removes committed and staged
state using the existing streams. Stop clears pump/results and metrics; the fresh
owner initializes new local ownership and acknowledgement state.
Interleaved control calls observe that quiescence boundary; the
active handler retains its own attempt-scoped messaging guards.
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
outbox reuses with the owning grain as sender. `PrepareSendAsync` snapshots and validates
the complete batch, reserves its message identities, and confirms a durable self-wakeup.
The returned activation-local batch retains those prerequisites. Preparation leaves
journaled state and visible message depth unchanged. `Send(batch)` synchronously stages
the prepared intents alongside application changes before ordinary persistence. Journal
standard dictionaries encode their commands during synchronous staging; standard values
encode changed values during capture. `Count`, `Messages`,
and `TryGetMessage` include staged and acknowledged messages once per ID. `Count`
and depth metrics use the dictionary count in constant time. Staged intents remain
delivery-fenced until their exact captured cohort is acknowledged. Repeated
equivalent enqueues preserve their original message, enqueue time, and commit status.
Direct envelopes require a nonempty message ID, an owning sender, a nondefault receiver,
and envelope data before intent admission.
Serialized null bodies remain valid; route selection supplies delivery and dead-letter
outcomes. Conflicting IDs fail; equivalence includes routing, timestamps, body and
context bytes, and declared type metadata.

The outbox uses six standard durable collections resolved by their existing keyed
names from the owning activation or standalone dependency scope. They register with
that scope's actual advanced owner and use its selected format configuration. The
seventh state retains the existing job-sequence value and associates outbox messages
with capture and acknowledgement. Its construction owner supplies the exact long-value
codec for the selected format. All seven stream names and state encodings remain stable.
Grain-facing `IDurableStateManager` writes share that same owner and acknowledgement
boundary.
Standalone fixtures use `CreateStandalone`, explicitly register their state machines,
and retain caller ownership of initialization, dependencies, and disposal.
Feature preparation serializes wakeup acquisition and shares the viable exact owner
between live batches. It releases the acquisition gate before returning each handle,
so callers can prepare several batches before applying them. Healthy owners retain
their exact handles. Feature operations complete ownership, generation and message
preconditions before applying their prepared commands synchronously. Ordinary journal
writes persist that safe-to-commit state. The sequence state's capture callback seals
the pending message identities and exact owner snapshot. Its acknowledgement follows
the journal's atomic storage write and releases only that cohort's delivery fences;
messages staged during the storage await remain pending for the next write. This
association also covers ordinary application writes and is independent of registration
order. Outbox-owned operations complete by awaiting their ordinary manager writes,
including successful zero-byte writes.

Delivery computes outcomes locally across awaits. It validates the physical owner,
activation generation, and message eligibility before synchronously staging message
removal, retry, and dead-letter changes and requesting their ordinary journal write.
Loopback calls use the local inbox; remote batches yield between timer turns and retain the durable
attempt's cancellation lifetime. Callbacks coalesce by logical ownership and perform
idempotent terminal cleanup. Obsolete timer turns release their matching waiting
results and cancellation registrations; stale polls retire only that run's completed
result. Outbox stop, fault, and deletion clear only outbox result entries. Batch
cancellation logs callback failures and retains its token source through the actual
completion of all delivery attempts. Shutdown drains in-flight delivery, preparation
and outbox-owned writes; retired batches release their resources once, preserving the original operation failure.
Diagnostics expose retained outbox dead letters and stage their removal for the next journal write.

An outbox-owned write failure preserves the first observed error and stops local work;
operation waiters observe their actual manager-write outcomes directly. An unexpected
standard-collection staging failure stops the feature and requests deactivation while
surfacing that error before an outbox write is requested. The journal manager
owns persistence fencing and grain deactivation. Ordinary callers observe their own
write failures; a standalone owner also stops and drains its feature instances when
its operation fails or its lifetime ends. Fresh instances replay the actual durable
outcome, including ambiguous append acknowledgements.

The owning grain or standalone host stops and drains both messaging features before
awaiting the advanced journal owner's actual deletion. It then disposes or deactivates
the owner; subsequent use starts with a fresh owner. Canceling a caller's wait leaves
this cleanup workflow responsible for its actual outcome. Deletion may discard stopped,
uncommitted intents and prepared handles. Reset clears their state and rotates the
generation while preserving the stopped lifetime. Old handles remain unusable, and
later disposal leaves a fresh owner's state intact.

Ordinary callers await each preparation and dispose the returned batch after the
staging/write scope. The handler facade owns batches through attempt completion,
including late preparation outcomes. Disposing an unused handle releases only its
reservation; disposing a staged handle leaves its cohort owned through the actual ACK.
Equivalent overlapping batches share intent identity, including a completed delivery
while another live batch still references it. Repeated `Send` of a live staged batch
is idempotent within its valid scope. Every call validates owner, epoch and lifetime.

A callback defers while scheduling or a provisional commit is unresolved. Once unused
preparation is released, a wakeup with no corresponding durable work retires harmlessly.
Caller cancellation ends its wait while scheduling retains resources through the actual
outcome, including releasing an unclaimed result. Activation shutdown drains owned
acquisition, preserves callback-error cleanup, and invalidates remaining handles.
After successful manager recovery, feature startup schedules repair for wholly absent
ownership pairs before an ordinary ownership write. Recovery callbacks refresh the
feature's cached state; malformed pairs report their existing explicit error at startup.
Scheduling errors before staging leave application and journaled state unchanged.

Handlers and ordinary application methods finish fallible preparation before applying
complete safe-to-commit business changes and prepared sends in one synchronous turn.
Already-captured cohorts acknowledge only their own snapshot; mutations staged during
the storage await remain pending for their own acknowledgement.

This intermediate project remains non-packable. Receiver tests compose the inbox
with existing Journaling and DurableJobs services and a journaled test outbox for
isolation. Bootstrap tests compose the real outbox and verify marker-selected and
legacy grains through atomic capture, fresh replay, remote delivery, and deduplication.
Outbox component tests construct the actual runtime through test-only reflection and
public interfaces. Public `AddDurableMessaging` hosting composition,
full-cluster sender/receiver integration, package publishing, and documentation-site
wiring are assembled in the final consumer layer.

Message outcome counters group by grain type and delivery or processing status.
Each successful duplicate delivery records one received duplicate outcome, whether
the message is pending in the inbox or retained as processed.
The sent-message counter and latency histograms group by grain type. Orphaned-job
metrics retain the job name, and depth gauges report aggregate pending work. Route
keys continue to select handlers and remain available in message diagnostics.
