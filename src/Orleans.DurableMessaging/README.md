# Microsoft Orleans Durable Messaging

`Microsoft.Orleans.DurableMessaging` provides grain-scoped durable inboxes and
outboxes built on Orleans Journaling and Durable Jobs. Configure their storage for
the deployment, then call `AddDurableMessaging` on the silo builder. The
`IServiceCollection` overload registers the same messaging services. Grains implement
`IDurableMessagingGrain` or derive from `DurableGrain`, inject `IDurableInbox` to
register their single handler, and inject `IDurableOutbox` to enqueue envelopes.

`AddDurableMessaging` selects the built-in `orleans-binary` journal format from the
default JSON format and preserves an explicit binary configuration. Another
`JournaledStateManagerOptions.JournalFormatKey` produces an
`InvalidOperationException` when options are evaluated, identifying the configured
and required formats. Its optional `DurableInboxOptions` callback configures
capacity, batches, retries, deduplication, and dead-letter retention.

`DurableEnvelope` carries application-supplied `HierarchicalKey MessageId`, `SenderId`, `ReceiverId`,
ordinal `Subject`, and an owned `ArcBuffer` payload.
The exact message identity supplies receiver-local deduplication across senders and subjects.
Applications namespace independent commands and preserve each command's destination, subject, and
body across resubmissions. Exact-key completion affects that command independently of parents
and children. Admission validates up to 1,024 UTF-8 bytes/32 segments per canonical key and
256 UTF-8 bytes per nonempty subject. Applications define payload formats, dispatch, and replies.
Dispose each owned envelope;
`Retain()` acquires an independent payload lifetime. Serialization borrows its input, deserialization
transfers ownership to its result, and deep copying acquires an independent retained slice.

The protocol and runtime provide:

- `DurableEnvelope` is a disposable readonly struct with `MessageId` (`HierarchicalKey`),
  `SenderId` and `ReceiverId` (`GrainId`), required ordinal `Subject`, and `Payload` (`ArcBuffer` from
  `Orleans.Serialization.Buffers`, field ID 3). Payload bytes are opaque to transport
  and treated as read-only. `ArcBuffer.Empty` is a valid owner-free empty payload.
- The caller owns each constructed slice/envelope. `DurableEnvelope.Retain()` creates
  an independent payload pin; struct copies borrow the same pin. Dispose each owned
  envelope exactly once, after staging or actual direct-send completion.
- `IDurableOutbox.Send` borrows the envelope; durable dictionary state independently
  retains it. `IInboxHandlerContext.Envelope` is borrowed until the actual handler
  method ends, including asynchronous preparation. Do not dispose a borrowed context
  payload. Retain explicitly to store it longer.
- Generated `IDurableInboxExtension` request copying retains its own payload pin.
  The extension owns and disposes that request clone on every path, including rejection,
  cancellation, and exceptions. Ordinary persistence/network serialization borrows
  payloads without consuming them. Retained operation owners remain alive through the
  actual operation, not just a caller's canceled wait.
- Application encoders use one reusable `ArcBufferWriter` per non-reentrant activation
  or serialized application scope. Ordinary serializers write records into it;
  `ConsumeSlice` returns owned disjoint payload slices which can share pages. Dispose
  the encoder through grain `IDisposable` or a DI-owned scoped service at teardown.
  Arc serializer overloads use `Reader.Create(ArcBuffer, session)`, permitting codecs
  to retain raw sub-slices during decoding without copying.
- `BufferPackage` is disposable, owning one Arc buffer and a read-only ordinal index
  of key offsets and lengths. Its `Buffer` and `TryGetBytes` sequence views are borrowed
  while the package owner remains live. `Retain()` returns an independent owner.
  `BufferPackageBuilder` owns a disposable Arc writer; `Add(key, span)` or a writer
  callback encodes entries, and `Build` transfers the buffer owner to the package.
  Serialize packages normally and release them after use, including decoded packages.
- `DurableMessageType<T>` binds an exact subject to ordinary `Serializer<T>` and verifies
  the subject before decoding. `AddDurableMessageType<T>` registers a keyed singleton binding.
  The scoped `DurableMessageWriter` prepares an owned envelope before shared mutation.
  `DurableInboxDispatcher` optionally selects typed delegates by exact subject.
  Application records carry request/response destinations and business data.
- `IDurableInbox`, `IDurableOutbox`, and `IDurableInboxExtension` define handler
  registration, inspection, enqueue, and delivery operations. `DeliveryResult` and
  `DeliveryStatus` describe delivery outcomes. An absent registered handler returns
  `HandlerNotFound` (numeric value 3); `DeliveryResult.HandlerNotFound()` reports
  `No inbox handler is registered.`.
- `IDurableOutbox.Send(envelope)` synchronously stages an outgoing envelope. The final
  journal capture hook establishes its durable self-wakeup before capture; dispatch
  follows acknowledgement of the exact captured messages and physical owner pair.
- `IInboxHandler` has only `ValueTask HandleAsync(IInboxHandlerContext,
  CancellationToken)`. The inbox has a single `RegisterHandler(handler)` registration.
  Use one application dispatcher for multiple message kinds. Its context exposes only
  `Envelope` and `Complete()`; inject `IDurableOutbox` directly to stage messages.
- `DurableInboxOptions` supplies defaults and validates capacity, retry, retention,
  and batch limits, including an outbox retry age shorter than the deduplication window.

`HierarchicalKey` is a readonly ordinal value with one immutable canonical backing path and a
cached process-local hash. `Create` and `CreateChildKey` accept literal segments; `Parse` reads an
escaped canonical path. `Append` composes built hierarchies. `default` is an unset identity.
Serialization stores canonical paths and reconstructs hash/navigation state. Shared journal value
lifecycles retain and release pending-message and dead-letter payloads at actual ownership boundaries.
Owned RPC arguments remain retained through their actual serialization and invocation outcomes.

## Handler and persistence boundaries

Handlers perform asynchronous I/O, validation, envelope construction, and cancellation
checks using local values before the first shared business or journaled mutation. Prepare
owned replies with `using var reply = ...`; disposal releases local pins after staging,
including on exceptions, while durable dictionary state retains its own pins. From
that first shared mutation through method completion, execute synchronously with no
awaits. Apply complete safe-to-commit changes, stage outgoing envelopes, call
`context.Complete()`, and return without further awaits. This mutation boundary is the
handler implementation's trusted responsibility.

`Complete()` synchronously stages inbox completion and deduplication in that same turn.
The runtime awaits `HandleAsync`'s outcome and then owns actual journal persistence,
acknowledgement, and resource cleanup. A valid final block completes even if attempt
cancellation arrives after shared updates have begun; check cancellation beforehand.
Repeated completion in the same still-active completed attempt coalesces after attempt
identity validation. Wrong-attempt and retired completion calls are rejected. The
handler stages all outgoing work before completion; the envelope remains inspectable.

Ordinary application methods prepare and encode outgoing envelopes locally, apply
business updates, call `outbox.Send(envelope)`, and await their usual journal write.
Compiled notification, inventory, payment, projection, fan-out, and dispatcher examples
live in the [documentation snippets](../../docs/site/src/content/docs/snippets/compiled/Grains/).
Handlers instead call `context.Complete()` after staging the final shared update and
return without awaiting; the runtime owns their write and acknowledgement.

The outbox registers the owner's single `IJournaledStateCaptureHook`. Ordinary before
callbacks run first; the work loop then directly awaits this final prerequisite and
synchronously captures state. The outbox confirms a viable durable self-wakeup for all
staged intents, including messages arriving during ordinary hooks or its own scheduling.
Scheduling uses the owned journal-operation and feature-shutdown lifetimes. Caller
cancellation ends only the caller wait. A `JournaledStatePreCommitException` reports a
failed prerequisite before capture: ordinary callers can restore the prerequisite and
explicitly retry the write with their business changes and message intents still
pending. Feature-owned operations retire their owner after a prerequisite failure and
recover from the durable outcome. A `JournaledStatePostCommitException` reports failed
post-persistence work after actual acknowledgement; business effects and messaging
acknowledgements remain committed.

External dispatch starts after the corresponding captured intents and owner pair are
acknowledged. Messages staged during a storage await belong to a later capture. The hook
retains actual scheduling ownership through the write outcome, independently of the gate
retained by feature-owned writes through acknowledgement. Owner retirement and
subsequent sends preserve exact physical job identity and generation boundaries.

Equivalent enqueues with a live `MessageId` coalesce across staged and durable
intents. The ID binds to its original sender, receiver, subject, and raw payload bytes.
Conflicting pending content fails before admission. Preserve the original command
identity for retransmission and application resubmission. The receiving inbox compares
pending subject/body bytes while permitting a changed immediate sender.

Completion records supply command deduplication during their configured retained lifetime.
A completed duplicate acknowledges the existing outcome; business results remain in
application state or the original reply intent. After retention expiry the same identity
can be accepted again. Retain completion records for the supported resubmission horizon.
Use deterministic child identities for distinct workflow steps and replies.

`IDurableMessagingGrain` is a local capability which selects durable messaging activation
setup. Implement it on a grain class, an application base class, or an application grain
interface. Existing `DurableGrain` implementations receive the same setup automatically.
Selection is cached with the concrete grain type, and each activation reuses its scoped
inbox, outbox, and registered journaled messaging states.

Setup validates the grain's execution model after the runtime assigns the constructed
grain instance and before lifecycle startup, journal initialization, or replay.
Supported activations use a single, noninterleaving grain execution model. Validation
uses the resolved grain properties which configure runtime interleaving and the
runtime's resolved placement strategy, including custom metadata and keyed placement
aliases. Grain construction and local state registration precede validation. The
standard state manager enrolls in the grain lifecycle during grain-bound construction.
Standard `IDurableStateManager` and `IJournaledStateManager` services alias that same
scoped manager. Application code uses the typed named-state API and ordinary writes;
messaging uses the journal owner for state-machine registration and persistence. Shared
setup resolves the complete messaging graph before recovery closes registration.

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
for pending work. Callbacks validate generation and physical job identity before
processing. Delivery requires a nonempty message ID, a nondefault sender, and a nonnull
valid Arc payload before duplicate lookup or admission. Empty raw payloads are valid;
application decoding and null validation belong to the handler. Empty-owner clearing
shares the inbox admission gate with delivery, so direct interleaved delivery proceeds
after the clear's durable outcome.

The handler context belongs to the current active attempt. It exposes the received
envelope and attempt-scoped completion. The directly injected outbox retains owning
activation and lifetime checks for outgoing staging. `Complete()` stages inbox
completion synchronously and marks the logical outcome for that attempt. A successful
method return without completion reports an explicit contract error. Method errors after
completion retain the staged logical outcome through actual persistence and cleanup,
then surface the original error.

Before `Complete()`, ordinary handler errors follow bounded retry/dead-letter accounting
under the trusted local-preparation contract. Attempt cancellation retains the committed
inbox and owner for another attempt on the same activation. The runtime owns admitted
persistence operations through their actual outcomes.

After `Complete()`, a handler exception is logged and retained while the ordinary
owned write persists the completed logical outcome. The exception is reported after
acknowledgement and cleanup. Subsequent wakeups observe completion and deduplication.
Actual persistence failure remains authoritative and terminal; any earlier handler
exception remains recorded. An admitted write continues independently of attempt
cancellation, and completion of the valid synchronous final block is preserved.

Asynchronous application I/O and decoding precede shared mutations. Independent
journal writes can persist earlier valid state during local preparation. The final
synchronous block joins business effects, outgoing intents, and inbox deduplication
through `Complete()`. Unknown application message kinds and malformed payloads fail
inside the handler and follow processing retry/dead-letter policy.
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
successful staging. Before-completion handler failures use the documented preparation
outcome, and completed handler failures retain actual persistence before surfacing.
Both paths retain their owned runtime operations through the actual outcome. Genuine journal failures
remain subject to the manager's internal failure fence and waiter completion; the
inbox catches its failed write and stops local processing with the original observed
cause. Operation lifetime tracking keeps shutdown waiting for actual writes and
operation retirement. A fresh activation replays
the actual durable outcome, including commits whose acknowledgement failed.
A delivery caller can cancel its wait while the owned operation retains admission
through completion. Activation shutdown drains that operation, and delivery failures
are logged and observed even after the caller has left.
Shutdown logs application cancellation-callback failures and completes inbox pump,
result-registration, metric, and cancellation-source cleanup. An existing terminal
failure remains the cause reported to operation waiters.

Retained duplicates return `Duplicate`; expiry permits
acceptance again. Capacity limits return `Backpressured` before persistence.
`HandleAsync` prepares local values, then stages complete business changes, outgoing
messages through the directly injected outbox, and completion without awaiting from
the first shared mutation through method return. Missing handler registration is a
transport admission outcome; application message-kind dispatch is handler code.
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

Single handler registration retains the original instance. Operational diagnostics
expose retained dead letters and stage their removal for the next journal write.
Application-authorized diagnostics inspect message identities and decode payload records.

Synchronous `Send(envelope)` validates and stages one message for the next journal
capture. Standard dictionaries encode commands during staging; standard values encode
changed values at capture. `Count`, `Messages`, and `TryGetMessage` include staged and
acknowledged messages once per ID. Depth accounting uses dictionary counts in constant
time. Intents remain delivery-fenced until their exact captured cohort is acknowledged.
Equivalent enqueues preserve their original enqueue time and commit status. Direct
envelopes require a nonempty message ID, owning sender, nondefault receiver, and nonnull
valid Arc payload before admission. Raw empty bytes are valid; the application decides
what payload encoding and content are meaningful.

The outbox uses six standard durable collections resolved by their existing keyed
names from the owning activation or standalone dependency scope. They register with
that scope's actual advanced owner and use its selected format configuration. The
seventh state retains the existing job-sequence value and associates outbox messages
with capture and acknowledgement. Its construction owner supplies the exact long-value
codec for the selected format. All seven stream names remain stable; verify envelope and journal schema
compatibility independently during upgrades.
Grain-facing `IDurableStateManager` writes share that same owner and acknowledgement
boundary.
Standalone fixtures use `CreateStandalone`, explicitly register their state machines,
and retain caller ownership of initialization, dependencies, and disposal.
The final capture hook establishes wakeup ownership for synchronous sends.
Healthy owners retain
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
completion of all delivery attempts. Shutdown drains in-flight delivery, scheduling,
and outbox-owned writes; retired operations release resources once, preserving the original failure.
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
uncommitted intents. Reset clears their state and rotates the generation while
preserving the stopped lifetime. Old contexts remain unusable; subsequent work uses a
fresh owner.

A callback defers while scheduling or a provisional commit is unresolved. A wakeup
with no corresponding durable work retires harmlessly after recovery confirms the
outcome. Caller cancellation ends only its wait; actual scheduling and persistence
retain resources through their outcome. Activation shutdown drains owned operations.
After successful manager recovery, feature startup schedules repair for wholly absent
ownership pairs before an ordinary ownership write. Recovery callbacks refresh the
feature's cached state; malformed pairs report their existing explicit error at startup.
Scheduling errors before staging leave application and journaled state unchanged.

Handlers and ordinary application methods finish fallible preparation before applying
complete safe-to-commit business changes and outgoing sends in one synchronous turn.
Already-captured cohorts acknowledge only their own snapshot; mutations staged during
the storage await remain pending for their own acknowledgement.

Message outcome counters group by grain type and delivery or processing status. Each
successful duplicate delivery records one received duplicate outcome, whether the
message is pending in the inbox or retained as processed. The sent-message counter and
latency histograms group by grain type. Orphaned-job metrics retain the job name, and
depth gauges report aggregate pending work. Envelope identities remain available in
message diagnostics. Log decoded application keys only through authorized diagnostics,
keeping high-cardinality keys out of metrics.

Transport is at-least-once and unordered. Retained deduplication records provide
effectively-once handler effects. Applications which require ordering carry
sequence numbers and converge on application-defined order. A single
non-interleaving activation owns each grain journal and its pumps. The journaled
state manager captures safe-to-commit state and fences admitted persistence failures.

Use shared, production-grade Journaling and Durable Jobs storage for multi-silo
deployments. In-memory storage supports development and tests. Inbox and outbox
dead letters are retained for 30 days by default, with up to 1,000 records in each
collection. Configure `DeadLetterRetentionPeriod` and `MaxRetainedDeadLetters` for
the application's operational retention policy. Expired and excess records are
compacted when dead letters are added and when an activation starts.

See the [durable messaging
guide](https://dotnet.github.io/orleans/docs/grains/durable-messaging/) and [hosting
API](https://dotnet.github.io/orleans/docs/api/csharp/microsoft.orleans.durablemessaging/orleans.hosting.durablemessagingextensions/)
for configuration and operating guarantees.
