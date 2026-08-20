---
title: Durable messaging
description: Understand the durable inbox and outbox guarantees, recovery model, and operating limits.
ms.date: 08/19/2026
ms.topic: conceptual
---

# Durable messaging

The `Microsoft.Orleans.DurableMessaging` package provides a grain-scoped inbox and
outbox built on Orleans Journaling and Durable Jobs. It is intended for application
messages whose state effects and outgoing messages must survive activation loss.

## Message and routing model

Each <xref:Orleans.DurableMessaging.DurableEnvelope> identifies its sender, target,
route, message ID, optional <xref:Orleans.HierarchicalKey> correlation, optional
`ReplyTo` grain ID, and an opaque serialized body. A receiver evaluates registered
handlers in registration order. Handlers can select envelopes by exact route, route
prefix, correlation hierarchy, or arbitrary metadata, and typed handlers deserialize
the body only when selected.

`ReplyTo` is general message metadata. Applications decide which route and body to use
for a follow-up message.

## Commit and delivery guarantees

Durable Messaging has the following boundaries:

- Calling <xref:Orleans.DurableMessaging.IDurableOutbox.Send*> stages an envelope in the
  grain journal. The envelope and other journaled grain effects become durable in one
  commit. Dispatch starts only after that commit succeeds.
- A failed turn restores the last durable journal version. Its staged effects and
  outgoing envelopes are discarded before the inbox records a retry or dead letter.
- A receiver returns `Accepted` only after both the envelope and stable ownership of
  its inbox drain job are durable.
- Transport is **at-least-once**. A crash after receiver acceptance but before durable
  outbox removal can send the same envelope again.
- The receiver deduplicates by `(SenderId, MessageId)`. Duplicate deliveries converge
  on one set of handler effects while that deduplication record is retained. After
  <xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.DeduplicationWindow>
  expires and compaction runs, the same envelope can be processed again.
- Delivery is **unordered**. Inbox and outbox storage are dictionaries, and retries can
  reorder envelopes. Applications which require ordering must carry sequence numbers
  and make their handlers converge on application-defined order.

The inbox and outbox use independent Durable Jobs. A blocked inbox handler on one grain
doesn't stop another grain's outbox. Activation recovery repairs missing scheduling,
including a crash after durable envelope storage but before a local pump is started.
Pump callbacks execute as non-interleaving grain timer turns so that infrastructure
writes can't commit provisional state from a concurrently running handler.

## Backpressure, retries, and dead letters

The inbox rejects new, nonduplicate envelopes with `Backpressured` when it reaches
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.MaxCapacity>. The
sender retains and retries the envelope. Handler failures restore the preceding durable
state before retry accounting is committed. Messages move to the appropriate inbox or
outbox dead-letter collection after their configured attempt or age limit. Use
<xref:Orleans.DurableMessaging.IDurableMessagingDiagnostics> to inspect those records.

Malformed typed bodies are isolated during handler deserialization and follow the same
retry and dead-letter path; they don't prevent later envelopes from being recovered.

## Deployment requirements

Configure Durable Jobs storage and Journaling storage before enabling Durable
Messaging. The Journaling implementation must support
<xref:Orleans.Journaling.IJournaledStateManager.RevertPendingChangesAsync*>; silo
startup fails when rollback capability is absent. Use shared, production-grade storage
for multi-silo deployments. In-memory Durable Jobs and journal storage are suitable
only for development and tests.

Capacity and retention settings bound storage growth and define the effectively-once
window. Monitor inbox depth, outbox depth, retry failures, dead letters, and oldest
pending-message age. Keep deduplication retention longer than the maximum expected
outbox retry age.
