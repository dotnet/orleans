---
title: Durable tasks
description: Understand the durable task programming model, its replay and commit guarantees, cancellation, signals, and task lifecycle.
ms.date: 10/13/2026
ms.topic: conceptual
---

# Durable tasks

Orleans Durable Tasks runs grain methods which return
<xref:Orleans.DurableTasks.DurableTask> or <xref:Orleans.DurableTasks.DurableTask`1>
as durable executions. A workflow is ordinary sequential C#: it awaits calls to other
grains, local state transitions, delays, and external signals. Each step's result is
recorded in the grain's journal, so the workflow survives activation loss and resumes
where it stopped. Durable tasks run over [Messaging](messaging.md), which carries
invocations, results, cancellations, and signals between grains through each grain's
durable inbox and outbox, and over Durable Jobs, which wakes grains when delays are due.

> [!NOTE]
> The `Microsoft.Orleans.DurableTasks` adapter and the
> `Microsoft.Orleans.DurableTasks.Abstractions` package await their first NuGet
> publication. Their APIs and persisted formats can change while the integration is
> evaluated. Projects in the Orleans repository reference them as source projects.

This page describes the programming model and its guarantees. Continue with:

- [Practical recipes](durable-tasks-recipes.md) for fan-out, races, compensation,
  signals, detached work, cancellation, and scheduling workflows from other grains.
- [Operations and troubleshooting](durable-tasks-operations.md) for configuration,
  retention, diagnostics, replay divergence, workflow versioning, and capacity.

## Choose durable tasks or message handlers

Both models commit business state, received messages, and outgoing messages in the
grain's journal, and they share one inbox and outbox per grain.

| Use durable tasks for | Use [message handlers](messaging.md) for |
| --- | --- |
| Multi-step workflows written as sequential code, with results flowing between steps. | Reacting to individual commands and events, one handler call per message. |
| Fan-out, races, and completion-order processing over durable calls. | Application-defined message identities, subjects, headers, and raw payloads. |
| Durable timers, deadlines, and waits for external events inside a workflow. | Scheduled messages and state machines which the application models explicitly. |
| Cooperative cancellation with inline compensation and recorded reasons. | Business replies and rejections exchanged as typed messages. |

## Define durable task methods

A grain interface declares a durable method by returning `DurableTask` or
`DurableTask<TResult>`. Arguments and results cross grains and persist in task state,
so their types need Orleans serializers:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_contracts" language="csharp":::

The implementing grain derives from <xref:Orleans.Journaling.DurableGrain> or
implements <xref:Orleans.Messaging.IMessagingGrain>. The grain's durable state change
and the task's result commit in one journal write, and the caller receives the result
once that write completes:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_catalog_grain" language="csharp":::

An `async DurableTask` method body is a deferred definition: calling the method creates
the definition, and the durable task runtime runs it under the task's identity. A body
which completes without awaiting is a normal step; the durable task analyzers suppress CS1998
for such methods, as described in [ORLEANS0028](../diagnostics/orleans0028.md).

How a durable call starts depends on the caller:

- Inside a durable task, awaiting the call schedules it as a child task of the running
  execution.
- From a client or an ordinary grain method,
  <xref:Orleans.DurableTasks.DurableTaskExtensions.ScheduleAsync*> starts it as a root
  task under a caller-chosen root ID and returns a
  <xref:Orleans.DurableTasks.ScheduledTask`1> handle.
- From anywhere, <xref:Orleans.DurableTasks.DurableTaskExtensions.DetachAsync*> starts
  it as an independent root task whose result the starter doesn't observe.

## Example: an order workflow

The following grain runs an order as one durable task. It records each status change
as a recorded effect, waits out a grace period, reserves every line concurrently, authorizes
payment, and waits for a shipment signal. Ordinary grain methods cancel the workflow
with a reason, abort it, and deliver the shipment. When the workflow is canceled, a line
is out of stock, or payment is declined, it releases the reserved stock inline:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_order_grain" language="csharp":::

A basket grain schedules the workflow under the root ID `place-order-{orderId}`. After
a deactivation, the next activation replays `PlaceOrderAsync` from the start: `submit`,
`grace-period`, `validate`, and the `reserve` inputs return their recorded results, and
the workflow continues with the first operation which hasn't completed. The
`playground/DurableOrderProcessing` project in the Orleans repository runs a complete
version of this workflow with a browser UI, detached projection updates, and diagnostics
endpoints.

## Task identity

A durable task is identified by its grain and its <xref:Orleans.DurableTasks.TaskId>.
Task IDs are hierarchical: a root ID, which the scheduling caller chooses, followed by
one segment per level of nesting. The order workflow above produces this tree:

```text
place-order-{orderId}
place-order-{orderId}/$once:submit
place-order-{orderId}/grace-period
place-order-{orderId}/$once:validate
place-order-{orderId}/reserve/0
place-order-{orderId}/reserve/1
place-order-{orderId}/authorize-payment
place-order-{orderId}/$once:pay
place-order-{orderId}/$signal:shipped
place-order-{orderId}/$once:ship
```

- <xref:Orleans.DurableTasks.DurableTaskExtensions.WithId*> names a child with a stable
  segment. A name which one run repeats, for example in a loop, identifies its later
  occurrences by their order: `line`, `line$2`, `line$3`. A child awaited without a name
  receives a generated segment, which begins with `$` and follows the order in which the
  execution creates unnamed children. Names don't contain `$`.
- A named combinator gives its inputs IDs beneath its own:
  `DurableTask.WhenAll(...).WithId("reserve")` identifies its inputs as `reserve/0`,
  `reserve/1`, and so on, in input order.
- A call to another grain runs on that grain under the same task ID, so
  `place-order-{orderId}/reserve/0` identifies both the order's record of the call and
  the reservation task on the catalog grain.
- Scheduling a task ID again with the same request reattaches to the existing task and
  its recorded result. Scheduling it with a different interface, method, or argument
  fails, so a task ID deduplicates schedules while the grain retains the task.

A durable task calls its own grain's durable methods directly, as local steps, such as
`await RefreshPricesAsync().WithId("refresh")`. A durable call through a reference to the
task's own grain is rejected.

## Recorded effects, local steps, and calls to other grains

A *recorded effect*,
<xref:Orleans.DurableTasks.DurableTask.Once*?displayProperty=nameWithType>, runs
synchronous code once for the running task and records its result, or the exception it
threw, in the task's tree under `$once:{name}`. The record commits with the task's next
durable write together with the journaled state which the effect changed, without a
write of its own, and a replay returns the recorded result without running the effect
again. The order workflow changes its status this way:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_once" language="csharp":::

- An effect changes grain state exactly once, reads state which later operations change,
  and produces identifiers, random values, and wall-clock times which every replay
  observes.
- The effect passes data to the workflow through its result: assignments to captured
  variables don't happen in later runs.
- A failure is recorded too, because journaled state has no rollback: changes made
  before the throw commit with the recorded exception. A result which doesn't serialize
  is recorded as that failure. A run which ends before a journal write commits the
  record also loses the effect's state changes, so the next run runs the effect again.
- A name which one run repeats identifies later occurrences by their order, such as
  `pay$2`. Effects and children named with `WithId` share the count, so a deployment
  which turns a recorded step into an effect, or an effect into a step, fails with the
  divergence error instead of running the work again.
- Effects are synchronous: asynchronous and durable effects don't compile, and run as
  awaited children instead. An effect runs on the activation's scheduler, so `Once`
  after `ConfigureAwait(false)` throws `InvalidOperationException` without running it.
- The record applies only within a durable task. Outside one, such as in a helper which
  an ordinary grain method shares, the effect runs every time.

A *local step* is an `async DurableTask` method of the same grain, awaited as a child,
for work which awaits. It records its result in the grain's task state, and the record
commits with its first durable write: its result, or a child, delay, or message which it
schedules. Its result is staged in the work item which runs its final code, so the state
changes which that code makes commit with the result. On replay, a step with a recorded
result returns that result without running again.

A *call to another grain* is a durable invocation. The calling grain records the call
in its task state and stages the invocation in its outbox; both commit with the
execution's next journal write. The callee's inbox accepts the invocation directly into
its task state, runs the method, and returns the result through the caller's inbox,
which records it in the caller's task state. Messages use identities derived from the
sender, receiver, task, and subject, so redeliveries and replayed sends are
deduplicated.

<xref:Orleans.DurableTasks.DurableTask.Run*?displayProperty=nameWithType> runs a
delegate as a child task, for work which isn't a grain call, such as a call to an
external service. A replay returns the delegate's recorded result, and runs a delegate
whose result wasn't recorded again, so the delegate's external effect needs its own
idempotency.

## Replay and determinism

After a deactivation or a failure, the next activation replays each pending task: the
method body runs again from the start, and every recorded operation returns its recorded
outcome. Replay starts once the grain's `OnActivateAsync` has completed, and a
deactivation signals running tasks to abort before the grain's `OnDeactivateAsync` runs.
A workflow resumes correctly when each replay schedules the same operations, with the
same IDs and arguments, in the same order.

Replay doesn't rebuild the grain's journaled state, which holds the latest committed
changes, including changes which later operations made. A durable body therefore has
three kinds of code:

- **Recorded operations** run once per execution, and a replay returns their recorded
  outcomes: durable calls to other grains, local steps, `DurableTask.Run` delegates,
  delays, signal waits and sends, detaches, and `DurableTask.Once` effects.
- **Re-executed code** precedes the body's last recorded operation and runs again in
  every replay. It computes values from the method's arguments, recorded results, the
  execution's logical time and cancellation state, and configuration which stays the
  same while executions are pending.
- **The synchronous tail** follows the body's last recorded operation, with no other
  `await` before the body returns. It runs only in a run which completes the body, and
  its state changes commit in the same journal write as the task's result, whichever
  operation requests that write, so no replay runs it again.

The following grain allocates a ticket number in an effect, authorizes a payment, and
records the outcome in its synchronous tail:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_replay_regions" language="csharp":::

Follow these rules in re-executed code:

- Put each side effect in a recorded operation or in the synchronous tail. Code between
  recorded operations computes values from the method's arguments, earlier results, and
  other values which are the same on every replay.
- Read and change grain state in recorded effects, steps, or the synchronous tail. Grain
  state holds every committed change, so code which replays reads the state which later
  operations wrote.
- Read time from `DurableTask.Current.UtcNow`, the execution's logical time, which
  <xref:Orleans.DurableTasks.DurableTask.Current?displayProperty=nameWithType> exposes.
  The runtime persists it before the execution's effects commit and advances it to the
  recorded completion time of each child result the execution observes, so a replay
  observes the same times and a delay starts when the work before it completed.
- Produce random values and identifiers in recorded effects or children, and call
  external services in steps or `DurableTask.Run` delegates, whose external effects need
  their own idempotency.
- Name children with `WithId` when they're scheduled conditionally, in loops, or
  concurrently outside a combinator, and name combinators. Generated segments are stable
  only when unnamed children are created in the same order. Detaches and signal sends
  are recorded under IDs derived from their target and root ID or signal, so adding one
  doesn't move others.
- Log through a logger which `logger.ReplaySafe()` returns, so each event is logged once.

<xref:Orleans.DurableTasks.DurableTaskContext.IsReplaying?displayProperty=nameWithType>
reports whether a run is re-running operations which earlier runs recorded: it is `true`
from the start of a run which finds recorded operations in the task's tree, other than
signal values, until the run has reached each of them or starts an operation which no
earlier run recorded, and then stays `false`. Use it for logging and diagnostics only. A
run which ended before a journal write committed its work is replayed, so code which runs
only while `IsReplaying` is `false` can run twice or never, and code which branches on it
schedules different operations in different runs.

A replay which schedules a different operation under a recorded ID fails with an
`InvalidOperationException` which names the recorded and the requested operations. See
[Diagnose replay divergence](durable-tasks-operations.md#diagnose-replay-divergence)
for workflow versioning.

## Combinators and handles

Combinators schedule their inputs concurrently, so the runtime commits the batch of
invocations together. Awaited, a combinator runs as part of the awaiting execution and
records no result of its own; each input runs as a child identified by its input index
under the combinator's operation ID. Name the operation with `WithId`, so its inputs
keep their IDs when a later deployment adds or removes earlier combinators.

- <xref:Orleans.DurableTasks.DurableTask.WhenAll*?displayProperty=nameWithType> follows
  `Task.WhenAll`. It completes after every input is terminal, including inputs which
  durable cancellation reaches. It then throws the first failure in input order;
  otherwise the first cancellation in input order; otherwise `WhenAll<TResult>` returns
  the results in input order. Input order selects the exception, so a replay observes
  the same one.
- <xref:Orleans.DurableTasks.DurableTask.WhenAny*?displayProperty=nameWithType> returns
  the index of the input which completed first, and `WhenAny<TResult>` also returns its
  result. A winner which failed or was canceled fails the combinator. The other inputs
  keep running.
- <xref:Orleans.DurableTasks.DurableTask.ScheduleAll*?displayProperty=nameWithType>
  schedules every input and returns their <xref:Orleans.DurableTasks.ScheduledTask>
  handles in input order, without waiting for results.
- <xref:Orleans.DurableTasks.DurableTask.WhenAllCompleted*?displayProperty=nameWithType>
  waits until every input is terminal and returns the handles. An input's failure or
  cancellation leaves the combinator successful, so the workflow can act on each
  outcome, for example by compensating exactly the inputs which succeeded.
- <xref:Orleans.DurableTasks.DurableTask.WhenEach*?displayProperty=nameWithType> schedules
  its inputs and yields each one's handle as an `IAsyncEnumerable`, in completion order.
  An input's failure or cancellation is yielded like a success.

The grain stamps each child's terminal result with a durable completion sequence, in
the journal write which records the result. `WhenAny` and `WhenEach` select the
remaining input with the lowest sequence, so every replay selects the same winner and
yields the same order, including a replay in which more inputs have already completed.
`ScheduleAll`, `WhenAllCompleted`, and `WhenEach` run only when awaited inside a durable
execution, because their handles belong to that execution.

A <xref:Orleans.DurableTasks.ScheduledTask> handle observes one task. Once a wait, a
poll, or a combinator observes its terminal response, `IsCompleted`, `Status`, and
`Exception` report it synchronously.
<xref:Orleans.DurableTasks.ScheduledTask`1.GetResult*> returns the result or throws the
recorded failure or cancellation, and
<xref:Orleans.DurableTasks.ScheduledTask`1.TryGetResult*> returns `true` with the result
of a task which completed successfully. Before the handle observes a terminal response,
`Status` is `Pending`. For existing handles,
<xref:Orleans.DurableTasks.ScheduledTask.WhenAll*?displayProperty=nameWithType>,
<xref:Orleans.DurableTasks.ScheduledTask.WhenAllCompleted*?displayProperty=nameWithType>,
and <xref:Orleans.DurableTasks.ScheduledTask.WhenEach*?displayProperty=nameWithType>
provide the same semantics. `WhenEach` accepts no `WithId`, so the named form schedules
the inputs with `ScheduleAll(...).WithId(name)` and enumerates
`ScheduledTask.WhenEach(handles)`.

<xref:Orleans.DurableTasks.DurableTask.Delay*?displayProperty=nameWithType> and
<xref:Orleans.DurableTasks.DurableTask.DelayUntil*?displayProperty=nameWithType> are
durable timers measured in logical time. The runtime records the delay's due time and
sends the grain a scheduled resume message, which its inbox holds until the Durable Jobs
clock reaches the due time. A recorded due time is a commitment: a replay which requests
another due time, for example after a configured duration changed, keeps the recorded
one and logs a warning.

## Cancellation

Cancellation is cooperative, durable, and idempotent. A grain requests it for its own
tasks with `IDurableTaskManager.CancelAsync(rootId, reason)`, and clients and other
callers request it through
<xref:Orleans.DurableTasks.ScheduledTask.CancelAsync*?displayProperty=nameWithType>.
The request commits before it propagates through the task's execution tree:

- Delays, children whose bodies haven't started, and new children which the canceled
  task schedules terminate as canceled without running.
- Running children observe the request through
  <xref:Orleans.DurableTasks.DurableTaskContext.CancellationToken?displayProperty=nameWithType>,
  and pending calls to other grains receive a cancellation which their callee applies
  to its own task.
- The task's body decides its outcome. Awaiting a canceled child throws
  `OperationCanceledException`. The task is canceled when that exception escapes the
  body; a body which catches it completes with its own result or failure.
- Completed children keep their recorded results, and a replay of a canceled task
  observes the request from the start, so the body takes the same path.

The first request's reason is durable:
<xref:Orleans.DurableTasks.DurableTaskContext.CancellationReason?displayProperty=nameWithType>
returns it in the canceled execution, its descendants, and every replay, and the
cancellation carries it to pending calls to other grains.

A body compensates inline inside a
<xref:Orleans.DurableTasks.DurableTaskContext.SuppressCancellation*?displayProperty=nameWithType>
scope. Each child which the body schedules in the scope, including local steps, delays,
calls to other grains, and combinator inputs, runs to completion although the task's
cancellation was requested, and a later request skips it. The scope follows the
asynchronous flow which entered it and covers children of that execution. An abort and
the deletion of the task's execution tree still reach these children.

An administrative abort, through `IDurableTaskManager.AbortAsync(rootId, reason)` or
<xref:Orleans.DurableTasks.ScheduledTask.AbortAsync*?displayProperty=nameWithType>,
commits the task and its execution tree as failed with
<xref:Orleans.DurableTasks.DurableTaskAbortedException> in one journal write, stops
their running attempts, and sends an abort to each pending call to another grain. Abort
runs no task code, including cancellation callbacks and compensation, so use it for a
task whose body ignores cancellation or whose cleanup is stuck.

## Detached tasks

`DetachAsync(rootId)` starts a durable call as a new root task on its target, for
fire-and-forget work, cleanup which outlives the starter, and durable calls from code
which doesn't await their results. The target retains the detached task like a
client-scheduled root, and its result is available by polling its root ID.

- Cancellation and deletion of the starter's execution tree leave the detached task
  running.
- Inside a durable task, the start and a marker in the execution's tree commit in one
  journal write, so a replay which reaches the marker starts nothing. The marker,
  `$detach:{target}:{rootId}`, derives from the detached task's identity, so adding
  operations to a workflow doesn't move it, and detaching the same task again reaches
  the same marker.
  <xref:Orleans.DurableTasks.DetachedTaskId.For*?displayProperty=nameWithType> derives a
  replay-stable root ID, `{taskId}:{segment}`, from the running task's ID.
- In an ordinary method of a durable grain, the start commits with the grain's pending
  state changes.
- Detaching the same request under the same root ID again leaves the task unchanged.

## Signals

A <xref:Orleans.DurableTasks.DurableSignal`1> carries an external event, such as a
shipment notice or an approval, into a running task. Senders and waiters share one
descriptor, which names the signal:

- <xref:Orleans.DurableTasks.DurableSignal`1.WaitAsync*> waits for the signal's value.
  <xref:Orleans.DurableTasks.DurableSignal`1.TryWaitAsync*> races the wait with a
  deadline and returns `(true, value)` or `(false, default)`, and the same outcome on
  every replay.
- A signal addresses an *invoked task*: a task which a client, another grain, or
  `DetachAsync` started on the grain. Waits anywhere in that task's execution tree
  observe its signals.
- The task records the first value for each signal name and returns it to every wait for
  that name, including waits during replay. A signal which arrives before its wait is
  recorded, and the wait returns it at once. A repeated signal with an equal value
  leaves the task unchanged, and a different value is rejected.
  <xref:Orleans.DurableTasks.DurableSignal`1.For*> derives a distinct name, such as
  `approved/round-2`, for each occurrence of a repeated event.
- A grain signals its own tasks with `IDurableTaskManager.SignalAsync` or
  `TrySignalAsync`, which commit with the grain's pending state changes. Clients and
  ordinary methods of other grains use the `SignalDurableTaskAsync` and
  `TrySignalDurableTaskAsync` grain-reference extensions, and durable tasks on other
  grains use `SignalDurableTaskAsync`, which records the send for replay under
  `$send:{target}:{taskId}:{name}`. Repeated sends of one signal in an execution, such
  as a retry after `TaskNotFound`, are numbered by occurrence, `$send:...$2` and so on,
  so each sends again. The `Try` forms return a `DurableSignalResult`: `Accepted`,
  `Duplicate`, `Conflict`, `TaskNotFound`, or `TaskCompleted`.
- A grain can commit an invocation on another grain and a signal for it in one journal
  write. The signal message can then arrive first; the receiving grain records its value,
  and the invocation observes it when it arrives.

## Commit model and guarantees

- `(grain, TaskId)` identifies one execution. A stable request fingerprint detects reuse
  of a task ID with a different interface, method, argument type, or argument value.
- Request identity, task state, caller subscription, and inbox completion commit before
  execution begins.
- Task state and outgoing messages share the grain's journal commit boundary with the
  grain's own journaled state. A failed journal write deactivates the grain, and the
  next activation recovers from committed state.
- Scheduling a durable call from a durable grain, including from an ordinary method,
  stages the invocation in the outbox, so it commits atomically with the grain's state
  changes.
- Each request and result payload is journaled once per grain, in task state. A
  dead-lettered completion's dead letter also holds its result, for inspection and replay.
- Recovery precedes new execution. Child IDs, completion order, and outgoing messages
  are replay-stable.
- Terminal results and the completion notifications to callers commit together. The
  first durable terminal result wins a race between completion and cancellation.
- A body's result is staged in the work item which runs the body's final code on the
  activation, so it commits in the same journal write as the state which that code
  changed, whichever operation requests the write. A `DurableTask.Once` record is
  staged in the same work item as the effect's state changes, and commits with them.

Durability covers state and messages which participate in Orleans Journaling and
Messaging commits. External side effects, such as calls to a payment provider, need
their own idempotency, for example a request key derived from the task ID.

## Lifecycle and retention

An *invoked task*, which a client or another grain scheduled, owns an execution tree:
the task and its descendants which aren't invoked tasks themselves, such as local
steps, delays, `DurableTask.Once` records, detach and signal-send markers, and the
records of calls to other grains. Once an invoked task is
terminal, its grain deletes the tree:

- When a grain called the task, in the journal write which retires the completion after
  the calling grain has durably accepted the result.
- When the task has no calling grain, such as a task which a client scheduled or a
  detached task, `DurableTaskOptions.ResultRetentionPeriod` after completion. Polls
  during that period return the retained result, and later polls throw
  `DurableTaskNotFoundException`.

Deleting a tree cancels its unfinished tasks. A task ID therefore deduplicates schedules
until its task is deleted, and scheduling it after the deletion runs the request again.
Completions and delay resumes which arrive for deleted tasks are ignored. See
[Retention and deletion](durable-tasks-operations.md#retention-and-deletion) for
operational details.

## Configure the silo and clients

Durable tasks need Journaling storage and Durable Jobs storage.
`AddDurableTasks` on the silo builder adds Messaging and the durable task runtime,
stores task state in the grain's journal, and registers the JSON metadata which the
JSON journal format needs for task state. Its optional callback configures
`DurableTaskOptions`. For development, configure in-memory storage:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_registration" language="csharp":::

External clients which schedule, observe, cancel, or signal durable tasks register the
client-side adapter:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_client_registration" language="csharp":::

Grains which derive from <xref:Orleans.Journaling.DurableGrain> or implement
<xref:Orleans.Messaging.IMessagingGrain> host durable tasks. During activation setup,
before journal recovery, the runtime binds to the grain's journal, inbox, and outbox,
and the grain can inject `IDurableTaskManager` to observe, cancel, abort, and signal its
own tasks. Task state can use any journal format; requests and results are stored with
the Orleans serializer. Use shared, production-grade Journaling and Durable Jobs
providers for multi-silo deployments; see
[Journaling configuration](journaling/configuration.md).
