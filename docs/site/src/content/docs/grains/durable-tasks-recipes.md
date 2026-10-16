---
title: Durable task recipes
description: Fan out, race, time out, compensate, settle every input, process completions in order, wait for signals, start detached work, cancel, abort, and schedule durable tasks.
ms.date: 10/14/2026
ms.topic: how-to
---

# Durable task recipes

These compiled examples use grains which derive from
<xref:Orleans.Journaling.DurableGrain> and the contracts from
[Durable tasks](durable-tasks.md#define-durable-task-methods). Configure Journaling,
Durable Jobs, and the durable task runtime as shown in
[Configure the silo and clients](durable-tasks.md#configure-the-silo-and-clients).
Each recipe verifies its outcome with the task list which
[diagnostics](durable-tasks-operations.md#list-a-grains-tasks) report for a grain.

## Fan out to every line with a named WhenAll

**Task:** reserve stock for every order line concurrently, and continue with each
line's result in line order.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_fan_out" language="csharp":::

<xref:Orleans.DurableTasks.DurableTask.WhenAll*?displayProperty=nameWithType> schedules
one reservation per line in one batch, which the order grain commits with one journal
write, and returns a `bool[]` in input order. It waits for every reservation, including
after a cancellation request, so the `catch` block runs when no reservation is still
running. `WithId("reserve")` identifies the inputs as `reserve/0`, `reserve/1`, and so
on.

Pitfalls:

- Build the inputs in a deterministic order, such as the order's line order. The input
  index is part of each input's task ID.
- Name the combinator. An unnamed combinator's generated ID counts the combinators which
  ran before it, so adding one earlier in a later deployment renumbers its inputs.
- `WhenAll` throws the first failure in input order, then the first cancellation. To act
  on each input's own outcome, use
  [`WhenAllCompleted`](#settle-every-input-and-inspect-each-outcome).

**Verify:** the order grain lists `place-order-{orderId}/reserve/0` through
`reserve/{n-1}`, each `Completed` with its `True` or `False` result, and each catalog
grain lists a task with the same ID.

## Take the first result and cancel the rest with Race

**Task:** take the first shipping quote from two carriers, and cancel the other
carrier's request.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_race" language="csharp":::

<xref:Orleans.DurableTasks.DurableTask.Race*?displayProperty=nameWithType> schedules the
requests as `first-quote/0` and `first-quote/1` and selects the one which completed
first. The grain stamps each input's result with a durable completion sequence, so a
replay selects the same winner even when the other request has completed since. `Race`
then requests cancellation of the other request with the reason
`Another task won the race.` and waits until that request is terminal, so the workflow
continues only after both carrier calls have settled. `Race<TResult>` returns the
winner's index and result.

Pitfalls:

- A winner which failed or was canceled fails `Race`. Return a value, such as `null`,
  from inputs whose failure is an expected outcome.
- Cancellation is cooperative. A carrier which ignores the request keeps the race
  waiting until its call completes, and the race discards that call's result.
- To leave the other inputs running, use
  <xref:Orleans.DurableTasks.DurableTask.WhenAny*?displayProperty=nameWithType>, whose
  other inputs run until they complete or the runtime deletes the task's execution tree.

**Verify:** while the slower request settles, the shipping grain lists it as
`Canceling` with the reason `Another task won the race.`; once `QuoteFirstAsync`
returns, `first-quote/0` and `first-quote/1` are both terminal.

## Time out a call with WaitAsync

**Task:** request a quote from the ground carrier, and give up after two minutes.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_timeout" language="csharp":::

<xref:Orleans.DurableTasks.DurableTaskExtensions.WaitAsync*?displayProperty=nameWithType>
races the call, `ground-quote/0`, with a durable delay, `ground-quote/1`. The runtime
records the delay's due time, so a replay after a deactivation keeps the original
deadline: the timeout counts from the first run. When the delay elapses first, the call
receives a cancellation request with the reason
`The operation timed out after 00:02:00.`, and `WaitAsync` waits for the carrier's
outcome. A call which ends canceled throws <xref:System.TimeoutException>, whose inner
exception is the call's `OperationCanceledException`; a quote which the carrier returns
anyway is the result. When the call completes first, `WaitAsync` cancels the delay and
returns the call's result or throws its failure.
<xref:Orleans.DurableTasks.DurableTaskExtensions.WaitUntilAsync*> takes an absolute
deadline instead.

Pitfalls:

- Compute absolute deadlines from
  <xref:Orleans.DurableTasks.DurableTaskContext.UtcNow?displayProperty=nameWithType>, so
  a replay computes the same deadline.
- Catch `TimeoutException` to handle the timeout. An `OperationCanceledException`
  reports another cancellation, such as the workflow's own.
- `Race(...).WaitAsync(timeout).WithId(name)` bounds a whole race, which then runs as the
  child `{name}/0` with its inputs beneath it.
- For a task which is already scheduled,
  <xref:Orleans.DurableTasks.ScheduledTask.WaitAsync*?displayProperty=nameWithType> with a
  timeout applies the same rules. Outside a durable task, such as in a client, it
  measures the timeout on the system clock.

**Verify:** the shipping grain lists `ground-quote/1` as `Delayed` until its due time.
After a timeout, `ground-quote/0` reports the reason
`The operation timed out after 00:02:00.`, and the workflow returns `null` once the
carrier's request ends canceled.

## Compensate inline when the workflow is canceled

**Task:** when the buyer cancels the order during validation, release every reservation
and record why the order was canceled.

The `catch` block in [the fan-out recipe](#fan-out-to-every-line-with-a-named-whenall)
filters on `IsTaskCancellationRequested`, so it handles the workflow's own task
cancellation, and passes
<xref:Orleans.DurableTasks.DurableTaskContext.TaskCancellationReason?displayProperty=nameWithType>
to the compensation:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_compensation" language="csharp":::

Children which a canceled task starts terminate as canceled without running, unless the
task starts them with
<xref:Orleans.DurableTasks.DurableTaskExtensions.Shielded*>. The shielded `release`
combinator shields each of its inputs, so the inputs and the `cancel` effect run to
completion, and the
workflow returns an `OrderResult` which records the reason. A reservation which never
ran leaves nothing to release, so `ReleaseAsync` returns whether the order held a
reservation, and releasing every line is safe:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_catalog_grain" language="csharp":::

Pitfalls:

- Shield every compensating child. A child which isn't shielded terminates as canceled
  without running.
- Make compensations idempotent and report whether they acted, so the workflow can
  compensate without knowing which steps ran.
- An abort runs no compensation. Abort is for tasks whose compensation is stuck.
- Work which must outlive the workflow, such as cleanup after the workflow's result is
  delivered, belongs in a [detached task](#start-follow-up-work-without-waiting).

**Verify:** after `CancelAsync` during the grace period, the order grain lists
`grace-period` as `Canceled`, `release/{n}` and `cancel` as `Completed`, and the root
task as `Completed` with an `OrderResult` whose description is `Cancelled by the buyer.`.

## Settle every input and inspect each outcome

**Task:** reserve every line, and when any reservation fails, is rejected, or is
canceled, release exactly the lines whose reservation succeeded.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_when_all_completed" language="csharp":::

<xref:Orleans.DurableTasks.DurableTask.WhenAllCompleted*?displayProperty=nameWithType>
schedules every input, waits until each is terminal, and returns their handles in input
order. An input's failure or cancellation leaves the combinator successful, including
after the workflow's own cancellation request, which reaches the inputs. Each handle has
observed its outcome, so
<xref:Orleans.DurableTasks.ScheduledTask`1.TryGetResult*> returns `true` with the result
of a reservation which completed successfully, and
<xref:Orleans.DurableTasks.ScheduledTask.Status>,
<xref:Orleans.DurableTasks.ScheduledTask.Exception>, and
<xref:Orleans.DurableTasks.ScheduledTask`1.GetResult*> inspect the others. The workflow
releases the held lines with a shielded combinator, so the release also runs when the
workflow was canceled, then rethrows the cancellation so the task ends as canceled.

Pitfalls:

- Branch on each handle's outcome, not on the combined result: the combinator never
  throws for an input.
- Shield the release whenever the workflow can be canceled while it compensates.
- `WhenAllCompleted` runs only when awaited inside a durable execution. For handles which
  `ScheduleAll` returned earlier,
  <xref:Orleans.DurableTasks.ScheduledTask.WhenAllCompleted*?displayProperty=nameWithType>
  waits the same way.

**Verify:** with one line out of stock, the allocation grain lists `reserve/{i}` with
`True` and `False` results, a `release/{n}` input for each `True` line only, and the
catalog grains' available stock returns to its starting value.

## Process results as they arrive with WhenEach

**Task:** request quotes from several carriers, keep the first `count` quotes as they
arrive, and cancel the remaining requests.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_when_each" language="csharp":::

<xref:Orleans.DurableTasks.DurableTask.ScheduleAll*?displayProperty=nameWithType>
schedules the requests as `quotes/0`, `quotes/1`, and so on, and returns their handles.
<xref:Orleans.DurableTasks.ScheduledTask.WhenEach*?displayProperty=nameWithType> yields
each handle once, in completion order, including failed and canceled requests. The
runtime selects each next handle by the durable completion sequence of its result, so
every replay yields the same order and runs the same steps, including a replay in which
more requests have completed. After the loop,
<xref:Orleans.DurableTasks.ScheduledTask.CancelAsync*?displayProperty=nameWithType>
cancels each request with a reason; canceling a request which already completed changes
nothing.

Pitfalls:

- Base decisions on the handles which the enumeration yields. A handle which it hasn't
  yielded can report a different `IsCompleted` value during a replay.
- `DurableTask.WhenEach` accepts no `WithId`; to name the inputs, await
  `ScheduleAll(...).WithId(name)` and enumerate `ScheduledTask.WhenEach(handles)`.
- Requests which the enumeration doesn't reach keep running until they complete, are
  canceled, or the task's execution tree is deleted.

**Verify:** the shipping grain lists `quotes/{i}` for every carrier. Each request which
hadn't completed reports the reason `Enough quotes were collected.` while `Canceling`,
then the outcome which its carrier grain reports.

## Schedule now, await later with ScheduleAll

**Task:** start printing every package label, record that the order was picked while
the labels print, then collect the labels.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_schedule_all" language="csharp":::

`ScheduleAll(...).WithId("labels")` commits every label request with one journal write
and returns their handles without waiting. The `pick` step runs while the labels print.
<xref:Orleans.DurableTasks.ScheduledTask.WhenAll*?displayProperty=nameWithType> then
waits for every label and throws the first failure in input order, otherwise the first
cancellation, so each handle's `GetResult()` returns its label.

Pitfalls:

- Await every handle, or pass it to a combinator, before the workflow returns. A label
  which the workflow doesn't await keeps running, and the runtime cancels it when it
  deletes the task's execution tree.
- `ScheduleAll` runs only when awaited inside a durable execution, because its handles
  belong to that execution.

**Verify:** the fulfillment grain lists `labels/{i}` and `pick`, and the label tasks
complete in any order relative to `pick`.

## Wait for an external event with a deadline

**Task:** wait up to three days for an approver's decision on an expense claim.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_signal" language="csharp":::

<xref:Orleans.DurableTasks.DurableSignal`1.TryWaitAsync*> races the signal with a durable
delay to the deadline and returns `(true, decision)` or `(false, default)`. The task
records the first decision, and every wait and replay returns it. An ordinary grain
method delivers a decision with `IDurableTaskManager.TrySignalAsync`, which commits with
the grain's pending state changes and returns a `DurableSignalResult`. A client sends
the same signal through the grain reference:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_signal_client" language="csharp":::

Pitfalls:

- Compute the deadline from
  <xref:Orleans.DurableTasks.DurableTaskContext.UtcNow?displayProperty=nameWithType>,
  so a replay computes the same deadline.
- Address the invoked task, here the root task which the client scheduled. A local
  child's ID isn't an invoked task, so a signal to it returns `TaskNotFound`.
- Each name records one value. Use
  <xref:Orleans.DurableTasks.DurableSignal`1.For*> to derive a name per occurrence of a
  repeated event, such as one per approval round.
- While the task runs, a decision which arrives after the deadline is recorded, and a
  later `Decided.WaitAsync()` in the same task returns it.

**Verify:** `DecideAsync` returns `Accepted`, the same decision again returns
`Duplicate`, a different decision returns `Conflict`, and a decision after the task
completed without one returns `TaskCompleted`.

## Start follow-up work without waiting

**Task:** send a receipt when an invoice is issued, without making the invoice wait for
the notification service or cancel the receipt when the invoice is canceled.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_detach" language="csharp":::

<xref:Orleans.DurableTasks.DurableTaskExtensions.Detached*> makes the call an independent
root task on the notification grain, named `receipt-{invoiceId}` by
<xref:Orleans.DurableTasks.DurableTaskExtensions.WithId*>, and
<xref:Orleans.DurableTasks.ConfiguredDurableTask.Post*> stages it in the invoice grain's
outbox. The invocation and its record in the invoice's execution tree commit with the
invoice's result, so a replay which reaches the record starts nothing. Observers address
the receipt by its name:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_detach_observe" language="csharp":::

Pitfalls:

- Name detached tasks after stable values. Starting a different request under an existing
  root ID fails like other task ID reuse.
- A workflow which hands the receipt's ID to another party before its own result commits
  starts it with `ScheduleAsync`, which returns once the notification grain holds it.
- The notification grain retains the receipt's result for `ResultRetentionPeriod` after
  completion, for polling.

**Verify:** the notification grain lists `receipt-{invoiceId}`, and the invoice grain
lists a `$detach:…` record beneath the invoice's task, named by the notification grain and
the receipt's root ID, until the invoice's tree is deleted.

## Cancel and abort from ordinary grain methods

**Task:** let the buyer cancel an order before its stock is confirmed, and let an
operator abort a stuck workflow.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_order_controls" language="csharp":::

The grain injects `IDurableTaskManager`, which runs against the activation's own
runtime, without a grain call. `CancelAsync(rootId, reason)` completes once the request
has committed and propagated; the workflow observes it and decides its outcome, as in
[the compensation recipe](#compensate-inline-when-the-workflow-is-canceled).
`AbortAsync(rootId, reason)` commits the workflow and its execution tree as failed with
<xref:Orleans.DurableTasks.DurableTaskAbortedException>, whose message is the reason, and
sends an abort to each pending call to another grain. Clients use the task's handle:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_client_controls" language="csharp":::

Pitfalls:

- Check the business state before canceling. A cancellation which arrives after the
  workflow passed its cancellable steps runs the compensation which those steps still
  reach.
- Repeated cancellation requests are idempotent, and the first request's reason is the
  one the workflow reads.
- Abort runs no compensation and leaves detached tasks running; abort them separately.

**Verify:** a canceled order's diagnostics report its root as `Completed` with the
cancellation result, or `Canceled` when the exception escaped the workflow, with the
reason as its response. An aborted root reports `Faulted` with
`DurableTaskAbortedException: <reason>`.

## Schedule a workflow from another grain or a client

**Task:** start the order workflow when a buyer checks out, and observe its result.

A durable grain starts the workflow from an ordinary method with
<xref:Orleans.DurableTasks.ConfiguredDurableTask.Post*>, which stages the invocation in
its outbox, so the cleared basket and the invocation commit together in the journal write
which follows:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_schedule_from_grain" language="csharp":::

The workflow is a root task on the order grain, which retains it and its result for
`ResultRetentionPeriod` after completion, and the basket polls it there with the
`GetDurableTask<TResult>` grain-reference extension.

A client schedules the workflow with
<xref:Orleans.DurableTasks.ConfiguredDurableTask.ScheduleAsync*>, which returns once the
order grain holds the task, and awaits the returned handle, or reattaches to it later by
root ID with `GetDurableTask<TResult>`:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_schedule_from_client" language="csharp":::

A <xref:Orleans.DurableTasks.PollingOptions> value with a zero `PollTimeout` returns the
current status at once; a longer timeout waits up to that time for completion. Inside a
durable task, awaiting the call runs it as a child, and `WithId(name).ScheduleAsync()`
starts a child and returns its handle to await later.

Pitfalls:

- Derive the root ID from a business identity, such as the order ID, with `WithId`, so a
  retried checkout reaches the same task instead of starting a second workflow.
- A root task is retained for `ResultRetentionPeriod` after completion. Later polls throw
  `DurableTaskNotFoundException`, and starting the same root ID again runs the workflow
  again.

**Verify:** the order grain lists `place-order-{orderId}`, the basket lists no task for
it, and `GetOrderResultAsync` returns the result after the order ships.
