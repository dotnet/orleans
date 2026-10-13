# Microsoft Orleans Durable Tasks

> [!WARNING]
> This source assembly contains an incubating Orleans integration and is not
> published as a NuGet package. Its APIs and persisted formats can change while
> the integration is evaluated.

`Microsoft.Orleans.DurableTasks` executes grain interface methods which return
`Orleans.DurableTasks.DurableTask` over Orleans Messaging. It owns the request
protocol, runtime adapter, storage integration, code-generation mapping,
diagnostics, cancellation propagation, completion delivery, and durable delays,
which resume through scheduled inbox messages.

## Configuration

Projects in this repository can reference the source project when declaring or
calling a durable grain interface:

```csharp
clientBuilder.AddDurableTasks();

siloBuilder.AddJournaling();
siloBuilder.AddDurableTasks();
```

The silo registration composes Messaging, which uses Durable Jobs to wake grains
with pending messages. Configure Journaling with a durable journal storage
provider and a Durable Jobs provider for production. Task state can be stored
in any journal format, including the
default JSON format and the Orleans binary format. Requests and responses are
stored as Orleans-serialized payloads, so their types need Orleans serializers
but no JSON metadata; `AddDurableTasks` registers the JSON metadata for the
remaining task state. Tests can register volatile task storage through
`AddVolatileDurableTaskStorage`.

Durable grains derive from `DurableGrain` or implement `IMessagingGrain`. During
activation setup, before journal recovery, the adapter binds its runtime to the
grain's journal and inbox.

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
- Task state and outgoing messages share the grain's Journaling commit boundary,
  including messages a grain sends to itself, which its own inbox accepts.
  Operations validate before staging and stage only changes which are safe to
  commit; a failed journal write deactivates the grain, and the next activation
  recovers from committed state.
- Inbox messages are staged synchronously, so the inbox commits each message
  with the changes it caused. Invocations, completions, and cancellations are
  accepted when they are delivered; scheduled delay resumes are processed when
  due. Execution starts, local completions, and cancellation propagation follow
  the commit.
- Each request and result payload is journaled once per grain. The callee's inbox
  accepts invocations, and the caller's inbox accepts completions, directly into
  task state, journaling only the message's processed record alongside it. The
  callee's outbox journals only the ID and delivery state of each completion, and
  the runtime materializes the message from the stored result for every delivery
  attempt. Task state persists
  request and result payloads separately from the task's metadata, so updates to
  the metadata do not rewrite them.
- Terminal responses and completion notifications commit together. The callee
  retains the result until the completion retires from its outbox. When the
  calling grain has durably accepted it, the callee prunes the task in the same
  journal write, keeping a tombstone which rejects conflicting reuse of its
  identity. For tasks scheduled by clients, the result remains available for
  `DurableTaskOptions.ResultRetentionPeriod` after completion; when the completion
  is dead-lettered, for that period after the dead letter. External clients poll
  retained results. Polls interleave with the grain's other requests and
  messages, so a waiting client doesn't delay the work which completes the task.
- A completion whose stored result no longer decodes, for example after a later
  deployment removes the result type, can't be materialized. The outbox
  dead-letters that completion, recording the decoding error as the reason, and the
  runtime retires it like any dead-lettered completion, without decoding the result.
  Activation doesn't decode stored results, so the grain keeps processing other
  work; operations which read that result, such as polling it, surface the decoding
  error to their caller.
- Cancellation is monotonic, duplicates are idempotent, and the first durable
  terminal result wins a race.
- Delays persist their logical due time and generation. Each delay sends a
  `durable-rpc/resume` message to its own grain, with `Envelope.DeliverAt` set to
  the due time. The grain's own inbox accepts the message in the delay's journal
  write, without an outbox round trip, so the delay and its resume commit together
  in one write, and the inbox processes the resume once the Durable Jobs clock
  reaches the due time. Resumes are generation-fenced: a resume for a canceled delay
  terminalizes it as canceled, and duplicate and stale resumes are harmless.
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
