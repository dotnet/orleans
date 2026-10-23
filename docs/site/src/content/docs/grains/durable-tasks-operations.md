---
title: Operate durable tasks
description: Configure retention and storage, inspect task state, diagnose replay divergence, version workflows, abort stuck tasks, handle dead letters, and size durable task workloads.
ms.date: 10/16/2026
ms.topic: how-to
---

# Operate durable tasks

Use this guide to configure and operate the durable task runtime described in
[Durable tasks](durable-tasks.md). Durable tasks run over Messaging, so the
[Messaging operations guide](messaging-operations.md) also applies to the messages
which carry invocations, results, cancellations, signals, and delay resumes.

## Configure storage and retention

`AddDurableTasks` on the silo builder adds Messaging, which adds Durable Jobs, and
stores task state in each grain's journal as the journaled state
`orleans.durable-tasks.runtime`. Give application states other names. Task state uses
the grain's journal format, JSON or Orleans binary; `AddDurableTasks` registers the JSON
metadata for task state, and requests, results, and signal values are stored as
Orleans-serialized payloads. The grain's journal is the source of truth for task state;
tests keep it in memory with volatile journal storage, such as `AddVolatileJournalStorage`.
Activation setup fails for a durable grain without a journaled state manager.

`DurableTaskOptions` configures retention and stalled executions through the
`AddDurableTasks` callback, and is validated at startup.

| Setting | Default | Operational effect |
| --- | --- | --- |
| `ResultRetentionPeriod` | 1 day | Retains the terminal result of a task which has no calling grain, such as a client-scheduled or detached task, for polling, and for this period after a dead-lettered completion. A task ID deduplicates schedules for this period after completion. Must not be negative. |
| `StalledExecutionRetryInterval` | 1 minute | How often a grain which holds a [stalled execution](durable-tasks-versioning.md#stalled-executions) wakes, so that once its activation ends, a later activation replays the execution on the code it runs. Must be positive. |

Messaging settings in `DurableMessagingOptions` govern the transport; see
[Capacity, retries, and retention](messaging-operations.md#capacity-retries-and-retention).

- `OutboxRetryTimeout` bounds how long the outbox retries an invocation, completion,
  cancellation, or signal after a failed delivery before dead-lettering it.
- `DeduplicationWindow` deduplicates redelivered durable task messages, like other
  messages. It is also the invocation admission window: a grain admits an invocation until
  this period has elapsed since its sender first staged it, and dead-letters a later one,
  which backpressure or a delivery pipeline held, with the `durable-rpc/expired` code. A
  cancellation or signal which arrives before its invocation is retained for twice this
  period, which covers the skew between the sender's clock and the grain's, so an admitted
  invocation terminates as canceled, or observes the signal. A signal whose invocation
  doesn't arrive in that time is deleted, and the grain logs a warning.
- Invocations, completions, cancellations, and signals are accepted directly into task
  state, so they don't occupy inbox capacity. Delay resumes are scheduled messages which
  the grain sends itself, so they don't count toward `MaxCapacity`, like other scheduled
  messages; the grain holds one resume for each delay it scheduled, until that delay's
  due time.

Configure shared, production-grade Journaling and Durable Jobs providers for
multi-silo deployments; see [Journaling configuration](journaling/configuration.md).

## Retention and deletion

Each invoked task owns an execution tree: the task, its local steps, delays,
combinator records, `DurableTask.RunInline` records, version decisions, signal records, subscriptions to other tasks,
records of detached tasks, signal-send markers, and its records of calls to other grains. The grain
deletes the tree in one journal write:

| Invoked by | Deleted |
| --- | --- |
| A durable task or an ordinary method of another durable grain | When the outbox retires the completion after the calling grain has durably accepted the result. |
| A client, an ordinary grain method, or a durable task which detached it | `ResultRetentionPeriod` after completion. |
| Any caller, after its completion was dead-lettered | `ResultRetentionPeriod` after the dead letter. |

A task to which durable tasks subscribe, through `GetDurableTask` or by starting it as a
detached task, is also retained until the outbox retires the completion to each
subscriber. A dead-lettered subscriber
completion restarts the retention period, like a caller's.

A grain's record of a call which an ordinary method scheduled is a root of its own,
retained for `ResultRetentionPeriod` after the result arrives.

Deleting a tree cancels its unfinished tasks: the deletion's journal write sends a
cancellation to each pending call to another grain, running executions are signaled
after that write, and their results are discarded. Completions and delay resumes which
arrive for deleted tasks are ignored. After deletion, polls throw
`DurableTaskNotFoundException`, and scheduling the same task ID runs the request again.

The grain applies retention when it activates, after each journal write, and before it
answers a poll or a client's schedule, so neither observes a task whose retention has
ended. An idle grain keeps the expired state in its journal until one of these occurs.

Size `ResultRetentionPeriod` to cover the longest interval in which clients poll results
or retry schedules, and account for the retained task state in each grain's journal:
the number of retained client-scheduled tasks is their completion rate multiplied by the
period.

## Observe a task

Grain references expose task control: the `GetDurableTask<TResult>(rootId)` extension
returns a `DurableTaskReference<TResult>` which polls, waits for, cancels, or aborts an
existing task, and which a durable task awaits to subscribe to its result, and the
`SignalDurableTaskAsync` and `TrySignalDurableTaskAsync` extensions deliver signals.
`GetStatusAsync` reports the task's `DurableTaskStatus`, and `GetResponseAsync` returns its
response, which holds the result or the exception. A poll of a task which the grain doesn't
retain throws `DurableTaskNotFoundException`:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_diagnostics" language="csharp":::

| Status | Meaning |
| --- | --- |
| `Pending` | The task hasn't recorded a result, including while a cancellation request or a delay is pending. |
| `CompletedSuccessfully` | The task completed successfully. |
| `Canceled` | The task was canceled. |
| `Failed` | The task failed or was aborted; an abort's exception is `DurableTaskAbortedException`, whose message is the abort reason. |

A grain observes, cancels, aborts, and signals its own tasks without a grain call through
`IDurableTaskManager`, which it resolves from its activation services.

## Observe runtime transitions

The `Orleans.DurableTasks` diagnostic listener, `DurableTaskEvents`, reports each
transition synchronously on the activation whose runtime makes it, when the runtime
stages it, with the activation's `IGrainContext` and the task's ID. The change commits
with the grain's next journal write.

| Event | Raised |
| --- | --- |
| `TaskCreated` | The runtime staged a task's state: an invoked task, or an operation of an execution. |
| `TaskCompleted` | The runtime staged a task's terminal result, which the event carries. |
| `TaskDeleted` | The runtime staged a task's deletion, with its execution tree or when its retention ended. |
| `ExecutionStarted` | An attempt of a task's body started on the activation. |
| `ExecutionStopped` | The attempt ended: its body completed, or an abort or deletion canceled the attempt. |
| `ReplayEnded` | A replaying attempt started an operation which no earlier attempt recorded, so `IsReplaying` reports `false`. |
| `SignalWaitStarted`, `SignalWaitEnded` | An execution started or ended a wait for a signal, under the signal's record. |
| `SelectionWaitStarted` | A selection, such as `DurableTask.WhenAny`'s, found no completed candidate and waits, under its decision. |
| `VersionRecorded` | An execution recorded the version of a change which it follows, which the event carries with the change's name. |
| `ExecutionStalled` | An attempt stalled because the running code can't replay its execution; the task stays pending. `Reason` is `UnsupportedVersion` or `Diverged`, and `Exception` describes the stall. See [Stalled executions](durable-tasks-versioning.md#stalled-executions). |

Tests subscribe to `DurableTaskEvents.AllEvents`, or use `DiagnosticEventCollector`, to
wait for a transition instead of polling. Messaging reports deliveries, retries, and
retirements through `MessagingEvents`, whose `InboxMessageReceiving` observers inject
delivery faults; see [Observe progress and size the system](messaging-operations.md#observe-progress-and-size-the-system).

## Diagnose replay divergence

A replay must schedule the same operation under each recorded ID, and must decode each
recorded outcome. An operation is the same when it has the same kind and target: a call
invokes the same interface method on the same grain, whatever its arguments, so a call
whose arguments changed or serialize differently returns its recorded outcome. When a
replay schedules a different operation, or a recorded outcome no longer decodes, the
execution stalls: the runtime cancels the
attempt with `DurableTaskAttemptCancellationReason.Diverged`, discards its result, and
keeps the task pending. The runtime logs a warning and raises `ExecutionStalled`, whose
`Exception` describes the divergence, such as:

```text
Durable task 'place-order-…/authorize-payment' is already associated with a different request.
It records a call to method '…Invokable_IPaymentGrain_…_Authorize…' on grain 'payment/…'
(fingerprint 3F2A9C01B7E4), and this attempt requested a call to method
'…Invokable_IPaymentGrain_…_Capture…' on grain 'payment/…' (fingerprint 91C0D4E2A6B8). A replay
schedules the same operation under each identifier: a call or request invokes the same method on
the same target, whatever its arguments; new workflow steps use new identifiers.
```

The message names the recorded operation, such as a call to a grain, a delay, a
`WhenAny` decision, a `DurableTask.RunInline` effect, a detached task's start, or a signal,
and the requested one. A call names its method by its generated invokable type, and its
fingerprint is a hash of its serialized arguments, which tells calls apart in diagnostics
but doesn't decide whether they match. Typical causes:

- A deployment replaced a step's call with a call to another method or grain, reordered
  unnamed children, or added an unnamed child or combinator before existing ones. Branch
  such changes on `DurableTask.GetVersion`, so pending executions keep their recorded
  operations.
- A loop or condition schedules children under IDs which depend on a nondeterministic
  value.
- A deployment changed a canceled task's body so that its replay ends before the
  cancellation point where the earlier run observed the cancellation. The message names
  the point which the replay reached and the recorded one.

Code between children which reads a value which differs on replay, such as
`DateTime.UtcNow`, a random number, or mutable grain state, and passes it to a child
doesn't diverge: the replay reads the recorded outcome of the call which the first run
made with the first value. `DurableTask.RunInline` records such a value, so every run
passes the same one to the code which follows.

The body observes the divergence as the attempt's `DurableTaskAttemptCanceledException`,
whose inner exception is the divergence, and the attempt's later durable operations
throw it too. Callers which await the task keep waiting. The grain keeps a wake-up every
`StalledExecutionRetryInterval`, so once its activation ends, a later activation replays
the task: deploy code which matches the record, such as code which branches the change on
`DurableTask.GetVersion`, and the execution resumes. To end the task instead, abort it;
see [Abort stuck tasks](#abort-stuck-tasks). Watch `ExecutionStalled` to find stalled
executions.

### Version workflows safely

When a deployment changes a workflow while executions are pending, those executions
replay under the new code:

- Keep existing steps, `RunInline` effects, and detached starts, with their IDs, methods,
  and targets. A call or detached start whose arguments a deployment changes returns, in
  executions which recorded it, the outcome recorded for its original arguments. A
  changed `RunInline` delegate doesn't affect executions which recorded it: they return the
  recorded result. An effect and a step with the same name share an ID, so turning a
  recorded step into an effect, or the reverse, diverges; branch that change on
  `DurableTask.GetVersion`.
- Give new steps new IDs, and place steps which depend on new behavior after the
  existing ones.
- Name every child and combinator which a later change might move, so generated IDs
  don't shift. Detached tasks and signal sends derive their record IDs from the detached
  task or the signal, so they don't shift. An unnamed detached task derives its root ID
  from the execution's unnamed operations, so name it with `WithId` when a later change
  might move it.
- Keep argument, result, and signal types decodable by the Orleans serializer, adding
  members with new `[Id(n)]` values. A replay which can't decode a recorded outcome
  diverges.
- A changed delay duration applies to new delays. A replayed delay keeps its recorded
  due time, and the runtime logs a warning.

To change operations which pending executions already passed, such as replacing a step
or changing its arguments, branch on `DurableTask.GetVersion`: each execution follows
the version which it recorded, and executions which started on earlier code follow
version `0`. See [Version durable tasks](durable-tasks-versioning.md).

To retire an old workflow shape, add the new shape as a new method and schedule new work
with it, and keep the old method until its pending tasks complete.

## Abort stuck tasks

Abort a task whose body ignores cancellation or whose compensation can't complete:

- From the owning grain, `IDurableTaskManager.AbortAsync(rootId, reason)`.
- From a client or tool, `grain.GetDurableTask<TResult>(rootId).AbortAsync(reason)`.

Abort commits the task and its execution tree as failed with
`DurableTaskAbortedException` in one journal write, sends an abort to each pending call
to another grain, whose callee aborts its own tree, and cancels the running attempts,
whose results are discarded. A canceled attempt's durable operations throw
`DurableTaskAttemptCanceledException` with the reason `TaskAborted`, and
`IsTaskCancellationRequested` stays `false`, so abort runs no compensation; undo any
partial effects separately. Code which runs as the attempt unwinds releases its
in-process resources in `finally` blocks, and its journaled state changes commit with
the grain's next journal write. Aborting a terminal task changes nothing, and
detached tasks are aborted separately. Diagnostics then report the task as `Faulted`
with `DurableTaskAbortedException: <reason>`.

## Dead letters

Durable task messages use subjects beginning with `durable-rpc/`: `invoke`,
`complete`, `cancel`, `resume`, and `signal`. Messaging's retry and dead-letter policy
applies to them; inspect and remove dead letters through `IMessagingDiagnostics`, as
described in [Inspect and replay dead letters](messaging-operations.md#inspect-and-replay-dead-letters).

- The receiving inbox dead-letters a message which it rejects permanently, with the
  rejection as its reason: an invocation whose task ID already identifies another request
  or caller, and a rejected signal, such as one with a conflicting value or for a
  completed task. A conflicting invocation's dead letter also carries the
  `durable-rpc/conflict` code from `DurableTaskRejectionCodes`, which the sender's outbox
  reports when it retires the invocation as rejected.
- A message which the receiving grain fails to accept for another reason, such as a
  transient failure or a payload which it can't decode, is retried. During a rolling
  deployment, a retry can reach an activation which decodes it.
- The outbox dead-letters a message whose delivery fails for `OutboxRetryTimeout`.
- The outbox dead-letters a completion whose stored result no longer decodes, for
  example after a deployment removed the result type, with the decoding error as its
  reason. The runtime retires that completion and retains the task for
  `ResultRetentionPeriod`; operations which read the result, such as polls, report the
  decoding error. The grain keeps processing its other work.
- A task whose stored request no longer decodes, for example after a deployment removed a
  type which its arguments hold, stalls without failing the grain's activation: the grain
  runs its other tasks, polls report the task as `Pending`, and `ExecutionStalled` reports
  it with the reason `Diverged` and the decoding error. Its stored request is kept, so a
  later activation whose code decodes the request runs the task; abort the task to end it
  instead.
- A task whose result or exception doesn't serialize, such as a value or an exception
  whose data holds a value of a type without a codec, fails with a `SerializerException`
  which names the type. The failure commits like any outcome, and the grain keeps
  committing its other work.

A dead-lettered completion leaves the calling execution waiting. Decide whether to
cancel or abort that execution, and replay a dead letter only after correcting its
cause.

A start which its target rejects, or which isn't delivered, fails the starting task's
record, so `ScheduleAsync`, and awaiting the task or its handle, throw the failure in
every replay:

| Outcome | Rejection code | Failure |
| --- | --- | --- |
| The task ID already identifies another request or caller. The target rejects it at once. | `durable-rpc/conflict` | `DurableTaskConflictException` |
| The invocation arrives after the target's admission window, `DeduplicationWindow` since the starter first staged it. | `durable-rpc/expired` | `DurableTaskRejectedException` |
| The target rejects the invocation with another code, or with none. | The target's code | `DurableTaskRejectedException` |
| The target doesn't accept the invocation before `OutboxRetryTimeout`, because of transient failures or a request which it can't decode, or the outbox can't reach it. | | `DurableTaskDeliveryException`, with the dead letter's reason |

A caller, such as an HTTP endpoint, tells a conflict, which it resolves by choosing
another task ID, from a start which wasn't accepted, by the exception's type. `DurableTaskRejectedException.Code` reports the target's code, and
`Target` the rejecting grain.

### Client waits across outages

A client, or an ordinary grain method, waits for a task by polling its grain. A poll
which can't reach the grain, because a connection drops, the grain's silo stops or
crashes, the call times out, or the grain deactivates, which fails the poll with
`DurableTaskAttemptCanceledException` and the reason `ActivationStopping`, leaves the task running, so the
wait retries it with exponential backoff, from 50 ms to 5 s, and the next poll reaches
the grain's next activation. The wait ends with the task's outcome, with
`DurableTaskNotFoundException` for a task which the grain doesn't retain, or when the
caller's token or the wait's timeout ends it.

## Capacity and throughput

- A recorded effect, `DurableTask.RunInline`, adds no journal write: its record and the state
  it changed commit with the task's next durable write. Each effect retains one record,
  holding its result, until the task's tree is deleted.
- A local step costs one journal write: its record commits with its first durable
  write, so a step which changes state and returns without awaiting commits its state
  change and result together.
- Combinators and `ScheduleAll` schedule their inputs concurrently, and the runtime
  coalesces the invocations into one journal write.
- Each request and result payload is journaled once per grain, and metadata updates
  don't rewrite it. A dead-lettered completion's result is also held by its dead letter
  while task state retains it for `ResultRetentionPeriod`. The completion sequence adds
  one integer to each task's state.
- Each segment of an execution, the code between two durable waits, runs as a request
  on the activation and follows the grain's reentrancy rules. On a non-reentrant grain,
  a segment which awaits other work, such as a call to another grain, holds the grain
  as a grain method does, and concurrent steps of one grain run one segment at a time.
  A hot grain bounds the throughput of its workflows, so partition workflows by business
  owner, such as one grain per order.
- Polls interleave with the grain's other work, so a waiting client doesn't delay the
  work which completes the task.
- Deleting an execution tree costs time proportional to its size, and retained trees
  grow the grain's journal snapshots until they're deleted.

Monitor the Messaging metrics for inbox and outbox depth, retries, and dead letters,
which include durable task messages; see
[Observe progress and size the system](messaging-operations.md#observe-progress-and-size-the-system).

## Known constraints

- Durability covers state and messages which participate in Journaling and Messaging
  commits. External side effects need their own idempotency, such as a request key
  derived from the task ID.
- A durable task calls its own grain's durable methods directly, as local steps. A
  durable call through a reference to its own grain is rejected.
- A durable task sends signals to other grains with `SignalDurableTaskAsync`, which
  records the send for replay; `TrySignalDurableTaskAsync` is for clients and ordinary
  grain methods.
- Signals address invoked tasks: tasks which a client, another grain, or a durable task
  which detached them started.
- `ScheduleAll`, `WhenAllCompleted`, and `WhenEach` run only when awaited inside a
  durable execution; scheduling one as a task of its own throws
  `NotSupportedException`.
- Activation shutdown stops new execution, discards parked attempts, lets running
  segments finish, discards their results, and leaves committed work for replay in the
  next activation.
- The adapter and abstractions await their first NuGet publication, and their persisted
  formats can change while the integration is evaluated.
