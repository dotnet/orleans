---
title: Messaging idempotency
description: Design application command identities, hierarchical keys, retained completion facts, and external idempotency.
ms.date: 10/09/2026
ms.topic: conceptual
---

# Messaging idempotency

An application assigns one stable hierarchical ID to each logical command. Durable
Messaging uses that ID throughout admission, retries, completion, diagnostics, and
recovery. The receiving inbox's retained completion record recognizes repeated
submissions of that command, from any grain.

For the commit protocol, see [Messaging](messaging.md).
For complete grain examples, see [Practical recipes](messaging-recipes.md).

## Choose an identity at each boundary

| Identity | Owner and scope | Purpose |
| --- | --- | --- |
| Envelope `MessageId` | Exact key in the receiving grain's inbox | Coalesces pending repeats and recognizes completed commands during retention. |
| Envelope `MessageId` | Exact key in the sending grain's outbox | Binds one pending outgoing message to its destination, subject, and body. |
| Workflow root | The application's protocol | Groups related command IDs and deterministic reply IDs. |
| External idempotency key | The external system's account or API namespace | Recognizes a retried external side effect using the canonical command ID. |

Derive a command ID from stable tenant, entity, and action identifiers before
sending. Every retry and reconstructed submission uses the original ID. Namespace
independent producers explicitly. A forwarding grain preserves the original
command ID. A new workflow command
uses a distinct child or sibling ID; fan-out derives one stable recipient child
per destination.

One key identifies one immutable command: destination, subject, and body retain
their original meaning. A genuinely new reservation, authorized compensation, or
revision uses a new key. Protocol upgrades preserve the identity of an existing
command even when producers and consumers are deployed at different times.

## Transport deduplication

Each receiver checks the exact `MessageId`, which identifies one command at that
receiver regardless of sender or subject. A parent and its child are independent
commands. A pending duplicate refers to the already-accepted work and carries the
same subject, body bytes, and `DeliverAt` time; a pending repeat with a different
subject, body, or `DeliverAt` fails.
A completed duplicate returns `Duplicate` while its completion record is within
<xref:Orleans.Messaging.Configuration.DurableMessagingOptions.DeduplicationWindow>.
Completed records retain the ID and timestamp. Applications uphold the immutable
command contract; the completion fact acknowledges the original execution.

A handler stages its business changes and outgoing messages, then calls
<xref:Orleans.Messaging.IInboxHandlerContext.Complete*>, which stages the inbox
removal and completion record. One journal write persists them together, and replay
restores the business effects and completion record together. This gives one local
effect per command while the completion record is retained.

After the record expires, the same key can be admitted and handled again. Retain
completion facts for the complete supported resubmission horizon, including outage
recovery and operator replay delays. Configuration requires
`OutboxRetryTimeout < DeduplicationWindow`; producer fleets coordinate their policies.
Permanent idempotency requires permanent or appropriately retained completion facts
and an explicit storage budget. Application result-query retention serves the
business's query and audit needs.

The outbox coalesces equivalent messages that share a pending message ID, whether
staged or durable. Equivalent messages have the same destination, subject,
payload bytes, and `DeliverAt` time; the original message and its headers are kept. Reusing a pending ID
for different content throws and leaves the original message in place.

A message a grain sends to itself is keyed only in that grain's inbox. `Send` applies
the receiver rules synchronously: a pending repeat with the same subject, body bytes,
and `DeliverAt` time coalesces, a completed ID within the deduplication window is a
duplicate, and a pending repeat with different content throws.

## Hierarchical business-operation keys

<xref:Orleans.Messaging.MessageId> identifies a message by wrapping a
<xref:Orleans.Messaging.HierarchicalKey>. Construct it with `new MessageId(key)` or
<xref:Orleans.Messaging.MessageId.Create*>, and read the key through
<xref:Orleans.Messaging.MessageId.Key>. Message IDs compare, hash, parse, and format
through that key.

<xref:Orleans.Messaging.HierarchicalKey> is an immutable value type wrapping its
canonical escaped path. Equality, `==`, `!=`, and hashing compare that path
ordinally and case-sensitively. The key provides segment-aware navigation through
<xref:Orleans.Messaging.HierarchicalKey.GetParent*>,
<xref:Orleans.Messaging.HierarchicalKey.IsParentOf*>,
<xref:Orleans.Messaging.HierarchicalKey.IsChildOf*>,
<xref:Orleans.Messaging.HierarchicalKey.IsAncestorOf*>, and a segment enumerator.
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

<xref:Orleans.Messaging.HierarchicalKey.Create*> accepts literal segments,
and <xref:Orleans.Messaging.HierarchicalKey.CreateChildKey*> appends one literal
segment. Both escape slash and backslash characters exactly once. Construct
`payment/charge` using separate `"payment"` and `"charge"` arguments.
<xref:Orleans.Messaging.HierarchicalKey.Parse*> reads an already escaped path,
and formatting emits that canonical representation.

The following helper constructs command IDs using literal tenant and SKU identifiers:

:::code source="../snippets/compiled/Grains/MessagingRecipes.cs" id="messaging_operation_keys" language="csharp":::

The SKU `widget/blue` occupies one literal segment:

:::code source="../snippets/compiled/Grains/MessagingRecipes.cs" id="messaging_hierarchy" language="csharp":::

Use the same canonical identity across producers, inboxes, logs, result queries,
and external provider keys.
<xref:Orleans.Messaging.HierarchicalKey.IsAncestorOf*> matches at segment
boundaries: `orders/42` groups `orders/42/payment`, while `orders/420/payment`
belongs to another order. A key is its own ancestor; `IsChildOf` tests exactly one
additional segment. Deduplication always compares the full key.

Trusted ingress derives the authorized tenant/entity root and checks the claimed
command key, subject, and destination against it before accepting work. A key
expresses identity; authorization comes from that trusted application boundary.
Sending and delivery limit a message ID's canonical path to 1,024 UTF-8 bytes and a
subject to 256 UTF-8 bytes.

<a id="persist-a-result-not-just-a-flag"></a>

## Persist queryable results

A handler can stage a durable reply whose ID is the command's stable `result` child.
The [inventory recipe](messaging-recipes.md#reserve-inventory-once-per-order-line)
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
[payment recipe](messaging-recipes.md#charge-an-external-provider-with-a-stable-key)
exposes a result query by the original command ID.

## External side effects

An external payment, email, or HTTP operation has its own commit boundary. An
activation can fail after that operation succeeds and before its result is journaled.
The payment recipe calls its provider during local preparation with
`context.Message.Envelope.MessageId.ToString()`. The provider enforces request consistency
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
[projection recipe](messaging-recipes.md#converge-an-out-of-order-stock-projection)
demonstrates a complete-snapshot policy.
