# Microsoft Orleans Durable Tasks

> [!WARNING]
> This source assembly is incubating and is not published as a NuGet package.
> It and `System.Distributed.DurableTasks` remain source-only until ownership
> and publication are decided. Their APIs and persisted formats can change.

The `Orleans.DurableTasks` assembly executes grain interface methods which return
`System.Distributed.DurableTasks.DurableTask` over Orleans Durable Messaging.
It is the Orleans-specific adapter for the portable durable-task model and owns
the request protocol, runtime, storage adapter, code-generation mapping,
diagnostics, cancellation, completion acknowledgement, and delayed resume jobs.

## Configuration

Projects in this repository can reference the source project when declaring or
calling a durable grain interface. Registration is opt-in:

```csharp
clientBuilder.AddDurableTasks();
siloBuilder.AddDurableTasks();
```

The silo registration composes Durable Messaging, Durable Jobs, and Journaling.
Configure a durable Journaling provider for production. Volatile task storage is
available only for tests through `AddVolatileDurableTaskStorage`.

## Guarantees

- `(target GrainId, TaskId)` identifies one execution. A stable request
  fingerprint uses canonical Orleans serialization and SHA-256 to detect reuse
  with a different aliased interface, method, argument type, or argument value.
  Cleanup retains an identity tombstone so an expired identifier cannot execute
  again.
- A target starts a request only after its request identity, task state, caller
  subscription, Durable Messaging inbox acceptance, and recovery ownership have
  committed. Equivalent deliveries reattach to stored state.
- Task state and Durable Messaging inbox/outbox mutations share the grain's
  Journaling commit boundary. Failed turns are rolled back by Durable Messaging.
- A terminal response and all completion notifications commit together. The
  target retains every waiter until that caller durably records completion and
  returns an idempotent completion acknowledgement. External clients are
  polling-only destinations, since they cannot participate in durable completion
  acknowledgement. Polling remains available for the configured retention period.
- Cancellation is monotonic. A cancellation tombstone is durable before child
  propagation, cancellation before invocation prevents execution, duplicates
  are harmless, and the first durable terminal result wins a race.
- Delays persist their due time and generation. Durable Jobs resumes are
  generation-fenced; stale, duplicate, unknown, canceled, or already-completed
  resumes cannot create successful task state.
- Recovery occurs before new request execution. Child identifiers and
  `WhenAny` decisions are replay-stable, and outbound effects use durable,
  idempotent messaging records.
- Activation shutdown stops new execution and cancels adapter-controlled waits.
  Results and failures produced after shutdown begins are discarded. Arbitrary
  user code can delay shutdown when it does not cooperate; the activation drains
  every execution before a replacement activation can replay pending requests.

## Limits

Durability covers state and messages participating in the Orleans Journaling and
Durable Messaging commit. Arbitrary non-durable side effects, including direct
network, file, database, or non-durable grain calls made by user code, are
outside the guarantee and can repeat after recovery. Durable Messaging remains
at-least-once transport; this adapter supplies task-level deduplication while
identity and completion records are retained.

The assembly currently depends on the experimental
`Microsoft.Orleans.DurableMessaging`, `Microsoft.Orleans.Journaling`,
`Microsoft.Orleans.DurableJobs`, and `System.Distributed.DurableTasks`
components.
