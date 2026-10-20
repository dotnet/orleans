---
title: Durable tasks (experimental)
description: Understand the experimental Orleans durable task programming model, which runs asynchronous workflows that survive deactivation, crashes, and redeployment by replaying their recorded operations.
ms.date: 10/19/2026
ms.topic: conceptual
---

# Durable tasks (experimental)

A durable task is an asynchronous operation whose progress survives grain deactivation, silo crashes, and redeployment. Its host records each operation which the task completes, such as a call to another grain, a delay, or a recorded effect. A task which didn't finish runs its body again from the start, and every recorded operation returns its recorded outcome, so the body resumes where it stopped. The programming model lives in the pre-release `Microsoft.Orleans.DurableTasks.Abstractions` package, in the `Orleans.DurableTasks` namespace, which also carries the [durable task analyzers](../diagnostics/index.md).

## Execution model

A method which returns `DurableTask` or `DurableTask<T>` defines a task, and the task runs when the caller awaits it, schedules it with `ScheduleAsync`, or posts it with `Post`. Each run of a task's body is an attempt, which runs on its grain activation's scheduler and reads its context from `DurableTask.Current`: its identifier, its logical time `UtcNow`, and its cancellation state.

- **Task cancellation** is durable. A caller requests it, for example with `CancelAsync` on the task's handle, and the host records the request together with the point at which each execution first observes it. The body observes it through `IsTaskCancellationRequested`, `TaskCancellationReason`, and `CancellationToken`, and every replay observes it at that same recorded point. The body decides the task's outcome, for example by compensating.
- **Attempt cancellation** ends the current attempt when its activation stops, when the task is aborted or deleted, or when its replay diverges from its record. Every durable operation of a canceled attempt throws `DurableTaskAttemptCanceledException`, the host discards the attempt's outcome, and a later attempt runs the task again when it still needs an outcome. `AttemptCancellationToken` is canceled only by attempt cancellation.

## Writing replay-safe workflows

A replay runs the body from the start, and each recorded operation returns its recorded outcome. A body therefore has three kinds of code:

- **Recorded operations** run once per execution: children such as durable grain calls, `async DurableTask` methods, and `DurableTask.Run` steps, delays, signal waits, detached and posted starts, and `DurableTask.RunInline` effects.
- **Replayed code** precedes the body's last recorded operation and runs again in every replay. It computes values from the method's arguments, recorded results, `DurableTask.Current.UtcNow`, and the cancellation state.
- **The tail** follows the body's last recorded operation and runs once, in the run which completes the body. The host commits the tail's state changes together with the task's result.

Replayed code changes grain state, reads state whose value decides later operations, and produces identifiers, random numbers, and clock values only inside recorded operations or in the tail. Each replay schedules the same operations, with the same identifiers and arguments, in the same order. `WithId` gives an operation a stable identifier, which loops, conditions, and deployments don't shift. A replay which schedules another operation under a recorded identifier diverges: the host cancels the attempt with `DurableTaskAttemptCancellationReason.Diverged` and keeps the task pending until code which matches the record replays it.

A body awaits the tasks which it observes, or waits for them with a durable timeout, and continues on its scheduler: durable operations which code starts after `ConfigureAwait(false)`, and polls such as `IsCompletedAsync`, throw.

## Recorded effects

`DurableTask.RunInline(name, effect)` runs a synchronous effect immediately and records its result or the exception which it threw. The record commits with the task's next journal write, together with the state changes which the effect made, and every replay returns the recorded outcome without running the effect. An effect returns every output which the body uses:

:::code source="../diagnostics/snippets/durable-tasks/Orleans0204.cs" id="orleans0204_fix" language="csharp":::

A name identifies an effect as `WithId` identifies a step, and `DurableTask.RunInline(effect)` takes the execution's next generated identifier. A `RunInline` call inside an effect runs as part of the enclosing effect. Every other durable operation inside an effect throws `InvalidOperationException`, which the effect records as its outcome.

## Combinators

`DurableTask.WhenAll`, `WhenAny`, `Race`, `WhenAllCompleted`, `ScheduleAll`, and `WhenEach` start their inputs as children of the awaiting execution, identified by their input indexes beneath the combinator's identifier, so concurrent inputs keep stable identifiers in any completion order. The host selects `WhenAny` and `Race` winners and the `WhenEach` order from its durable completion order, so every replay observes the same selection:

:::code source="../diagnostics/snippets/durable-tasks/Orleans0210.cs" id="orleans0210_fix_when_all" language="csharp":::

`WaitAsync(timeout)` races a task with a durable delay, whose due time a replay keeps, and requests the task's cancellation when the delay wins.

## Scheduled task handles

`ScheduleAsync` returns a `ScheduledTask` handle once the task's host has accepted and stored the task. Awaiting the handle returns the task's result, and combinators such as `ScheduledTask.WhenAll` and `ScheduledTask.WhenAny` wait for several handles. A handle caches the outcome which it observes, so `Status`, `Exception`, and `TryGetResult` inspect it synchronously. A durable task body observes each child which it schedules:

:::code source="../diagnostics/snippets/durable-tasks/Orleans0219.cs" id="orleans0219_fix_await" language="csharp":::

## Starting and observing tasks

The configuration of a task says what it is, and the verb which starts it says when the caller continues:

- `await task` runs the task and returns its result once the result is durable.
- `task.ScheduleAsync()` returns the task's handle once its host has accepted and stored the task.
- `task.Post()` stages the start with the caller's next durable write and returns the handle at once. The host delivers the start and the task's host deduplicates it by the task's identifier.

Within a durable execution, a task is an attached child, which the execution owns and whose outcome it observes. `.Shielded()` keeps the execution's cancellation from reaching the child. `.Detached()` starts a durable grain call as an independent root task, which continues after the execution completes, and `.Detached(rootId)` gives that root task an identifier which other parties know in advance. Outside a durable execution, every task is a root task, and `WithId` supplies its identifier, which makes a start safe to retry:

:::code source="../diagnostics/snippets/durable-tasks/Orleans0216.cs" id="orleans0216_fix_schedule" language="csharp":::

## Signals

`DurableSignal<T>` carries a value into a running task, such as an approval. Each signal name of a task holds the first value which a sender delivers, a value which arrives before the wait is buffered, and every wait for the name returns it, including waits during replay. `DurableSignal<T>.For(suffix)` derives a name for each occurrence of a repeated wait, and `TryWaitAsync(deadline)` waits until a durable deadline.

## Compensating canceled work

A body which observes task cancellation compensates for the work which it completed. A handler filtered by `DurableTask.Current.IsTaskCancellationRequested` runs for task cancellation and lets attempt cancellation end the attempt. A `.Shielded()` child runs to completion after the execution's cancellation, so compensation which runs as a child completes, and calls which cleanup makes receive `AttemptCancellationToken`:

:::code source="../diagnostics/snippets/durable-tasks/Orleans0212.cs" id="orleans0212_fix_filter" language="csharp":::

## Versioned changes

`DurableTask.GetVersion(name, minSupported, maxSupported)` returns the version of a change which the running execution follows, so one body holds the code of every version while executions which started on earlier code are pending. The first check of a name records the version: an execution which reaches the check first records `maxSupported`, and one which earlier code ran past records `0`. `DurableVersion` declares a change's name and range once. Raising `minSupported` retires a version, and an execution which recorded a retired version stays pending until code which supports it replays it:

:::code source="../diagnostics/snippets/durable-tasks/Orleans0218.cs" id="orleans0218_fix_descriptor" language="csharp":::

## Replay diagnostics and logging

`DurableTask.Current.IsReplaying` reports whether a run re-runs operations which an earlier run recorded. Use it for logging and diagnostics only: work which runs once belongs in a recorded effect, a step, or the tail. `logger.ReplaySafe()` returns a logger which skips events while the task replays, so each event is logged once:

:::code source="../diagnostics/snippets/durable-tasks/Orleans0215.cs" id="orleans0215_fix" language="csharp":::
