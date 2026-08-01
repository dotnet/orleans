---
date: 2026-01-20
last_updated: 2026-08-01
branch: feature/durabletask/6
topic: "Durable tasks, collections, and messaging in Orleans"
tags: [research, orleans, durable-tasks, journaling, durable-messaging]
status: complete
---

# Durable tasks and collections in Orleans

## Overview

`Orleans.Journaling` provides journal-backed state machines which can be composed inside a grain and committed together using `WriteStateAsync`. The Workflows application demonstrates three related features:

1. **Durable tasks** for replay-safe, long-running workflows.
2. **Durable collections** for grain state represented as familiar collection types.
3. **Durable inbox/outbox messaging** for state changes which must reliably emit events.

The samples are under `playground/WorkflowsApp/WorkflowsApp.Service/Samples`.

## Durable collections

| Type | Typical use |
|---|---|
| `IDurableValue<T>` | Counters, settings, flags |
| `IDurableList<T>` | Ordered work or history |
| `IDurableDictionary<TKey, TValue>` | Keyed entities and indexes |
| `IDurableQueue<T>` | FIFO work |
| `IDurableSet<T>` | Unique membership and tags |
| `IDurableTaskCompletionSource<T>` | Durable external completion |

Collections are injected as keyed services. The key is the state-machine name in the grain journal:

```csharp
internal sealed class CounterGrain(
    [FromKeyedServices("count")] IDurableValue<long> count)
    : DurableGrain
{
    public async ValueTask<long> IncrementAsync()
    {
        count.Value++;
        await WriteStateAsync();
        return count.Value;
    }
}
```

Mutating a collection updates its in-memory representation and appends a pending journal operation. `WriteStateAsync` commits all registered state-machine operations. Recovery replays those operations (or a snapshot) before the grain accepts calls.

## Durable tasks

Methods returning `DurableTask<T>` are durable workflow methods, not ordinary asynchronous methods. Calls can be scheduled and observed:

```csharp
var scheduled = await grain.RunSample().ScheduleAsync("instance-id");
var result = await scheduled.WaitAsync();
```

Child operations can use stable IDs:

```csharp
var confirmation = await payment
    .ChargeCustomer(customerId, amount)
    .WithId("charge-payment");
```

On replay, completed child operations return their recorded result instead of repeating the side effect. Workflow contracts and aliases therefore must remain stable. In particular, the HelloWorld sample retains `IHelloWorkflowGrain`, `DurableTask` return types, and `ScheduleAsync`.

## Durable messaging

An `IDurableOutbox` is itself a registered state machine. Adding an envelope to the outbox before `WriteStateAsync` commits the business state and outbound event in the same journal write. Delivery starts only after that write succeeds. The receiver persists the envelope in its `IDurableInbox`, tracks `(SenderId, MessageId)`, and dispatches it to the first matching `IInboxHandler`.

The guarantees are:

- Business state and outbox insertion are atomic when they use the same state-machine manager write.
- Delivery is at least once.
- Inbox message-ID tracking makes retries safe within the configured deduplication window.
- Message ordering is not guaranteed.
- A handler's state changes and any messages it adds through `IInboxHandlerContext.Send` are committed together by inbox processing.
- Atomicity does not span the sender and receiver. Applications still need stable business idempotency keys.

`DurableEnvelopeBuilder(SerializerSessionPool, GrainId)` is available for grain code which starts an outbox flow outside an inbox handler. Inside a handler, prefer `context.CreateEnvelope()`.

## Inventory reservation correctness

`Samples/InventoryReservation/InventoryReservation.cs` applies the following rules:

1. Validate every SKU and require every quantity to be positive.
2. Aggregate duplicate SKUs before checking available stock or mutating it.
3. Store one durable result keyed by `orderId`.
4. On a repeated `orderId`, return the original reservation or repeat the original failure without changing stock or emitting another event.
5. Add either `InventoryReservedEvent` or `ReservationFailedEvent` to the outbox before the same `WriteStateAsync` which commits inventory and the order result.
6. Receive both event types through registered typed inbox handlers.
7. Key notifications by `orderId` as an additional business-level inbox guard.

An order ID is an idempotency key and must not be reused for a different logical order. Failed order IDs are also retained, so replenishing stock does not change the result of retrying the same order ID.

## Runnable Workflows application samples

`Program.cs` runs these samples after the silo starts:

| Sample | Feature |
|---|---|
| `HelloWorld` | Scheduled durable workflow and stable workflow contract |
| `SumOfSquares` | Parallel durable task composition |
| `Bank` | Durable transfer workflow |
| `CancelWorld` | Durable workflow cancellation |
| `Counter` | `IDurableValue<long>` |
| `TodoList` | `IDurableList<string>` |
| `MessageQueue` | `IDurableQueue<Message>` |
| `TagTracker` | `IDurableSet<string>` |
| `OrderSaga` | Multi-step durable task orchestration |
| `InventoryReservation` | Dictionary state plus durable outbox/inbox events |
| dictionary loop in `Program.cs` | Versioned `IDurableDictionary` access |

`HumanInTheLoop.ConfigureApp` also maps `/greet/{greeting}`. Its `RunAsync` method intentionally is not in the automatic sequence because it waits for an external HTTP action.

## Running

Start `WorkflowsApp.AppHost`. It provisions an Azurite container and supplies the `state` blob connection used by journal storage:

```powershell
dotnet run --project playground\WorkflowsApp\WorkflowsApp.AppHost
```

Running `WorkflowsApp.Service` directly requires an equivalent `state` blob resource configuration. The automatic sample sequence is intentionally stateful; some collection samples clear their own demo keys, while durable workflow instances use stable or explicit IDs.

## Validation

Focused inventory tests are in:

`playground/WorkflowsApp/WorkflowsApp.Service.Tests/InventoryReservationTests.cs`

They use `VolatileStateMachineStorageProvider`, so no Azure, Azurite, database, or other external service is required. The tests cover:

- duplicate-SKU aggregation;
- zero and negative quantity rejection;
- aggregate stock checks before mutation;
- `orderId` idempotency;
- successful and failed event delivery through the durable outbox/inbox handlers;
- one notification per logical order.

Run them with:

```powershell
dotnet test playground\WorkflowsApp\WorkflowsApp.Service.Tests\WorkflowsApp.Service.Tests.csproj --framework net10.0 -- -parallel none -noshadow
```

Build the runnable service with:

```powershell
dotnet build playground\WorkflowsApp\WorkflowsApp.Service\WorkflowsApp.Service.csproj
```

## Primary implementation references

- `src/Orleans.Journaling/DurableGrain.cs`
- `src/Orleans.Journaling/StateMachineManager.cs`
- `src/Orleans.Journaling/DurableDictionary.cs`
- `src/Orleans.Journaling/DurableList.cs`
- `src/Orleans.Journaling/DurableQueue.cs`
- `src/Orleans.Journaling/DurableSet.cs`
- `src/Orleans.Journaling/DurableValue.cs`
- `src/Orleans.Journaling/Messaging/DurableOutbox.cs`
- `src/Orleans.Journaling/Messaging/DurableInboxExtension.cs`
- `src/Orleans.Journaling/Messaging/IInboxHandler.cs`
- `src/Orleans.Journaling/Messaging/IInboxHandlerContext.cs`
