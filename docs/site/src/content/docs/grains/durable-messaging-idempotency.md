---
title: Durable messaging idempotency
description: Design transport identities, hierarchical business-operation keys, and durable outcome ledgers.
ms.date: 10/08/2026
ms.topic: conceptual
---

# Durable messaging idempotency

Idempotency makes repeated execution of one logical operation converge on its original
outcome. Durable Messaging combines receiver-side transport deduplication with
application-defined business idempotency. Use both when clients, upstream services,
or operators can submit the same business request more than once.

For the commit protocol, see [Durable messaging](durable-messaging.md).
For complete grain examples, see [Practical recipes](durable-messaging-recipes.md).

## Choose an identity at each boundary

| Identity | Owner and scope | Purpose |
| --- | --- | --- |
| `(SenderId, MessageId)` | The receiving grain's inbox | Coalesces retransmission of an envelope while it is pending or its processed record is retained. |
| `CorrelationKey` | The application's workflow | Groups related requests, responses, and child steps using a hierarchy. |
| Business-operation key | A journaled application ledger | Recognizes the same business request across fresh envelope IDs, different producers, and longer retention periods. |
| External idempotency key | The external system's account or API namespace | Makes a retried external side effect return the original provider outcome. |

<xref:Orleans.DurableMessaging.DurableEnvelopeBuilder.Build*> creates a fresh
`MessageId` and creation timestamp. The outbox retains that envelope across its own
delivery retries. An application which reconstructs a request creates another transport
identity, so its receiver uses a business-operation ledger to recognize the logical
request.

For example, two envelopes can share the operation key
`tenants/acme/orders/42/payment/charge` while having different message IDs. The inbox
accepts both transport identities; the payment ledger returns the already-recorded
charge result for the second. Conversely, an order's `payment/charge` and
`inventory/widget/reserve` keys identify separate effects within one workflow.

## Transport deduplication

The inbox checks the sender and message ID together. A pending duplicate points to the
already-admitted work. A processed duplicate returns `Duplicate` while its record is
within <xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.DeduplicationWindow>.
The same GUID used by a different sender identifies a different transport message.
Each receiver owns its own records.

Handler <xref:Orleans.DurableMessaging.IInboxHandlerContext.Complete*> stages the
processed record, inbox removal, business changes, and outgoing intents in one
synchronous final block. Journal acknowledgement makes that captured cohort durable.
Replay restores the business effects and processed identity together.

After the processed record expires, a replay can be admitted again. Choose the window
to cover delivery retries, outage recovery, and planned operational replay delays.
Configuration requires `MaxOutboxRetryAge < DeduplicationWindow`. Producer fleets must
use compatible policies, and an operator replay after this window relies on the
application ledger.

The outbox coalesces equivalent envelopes with a live message ID across prepared,
staged, and durable intents. Equivalence includes receiver, route, correlation,
reply destination, timestamp, body, and request-context bytes and types. Preserve
the original envelope when explicitly resubmitting it; an ID binds to its original
content. Reuse with conflicting content raises an error.

## Hierarchical business-operation keys

<xref:Orleans.DurableMessaging.HierarchicalKey> provides ordinal, case-sensitive,
segment-aware equality and ancestry. A useful hierarchy is:

```text
tenants/{tenant}/orders/{order}
  inventory/{sku}/reserve
  inventory/{sku}/release
  payment/charge
  payment/refund/{refund}
  shipment/create
```

The root identifies the workflow. Each leaf identifies one business effect. Use a
distinct leaf for a compensation, partial refund, or intentionally new attempt whose
business meaning differs. Retried requests keep the same leaf. Choose the key from
stable domain identifiers before sending; generating a random key during handling
would identify a new operation on every retry.

The following helper percent-encodes opaque tenant and SKU identifiers into single
segments. All producers use the same normalization and encoding:

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_operation_keys" language="csharp":::

For readable slash-containing segments, use
<xref:Orleans.DurableMessaging.HierarchicalKey.CreateEscapedChildKey*>. The following
key has one SKU segment, `widget/blue`:

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_hierarchy" language="csharp":::

`CreateChildKey("payment/charge")` appends two segments; the escaped-child form treats
the slash as part of one segment. Use one canonical representation consistently
across producers, ledger keys, logs, and external provider keys.
<xref:Orleans.DurableMessaging.HierarchicalKey.IsAncestorOf*> matches a root and
its descendants at segment boundaries: `orders/42` matches `orders/42/payment`,
whereas `orders/420/payment` belongs to another order. A key is its own ancestor;
`IsChildOf` tests exactly one additional segment.

The runtime uses correlation for selection and diagnostics. Business deduplication
is an explicit lookup in an application-owned
<xref:Orleans.Journaling.IDurableDictionary`2>. Store the full leaf key for exact
operation matching. Use ancestor checks for grouping or authorization after deriving
the authorized root from trusted caller identity.

## Persist a result, not just a flag

A durable outcome ledger binds each operation key to the original request and result.
On receipt:

1. Validate the body, trusted tenant, operation key, and reply destination locally.
2. Look up the key. For an existing entry, verify that the immutable request fields
   match and prepare a response with the original result.
3. For a new entry, compute the business outcome and construct outgoing envelopes
   before changing shared state.
4. Synchronously apply the business change, record its outcome, send the response,
   call `Complete()`, and return.

The [inventory recipe](durable-messaging-recipes.md#reserve-inventory-once-per-order-line)
binds a key to its quantity and reservation decision. A second request for the same
key and quantity returns the cached decision. A conflicting quantity is a contract
error handled by the configured processing-failure policy. A stock shortage is a
recorded rejection, so subsequent redelivery returns that same business decision.
A newly authorized reservation uses a new operation key.

Outcome records also serve status queries after an ambiguous caller timeout. A caller
can retry or query by the same domain operation key instead of inventing a new request.
The ledger's retention is a domain decision: retain financial operations for the
required audit horizon, or compact completed order steps into an order tombstone which
continues to recognize those requests. Removing a record deliberately permits later
execution under that key.

## External side effects

An external payment, email, or HTTP operation has its own commit boundary. An
activation can fail after that operation succeeds and before its result is journaled.
The [payment recipe](durable-messaging-recipes.md#charge-an-external-provider-with-a-stable-key)
calls a provider during local preparation with a stable operation key. The provider
enforces request consistency and returns its original result on retries.

Specify the provider account/tenant namespace, key format, retention, conflict
behavior, and outcome-query API as part of that integration. Keep its retention at
least as long as every retry or replay which can reach it. An ambiguous response is
reconciled or retried with the original key. A provider which exposes only a
non-idempotent operation needs an application reconciliation workflow for uncertain
outcomes before another effect is authorized.

Once the result is known, construct the reply, observe cancellation, and finish the
journaled result, outgoing reply, and `Complete()` without awaiting. Every asynchronous
provider call precedes the first shared mutation.

## Fan-out and multi-grain workflows

Give each participant or step its own operation key under the workflow root. A
coordinator records its submitted steps and aggregates correlated outcomes in its own
journal. Participants commit their individual effects and replies atomically.
The coordinator commits each received outcome and any next-step intent together.

Use a durable step status such as `Requested`, `Succeeded`, `Rejected`, or
`Compensated`, plus the recorded participant outcome. Duplicate responses converge
on that status. A rejection can emit separately keyed release or refund commands for
already-completed steps. Each participant applies that compensation idempotently.
This gives the workflow an explicit forward and recovery path across independently
committed grain journals.

## Ordering and replay

Choose idempotency according to the operation's meaning:

| Operation | Convergent application policy |
| --- | --- |
| Reserve stock or charge a payment | Exact operation-key lookup with immutable request and cached outcome. |
| Replace a projection with a complete snapshot | Apply only a newer domain version; complete older snapshots as already superseded. |
| Apply every incremental event | Persist the expected sequence and buffer gaps in journaled state before applying contiguous events. |
| Intentionally reprocess with corrected logic | Authorize a new revision/operation key and record its relationship to the original outcome. |

An order root groups messages; a version or sequence field establishes domain order.
The [projection recipe](durable-messaging-recipes.md#converge-an-out-of-order-stock-projection)
demonstrates a complete-snapshot policy.
