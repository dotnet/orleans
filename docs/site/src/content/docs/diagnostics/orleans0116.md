---
title: "ORLEANS0116: Post durable tasks from grains or durable task bodies"
description: Understand ORLEANS0116, which reports code which posts a durable task where no durable outbox can stage the start.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0116: Post durable tasks from grains or durable task bodies

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code outside a grain and outside a durable task body calls `Post` on a durable task definition or configuration.

## Rule description

`Post` stages a start in the durable outbox of the grain which runs the calling code, and the start commits with that grain's next durable write. A grain method, a durable task body, and a `DurableTask.Run` delegate run in a grain. Other code, such as a client, has no durable outbox, so `Post` throws `InvalidOperationException` there.

For how tasks start, see [Starting and observing tasks](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#starting-and-observing-tasks).

## How to fix violations

Schedule the task with `ScheduleAsync`, which completes once the task's host has accepted the task. Use `WithId` so a retry after an ambiguous failure reaches the same task:

```csharp
var placement = await order.PlaceOrderAsync(request).WithId($"place-order-{orderId}").ScheduleAsync();
```

## When to suppress

Suppress the diagnostic in a helper which only grains or durable task bodies call, such as a static method which a workflow uses to start follow-up work.
