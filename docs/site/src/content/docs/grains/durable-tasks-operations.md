---
title: Operate durable tasks
description: Configure retention and storage, inspect task state, diagnose replay divergence, version workflows, abort stuck tasks, handle dead letters, and size durable task workloads.
ms.date: 10/13/2026
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
Orleans-serialized payloads. `AddVolatileDurableTaskStorage` replaces journal-backed task
state with in-memory state for tests.

`DurableTaskOptions` configures retention through the `AddDurableTasks` callback, and is
validated at startup.

| Setting | Default | Operational effect |
| --- | --- | --- |
| `ResultRetentionPeriod` | 1 day | Retains the terminal result of a task which has no calling grain, such as a client-scheduled or detached task, for polling, and for this period after a dead-lettered completion. A task ID deduplicates schedules for this period after completion. Must not be negative. |

Messaging settings in `DurableMessagingOptions` govern the transport; see
[Capacity, retries, and retention](messaging-operations.md#capacity-retries-and-retention).

- `OutboxRetryTimeout` bounds how long the outbox retries an invocation, completion,
  cancellation, or signal before dead-lettering it. A cancellation or signal which arrives
  before its invocation is retained for this period, so the invocation terminates as
  canceled, or observes the signal, when it arrives. A signal whose invocation doesn't
  arrive in this period is deleted, and the grain logs a warning.
- `DeduplicationWindow` deduplicates redelivered durable task messages, like other
  messages.
- Invocations, completions, cancellations, and signals are accepted directly into task
  state, so they don't occupy inbox capacity. Delay resumes are scheduled messages which
  the grain sends itself, so they don't count toward `MaxCapacity`, like other scheduled
  messages; the grain holds one resume for each delay it scheduled, until that delay's
  due time.

Configure shared, production-grade Journaling and Durable Jobs providers for
multi-silo deployments; see [Journaling configuration](journaling/configuration.md).

## Retention and deletion

Each invoked task owns an execution tree: the task, its local steps, delays,
combinator records, `DurableTask.Once` records, signal records, subscriptions to other tasks,
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

## List a grain's tasks

Every durable grain serves `IDurableTaskGrainExtension`, which lists the tasks the grain
retains. `GetTasksAsync` returns each task's ID and a `DurableTaskDiagnosticState`, and
`GetRunningTasksAsync` returns the IDs of the executions running in the activation:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_diagnostics" language="csharp":::

| Status | Meaning | `Response` |
| --- | --- | --- |
| `Pending` | The task hasn't recorded a result. | None |
| `Delayed` | A delay is waiting for its due time, reported in `DueTime`. | None |
| `Canceling` | Cancellation was requested and the task hasn't recorded a result. | The cancellation reason |
| `Completed` | The task completed successfully. | The result's `ToString()` |
| `Canceled` | The task was canceled. | The cancellation reason |
| `Faulted` | The task failed or was aborted. | The exception type and message, such as `DurableTaskAbortedException: <reason>` |
| `Unreadable` | The stored result no longer decodes. | The decoding error |

`Request` describes an invoked task's method call, `RemoteTarget` names the grain which
runs a call to another grain, `CreatedAt` and `CompletedAt` record timestamps, and
`Waiters` lists the grains whose completion is pending in the outbox. Diagnostics decode
stored payloads and report decoding failures instead of throwing.

Grain references also expose task control: the `GetDurableTask<TResult>(rootId)`
extension returns a reference which polls, waits for, cancels, or aborts an existing
task, and which a durable task awaits to subscribe to its result, and the
`SignalDurableTaskAsync` and `TrySignalDurableTaskAsync` extensions deliver signals.
Diagnostics describe a subscription child's `Request` as
`a subscription to task '{taskId}' on grain '{grain}'`, and `Waiters` lists a
subscriber other than the invoker as its grain followed by its task ID.

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

Tests subscribe to `DurableTaskEvents.AllEvents`, or use `DiagnosticEventCollector`, to
wait for a transition instead of polling. Messaging reports deliveries, retries, and
retirements through `MessagingEvents`, whose `InboxMessageReceiving` observers inject
delivery faults; see [Observe progress and size the system](messaging-operations.md#observe-progress-and-size-the-system).

## Diagnose replay divergence

A replay must schedule the same operation, with the same arguments, under each recorded
ID. When it schedules a different one, the operation throws an
`InvalidOperationException` such as:

```text
Durable task 'place-order-…/authorize-payment' is already associated with a different request.
It records a call to grain 'payment/…' (fingerprint 3F2A9C01B7E4), and this attempt requested a
call to grain 'payment/…' (fingerprint 91C0D4E2A6B8). A replay schedules the same operation, with the
same arguments, under each identifier; new workflow steps use new identifiers.
```

The message names the recorded operation, such as a call to a grain, a delay, a
`WhenAny` decision, a `DurableTask.Once` effect, a detached task's start, or a signal,
and the requested one. Typical causes:

- Code between children reads a value which differs on replay, such as
  `DateTime.UtcNow`, a random number, or mutable grain state, and passes it to a child.
  `DurableTask.Once` records such a value, so every replay passes the same one.
- A deployment changed a step's arguments, reordered unnamed children, or added an
  unnamed child or combinator before existing ones.
- A loop or condition schedules children under IDs which depend on a nondeterministic
  value.

The failure propagates through the workflow like other exceptions. Once the code is
fixed, abort the affected task and schedule it again under a new root ID, or let the
caller observe the failure.

### Version workflows safely

When a deployment changes a workflow while executions are pending, those executions
replay under the new code:

- Keep existing steps, `Once` effects, and detached starts, with their IDs and arguments. A
  changed `Once` delegate doesn't affect executions which recorded it: they return the
  recorded result. Turning a recorded step into an effect under the same name, or the
  reverse, fails as a divergence.
- Give new steps new IDs, and place steps which depend on new behavior after the
  existing ones.
- Name every child and combinator which a later change might move, so generated IDs
  don't shift. Detached tasks and signal sends derive their record IDs from the detached
  task or the signal, so they don't shift. An unnamed detached task derives its root ID
  from the execution's unnamed operations, so name it with `WithId` when a later change
  might move it.
- Keep argument, result, and signal types decodable by the Orleans serializer, adding
  members with new `[Id(n)]` values.
- A changed delay duration applies to new delays. A replayed delay keeps its recorded
  due time, and the runtime logs a warning.

To retire an old workflow shape, add the new shape as a new method and schedule new work
with it, and keep the old method until its pending tasks complete.

## Abort stuck tasks

Abort a task whose body ignores cancellation or whose compensation can't complete:

- From the owning grain, `IDurableTaskManager.AbortAsync(rootId, reason)`.
- From a client or tool, `grain.GetDurableTask<TResult>(rootId).AbortAsync(reason)`.

Abort commits the task and its execution tree as failed with
`DurableTaskAbortedException` in one journal write, sends an abort to each pending call
to another grain, whose callee aborts its own tree, and cancels the running attempts,
whose results are discarded. A canceled attempt records nothing: its durable operations
and journaled state changes throw `DurableTaskAttemptCanceledException` with the reason
`TaskAborted`, so abort runs no compensation; undo any partial effects separately. Code
which runs as the attempt unwinds still releases its in-process resources in `finally`
blocks. Aborting a terminal task changes nothing, and
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

A dead-lettered completion leaves the calling execution waiting. Decide whether to
cancel or abort that execution, and replay a dead letter only after correcting its
cause.

A start which its target rejects, or which isn't delivered, fails the starting task's
record, so `ScheduleAsync`, and awaiting the task or its handle, throw the failure in
every replay:

| Outcome | Rejection code | Failure |
| --- | --- | --- |
| The task ID already identifies another request or caller. The target rejects it at once. | `durable-rpc/conflict` | `DurableTaskConflictException` |
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

- A recorded effect, `DurableTask.Once`, adds no journal write: its record and the state
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
- Executions run on the activation's scheduler and interleave with the grain's methods
  at each `await`. A hot grain bounds the throughput of its workflows, so partition
  workflows by business owner, such as one grain per order.
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
- Activation shutdown stops new execution, drains running executions, discards their
  results, and leaves committed work for replay in the next activation.
- The adapter and abstractions await their first NuGet publication, and their persisted
  formats can change while the integration is evaluated.
