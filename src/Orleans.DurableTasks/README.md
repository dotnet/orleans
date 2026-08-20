# Microsoft Orleans Durable Tasks

> [!WARNING]
> This package is experimental. Its APIs and persisted formats can change before
> the first stable release.

`Microsoft.Orleans.DurableTasks` executes grain interface methods which return
`System.Distributed.DurableTasks.DurableTask` over Orleans Durable Messaging.
It is the Orleans-specific adapter for the portable durable-task model and owns
the request protocol, runtime, storage adapter, code-generation mapping,
diagnostics, cancellation, completion acknowledgement, and delayed resume jobs.

## Configuration

Reference this package from every project which declares or calls a durable
grain interface. Registration is opt-in:

```csharp
clientBuilder.AddDurableTasks();
siloBuilder.AddDurableTasks();
```

The silo registration composes Durable Messaging, Durable Jobs, and Journaling.
Configure a durable Journaling provider for production. Volatile task storage is
available only for tests through `AddVolatileDurableTaskStorage`.

## Guarantees

- `(target GrainId, TaskId)` identifies one execution. A stable request
  fingerprint detects reuse with a different interface, method, or arguments.
  Cleanup retains an identity tombstone so an expired identifier cannot execute
  again.
- A target starts a request only after its request identity, task state, caller
  subscription, Durable Messaging inbox acceptance, and recovery ownership have
  committed. Equivalent deliveries reattach to stored state.
- Task state and Durable Messaging inbox/outbox mutations share the grain's
  Journaling commit boundary. Failed turns are rolled back by Durable Messaging.
- A terminal response and all completion notifications commit together. The
  target retains every waiter until that caller durably records completion and
  returns an idempotent completion acknowledgement. Polling remains available
  for the configured retention period.
- Cancellation is monotonic. A cancellation tombstone is durable before child
  propagation, cancellation before invocation prevents execution, duplicates
  are harmless, and the first durable terminal result wins a race.
- Delays persist their due time and generation. Durable Jobs resumes are
  generation-fenced; stale, duplicate, unknown, canceled, or already-completed
  resumes cannot create successful task state.
- Recovery occurs before new request execution. Child identifiers and
  `WhenAny` decisions are replay-stable, and outbound effects use durable,
  idempotent messaging records.

## Limits

Durability covers state and messages participating in the Orleans Journaling and
Durable Messaging commit. Arbitrary non-durable side effects, including direct
network, file, database, or non-durable grain calls made by user code, are
outside the guarantee and can repeat after recovery. Durable Messaging remains
at-least-once transport; this adapter supplies task-level deduplication while
identity and completion records are retained.

The package currently depends on the experimental
`Microsoft.Orleans.DurableMessaging`, `Microsoft.Orleans.Journaling`,
`Microsoft.Orleans.DurableJobs`, and `System.Distributed.DurableTasks`
components.
