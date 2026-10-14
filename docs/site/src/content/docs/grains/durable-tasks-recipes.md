---
title: Durable task recipes
description: Fan out, race, compensate, settle every input, process completions in order, wait for signals, detach work, cancel, abort, and schedule durable tasks.
ms.date: 10/13/2026
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

## Race inputs against a deadline with WhenAny

**Task:** take the first shipping quote from two carriers, or give up after two minutes.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_race" language="csharp":::

The deadline is a local step which awaits a durable delay and returns `null`, so it has
the same result type as the carrier calls. `WhenAny<TResult>` returns the index and
result of the input which completed first. The grain stamps each input's result with a
durable completion sequence, so a replay returns the same winner even when the other
inputs have completed since.

Pitfalls:

- The other inputs keep running after the race. Their results are recorded and unused,
  and the runtime cancels any which are still pending when it deletes the task's
  execution tree. Use [`WhenEach`](#process-results-as-they-arrive-with-wheneach) to
  cancel the remaining inputs explicitly.
- A winner which failed or was canceled fails `WhenAny`. Return a value, such as `null`,
  from inputs whose failure is an expected outcome.
- Measure deadlines with durable delays or
  <xref:Orleans.DurableTasks.DurableTaskContext.UtcNow?displayProperty=nameWithType>.

**Verify:** the shipping grain lists `first-quote/0`, `first-quote/1`, and
`first-quote/2`, and `first-quote/2/$child-1`, the deadline's delay, as `Delayed` until its due
time.

## Compensate inline when the workflow is canceled

**Task:** when the buyer cancels the order during validation, release every reservation
and record why the order was canceled.

The `catch` block in [the fan-out recipe](#fan-out-to-every-line-with-a-named-whenall)
filters on `IsCancellationRequested`, so it handles the workflow's own durable
cancellation, and passes
<xref:Orleans.DurableTasks.DurableTaskContext.CancellationReason?displayProperty=nameWithType>
to the compensation:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_compensation" language="csharp":::

Children which a canceled task schedules terminate as canceled without running, except
inside a
<xref:Orleans.DurableTasks.DurableTaskContext.SuppressCancellation*?displayProperty=nameWithType>
scope. The `release` inputs and the `cancel` step therefore run to completion, and the
workflow returns an `OrderResult` which records the reason. A reservation which never
ran leaves nothing to release, so `ReleaseAsync` returns whether the order held a
reservation, and releasing every line is safe:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_catalog_grain" language="csharp":::

Pitfalls:

- Schedule every compensating child inside the scope. A child scheduled after the scope
  is disposed terminates as canceled without running.
- Make compensations idempotent and report whether they acted, so the workflow can
  compensate without knowing which steps ran.
- An abort runs no compensation. Abort is for tasks whose compensation is stuck.
- Work which must outlive the workflow, such as cleanup after the workflow's result is
  delivered, belongs in a [detached task](#detach-fire-and-forget-side-effects).

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
releases the held lines in a suppression scope, so the release also runs when the
workflow was canceled, then rethrows the cancellation so the task ends as canceled.

Pitfalls:

- Branch on each handle's outcome, not on the combined result: the combinator never
  throws for an input.
- Release inside `SuppressCancellation()` whenever the workflow can be canceled while it
  compensates.
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

## Detach fire-and-forget side effects

**Task:** send a receipt when an invoice is issued, without making the invoice wait for
the notification service or cancel the receipt when the invoice is canceled.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_detach" language="csharp":::

<xref:Orleans.DurableTasks.DurableTaskExtensions.DetachAsync*> starts the call as an
independent root task on the notification grain. The start and a marker in the
invoice's execution tree commit in one journal write, so a replay which reaches the
marker starts nothing.
<xref:Orleans.DurableTasks.DetachedTaskId.For*?displayProperty=nameWithType> derives the
root ID `issue-{invoiceId}:receipt` from the running task's ID, and observers derive the
same ID from the starter's task ID to poll the receipt:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_detach_observe" language="csharp":::

Pitfalls:

- Derive detached root IDs from stable values. Detaching a different request under an
  existing root ID fails like other task ID reuse.
- The detached task's result returns to no grain. The target retains it for
  `ResultRetentionPeriod` after completion, for polling.
- From an ordinary grain method, `DetachAsync(rootId)` commits the start with the grain's
  pending state changes; pass an explicit root ID there, because `DetachedTaskId.For`
  derives the ID from a running durable task.

**Verify:** the notification grain lists `issue-{invoiceId}:receipt`, and the invoice
grain lists a completed `issue-{invoiceId}/$detach:{notification}:issue-{invoiceId}\:receipt`
marker, named by the detached task's target and root ID, until the invoice's tree is deleted.

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

A durable grain schedules the workflow from an ordinary method with
<xref:Orleans.DurableTasks.DurableTaskExtensions.ScheduleAsync*>, which stages the
invocation in its outbox and writes the journal, so the cleared basket and the
invocation commit together:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_schedule_from_grain" language="csharp":::

The basket records the call under the root ID and receives the result through its
inbox. `IDurableTaskManager.GetTask<TResult>(rootId)` reads that record, and the basket
retains the result for `ResultRetentionPeriod` after it arrives. The order grain deletes
the workflow's execution tree once the basket has accepted the result. A starter which
doesn't use the result calls `DetachAsync(rootId)` instead, in the same write; the order
grain then retains the workflow and its result for `ResultRetentionPeriod`, and
observers poll it there.

A client schedules the workflow and awaits the returned handle, or reattaches to it
later by root ID with the `GetDurableTask<TResult>` grain-reference extension:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_schedule_from_client" language="csharp":::

A <xref:Orleans.DurableTasks.PollingOptions> value with a zero `PollTimeout` returns the
current status at once; a longer timeout waits up to that time for completion. Inside a
durable task, awaiting the call schedules it as a child, and
`WithId(segment).ScheduleAsync()` schedules a child and returns its handle to await
later.

Pitfalls:

- Derive the root ID from a business identity, such as the order ID, so a retried
  checkout schedules the same task instead of a second workflow.
- From a durable grain, observe the result through `IDurableTaskManager`. The handle
  which `ScheduleAsync` returns polls the target grain, which deletes the task after the
  scheduling grain accepts its result.
- A client-scheduled task is retained for `ResultRetentionPeriod` after completion.
  Later polls throw `DurableTaskNotFoundException`, and scheduling the same root ID again
  runs the workflow again.

**Verify:** the basket lists `place-order-{orderId}` with the order grain as its remote
target, `GetOrderResultAsync` returns the result after the order ships, and the order
grain no longer lists the workflow.
