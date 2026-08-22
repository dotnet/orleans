# Microsoft Orleans Durable Tasks

> [!WARNING]
> This source assembly contains an incubating Orleans integration and is not
> published as a NuGet package. Its APIs and persisted formats can change while
> the integration is evaluated.

`Microsoft.Orleans.DurableTasks` executes grain interface methods which return
`Orleans.DurableTasks.DurableTask` over Orleans Durable Messaging. It owns the
request protocol, runtime adapter, storage integration, code-generation mapping,
diagnostics, cancellation propagation, completion acknowledgement, and delayed
resume jobs.

## Configuration

Projects in this repository can reference the source project when declaring or
calling a durable grain interface:

```csharp
clientBuilder.AddDurableTasks();
siloBuilder.AddDurableTasks();
```

The silo registration composes Durable Messaging, Durable Jobs, and Journaling.
Configure a durable Journaling provider for production. Tests can register
volatile task storage through `AddVolatileDurableTaskStorage`.

## Runtime guarantees

- `(target GrainId, TaskId)` identifies one execution. A stable request
  fingerprint detects reuse with a different interface, method, argument type,
  or argument value.
- Request identity, task state, caller subscription, inbox acceptance, and
  recovery ownership commit before execution begins.
- Task state and Durable Messaging mutations share the grain's Journaling
  commit boundary.
- Terminal responses and completion notifications commit together. Durable
  callers acknowledge completion idempotently; external clients poll retained
  results.
- Cancellation is monotonic, duplicates are idempotent, and the first durable
  terminal result wins a race.
- Delays persist their due time and generation. Durable Jobs resumes are
  generation-fenced.
- Recovery precedes new execution. Child identifiers, selection decisions, and
  outbound durable messages remain replay-stable.
- Activation shutdown stops new execution, cancels adapter-controlled waits,
  drains active execution, and leaves committed work available for replay.

Durability covers state and messages participating in Orleans Journaling and
Durable Messaging commits. Direct external side effects require their own
idempotency guarantees.
