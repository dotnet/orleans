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
with pending messages or runnable executions. Configure Journaling with a durable journal storage
provider and a Durable Jobs provider for production. Task state can be stored
in any journal format, including the
default JSON format and the Orleans binary format. Requests and responses are
stored as Orleans-serialized payloads, so their types need Orleans serializers
but no JSON metadata; `AddDurableTasks` registers the JSON metadata for the
remaining task state. Task state lives in the grain's journal, which is the
source of truth for the grain's executions; tests keep it in memory with volatile
journal storage, such as `AddVolatileJournalStorage`.

Durable grains derive from `DurableGrain` or implement `IMessagingGrain`. During
activation setup, before journal recovery, the adapter binds its runtime to the
grain's journal and inbox; an activation without a journaled state manager fails
during this setup.

`GetDurableTask<T>(rootId)`, or its `TaskId` overload, returns a `DurableTaskReference<T>` to an
existing task without scheduling a new execution. Clients and ordinary grain methods await it, poll
it, cancel it, or abort it:

```csharp
var task = grain.GetDurableTask<MyResult>("stable-root-id");
var response = await task.GetResponseAsync(cancellationToken);
```

The reference is awaitable through the `GetAwaiter` extension method, so frameworks which inspect a
return type's instance members, such as ASP.NET Core Minimal APIs, treat it as a value: a handler
returns `grain.GetDurableTask<T>(id)` directly. `IDurableTaskReference` reads a reference's `Target`
and `Id` without its result type.

A `DurableTaskAddress(GrainId Target, TaskId Id)` is a task reference as a value, which code stores
in journaled state or sends in grain calls and HTTP payloads. Orleans serializes it, and
System.Text.Json writes it as `{"Target":"{grain type}/{grain key}","Id":"{task ID}"}`, with metadata
which `AddDurableTasks` registers for JSON journals. `TaskId` serializes as its escaped path through
the converter which the abstractions declare on it. `IGrainFactory.GetDurableTask<T>(address)`, which
grains and `IClusterClient` share, turns an address back into a reference, and
`DurableTaskReference<T>.Address` reads one from a reference:

```csharp
_pending.Value = order.GetDurableTask<OrderResult>(orderTaskId).Address;
await WriteStateAsync();

// Later, after any number of reactivations, in a durable task or elsewhere:
var result = await GrainFactory.GetDurableTask<OrderResult>(_pending.Value);
```

`ScheduledTask.TryGetAddress(out address)` reads the address of a handle's task through the handle's
`IDurableTaskReference` feature. Handles of root tasks, which clients and ordinary grain methods
start or which `IDurableTaskManager.GetTask` returns, and, in a durable execution, handles of detached
tasks and of `GetDurableTask` subscriptions have addresses. An attached child belongs to its caller's
tree and has none.

Waits and polls from clients and ordinary grain methods retry the failures which leave the task
unaffected: a lost connection, a stopped or crashed silo, a call timeout, or a deactivating
activation. They back off exponentially, from 50 ms to 5 s, until the task's grain answers or the
caller's token or the wait's timeout ends the wait. An absent root or a root whose result retention
has expired throws `DurableTaskNotFoundException` at once. A returned failed response remains an execution
failure, including when the stored user exception has that same type.

A durable task awaits the same reference to subscribe to the result of an invoked task which it
didn't invoke, such as a client-scheduled operation which a webhook relay reports:

```csharp
var result = await operation.GetDurableTask<OperationResult>("operation-42")
    .WaitAsync(TimeSpan.FromDays(1));
```

- Awaiting the reference, scheduling it, or passing it to a combinator such as `WhenAny`, `Race`,
  or `WaitAsync` subscribes a child of the execution, recorded under the child ID which that
  operation assigns. The child's fingerprint, `$subscribe:{target}{taskId}`, names the task, and the
  child and its subscription commit in one journal write, so a replay which reaches the child
  doesn't subscribe again and observes the recorded outcome.
- A subscription is a `durable-rpc/invoke` message which carries the child's ID instead of a
  request: the target neither schedules nor claims the task, records no caller, and validates none.
  It adds `(subscriber grain, child ID)` to the task's completion destinations, beside the invoker,
  which subscribes as `(caller, task ID)`, and the task's result reaches every destination the same
  way: through the outbox, with the task retained until the completion retires. The child completes
  with that outcome: the result, failure, or cancellation.
- A subscription to a terminal task sends its completion at once, and one to a task which the
  target doesn't retain as an invoked task completes the child with `DurableTaskNotFoundException` at
  once. A task ID which reaches the subscriber refers to a task which its target already holds:
  `ScheduleAsync` returns once the target has accepted the task, and a durable execution which starts
  a detached task subscribes through the start itself.
- Canceling the child ends it as canceled and sends an unsubscription, a `durable-rpc/cancel`
  message which names the child, which removes the destination while the task is running; the task
  continues. An abort or deletion of the child's tree ends it the same way, and a completion which
  arrives for an ended child changes nothing.
- A subscription to a task of the same grain adds the destination in task state, and the write
  which stores the task's result also completes the child.
- Outside a durable execution, the reference's polling and control members, and awaiting it, call
  the target; a durable execution doesn't record those calls.

A durable grain observes, cancels, and aborts its own tasks through `IDurableTaskManager`, which
it injects from its activation services. The manager runs against the activation's
runtime, without a grain call to the grain itself. Its overloads which take a root ID string
address the root task `TaskId.CreateRoot(rootId)`:

```csharp
public sealed class OrderGrain(IDurableTaskManager tasks) : DurableGrain, IOrderGrain
{
    public async Task<bool> CancelAsync()
    {
        await tasks.CancelAsync(PlaceOrderTaskId(this.GetPrimaryKey()), "Cancelled by the buyer");
        return true;
    }
}
```

A cancellation request can carry a reason, which the canceled workflow reads from
`DurableTask.Current.TaskCancellationReason`, for example to record why it stopped. The first
request determines the reason; `ScheduledTask.CancelAsync(reason)`,
`DurableTaskReference<T>.CancelAsync(reason)`, and `IDurableTaskManager.CancelAsync(taskId, reason)`
carry one from clients, tooling, and the task's grain.

Configuration on a durable grain call sets what the task is, and the call which starts it sets when
the caller continues:

```csharp
// In a workflow: start the order as an independent root task, in the same write as the basket's change.
_ = order.PlaceOrderAsync(request).Detached(PlaceOrderTaskId(orderId)).Post();

// Anywhere: return once the target holds the task, so its ID can be handed to other parties.
var placement = await order.PlaceOrderAsync(request).WithId(PlaceOrderTaskId(orderId)).ScheduleAsync();
```

- In a durable task, awaiting a call, scheduling it, or posting it records an attached child under its
  task ID and stages a `durable-rpc/invoke` message, whose target claims the task for the caller.
  Root IDs are scoped per grain, so the target stores the child under the caller's scope,
  `$caller/{callerGrainId}/{incarnation}/{taskId}`: two callers whose workflows share a root ID and a
  child name run separate tasks on a shared target. Messages name the child by its *wire ID*,
  `{incarnation}/{taskId}`, where `{incarnation}` is the incarnation of the caller's run, the GUID
  which the caller's grain mints when it stores the root task's invocation, formatted with `"N"`. A
  root ID which runs again after its grain deleted the earlier run has a new incarnation, so its calls
  reach new tasks and their messages aren't deduplicated against the earlier run's. The caller's
  invocation, cancellation, and abort address its child's wire ID, which the target maps to its scope,
  and the target's completion names that wire ID as its subscriber; the caller ignores a completion
  for an earlier run. Subscriptions and detached starts name their recording child by its wire ID too.
  A root ID, which a detached start sends, addresses the target's root task itself, and the root ID
  `$caller` is reserved: only calls from other grains create tasks beneath it.
  The target acknowledges the invocation once its journal write stores the task, and the starter's outbox
  retires the invocation as delivered, in the write which records the acceptance on the child.
  `ScheduleAsync` returns once that acceptance, or the task's result, is durable. `Post` stages the
  child and its invocation in the calling code's turn, so they commit with the grain's next journal
  write; a replay which reaches the child doesn't send it again. The target holds a posted task once
  the invocation is delivered, so code which hands a task's ID to other parties starts the task with
  `ScheduleAsync`.
- A detached call is an independent root task on its target. This host detaches durable grain calls:
  the target recovers the root task from the call's serialized request, so detaching any other
  `ISchedulableTask` fails the operation with `NotSupportedException` before anything is recorded or
  sent. The execution records it under
  `{taskId}/$detach:{target}:{rootId}`, which records the root ID and the request's method, so a
  replay which reaches the record doesn't start the task again, whatever the request's arguments,
  and one which starts another method under the same record diverges. The invocation carries the request and names the record
  as a subscriber, and the target adds the record to the task's completion destinations when it stores
  the task.
  The record observes the task like a `GetDurableTask` subscription: the task's result completes it,
  and its cancellation, an abort or deletion of its tree, or the starter's cancellation ends only the
  subscription. Its root ID is the `.Detached(rootId)` name, verbatim, or the single segment
  `{grainId}/{taskId}/{segment}`, in which each part escapes `/` and `\` as task IDs do, `{grainId}`
  is the starting grain and `{segment}` is the operation's segment, so workflows with the same task ID
  on different grains derive different root IDs.
- A handle's `TargetTaskId` names the task which its record started or observes, on the grain which
  stores it: the root ID of a detached task, the subscribed task of a `GetDurableTask` subscription,
  and `$caller/{grainId}/{incarnation}/{taskId}` for a call to a grain. Other parties address the task by it, such as
  a client which polls a detached task with `GetDurableTask<T>(handle.TargetTaskId)`. The handle's `Id`
  names the record, which combinators, cancellation, and replays address.
- The outbox holds every invocation of a task on another grain by reference, and the starter provides
  it from a journaled table of pending invocations, keyed by message ID, until the outbox retires it.
  The table is separate from the execution trees, so a start survives deletion of the tree which made
  it, and the invocation's payload is journaled once. A replay which reaches a start whose invocation
  is still pending coalesces with it.
- A target rejects an invocation at once only when its task ID already identifies another request or
  caller, which is final: it dead-letters the invocation with the `durable-rpc/conflict` code from
  `DurableTaskRejectionCodes` and its reason, and the starter's outbox retires the invocation as
  rejected with both. A target which fails to accept an invocation for another reason, such as a
  transient failure or a request which it can't decode, throws, so the starter's outbox retries the
  delivery until Messaging's `OutboxRetryTimeout`; during a rolling deployment, a retry can reach an
  activation which decodes it. The starter's record stores the failure as its result, and
  `ScheduleAsync`, and awaiting the task or its handle, throw it in every replay:

  | Outcome | Code | Failure |
  | --- | --- | --- |
  | The task ID already identifies another request or caller. | `durable-rpc/conflict` | `DurableTaskConflictException` |
  | The invocation arrives after the target's admission window, Messaging's `DeduplicationWindow` since the starter first staged it. The target may have run and deleted the task earlier, so the outcome is unknown. | `durable-rpc/expired` | `DurableTaskRejectedException` |
  | The target rejects the invocation with another code, or with none. | The target's code | `DurableTaskRejectedException` |
  | The outbox can't confirm the invocation before `OutboxRetryTimeout`, such as one which never decodes, or the target fences it after its admission cutoff. An earlier attempt may have reached the target, so the outcome is unknown. | | `DurableTaskDeliveryException`, with the dead letter's reason |

  An unknown outcome means the task may have run: establish whether it did, for example by polling it
  or checking its effects, before starting the work again under another task ID.

  `DurableTaskRejectedException.Code` reports the target's code, and `Target` the rejecting grain.
- In an ordinary method of a durable grain, `ScheduleAsync` calls the target directly, like a client,
  and returns once the target has stored the task; a call to the grain itself goes through the grain's
  own runtime. `Post` stages the invocation in the grain's outbox, so it commits with the grain's next
  journal write, together with the state changes which the method made, and is delivered once. The
  started task is a root task, which the grain doesn't store, and its target sends its result to no
  grain.
- A client's `ScheduleAsync` returns once the target has stored the task. A client has no outbox,
  so `Post` throws `InvalidOperationException`.
- A call to the grain itself starts as a local root after the journal write which commits it. Its
  target retains a root task like a client-scheduled root, which `GetDurableTask<T>(rootId)` polls.

A `DurableSignal<T>` carries an external event, such as a shipment notice or a human
approval, into an invoked task. The task's execution waits with `WaitAsync()`, and senders
deliver a value to `(grain, task ID, signal name)`:

```csharp
static readonly DurableSignal<Shipment> Shipped = new("shipped");

// In the workflow, which a client or another grain invoked under its task ID.
var shipment = await Shipped.WaitAsync();

// From a client, or from a durable task on another grain.
await order.SignalDurableTaskAsync(workflowId, Shipped, shipment);

// From the grain's own methods.
await tasks.SignalAsync(workflowId, Shipped, shipment);
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
- A sender can commit an invocation and a signal for it in one write, and the outbox delivers
  them independently, so a signal message can arrive first. The inbox records a signal message
  for a task which the grain doesn't hold yet, and the invocation finds the value when it
  arrives. The grain admits an invocation until Messaging's `DeduplicationWindow` has elapsed
  since its sender first staged it, and keeps the value for twice that window after it arrives,
  which covers the skew between the sender's clock and the grain's, so an admitted invocation
  finds it. When no invocation arrives within that time, the grain deletes the value and logs a
  warning.
- In a durable task, the send and a marker in the execution's tree commit in one journal write,
  like a posted start, so a replay which reaches the marker doesn't send it again, whatever value
  it passes. The marker is
  `{taskId}/$send:{target}:{task}:{name}`, and repeated sends of one signal in an execution,
  such as retries after `TaskNotFound`, are numbered `$send:...$2`, and so on, so each sends
  again and the target reports an equal value as `Duplicate`. A signal to
  another grain is a `durable-rpc/signal` message in the outbox, whose identity covers the
  sender, target, task, name, value, and the incarnation of the sending run; the target's inbox records the value directly in task
  state. A signal to the grain itself is recorded in the same write.
- From a client or an ordinary grain method, `SignalDurableTaskAsync` calls the target, which
  completes once the task durably holds the value. A grain signals its own tasks through
  `IDurableTaskManager.SignalAsync`, which commits with the grain's pending state changes.
- `IDurableTaskManager.TrySignalAsync` and, for clients and ordinary grain methods,
  `TrySignalDurableTaskAsync` return a `DurableSignalResult` instead of throwing: `Accepted` or
  `Duplicate` once the task durably holds the value, or `Conflict`, `TaskNotFound`, or
  `TaskCompleted` for a rejected signal, which changes nothing. In a durable task, the
  manager records the result with the send, so a replay returns the same result:

```csharp
var result = await tasks.TrySignalAsync(PlaceOrderTaskId(orderId), Shipped, shipment);
if (result is DurableSignalResult.Conflict or DurableSignalResult.TaskCompleted)
{
    return false; // Already shipped, or the order finished.
}
```
- A wait advances its execution's logical time to when the signal was recorded. A deadline is
  `TryWaitAsync(deadline)`, a `Race` between the wait and `DurableTask.DelayUntil`, whose
  indexed slots belong to the waiting execution's tree. The race cancels the input which
  loses and completes once both are terminal.

Repository consumers which need a preview package can opt this project into
packing with `-p:PackDurableTaskAdapter=true`. Packing validates the package's
framework assets; its first published version establishes the compatibility baseline.

## Diagnostics

`DurableTaskReference<T>.GetStatusAsync` and `GetResponseAsync` report a task's status and result.
`DurableTaskEvents`, the `Orleans.DurableTasks` diagnostic listener, reports the runtime's transitions
synchronously on the activation which makes them, when the runtime stages them: `TaskCreated`,
`TaskCompleted`, `TaskDeleted`, `ExecutionStarted`, `ExecutionStopped`, `ReplayEnded`,
`SignalWaitStarted`, `SignalWaitEnded`, and `SelectionWaitStarted`. Each event carries the activation's
`IGrainContext` and the task's ID. Tests use the events as barriers instead of polling.

## Runtime guarantees

- `(target GrainId, TaskId)` identifies one execution. A request's identity is its
  caller and the interface method which it invokes, identified by the method's
  generated invokable type; its arguments aren't compared. Scheduling a task ID
  again with the same method, from the same caller, reaches the existing task even
  when the arguments differ or serialize differently, such as after a deployment
  adds an optional field. A schedule with another method or caller throws
  `DurableTaskConflictException`, an `InvalidOperationException`, and leaves the
  existing task unchanged. Each task also stores a fingerprint of its request's
  serialized arguments, which diagnostics report and nothing compares.
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
- An activation recovers task state after its journal, inbox, and outbox have
  recovered, and starts the recovered executions once the grain's
  `OnActivateAsync` has completed.
- An execution's attempt runs in segments: the code between its durable waits.
  Each segment is a local request on the activation, which follows the grain's
  reentrancy rules, so on a non-reentrant grain a segment excludes the grain's
  other requests, as a grain method does. A durable wait which is pending, such
  as the wait for a child's result, a signal, a delay, or the first of several
  completions, parks the attempt and ends its segment; once the wait completes,
  a new segment resumes the waiting code. Other awaits, such as journal writes
  and calls to other grains, stay within the segment.
- A request which waits for a task of its own grain, such as a grain method
  which awaits `this.AsReference<T>().M()` or `IDurableTaskManager.GetTask(id)`'s
  response, must let the task's segments run alongside it: mark its interface
  method `[AlwaysInterleave]` or the grain class `[Reentrant]`, or await the task
  from durable code, whose durable wait ends its segment. A non-reentrant
  request which waits instead holds the segments behind itself until the call
  times out. Code which holds the grain's exclusive section, such as a
  transaction, holds the segments back until it closes the section, even from
  an `[AlwaysInterleave]` method, so it waits for its grain's tasks only
  outside the section.
- The runtime is an inbox work source: while the grain holds a runnable
  execution, one whose segment runs or is about to run, Messaging keeps the
  grain's Durable Jobs wake-up, which a journal write ensures before it becomes
  durable. A grain which crashes while an execution is runnable is reactivated
  within about 30 seconds, and recovery resumes the execution. When an
  activation starts, its recovered pending executions re-arm the wake-up, and
  while it deactivates, its pending executions keep the wake-up, so an
  execution which the deactivation discards while runnable resumes in the next
  activation. A
  parked execution resumes when the message it waits for reactivates the grain.
- Activation shutdown begins when deactivation starts, before the grain's
  `OnDeactivateAsync` runs. It stops new execution and cancels every running
  attempt with the reason `ActivationStopping`: from then on, the attempt's
  durable operations and its writes to journaled state throw
  `DurableTaskAttemptCanceledException`, its reads of journaled state run live
  and unrecorded, and its `AttemptCancellationToken` and `CancellationToken` are
  canceled. A parked attempt is discarded and runs no further code; a running
  segment finishes, and its result is discarded. The next activation replays
  committed work. Other grain work, such as `OnDeactivateAsync` and the inbox, keeps
  committing while segments finish. A poll or wait on
  the activation fails with `DurableTaskAttemptCanceledException`, as a call
  which arrives while the runtime stops does, and the task continues in the next
  activation.
- Each request and result payload is journaled once per grain. The callee's inbox
  accepts invocations, and the caller's inbox accepts completions, directly into
  task state, journaling only the message's processed record alongside it. The
  callee's outbox journals only the ID and delivery state of each completion, and
  the runtime materializes the message from the stored result for every delivery
  attempt. Task state persists
  request and result payloads separately from the task's metadata, so updates to
  the metadata do not rewrite them. A dead-lettered completion is the exception:
  its dead letter holds a copy of the result for inspection and replay, while
  task state keeps its own for `ResultRetentionPeriod`, so a poller or a
  re-invocation still observes the result.
- Terminal responses and completion notifications commit together. The callee
  retains the result until the completion retires from its outbox.
- A task's completion destinations are its subscribers: its invoker, each durable
  execution's child which subscribed with `GetDurableTask`, and the record of each execution which
  started the task as a detached task. Each receives the result in a
  completion which commits with the result, and the task is retained until every
  completion retires: a task with a grain caller is deleted when the last one retires,
  and a task without one keeps its result for `ResultRetentionPeriod` after completion,
  and at least until its subscribers' completions retire. A dead-lettered completion
  restarts that period. A subscriber's grain accepts the completion directly into the
  child; a completion for a child which ended, or was deleted with its tree, is
  accepted without changes.
- An invoked task, which a client or another grain scheduled as a request, owns
  an execution tree: the task and its descendants which aren't invoked tasks
  themselves, such as local children, delays, combinator decisions, subscriptions,
  records of detached tasks, and calls to other grains. Callers choose task identifiers, so an invoked task nested under
  another task's identifier keeps its own lifecycle. Once an invoked task is
  terminal, the grain deletes its execution tree: in the journal write which
  retires its completion when the calling grain has durably accepted it, or
  `DurableTaskOptions.ResultRetentionPeriod` after completion when it has no
  grain caller, such as a task scheduled by a client; when the completion is
  dead-lettered, for that period after the dead letter. A task identifier
  therefore deduplicates schedules for `ResultRetentionPeriod` after completion,
  and scheduling it after the deletion executes the request again, as a new run with
  its own incarnation, independently of Messaging's `DeduplicationWindow`. External
  clients poll retained results, and a poll after the deletion throws
  `DurableTaskNotFoundException`. The grain applies retention when it activates,
  after each journal write, and before it answers a poll, a client's schedule, or an
  subscription, so none observes a task whose retention has ended. Polls interleave with the grain's other
  requests and messages, so a waiting client doesn't delay the work which
  completes the task.
- Deleting an execution tree cancels its unfinished tasks: the deletion's journal
  write sends a cancellation to each pending call to another grain, and each
  running execution is signaled after that write and its result discarded. A
  deleted execution's further scheduling throws `OperationCanceledException`.
  Completions and delay resumes which arrive for deleted tasks, or for an earlier run
  of a root ID which runs again, are ignored. A caller's
  cancellation of its child on another grain which arrives before the child's invocation is recorded, so the invocation
  terminalizes as canceled when it arrives, and the record is deleted with its tree. A
  direct cancellation, from a client, a grain method, or `IDurableTaskManager.CancelAsync`, of a task which the grain
  doesn't retain throws `DurableTaskNotFoundException` and stores nothing. The
  grain admits an invocation until Messaging's `DeduplicationWindow` has elapsed since its
  sender first staged it, the delivery's `InboxMessage.SentAt`, and dead-letters a later one with the `durable-rpc/expired` code.
  An early record is kept for twice that window after it arrives, because admission reads the sender's clock and the
  record's lifetime the grain's; Messaging bounds how far a send time can be ahead of the grain's clock by one window.
- A completion whose stored result no longer decodes, for example after a later
  deployment removes the result type, can't be materialized. The outbox
  dead-letters that completion, recording the decoding error as the reason, and the
  runtime retires it like any dead-lettered completion, without decoding the result.
  Activation doesn't decode stored results, so the grain keeps processing other
  work; operations which read that result, such as polling it, surface the decoding
  error to their caller.
- A stored request which no longer decodes, for example after a deployment removes a type
  which its arguments hold, quarantines only its task. Activation classifies tasks by their
  record kind and whether they store a request, without decoding requests, so the grain
  activates and runs its other tasks. Starting the task decodes its request; when that fails,
  the task stays pending with its stored request unchanged and stalls: the runtime logs a
  warning and raises `ExecutionStalled` with `DurableTaskAttemptCancellationReason.Diverged`
  and the decoding error, once per activation, and keeps the grain's
  `StalledExecutionRetryInterval` wake-up, so a later activation whose code decodes the
  request runs the task. Polls report the task as pending, and aborting it ends it.
- A result or failure which doesn't serialize, such as a value or an exception of a type
  which has no codec, can't be stored. The task faults instead with a `SerializerException`
  which names the type and the serialization error, and the fault commits like any result,
  so the task ends and the grain keeps committing its other work.
- Cancellation is cooperative, monotonic, and idempotent. A cancellation request commits before it
  propagates: it cancels the durable token of each running execution in the task's tree, stages a
  cancellation to each pending call to another grain, whose callee applies the same rules to its own
  task, and terminalizes delays and tasks whose bodies haven't started as canceled. A started body
  decides its task's outcome: the task is canceled when the cancellation escapes it, and otherwise
  completes with the body's result or failure. Attached children which aren't shielded and which the
  body starts after the request are canceled without running, and from such a start on, the body
  observes the request through `IsTaskCancellationRequested`, even before the request's write
  completes. The body observes the cancellation at a cancellation point: the start of one of its
  durable operations, such as a child's start, a `RunInline` effect, or a wait for a task or a
  signal. A running body observes it at its current point, which for a body parked at a durable
  wait is that wait, and the execution records the point under `{execution}/$cancellation-observed`
  in the journal write which follows. A replay of a canceled body keeps its durable token
  uncanceled until it reaches the recorded point, and observes the cancellation there, so completed
  children return their recorded results and the body compensates where it first did. A replay
  which finds no recorded point observes the cancellation where it reaches the last operation which
  earlier attempts recorded, and records that point. A replay which ends before its recorded point
  diverges, and stalls until code which reaches the point replays it. The first durable terminal
  result wins a race.
- A body starts compensation with `.Shielded()`. A shielded child, such as a local step, a delay, or a
  call to another grain, records that its ancestors' cancellation doesn't reach it: it is created and
  runs when its parent's cancellation was already requested, a later request skips it and its
  descendants, and recovery replays it with its own cancellation state. A shielded combinator shields
  each input which it starts. A shielded signal wait which runs as part of the body waits with the
  attempt's `AttemptCancellationToken`, so the body's cancellation doesn't end it; a shielded
  subscription, including the record of a detached task, isn't unsubscribed by it. Its own
  cancellation and the cancellation of its attempt, by deactivation, an abort of its tree, or
  deletion of its tree, still reach every shielded operation. A body detaches work which must
  outlive the task itself.
- A cancellation request's reason commits with the request on each task it reaches, travels with the
  cancellation to each pending call to another grain, and is what a replay of a canceled body reads
  from `DurableTask.Current.TaskCancellationReason`.
- An administrative abort, through `IDurableTaskManager.AbortAsync`, `DurableTaskReference<T>.AbortAsync`,
  or `ScheduledTask.AbortAsync`, commits the task and its execution tree as failed with
  `DurableTaskAbortedException` in one journal write, which also stages an abort to each pending call to
  another grain; that callee aborts its task's tree in turn. Each running attempt in the tree is canceled
  with the reason `TaskAborted` and its result discarded, so its later durable operations throw, and
  abort runs no compensation. The tree's delays terminalize with it, so their
  resumes are stale. The stored exception's message is the abort reason, and the task's completion time
  records when it was aborted; polling the task reports it as `Failed` with
  `DurableTaskAbortedException` carrying the reason. Aborting a terminal task changes
  nothing, and an abort which arrives before its invocation is recorded as a cancellation.
- A detached task is a root on its target with no caller. Durable cancellation and abort of its
  starter don't reach it, and deleting the starter's execution tree deletes only the record which
  observed it. Starting the same method under the same root ID again leaves the task unchanged.
  Another method under that ID fails like other task ID reuse: the target dead-letters the invocation,
  and the starter's record fails with the rejection. A detached task which an ordinary grain method posts
  has no record, so its target's dead letter holds the rejected invocation.
- Delays persist their logical due time and generation. Each delay sends a
  `durable-rpc/resume` message to its own grain, with `Envelope.DeliverAt` set to
  the due time. The grain's own inbox accepts the message in the delay's journal
  write, without an outbox round trip, so the delay and its resume commit together
  in one write, and the inbox processes the resume once the Durable Jobs clock
  reaches the due time. Logical time, due times, and task timestamps read that clock,
  the `TimeProvider` keyed `DurableJobTimeProviderNames.DurableJobs`, so they share
  one time domain with Messaging's scheduling. Resumes are fenced by the delay's generation and its run's incarnation, which the resume carries: a resume for a canceled delay
  terminalizes it as canceled, and duplicate and stale resumes are harmless.
  Like every scheduled message, a resume doesn't count toward Messaging's
  `MaxCapacity`: its sender bounds it, and the grain holds one resume for each
  delay it scheduled, until that delay's due time.
  A delay's due time is a commitment: a replay which requests another due time,
  for example after a configured duration changed, keeps the recorded one and
  logs a warning. A delay canceled while its write is in flight terminalizes as
  canceled when that write completes.
- Messages use stable identities derived from the sender, receiver, task, and
  subject, so replayed sends coalesce and redeliveries are deduplicated. A
  message's sender routes completions and detects a message which doesn't match
  the task's recorded caller or callee. It isn't an authorization mechanism:
  like any grain method, a durable task, its signals, and its cancellation are
  available to every client and grain in the cluster, which is Orleans' trust
  boundary.
- A local step's record commits with its first durable write: its result, or a
  child, delay, or message it schedules. A synchronous step therefore costs one
  journal write. A step's identity and inputs follow from its parent's inputs and
  earlier results, so a replay which finds no record runs the step again.
- A body's result is staged in the work item which runs the body's final code, so
  it commits in the same journal write as the journaled state which that code
  changed, whichever operation requests the write. A replay which returns the
  recorded result therefore finds those changes applied once.
- A `DurableTask.RunInline` effect runs on the activation, and its outcome is recorded
  as a terminal entry of the execution's tree, `{taskId}/{name}`, staged in
  the same work item as the journaled state the effect changed. Both commit in the
  next journal write, and a replay returns the recorded outcome without running
  the effect. The record is deleted with the tree, and an activation which ends
  before that write loses both, so the next attempt runs the effect again. A result
  which doesn't serialize is recorded as that failure. Code which continues off the
  activation, for example after `ConfigureAwait(false)`, can't record effects.
  Effects and children share identifiers: `DurableTask.RunInline("x", f)` records under
  `x`, as `WithId("x")` names a child, and an unnamed effect,
  `DurableTask.RunInline(effect)`, takes the execution's next generated child
  identifier, such as `$child-3`. A replay which finds a child where an effect
  runs, or an effect where a child starts, diverges, and the divergence names both
  kinds; so does a named combinator's input beneath an effect's identifier, or an
  effect where a named combinator recorded its inputs.
- A replay which diverges from its execution's record, by requesting another operation
  under a recorded identifier or by finding a recorded outcome which no longer decodes,
  stalls: the runtime cancels the attempt with
  `DurableTaskAttemptCancellationReason.Diverged`, as it does with `UnsupportedVersion`
  for an unsupported version, discards its result, keeps the task pending, and raises
  `ExecutionStalled` with the reason and the divergence. An operation's identity is its
  record's kind, its identifier, and its target: a call's grain and method, a detached
  start's root ID and method, or a subscription's task. Arguments aren't compared, so a
  replay whose call passes other arguments, or arguments which serialize differently,
  reads the call's recorded outcome. Each record stores the kind of
  operation which created it, such as a call, a step, a delay, an effect, or a cancellation
  which arrived before its invocation, so a replay which requests a delay where a step was
  recorded, a step where a delay was recorded, or a delay where a call was recorded stalls
  without observing or changing the other operation's record. The grain wakes every
  `DurableTaskOptions.StalledExecutionRetryInterval`, so a later activation replays the
  execution with the code it runs. A caller whose start conflicts with another method
  under the same root identifier still receives `DurableTaskConflictException`.
- `DurableTask.GetVersion(name, minSupported, maxSupported)` records the version of a
  change which an execution follows as a terminal entry of its tree,
  `{taskId}/$version:{name}`, at the execution's first check of the name, staged like
  an effect's record. An execution records `maxSupported` when it reaches the check
  first, and `0` when its replay reaches the check while operations or accesses to
  journaled state which earlier runs recorded remain unreached, because earlier code ran
  past the check. A step reads an
  enclosing execution's record within the same invoked task. Version records aren't
  operations which a replay reaches, so a deployment which removes a check leaves the
  replay's progress unchanged. An attempt which finds a version outside the supported
  range stalls: the runtime cancels the attempt for `UnsupportedVersion`, as it
  cancels the attempts of a stopping activation, so the check and the attempt's later
  durable operations throw `DurableTaskAttemptCanceledException`, whose inner exception is a
  `DurableTaskVersionException`. The attempt's result is discarded, the task stays
  pending, and the grain keeps a one-minute wake-up so that a later activation replays it.
- A body's accesses to journaled state, such as `IDurableValue<T>` and the durable
  collections, and its sends through the outbox, are recorded in the execution's
  access log, in order, with a hash of each access's arguments. A write's record is
  staged in the same work item as the write, and a read's record with the grain's next
  journal write.
  A replay returns each recorded outcome, a read's value, a write's result, or an
  access's exception, without running the access, and runs live once it has
  consumed the log. An access of other state or with other arguments at a recorded
  position, or a recorded outcome which no longer decodes, stalls the attempt for
  `Diverged`, as an unsupported version does. Values and exceptions are recorded with the Orleans
  serializer in every journal format, and each journal write journals one entry per
  execution whose log grew. A read which returns the same outcome as the attempt's
  latest recorded read of the same state and operation records a reference to that
  read, and consecutive references with the same arguments share one record. Live
  observations, `IDurableTaskCompletionSource<T>.Task`,
  `IDurableLog<T>.ReadAsync`, and `WatchAsync`, run unrecorded. The runtime's own state
  and its durable messages run as the host's work, so a body's log holds only the body's
  accesses. Aborting or deleting the task, or completing its body, closes the access
  log: the attempt's later code, such as cleanup or work which outlives it, returns its
  remaining records and then accesses state directly. Stopping the activation, or a
  stall, discards the attempt, whose later writes throw its cancellation, because a later
  attempt reruns that code. Storing the execution's result removes its access log. The
  runtime fails to bind to an activation whose task storage or journaled state manager
  can't record these accesses.
- A durable task publishes progress to an `IDurableLog<T>` of its grain. It
  appends an entry with `log.Append(item)`, whose entry commits with its access
  record. A step which streams content writes it to a
  draft from `BeginDraft()`, which readers with `DurableWatchMode.Drafts` receive
  live, and commits the draft in its final synchronous code, so the entries commit
  in the same journal write as the step's result. Either way, a replay returns the
  recorded sequence number without appending again.
- A run of a task's body reports `IsReplaying` from its start while earlier runs
  recorded operations in the task's tree: children, including subscriptions to other
  tasks, detach and signal-send markers, and recorded effects, but not signal values or
  invoked descendants, and while it hasn't consumed its access log.
  It stops once the run has reached each of them, or starts an operation which
  no earlier run recorded; reaching an operation again doesn't stop it. It
  serves logging and diagnostics only.
- Recovery precedes new execution. Child identifiers, selection decisions, and
  outbound durable messages remain replay-stable. A replay which schedules another
  operation under a recorded identifier fails with an error which names the
  recorded and the requested operations. Each execution's logical UTC
  time is persisted before its effects commit and reused during replay; it
  advances to the recorded completion time of each child result the execution
  observes, so a delay starts when the work before it completed.
- Each terminal result carries a completion sequence, which the grain stamps when it
  stages the result, in the same journal write: a local child's result, a call's result
  when the caller's inbox accepts it, a delay's outcome when its resume is processed,
  and cancellations and aborts. A result staged later receives a higher sequence. The
  sequence adds one integer to the task's state and no journal entry: activation derives
  the last sequence from the recovered tasks. `WhenAny` and `WhenEach` select the
  completed candidate with the lowest sequence, so a replay selects the same
  candidates without a recorded decision, including when several results commit in
  one write. A candidate which completes without a sequence, such as a child of a
  canceled ancestor whose start was refused or a result stored before sequences were stamped, is selected by a
  decision recorded in the execution's tree, and a recorded decision keeps its winner.

Durability covers state and messages participating in Orleans Journaling and
Messaging commits. Direct external side effects require their own idempotency
guarantees.
