---
title: Durable messaging
description: Understand the durable inbox and outbox guarantees, recovery model, and operating limits.
ms.date: 09/21/2026
ms.topic: conceptual
---

# Durable messaging

The `Microsoft.Orleans.DurableMessaging` package provides a grain-scoped inbox and
outbox built on Orleans Journaling and Durable Jobs. It preserves application
message effects and outgoing messages across activation loss.

## Message and routing model

Each <xref:Orleans.DurableMessaging.DurableEnvelope> identifies its sender, target,
route, message ID, optional <xref:Orleans.DurableMessaging.HierarchicalKey> correlation, optional
`ReplyTo` grain ID, and an opaque serialized body. Exact route registrations take
precedence. When no exact route is registered, a receiver evaluates generic handlers in
registration order. Handlers can select envelopes by route prefix, correlation hierarchy,
or arbitrary metadata, and typed handlers deserialize the body only when selected. The
receiving grain verifies that the envelope target matches its own identity before
deduplication or persistence.

`ReplyTo` is general message metadata. Applications decide which route and body to use
for a follow-up message.

## Commit and delivery guarantees

Durable Messaging has the following boundaries:

- Awaiting <xref:Orleans.DurableMessaging.IDurableOutbox.PrepareSendAsync*> copies and
  validates the envelopes, reserves their identities, and confirms a durable self-wakeup
  before application state changes. The returned
  <xref:Orleans.DurableMessaging.IPreparedOutboxBatch> retains those prerequisites.
  Preparation keeps message depth and journaled state unchanged.
- Calling <xref:Orleans.DurableMessaging.IDurableOutbox.Send*> with that batch
  synchronously stages its intents alongside business changes. Outbox inspection includes
  staged intents and journaled messages once per ID. An ordinary journal write captures
  the envelopes, ownership generation, and exact returned job handle with those effects.
  Acknowledgement releases exactly the captured messages for dispatch; intents staged
  during storage I/O await another write.
- Repeatedly sending the same live batch within its valid scope is idempotent. Equivalent
  envelopes sharing a `MessageId` coalesce while their original intent is prepared,
  staged, or durable. Every call validates activation, scope, and lifetime first.
  Reusing an ID with different routing, correlation, body, or request-context content
  throws before intent admission.
- Handlers prepare local values asynchronously and return a synchronous action.
  Expected preparation failures produce retry or dead-letter accounting. Messaging
  invokes the action once for that prepared attempt and stages outgoing intents, inbox
  completion and deduplication in the same uninterrupted activation turn.
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

## Handler preparation and terminal recovery

<xref:Orleans.DurableMessaging.IInboxHandler.CanHandle*> is a pure metadata predicate.
Exact registration preserves handler identity; generic predicates run in registration
order. Selection keeps shared application state unchanged. Its context exposes envelope
metadata and grain identity, and validates access to outgoing-message operations.

<xref:Orleans.DurableMessaging.IInboxHandler.PrepareAsync*> performs fallible computation,
I/O and envelope serialization using operation-local values. It awaits
`context.Outbox.PrepareSendAsync` for outgoing envelopes, then returns a non-null
synchronous action. The action applies the prepared business mutations and calls
`context.Send(batch)` to stage the prepared output. Messaging validates the current message and ownership
before invoking it, then stages inbox completion and deduplication before the activation
turn yields. Earlier journal writes can complete while preparation awaits because the
prepared attempt's effects are still local.

Complete fallible application work and check preconditions during preparation. The
returned action applies the complete set of business changes synchronously, leaving
state ready for an atomic journal write. Orleans' single-threaded activation execution
keeps these updates together until the action returns.

The handler context admits batch preparation during its matching `PrepareAsync` call
and outgoing sends while the returned action executes. Both context send paths share
this attempt-scoped boundary. Preparation supports read-only outbox inspection and
envelope construction. Every use validates the current attempt, phase, activation and
batch lifetime; a contract violation retains its first cause and prevents completion.

Handlers consume preparation results and handle or propagate their failures before
returning an action. Retrieving a failed result counts as consumption when it throws.
The runtime rejects unfinished acquisitions and completed failed or canceled results
which remain unconsumed. It owns started acquisitions and their late results independently
of caller consumption. Converting a returned value task with `AsTask()` transfers result
retrieval to the task adapter; application code then awaits or handles that caller-owned
task before returning the action.

The handler facade retains prepared batches through the attempt's actual persistence
outcome and disposes unused or late results. Ordinary application methods await every
preparation and dispose each successful batch after their synchronous mutation/staging
and journal-write scope. Disposing an unstaged batch releases its reservation; disposing
a staged batch preserves its cohort's ownership through acknowledgement. An empty batch
is a valid no-op which requires no new job.

Durable Messaging uses standard named journaled dictionaries and values. Journaling
supplies their capture, acknowledgement, replay and reset protocol. The outbox's
existing journaled sequence state associates captured message identities and wake-up
ownership with the storage acknowledgement, releasing exactly that cohort for dispatch.
Feature preparation establishes durable wake-up ownership before staging. Application and messaging code
complete their preconditions before applying synchronous changes, so the registered
<xref:Orleans.Journaling.IStateMachine> instances are ready for capture when the journal
write begins. Independent writes can commit earlier valid state while another operation
prepares local values.
Mutations made during storage I/O remain pending for the next capture.

A failure in an admitted write or delete fences the journal manager, faults its
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

Malformed typed bodies follow the same retry and dead-letter path during handler
deserialization, while later envelopes remain available for recovery and processing.
A successfully decoded null body is delivered as null. Typed handler parameters are
explicitly null-capable and handlers which require a non-null body must validate it.

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

The following grain selects messaging through its application interface, prepares an
optional reply, and stages the reply and notification count for the inbox's completion write:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_grain" language="csharp":::

An ordinary grain method awaits preparation before changing its sent count, stages the
prepared batch, and persists both through the application-facing state manager:

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
journal format so opaque envelope bodies and request-context slices recover exactly.
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
identifies terminal cleanup of schedule-before-commit crash remnants.
Message outcome counters group by grain type and status; pending and processed
duplicates each record one duplicate receipt. Sent-message counters and latency
histograms group by grain type, orphan metrics retain job name, and depth gauges report
aggregate pending work. Routing keys remain available in message diagnostics.
