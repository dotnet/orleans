# Orleans.DurableTasks.Abstractions

> [!IMPORTANT]
> This incubating assembly defines an Orleans durable task API surface under active evaluation.
> The project is explicitly non-packable, and its programming model evolves as hosting
> implementations provide feedback.

This assembly defines the Orleans programming model for durable asynchronous operations under
the `Orleans.DurableTasks` namespace. An Orleans host supplies scheduling, persistence,
deterministic time, and durable cancellation. Task definitions and application code depend only
on this abstractions assembly.

## Execution model

- An `async DurableTask` call creates a deferred definition. Its compiler-generated state
  machine begins when a host runs the definition.
- Every root execution has an explicit `TaskId`. Child IDs append either a caller-provided
  segment or a replay-stable generated segment to the parent ID. Generated segments begin
  with `$`; that prefix is reserved and rejected for explicit child names.
- Scheduling the same definition under an existing ID reattaches to the response recorded
  for that ID. Hosts preserve the first definition associated with an ID.
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
  callback causality scope. It cannot retroactively change the already-completed shared cancellation
  result. Its returned registration independently tracks the invocation: asynchronous disposal waits
  for completion and propagates the callback failure.
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
  same logical time.

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
replay when the input order is stable. Their durable results contain task IDs rather than
runtime handles. `WhenAny` asks the host to persist its selected winner under a deterministic
decision ID, then leaves all other scheduled tasks running. A host can obtain a handle for a
returned ID when cancellation or further observation is required.

`ScheduledTask.WhenAny` returns the first task whose durable response arrives, including a
failed or canceled response. Caller cancellation and host wait failures are propagated. After
selection, it cancels and drains the losing wait operations without canceling the durable tasks.

## Host responsibilities

A host derives from `DurableExecutionContext`, implements `ISchedulableTask` definitions
and `IScheduledTaskHandle` handles, and persists the mapping from `TaskId` to definition,
response, and cancellation state. The host returns the recorded response when an existing
ID is scheduled again, records `SelectCompletionAsync` decisions, and supplies
replay-consistent `UtcNow` values.
