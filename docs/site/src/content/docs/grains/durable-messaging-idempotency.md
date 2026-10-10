---
title: Durable messaging idempotency
description: Design application command identities, hierarchical keys, retained completion facts, and external idempotency.
ms.date: 10/09/2026
ms.topic: conceptual
---

# Durable messaging idempotency

An application assigns one stable hierarchical ID to each logical command. Durable
Messaging uses that ID throughout admission, retries, completion, diagnostics, and
recovery. The receiving inbox's retained completion record recognizes repeated
submissions of that command across senders and subjects.

For the commit protocol, see [Durable messaging](durable-messaging.md).
For complete grain examples, see [Practical recipes](durable-messaging-recipes.md).

## Choose an identity at each boundary

| Identity | Owner and scope | Purpose |
| --- | --- | --- |
| Envelope `MessageId` | Exact key in the receiving grain's inbox | Coalesces pending repeats and recognizes completed commands during retention. |
| Envelope `MessageId` | Exact key in the sending grain's outbox | Binds one live outgoing intent to its destination, subject, and body. |
| Workflow root | The application's protocol | Groups related command IDs and deterministic reply IDs. |
| External idempotency key | The external system's account or API namespace | Recognizes a retried external side effect using the canonical command ID. |

Derive a command ID from stable tenant, entity, and action identifiers before
sending. Every retry and reconstructed submission uses the original ID. Namespace
independent producers explicitly. A forwarding grain preserves the original
logical command ID while becoming the immediate sender. A new workflow command
uses a distinct child or sibling ID; fan-out derives one stable recipient child
per destination.

One key identifies one immutable command: destination, subject, and body retain
their original meaning. A genuinely new reservation, authorized compensation, or
revision uses a new key. Protocol upgrades preserve the identity of an existing
command even when producers and consumers are deployed at different times.

## Transport deduplication

Each receiver checks the exact `MessageId`. Sender and subject are provenance and
dispatch metadata; the ID establishes receiver-local uniqueness across both.
A parent and its child are independent commands. A pending duplicate refers to the
already-admitted work and must preserve its subject, receiver, and body bytes.
A completed duplicate returns `Duplicate` while its completion record is within
<xref:Orleans.DurableMessaging.Configuration.DurableInboxOptions.DeduplicationWindow>.
Completed records retain the ID and timestamp. Applications uphold the immutable
command contract; the completion fact acknowledges the original execution.

Handler <xref:Orleans.DurableMessaging.IInboxHandlerContext.Complete*> stages the
completion record, inbox removal, business changes, and outgoing intents in one
synchronous final block. Journal acknowledgement makes that captured cohort durable.
Replay restores the business effects and completion identity together. This supplies
one local effect per command during the retained completion lifetime.

After the record expires, the same key can be admitted and handled again. Retain
completion facts for the complete supported resubmission horizon, including outage
recovery and operator replay delays. Configuration requires
`MaxOutboxRetryAge < DeduplicationWindow`; producer fleets coordinate their policies.
Permanent idempotency requires permanent or appropriately retained completion facts
and an explicit storage budget. Application result-query retention serves the
business's query and audit needs.

The outbox coalesces equivalent envelopes with a live message ID across staged and
durable intents. Equivalence includes sender, receiver, subject, and raw body bytes.
Conflicting live intent reuse raises an error before replacing the original intent.

## Hierarchical business-operation keys

<xref:Orleans.DurableMessaging.HierarchicalKey> is an immutable value type with ordinal,
case-sensitive equality, `==` and `!=`, and segment-aware navigation. Its canonical
escaped path identifies the application command directly in the envelope.
`default(HierarchicalKey)` is the unset value, exposed by `IsDefault`; envelope
validation requires a set ID.

A useful hierarchy is:

```text
tenants/{tenant}/orders/{order}
  inventory/{sku}/reserve
  inventory/{sku}/release
  payment/charge
  payment/refund/{refund}
  shipment/create
```

The root groups a workflow; each leaf identifies one effect. Retried requests keep
the same leaf. Assign a stable attempt/revision segment once when authorizing a
new effect. Format numeric components invariantly and normalize business identifiers
before construction. Key comparison preserves their ordinal spelling.

<xref:Orleans.DurableMessaging.HierarchicalKey.Create*> accepts literal segments,
and <xref:Orleans.DurableMessaging.HierarchicalKey.CreateChildKey*> appends one literal
segment. Both escape slash and backslash characters exactly once. Construct
`payment/charge` using separate `"payment"` and `"charge"` arguments.
<xref:Orleans.DurableMessaging.HierarchicalKey.Parse*> reads an already escaped path,
and formatting emits that canonical representation.

The following helper constructs command IDs using literal tenant and SKU identifiers:

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_operation_keys" language="csharp":::

The SKU `widget/blue` occupies one literal segment:

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_hierarchy" language="csharp":::

Use the same canonical identity across producers, inboxes, logs, result queries,
and external provider keys.
<xref:Orleans.DurableMessaging.HierarchicalKey.IsAncestorOf*> matches at segment
boundaries: `orders/42` groups `orders/42/payment`, while `orders/420/payment`
belongs to another order. A key is its own ancestor; `IsChildOf` tests exactly one
additional segment. Deduplication always compares the full key.

Trusted ingress derives the authorized tenant/entity root and checks the claimed
command key, subject, and destination against it before accepting work. A key
expresses identity; authorization comes from that trusted application boundary.
Message admission bounds IDs to 1,024 UTF-8 bytes and 32 segments and subjects to
256 UTF-8 bytes. General key construction remains a segment utility; transport
validation applies those message-specific bounds.

<a id="persist-a-result-not-just-a-flag"></a>

## Persist queryable results

A handler can stage a durable reply whose ID is the command's stable `result` child.
The [inventory recipe](durable-messaging-recipes.md#reserve-inventory-once-per-order-line)
computes its reservation decision and next stock locally. Typed `SendReply`
encodes and stages the reply, then the method updates stock and completes synchronously. A shortage is a completed rejection with unchanged
stock. The originally committed outbox intent supplies result delivery within its
configured policy. A resubmission acknowledges the retained completion fact and
preserves that original execution.

An inbox `Duplicate` acknowledges pending or completed command identity. Obtain
the business result from the durable reply, or query business state keyed by the
same command ID. A distinct query command can request a result after the original
reply's delivery policy ends.

Store reservation records when lookup, release, or expiry needs them, payment
results for status and reconciliation, and workflow phases for progress queries.
These are business state with explicit query/audit retention. Commit them together
with local effects, outgoing intent, and inbox completion. The
[payment recipe](durable-messaging-recipes.md#charge-an-external-provider-with-a-stable-key)
exposes a result query by the original command ID.

## External side effects

An external payment, email, or HTTP operation has its own commit boundary. An
activation can fail after that operation succeeds and before its result is journaled.
The payment recipe calls its provider during local preparation with
`context.Envelope.MessageId.ToString()`. The provider enforces request consistency
and returns its original result on retries with that key.

Specify the provider account/tenant namespace, key format, retention, conflict
behavior, and outcome-query API as part of that integration. Retain provider
idempotency facts for every retry or replay which can reach it. For a provider's
key-length restriction, an adapter can deterministically hash the canonical ID and
retain the original command ID locally for correlation. Reconcile an ambiguous
response or retry with the original key. An API with an uncertain outcome requires
its reconciliation workflow before another effect is authorized.

Once the result is known, observe cancellation, send the typed reply, and finish the
journaled result and `Complete()` without awaiting. Every asynchronous
provider call precedes the first shared mutation.

## Fan-out and multi-grain workflows

Give each participant or step its own command ID under the workflow root. One live
outbox key maps to one destination, so notifications use stable recipient-specific
children. A coordinator records submitted steps and aggregates correlated outcomes
in its own journal. Participants commit their effects and deterministic replies
atomically; the coordinator commits each received outcome and next-step intent together.

Use a durable step status such as `Requested`, `Succeeded`, `Rejected`, or
`Compensated` with the participant outcome. A rejection can emit separately keyed
release or refund commands for completed steps. Each participant completes that
compensation under its own exact key. This gives the workflow an explicit forward
and recovery path across independently committed grain journals.

## Ordering and replay

| Operation | Application policy |
| --- | --- |
| Reserve stock or charge a payment | Preserve the command ID through retries and retain its inbox completion fact for the supported horizon. |
| Replace a projection with a complete snapshot | Apply only a newer domain version; complete older snapshots as superseded. |
| Apply every incremental event | Persist the expected sequence and buffer gaps before applying contiguous events. |
| Intentionally reprocess with corrected logic | Authorize a new revision key and record its relationship to the original outcome. |

Message identity recognizes completed commands; a domain version or sequence
establishes order. The
[projection recipe](durable-messaging-recipes.md#converge-an-out-of-order-stock-projection)
demonstrates a complete-snapshot policy.
