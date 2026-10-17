---
title: "ORLEANS0116: Post durable tasks from grains or durable task bodies"
description: Understand ORLEANS0116, which reports code which posts a durable task where no durable outbox can stage the start, and fix it with ScheduleAsync or a post from a durable grain.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0116: Post durable tasks from grains or durable task bodies

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code outside a grain and outside a durable task body calls `Post` on a durable task definition or configuration, for example in a client or a web endpoint:

:::code source="snippets/durable-tasks/Orleans0116.cs" id="orleans0116_violation" language="csharp":::

## Rule description

`Post` is synchronous: it stages the start in the durable outbox of the grain which runs the calling code, and the start commits with that grain's next journal write, together with the state changes which the calling code made. A grain method, a durable task body, and a `DurableTask.Run` delegate run in a grain. A client has no durable outbox, so `Post` throws `InvalidOperationException` there, and the checkout fails.

For how tasks start, see [Starting and observing tasks](../grains/durable-tasks.md#starting-and-observing-tasks).

## How to fix violations

**Schedule the task with `ScheduleAsync`.** It completes once the task's grain has accepted and stored the task, and returns the task's handle. Name the task with `WithId`, so a retry after an ambiguous failure, such as a timeout, reaches the same task:

:::code source="snippets/durable-tasks/Orleans0116.cs" id="orleans0116_fix_schedule" language="csharp":::

**Post from a durable grain.** An ordinary method of a durable grain posts the start, which commits atomically with the grain's state changes in its next journal write:

:::code source="snippets/durable-tasks/Orleans0116.cs" id="orleans0116_fix_grain" language="csharp":::

## When to suppress

Suppress the diagnostic in a helper which only grains or durable task bodies call, such as a static method which a workflow uses to start follow-up work.
