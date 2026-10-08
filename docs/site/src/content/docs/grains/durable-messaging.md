---
title: Durable messaging
description: Understand the durable inbox and outbox guarantees, recovery model, and operating limits.
ms.date: 10/08/2026
ms.topic: conceptual
---

# Durable messaging

The `Microsoft.Orleans.DurableMessaging` package provides a grain-scoped inbox and
outbox built on Orleans Journaling and Durable Jobs. It preserves application
message effects and outgoing messages across activation loss.

Use this page for the payload, commit, execution, and recovery model. Continue with:

- [Idempotency and hierarchical operation keys](durable-messaging-idempotency.md)
  for transport identities, durable business-outcome ledgers, and external effects.
- [Practical recipes](durable-messaging-recipes.md) for inventory reservation,
  payment-provider reconciliation, out-of-order projections, and notification fan-out.
- [Operations and throughput](durable-messaging-operations.md) for configuration,
  diagnostics, dead-letter replay, upgrades, and sequential messaging benchmarks.

## Message and routing model

Each <xref:Orleans.DurableMessaging.DurableEnvelope> has exactly four transport
members: `MessageId` (<xref:System.Guid>), `SenderId` and `ReceiverId`
(<xref:Orleans.Runtime.GrainId>), and required `Payload`
(<xref:Orleans.Serialization.Buffers.ImmutableBuffer>). The receiving grain verifies
that the receiver matches its own identity before deduplication or persistence.

Register one <xref:Orleans.DurableMessaging.IInboxHandler> with
<xref:Orleans.DurableMessaging.IDurableInbox.RegisterHandler*>. That handler decodes
and dispatches the application's message kinds. Requests and responses carry their
business-operation keys, response destinations, and versioning inside ordinary
application records. The transport only sees opaque bytes and the three identities.
A receiver without a registered handler returns
<xref:Orleans.DurableMessaging.DeliveryStatus.HandlerNotFound>; the result factory
<xref:Orleans.DurableMessaging.DeliveryResult.HandlerNotFound*> reports
`No inbox handler is registered.`. An unknown application kind instead fails during
application decoding/dispatch and follows handler retry/dead-letter policy.

### Encode ordinary application values

Use <xref:Orleans.Serialization.Serializer> or
<xref:Orleans.Serialization.Serializer`1> in an application-local codec. This helper
encodes concrete record types as the application's message discriminator, with
operation identity and optional response destination in the request:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_payload" language="csharp":::

`ImmutableBuffer` freezes raw managed bytes. Its
<xref:Orleans.Serialization.Buffers.ImmutableBuffer.Memory> is read-only;
<xref:Orleans.Serialization.Buffers.ImmutableBuffer.Length> is the raw byte count and
<xref:Orleans.Serialization.Buffers.ImmutableBuffer.Empty> represents zero bytes.
Copying constructors snapshot a borrowed
`ReadOnlySpan<byte>`, `ReadOnlySequence<byte>`, or
<xref:Orleans.Serialization.Buffers.ArcBuffer>. After copying an Arc input, immediately
dispose its owner. <xref:Orleans.Serialization.Buffers.ImmutableBuffer.Create*>
accepts an `Action<IBufferWriter<byte>>` and snapshots the callback's
written bytes once. Retaining the callback writer or mutating the source cannot
change the result. Consumers treat exposed read-only memory as immutable, including
when using memory interop APIs.

Managed immutable backing avoids retaining a minimum 16 KiB Arc page for every tiny
message. Outbox staging, RPC serialization/copying, and journal values can share
immutable references safely; persistence and networking still perform their own
encoding and I/O. Application record decoding remains explicit at the receiver.

When an existing encoder writes to an Arc writer, snapshot its raw bytes into the
immutable payload and release both writer and slice owners immediately:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_arc_snapshot" language="csharp":::

### Carry independently encoded items

<xref:Orleans.Serialization.Buffers.BufferPackage> is a reusable application value
for keyed raw entries. <xref:Orleans.Serialization.Buffers.BufferPackageBuilder>
accepts a raw span or a synchronous writer callback with
<xref:Orleans.Serialization.Buffers.BufferPackageBuilder.Add*>, then
<xref:Orleans.Serialization.Buffers.BufferPackageBuilder.Build*> freezes the package
and builder. <xref:Orleans.Serialization.Buffers.BufferPackage.Keys> inspects the
index; <xref:Orleans.Serialization.Buffers.BufferPackage.TryGetBytes*> returns an
entry's read-only bytes without decoding other entries.
<xref:Orleans.Serialization.Buffers.BufferPackage.Count> gives the entry count and
<xref:Orleans.Serialization.Buffers.BufferPackage.Buffer> exposes the frozen raw backing. Each key has an application-defined
encoding. Encode the package itself with an ordinary serializer when using it as an
envelope payload:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_buffer_package" language="csharp":::

## Commit and delivery guarantees

Durable Messaging has the following boundaries:

- Calling <xref:Orleans.DurableMessaging.IDurableOutbox.Send*> with an envelope
  synchronously stages an outgoing intent alongside complete, safe-to-commit business
  changes. Before the next journal capture, an outbox persistence hook confirms a
  durable self-wakeup and stages its ownership handle. Ordinary persistence captures
  the messages and ownership with the business effects.
- Outbox inspection includes staged and journaled messages once per ID. An ordinary
  journal write captures envelopes and wakeup ownership with business effects.
  Acknowledgement releases exactly that captured cohort for dispatch; intents staged
  during storage I/O await another write.
- Equivalent envelopes sharing a live `MessageId` coalesce while the original intent
  is staged or durable. Reusing that ID with a different sender, receiver, or payload
  throws before intent admission. Preserve the original envelope when retransmitting.
- Handlers use <xref:Orleans.DurableMessaging.IInboxHandler.HandleAsync*> for
  asynchronous local preparation followed by complete shared updates and
  <xref:Orleans.DurableMessaging.IInboxHandlerContext.Complete*>. From the first
  shared-state mutation through completion of the handler method, the handler
  performs its work without awaiting. Completion stages inbox removal and
  deduplication in that same uninterrupted turn as business changes and outgoing intents.
- Journal capture observes the combined, safe-to-commit staged effects. The manager
  persists them atomically, then acknowledges each state's captured changes.
- The owning grain or standalone host keeps messaging operations quiescent through
  deletion. Successful deletion resets durable messaging state and pending intents
  before that owner is disposed or deactivated. A fresh owner handles subsequent work.
- A receiver allocates an ownership token and places it in a scheduled inbox job
  before committing the envelope, token, and returned job handle together. It returns
  `Accepted` after that commit succeeds.
- Transport is **at-least-once**. A crash after receiver acceptance but before durable
  outbox removal can send the same envelope again.
- The receiver deduplicates by `(SenderId, MessageId)`. Duplicate deliveries converge
  on one set of handler effects while that deduplication record is retained. After
  <xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.DeduplicationWindow>
  expires, the same envelope can be accepted and processed again. The expired record and
  replay acceptance are committed atomically.
- Delivery is **unordered**. Inbox and outbox storage are dictionaries, and retries can
  reorder envelopes. Applications which require ordering must carry sequence numbers
  and make their handlers converge on application-defined order.

The inbox and outbox use independent Durable Jobs, allowing other grains' pumps to
progress while a handler is blocked. Durable Jobs assigns each scheduled wake-up its
own physical job ID and manages its storage, sharding, and silo failover. Durable
Messaging stores each logical ownership generation and its returned job handle together
in the grain journal, and carries the generation in job metadata. Retrying an ambiguous
scheduling response can create several physical jobs for one logical generation,
including jobs in different shards. The committed handle identifies the drain owner.
Callbacks for that job coalesce into one active logical pump; other physical jobs for
the generation complete once the committed owner is established.

Completed-generation tombstones let delayed duplicates terminate. A job which wakes
before its ownership commit or activation recovery is visible polls the same attempt.
After recovery, a scheduled generation with no committed owner and no work is a
confirmed orphan and completes, so Durable Jobs removes it. If recovered work has
neither an ownership generation nor a job handle, recovery schedules a replacement and
commits its generation and returned handle before the orphan terminates. Callbacks poll
until replacement ownership commits. A healthy recovered owner retains its exact
handle; Durable Jobs handles that job's shard and silo failover. A partial pair
or mismatched ownership metadata reports an invariant violation and blocks activation
and drain execution. Pump callbacks execute as non-interleaving grain timer turns,
keeping infrastructure writes and handler effects within their owning journal
boundaries.

Acknowledged outbox cohorts and completed remote delivery batches wake a local
pump immediately. Zero-due timer turns enter the normal activation message queue,
with the same non-interleaving and keep-alive policy. Local drains share the exact
physical ownership handle with job-driven drains. Activation-owned timer instances
coalesce ready work into non-interleaving turns; each logical arm keeps its
physical-owner, generation, and cancellation identity. Pending remote deliveries
release the pump turn; their actual
completion schedules collection of the retained outcomes. This keeps reciprocal
senders responsive while Durable Jobs owns recovery, scheduled retries, and
ownership retirement.

An empty outbox retains its acknowledged recovery handle for the configured idle
grace, allowing a new burst to reuse that handle. Eligible delivery accounting and
owner retirement share one acknowledged journal write. Work arriving during
prerequisite hooks or capture retains a valid wakeup and its own acknowledgement
boundary. The default grace is 100 milliseconds; zero selects immediate retirement.
See [Capacity, retries, and retention](durable-messaging-operations.md#capacity-retries-and-retention)
for scheduling and recovery-budget guidance.

## Handler preparation and terminal recovery

The single registered handler receives the envelope through
<xref:Orleans.DurableMessaging.IInboxHandlerContext.Envelope>. Its context exposes
only that envelope and `Complete()`. Inject
<xref:Orleans.DurableMessaging.IDurableOutbox> directly for outgoing messages.
Application dispatch, validation, authorization, and decoding run inside the handler.

<xref:Orleans.DurableMessaging.IInboxHandler.HandleAsync*> returns a
<xref:System.Threading.Tasks.ValueTask>. Perform fallible computation, asynchronous
I/O, validation and envelope serialization using operation-local values. Recheck
relevant preconditions and observe cancellation before the first shared mutation.
Then apply the complete business update, stage outgoing messages using
`outbox.Send(envelope)`, and call <xref:Orleans.DurableMessaging.IInboxHandlerContext.Complete*>.
From the first shared-state mutation until the handler method completes, perform
these operations without an intervening await, including after `Complete()`.
This is the handler's coding contract; ordinary journaled collections remain the
application's state interfaces. Handler continuations use the owning activation's
logical execution context when accessing shared state.

`Complete()` synchronously removes the current pending inbox message and stages
its completion/deduplication record. That record can be captured with the already
staged business changes and outgoing messages even if an earlier queued journal
writer runs before the inbox observes the method's return. After handling, the
runtime owns ordinary journal persistence and actual acknowledgement. Storage
acknowledgement establishes durability and releases the captured outgoing cohort
for dispatch.

The journal owner's final capture hook establishes a durable wakeup for outgoing
intents before capture. Applications construct and encode outgoing envelopes locally
before the first shared mutation, then call the directly injected outbox's synchronous
`Send(envelope)` in the final block. Earlier journal writes can complete during
asynchronous local preparation because proposed business effects are still local.

Every successful handler calls `Complete()`, including handlers which produce no
business or outgoing-message changes. A successful return which omits completion reports
a handler contract error and retires the owner. Context operations retain their actual
attempt, activation and resource lifetimes. Completion ends that attempt; the received
envelope remains available for inspection.

An expected failure before completion follows preparation retry/dead-letter policy
under the coding contract. A handler error after completion is reported after
owned persistence and cleanup; its already-staged completion and output are
preserved, so the runtime completes the logical operation rather than reapplying
its business effects. Actual storage failures retain terminal handling and their
original outcome.

Durable Messaging uses standard named journaled dictionaries and values. Journaling
supplies their capture, acknowledgement, replay and reset protocol. The outbox's
existing journaled sequence state associates captured message identities and wake-up
ownership with the storage acknowledgement, releasing exactly that cohort for dispatch.
The outbox's final <xref:Orleans.Journaling.IJournaledStateCaptureHook> establishes durable wake-up
ownership before capture.
Application and messaging code
complete their preconditions before applying synchronous changes, so the registered
<xref:Orleans.Journaling.IStateMachine> instances are ready for capture when the journal
write begins. Independent writes can commit earlier valid state while another operation
prepares local values.
Mutations made during storage I/O remain pending for the next capture.

A storage failure in an admitted write or delete fences the journal manager, faults its
operation waiters and requests grain deactivation. Messaging handles failed writes by completing
the affected operations and releasing their owned resources. Activation shutdown drains
messaging work; standalone owners coordinate component cleanup with manager disposal.
Already-captured cohorts retain their actual storage outcome and acknowledgement.
Genuine preparation I/O failures occur before shared mutations and follow retry/dead-letter
accounting, or a handler can catch them and prepare a safe alternative.
A fresh activation creates new state objects and replays the
actual durable outcome. A failed append response can follow a successful storage
commit: replay restores that committed envelope and exact ownership handle. A failed
ownership-clear write follows the same boundary; fresh replay determines whether the
previous owner remains responsible or cleanup was committed.

A failed journal prerequisite hook reports
<xref:Orleans.Journaling.JournaledStatePreCommitException>. Storage has not been
attempted, and complete safe-to-commit changes remain pending for explicit persistence
retry or owner retirement and fresh replay. An explicit persistence retry commits
the already-staged changes; fresh-owner recovery determines subsequent handler work.
A post-persistence hook failure reports
<xref:Orleans.Journaling.JournaledStatePostCommitException>; the captured cohort is
already acknowledged. Handle that post-persistence work using the committed feature
state. See [Persistence operation hooks](journaling/runtime-behavior.md#persistence-operation-hooks).

Cancellation of a caller's wait for
<xref:Orleans.Journaling.IJournaledStateManager.WriteStateAsync*> leaves an already
queued write running through capture and acknowledgement. The manager completes the
write according to the captured buffer's actual storage outcome. Messaging completes
its operations against that outcome and retains their resources through cleanup. A delivery
caller can also cancel its wait while the owned delivery operation retains admission
through completion. Activation shutdown drains that operation; late failures are
observed and logged even after the caller has left. The owner keeps delivery operations,
gates and pump leases quiescent by stopping and draining the inbox and outbox before
deleting the journal. It awaits the actual deletion task, then disposes or deactivates
the owner. A fresh owner handles subsequent messages. Durable attempts and timer
turns retain their own cancellation lifetimes: an outgoing remote batch keeps its
durable attempt token across timer turns.

Cancellation observed before the handler's first shared mutation leaves the committed
inbox message and physical owner available for a replacement attempt on the same
activation. Retirement drains owned runtime operations before completing the canceled
attempt. The handler observes cancellation before its final update. Once it begins that
update, it completes the business changes, outgoing staging, completion and method
return without awaiting. Attempt cancellation which arrives during this uninterrupted
block leaves completion and the subsequent owned write intact. The write and its
resources stay owned through the actual storage outcome.

## Backpressure, retries, and dead letters

The inbox rejects new, nonduplicate envelopes with `Backpressured` when it reaches
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.MaxCapacity>. The
sender retains and retries the envelope. Handlers report expected failures during
preparation, before shared application mutations. Retry accounting is applied with
inbox completion policy in the admitted write. Messages move to the appropriate inbox
or outbox dead-letter collection after their configured attempt or age limit. Use
<xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics> to inspect those records.
After an operator or application has handled a record, remove it with
<xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.RemoveInboxDeadLetter*>
or <xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.RemoveOutboxDeadLetter*>
so dead-letter storage remains bounded by the application's retention policy.
Removal is staged in the grain's journaled state and becomes durable with its next
journal write.

Malformed application payloads follow the retry and dead-letter path when the handler's
ordinary serializer or validation fails. Later envelopes remain available for recovery
and processing. Applications validate null results and unknown message kinds explicitly.

Processed-record maintenance begins at the earliest tracked expiry and amortizes
subsequent maintenance cycles to at most once per quarter of the deduplication window.
It joins admitted writes while the inbox is busy; durable pump maintenance and fresh
activation also remove due records. A maintenance-only write requires expired records.
Delivery evaluates each duplicate against its exact retention boundary.

## Deployment requirements

Configure Durable Jobs storage and Journaling storage, then enable Durable Messaging
with <xref:Orleans.Hosting.DurableMessagingExtensions.AddDurableMessaging*> on the
silo builder or service collection. The optional configuration callback supplies
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions>. Registration
preserves an application-supplied <xref:System.TimeProvider> and validates messaging
options through the options contract.

For development, configure in-memory storage and enable messaging:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_registration" language="csharp":::

Grains select messaging by implementing <xref:Orleans.DurableMessaging.IDurableMessagingGrain>
on a concrete class, an application base class, or an application grain interface.
Grains deriving from <xref:Orleans.Journaling.DurableGrain> receive the same setup
automatically. The capability enables ordinary grains to use their application's
inheritance model with scoped inbox and outbox services.

The following grain selects messaging through its application interface, constructs
an optional reply, then updates its notification count, sends the reply and completes
handling before returning. The inbox owns their journal write:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_grain" language="csharp":::

An ordinary grain method stages its sent count and outgoing envelope synchronously,
then persists both through the application-facing state manager. The outbox hook
confirms the wakeup before capture:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_send" language="csharp":::

Orleans caches messaging selection with each concrete grain type. After construction
and grain-instance assignment, shared activation setup validates the execution model,
resolves the activation's scoped endpoints and their registered journaled states.
Journal recovery then restores application and messaging state before activation completes.
With <xref:Orleans.Journaling.JournalingHostingExtensions.AddJournaling*>, the standard manager
enrolls in the grain lifecycle during grain-bound construction, before resolution returns.
An application-supplied manager establishes one enrollment owner in its constructor or
registration factory. A scoped factory assigning an explicit-<xref:Orleans.Journaling.JournalId>
manager to a grain lifecycle performs that enrollment before returning it. Standalone
managers retain caller-owned initialization and disposal.

A standalone owner can explicitly retry failed initial recovery by calling
<xref:Orleans.Journaling.IJournaledStateManager.InitializeAsync*> again. Each attempt
rebuilds state from the beginning of the journal. The host starts messaging after
initialization succeeds. An admitted write or delete failure follows the terminal
failure path and recovery uses a fresh owner.

Standard activation services resolve <xref:Orleans.Journaling.IDurableStateManager>
and <xref:Orleans.Journaling.IJournaledStateManager> to the same manager.
Grain code uses the former for named state access and commits; integrations use the
latter for state-machine registration, recovery, and full deletion.
<xref:Orleans.Journaling.IJournaledStateManagerFactory.CreateStandalone*> creates an
explicit journal owner whose state components and dependencies have caller-assigned
lifetimes. An ordinary grain selecting messaging can use that owner by registering
its states and enrolling the owner in the grain lifecycle.

Durable Messaging selects the built-in `orleans-binary`
journal format so opaque envelope payloads recover exactly.
Durable Messaging grains use non-reentrant execution. Activation validates the grain's
execution model and reports conflicting `Reentrant`, `MayInterleave`, `AlwaysInterleave`,
or `StatelessWorker` declarations. Resolved grain properties govern the reentrancy checks,
including properties supplied by custom attributes. Resolved placement strategies
identify stateless workers, including keyed placement aliases. A single non-interleaving
activation owns each grain journal and pump. Journaling captures and replays registered
<xref:Orleans.Journaling.IStateMachine> instances, acknowledges persisted changes, and
resets them after deletion. The owning grain or standalone host coordinates messaging
quiescence and cleanup through the complete deletion operation.
Journaling constructs the named durable collections in their owning scope using the
configured write format. Standalone hosts bind collection factories to the actual
advanced owner and retain their dependency scope until that owner is disposed.
Use shared, production-grade storage for multi-silo deployments. In-memory Durable Jobs
and journal storage support development and tests.

Capacity and retention settings bound storage growth and define the effectively-once
window. Monitor inbox depth, outbox depth, retry failures, dead letters, and oldest
pending-message age. Keep deduplication retention longer than the maximum expected
outbox retry age. The `orleans-durable-messaging-orphaned-jobs-reclaimed` counter
identifies terminal cleanup of schedule-before-commit crash remnants. Message outcome
counters group by grain type and status; pending and processed duplicates each record
one duplicate receipt. Sent-message counters and latency histograms group by grain type,
orphan metrics retain job name, and depth gauges report aggregate pending work. Envelope
identities remain available in message diagnostics; decode application operation keys in
authorized application diagnostics.