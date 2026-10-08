---
title: Durable messaging practical recipes
description: Implement stock reservation, idempotent payment, ordered projections, and durable notification fan-out.
ms.date: 10/08/2026
ms.topic: how-to
---

# Durable messaging practical recipes

These compiled examples use ordinary grains, named journaled state, and the
<xref:Orleans.DurableMessaging.IDurableMessagingGrain> capability. Configure
Journaling and Durable Jobs and call
<xref:Orleans.Hosting.DurableMessagingExtensions.AddDurableMessaging*> as shown in
[Durable messaging](durable-messaging.md#deployment-requirements).
Use persistent shared providers for deployments; the in-memory configuration is
suited to local execution.

All handler examples finish local validation, asynchronous preparation, and envelope
construction before the first shared mutation. Their final shared update, sends,
`Complete()`, and method return run synchronously. The inbox owns their journal
write. Ordinary methods explicitly await their application's journal write. Register one
non-generic handler per inbox and inject the outbox directly; record types identify
application message kinds.

## Reserve inventory once per order line

Use one inventory grain per tenant/SKU, with `available-stock` and a durable
`reservations` outcome ledger. The sender derives a leaf operation key using
[OrderOperationKeys](durable-messaging-idempotency.md#hierarchical-business-operation-keys),
includes it and the response destination in the typed `ReserveStock` application
record. The [application-local codec](durable-messaging.md#encode-ordinary-application-values)
encodes that record into the envelope's immutable raw payload.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_inventory" language="csharp":::

The first request commits a stock decrement, its reservation decision, the reply,
and inbox completion together. A request with a fresh transport message ID and the
same business key returns the saved decision. A shortage records `Reserved = false`
and leaves stock unchanged. That rejection is a normal completed business outcome.

The example's `SetAvailableAsync` is an administrative absolute-stock update. In
an order workflow, add explicit confirmation, expiry, and release policies for held
reservations. Give release its own leaf, for example
`{order}/inventory/{sku}/release`, and commit its outcome with stock restoration.
Authorize stock administration and derive the tenant/SKU grain identity at the
application's trusted entry point.

## Charge an external provider with a stable key

Register an <xref:Microsoft.Extensions.DependencyInjection.IServiceCollection>
implementation of the shown gateway contract. Its adapter maps the key to the
provider's idempotency header or request field and implements the provider's outcome
reconciliation protocol.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_payment" language="csharp":::

The provider call precedes shared journaled changes. If it succeeds and activation
loss interrupts the local commit, a replacement attempt uses the same provider key.
If the payment result is already journaled, a fresh-envelope duplicate bypasses the
gateway and resends the stored outcome. The reply and the result ledger share the
inbox completion write.

Normalize currency and scope the operation key to the provider account and tenant.
Treat declined payments as completed outcomes; treat ambiguous provider responses
using the provider's same-key retry or query contract. Expose a status query, as this
grain does, so a caller can reconcile an uncertain response with the recorded result.
See [External side effects](durable-messaging-idempotency.md#external-side-effects)
for retention and reconciliation responsibilities.

## Converge an out-of-order stock projection

A dashboard or search projection can accept complete stock snapshots independently
of transport ordering. The authoritative inventory producer assigns a monotonically
increasing version for the same tenant/SKU.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_projection" language="csharp":::

If version 12 arrives before version 11, the projection retains version 12 and
completes version 11 as superseded. A repeated version binds to its original content.
The version and complete stock value are one durable record.

This recipe applies to **complete replacement snapshots**. Incremental debit, credit,
or stock-delta events use a contiguous-sequence policy with a journaled gap buffer
and a domain-defined recovery path for missing events.

## Publish a durable notification campaign

An ordinary method can stage several destinations with one business update. Give the
campaign a stable ID so a repeated client submission finds its recorded content and
recipient set.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_fanout" language="csharp":::

The campaign record and every outgoing intent are captured in the same sender
journal write. Each destination commits independently through the
[notification handler](durable-messaging.md#deployment-requirements). Operation
keys in the `Notify` payload identify each recipient under the campaign root. The
recipient's durable notification ledger prevents a fresh-ID business duplicate
from incrementing the count twice, while retaining the original text. A successful publish response
means the campaign and intents are durable; recipient completion is a later event.

Bound the recipient count and payload size at your ingress according to journal
capacity and latency budgets. For larger campaigns, persist a campaign cursor and
emit bounded recipient chunks, committing the cursor and each chunk together.
If delivery confirmation is needed, have recipients reply with a keyed outcome and
let the campaign grain aggregate those results. Retain the campaign record for the
client-retry horizon, and use receiver-side business ledgers when recipients need
idempotency beyond the inbox's transport window.

## Combine the recipes into an order workflow

Use one dispatcher when the coordinator receives several application message kinds.
This example records inventory and payment outcomes under their leaf keys; duplicate
responses with fresh envelope IDs reuse the recorded outcome:

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_dispatcher" language="csharp":::

A per-order coordinator persists its expected step keys and phase, sends
inventory and payment requests, then completes the initiating command. Each
response records its outcome and completes in one local commit. Once all required
steps have succeeded, one transition emits `shipment/create`.

| Phase | Durable transition |
| --- | --- |
| Submitted | Save the expected leaf keys and emit stock and payment requests. |
| Waiting | Save each correlated participant result; repeated results reuse the existing step outcome. |
| Ready | Record the ready phase and emit the separately keyed shipment request. |
| Rejected | Record the business rejection and emit release/refund commands for completed participant steps. |
| Finished | Record shipment or compensation outcomes and retain the operation ledger for replay. |

Each transition uses the same prepare-then-synchronous-final-block pattern. Keep
business rejection in the workflow state and reserve exception retry policy for
preparation failures. Use the step ledgers to answer status queries during outages.

## Verify your handlers

Exercise fresh-message duplicates, conflicting request reuse, out-of-order arrival,
and cancellation during local preparation. Assert business state, cached outcomes,
outgoing envelopes, and completion together. Test activation recovery and ambiguous
storage/provider outcomes against the actual providers used in deployment.

The executable documentation examples in `DurableMessagingRecipeTests` cover
hierarchical key isolation, repeated reservations, recorded shortages, conflicting
requests, provider-success/local-cancellation retry, out-of-order projections, and a single
dispatcher receiving both response kinds. `DurableMessagingSnippetTests` also checks
notification-ledger deduplication and independently decoded package entries.
For runtime guarantees and operating controls, see
[Durable messaging operations](durable-messaging-operations.md).
