---
title: Durable tasks
description: Understand the durable task programming model, its replay and commit guarantees, cancellation, signals, and task lifecycle.
ms.date: 10/16/2026
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
durable inbox and outbox, and over Durable Jobs, which wakes grains when delays are due
and resumes executions after their grain deactivates.

> [!NOTE]
> The `Microsoft.Orleans.DurableTasks` adapter and the
> `Microsoft.Orleans.DurableTasks.Abstractions` package await their first NuGet
> publication. Their APIs and persisted formats can change while the integration is
> evaluated. Projects in the Orleans repository reference them as source projects.

This page describes the programming model and its guarantees. Continue with:

- [Task identity and references](durable-tasks-identity.md) for how task IDs are
  constructed, which changes keep them stable, and how to address a task from elsewhere.
- [Structured concurrency](durable-tasks-structured-concurrency.md) for task trees,
  ownership, lifetimes, and how cancellation flows through them.
- [Idempotency](durable-tasks-idempotency.md) for the effects which commit exactly once
  and the external effects which need idempotency keys.
- [Replay-safety rules](durable-tasks-replay-rules.md) for the rules which the durable
  task analyzers enforce.
- [Versioning](durable-tasks-versioning.md) for changing workflow code while executions
  are pending, and retiring old versions.
- [Integrate with other systems](durable-tasks-integrations.md) for payment providers,
  databases, HTTP services, webhooks, and where to retry.
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
for such methods, as described in [ORLEANS0224](../diagnostics/orleans0224.md).

Inside a durable task, awaiting a durable call runs it as a child task of the running
execution. From a client or an ordinary grain method, awaiting it or
<xref:Orleans.DurableTasks.DurableTaskExtensions.ScheduleAsync*> starts it as a root task.
[Starting and observing tasks](#starting-and-observing-tasks) describes every way to start
a task.

## Example: an order workflow

The following grain runs an order as one durable task. It records each status change
as a recorded effect, waits out a grace period, reserves every line concurrently, authorizes
payment, and waits for a shipment signal. Ordinary grain methods cancel the workflow
with a reason, abort it, and deliver the shipment. When the workflow is canceled, a line
is out of stock, or payment is declined, it releases the reserved stock inline:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_order_grain" language="csharp":::

A basket grain starts the workflow under the root ID `place-order-{orderId}`. After
a deactivation, the next activation replays `PlaceOrderAsync` from the start: `submit`,
`grace-period`, `validate`, and the `reserve` inputs return their recorded results, and
the workflow continues with the first operation which hasn't completed. The
`playground/DurableOrderProcessing` project in the Orleans repository runs a complete
version of this workflow with a browser UI which places orders through durable HTTP
operations, detached projection updates, and diagnostics endpoints.

## Task identity

A durable task is identified by its grain and its <xref:Orleans.DurableTasks.TaskId>.
Task IDs are hierarchical: a root ID, which the starting caller chooses, followed by
one segment per level of nesting. [Task identity and references](durable-tasks-identity.md)
lists every kind of ID and its stability. The order workflow above produces this tree:

```text
place-order-{orderId}
place-order-{orderId}/submit
place-order-{orderId}/grace-period
place-order-{orderId}/validate
place-order-{orderId}/reserve/0
place-order-{orderId}/reserve/1
place-order-{orderId}/authorize-payment
place-order-{orderId}/pay
place-order-{orderId}/$signal:shipped
place-order-{orderId}/ship
```

- <xref:Orleans.DurableTasks.DurableTaskExtensions.WithId*> names a child with a stable
  segment. A name which one run repeats, for example in a loop, identifies its later
  occurrences by their order: `line`, `line$2`, `line$3`. A child awaited without a name
  receives a generated segment, which begins with `$` and follows the order in which the
  execution creates unnamed children. Names don't contain `$`.
- A named combinator gives its inputs IDs beneath its own:
  `DurableTask.WhenAll(...).WithId("reserve")` identifies its inputs as `reserve/0`,
  `reserve/1`, and so on, in input order.
- A call to another grain runs on that grain under the caller's scope,
  `$caller/{callerGrainId}/{incarnation}/{taskId}`, where `{incarnation}` identifies the caller's run: the
  order's record of the call `place-order-{orderId}/reserve/0` is the catalog grain's task
  `$caller/order/{orderId}/{incarnation}/place-order-{orderId}/reserve/0`. Each caller has its own
  scope, so callers whose workflows share a root ID never share a callee's task, and each run of a
  root ID has its own incarnation, so a run after the earlier one was deleted calls new tasks. A
  root task, such as a detached start, keeps its root ID on its grain, and the root ID
  `$caller` is reserved.
- Scheduling a task ID again with the same method, from the same caller, reattaches to
  the existing task and its recorded result, whatever the arguments, so a retry whose
  arguments serialize differently reaches the task. Scheduling it with a different
  interface or method fails, so a task ID deduplicates schedules while the grain retains
  the task.

A durable task calls its own grain's durable methods directly, as local steps, such as
`await RefreshPricesAsync().WithId("refresh")`. A durable call through a reference to the
task's own grain is rejected.

## Journaled state in durable task bodies

A durable task body reads and changes its grain's journaled state directly:
<xref:Orleans.Journaling.IDurableValue`1>, <xref:Orleans.Journaling.IDurableDictionary`2>,
<xref:Orleans.Journaling.IDurableList`1>, <xref:Orleans.Journaling.IDurableQueue`1>,
<xref:Orleans.Journaling.IDurableSet`1>, <xref:Orleans.Journaling.IDurableLog`1>, and
<xref:Orleans.Journaling.IDurableTaskCompletionSource`1>, and it sends messages through
the Messaging <xref:Orleans.Messaging.IOutbox>. The runtime records each access in the
execution's access log, in order, and a replay returns the recorded outcomes:

- A read returns the value which the recording run read, including after other grain
  work changed the state, so a replay takes the same branches and schedules the same
  operations with the same arguments. An enumeration returns the recorded items.
- A committed write returns its recorded result, such as the `bool` which `Remove`
  returned or the sequence number which `Append` returned, and leaves the state as the
  recording run changed it. A committed send returns its record, so the outbox holds one
  message.
- An access which threw records its exception, and a replay throws it again.
- A write's record is staged in the work item which makes the write, so every journal
  write which commits the change also commits its record. A read's record commits with
  the next journal write of the grain, which is how any decision based on the read
  becomes durable. A run which ends before a journal write commits a change loses the
  change with its record, so the next run makes the change again.

The order workflow changes its status this way:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_state" language="csharp":::

- A replay returns a recorded value decoded from the log, so the body changes a value by
  writing it back, such as `orders[id] = order with { Status = OrderStatus.Paid }`. Values
  and exceptions are recorded with the Orleans serializer in every journal format, as
  `DurableTask.RunInline` results are, so the state's types are Orleans-serializable.
- Every run accesses the same state, with the same keys, indexes, and written values, in
  the same order. A replay which makes a different access, such as reading other state or
  another key at a recorded position, or whose recorded value no longer decodes, stalls: the runtime cancels the attempt for `Diverged`, discards its
  result, and keeps the task pending until code which makes the recorded accesses replays
  it. The attempt's handlers can't continue the task: its durable operations throw
  `DurableTaskAttemptCanceledException`, whose inner exception names the recorded and the
  requested access, and the `ExecutionStalled` event reports the stall.
- The body accesses state on the activation's scheduler, as it records other operations.
- The body awaits durable operations, such as signals and children, rather than observing
  state as it changes: `IDurableTaskCompletionSource<T>.Task`, `IDurableLog<T>.ReadAsync`,
  and `WatchAsync` report changes as they happen, unrecorded, so a replay can observe
  different changes.
- Task cancellation keeps recording: compensation in a `catch` block is part of the
  execution's history, and a replay returns its records as it returns the body's others.
- Once the task ends without its body, because it's aborted or deleted, or the body
  completes, the attempt's later code, such as cleanup in a `catch` or `finally` block and
  work which the body started without awaiting it, first returns the records which the
  attempt hasn't replayed yet, then accesses state directly, as other grain code does. Its
  changes commit with the grain's next journal write.
- When the activation stops, or the attempt stalls, a later attempt runs the body again
  from its records, so the stopped attempt's later code reads state directly, and its
  writes throw `DurableTaskAttemptCanceledException` and leave the state unchanged. The
  next attempt runs that code again and records it.
- A `DurableTask.RunInline` effect's record covers the accesses which it makes, and so
  does a journaled state operation's record cover the code which the operation runs, such as
  a comparer or the source which `AddRange` enumerates. An effect inside them runs as part
  of them, and a durable operation inside them, such as starting a child, throws
  `InvalidOperationException`, because a replay returns the record without running that code.
- Registrations on the body's cancellation tokens observe the cancellation without
  accessing journaled state: they run when the token is canceled, which can be inside
  another operation, so a replay runs them at another point. The body's `catch` block is
  where compensation changes state.
- The access log holds one record for each access in re-executed code and is deleted when
  the execution completes. A read records a reference to the attempt's latest recorded
  read of the same state and operation, rather than its value, when its result is equal
  and compares exactly, as strings, integers, and enumerations do, or when the state is
  unchanged since that read, the read's key or index is equal, and its result is
  immutable, such as a `double`, a date, or a copy of such values. Consecutive
  references share one record, so a loop which reads unchanged values adds a few bytes
  rather than a value per iteration. Another pure read of the grain's journaled state,
  such as `TryGetValue`, `Count`, or a value's `Value`, records the journal's position
  rather than its value. The journal keeps its history after a recorded position while
  the execution runs, and a recovery replays that history to supply the value which the
  read observed, before the execution replays. Every other read records its value, so a
  replay returns what each read observed when it ran. A workflow which loops indefinitely
  bounds its history as
  [Replay-safety rules](durable-tasks-replay-rules.md#rules-which-the-analyzers-dont-check)
  describes.
- <xref:Orleans.Runtime.IPersistentState`1> holds a value which the grain changes in
  place, and plain fields hold values outside the journal, so the body reads and changes
  them in recorded effects, steps, or the synchronous tail; analyzer
  [ORLEANS0200](../diagnostics/orleans0200.md) reports other accesses.

## Recorded effects, local steps, and calls to other grains

A *recorded effect*,
<xref:Orleans.DurableTasks.DurableTask.RunInline*?displayProperty=nameWithType>, runs
synchronous code in the caller's current work item before it returns, with no
suspension and no child execution, and records its result, or the exception it threw,
in the task's tree under its name. The record commits in the same journal write as the
journaled state which the effect changed, and a replay returns the recorded result
without running the effect again. The ticket grain draws a reference and reads the clock this way:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_run_inline" language="csharp":::

- An effect produces identifiers, random values, and wall-clock times which every replay
  observes, changes persistent state exactly once, and groups several accesses to
  journaled state under one record.
- The effect passes data to the workflow through its result: assignments to captured
  variables don't happen in later runs.
- A failure is recorded too, because journaled state has no rollback: changes made
  before the throw commit with the recorded exception. A result which doesn't serialize
  is recorded as that failure. A run which ends before a journal write commits the
  record also loses the effect's state changes, so the next run runs the effect again.
- Consecutive effects in one synchronous block, and the journaled state which they
  change, commit in the same journal write, and no other turn of the grain runs between
  them. Returning from an effect doesn't mean that its record has committed: it commits
  with the task's next journal write. The effect runs after task cancellation too, so
  compensation records its effects.
- An effect within another effect runs as part of the outer effect, whose record
  covers it.
- A name identifies an effect as `WithId` identifies a step: `pay`, then `pay$2` for a
  later occurrence in the same run.
- `DurableTask.RunInline(effect)` omits the name, such as
  `var reference = DurableTask.RunInline(Guid.NewGuid);`. The effect takes the execution's
  next generated child ID, such as `$child-3`, which unnamed effects share with unnamed
  children, so it finds its record when every run creates its unnamed operations in the
  same order. Name effects whose position can change between runs or deployments.
- Effects are synchronous: asynchronous and durable effects don't compile, and the
  error shows the replacement, `await DurableTask.Run(async () => ...)`. An effect runs
  on the activation's scheduler, so `RunInline` after `ConfigureAwait(false)` throws
  `InvalidOperationException` without running it.
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

<xref:Orleans.DurableTasks.DurableTask.Run*?displayProperty=nameWithType> creates an
unstarted step, for work which isn't a grain call, such as a call to an external
service. The step runs its delegate when it's awaited, scheduled, or posted, possibly in
a later turn, as a child execution with its own `DurableTask.Current` and cancellation
scope. Creating it starts nothing, and nothing runs on the thread pool. A replay returns
the delegate's recorded result, and runs a delegate whose result wasn't recorded again,
so the delegate's external effect needs its own idempotency.

### Effects and steps

`DurableTask.RunInline("hold-id", Guid.NewGuid)` and
`await DurableTask.Run(async () => ...).WithId("hold-id")` use the same identifier,
`{taskId}/hold-id`, and both record their outcome once. They differ in when the code
runs, and the other differences follow from that. "Inline" means what it means elsewhere
in .NET, as in `TaskScheduler.TryExecuteTaskInline`: the code runs synchronously in the
current work item instead of being scheduled.

- An effect runs immediately, in the current work item, before `RunInline` returns. So
  consecutive effects commit together with the state they change, the effect observes
  the caller's `DurableTask.Current`, and it runs after task cancellation.
- A step runs when it's awaited, scheduled, or posted, possibly in a later turn, as a
  child execution. So it has its own `DurableTask.Current`, a step which isn't shielded
  completes as canceled when its parent's cancellation was requested before it starts,
  and its record commits in a journal write of its own.

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_effect_and_step" language="csharp":::

A replay which finds one kind recorded where the body now runs the other diverges
instead of running the work again: its execution stalls, and `ExecutionStalled` names
both kinds. A deployment
which changes an effect into a step, or a step into an effect, branches on
[`DurableTask.GetVersion`](durable-tasks-versioning.md) so that executions which
recorded the old kind keep it.

## Publish progress and stream drafts

A workflow publishes progress to observers through a journaled
<xref:Orleans.Journaling.IDurableLog`1> of its grain. Observers read the log with
<xref:Orleans.Journaling.IDurableLog`1.ReadAsync*>, typically through an
`IAsyncEnumerable<T>` grain method. [Observe journaled state](journaling/observe-state.md)
describes reading, resuming, and trimming logs.

To publish an entry, append it, `log.Append(item)`. The runtime records the append, and
the entry commits together with its record. A replay returns the recorded sequence
number without appending again, so a reader in the committed mode receives each entry
once, in the order in which the task appended it. Entries suit progress
which callers and user interfaces watch, such as each status of an order or each
completed partition of an export:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_progress" language="csharp":::

The `playground/DurableOrderProcessing` project keeps each order's state as a log of
snapshots which its workflow appends this way, and streams them to the browser as
server-sent events.

To stream content as it's produced, such as a model's response, run the stream in a
local step. The step opens a draft with
<xref:Orleans.Journaling.IDurableLog`1.BeginDraft*>, writes each item to it, and commits
the final content in its final synchronous code, after its last `await`:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_stream_draft" language="csharp":::

- A draft's items are volatile. Readers which include
  <xref:Orleans.Journaling.DurableWatchMode.Drafts?displayProperty=nameWithType> receive
  each item as the step writes it, and the content becomes durable when `Commit` appends
  the entries which replace the draft.
- The step's result is staged in the work item which runs its final synchronous code, so
  the entries which `Commit` appends and the step's recorded result commit in the same
  journal write. Readers in the committed mode receive the entries once that write is
  durable.
- The content therefore commits exactly once: every committed journal holds both the
  entries and the step's record, or neither. A replay after that write returns the
  recorded result without running the step. A run which ends before that write loses
  both, so the next run runs the step, which streams and commits the content.
- A step which throws, including after cancellation, disposes the draft, which abandons
  it, and the step's failure is recorded, so a replay rethrows it without streaming again.
- A journaled change which the step makes before an `await`, such as an `Append`, commits
  with its record. A step which runs again returns the recorded outcome without making
  the change again. A draft's items are volatile, so a step which runs again streams them
  again.

## Replay and determinism

After a deactivation or a failure, the next activation replays each pending task: the
method body runs again from the start, and every recorded operation returns its recorded
outcome. Replay starts once the grain's `OnActivateAsync` has completed, and a
deactivation cancels the attempts of running tasks before the grain's `OnDeactivateAsync`
runs, so their later durable operations throw; see
[Task cancellation and attempt cancellation](durable-tasks-structured-concurrency.md#task-cancellation-and-attempt-cancellation).
A workflow resumes correctly when each replay schedules the same operations, with the
same IDs and arguments, in the same order.

Replay doesn't rebuild the grain's state, which holds the latest committed changes,
including changes which later operations made. Journaled state returns the outcomes
which the execution's access log recorded, as
[Journaled state in durable task bodies](#journaled-state-in-durable-task-bodies)
describes. A durable body has three kinds of code:

- **Recorded operations** run once per execution, and a replay returns their recorded
  outcomes: durable calls to other grains, local steps, `DurableTask.Run` delegates,
  delays, signal waits and sends, detached and posted starts, `DurableTask.RunInline`
  effects, and accesses to journaled state.
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
- Read and change journaled state anywhere in the body, and persistent state and plain
  fields in recorded effects, steps, or the synchronous tail. Persistent state holds
  every committed change, so code which replays reads the state which later operations
  wrote.
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
  only when unnamed children are created in the same order. Detached tasks and signal
  sends are recorded under IDs derived from their target and root ID or signal, so adding
  one doesn't move others.
- Log through a logger which `logger.ReplaySafe()` returns, so each event is logged once.

[Replay-safety rules](durable-tasks-replay-rules.md) lists every rule, the failure which
it prevents, and the analyzer diagnostic which reports it.

<xref:Orleans.DurableTasks.DurableTaskContext.IsReplaying?displayProperty=nameWithType>
reports whether a run is re-running operations which earlier runs recorded: it is `true`
from the start of a run which finds recorded operations in the task's tree, other than
signal values, until the run has reached each of them or starts an operation which no
earlier run recorded, and then stays `false`. Use it for logging and diagnostics only. A
run which ended before a journal write committed its work is replayed, so code which runs
only while `IsReplaying` is `false` can run twice or never, and code which branches on it
schedules different operations in different runs.

A replay which schedules a different operation under a recorded ID, or which can't decode
a recorded outcome, diverges: the runtime stalls its execution, which stays pending until
code which matches the record replays it, and raises `ExecutionStalled`, which names the
recorded and the requested operations. See
[Diagnose replay divergence](durable-tasks-operations.md#diagnose-replay-divergence).
To change a workflow while executions are pending, branch on
`DurableTask.GetVersion`, which gives each execution the version of the change which it
recorded, and version `0` to executions which earlier code ran past the change; see
[Version durable tasks](durable-tasks-versioning.md).

## Combinators and handles

Combinators schedule their inputs concurrently, so the runtime commits the batch of
invocations together. Awaited, a combinator runs as part of the awaiting execution and
records no result of its own; each input runs as a child identified by its input index
under the combinator's operation ID. Name the operation with `WithId`, so its inputs
keep their IDs when a later deployment adds or removes earlier combinators.

Each combinator is also an extension method on a sequence of tasks, so a list, an
array, or a query composes in place, as in
`lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units)).WhenAll().WithId("reserve")`.
Lists of handles compose the same way, for example `handles.WhenAny(cancellationToken)`.
A collection expression has no type of its own, so `[a, b]` goes to the static form:
`DurableTask.WhenAll([a, b])`.

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
- <xref:Orleans.DurableTasks.DurableTask.Race*?displayProperty=nameWithType> selects its
  winner like `WhenAny` and returns the same values, then requests cancellation of every
  other input with the reason `Another task won the race.` and waits until each is
  terminal, so no input outlives the race. A winner which failed or was canceled fails
  it; the other inputs' outcomes don't.
- <xref:Orleans.DurableTasks.DurableTaskExtensions.WaitAsync*> and
  <xref:Orleans.DurableTasks.DurableTaskExtensions.WaitUntilAsync*> race a task, input
  `0`, with a durable delay, input `1`. When the task completes first, the combinator
  cancels the delay and returns the task's outcome. When the delay elapses first, the
  task receives a cancellation request whose reason names the timeout, and the
  combinator waits for the task's outcome: a task which ends canceled fails it with a
  <xref:System.TimeoutException>, and any other outcome, such as a result which the task
  returns anyway, is its outcome.
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
the journal write which records the result. `WhenAny`, `Race`, and `WhenEach` select the
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
`Status` is `Pending`. Inside a durable task, a handle's `Id` names the task's operation,
which combinators and replays address, and
<xref:Orleans.DurableTasks.ScheduledTask.TargetTaskId> names the task on the grain which
stores it, which other parties address: a detached task's root ID, the task which
`GetDurableTask` observes, or a call's task on its target. For existing handles,
<xref:Orleans.DurableTasks.ScheduledTask.WhenAll*?displayProperty=nameWithType>,
<xref:Orleans.DurableTasks.ScheduledTask.WhenAllCompleted*?displayProperty=nameWithType>,
and <xref:Orleans.DurableTasks.ScheduledTask.WhenEach*?displayProperty=nameWithType>
provide the same semantics, and
<xref:Orleans.DurableTasks.ScheduledTask.Race*?displayProperty=nameWithType> returns the
winning handle once every handle has observed its outcome. A handle's `WaitAsync` and
`WaitUntilAsync` with a timeout follow the combinators' timeout rules; outside a durable
task, such as in a client, they measure the timeout on the system clock. `WhenEach`
accepts no `WithId`, so the named form schedules the inputs with
`ScheduleAll(...).WithId(name)` and enumerates `ScheduledTask.WhenEach(handles)`.

<xref:Orleans.DurableTasks.DurableTask.Delay*?displayProperty=nameWithType> and
<xref:Orleans.DurableTasks.DurableTask.DelayUntil*?displayProperty=nameWithType> are
durable timers measured in logical time. The runtime records the delay's due time and
sends the grain a scheduled resume message, which its inbox holds until the Durable Jobs
clock reaches the due time. Logical time reads the Durable Jobs clock, so due times and
the inbox share one time domain. A recorded due time is a commitment: a replay which requests
another due time, for example after a configured duration changed, keeps the recorded
one and logs a warning. Timeouts use these delays, so a timeout counts from the run which
first awaited it.

## Cancellation

Cancellation is cooperative, durable, and idempotent. A grain requests it for its own
tasks with `IDurableTaskManager.CancelAsync(rootId, reason)`, and clients and other
callers request it through
<xref:Orleans.DurableTasks.ScheduledTask.CancelAsync*?displayProperty=nameWithType>.
A request for a task which the grain doesn't retain throws `DurableTaskNotFoundException`
and changes nothing. The request commits before it propagates through the task's execution tree:

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
  observes the request at the same point as the run which it replays, so the body takes
  the same path. That point is the start of one of the body's durable operations, such as
  a child's start, a `DurableTask.RunInline` effect, or a wait for a task or a signal: a
  running body observes the request at its current operation, which for a body waiting
  durably is that wait, and the runtime records the point with the task. A replay keeps
  the durable token uncanceled until it reaches the recorded point. A replay of a run
  whose point didn't commit observes the request where it reaches the last operation
  which the earlier run recorded, and records that point. A replay which ends before its
  recorded point [stalls](durable-tasks-versioning.md#stalled-executions) as diverged.

The first request's reason is durable:
<xref:Orleans.DurableTasks.DurableTaskContext.TaskCancellationReason?displayProperty=nameWithType>
returns it in the canceled execution, its descendants, and every replay, and the
cancellation carries it to pending calls to other grains.

A body compensates inline by starting its compensation with
<xref:Orleans.DurableTasks.DurableTaskExtensions.Shielded*>. A shielded child, such as a
local step, a delay, or a call to another grain, runs to completion although the task's
cancellation was requested, and a later request skips it, while the body still owns it
and awaits its result. A shielded combinator shields each input which it starts. Every
awaited operation honors the shield the same way, so a shielded signal wait, delay,
`GetDurableTask` subscription, `Race`, `WhenAny`, or `WaitAsync` completes after the
task's cancellation. The cancellation of the attempt, by a deactivation, an abort, or the
deletion of the task's execution tree, still ends shielded operations.

An administrative abort, through `IDurableTaskManager.AbortAsync(rootId, reason)` or
<xref:Orleans.DurableTasks.ScheduledTask.AbortAsync*?displayProperty=nameWithType>,
commits the task and its execution tree as failed with
<xref:Orleans.DurableTasks.DurableTaskAbortedException> in one journal write, stops
cancels their running attempts, and sends an abort to each pending call to another grain.
A canceled attempt's durable operations throw
<xref:Orleans.DurableTasks.DurableTaskAttemptCanceledException>, and
`IsTaskCancellationRequested` stays `false`, so an abort ends a task without compensation. Use it for a task whose body ignores cancellation or whose
cleanup is stuck.

Task cancellation and attempt cancellation differ in what they record; see
[Task cancellation and attempt cancellation](durable-tasks-structured-concurrency.md#task-cancellation-and-attempt-cancellation).

## Starting and observing tasks

Configuration on a durable call sets what the task is, and the call which starts it sets
when the caller continues:

| | `await call` | `await call.ScheduleAsync()` | `call.Post()` |
| --- | --- | --- | --- |
| Attached (default) | Runs the child and returns its result once the result is durable. | Returns the child's handle once its grain has accepted and stored it. | Stages the child with the execution's next journal write and returns its handle. |
| `.Shielded()` | As attached; the caller's cancellation doesn't reach the child. | As attached; the caller's cancellation doesn't reach the child. | As attached; the caller's cancellation doesn't reach the child. |
| `.Detached()` | Starts an independent root task and returns its result. | Returns the root task's handle once its grain has accepted and stored it. | Stages the root task with the caller's next journal write and returns its handle. |

- An attached child belongs to the running execution's tree: the execution owns it, its
  cancellation reaches the child, and deleting the tree deletes the child.
- <xref:Orleans.DurableTasks.DurableTaskExtensions.Detached*> starts an independent root
  task on its target, for follow-up work, cleanup which outlives the starter, and calls
  whose results the starter doesn't need. The starter can complete while it runs, and the
  task keeps running through the starter's cancellation, abort, and deletion. Its root ID
  is the <xref:Orleans.DurableTasks.DurableTaskExtensions.WithId*> name, verbatim, so
  other parties address it by that name, or `{grainId}/{taskId}:{segment}`, where
  `{grainId}` is the starting grain and `{segment}` is the generated ID of the execution's
  next unnamed operation, so workflows with the same task ID on different grains start
  different tasks. Starters which share a task name it. The start names the execution
  as a subscriber, so awaiting the task, or its handle, receives its result; canceling
  that wait ends only the subscription.
- <xref:Orleans.DurableTasks.ConfiguredDurableTask.ScheduleAsync*> returns once the
  target grain has accepted and durably stored the task, so the task's ID, the handle's
  <xref:Orleans.DurableTasks.ScheduledTask.TargetTaskId>, can be handed to other parties,
  which address it with `GetDurableTask`. The target acknowledges the
  invocation, and the caller's outbox retires it, once the target's journal write stores
  the task. A start which the target rejects, because the task ID already identifies
  another method, throws `DurableTaskConflictException`. Other failures to accept the
  start, such as a request which the target can't decode, are retried, and a start which
  isn't accepted before Messaging's `OutboxRetryTimeout` throws
  `DurableTaskDeliveryException`. Awaiting the task throws the same failure. See [Dead letters](durable-tasks-operations.md#dead-letters).
- <xref:Orleans.DurableTasks.ConfiguredDurableTask.Post*> is synchronous. It stages the
  invocation in the caller's outbox, which commits with the grain's next journal write,
  together with the state changes which the calling code made, and delivers it once. The
  target holds the task once the invocation is delivered, so a task whose ID other
  parties look up starts with `ScheduleAsync`. Inside a durable task, the start is recorded like a `DurableTask.RunInline` effect, so a
  replay returns the same handle, and awaiting the handle later observes the result. A
  client has no outbox, so `Post` throws `InvalidOperationException` there, and
  [ORLEANS0216](../diagnostics/orleans0216.md) reports it.
- Inside a durable task, the record of a detached task, `$detach:{target}:{rootId}`,
  commits with the start, so a replay which reaches the record starts nothing. The
  starter holds the invocation, which carries the request, until its target accepts it,
  so the start survives the deletion of the starter's tree. Starting the same method under the same root ID again leaves the task unchanged.
- Outside a durable task, every task is a root task, so `.Detached()` leaves the call
  unchanged. `WithId` supplies the root ID and makes a start safe to retry: a caller which
  retries a start after an ambiguous failure, such as a timeout, reaches the same task. A
  start without `WithId` receives a new random root ID, which the handle's `Id` reports,
  and behaves like an ordinary call.

A checkout clears the basket and starts the order's workflow in one journal write:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_checkout_post" language="csharp":::

## Signals

A <xref:Orleans.DurableTasks.DurableSignal`1> carries an external event, such as a
shipment notice or an approval, into a running task. Senders and waiters share one
descriptor, which names the signal:

- <xref:Orleans.DurableTasks.DurableSignal`1.WaitAsync*> waits for the signal's value.
  <xref:Orleans.DurableTasks.DurableSignal`1.TryWaitAsync*> races the wait with a
  deadline and returns `(true, value)` or `(false, default)`, and the same outcome on
  every replay.
- A signal addresses an *invoked task*: a task which a client, another grain, or a
  durable task which detached it started on the grain. Waits anywhere in that task's execution tree
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

## Await tasks which another caller started

The `GetDurableTask<TResult>(rootId)` grain-reference extension, or its `TaskId` overload,
returns a <xref:Orleans.DurableTasks.DurableTaskReference`1> to an existing task, such as a
client-scheduled operation or a detached task. A durable task awaits the reference to
subscribe to the task's result, and other code awaits it to wait for the result. The
reference is awaitable through an extension method, so an ASP.NET Core Minimal API
handler returns it as a value, and <xref:Orleans.DurableTasks.IDurableTaskReference>
reads its `Target` and `Id` without its result type. A webhook relay, for example, waits
for an operation and then delivers its result:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_reference_relay" language="csharp":::

- In a durable task, awaiting the reference, scheduling it, or passing it to `WhenAny`,
  `Race`, `WaitAsync`, or `WaitUntilAsync` subscribes a child of the execution, recorded
  under the child ID which that operation assigns, like a call to another grain. The child
  and its subscription commit in one journal write, and a replay which reaches the child
  doesn't subscribe again.
- The subscription travels through the outbox to the task's grain as an invocation without
  a request, which neither runs nor claims the task. The grain adds the child to the task's
  subscribers, beside the task's invoker, sends the task's result through its outbox, as it
  does to the invoker, and retains the task until that completion retires. The child
  completes with the task's outcome: its result, its failure, or its cancellation.
- A subscription to a terminal task completes promptly, and a subscription to a task which
  the grain doesn't retain as an invoked task, because its retention ended or it was never
  invoked, fails promptly with `DurableTaskNotFoundException`; replays observe the same
  failure. A task ID which reaches the subscriber refers to a task which its grain holds,
  because `ScheduleAsync` returns once the grain has accepted the task. Bound the wait with
  `WaitAsync` or `WaitUntilAsync`.
- Canceling the child, for example when a `WaitAsync` deadline passes or a `Race` input
  loses, ends it as canceled and ends its subscription. The task continues.
- A subscription to a task of the same grain completes in the journal write which stores
  the task's result.
- Clients and ordinary grain methods await the same reference, or poll, cancel, or abort
  the task through it, with calls to the task's grain.

### Store and send task addresses

A <xref:Orleans.DurableTasks.DurableTaskAddress> pairs a task's grain with its ID, so code
stores a task in journaled state, sends it in grain calls, or returns it in HTTP
payloads, and reaches the task later. Orleans serializes it, and System.Text.Json writes
it as `{"Target":"order/42","Id":"place-order"}` in JSON journals and HTTP payloads, with no
registration. A <xref:Orleans.DurableTasks.TaskId> serializes as its escaped path, such as
`place-order/reserve`.

- `GrainFactory.GetDurableTask<TResult>(address)`, or the same method on an
  `IClusterClient`, returns the task's reference, which awaits, subscribes, polls,
  cancels, or aborts it, as above.
- `DurableTaskReference<TResult>.Address` reads a reference's address.
- `handle.TryGetAddress(out var address)` reads the address of a
  <xref:Orleans.DurableTasks.ScheduledTask> handle's task: a root task which a client or an
  ordinary grain method started, or, inside a durable task, a detached task or a task which
  `GetDurableTask` observes. An attached child belongs to its caller's tree, so its
  handle has no address.

A grain records the detached task which it hands off, and later awaits, polls, or cancels it,
after any number of reactivations:

:::code source="../snippets/compiled/Grains/DurableTaskIdentitySnippets.cs" id="durable_tasks_identity_store" language="csharp":::

[Task identity and references](durable-tasks-identity.md#address-a-task-from-elsewhere)
describes which tasks have addresses and how long they resolve.

## Commit model and guarantees

- `(grain, TaskId)` identifies one execution. A task ID's reuse is the same request when
  it invokes the same interface method from the same caller; arguments aren't compared,
  and reuse with a different interface or method conflicts.
- Request identity, task state, caller subscription, and inbox completion commit before
  execution begins.
- Task state and outgoing messages share the grain's journal commit boundary with the
  grain's own journaled state. A failed journal write deactivates the grain, and the
  next activation recovers from committed state.
- Each segment of an execution, the code between two durable waits, runs as a request
  on the grain, so it follows the grain's reentrancy rules as a grain method does.
- While an execution's segment runs or waits to run, the grain keeps a Durable Jobs
  wake-up, so a grain which crashes is reactivated within about 30 seconds and resumes
  the execution. An execution parked on a call's result, a signal, or a delay resumes
  when the message it waits for reactivates its grain.
- Posting a durable call from a durable grain, including from an ordinary method, stages
  the invocation in the outbox, so it commits atomically with the grain's state changes.
  Inside a durable task, awaiting and scheduling a call stage it the same way.
- Each request and result payload is journaled once per grain, in task state. A
  dead-lettered completion's dead letter also holds its result, for inspection and replay.
- Recovery precedes new execution. Child IDs, completion order, and outgoing messages
  are replay-stable.
- Terminal results and the completion notifications to callers commit together. The
  first durable terminal result wins a race between completion and cancellation.
- A body's result is staged in the work item which runs the body's final code on the
  activation, so it commits in the same journal write as the state which that code
  changed, whichever operation requests the write. A `DurableTask.RunInline` record is
  staged in the same work item as the effect's state changes, and commits with them. A
  write to journaled state, including a send through the outbox, is recorded in the same
  work item, and commits with its record.

Durability covers state and messages which participate in Orleans Journaling and
Messaging commits. External side effects, such as calls to a payment provider, need
their own idempotency, for example a request key derived from the task ID; see
[Idempotency in durable tasks](durable-tasks-idempotency.md).

## Lifecycle and retention

An *invoked task*, which a client or another grain scheduled, owns an execution tree:
the task and its descendants which aren't invoked tasks themselves, such as local
steps, delays, `DurableTask.RunInline` records, records of detached tasks, signal-send
markers, subscriptions, and the records of calls to other grains. Once an invoked task is
terminal, its grain deletes the tree:

- When a grain called the task, in the journal write which retires the completion after
  the calling grain has durably accepted the result.
- When the task has no calling grain, such as a task which a client scheduled or a
  detached task, `DurableTaskOptions.ResultRetentionPeriod` after completion. Polls
  during that period return the retained result, and later polls throw
  `DurableTaskNotFoundException`.
- In either case, not before the completion to each subscribing durable task retires.

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
