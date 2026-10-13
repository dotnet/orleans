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
  invocation assigns stable indexed slots to its concurrent batch. Independent concurrent
  scheduling operations, including concurrent combinator invocations, use explicit `WithId`
  segments or otherwise begin in deterministic order. Generated segments begin with `$`; that
  prefix is reserved and rejected for explicit child names.
- Scheduling the same definition under an existing ID reattaches to the response recorded
  for that ID. Hosts preserve the first definition associated with an ID.
- A host can supply an execution-abort token when constructing `DurableExecutionContext`.
  `DurableExecutionContext.CancellationToken`, which delegate and delay operations receive,
  combines host abort and durable cancellation. Host abort lets the host drain an execution attempt
  before replaying it in a new context; `IsCancellationRequested` and durable callbacks report the
  durable request only. An `async DurableTask` method reads the token from
  `DurableExecutionContext.Current`. Hosts discard responses from aborted attempts and retain the
  task's durable state for replay.
- A wait cancellation token abandons that scheduling, polling, or wait operation. It does
  not request durable task cancellation.
- `CancelAsync` requests durable cancellation. The request is monotonic and idempotent, and
  hosts retain it with task state. The durable token enters the canceled state before cancellation
  is published to durable callbacks, including callbacks registered concurrently with the request.
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
cancellation or failure.

## Combinators

`WhenAll` and `WhenAny` assign child IDs from input indexes, preserving identity across
replay when the input order is stable. They schedule their inputs concurrently, so a host can
commit the batch together. `WhenAll<T>` returns the inputs' results in input order.
`WhenAny` returns the winner's input index, and `WhenAny<T>` also returns its result; a winner
which failed or was canceled fails the combinator. `WhenAny` asks the host to persist its
selected winner under a deterministic decision ID, then leaves all other scheduled tasks running.

`ScheduledTask.WhenAny` returns the first task whose durable response arrives, including a
failed or canceled response. It captures its candidates when called, preserving the selected
task's identity when the caller subsequently reuses or changes the input collection. Caller
cancellation and host wait failures are propagated. After selection, it cancels and drains the
losing wait operations without canceling the durable tasks.

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
  task. As a `WhenAny` input it races other inputs, so a deadline is a race with `DelayUntil`; after
  the signal wins, awaiting `WaitAsync()` again returns the recorded value:

```csharp
var index = await DurableTask.WhenAny([Approved.WaitAsync(), DurableTask.DelayUntil(deadline)]);
var approval = index == 0 ? await Approved.WaitAsync() : null;
```

- Durable cancellation of the waiting execution cancels its wait.

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
  or concurrently outside a combinator. Generated identifiers are stable only when unnamed
  children are created in the same order.
- Scheduling an existing identifier with different arguments fails, so a replay which diverges
  from the recorded execution surfaces as an error.
  When a deployment changes a workflow while executions are pending, keep the existing steps
  and identifiers, and give new steps new identifiers.
- An `async DurableTask` method which completes without awaiting is a normal step, for example
  a state transition whose result the parent records. Compilers which report CS1998 for such
  methods can suppress it.

## Host responsibilities

A host derives from `DurableExecutionContext`, implements `ISchedulableTask` definitions
and `IScheduledTaskHandle` handles, and persists the mapping from `TaskId` to definition,
response, and cancellation state. The host returns the recorded response when an existing
ID is scheduled again, records `SelectCompletionAsync` decisions, and supplies
replay-consistent `UtcNow` values. Hosts which support detached tasks override
`DurableExecutionContext.DetachTaskAsync` and implement `ISchedulableTask.DetachAsync`. Hosts which
support signals override `DurableExecutionContext.WaitForSignalAsync`, record each task's first value
for each signal name, and deliver signals from senders.
