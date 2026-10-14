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
- Every root execution has an explicit `TaskId`. Child IDs append either a caller-provided
  segment or a generated segment to the parent ID. Generated segments are replay-stable when
  unnamed children are created in the same deterministic order. One `WhenAll` or `WhenAny`
  invocation assigns stable indexed slots to its concurrent batch, beneath an operation ID which
  `WithId` names. Independent concurrent
  scheduling operations, including concurrent combinator invocations, use explicit `WithId`
  segments or otherwise begin in deterministic order. Generated segments begin with `$`; that
  prefix is reserved and rejected for explicit child names.
- Scheduling the same definition under an existing ID reattaches to the response recorded
  for that ID. Hosts preserve the first definition associated with an ID.
- A host can supply an execution-abort token when constructing `DurableExecutionContext`.
  `DurableExecutionContext.CancellationToken`, which delegate and delay operations receive,
  combines host abort and durable cancellation. Host abort stops an execution attempt, for example
  to drain it before replaying it in a new context, or when an operator aborts the task;
  `IsCancellationRequested` and durable callbacks report the durable request only. An `async
  DurableTask` method reads the token from `DurableExecutionContext.Current`. Hosts discard
  responses from aborted attempts; a drained task replays from its durable state.
- A wait cancellation token abandons that scheduling, polling, or wait operation. It does
  not request durable task cancellation.
- `CancelAsync` requests durable cancellation and completes once the host has recorded the request.
  The request is monotonic and idempotent, and hosts retain it with task state. Cancellation is
  cooperative: the durable token is canceled, `IsCancellationRequested` becomes true, the request
  reaches the task's attached children, and the task's body decides its outcome. When the
  cancellation escapes the body, for example from awaiting a canceled child, the task is canceled;
  otherwise it completes with the body's result or failure. The durable token enters the canceled
  state before cancellation is published to durable callbacks, including callbacks registered
  concurrently with the request.
- `CancelAsync(reason)` attaches a reason to the request, which the task's code reads from
  `DurableExecutionContext.CancellationReason`, for example in the `catch` block which decides its
  outcome. The first request determines the reason, the host retains it with the task's cancellation
  and passes it to the task's attached children, and a replay observes the same reason.
  `CancellationReason` is `null` until cancellation is requested and when the request carries no
  reason. A host supplies the reason through `DurableTaskRuntimeHelper.RequestCancellationAsync`.
- `RegisterCancellationCallbackAsync` establishes cancellation-operation causality. Its callbacks
  execute with their durable context ambient and participate in durable dependency and failure
  tracking. Causality flows through ordinary awaits and safe `Task.Run` dispatch. Suppressing
  `ExecutionContext` flow or using unsafe dispatch detaches subsequent asynchronous work according
  to standard .NET behavior, so that work is external.
- A durable callback registered while cancellation completion is still open joins that same
  cancellation operation, even when the durable token has already been canceled. Cancellation
  completion closes registration atomically and waits for every callback accepted before that
  boundary. Their failures are included in the shared cancellation result.
- A durable callback registered after cancellation has completed starts immediately in a new
  callback causality scope. When registered from another durable callback, the new scope retains
  that callback's causality for dependency-cycle handling. Its returned registration independently
  tracks the invocation: asynchronous disposal waits for completion and propagates the callback
  failure, while the shared cancellation result remains completed.
- Callbacks registered directly on `CancellationToken` are ordinary synchronous .NET observers,
  not durable cancellation callbacks. They are for cooperative checks and synchronous cleanup only.
  They follow standard `CancellationToken` behavior: registration-time `ExecutionContext` capture,
  optional `SynchronizationContext` dispatch, and current execution-context behavior for unsafe
  registrations.
- Ordinary token callbacks must return promptly. They must not call, block on, await, or otherwise
  orchestrate `RequestCancellationAsync` or durable cancellation of any context. Violating this
  boundary is outside the contract and has the usual synchronous reentrancy and sync-over-async risks.
  All cancellation work which requests another durable context, awaits asynchronous work, needs cycle
  handling, or participates in durable failure aggregation must use
  `RegisterCancellationCallbackAsync`.
- Disposing a durable cancellation registration prevents a snapshotted callback which has not
  started, or waits for an active callback to finish, including when that callback fails. A callback
  can dispose its own registration without blocking.
- Delays use `DurableExecutionContext.UtcNow`, supplied by the host, so replay observes the
  same logical time. `DurableTask.DelayUntil` waits for an absolute due time.

### Cancellation completion and cycles

`RequestCancellationAsync` starts the durable request once and all callers observe the same
monotonic completion. Ordinary token observers run synchronously using standard
`CancellationTokenSource` cancellation semantics. External callers and acyclic durable callback
dependencies wait until durable observers finish and receive their aggregated failures. Dependency
edges are created only from cancellation operations established by
`RegisterCancellationCallbackAsync` and flowed into subsequent asynchronous work by standard
`ExecutionContext` behavior; there is no global or thread activity inference. Cycle breaking applies
only to `RequestCancellationAsync` calls made from those durable callbacks or their normally flowed
asynchronous work. If adding such an edge would close a durable callback cycle, the target
cancellation is initiated but the cycle-closing call returns without waiting on that edge. Completed
operations release their graph edges.

Cancellation completion and callback registration have one atomic boundary. Registrations accepted
before it belong to the shared operation and delay its completion. Registrations accepted after it
run independently and are observed by disposing their registration, so they never alter a result
which an existing cancellation observer could already have consumed.

The cancellation token passed to `RequestCancellationAsync` only abandons that caller's wait.
It cannot reverse or withdraw the durable request.

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

## Scheduled task handles

A `ScheduledTask` is the durable counterpart of a hot `Task`: it carries the task's ID and observes its response.
Once a wait, a poll, or a combinator observes the terminal response, the handle caches it, so `IsCompleted`, `Status`,
and `Exception` inspect the task synchronously, and `ScheduledTask<T>.GetResult()` returns the result or throws the
recorded failure or cancellation, as awaiting does. `TryGetResult(out value)` returns `true` with the result of a task
which completed successfully. Before the handle observes the terminal response, `Status` is `Pending`.

`DurableTask.ScheduleAll(inputs)` schedules every input concurrently, so a host can commit the batch in one write, and
returns their handles in input order without waiting for results. `DurableTask.WhenAllCompleted(inputs)` also waits until
every input is terminal and returns the handles, each with its observed outcome; an input's failure or cancellation
doesn't fail it, and host abort of the attempt does. `ScheduledTask.WhenAllCompleted(tasks)` waits for existing handles
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

## Detached tasks

`DetachAsync(rootId)` starts a definition as a new root task, for fire-and-forget work, cleanup
which outlives its starter, and durable calls from code which doesn't await their results:

```csharp
await buyer.RecordOrderAsync(summary).DetachAsync($"order-{orderId:N}-v{summary.Version}");
```

- The detached task has the caller-chosen root ID, so it is independent of the starter: durable
  cancellation of the starter doesn't cancel it, and the starter's lifecycle doesn't delete it.
- `DetachAsync` completes once the host has durably accepted the schedule. The starter receives no
  result; the host retains the result for polling by root ID as it does for other roots which no
  caller observes.
- Detaching the same definition under the same root ID again leaves the task unchanged. Another
  definition under that ID fails like other ID reuse.
- Within a durable execution, the host records each detach under a generated child ID of the
  execution and commits the record with the start, so a replay doesn't start the task again.
  Detach calls begin in deterministic order, like unnamed children. Outside a durable execution,
  the definition starts through `ISchedulableTask.DetachAsync`.
- Within a durable execution, `DetachedTaskId.For(segment)` derives a root ID from the execution's
  task ID, `{taskId}:{segment}`, which is unique to that execution and segment and the same on every
  replay. Segments contain no `:`. Observers derive the same ID with
  `DetachedTaskId.For(starterTaskId, segment)` to poll or signal the detached task:

```csharp
await inventory.ReleaseAsync(order.Id).DetachAsync(DetachedTaskId.For("release"));
```

## Signals

A `DurableSignal<T>` names an event which carries a value into a running task, such as a shipment
notice or a human approval. Senders and waiters share one descriptor:

```csharp
static readonly DurableSignal<Approval> Approved = new("approved");

var approval = await Approved.WaitAsync();
```

- A signal addresses one invoked task: a task which a caller, such as a client, another grain, or
  `DetachAsync`, started. A wait
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
  task. `TryWaitAsync(deadline)` races the wait with `DelayUntil(deadline)` as a `WhenAny` and returns
  `(true, value)` when the signal wins and `(false, default)` when the deadline wins. The host selects the same
  winner on replay, so a replay returns the same outcome, and `WithId` names the race like other combinators:

```csharp
var (received, approval) = await Approved.TryWaitAsync(deadline).WithId("approval");
```

- After the deadline wins, the signal keeps its latch, so a later `WaitAsync()` returns a value which
  arrives after the deadline. As a `WhenAny` input, a wait races other inputs, and after the signal wins,
  awaiting `WaitAsync()` again returns the recorded value.
- Durable cancellation of the waiting execution cancels its wait.

## Compensating canceled work

A canceled workflow keeps running until its body completes, so it can undo the work it has done. It
runs compensation inline in a `DurableExecutionContext.SuppressCancellation()` scope: the children it
schedules in the scope run to completion, and the workflow awaits their results as usual:

```csharp
public async DurableTask<OrderResult> PlaceOrderAsync(Order order)
{
    await inventory.ReserveAsync(order.Id).WithId("reserve");
    try
    {
        return await payment.ChargeAsync(order.Id, order.Total).WithId("charge");
    }
    catch (OperationCanceledException) when (DurableExecutionContext.Current!.IsCancellationRequested)
    {
        using (DurableExecutionContext.SuppressCancellation())
        {
            await inventory.ReleaseAsync(order.Id).WithId("release");
        }

        return OrderResult.Canceled(DurableExecutionContext.Current.CancellationReason);
    }
}
```

- Children scheduled in the scope, including local steps, delays, calls to other grains, and combinator
  inputs, run even though the workflow's cancellation was requested, and its cancellation doesn't reach
  them later, including after a replay. The workflow's own token, `IsCancellationRequested`, and
  `CancellationReason` still report its cancellation. An administrative abort still stops them, and
  they are deleted with the workflow's execution tree.
- The scope follows the asynchronous flow which entered it, across awaits, and covers only children of
  the execution which entered it. Scopes nest, and disposing a scope restores the state which preceded
  it.
- Work which must outlive the workflow, such as cleanup which continues after the workflow's result is
  observed, runs as a detached task, which the workflow's cancellation doesn't reach either:
  `inventory.ReleaseAsync(order.Id).DetachAsync(DetachedTaskId.For("release"))`.
- Hosts terminalize leaves of a canceled task as canceled promptly: delays, children whose bodies
  haven't started, and attached children scheduled after the request outside a suppression scope,
  which don't run. A child which already completed keeps its recorded result, and a running child
  observes the request itself.
- A replay of a canceled task observes the request from the start, so the body takes the same path.
## Aborting tasks

`ScheduledTask.AbortAsync(reason)` is for operators, for example when a body ignores cancellation or
its cleanup is stuck. The host terminalizes the task and its attached descendants as failed with
`DurableTaskAbortedException`, whose message is the reason, and signals host abort to their running
attempts, whose responses it discards. Abort runs no task code: neither durable cancellation callbacks
nor compensation run. Awaiting an aborted task throws `DurableTaskAbortedException`. Aborting a
terminal task leaves it unchanged, and detached tasks, which are independent roots, are aborted
separately.

## Writing replay-safe workflows

A host replays an execution which didn't finish, for example after its grain is deactivated: the
method body runs again from the start, and every child which already completed returns its
recorded result. A workflow resumes correctly when each replay
schedules the same children, with the same identifiers and arguments, in the same order.

- Put each side effect in a child: a durable call to another grain, a `DurableTask.Run`
  delegate, or an `async DurableTask` method awaited with `WithId`. The child runs once, and a
  replay returns its recorded result. Code between children runs on every replay, so it
  computes values from the method's arguments, the results of earlier children, and other
  values which are the same on every replay.
- Read time from `DurableExecutionContext.UtcNow`, and produce random values and identifiers
  inside children, so a replay observes the values the first execution recorded.
- Give stable identifiers with `WithId` to children which are scheduled conditionally, in loops,
  or concurrently outside a combinator, and to combinators. Generated identifiers are stable only when unnamed
  children and combinators are created in the same order.
- Scheduling an existing identifier with different arguments fails, so a replay which diverges
  from the recorded execution surfaces as an error.
  When a deployment changes a workflow while executions are pending, keep the existing steps
  and identifiers, and give new steps new identifiers.
- An `async DurableTask` method which completes without awaiting is a normal step, for example
  a state transition whose result the parent records. The Orleans analyzers suppress CS1998 for
  such methods ([ORLEANS0028](https://aka.ms/orleans/diagnostics#orleans0028)).

## Host responsibilities

A host derives from `DurableExecutionContext`, implements `ISchedulableTask` definitions
and `IScheduledTaskHandle` handles, and persists the mapping from `TaskId` to definition,
response, and cancellation state. The host returns the recorded response when an existing
ID is scheduled again, returns the same `SelectCompletionAsync` selection for a decision ID on every call, by recording
the selection or by deriving it from a durable completion order, and supplies
replay-consistent `UtcNow` values. It runs and replays the body of a canceled task with the durable
request and its recorded reason applied, and terminalizes canceled leaves as canceled. Hosts which record
cancellation reasons implement `IScheduledTaskHandle.CancelAsync(reason, cancellationToken)`. Hosts which
support cancellation suppression read `DurableExecutionContext.IsCancellationSuppressed` when they schedule
a child, record it with the child, and neither cancel such a child for its parent's earlier request nor
propagate the parent's later request to it. Hosts which support detached tasks
override `DurableExecutionContext.DetachTaskAsync` and implement `ISchedulableTask.DetachAsync`.
Hosts which support signals override `DurableExecutionContext.WaitForSignalAsync`, record each
task's first value for each signal name, and deliver signals from senders. Hosts which support abort
implement `IScheduledTaskHandle.AbortAsync`.
