---
title: Durable messaging practical recipes
description: Implement stock reservation, idempotent payment, ordered projections, and durable notification fan-out.
ms.date: 10/09/2026
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

All handler examples finish local validation and asynchronous preparation before
the first shared mutation. Typed sends encode before staging, followed by the
prepared business update. Their outgoing staging, shared update,
`Complete()`, and method return run synchronously. The inbox owns their journal
write. Ordinary methods explicitly await their application's journal write. Register one
non-generic handler per inbox and inject the outbox directly. Exact subjects identify
protocol operations; keyed <xref:Orleans.DurableMessaging.DurableMessageType`1>
bindings select their ordinary serializers. Typed outbox `Send` and `SendReply`
use private pooled encoders and release temporary envelopes internally after
staging retains their payload pins. Handler context payloads are borrowed.
Bulk preparation and raw/package protocols retain explicit local ownership.

## Run the stock-reservation sample

The [Durable Messaging sample](https://github.com/dotnet/orleans/tree/main/samples/DurableMessaging)
runs an order grain and a stock grain in one localhost silo. Two submissions use
the same hierarchical command ID. The host observes the original reply's journal
acknowledgement, explicitly resubmits the same command, and verifies `Duplicate`
admission, one handler execution, and one stock decrement.

From the repository root:

```powershell
pwsh .\samples\Build-Samples.ps1 -SkipExternalAssets
dotnet run --project .\samples\DurableMessaging\DurableMessaging.csproj --configuration Release --no-build
```

The sample uses locally packed Orleans packages while these APIs await publication.
Its README also describes the self-contained copy-out workflow. Volatile journals
and in-memory jobs support local execution; configure persistent shared providers
for restart recovery.

## Reserve inventory once per order line

Use one inventory grain per tenant/SKU with `available-stock`. The sender derives
the envelope's command ID using
[OrderOperationKeys](durable-messaging-idempotency.md#hierarchical-business-operation-keys),
and places the quantity and response destination in `ReserveStock`. The
[typed send helper](durable-messaging.md#encode-ordinary-application-values) encodes the
record under `inventory.reserve.v1` into an owning, read-only Arc slice.

The inventory registers typed methods through
<xref:Orleans.DurableMessaging.DurableInboxExtensions.RegisterHandlers*>.
`inventory.reserve.v1` selects `ReserveStock` and its reservation method;
`inventory.restock.v1` selects `Restock` and its stock-increment method. One
dispatcher performs exact subject lookup and typed decoding, so each method
receives its application record directly. Each registration passes the grain as
state and uses a static delegate. Synchronous dispatch checks cancellation at the
boundary before entering these methods.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_inventory" language="csharp":::

The reservation method computes its next stock and result locally. `SendReply`
encodes and stages the deterministic `result` reply, then the method applies stock
and completes synchronously. The first command commits that stock decrement, reply,
and inbox completion together. A repeat with the same command ID recognizes the
retained completion fact and preserves the original handler effects. A shortage
sends `Reserved = false` and completes with unchanged stock. That rejection is a
normal completed business outcome. The original outbox intent delivers the reply
within its configured delivery policy.

`Restock` validates a positive increment and computes the checked new stock value
before mutation, then commits that update with inbox completion. Give each distinct
restocking operation its own stable command ID. The example's `SetAvailableAsync`
is an administrative absolute-stock update. In
an order workflow, add explicit confirmation, expiry, and release policies for held
reservations and keep a record indexed by the reservation command ID when those
operations need lookup. Give release its own leaf, for example
`{order}/inventory/{sku}/release`, and commit its outcome with stock restoration.
Authorize stock administration and derive the tenant/SKU grain identity at the
application's trusted entry point.

## Charge an external provider with a stable key

Register an <xref:Microsoft.Extensions.DependencyInjection.IServiceCollection>
implementation of the shown gateway contract. Its adapter maps the key to the
provider's idempotency header or request field and implements the provider's outcome
reconciliation protocol.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_payment" language="csharp":::

The provider call precedes shared journaled changes. The static asynchronous route
passes its token to the gateway, and the method checks cancellation after that
await before `SendReply` and the final result update. If the provider succeeds and activation
loss interrupts the local commit, a replacement attempt uses the same provider key.
The canonical envelope ID is also the provider idempotency key. After local
completion, the inbox recognizes a resubmission during retention. The result-query
state, deterministic reply, and inbox completion share one write.

Normalize currency and scope the command ID to the provider account and tenant.
Treat declined payments as completed outcomes; treat ambiguous provider responses
using the provider's same-key retry or query contract. Expose a status query, as this
grain does, so a caller can reconcile an uncertain response by the same command ID.
Retain provider idempotency facts for the full retry horizon; adapt key length with
a deterministic canonical-key hash when the provider requires it.
See [External side effects](durable-messaging-idempotency.md#external-side-effects)
for retention and reconciliation responsibilities.

## Converge an out-of-order stock projection

A dashboard or search projection can accept complete stock snapshots independently
of transport ordering. The authoritative inventory producer assigns a monotonically
increasing version for the same tenant/SKU.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_projection" language="csharp":::

If version 12 arrives before version 11, the projection retains version 12 and
completes version 11 as superseded. A repeated version binds to its original content.
The version and complete stock value are one durable record. A stable message ID
recognizes the completed snapshot command; the domain version orders different
snapshot commands.

This recipe applies to **complete replacement snapshots**. Incremental debit, credit,
or stock-delta events use a contiguous-sequence policy with a journaled gap buffer
and a domain-defined recovery path for missing events.

## Publish a durable notification campaign

An ordinary method can stage several destinations with one business update. Give the
campaign a stable ID so a repeated client submission finds its recorded content and
recipient set.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_fanout" language="csharp":::

The campaign record and every outgoing intent are captured in the same sender
journal write. Fan-out uses the type binding's owning `Create` method to prepare
all envelopes locally before changing shared state,
then disposes every local owner in `finally`, including partial preparation on failure.
The outbox keeps its independently retained pins through acknowledgement and delivery.
Each destination commits independently through the
[notification handler](durable-messaging.md#deployment-requirements). Each envelope's
ID is a stable recipient child under the campaign root, with literal grain identity
escaping handled by the key factory. The recipient's retained inbox completion
record recognizes that notification command. The campaign record also supports
`GetCampaignAsync` queries. A successful publish response
means the campaign and intents are durable; recipient completion is a later event.

Bound the recipient count and payload size at your ingress according to journal
capacity and latency budgets. For larger campaigns, persist a campaign cursor and
emit bounded recipient chunks, committing the cursor and each chunk together.
If delivery confirmation is needed, have recipients reply with a keyed outcome and
let the campaign grain aggregate those results. Retain campaign state for its
submission/query horizon and receiver completion facts for the supported
resubmission horizon.

## Combine the recipes into an order workflow

Use <xref:Orleans.DurableMessaging.DurableInboxDispatcher> when the coordinator
receives several subjects. This example registers typed inventory and payment
result delegates once and records outcomes by their deterministic reply IDs for
progress and result queries:

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_dispatcher" language="csharp":::

The dispatcher matches subjects ordinally and invokes typed handler objects
containing each binding, static delegate, and grain state argument. Configuration
freezes the routes before installation. Asynchronous routes return their actual
handler `ValueTask`.
Each delegate explicitly completes in the same synchronous block as its shared
mutation and actual method return. Unknown subjects and decode failures enter
processing retry/dead-letter policy before mutation.

A per-order coordinator persists its expected step keys and phase, sends
inventory and payment requests, then completes the initiating command. Each
response records its outcome and completes in one local commit. Once all required
steps have succeeded, one transition emits `shipment/create`.

| Phase | Durable transition |
| --- | --- |
| Submitted | Save the expected leaf keys and emit stock and payment requests. |
| Waiting | Save each correlated participant result under its reply ID; the inbox recognizes repeated reply commands. |
| Ready | Record the ready phase and emit the separately keyed shipment request. |
| Rejected | Record the business rejection and emit release/refund commands for completed participant steps. |
| Finished | Record shipment or compensation outcomes and retain workflow state for result queries and audit. |

Each transition uses the same prepare-then-synchronous-final-block pattern. Keep
business rejection in the workflow state and reserve exception retry policy for
preparation failures. Use recorded step outcomes to answer status queries during outages.

## Choose an application migration pattern

Move existing application responsibilities into the handler and its typed payload
records, using the durable inbox/outbox commit boundary for outgoing intent:

| Existing application pattern | Durable messaging pattern |
| --- | --- |
| Request/reply grain method | Put the stable command ID and subject in the envelope and the response destination in its body; the handler stages a deterministic typed reply with completion. |
| Business update followed by a remote call | Stage the outgoing envelope with the business update; acknowledged outbox state drives delivery and retry. |
| External provider call | Prepare the provider outcome using the canonical command ID, then commit its local query state, reply, and inbox completion together. |
| Notification loop | Prepare a bounded batch using the type binding's owning `Create` method, stage all intents with the campaign record, and release local pins before awaiting the write. |
| Multiple encoded attachments | Build one disposable keyed package; decode only needed borrowed entries while retaining its owner. |

Preserve command identity and recorded outcomes when moving application workflows.
Typed subject bindings and outbox send/reply helpers carry ordinary records in Arc payloads.
Applications own subject contracts, reply routing, authorization, and ID construction.
Choose explicit retain/release at each ownership boundary. See
[Application payload evolution](durable-messaging-operations.md#evolve-application-payload-records)
for rolling application-record changes.

## Verify your handlers

Exercise same-ID resubmissions across senders, conflicting pending request reuse,
distinct child commands, out-of-order arrival, and cancellation during local
preparation. Assert business state, query outcomes,
outgoing envelopes, and completion together. Test activation recovery and ambiguous
storage/provider outcomes against the actual providers used in deployment.

The executable documentation examples exercise hierarchical key isolation,
reservation completion, shortages, provider-success/local-cancellation retry,
out-of-order projections, typed multi-subject dispatch, and independently decoded
package entries. Verify the original reply's actual journal acknowledgement and
the exact completion-retention boundary when testing end-to-end resubmission.
For runtime guarantees and operating controls, see
[Durable messaging operations](durable-messaging-operations.md).
