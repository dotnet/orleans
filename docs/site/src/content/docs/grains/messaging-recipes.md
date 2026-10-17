---
title: Messaging practical recipes
description: Implement stock reservation, idempotent payment, ordered projections, durable notification fan-out, and scheduled workflow steps.
ms.date: 10/10/2026
ms.topic: how-to
---

# Messaging practical recipes

These compiled examples use ordinary grains, named journaled state, and the
<xref:Orleans.Messaging.IMessagingGrain> capability. Configure
Journaling and Durable Jobs and call
<xref:Orleans.Hosting.MessagingExtensions.AddMessaging*> as shown in
[Messaging](messaging.md#deployment-requirements).
Use persistent shared providers for deployments; the in-memory configuration is
suited to local execution.

All handler examples finish validation and asynchronous preparation with local
values, then send typed messages, apply the business update, and call `Complete()`
without awaiting. The inbox persists that outcome. Ordinary grain methods await
their own journal write. Each grain registers typed handlers with
<xref:Orleans.Messaging.InboxExtensions.Register*> and injects the outbox directly.
Grains inject the application's `Messages` class, whose
<xref:Orleans.Messaging.MessageType`1> bindings pair each subject with its body type.

## Run the stock-reservation sample

The [Messaging sample](https://github.com/dotnet/orleans/tree/main/samples/Messaging)
runs an order grain and a stock grain in one localhost silo. Three independent
hierarchical command IDs exercise acceptance, insufficient stock, and invalid
quantity. For each command, the host arms an explicit journal-acknowledgement
observer before submission, awaits the typed reply's acknowledgement, and resubmits
the same immutable command. It verifies `Duplicate` admission for successful and
rejected commands, one handler execution per ID, unchanged stock for rejections,
and one stock decrement using acknowledged replies and state snapshots.

From the repository root:

```powershell
pwsh .\samples\Build-Samples.ps1 -SkipExternalAssets
dotnet run --project .\samples\Messaging\Messaging.csproj --configuration Release --no-build
```

The sample uses locally packed Orleans packages while these APIs await publication.
Its README also describes the self-contained copy-out workflow. Volatile journals
and in-memory jobs support local execution; configure persistent shared providers
for restart recovery.

## Reserve inventory once per order line

Use one inventory grain per tenant/SKU with `available-stock`. The sender derives
the envelope's command ID using
[OrderOperationKeys](messaging-idempotency.md#hierarchical-business-operation-keys),
and places the quantity and response destination in `ReserveStock`. The
[typed send helper](messaging.md#encode-ordinary-application-values) encodes the
record under the envelope's `inventory.reserve.v1` subject.

The inventory registers two typed handlers with
<xref:Orleans.Messaging.InboxExtensions.Register*>: `inventory.reserve.v1` decodes
`ReserveStock` for the reservation method, and `inventory.restock.v1` decodes
`Restock` for the stock-increment method. Each method receives its decoded record
and the handler context. The grain registers both in
<xref:Orleans.Grain.OnActivateAsync*>, after journal recovery has restored its state.

:::code source="../snippets/compiled/Grains/MessagingRecipes.cs" id="messaging_inventory" language="csharp":::

The reservation method computes its next stock and result locally. `SendReply`
encodes and stages the deterministic `result` reply, then the method applies stock
and completes synchronously. The first command commits that stock decrement, reply,
and inbox completion together. A repeat with the same command ID recognizes the
retained completion fact and preserves the original handler effects.
The serialized abstract `ReservationResult` derives from `OrderOutcome`;
sealed `ReservationAccepted` and `ReservationRejected` records distinguish the
valid business result types and carry the original quantity and remaining stock.
A nonpositive quantity sends `ReservationRejected` with `InvalidQuantity`;
a shortage sends it with `InsufficientStock`. Both complete with unchanged stock.
Invalid immutable reservation commands complete as durable business rejections.
The original outbox intent delivers the reply
within its configured delivery policy.

For a nonpositive `Restock` increment, the handler calls
<xref:Orleans.Messaging.IInboxHandlerContext.Fail*> with a clear reason
and returns before mutation. A positive increment computes the checked new stock
value before mutation, then commits that update with inbox completion. Checked
overflow still throws and follows the ordinary bounded processing retry policy.
Give each distinct
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

:::code source="../snippets/compiled/Grains/MessagingRecipes.cs" id="messaging_payment" language="csharp":::

The provider call precedes shared journaled changes. The asynchronous handler
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
See [External side effects](messaging-idempotency.md#external-side-effects)
for retention and reconciliation responsibilities.

## Converge an out-of-order stock projection

A dashboard or search projection can accept complete stock snapshots independently
of transport ordering. The authoritative inventory producer assigns a monotonically
increasing version for the same tenant/SKU.

:::code source="../snippets/compiled/Grains/MessagingRecipes.cs" id="messaging_projection" language="csharp":::

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

:::code source="../snippets/compiled/Grains/MessagingRecipes.cs" id="messaging_fanout" language="csharp":::

The campaign record and every outgoing intent are captured in the same sender
journal write. The typed batch
<xref:Orleans.Messaging.OutboxExtensions.Send*> helper encodes and stages each
recipient command in order. The campaign record is added after all sends succeed.
When a send fails, earlier intents stay staged, and a repeated submission uses the
same recipient command identities and content.
Each destination commits independently through the
[notification handler](messaging.md#deployment-requirements). Each envelope's
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

A coordinator that receives several subjects registers one typed handler per
subject. This example registers inventory and payment result handlers that share
one method and records outcomes by their deterministic reply IDs for progress and
result queries:

:::code source="../snippets/compiled/Grains/MessagingRecipes.cs" id="messaging_dispatcher" language="csharp":::

Each registration matches its subject ordinally and decodes the body with its
binding. Each handler completes in the same synchronous block as its state update.
Decoding failures and subjects without a matching handler follow the processing
retry and dead-letter policy.

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

## Schedule a workflow step

A grain schedules its own later step by sending itself a message with a `deliverAt`
time. This checkout confirms an order once its grace period elapses, unless the
buyer cancels first:

:::code source="../snippets/compiled/Grains/MessagingRecipes.cs" id="messaging_grace_period" language="csharp":::

The checkout's inbox accepts the `GracePeriodElapsed` message in the same journal
write that records the submission, so the scheduled step exists exactly when the
`Submitted` status does. Messaging wakes the grain when the message is due and
runs its handler with the inbox's retry policy. The handler acts on the current
status, so a cancelled checkout completes the message without changing state.
The message ID is a child of the submission's ID, and a redelivered submission
finds a recorded status and schedules nothing new.

## Choose an application migration pattern

Move existing application responsibilities into the handler and its typed payload
records, using the inbox/outbox commit boundary for outgoing intent:

| Existing application pattern | Messaging pattern |
| --- | --- |
| Request/reply grain method | Put the stable command ID and subject in the envelope and the response destination in its body; the handler stages a deterministic typed reply with completion. |
| Business update followed by a remote call | Stage the outgoing envelope with the business update; acknowledged outbox state drives delivery and retry. |
| External provider call | Prepare the provider outcome using the canonical command ID, then commit its local query state, reply, and inbox completion together. |
| Notification loop | Call typed `Send` for each stable recipient identity, record the campaign after successful staging, and await the journal write. |
| Related records and attachments | Put application records and `byte[]` attachments in one serializable message and use the typed send and handler helpers. |

Preserve command identity and recorded outcomes when moving application workflows.
Applications own subject contracts, reply routing, authorization, and ID construction.
See
[Application payload evolution](messaging-operations.md#evolve-application-payload-records)
for rolling application-record changes.

## Verify your handlers

Exercise same-ID resubmissions from several grains, conflicting pending request reuse,
distinct child commands, out-of-order arrival, and cancellation during local
preparation. Assert business state, query outcomes,
outgoing envelopes, and completion together. Test activation recovery and ambiguous
storage/provider outcomes against the actual providers used in deployment.

The executable documentation examples exercise hierarchical key isolation,
typed reservation acceptance, zero/negative-quantity rejections, shortages,
polymorphic result round trips, reply-stage failure without stock mutation,
permanent invalid-restock dead-lettering, checked restock overflow,
provider-success/local-cancellation retry,
out-of-order projections, typed multi-subject dispatch, and ordinary composite-message
round trips. Verify the original reply's actual journal acknowledgement and
the exact completion-retention boundary when testing end-to-end resubmission.
For runtime guarantees and operating controls, see
[Messaging operations](messaging-operations.md).
