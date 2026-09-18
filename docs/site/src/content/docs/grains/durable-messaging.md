---
title: Durable messaging
description: Understand the durable inbox and outbox guarantees, recovery model, and operating limits.
ms.date: 09/18/2026
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

- Calling <xref:Orleans.DurableMessaging.IDurableOutbox.Send*> creates a local pending
  intent. Outbox inspection includes local intents and journaled messages once per ID.
  An ordinary journal write prepares the required durable wake-up before capturing the
  envelope, ownership generation, and exact returned job handle with the grain's staged
  effects. State readiness is checked again after asynchronous preparation, so newly
  staged intents have a viable wake-up before capture. Completion releases exactly the
  captured messages for dispatch; intents staged during storage I/O await another write.
- Sending an equivalent envelope with the same `MessageId` more than once is idempotent,
  whether the original is provisional or durable. Reusing that ID with different routing,
  correlation, body, or request-context content throws without changing the outbox.
- Handlers prepare local values asynchronously and return a synchronous action.
  Expected preparation failures produce retry or dead-letter accounting. Messaging
  invokes the action once for that prepared attempt and stages outgoing intents, inbox
  completion and deduplication in the same uninterrupted activation turn.
- Journal capture observes the combined staged effects. The manager serializes
  prerequisite preparation, capture and storage; each state acknowledges its captured
  changes after storage succeeds.
- Deletion requires quiescent messaging operations. Successful deletion clears durable
  messaging state and pending intents before subsequent writes begin.
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
I/O and envelope serialization using operation-local values, then returns a non-null
synchronous action. The action applies the prepared business mutations and stages
prepared outgoing envelopes. Messaging validates the current message and ownership
before invoking it, then stages inbox completion and deduplication before the activation
turn yields. Earlier journal writes can complete while preparation awaits because the
prepared attempt's effects are still local.

The registered inbox and outbox states own their capture, acknowledgement, replay and
reset bookkeeping. The outbox prepares durable wake-up ownership inside the serialized
journal operation. After every preparation await, the manager checks all states'
readiness again. A successful readiness pass and capture execute synchronously, so a
newly staged message is included with a prepared owner. Mutations made during the
storage await remain pending for the next capture.

A terminal preparation, capture or storage failure fences the journal manager. State
fault notifications stop messaging work before failed write waiters resume. Unexpected
application or staging failures also latch the original error in the inbox state and
drive a journal operation into this terminal path before a queued capture can persist
partial new effects. A fresh activation creates new state objects and replays the
actual durable outcome. A failed append response can follow a successful storage
commit: replay restores that committed envelope and exact ownership handle. A failed
ownership-clear write follows the same boundary; fresh replay determines whether the
previous owner remains responsible or cleanup was committed.

If another state rejects a write request after Messaging has staged an attempt,
Messaging retains the original failure, stops that activation's work and requests
deactivation. The manager's request-validation boundary can reject the request while
remaining healthy. The inbox's latched failure blocks a later admitted capture, and
a fresh activation recovers the durable outcome.

Cancellation of a caller's wait for
<xref:Orleans.Journaling.IJournaledStateManager.WriteStateAsync*> leaves an already
queued write running through capture and acknowledgement. Feature completion tracks
both the triggering write and its captured state acknowledgement. A delivery
caller can also cancel its wait while the owned delivery operation retains admission
through completion. Activation shutdown drains that operation; late failures are
observed and logged even after the caller has left. The owner keeps delivery operations,
gates and pump leases quiescent through the full deletion task and resumes delivery
after awaiting successful deletion. Durable attempts and timer
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

The following grain selects messaging through its application interface and stages a
validated notification count for the inbox's completion write:

:::code source="../snippets/compiled/Grains/DurableMessagingSnippets.cs" id="messaging_grain" language="csharp":::

Orleans caches messaging selection with each concrete grain type. After construction
and grain-instance assignment, shared activation setup validates the execution model,
resolves the activation's scoped endpoints and their registered journaled states.
Journal recovery then restores application and messaging state before activation completes.
With <xref:Orleans.Journaling.HostingExtensions.AddJournalStorage*>, the standard manager
enrolls in the grain lifecycle during grain-bound construction, before resolution returns.
An application-supplied manager establishes one enrollment owner in its constructor or
registration factory. A scoped factory assigning an explicit-<xref:Orleans.Journaling.JournalId>
manager to a grain lifecycle performs that enrollment before returning it. Standalone
managers retain caller-owned initialization and disposal.

Durable Messaging selects the built-in `orleans-binary`
journal format so opaque envelope bodies and request-context slices recover exactly.
Durable Messaging grains use non-reentrant execution. Activation validates the grain's
execution model and reports conflicting `Reentrant`, `MayInterleave`, `AlwaysInterleave`,
or `StatelessWorker` declarations. A single non-interleaving activation owns each grain
journal and pump. The Journaling implementation prepares registered
<xref:Orleans.Journaling.IJournaledState> instances before capture, acknowledges their
persisted changes, and notifies them of terminal failure. State-level deletion checks
enforce quiescence and successful reset restores local messaging bookkeeping.
Command codecs are resolved from the owning manager's configured write format. Use
shared, production-grade storage for multi-silo deployments. In-memory Durable Jobs and
journal storage support development and tests.

Capacity and retention settings bound storage growth and define the effectively-once
window. Monitor inbox depth, outbox depth, retry failures, dead letters, and oldest
pending-message age. Keep deduplication retention longer than the maximum expected
outbox retry age. The `orleans-durable-messaging-orphaned-jobs-reclaimed` counter
identifies terminal cleanup of schedule-before-commit crash remnants.
