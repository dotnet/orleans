# Microsoft Orleans Durable Tasks

> [!WARNING]
> This source assembly contains an incubating Orleans integration and is not
> published as a NuGet package. Its APIs and persisted formats can change while
> the integration is evaluated.

`Microsoft.Orleans.DurableTasks` executes grain interface methods which return
`Orleans.DurableTasks.DurableTask` over Orleans Messaging. It owns the request
protocol, runtime adapter, storage integration, code-generation mapping,
diagnostics, cancellation propagation, completion delivery, signals, and durable delays,
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

A durable grain observes, cancels, and aborts its own tasks through `IDurableTaskManager`, which
it injects from its activation services. The manager runs against the activation's
runtime, without a grain call to the grain itself:

```csharp
public sealed class OrderGrain(IDurableTaskManager tasks) : DurableGrain, IOrderGrain
{
    public async Task<bool> CancelAsync()
    {
        await tasks.CancelAsync(TaskId.CreateRoot(PlaceOrderTaskId(this.GetPrimaryKey())));
        return true;
    }
}
```

`DetachAsync` starts a durable grain call as a detached root task on its target, for
fire-and-forget work, cleanup which outlives a canceled workflow, and durable calls from
ordinary grain methods:

```csharp
await buyer.RecordOrderAsync(summary).DetachAsync($"order-{orderId:N}-v{summary.Version}");
```

- In a durable task, the call and a marker in the execution's tree commit in one journal
  write. A replay which reaches the marker doesn't send the call again, and the marker is
  deleted with the tree.
- In an ordinary method of a durable grain, the call is staged with the grain's pending
  state changes, and `DetachAsync` completes once the journal write which commits them
  completes.
- From a client, or code outside a durable grain, `DetachAsync` completes once the target
  has scheduled the root.

A call to the grain itself starts as a local root after the commit; a call to another
grain is an invocation in the outbox. The starter stores no state for the detached task.
Its target retains it like a client-scheduled root, which `GetDurableTask<T>(rootId)`
polls, and sends its result to no grain.

A `DurableSignal<T>` carries an external event, such as a shipment notice or a human
approval, into an invoked task. The task's execution waits with `WaitAsync()`, and senders
deliver a value to `(grain, task ID, signal name)`:

```csharp
static readonly DurableSignal<Shipment> Shipped = new("shipped");

// In the workflow, which a client or another grain invoked under its task ID.
var shipment = await Shipped.WaitAsync();

// From a client, or from a durable task on another grain.
await order.SignalDurableTaskAsync(TaskId.CreateRoot(workflowId), Shipped, shipment);

// From the grain's own methods.
await tasks.SignalAsync(TaskId.CreateRoot(workflowId), Shipped, shipment);
```

- The invoked task records the first value for each name as a terminal record in its
  execution tree, `{taskId}/$signal:{name}`, together with the change which delivered it.
  Every wait in the tree for that name, including waits during replay, returns the recorded
  value, and the record is deleted with the tree. A signal which arrives before its wait
  completes the wait immediately.
- A repeated signal with an equal serialized value succeeds and leaves the task unchanged. A
  signal with a different value, a signal for a terminal task which holds no value for that
  name, and a signal for a task which the grain doesn't retain as an invoked task are rejected:
  callers receive `InvalidOperationException` or `DurableTaskNotFoundException`, and the inbox
  dead-letters a rejected signal message with the rejection as its reason.
- In a durable task, the send and a marker in the execution's tree commit in one journal write,
  like `DetachAsync`, so a replay which reaches the marker doesn't send it again. A signal to
  another grain is a `durable-rpc/signal` message in the outbox, whose identity covers the
  sender, target, task, name, and value; the target's inbox records the value directly in task
  state. A signal to the grain itself is recorded in the same write.
- From a client or an ordinary grain method, `SignalDurableTaskAsync` calls the target, which
  completes once the task durably holds the value. A grain signals its own tasks through
  `IDurableTaskManager.SignalAsync`, which commits with the grain's pending state changes.
- A wait advances its execution's logical time to when the signal was recorded. A deadline is
  a `WhenAny` race between the wait and `DurableTask.DelayUntil`.

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
  with the changes it caused. Invocations, completions, cancellations, and signals
  are accepted when they are delivered; scheduled delay resumes are processed when
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
  retains the result until the completion retires from its outbox.
- An invoked task, which a client or another grain scheduled as a request, owns
  an execution tree: the task and its descendants which aren't invoked tasks
  themselves, such as local children, delays, combinator decisions, and calls to
  other grains. Callers choose task identifiers, so an invoked task nested under
  another task's identifier keeps its own lifecycle. Once an invoked task is
  terminal, the grain deletes its execution tree: in the journal write which
  retires its completion when the calling grain has durably accepted it, or
  `DurableTaskOptions.ResultRetentionPeriod` after completion when it has no
  grain caller, such as a task scheduled by a client; when the completion is
  dead-lettered, for that period after the dead letter. A task identifier
  therefore deduplicates schedules for `ResultRetentionPeriod` after completion,
  and scheduling it after the deletion executes the request again. External
  clients poll retained results, and a poll after the deletion throws
  `DurableTaskNotFoundException`. Polls interleave with the grain's other
  requests and messages, so a waiting client doesn't delay the work which
  completes the task.
- Deleting an execution tree cancels its unfinished tasks: the deletion's journal
  write sends a cancellation to each pending call to another grain, and each
  running execution is signaled after that write and its result discarded. A
  deleted execution's further scheduling throws `OperationCanceledException`.
  Completions and delay resumes which arrive for deleted tasks are ignored. A
  cancellation which arrives before its invocation is recorded, so the invocation
  terminalizes as canceled when it arrives; the record is deleted once Messaging's
  `MaxOutboxRetryAge` has elapsed, after which the invocation can no longer arrive.
- A completion whose stored result no longer decodes, for example after a later
  deployment removes the result type, can't be materialized. The outbox
  dead-letters that completion, recording the decoding error as the reason, and the
  runtime retires it like any dead-lettered completion, without decoding the result.
  Activation doesn't decode stored results, so the grain keeps processing other
  work; operations which read that result, such as polling it, surface the decoding
  error to their caller.
- Cancellation is cooperative, monotonic, and idempotent. A cancellation request commits before it
  propagates: it cancels the durable token of each running execution in the task's tree, stages a
  cancellation to each pending call to another grain, whose callee applies the same rules to its own
  task, and terminalizes delays and tasks whose bodies haven't started as canceled. A started body
  decides its task's outcome: the task is canceled when the cancellation escapes it, and otherwise
  completes with the body's result or failure. Attached children which the body schedules after the
  request are canceled without running, and a body detaches work which must outlive the cancellation,
  such as compensation. Recovery replays a canceled body with its cancellation requested, so completed
  children return their recorded results. The first durable terminal result wins a race.
- An administrative abort, through `IDurableTaskManager.AbortAsync`, `IDurableTaskGrainExtension.AbortAsync`,
  or `ScheduledTask.AbortAsync`, commits the task and its execution tree as failed with
  `DurableTaskAbortedException` in one journal write, which also stages an abort to each pending call to
  another grain; that callee aborts its task's tree in turn. The write's completion signals each running
  attempt in the tree through its execution-abort token and discards its result. Abort runs no task code,
  including cancellation callbacks and compensation, and the tree's delays terminalize with it, so their
  resumes are stale. The stored exception's message is the abort reason, and the task's completion time
  records when it was aborted; diagnostics report the task as `Faulted` with
  `DurableTaskAbortedException: <reason>`. A child which a custom scheduler runs receives the abort through its
  handle, or a cancellation request when its scheduler doesn't support abort. Aborting a terminal task changes
  nothing, and an abort which arrives before its invocation is recorded as a cancellation.
- A detached task is a root on its target with no caller. Durable cancellation of its
  starter doesn't reach it, and deleting the starter's execution tree deletes only the
  marker which recorded the start. Detaching the same request under the same root ID
  again leaves the task unchanged. Another request under that ID fails like other task
  ID reuse; when the call to another grain is delivered through the outbox, the target's
  inbox dead-letters it.
- Delays persist their logical due time and generation. Each delay sends a
  `durable-rpc/resume` message to its own grain, with `Envelope.DeliverAt` set to
  the due time. The grain's own inbox accepts the message in the delay's journal
  write, without an outbox round trip, so the delay and its resume commit together
  in one write, and the inbox processes the resume once the Durable Jobs clock
  reaches the due time. Resumes are generation-fenced: a resume for a canceled delay
  terminalizes it as canceled, and duplicate and stale resumes are harmless.
  A delay's due time is a commitment: a replay which requests another due time,
  for example after a configured duration changed, keeps the recorded one and
  logs a warning. A delay canceled while its write is in flight terminalizes as
  canceled when that write completes.
- Messages use stable identities derived from the sender, receiver, task, and
  subject, so replayed sends coalesce and redeliveries are deduplicated.
- Recovery precedes new execution. Child identifiers, selection decisions, and
  outbound durable messages remain replay-stable. Each execution's logical UTC
  time is persisted before its effects commit and reused during replay; it
  advances to the recorded completion time of each child result the execution
  observes, so a delay starts when the work before it completed.
- Activation shutdown stops new execution, signals the attempt-scoped execution-abort
  token observed by delegate and delay operations and adapter-controlled waits,
  drains active execution, discards its results, and leaves committed work
  available for replay in the next activation.

Durability covers state and messages participating in Orleans Journaling and
Messaging commits. Direct external side effects require their own idempotency
guarantees.
