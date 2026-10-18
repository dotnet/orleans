# Orleans.DurableTasks.Abstractions

> [!IMPORTANT]
> This pre-release alpha package provides Orleans durable task abstractions under active
> evaluation. Its programming model can evolve between alpha releases as hosting implementations
> provide feedback.

This assembly defines the Orleans programming model for durable asynchronous operations under
the `Orleans.DurableTasks` namespace. An Orleans host supplies scheduling, persistence,
deterministic time, and durable cancellation. Task definitions and application code depend only
on this abstractions assembly.

## Execution model

- An `async DurableTask` call creates a single-use deferred definition. Its compiler-generated
  state machine begins when a host runs the definition and releases captured state after terminal
  completion. Create a new instance for each execution. Definitions created with `DurableTask.Run`
  can be scheduled repeatedly.
- Every root execution has a `TaskId`: the name which `WithId` assigns, or a new random ID for a
  task started outside a durable execution without a name. Child IDs append either a caller-provided
  segment or a generated segment to the parent ID. Generated segments are replay-stable when
  unnamed children are created in the same deterministic order. One `WhenAll` or `WhenAny`
  invocation assigns stable indexed slots to its concurrent batch, beneath an operation ID which
  `WithId` names. Independent concurrent
  scheduling operations, including concurrent combinator invocations, use explicit `WithId`
  segments or otherwise begin in deterministic order. A segment which `WithId` repeats in one run
  identifies its later occurrences by their order, `line`, `line$2`, and so on, so children named in
  a loop keep their IDs on replay. Generated segments contain `$`, which explicit names don't.
- A `TaskId` formats and parses as its escaped hierarchical path, such as `order-42/charge`, where `\`
  escapes `/` and `\` within a segment. System.Text.Json serializes it as that string, as a value or a
  dictionary key, through `TaskIdJsonConverter`, so JSON payloads and source-generated contexts carry
  task IDs without registration.
- Scheduling the same definition under an existing ID reattaches to the response recorded
  for that ID. Hosts preserve the first definition associated with an ID.
- A wait cancellation token abandons that scheduling, polling, or wait operation. It does
  not request durable task cancellation.
- `CancelAsync` requests durable *task cancellation* and completes once the host has recorded the
  request. The request is monotonic and idempotent, and hosts retain it with task state. Cancellation
  is cooperative: `DurableTask.Current.CancellationToken` is canceled,
  `IsTaskCancellationRequested` becomes true, the request reaches the task's attached children, and
  the task's body decides its outcome. When the cancellation escapes the body, for example from
  awaiting a canceled child, the task is canceled; otherwise it completes with the body's result or
  failure. The body's compensation, such as `DurableTask.RunInline` effects, shielded children, and
  journaled state changes, commits.
- `CancelAsync(reason)` attaches a reason to the request, which the task's code reads from
  `DurableTask.Current.TaskCancellationReason`, for example in the `catch` block which decides its
  outcome. The first request determines the reason, the host retains it with the task's cancellation
  and passes it to the task's attached children, and a replay observes the same reason.
  `TaskCancellationReason` is `null` until cancellation is requested and when the request carries no
  reason. A host supplies the reason through `DurableTaskRuntimeHelper.RequestTaskCancellation`.
- A host cancels an *attempt*, one in-memory run of a task's body, with
  `DurableTaskRuntimeHelper.CancelAttempt` when the attempt's work can't be recorded any more: its
  grain activation is stopping, the task was aborted or deleted, or the running code can't replay the
  task's recorded execution (`UnsupportedVersion` or `Diverged`). From then on, every durable
  operation of the attempt throws `DurableTaskAttemptCanceledException`, which derives from
  `OperationCanceledException` and carries the `DurableTaskAttemptCancellationReason`: `RunInline`, starting
  or awaiting children, including shielded and detached ones, `Post`, `ScheduleAsync`, signals,
  delays, handle operations, and combinators. A wait which the attempt's cancellation ends throws it
  too. The host discards the attempt's response; a stopping activation's task replays in a new
  attempt, and a task whose attempt can't replay its record stays pending until code which can
  replay it runs. Changes which the attempt's code makes to journaled grain state, such as cleanup,
  commit with the grain's next journal write.
- `DurableTask.Current.CancellationToken` is canceled by task cancellation and by attempt
  cancellation, so the calls which ordinary task code makes end promptly in both cases.
  `DurableTask.Current.AttemptCancellationToken` is canceled only by attempt cancellation. Code which
  runs durable cleanup after task cancellation, in a `catch` filtered on
  `IsTaskCancellationRequested`, passes `AttemptCancellationToken` to the calls it makes, because
  `CancellationToken` is already canceled, and
  [ORLEANS0217](https://aka.ms/orleans/diagnostics#orleans0217) reports cleanup which passes
  `CancellationToken`. In-process cleanup, such as releasing a semaphore or
  disposing a resource, belongs in `finally` blocks and `using` statements, which run for both.
- Registrations on either token are ordinary synchronous .NET observers, which run when the token is
  canceled and return promptly.
- Delays use `DurableTask.Current.UtcNow`, supplied by the host, so replay observes the
  same logical time. `DurableTask.DelayUntil` waits for an absolute due time.
## Responses

`DurableTaskResponse` has pending, subscribed, succeeded, canceled, and failed states.
Pending and subscribed responses are incomplete. Succeeded, canceled, and failed responses
are terminal, and awaiting a terminal response returns its value or throws its recorded
cancellation or failure. A host's wait returns a terminal response; `ScheduledTask.WaitAsync` and the combinators
which return or yield observed handles, such as `ScheduledTask.WhenAny`, `ScheduledTask.WhenEach`, and
`WhenAllCompleted`, throw `InvalidOperationException` when a wait returns an incomplete response.

## Combinators

`WhenAll` and `WhenAny` accept a list or any sequence of inputs, which they capture when called. Awaited,
a combinator runs as part of the awaiting execution and records no result of its own. Each input runs as a
child identified by its input index under the combinator's operation identifier, so its identity is stable
across replay when the input order is stable. The combinators schedule their inputs concurrently, so a host
can commit the batch together.

Each combinator is also an extension method on its input collection, so a list, an array, or a query composes in
place:

```csharp
var reserved = await lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units))
    .WhenAll()
    .WithId("reserve");
```

Handles compose the same way: `handles.WhenAll(cancellationToken)`, `handles.WhenAny()`, `handles.Race()`,
`handles.WhenAllCompleted()`, and `handles.WhenEach()`. A collection expression such as `[a, b]` has no type of its own, so pass it to the static form,
`DurableTask.WhenAll([a, b])`.

`WhenAll` follows `Task.WhenAll`: it completes only after every input is terminal, including inputs which
durable cancellation reaches. Awaiting it then throws the first failure in input order when any input failed;
otherwise it throws the first input's `OperationCanceledException` in input order when any input was canceled;
otherwise `WhenAll<T>` returns an array of the results in input order. Input order selects the exception, so a
replay observes the same one in any completion order. `WhenAny` returns the winner's input index,
and `WhenAny<T>` also returns its result; a winner which failed or was canceled fails the combinator. The winner is the
input which completed first, and the host selects the same winner on replay: it derives the winner from a durable
completion order, or persists its selection under a deterministic decision ID. `WhenAny` leaves all other scheduled
tasks running. Durable cancellation reaches the inputs, which are attached children, and the combinators wait for
their outcomes.

`Race` keeps structured concurrency: it schedules its inputs and selects the winner like `WhenAny`, then requests
durable cancellation of every other input with the reason `Another task won the race.`, and completes once every input
is terminal, so no input outlives it. It returns what `WhenAny` returns, a winner which failed or was canceled fails it,
and the other inputs' outcomes don't affect it. `WaitAsync(timeout)` and `WaitUntilAsync(dueTime)` race a definition, as
input `0`, with a durable `Delay` or `DelayUntil`, as input `1`. A replay keeps the delay's recorded due time, so a
timeout counts from the first run. When the delay elapses first, the task receives a cancellation request with a reason
such as `The operation timed out after 00:02:00.`, and the combinator waits for its outcome: a task which ends canceled
fails it with a `TimeoutException` whose inner exception is the task's `OperationCanceledException`, and any other
outcome, such as a result which the task returns after observing the request, is its outcome:

```csharp
// The authorization runs as authorize/0 and its timeout as authorize/1.
var payment = await Payments.AuthorizeAsync(order.Total).WaitAsync(TimeSpan.FromMinutes(2)).WithId("authorize");
```

A combinator's generated operation identifier, such as `$when-all-2`, counts the combinators which ran before it in
the execution. `WithId` names the operation, so its inputs keep their identifiers when a deployment adds or removes
earlier combinators:

```csharp
var reserved = await DurableTask.WhenAll(lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units)))
    .WithId("reserve");
```

The inputs are `{taskId}/reserve/0`, `{taskId}/reserve/1`, and so on, and a host which records selections records a
named `WhenAny`'s winner under `{taskId}/{name}/$winner`. `WithId(name).ScheduleAsync()` schedules the combinator as a
child task under that identifier, which runs as its own execution, records the combined result, and identifies its
inputs under a generated operation identifier of that execution.

`ScheduledTask.WhenAll` waits for scheduled tasks like `WhenAll`: it completes after every task is terminal, then throws
the first failure in input order, or otherwise the first cancellation in input order. `ScheduledTask.WhenAny` returns the
first task to complete, including a failed or canceled task. Within a durable execution, the host selects it like
`WhenAny`'s winner, from its durable completion order or from a selection which it records under `$winner` beneath a
generated `$when-any-N` operation identifier, so every replay returns the same task. Outside a durable execution, it
returns the first task whose durable response arrives. It captures its candidates when called, preserving the selected
task's identity when the caller subsequently reuses or changes the input collection, and the returned task has observed
its terminal response. Caller cancellation and host wait failures are propagated. Outside a durable execution, after
selection, it cancels and drains the losing wait operations without canceling the durable tasks.

`ScheduledTask.Race` selects its winner like `ScheduledTask.WhenAny`, under a generated `$race-N` operation identifier
within a durable execution, then requests durable cancellation of the other tasks and returns the winner once every task
is terminal, so each handle's outcome is observable. A handle's `WaitAsync(timeout)` and `WaitUntilAsync(dueTime)`
return `Task` and `Task<T>`, with the outcomes of the definition overloads. Within a durable execution, the timeout is a
durable delay which the execution schedules as `1` beneath a generated `$timeout-N` operation identifier, and the host
selects the winner; outside one, the timeout elapses on the system clock, and the handle receives the cancellation
request when it does. Their cancellation tokens abandon the wait without canceling the task.

## Scheduled task handles

A `ScheduledTask` is the durable counterpart of a hot `Task`: it carries the task's ID and observes its response.
Once a wait, a poll, or a combinator observes the terminal response, the handle caches it, so `IsCompleted`, `Status`,
and `Exception` inspect the task synchronously, and `ScheduledTask<T>.GetResult()` returns the result or throws the
recorded failure or cancellation, as awaiting does. `TryGetResult(out value)` returns `true` with the result of a task
which completed successfully. Before the handle observes the terminal response, `Status` is `Pending`.

A handle's `Id` names the operation in the execution which started or observes the task, which combinators and replays
address, and its `TargetTaskId` names the task on its host, which other parties address. The two are equal unless the
host stores the task under another identifier: a detached task's handle reports its root ID, and a host can scope the
tasks which callers invoke by the caller, through `IScheduledTaskHandle.TargetTaskId`. A host describes a task further
through features, which `ScheduledTask.GetFeature<TFeature>()` returns from `IScheduledTaskHandle.GetFeature<TFeature>()`,
such as the address under which other parties reach the task.

`DurableTask.ScheduleAll(inputs)` schedules every input concurrently, so a host can commit the batch in one write, and
returns their handles in input order without waiting for results. `DurableTask.WhenAllCompleted(inputs)` also waits until
every input is terminal and returns the handles, each with its observed outcome; an input's failure or cancellation
doesn't fail it, and the cancellation of the attempt does. `ScheduledTask.WhenAllCompleted(tasks)` waits for existing handles
the same way. Their inputs are identified like `WhenAll` inputs, under `$schedule-all-N` or `$when-all-completed-N`, or
under the segment which `WithId` assigns. The handles belong to the awaiting execution, so these combinators run only
when awaited within a durable execution, and scheduling one as a task of its own with `ScheduleAsync()` throws
`NotSupportedException`.

`WhenAllCompleted` compensates exactly the inputs which succeeded:

```csharp
var reservations = await DurableTask.WhenAllCompleted(
        lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units)))
    .WithId("reserve");
var reserved = lines.Where((line, i) => reservations[i].TryGetResult(out var ok) && ok).ToList();
if (reserved.Count < lines.Count)
{
    await DurableTask.WhenAll(reserved.Select(line => Catalog(line.ProductId).ReleaseAsync(OrderId))).WithId("release");
}
```

## Completion order

`ScheduledTask.WhenEach(tasks)` yields each task once, in completion order, as an `IAsyncEnumerable`, and
`DurableTask.WhenEach(inputs)` schedules its inputs under a generated `$when-each-N` operation identifier when the
enumeration begins, then yields them the same way. A yielded handle has observed its terminal response, and a failure or
a cancellation doesn't fail the enumeration. Inputs which the enumeration doesn't reach keep running. The enumeration's
cancellation token abandons scheduling the inputs and waiting for them, without canceling them.

Within a durable execution, the host selects each next task: the remaining task which completed first in its durable
completion order. Every replay yields the same order, including a replay in which more tasks have already completed, so
code which acts on each result as it arrives replays the same steps:

```csharp
var quotes = await DurableTask.ScheduleAll(suppliers.Select(supplier => Supplier(supplier).QuoteAsync(order)))
    .WithId("quotes");
await foreach (var quote in ScheduledTask.WhenEach(quotes))
{
    if (quote.TryGetResult(out var offer) && offer.Price <= budget)
    {
        return await Supplier(offer.SupplierId).AcceptAsync(order).WithId("accept");
    }
}
```

`ScheduleAll(...).WithId(name)` names the inputs, as in this example. Outside a durable execution,
`ScheduledTask.WhenEach` yields tasks in the order in which their waits observe their responses.

## Starting and observing tasks

Configuration on the definition sets what a task is, and the call which starts it sets when the caller
continues:

| | `await definition` | `await definition.ScheduleAsync()` | `definition.Post()` |
| --- | --- | --- | --- |
| Attached (default) | Runs the child and returns its result once the result is durable. | Returns the child's handle once its host has accepted and durably stored it. | Stages the child with the execution's next durable write and returns its handle. |
| `.Shielded()` | As attached; the caller's cancellation doesn't reach the child. | As attached; the caller's cancellation doesn't reach the child. | As attached; the caller's cancellation doesn't reach the child. |
| `.Detached()` | Starts an independent root task and returns its result; canceling the wait ends only the caller's subscription. | Returns a handle to the root task once its host has accepted and durably stored it. | Stages the root task with the caller's next durable write and returns its handle. |

- Within a durable execution, an attached child belongs to the execution's tree: the execution owns it,
  its durable cancellation reaches it, and deleting the execution's tree deletes it.
- `.Shielded()` keeps the execution's cancellation from reaching the child, while the execution still
  owns it and waits for it; see [Compensating canceled work](#compensating-canceled-work).
- `.Detached()` starts an independent root task. The starting execution can complete while it runs,
  and the execution's cancellation, abort, and deletion don't reach it. The start names the execution
  as a subscriber, so awaiting the task, or its handle, observes its result without a separate
  subscription; canceling that wait ends only the subscription. Its root ID is the `WithId` name,
  verbatim, so other parties can address it, or `{hostId}/{executionId}:{segment}`, where `{segment}`
  is the generated ID of the execution's next unnamed operation and `{hostId}` is the identity which the
  host supplies through `DurableTaskContext.HostId`, such as its grain, so executions with the same ID
  on different hosts derive different root IDs. Starting it again under the same root ID, including
  during a replay, reaches the same task.
- `ScheduleAsync()` returns once the task's host holds the task, so its ID, the handle's `TargetTaskId`,
  can be handed to other parties, which address it with lookups such as `GetDurableTask`. A start which its host rejects, for
  example because the ID already identifies another definition, fails. Within a durable execution, the execution
  observes each attached child which it schedules by awaiting the handle, passing it to a combinator, or returning it.
- `Post()` is synchronous: it stages the start, and the start commits with the caller's next durable
  write, together with the state changes which the calling code made, and is then delivered once.
  Within a durable execution, the start is recorded like a `DurableTask.RunInline` effect, so a replay
  returns the same handle without starting the task again, and awaiting the handle later observes
  the result. Code without a durable outbox, such as a client, can't post.
- Outside a durable execution, every task is a root task, so `.Detached()` changes nothing. `WithId`
  supplies the root ID, which makes a start safe to retry: a caller which retries after an ambiguous
  failure, such as a timeout, reaches the same task. A start without `WithId` receives a new random
  root ID, which the handle's `Id` reports, and behaves like an ordinary call.

A workflow which starts follow-up work and completes without waiting for it posts a detached task:

```csharp
order.PlaceOrderAsync(request).Detached().WithId($"place-order-{orderId}").Post();
```

## Signals

A `DurableSignal<T>` names an event which carries a value into a running task, such as a shipment
notice or a human approval. Senders and waiters share one descriptor:

```csharp
static readonly DurableSignal<Approval> Approved = new("approved");

var approval = await Approved.WaitAsync();
```

- A signal addresses one invoked task: a task which a caller, such as a client, another grain, or
  a durable execution which detached it, started. A wait
  anywhere in that task's execution observes the task's signals, including a wait in an
  `async DurableTask` step the task awaits and a wait in a `WhenAny` input. Each invoked task has
  its own signals.
- Each name latches one value per task. The host records the first value with the task's state
  and returns it to every wait for that name, including waits during replay. A repeated signal with
  an equal value leaves the task unchanged, and a signal with a different value is rejected.
  `For(suffix)` derives a distinct name, such as `approved/round-2`, for each occurrence of a
  repeated event.
- A signal which arrives before its wait is recorded, and the wait returns it immediately.
- `WaitAsync()` awaited directly runs as part of the awaiting execution, so it creates no child
  task. `TryWaitAsync(deadline)` races the wait with `DelayUntil(deadline)` as a `Race` and returns
  `(true, value)` when the signal wins and `(false, default)` when the deadline wins. The race cancels the
  other input and completes once both are terminal. The host selects the same winner on replay, so a replay
  returns the same outcome, and `WithId` names the race like other combinators:

```csharp
var (received, approval) = await Approved.TryWaitAsync(deadline).WithId("approval");
```

- After the deadline wins, the signal keeps its latch, so a later `WaitAsync()` returns a value which
  arrives after the deadline. As a `WhenAny` input, a wait races other inputs, and after the signal wins,
  awaiting `WaitAsync()` again returns the recorded value.
- Durable cancellation of the waiting execution cancels its wait.

## Compensating canceled work

A canceled workflow keeps running until its body completes, so it can undo the work it has done. It
starts compensation with `.Shielded()`: a shielded child runs to completion, and the workflow awaits
its result as usual:

```csharp
public async DurableTask<OrderResult> PlaceOrderAsync(Order order)
{
    await inventory.ReserveAsync(order.Id).WithId("reserve");
    try
    {
        return await payment.ChargeAsync(order.Id, order.Total).WithId("charge");
    }
    catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
    {
        await inventory.ReleaseAsync(order.Id).WithId("release").Shielded();
        return OrderResult.Canceled(DurableTask.Current.TaskCancellationReason);
    }
}
```

- A shielded child, such as a local step, a delay, or a call to another grain, runs even though the
  workflow's cancellation was requested, and the workflow's cancellation doesn't reach it later,
  including after a replay. The workflow's own `CancellationToken`, `IsTaskCancellationRequested`, and
  `TaskCancellationReason` still report its cancellation. An administrative abort still stops a
  shielded child, and it is deleted with the workflow's execution tree. Canceling it through its own handle
  cancels it and its children.
- A shielded child's own children are reached only by the child's cancellation, so a shielded
  `DurableTask.Run` step and the work it starts run to completion. A shielded combinator runs as part
  of the workflow and shields each input which it starts.
- Every awaited durable operation honors `.Shielded()` the same way, whether it runs as a child or as
  part of the workflow: a shielded signal wait, `TryWaitAsync`, delay, `GetDurableTask` subscription,
  `Race`, `WhenAny`, or `WaitAsync` completes after the workflow's cancellation, and only the
  cancellation of the attempt ends it. A workflow which waits for a payment with
  `await Paid.WaitAsync().Shielded()` still receives a
  payment which arrives after a cancellation, and decides what the payment means.
- Work which must outlive the workflow, such as cleanup which continues after the workflow's result is
  observed, runs as a detached task, which the workflow's cancellation doesn't reach either:
  `inventory.ReleaseAsync(order.Id).Detached().Post()`.
- Hosts terminalize leaves of a canceled task as canceled promptly: delays, children whose bodies
  haven't started, and attached children which aren't shielded and are scheduled after the request,
  which don't run. A child which already completed keeps its recorded result, and a running child
  observes the request itself.
- A replay of a canceled task observes the request from the start, so the body takes the same path.

## Aborting tasks

`ScheduledTask.AbortAsync(reason)` is for operators, for example when a body ignores cancellation or
its cleanup is stuck. The host terminalizes the task and its attached descendants as failed with
`DurableTaskAbortedException`, whose message is the reason, and cancels their running attempts with
the reason `TaskAborted`, whose responses it discards. The attempts' durable operations throw, and
`IsTaskCancellationRequested` stays `false`, so no compensation runs. Awaiting an aborted task
throws `DurableTaskAbortedException`. Aborting a
terminal task leaves it unchanged, and detached tasks, which are independent roots, are aborted
separately.

## Writing replay-safe workflows

A host replays an execution which didn't finish, for example after its grain is deactivated: the
method body runs again from the start, and every recorded operation returns its recorded outcome. A
workflow resumes correctly when each replay schedules the same operations, with the same
identifiers and arguments, in the same order.

Replay doesn't rebuild the grain's journaled state, which holds the latest committed changes,
including changes which later operations made. A durable body therefore has three kinds of code:

- **Recorded operations** run once per execution, and a replay returns their recorded outcomes:
  children such as durable grain calls, `async DurableTask` methods, `DurableTask.Run` delegates,
  and delays, as well as signal waits, detached and posted starts, and `DurableTask.RunInline` effects.
- **Re-executed code** precedes the body's last recorded operation and runs again in every replay.
  It computes values from the method's arguments, recorded results, `DurableTask.Current.UtcNow`,
  the cancellation state, and configuration which stays the same while executions are pending.
- **The synchronous tail** follows the body's last recorded operation, with no other `await`
  before the body returns. It runs only in a run which completes the body. Hosts which implement
  `DurableTaskContext.OnBodyCompleted` commit its state changes in the same write as the task's
  result, so no replay runs it again.

Follow these rules in re-executed code:

- Put each side effect in a recorded operation, or in the synchronous tail. Change grain state, read
  grain state whose value decides later operations, and produce random values, identifiers, and
  clock and environment values in `DurableTask.RunInline`, in a child, or in the tail, so a replay
  observes the values the first run recorded. Read logical time from `DurableTask.Current.UtcNow`. A
  tail whose result didn't commit runs again, so a value which the tail sends to an external service,
  such as an idempotency key, comes from `DurableTask.RunInline` or a step. Call external services in
  children, whose external effects need their own idempotency.
- Store immutable values, such as records and immutable collections, in journaled grain state, and
  assign a new value to change one. The state's API records each assignment, and a change made in
  place to an object which the state holds bypasses it.
- Give stable identifiers with `WithId` to children which are scheduled conditionally, in loops,
  or concurrently outside a combinator, and to combinators. Generated identifiers are stable only when unnamed
  children, unnamed `DurableTask.RunInline` effects, unnamed detached tasks, and combinators are created in the same order.
- A replay which schedules another operation, or other arguments, under an identifier which an
  earlier run recorded diverges from the recorded execution. The host cancels the attempt with
  `DurableTaskAttemptCancellationReason.Diverged` and keeps the task pending until code which
  matches the record replays it.
  When a deployment changes a workflow while executions are pending, keep the existing steps
  and identifiers, and give new steps new identifiers, or branch the change on
  `DurableTask.GetVersion`; see [Versioned changes](#versioned-changes). A changed `RunInline` delegate
  doesn't affect executions which recorded it.
- An `async DurableTask` method which completes without awaiting is a normal step whose result the
  parent records. The package's analyzers suppress CS1998 for such methods
  ([ORLEANS0224](https://aka.ms/orleans/diagnostics#orleans0224)). A synchronous state transition
  runs as a `DurableTask.RunInline` effect, which commits with the next durable write without a write of
  its own.
- Logs in re-executed code repeat on replay. `logger.ReplaySafe()` writes each event once; see
  [Replay diagnostics and logging](#replay-diagnostics-and-logging).

## Recorded effects

`DurableTask.RunInline(name, effect)` runs a synchronous effect in the caller's current work item before
it returns, with no suspension and no child execution, and records its outcome. Every later run of
the body, including a replay, returns the recorded result, or throws the recorded exception, without
running the effect:

```csharp
var summary = DurableTask.RunInline("pay", () => SetStatus(OrderStatus.Paid));
var paymentId = DurableTask.RunInline("payment-id", Guid.NewGuid);
```

- The record commits with the task's next durable write, together with the state changes which the
  effect made, such as journaled grain state, so the effect's changes and its result commit together
  and apply once. It adds no write and no await. A run which ends before that write loses both, and
  the next run runs the effect again.
- The effect passes data to the body through its result. It reads state which later steps change,
  changes state, and produces values such as identifiers and random numbers.
- A failure is recorded too, because journaled state has no rollback: changes made before the throw
  commit with the recorded exception. A result which the host can't store, for example because it
  doesn't serialize, is recorded as that failure.
- Consecutive `DurableTask.RunInline` calls in one synchronous block, and the journaled state which they
  change, commit in the same journal write, and no other turn of the grain runs between them. The
  effect runs after task cancellation too, and returning doesn't mean that its record has committed.
- A name identifies the effect as `WithId` identifies a step: `pay`, then `pay$2` for a later
  occurrence in the same run. Names don't contain `$`. `DurableTask.RunInline("get-id", Guid.NewGuid)`
  and `await DurableTask.Run(async () => Guid.NewGuid()).WithId("get-id")` use the same identifier,
  `get-id`, but run differently: the step runs when it is awaited, possibly in a later turn, as a
  child execution with its own `DurableTask.Current` and cancellation scope, and commits in a
  journal write of its own. A replay which finds one kind recorded where the body now runs the
  other diverges instead of running the work again, so a deployment which changes one into the
  other branches on `DurableTask.GetVersion`.
- `DurableTask.RunInline(effect)` omits the name for terseness, such as
  `var paymentId = DurableTask.RunInline(Guid.NewGuid);`. The effect takes the execution's next generated
  identifier, `$child-1`, `$child-2`, and so on, which unnamed effects share with unnamed children,
  so it's replay-stable when the body creates its unnamed operations in the same order. Name effects
  whose position can change between runs or deployments.
- The effect is synchronous. Asynchronous and durable effects don't compile, and a result type which
  is asynchronous through generic code, such as `ValueTask<T>`, throws `NotSupportedException`
  before the effect runs. Asynchronous work runs as an awaited step, such as
  `await DurableTask.Run(async () => ...)` or an `async DurableTask` method. An `async void` delegate,
  such as an `async` lambda cast to `Action`, returns at its first `await`, so neither an effect nor a
  step records its work.
- Synchronous work runs as an effect rather than as a step: a step which neither awaits nor observes
  its `CancellationToken` adds a child execution and a journal write of its own.
- The record applies only within a durable task. Outside one, the effect runs every time, so helpers
  which ordinary methods share with durable task bodies can call it.

## Versioned changes

`DurableTask.GetVersion(name, minSupported, maxSupported)` returns the version of a change which the
running execution follows, so one body holds the code of every version while executions which started
on earlier code are pending:

```csharp
var shipping = DurableTask.GetVersion("carrier-booking", minSupported: 0, maxSupported: 1);
var trackingId = shipping >= 1
    ? await carrier.BookAsync(orderId).WithId("book")
    : await warehouse.ShipAsync(orderId).WithId("ship");
```

- The first check of a name in an execution records the version, and every later check and replay
  returns it. An execution which reaches the check first records `maxSupported`; one which earlier
  code ran past records `0`. A step follows the version which an enclosing execution recorded.
- `DurableVersion` declares a change's name and range once, like `DurableSignal<T>`:
  `static readonly DurableVersion CarrierBooking = new("carrier-booking", 0, 1);`, read with
  `CarrierBooking.Get()` or `CarrierBooking.IsAtLeast(1)`.
- Raising `minSupported` retires a version. An execution which recorded a version outside the range
  stalls: the host cancels the attempt for `DurableTaskAttemptCancellationReason.UnsupportedVersion`,
  whose `DurableTaskAttemptCanceledException` carries a `DurableTaskVersionException`, discards the
  attempt's result, and keeps the task pending until code which supports the version replays it.
- Outside a durable task, the check returns `maxSupported`. Names don't contain `$`, and
  [ORLEANS0218](https://aka.ms/orleans/diagnostics#orleans0218) reports names and ranges which
  aren't constants.

## Replay diagnostics and logging

`DurableTask.Current.IsReplaying` reports whether a run of the body is re-running operations which an
earlier run recorded. It is `true` from the start of a run which finds operations that earlier runs
recorded in the task's tree, such as children, records of detached tasks, and `RunInline` records,
until the run has reached each of them or starts an operation which no earlier run recorded. It then
stays `false`. A first run reports `false` throughout. Signal values which callers sent to the task
don't count as recorded operations.

Use it for logging and diagnostics only. A run which ended before a journal write committed its work
is replayed, so code which runs only while `IsReplaying` is `false` can run twice or never, and code
which branches on it schedules different operations in different runs. Work which runs once belongs
in `DurableTask.RunInline`, a step, or the body's synchronous tail.

`logger.ReplaySafe()` returns a logger which skips events while the task replays, so each event is
logged once:

```csharp
private readonly ILogger _log = logger.ReplaySafe();

public async DurableTask<OrderResult> PlaceOrderAsync(SubmitOrder request)
{
    _log.LogInformation("Placing order {OrderId}", request.OrderId);
    ...
}
```

The logger reads the running task when it logs, so one instance serves durable task bodies and
ordinary grain methods. Logs inside a `DurableTask.RunInline` effect, or inside a step which completed,
don't repeat without it, because a replay returns their recorded results without running them.

The package carries analyzers which report code that a replay would run differently, or which
doesn't run as written, as diagnostics ORLEANS0200 to ORLEANS0223. Each diagnostic links to a page
which describes the hazard and its fix.

## Host responsibilities

A host derives from `DurableTaskContext`, implements `ISchedulableTask` definitions
and `IScheduledTaskHandle` handles, and persists the mapping from `TaskId` to definition,
response, and cancellation state. The host returns the recorded response when an existing
ID is scheduled again, returns the same `SelectCompletionAsync` selection for a decision ID on every call, by recording
the selection or by deriving it from a durable completion order, and supplies
replay-consistent `UtcNow` values. It runs and replays the body of a canceled task with the durable
request and its recorded reason applied, and terminalizes canceled leaves as canceled. Hosts which record
cancellation reasons implement `IScheduledTaskHandle.CancelAsync(reason, cancellationToken)`. Hosts start
tasks in `DurableTaskContext.StartTaskAsync`, which receives `DurableTaskStartOptions`: `Detached` for an
independent root task which names the execution as a subscriber, and `WaitForAcceptance` for a start which
completes once the task's host has accepted the task. They read `DurableTaskContext.SchedulesShieldedChildren`
when they start a child, record it with the child, and neither cancel such a child for its parent's earlier
request nor propagate the parent's later request to it. Hosts which can stage a start synchronously override
`DurableTaskContext.PostTask`, and definitions which can stage a root start in a durable outbox implement
`ISchedulableTask.Post`. `ISchedulableTask.ScheduleAsync` completes once the host has accepted and durably
stored the task.
Hosts which support signals override `DurableTaskContext.WaitForSignalAsync`, record each
task's first value for each signal name, and deliver signals from senders. Hosts which support abort
implement `IScheduledTaskHandle.AbortAsync`. Hosts which support recorded effects override
`DurableTaskContext.RunEffect`: they return an effect's recorded outcome, or run the effect and stage its
record in the same synchronous work, so the record commits with the state changes the effect made. Hosts
which commit a body's state changes with its result stage the result in
`DurableTaskContext.OnBodyCompleted`, which runs synchronously with the body's final code. Hosts which
track replay override `DurableTaskContext.IsReplaying`.
