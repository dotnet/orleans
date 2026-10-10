---
title: Durable messaging
description: Understand the durable inbox and outbox guarantees, recovery model, and operating limits.
ms.date: 10/09/2026
ms.topic: conceptual
---

# Durable messaging

The `Microsoft.Orleans.DurableMessaging` package provides a grain-scoped inbox and
outbox built on Orleans Journaling and Durable Jobs. It preserves application
message effects and outgoing messages across activation loss.

Use this page for the payload, commit, execution, and recovery model. Continue with:

- [Idempotency and hierarchical operation keys](durable-messaging-idempotency.md)
  for application command identities, retained completion facts, and external effects.
- [Practical recipes](durable-messaging-recipes.md) for inventory reservation,
  payment-provider reconciliation, out-of-order projections, and notification fan-out.
- [Operations and throughput](durable-messaging-operations.md) for configuration,
  diagnostics, dead-letter replay, upgrades, and sequential messaging benchmarks.

## Message and routing model

Each <xref:Orleans.DurableMessaging.DurableEnvelope> carries an application-supplied
`MessageId` (<xref:Orleans.DurableMessaging.HierarchicalKey>), `SenderId` and `ReceiverId`
(<xref:Orleans.Runtime.GrainId>), an exact ordinal `Subject`, and an owning `Payload`
(<xref:Orleans.Serialization.Buffers.ArcBuffer>). The receiving grain verifies its
destination before deduplication or persistence. Admission requires a nondefault
command ID of at most 1,024 UTF-8 bytes and 32 segments and a nonempty subject of at
most 256 UTF-8 bytes. Both send and admission validate these bounds before durable mutation.

Register one <xref:Orleans.DurableMessaging.IInboxHandler> with
<xref:Orleans.DurableMessaging.IDurableInbox.RegisterHandler*>. That handler decodes
and dispatches the application's subjects. The envelope's ID identifies the logical
command; ordinary application records carry response destinations and domain versions.
The runtime transports the subject and opaque body bytes. Retries and reconstructed
submissions preserve the same ID, subject, destination, and body.
A receiver without a registered handler returns
<xref:Orleans.DurableMessaging.DeliveryStatus.HandlerNotFound>; the result factory
<xref:Orleans.DurableMessaging.DeliveryResult.HandlerNotFound*> reports
`No inbox handler is registered.`. An unknown application kind instead fails during
application subject lookup or decoding and follows handler retry/dead-letter policy.

### Encode ordinary application values

Register <xref:Orleans.DurableMessaging.DurableMessageType`1> using
<xref:Orleans.Hosting.DurableMessageTypeExtensions.AddDurableMessageType*>. Each keyed
singleton binds one explicit subject to the ordinary
<xref:Orleans.Serialization.Serializer`1>. Inject it using `FromKeyedServices` and
decode with <xref:Orleans.DurableMessaging.DurableMessageType`1.Decode*>; the binding
verifies the envelope's subject before reading the borrowed payload.

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_payload" language="csharp":::

Use the typed outbox `Send` extension with a binding, stable command ID, destination,
and body. The destination can be a <xref:Orleans.Runtime.GrainId> or an
<xref:Orleans.Runtime.IAddressable> grain reference. The helper encodes the body,
assigns <xref:Orleans.DurableMessaging.IDurableOutbox.SenderId> as sender, stages
the envelope, and releases its temporary owner after the outbox retains its pin.

The typed `SendReply` extension takes the binding, inbox context, explicit
destination, and result body. It derives the reply ID by appending the literal
`result` segment to the received command ID. The application supplies the reply
destination, for example from its request body. Compute fallible business results
locally, call typed send/reply before business mutation, then apply the prepared
state and call `Complete()` synchronously. Encoding finishes before the helper's
first outbox mutation; serialization errors propagate with no outgoing intent staged.

For explicit admission or bulk preparation,
<xref:Orleans.DurableMessaging.DurableMessageType`1.Create*> accepts the command ID,
sender ID, destination ID, and body and returns an owning envelope. Use `using` or
`finally` for these explicitly created owners. The typed outbox helpers manage
their envelopes internally.

Subjects use exact ordinal spelling, for example `inventory.reserve.v1`. Register
each subject once; separate subjects can bind the same CLR type. Define supported
polymorphism in the ordinary serialization contract when a subject uses a base
type. Use <xref:Orleans.DurableMessaging.DurableInboxExtensions.RegisterHandlers*>
to register typed methods for one or several subjects. It installs one
<xref:Orleans.DurableMessaging.DurableInboxDispatcher>, freezes the configured routes,
and decodes each body with its subject's binding before invoking the selected method.
Configuration requires at least one route and rejects duplicate subjects before
installing the handler. The dispatcher returns the actual handler outcome;
each successful handler explicitly calls `Complete()` and returns synchronously
after shared mutation. See [Inventory dispatch](durable-messaging-recipes.md#reserve-inventory-once-per-order-line)
and [Typed dispatch](durable-messaging-recipes.md#combine-the-recipes-into-an-order-workflow).

`Register` accepts synchronous actions or asynchronous functions returning
`ValueTask`. Pass a state argument with an
`Action<T, TArg, IInboxHandlerContext>` or
`Func<T, TArg, IInboxHandlerContext, CancellationToken, ValueTask>` to use static
delegates. The dispatcher stores typed handler objects with that state and invokes
their concrete generic serializer directly, supporting AOT compilation. Simple
delegate overloads are also available.

Register handlers in <xref:Orleans.Grain.OnActivateAsync*>. Journal recovery
restores durable state before this method runs, and queued inbox pump turns and
incoming requests begin after it completes. Initialize named durable state during
construction so it participates in recovery.

Synchronous dispatch checks the attempt token after decoding and before entering
the method. These short synchronous methods proceed through explicit `Complete()`
and return in the same turn. Asynchronous preparation receives the token and checks
it after awaited work and before outgoing staging or business mutation.

### Own and borrow payload slices

An envelope is a disposable readonly struct containing an owned
<xref:Orleans.Serialization.Buffers.ArcBuffer> slice. Treat its bytes as read-only.
<xref:Orleans.Serialization.Buffers.ArcBuffer.Empty> represents an owner-free empty
payload. A struct assignment copies the view, not its ownership: use
<xref:Orleans.DurableMessaging.DurableEnvelope.Retain*> to obtain an independent
payload pin, and dispose each owning envelope exactly once.

Typed encoding rents an <xref:Orleans.Serialization.Buffers.ArcBufferWriter>
from a private shared pool for the synchronous serialization call.
<xref:Orleans.Serialization.Buffers.ArcBufferWriter.ConsumeSlice*> returns an owned
slice of the newly written bytes; subsequent messages occupy disjoint regions and
can share backing pages. Consuming a slice advances the writer's readable range;
the returned slice independently keeps its pages alive after the encoder returns
to the pool. Failed encoding clears partial output before returning the encoder.
Application-owned raw encoders have explicit scope disposal and synchronous usage.

| Boundary | Ownership and release |
| --- | --- |
| Application constructs a slice or envelope | The caller owns it. Use `using` and dispose after staging or handing it to a delivery call which acquires independent ownership. |
| Typed outbox `Send` or `SendReply` | Encodes and stages synchronously, then disposes its temporary envelope. Durable state owns the independently retained pin. |
| `IDurableOutbox.Send(envelope)` | Borrows the envelope during the call. Durable dictionary state retains its own pin, so the caller can dispose its local owner immediately after staging. |
| `IInboxHandlerContext.Envelope` | Borrowed until the actual handler method ends, including asynchronous preparation. Do not dispose the context envelope or its payload. Retain explicitly when keeping it longer. |
| Direct `IDurableInboxExtension.DeliverAsync` call | Borrows the caller's envelope and retains an admission pin before its first asynchronous wait. The caller can dispose its local owner after initiating the call. Admission retains its pin until the actual acceptance operation finishes, including when the caller cancels its wait. |
| `IDurableInboxExtension` RPC request | The generated proxy synchronously copies the envelope with an independent pin; the receiving request owns its decoded slice. The caller can dispose its local owner after initiating the call. Serialization and invocation retain active uses; terminal responses, rejection, cancellation, and shutdown release request ownership after those uses finish. |
| Ordinary persistence or networking serialization | Borrows the payload and leaves its pin intact. Repeated serialization is non-consuming. Operation buffers and decoded owners have their own lifetimes. |

Compute reply bodies and proposed state locally before shared mutations. Typed
`SendReply` serializes and stages the reply before applying that state. Continue
through `Complete()` and actual handler return synchronously. For a batch of outputs,
create every owning envelope before staging any shared changes, then stage the
prepared envelopes and release all local owners, including partial preparation
on failure. In-flight operations retain their own payload pins through actual
completion independently of the caller's wait or local owner.

Messaging registers
<xref:Orleans.Journaling.IDurableDictionaryValueLifecycle`1> for envelopes and
dead letters. Live dictionary mutations retain values before encoding them.
Replay transfers already-owned decoded values into state. Replacement, removal,
reset, journal deletion, and scoped dictionary disposal release the corresponding
state owners. Handler and delivery-batch pins keep borrowed payloads readable
through their actual outcomes even when a preceding writer captures completion
and removes a state owner earlier.

The Arc overloads of <xref:Orleans.Serialization.Serializer.Deserialize*> and
<xref:Orleans.Serialization.Serializer`1.Deserialize*> create an Arc-backed reader.
Codecs can retain raw sub-slices during decode instead of copying them. For explicit
reader control, use <xref:Orleans.Serialization.Buffers.Reader.Create*> with the
borrowed Arc buffer and a serializer session, then the generic serializer's public
`Deserialize(ref reader)` overload:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_arc_ownership" language="csharp":::

### Carry independently encoded items

<xref:Orleans.Serialization.Buffers.BufferPackage> owns one Arc buffer and a read-only
ordinal key index of `(offset, length)` entries. The application chooses each entry's
encoding. <xref:Orleans.Serialization.Buffers.BufferPackageBuilder> owns its Arc
writer; dispose the builder even if adding or encoding an entry fails.
<xref:Orleans.Serialization.Buffers.BufferPackageBuilder.Build*> transfers the
buffer owner into a disposable package. Encoding that package with an ordinary
serializer borrows it; release the package after encoding.

<xref:Orleans.Serialization.Buffers.BufferPackage.Keys> inspects the index without
decoding values. <xref:Orleans.Serialization.Buffers.BufferPackage.TryGetBytes*>
returns a borrowed <xref:System.Buffers.ReadOnlySequence`1> for an entry, and
<xref:Orleans.Serialization.Buffers.BufferPackage.Buffer> exposes a borrowed Arc
view. Keep an owning package alive throughout access to these views; dispose
neither the borrowed buffer nor its entry views. Use
<xref:Orleans.Serialization.Buffers.BufferPackage.Retain*> for an independent package
lifetime. <xref:Orleans.Serialization.Buffers.BufferPackage.Count> includes empty
entries. The index references the concatenated bytes directly, without an array per
entry.

Decode through the Arc serializer overload to retain the package's raw sub-slice.
The decoded package owns its pin independently of the envelope. Use `using` around
that package while inspecting or decoding entries, and dispose it after use:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_buffer_package" language="csharp":::

Raw/package protocols choose an explicit subject and body encoding. The shipment
example returns an owning envelope under `shipments.manifest.v1`; its decoder
checks that subject and returns an independently owning package. Keep that package
in a `using` scope while reading its borrowed entries. Typed DTOs containing owning
Arc slices or packages have the same explicit application disposal responsibility.

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
  is staged or durable. Reusing that ID with a different sender, receiver, subject, or payload
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
- The receiver deduplicates by exact `MessageId` across senders and subjects. A pending
  repeat must retain the original receiver, subject, and body. A completed duplicate
  acknowledges the retained completion fact. Completion records retain the ID and time;
  the immutable-command contract keeps parameters stable after completion.
  Distinct parent and child keys identify independent commands. Duplicate deliveries
  converge on one set of handler effects while that completion record is retained. After
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
relevant preconditions after asynchronous preparation and observe cancellation
before the first shared mutation. Synchronous typed routes receive the dispatcher's
boundary cancellation check. Compute the complete business update locally, stage
typed outgoing messages before applying it, and call
<xref:Orleans.DurableMessaging.IInboxHandlerContext.Complete*>. For several outgoing
messages, encode every envelope before staging the prepared batch.
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
intents before capture. Applications compute results locally and use typed outbox
helpers to encode before their first outgoing mutation, followed by synchronous
business changes and completion. Bulk output prepares all envelopes before the
final block. Earlier journal writes can complete during
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

Unknown subjects, malformed application payloads, and validation errors follow the
processing retry and dead-letter path. Typed decoding precedes business mutation;
later envelopes remain available for recovery and processing.

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

An ordinary grain method accepts a stable application command ID, computes its
submission count, then uses typed `Send` before assigning that count synchronously,
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
IDs and subjects remain available in authorized message diagnostics. Log canonical
command IDs to correlate retries, participant effects, and deterministic replies.