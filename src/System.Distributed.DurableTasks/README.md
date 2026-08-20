# System.Distributed.DurableTasks

> [!IMPORTANT]
> This assembly contains incubating API under evaluation while formal .NET/BCL ownership of
> the `System.Distributed.DurableTasks` namespace is reviewed. It is deliberately non-packable
> and cannot ship from this repository's release pipeline. Its presence does not indicate
> approved platform ownership or a commitment to publish this API.

This assembly defines a runtime-independent programming model for durable asynchronous
operations. A host supplies scheduling, persistence, deterministic time, and durable
cancellation. Task definitions and application code depend only on this assembly.

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
- Callbacks registered through `RegisterCancellationCallbackAsync` execute with their durable
  context ambient and participate in durable dependency and failure tracking. Their explicit
  cancellation-operation causality flows through ordinary awaits and safe `Task.Run` dispatch.
  Suppressed `ExecutionContext` flow and unsafe dispatch detach that causality and behave as
  external observers.
- Callbacks registered directly on `CancellationToken` are ordinary synchronous .NET observers,
  not durable cancellation callbacks. They follow standard registration-time `ExecutionContext`
  capture and optional `SynchronizationContext` dispatch. A callback registered outside a durable
  cancellation callback is an external observer. One registered inside an active durable callback
  inherits its cancellation-operation causality unless execution-context flow is suppressed or an
  unsafe registration API is used. This also applies to immediate registration on an already-canceled
  token: the context current at registration determines its causality.
- Regardless of inherited causality, ordinary token callbacks must return promptly and must not
  synchronously block on `RequestCancellationAsync` or any other durable cancellation completion.
  Use `RegisterCancellationCallbackAsync` for asynchronous work, awaited cross-context cancellation
  dependencies, failure aggregation, and clear cycle semantics.
- Disposing a durable cancellation registration prevents a snapshotted callback which has not
  started, or waits for an active callback to finish, including when that callback fails. A callback
  can dispose its own registration without blocking.
- Delays use `DurableExecutionContext.UtcNow`, supplied by the host, so replay observes the
  same logical time.

### Cancellation completion and cycles

`RequestCancellationAsync` starts the durable request once and all callers observe the same
monotonic completion. External callers and acyclic durable callback dependencies wait until all
observers finish and receive their aggregated failures. Dependency edges are created only from
the explicit cancellation operation flowed by `RegisterCancellationCallbackAsync`, including when
standard `ExecutionContext` capture carries that operation into an ordinary token callback. There
is no global or thread activity inference. If adding an edge would close a durable callback cycle,
the target cancellation is initiated but the cycle-closing call returns without waiting on that
edge. Completed operations release their graph edges.

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
