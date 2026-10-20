---
title: "ORLEANS0225: Await durable tasks instead of polling them in durable task bodies"
description: Understand ORLEANS0225, which reports polls of scheduled tasks in durable task bodies, where they throw, and fix them by awaiting the task or waiting with a durable timeout.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0225: Await durable tasks instead of polling them in durable task bodies

| Property | Value |
| --- | --- |
| Severity | Error |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body, including a step, polls a scheduled task with `ScheduledTask.IsCompletedAsync`, `ScheduledTask.GetStatusAsync`, or `ScheduledTask.GetResponseAsync` with `PollingOptions`:

:::code source="snippets/durable-tasks/Orleans0225.cs" id="orleans0225_violation" language="csharp":::

## Rule description

A poll reports the task's status when the poll runs, and the execution records nothing about it. A replay re-runs the body at another time, when the poll observes another status. A body which branches on the status, such as the loop in the example, then schedules other operations than the run which the execution recorded, and the replay diverges.

The runtime throws `NotSupportedException` for a poll inside a durable task, so the body fails at its first poll.

An await, or a wait with a durable timeout, records its outcome, and every replay observes the same one. `ScheduledTask.GetResponseAsync` without `PollingOptions` waits for the terminal response like an await. Code outside durable task bodies, such as a client, polls.

For handles and timeouts, see [Combinators and handles](../grains/durable-tasks.md#combinators-and-handles).

## How to fix violations

**Await the task.** The body resumes when the task completes, and a replay returns the recorded result. A body which does other work first schedules the task and awaits its handle later:

:::code source="snippets/durable-tasks/Orleans0225.cs" id="orleans0225_fix_await" language="csharp":::

**Wait with a durable timeout.** `WaitAsync` with a timeout races the task with a durable delay. When the delay completes first, the task receives a cancellation request, and a task which ends canceled throws `TimeoutException`. A handle's `WaitAsync` with a timeout waits the same way, and `ScheduledTask.WhenAny` with a scheduled `DurableTask.Delay` returns whichever completes first:

:::code source="snippets/durable-tasks/Orleans0225.cs" id="orleans0225_fix_timeout" language="csharp":::

## When to suppress

Keep the diagnostic enabled: a poll in a durable task body throws `NotSupportedException`. Polls belong in code outside durable task bodies, such as a client or an ordinary grain method.
