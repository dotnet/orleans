---
title: Durable messaging
description: Understand the durable inbox and outbox guarantees, recovery model, and operating limits.
ms.date: 08/20/2026
ms.topic: conceptual
---

# Durable messaging

The `Microsoft.Orleans.DurableMessaging` package provides a grain-scoped inbox and
outbox built on Orleans Journaling and Durable Jobs. It is intended for application
messages whose state effects and outgoing messages must survive activation loss.

## Message and routing model

Each <xref:Orleans.DurableMessaging.DurableEnvelope> identifies its sender, target,
route, message ID, optional <xref:Orleans.DurableMessaging.HierarchicalKey> correlation, optional
`ReplyTo` grain ID, and an opaque serialized body. A receiver evaluates registered
handlers in registration order. Handlers can select envelopes by exact route, route
prefix, correlation hierarchy, or arbitrary metadata, and typed handlers deserialize
the body only when selected.

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
- A failed turn restores the last durable journal version. Its staged effects and
  outgoing envelopes are discarded before the inbox records a retry or dead letter.
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
callbacks; if an ambiguous scheduling response creates more than one job, callbacks
with that generation poll the same durable queue safely. Completed-generation
tombstones let delayed duplicates terminate. A job which wakes before its ownership
commit or activation recovery is visible polls the same attempt instead of completing.
After recovery, a scheduled generation with no committed owner and no work is a
confirmed orphan and completes, so Durable Jobs removes it. If recovered work has no
matching owner, recovery schedules and commits a new generation before the old
generation terminates. Ownership-clear write failures restore the preceding generation,
so the current job remains responsible. Pump callbacks execute as non-interleaving grain
timer turns so that infrastructure writes can't commit provisional state from a
concurrently running handler.

## Backpressure, retries, and dead letters

The inbox rejects new, nonduplicate envelopes with `Backpressured` when it reaches
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.MaxCapacity>. The
sender retains and retries the envelope. Handler failures restore the preceding durable
state before retry accounting is committed. Messages move to the appropriate inbox or
outbox dead-letter collection after their configured attempt or age limit. Use
<xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics> to inspect those records.

Malformed typed bodies are isolated during handler deserialization and follow the same
retry and dead-letter path; they don't prevent later envelopes from being recovered.
A successfully decoded null body is delivered as null. Typed handler parameters are
explicitly null-capable and handlers which require a non-null body must validate it.

## Deployment requirements

Configure Durable Jobs storage and Journaling storage before enabling Durable
Messaging. The Journaling implementation must support
<xref:Orleans.Journaling.IJournaledStateManager.RevertPendingChangesAsync*> and report
<xref:Orleans.Journaling.IJournaledStateManager.SupportsObservers> so Durable Messaging
can use <xref:Orleans.Journaling.IJournaledStateManager.RegisterObserver*> for commit and
recovery notifications. Activation fails with a durable-messaging-specific diagnostic
when either capability is absent. Use shared, production-grade storage for multi-silo
deployments. In-memory Durable Jobs and journal storage are suitable only for development
and tests.

Capacity and retention settings bound storage growth and define the effectively-once
window. Monitor inbox depth, outbox depth, retry failures, dead letters, and oldest
pending-message age. Keep deduplication retention longer than the maximum expected
outbox retry age. The `orleans-durable-messaging-orphaned-jobs-reclaimed` counter
identifies terminal cleanup of schedule-before-commit crash remnants.
