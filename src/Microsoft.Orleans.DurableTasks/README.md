# Microsoft Orleans Durable Tasks

> [!WARNING]
> This source assembly contains an incubating Orleans integration and is not
> published as a NuGet package. Its APIs and persisted formats can change while
> the integration is evaluated.

`Microsoft.Orleans.DurableTasks` executes grain interface methods which return
`Orleans.DurableTasks.DurableTask` over Orleans Messaging. It owns the request
protocol, runtime adapter, storage integration, code-generation mapping,
diagnostics, cancellation propagation, completion acknowledgement, and delayed
resume jobs.

## Configuration

Projects in this repository can reference the source project when declaring or
calling a durable grain interface:

```csharp
clientBuilder.AddDurableTasks();

siloBuilder.AddJournaling();
siloBuilder.AddDurableTasks();
```

The silo registration composes Messaging and Durable Jobs. Configure Journaling
with a durable journal storage provider and a Durable Jobs provider for
production. Task state can be stored in any journal format, including the
default JSON format and the Orleans binary format. Requests and responses are
stored as Orleans-serialized payloads, so their types need Orleans serializers
but no JSON metadata; `AddDurableTasks` registers the JSON metadata for the
remaining task state. Tests can register volatile task storage through
`AddVolatileDurableTaskStorage`.

Durable grains derive from `DurableGrain` or implement `IMessagingGrain`. During
activation setup, before journal recovery, the adapter binds its runtime to the
grain's journal, inbox, and Durable Jobs handlers.

Existing retained tasks can be observed without scheduling a new execution:

```csharp
var task = grain.GetDurableTask<MyResult>("stable-root-id");
var response = await task.GetResponseAsync(cancellationToken);
```

An absent root or a root whose result retention has expired throws
`DurableTaskNotFoundException`. A returned failed response remains an execution
failure, including when the stored user exception has that same type.

Repository consumers which need a preview package can opt this project into
packing with `-p:PackDurableTaskAdapter=true`. The project keeps package
validation disabled until its first published version establishes a baseline.

## Runtime guarantees

- `(target GrainId, TaskId)` identifies one execution. A stable request
  fingerprint detects reuse with a different interface, method, argument type,
  or argument value.
- Request identity, task state, caller subscription, inbox completion, and
  recovery ownership commit before execution begins.
- Task state and outbox messages share the grain's Journaling commit boundary.
  Operations validate before staging and stage only changes which are safe to
  commit; a failed journal write deactivates the grain, and the next activation
  recovers from committed state.
- Inbox messages are staged and completed synchronously, so the inbox commits
  each batch with the changes it caused. Execution starts, local completions,
  and cancellation propagation follow that commit.
- Terminal responses and completion notifications commit together. Durable
  callers acknowledge completion idempotently; external clients poll retained
  results.
- Cancellation is monotonic, duplicates are idempotent, and the first durable
  terminal result wins a race.
- Delays persist their logical due time and generation. Their Durable Jobs
  resumes are scheduled before the delay commits and are generation-fenced, so
  duplicate and stale resumes are harmless.
- Messages use stable identities derived from the sender, receiver, task, and
  subject, so replayed sends coalesce and redeliveries are deduplicated.
- Recovery precedes new execution. Child identifiers, selection decisions, and
  outbound durable messages remain replay-stable. Each execution's logical UTC
  time is persisted before its effects commit and reused during replay.
- Activation shutdown stops new execution, signals the attempt-scoped execution-abort
  token observed by delegate and delay operations and adapter-controlled waits,
  drains active execution, discards its results, and leaves committed work
  available for replay in the next activation.

Durability covers state and messages participating in Orleans Journaling and
Messaging commits. Direct external side effects require their own idempotency
guarantees.
