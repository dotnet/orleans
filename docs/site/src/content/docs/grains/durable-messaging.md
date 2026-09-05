---
title: Durable messaging
description: Understand the durable inbox and outbox guarantees, recovery model, and operating limits.
ms.date: 08/29/2026
ms.topic: conceptual
---

# Durable messaging

The `Microsoft.Orleans.DurableMessaging` package provides a grain-scoped inbox and
outbox built on Orleans Journaling and Durable Jobs. It is intended for application
messages whose state effects and outgoing messages must survive activation loss.

## Message and routing model

Each <xref:Orleans.DurableMessaging.DurableEnvelope> identifies its sender, target,
route, message ID, optional <xref:Orleans.DurableMessaging.HierarchicalKey> correlation, optional
`ReplyTo` grain ID, and an opaque serialized body. Exact route registrations take
precedence. When no exact route is registered, a receiver evaluates generic handlers in
registration order. Handlers can select envelopes by route prefix, correlation hierarchy,
or arbitrary metadata, and typed handlers deserialize the body only when selected. The
receiving grain verifies that the envelope target matches its own identity before
deduplication or persistence.

The preview correlation key type now belongs to this package as
<xref:Orleans.DurableMessaging.HierarchicalKey>. Draft consumers of
`Orleans.HierarchicalKey` should update their namespace import. Its serialized alias
and member identifiers remain unchanged, so envelopes written by the earlier draft
remain readable.

`ReplyTo` is general message metadata. Applications decide which route and body to use
for a follow-up message.

## Commit and delivery guarantees

Durable Messaging has the following boundaries:

- Calling <xref:Orleans.DurableMessaging.IDurableOutbox.Send*> stages an envelope in the
  grain journal. Before journal capture, Durable Messaging allocates a stable job ID and
  durably schedules the outbox job. The envelope, job ownership, and other journaled
  grain effects then become durable in one commit. The job polls safely while the
  envelope is provisional, and dispatch starts only after that commit succeeds.
- Sending an equivalent envelope with the same `MessageId` more than once is idempotent,
  whether the original is provisional or durable. Reusing that ID with different routing,
  correlation, body, or request-context content throws without changing the outbox.
- A failed inbox-handler attempt restores the last durable journal version before
  retry or dead-letter accounting commits. The failed attempt's staged effects and
  outgoing envelopes are discarded at that boundary.
- Inbox handlers stage journaled effects and outgoing envelopes. Durable Messaging
  commits those changes together with inbox completion after the handler returns;
  handlers cannot create an earlier journal commit or delete boundary. A direct
  `WriteStateAsync` or `DeleteStateAsync` attempt fails the handler and restores the
  preceding durable state before retry accounting is recorded, including when the
  handler catches the immediate exception and returns.
- Deleting the grain journal discards staged inbox and outbox work and clears the
  corresponding volatile pump bookkeeping before a later write begins.
- A receiver allocates a stable ownership token and places it in a scheduled inbox job
  before committing both the envelope and ownership, and returns `Accepted` only after
  both are durable.
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

The inbox and outbox use independent Durable Jobs. A blocked inbox handler on one grain
doesn't stop another grain's outbox. Monotonic ownership generations fence job
callbacks. Scheduling uses an internal stable physical job ID for each grain, pump, and
ownership generation, so retrying an ambiguous response while the original schedule is
active returns that job instead of creating another one. Completed-generation
tombstones let delayed duplicates terminate. A job which wakes before its ownership
commit or activation recovery is visible polls the same attempt instead of completing.
After recovery, a scheduled generation with no committed owner and no work is a
confirmed orphan and completes, so Durable Jobs removes it. If recovered work has no
matching owner, recovery schedules and commits a new generation before the old
generation terminates. Callbacks for the recovered and replacement generations both poll
until replacement ownership commits, preserving the existing durable wake-up if
scheduling or persistence must retry. Ownership-clear write failures restore the
preceding generation, so the current job remains responsible. Pump callbacks execute as
non-interleaving grain timer turns so that infrastructure writes can't commit
provisional state from a concurrently running handler.

## Durable RPC incubation

The `Orleans.DurableTasks.Abstractions` and `Microsoft.Orleans.DurableTasks`
assemblies are source-only, incubating components. Neither is published as a NuGet package.
Documentation and samples must not present them as externally consumable until their
ownership and publication model is decided.

During activation shutdown, the Orleans adapter rejects new durable RPC work and
cancels adapter-controlled scheduling and waits without converting activation shutdown
into durable cancellation. It discards user results and failures produced after
shutdown begins, then drains all executing user code before replacement replay starts.
User code which ignores cancellation can therefore delay activation shutdown.

Recovery follows the same non-overlap rule. It aborts adapter-controlled waits and
drains stale execution before replay. If user code remains active beyond
<xref:Orleans.Configuration.DurableTaskOptions.RecoveryExecutionDrainTimeout>, the
runtime fences new durable scheduling and requests activation deactivation instead of
starting a concurrent replay.

## Backpressure, retries, and dead letters

The inbox rejects new, nonduplicate envelopes with `Backpressured` when it reaches
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.MaxCapacity>. The
sender retains and retries the envelope. Handler failures restore the preceding durable
state before retry accounting is committed. Messages move to the appropriate inbox or
outbox dead-letter collection after their configured attempt or age limit. Use
<xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics> to inspect those records.
After an operator or application has handled a record, remove it with
<xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.RemoveInboxDeadLetter*>
or <xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics.RemoveOutboxDeadLetter*>
so dead-letter storage remains bounded by the application's retention policy.
Removal is staged in the grain transaction and becomes durable with the grain's next
journal write.

Dead letters are retained for
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.DeadLetterRetentionPeriod>
and bounded by
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.MaxRetainedDeadLetters>
independently for each grain inbox and outbox. When a new dead letter reaches the
configured capacity, Durable Messaging removes the oldest retained entries in the same
commit. Activation recovery also removes entries which exceed the retention period or a
newly reduced capacity.

Malformed typed bodies are isolated during handler deserialization and follow the same
retry and dead-letter path; they don't prevent later envelopes from being recovered.
A successfully decoded null body is delivered as null. Typed handler parameters are
explicitly null-capable and handlers which require a non-null body must validate it.

## Deployment requirements

Configure Durable Jobs storage and Journaling storage before enabling Durable
Messaging. Grains which use Durable Messaging derive from
<xref:Orleans.Journaling.DurableGrain>; its activation lifecycle initializes the
journaled state manager and materializes the inbox and outbox participants before
message recovery begins. Durable Messaging selects the built-in `orleans-binary`
journal format so opaque envelope bodies and request-context slices recover exactly.
Durable Messaging grains use non-reentrant execution: they don't apply `Reentrant`,
`MayInterleave`, `AlwaysInterleave`, or `StatelessWorker`. A single non-interleaving
activation owns each grain journal and pump, so infrastructure writes cannot commit
provisional application state or compete with another activation for the same ownership.
The Journaling implementation must provide
<xref:Orleans.Journaling.IJournaledStateManager.RevertPendingChangesAsync*> and accept
<xref:Orleans.Journaling.IJournaledStateManager.RegisterObserver*> so Durable Messaging
receives commit and recovery notifications. It must also provide the built-in
request-time mutation guard used to reject handler commits and deletes before enqueue.
Durable Messaging validates observer registration and the mutation guard when the
activation is constructed. Retry and failure paths rely on
<xref:Orleans.Journaling.IJournaledStateManager.RevertPendingChangesAsync*> and surface
its failure if rollback is unavailable. Use shared, production-grade storage for
multi-silo deployments. In-memory Durable Jobs and journal storage are suitable only for
development and tests.

Durable Messaging uses the `orleans-binary` journal format. Inbox, outbox, ownership,
and durable RPC state contain Orleans-polymorphic values whose recovery contract is
validated with the Orleans serializer. `AddDurableMessaging` selects this format, and
startup validation reports an incompatible format override before activations process
messages. `JournaledStateManagerOptions` applies to the host, so workloads which require
Journaling's JSON migration workflow run in a separate silo host which does not call
`AddDurableMessaging`.

Capacity and retention settings bound storage growth and define the effectively-once
window. Monitor inbox depth, outbox depth, retry failures, dead letters, and oldest
pending-message age. Keep deduplication retention longer than the maximum expected
outbox retry age. The `orleans-durable-messaging-orphaned-jobs-reclaimed` counter
identifies terminal cleanup of schedule-before-commit crash remnants.
