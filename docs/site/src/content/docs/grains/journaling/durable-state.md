---
title: Use durable state
description: Build an experimental Orleans Journaling grain with durable values and collections.
ms.date: 08/21/2026
ms.topic: how-to
---

# Use durable state

Install the pre-release [`Microsoft.Orleans.Journaling`](https://www.nuget.org/packages/Microsoft.Orleans.Journaling) package in the silo project. Install a [journal storage provider](configuration.md#choose-a-storage-provider) and configure it on the silos which host grains using durable state.

All Journaling APIs are experimental and carry diagnostic `ORLEANSEXP005`.

## Define a durable grain

Compose an ordinary <xref:Orleans.Grain> with an injected <xref:Orleans.Journaling.IJournaledStateManager> and durable states. Inject durable states with <xref:Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute>. The service key becomes the state's stable name in the grain journal:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingSnippets.cs" id="composed_shopping_cart":::

The standard manager enrolls itself in the grain lifecycle when constructed with the activation's <xref:Orleans.Runtime.IGrainContext>. Durable states register during construction, and recovery finishes before <xref:Orleans.Grain.OnActivateAsync*> and request processing. The same composition works with an application-owned grain base class.

The dictionary mutation is immediately visible to the current activation. Awaiting <xref:Orleans.Journaling.IJournaledStateManager.WriteStateAsync*> establishes the durability point for every pending durable-state mutation on that grain. Accept a <xref:System.Threading.CancellationToken> on grain operations and flow it through state-manager calls so cancellation follows the caller's operation lifetime.

<xref:Orleans.Journaling.DurableGrain> remains a convenience base exposing its protected <xref:Orleans.Journaling.DurableGrain.StateManager>, <xref:Orleans.Journaling.DurableGrain.GetOrCreateState*>, and <xref:Orleans.Journaling.DurableGrain.WriteStateAsync*> members. Choose that base when those helpers fit the application; constructor-injected composition gives existing grain hierarchies the same standard recovery behavior.

The composition example is compiled against repository source so it exercises constructor-owned lifecycle enrollment.

## Select a state type

| State type | In-memory API | Journaled operations |
| --- | --- | --- |
| <xref:Orleans.Journaling.IDurableValue`1> | One mutable value | Set |
| <xref:Orleans.Journaling.IDurableDictionary`2> | <xref:System.Collections.Generic.IDictionary`2> | Set, remove, clear, snapshot |
| <xref:Orleans.Journaling.IDurableList`1> | <xref:System.Collections.Generic.IList`1> plus `AddRange` | Add, insert, set, remove, clear, snapshot |
| <xref:Orleans.Journaling.IDurableQueue`1> | Queue operations | Enqueue, dequeue, clear, snapshot |
| <xref:Orleans.Journaling.IDurableSet`1> | <xref:System.Collections.Generic.ISet`1> | Add, remove, clear, snapshot |
| <xref:Orleans.Journaling.IDurableTaskCompletionSource`1> | Durable task completion | Complete, fault, or cancel |
| <xref:Orleans.Runtime.IPersistentState`1> | Record-style state | Set or clear a versioned state value |

All named states in one grain share the grain's journal and participate in the same write. This makes a single <xref:Orleans.Journaling.IJournaledStateManager.WriteStateAsync*> the atomic storage boundary for their pending changes. Coordination with another grain or an external service requires an application protocol such as idempotency, an inbox, or an outbox.

An <xref:Orleans.Journaling.IDurableTaskCompletionSource`1> changes status in memory when `TrySetResult`, `TrySetException`, or `TrySetCanceled` succeeds. Its `Task` completes after a write acknowledges that status or recovery replays it, allowing waiters to observe a durable completion.

## Keep state names and schemas stable

The keyed service name identifies a durable state across activations and deployments. Apply these rules:

- Keep each name unique within the grain.
- Preserve names when changing constructors or refactoring fields.
- Keep JSON key, value, and record schemas backward readable during rolling upgrades.
- Register every JSON payload type in the configured source-generated serializer context when trimming or using Native AOT.
- Retain removed state definitions through the [retirement grace period](runtime-behavior.md#retire-a-named-state) when a rollback can reintroduce them.

Registering two states with the same name fails activation. Registering a state after activation setup also fails because recovery has already assigned journal stream identities.

## Compose an activation-scoped feature

A reusable feature can own durable state independently of the grain's constructor and base class. Select the grain implementation once per grain type using <xref:Orleans.Metadata.GrainClassMap> in an <xref:Orleans.Runtime.IConfigureGrainTypeComponents> implementation, then register a shared setup action. This example selects the existing shopping-cart interface and records an activation count:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingSnippets.cs" id="journaled_feature":::

Register the feature as scoped and the configurator as singleton:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingSnippets.cs" id="journaled_feature_registration":::

The action resolves the feature from <xref:Orleans.Runtime.IGrainContext.ActivationServices> and enrolls its lifecycle participant. Resolving the feature's dependencies also constructs the standard state manager, which enrolls itself in the grain lifecycle. The feature uses a stage after <xref:Orleans.Runtime.GrainLifecycleStage.SetupState> and before <xref:Orleans.Runtime.GrainLifecycleStage.Activate> so it increments recovered state before application activation begins.

Keep one enrollment owner per participant. Setup actions are shared across concurrent activations; resolve activation-specific data from the supplied context and keep shared callbacks stateless. See [Shared activation setup](../grain-lifecycle.md#shared-activation-setup) and [Journaling activation and recovery](runtime-behavior.md#activation-and-recovery) for ordering and failure behavior.

## Use journal-backed persistent state

Journaling registers keyed <xref:Orleans.Runtime.IPersistentState`1> services. Its familiar `State`, `WriteStateAsync`, and `ClearStateAsync` members write through the same journal manager as the durable collections. `ReadStateAsync` completes from the already-recovered in-memory state because activation setup replayed the grain journal.

Use a unique keyed service name exactly as you would for another durable state. The `ETag` is the journal-backed state's recovered version and `RecordExists` indicates whether a stored value is present.

## Implement a custom journaled state

Advanced integrations can implement <xref:Orleans.Journaling.IJournaledState> and register it with <xref:Orleans.Journaling.IJournaledStateManager>. The implementation owns its operation codec, snapshot representation, replay logic, deep-copy behavior, and volatile bookkeeping.

An implementation runs on one logical grain thread. It applies mutations in memory, writes recoverable operations, and uses `OnWriteCompleted` for behavior that must follow storage acknowledgement. Its `Reset` and replay methods must rebuild all state after a failed write or activation recovery.
