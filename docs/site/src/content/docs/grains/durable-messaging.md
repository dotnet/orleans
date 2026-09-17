---
title: Durable messaging
description: Understand the durable inbox and outbox guarantees, recovery model, and operating limits.
ms.date: 09/16/2026
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
  An admitted write prepares the required durable wake-up before applying the envelope,
  ownership generation, and exact returned job handle synchronously. Journal capture
  includes those changes with the grain's safe staged effects. Completion releases
  exactly the captured messages for dispatch; later intents await another write.
- Sending an equivalent envelope with the same `MessageId` more than once is idempotent,
  whether the original is provisional or durable. Reusing that ID with different routing,
  correlation, body, or request-context content throws without changing the outbox.
- Handlers complete fallible work using local values before staging shared journaled
  effects. Expected preparation failures produce retry or dead-letter accounting.
  Each application mutation is safe for a queued journal write to commit.
- The admitted journal operation prepares its inbox handlers, then the outgoing
  messages and scheduling prerequisites. Synchronous finalization applies the validated
  inbox completion, deduplication and outbox ownership. Their capture includes the
  handler's safe effects. Other queued writes wait for this operation to finish.
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

<xref:Orleans.DurableMessaging.IInboxHandler.HandleAsync*> prepares fallible computation,
I/O and envelope serialization using local values before applying shared effects. The
framework's single messaging observer prepares handlers before outbox prerequisites,
then validates the admitted ownership and applies both endpoints synchronously in the
grain turn. The captured set is fixed for that operation. Unexpected owner, generation
or message invalidation after handler invocation faults the whole admitted operation.

An admitted preparation, finalization, capture or storage failure permanently fences
the journal manager, signals both messaging endpoints, faults pending operations and
requests grain deactivation. A fresh activation creates new state objects and replays
the actual durable outcome. A failed append response can follow a successful storage
commit: replay restores that committed envelope and exact ownership handle. A failed
ownership-clear write follows the same boundary; fresh replay determines whether the
previous owner remains responsible or cleanup was committed.

Cancellation of a caller's wait for
<xref:Orleans.Journaling.IJournaledStateManager.WriteStateAsync*> leaves an already
queued write running through capture and acknowledgement. Feature completion tracks
both the triggering write and its admitted descriptor acknowledgement. Gate waits,
durable attempts and timer turns retain their own cancellation lifetimes. An outgoing
remote batch keeps its durable attempt token across timer turns.

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
Removal is staged in the grain transaction and becomes durable with the grain's next
journal write.

Malformed typed bodies follow the same retry and dead-letter path during handler
deserialization, while later envelopes remain available for recovery and processing.
A successfully decoded null body is delivered as null. Typed handler parameters are
explicitly null-capable and handlers which require a non-null body must validate it.

## Deployment requirements

Configure Durable Jobs storage and Journaling storage, then enable Durable Messaging
with <xref:Orleans.Hosting.DurableMessagingExtensions.AddDurableMessaging*> on the
silo builder or service collection. The optional configuration callback supplies
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions>. Registration
preserves an application-supplied <xref:System.TimeProvider> and validates messaging
options through the options contract. Grains which use Durable Messaging derive from
<xref:Orleans.Journaling.DurableGrain>; its activation lifecycle initializes the
journaled state manager and materializes the inbox and outbox participants before
message recovery begins. Durable Messaging selects the built-in `orleans-binary`
journal format so opaque envelope bodies and request-context slices recover exactly.
Durable Messaging grains use non-reentrant execution. Activation validates the grain's
execution model and reports conflicting `Reentrant`, `MayInterleave`, `AlwaysInterleave`,
or `StatelessWorker` declarations. A single non-interleaving activation owns each grain
journal and pump, keeping infrastructure writes within their owning transaction.
The Journaling implementation admits serialized writes with preparation and synchronous
finalization, and accepts <xref:Orleans.Journaling.IJournaledStateManager.RegisterObserver*>
so Durable Messaging receives commit, initial recovery and terminal-fault notifications. Activation reports a
durable-messaging-specific diagnostic when observer registration is unsupported. Use
shared, production-grade storage for multi-silo deployments. In-memory Durable Jobs and
journal storage support development and tests.

Capacity and retention settings bound storage growth and define the effectively-once
window. Monitor inbox depth, outbox depth, retry failures, dead letters, and oldest
pending-message age. Keep deduplication retention longer than the maximum expected
outbox retry age. The `orleans-durable-messaging-orphaned-jobs-reclaimed` counter
identifies terminal cleanup of schedule-before-commit crash remnants.
