---
title: Use durable state
description: Build an experimental Orleans Journaling grain with durable values and collections.
ms.date: 08/21/2026
ms.topic: how-to
---

# Use durable state

Install the pre-release [`Microsoft.Orleans.Journaling`](https://www.nuget.org/packages/Microsoft.Orleans.Journaling) package in the silo project. Install a [journal storage provider](configuration.md#choose-a-storage-provider) and configure it before activating a <xref:Orleans.Journaling.DurableGrain>.

All Journaling APIs are experimental and carry diagnostic `ORLEANSEXP005`.

## Define a durable grain

Inject durable states with <xref:Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute>. The service key becomes the state's stable name in the grain journal:

:::code language="csharp" source="./snippets/journaling/JournalingBasics.cs" id="durable_shopping_cart":::

The dictionary mutation is immediately visible to the current activation. Awaiting <xref:Orleans.Journaling.DurableGrain.WriteStateAsync*> establishes the durability point for every pending durable-state mutation on that grain.

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

All named states in one grain share the grain's journal and participate in the same write. This makes a single <xref:Orleans.Journaling.DurableGrain.WriteStateAsync*> the atomic storage boundary for their pending changes. Coordination with another grain or an external service requires an application protocol such as idempotency, an inbox, or an outbox.

An <xref:Orleans.Journaling.IDurableTaskCompletionSource`1> changes status in memory when `TrySetResult`, `TrySetException`, or `TrySetCanceled` succeeds. Its `Task` completes after a write acknowledges that status or recovery replays it, allowing waiters to observe a durable completion.

## Keep state names and schemas stable

The keyed service name identifies a durable state across activations and deployments. Apply these rules:

- Keep each name unique within the grain.
- Preserve names when changing constructors or refactoring fields.
- Keep JSON key, value, and record schemas backward readable during rolling upgrades.
- Register every JSON payload type in the configured source-generated serializer context when trimming or using Native AOT.
- Retain removed state definitions through the [retirement grace period](runtime-behavior.md#retire-a-named-state) when a rollback can reintroduce them.

Registering two states with the same name fails activation. Registering a state after activation setup also fails because recovery has already assigned journal stream identities.

## Use journal-backed persistent state

Journaling registers keyed <xref:Orleans.Runtime.IPersistentState`1> services. Its familiar `State`, `WriteStateAsync`, and `ClearStateAsync` members write through the same journal manager as the durable collections. `ReadStateAsync` completes from the already-recovered in-memory state because activation setup replayed the grain journal.

Use a unique keyed service name exactly as you would for another durable state. The `ETag` is the journal-backed state's recovered version and `RecordExists` indicates whether a stored value is present.

## Implement a custom journaled state

Advanced integrations can implement <xref:Orleans.Journaling.IJournaledState> and register it with <xref:Orleans.Journaling.IJournaledStateManager>. The implementation owns its operation codec, snapshot representation, replay logic, deep-copy behavior, and volatile bookkeeping.

An implementation runs on one logical grain thread. It applies mutations in memory, writes recoverable operations, and uses `OnWriteCompleted` for behavior that must follow storage acknowledgement. Its `Reset` and replay methods must rebuild all state after a failed write or activation recovery.
