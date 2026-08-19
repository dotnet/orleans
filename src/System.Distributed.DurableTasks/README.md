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
  context ambient. Callbacks registered directly on `CancellationToken` follow standard .NET
  registration `ExecutionContext` capture semantics instead.
- Disposing a durable cancellation registration prevents a snapshotted callback which has not
  started, or waits for an active callback to finish, including when that callback fails. A callback
  can dispose its own registration without blocking.
- Delays use `DurableExecutionContext.UtcNow`, supplied by the host, so replay observes the
  same logical time.

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
