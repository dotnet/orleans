---
title: "ORLEANS0105: Await, schedule, or post durable task definitions"
description: Understand ORLEANS0105, which reports a durable task definition which code creates and never runs, and fix it by awaiting, scheduling, or posting it.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0105: Await, schedule, or post durable task definitions

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code calls a method which returns `DurableTask` or `DurableTask<TResult>`, or configures the result with `WithId`, `Detached`, or `Shielded`, and discards the value without awaiting, scheduling, or posting it:

:::code source="snippets/durable-tasks/Orleans0105.cs" id="orleans0105_violation" language="csharp":::

The C# compiler also reports CS4014 for some of these statements in `async` methods. ORLEANS0105 also covers definitions which are assigned to a discard, `_ = SendReceipt(customer);`, and configurations, such as `SendReceipt(customer).WithId("receipt");`.

## Rule description

A durable task is cold: calling its method creates a definition, and configuring the definition returns a configuration of it. Neither starts the task. The task runs when the definition is awaited, scheduled with `ScheduleAsync`, or started with `Post`. A discarded definition never runs, so the receipt in the example is never sent, and no error reports it.

For the ways to start a task, see [Starting and observing tasks](../grains/durable-tasks.md#starting-and-observing-tasks).

## How to fix violations

**Await the definition** to run the task and continue with its result:

:::code source="snippets/durable-tasks/Orleans0105.cs" id="orleans0105_fix_await" language="csharp":::

**Schedule it and await the handle later** to run it concurrently with other work. `ScheduleAsync` returns the task's handle once its host has accepted it:

:::code source="snippets/durable-tasks/Orleans0105.cs" id="orleans0105_fix_schedule" language="csharp":::

**Post it** to stage the start with the execution's next journal write, without an await. `Post` returns the handle, which the execution can await later:

:::code source="snippets/durable-tasks/Orleans0105.cs" id="orleans0105_fix_post" language="csharp":::

A durable grain call which the starter doesn't wait for runs as a detached task, `.Detached().Post()`, which runs independently of its starter.

## When to suppress

Suppress the diagnostic where a definition is passed to code which starts it later, through a path which the analyzer doesn't follow.
