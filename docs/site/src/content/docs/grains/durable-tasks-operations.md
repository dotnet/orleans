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

- `MaxOutboxRetryAge` bounds how long the outbox retries an invocation, completion,
  cancellation, or signal before dead-lettering it. A cancellation or signal which arrives
  before its invocation is retained for this period, so the invocation terminates as
  canceled, or observes the signal, when it arrives. A signal whose invocation doesn't
  arrive in this period is deleted, and the grain logs a warning.
- `DeduplicationWindow` deduplicates redelivered durable task messages, like other
  messages.
- Invocations, completions, cancellations, and signals are accepted directly into task
  state, so they don't occupy inbox capacity. Delay resumes are scheduled messages which
  the grain sends itself, so they're always admitted.

Configure shared, production-grade Journaling and Durable Jobs providers for
multi-silo deployments; see [Journaling configuration](journaling/configuration.md).

## Retention and deletion

Each invoked task owns an execution tree: the task, its local steps, delays,
combinator records, signal records, detach markers, and its records of calls to other
grains. The grain deletes the tree in one journal write:

| Invoked by | Deleted |
| --- | --- |
| A durable task or an ordinary method of another durable grain | When the outbox retires the completion after the calling grain has durably accepted the result. |
| A client, or `DetachAsync` | `ResultRetentionPeriod` after completion. |
| Any caller, after its completion was dead-lettered | `ResultRetentionPeriod` after the dead letter. |

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
extension returns a handle which polls, waits for, cancels, or aborts an existing root
task, and the `SignalDurableTaskAsync` and `TrySignalDurableTaskAsync` extensions deliver
signals.

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
`WhenAny` decision, a detached task's start, or a signal, and the requested one.
Typical causes:

- Code between children reads a value which differs on replay, such as
  `DateTime.UtcNow`, a random number, or mutable grain state, and passes it to a child.
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

- Keep existing steps, their IDs, and their arguments.
- Give new steps new IDs, and place steps which depend on new behavior after the
  existing ones.
- Name every child and combinator which a later change might move, so generated IDs
  don't shift.
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
to another grain, whose callee aborts its own tree, and signals the running attempts,
whose results are discarded. Abort runs no cancellation callbacks or compensation, so
undo any partial effects separately. Aborting a terminal task changes nothing, and
detached tasks are aborted separately. Diagnostics then report the task as `Faulted`
with `DurableTaskAbortedException: <reason>`.

## Dead letters

Durable task messages use subjects beginning with `durable-rpc/`: `invoke`,
`complete`, `cancel`, `resume`, and `signal`. Messaging's retry and dead-letter policy
applies to them; inspect and remove dead letters through `IMessagingDiagnostics`, as
described in [Inspect and replay dead letters](messaging-operations.md#inspect-and-replay-dead-letters).

- The receiving inbox dead-letters a message which fails validation, such as an
  invocation which reuses a task ID with a different request, and a rejected signal,
  such as one with a conflicting value or for a completed task, with the rejection as
  its reason.
- The outbox dead-letters a message whose delivery fails for `MaxOutboxRetryAge`.
- The outbox dead-letters a completion whose stored result no longer decodes, for
  example after a deployment removed the result type, with the decoding error as its
  reason. The runtime retires that completion and retains the task for
  `ResultRetentionPeriod`; operations which read the result, such as polls, report the
  decoding error. The grain keeps processing its other work.

A dead-lettered completion leaves the calling execution waiting. Decide whether to
cancel or abort that execution, and replay a dead letter only after correcting its
cause.

## Capacity and throughput

- A local step costs one journal write: its record commits with its first durable
  write, so a step which changes state and returns without awaiting commits its state
  change and result together.
- Combinators and `ScheduleAll` schedule their inputs concurrently, and the runtime
  coalesces the invocations into one journal write.
- Each request and result payload is journaled once per grain, and metadata updates
  don't rewrite it. The completion sequence adds one integer to each task's state.
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
- Signals address invoked tasks: tasks which a client, another grain, or `DetachAsync`
  started.
- `ScheduleAll`, `WhenAllCompleted`, and `WhenEach` run only when awaited inside a
  durable execution; scheduling one as a task of its own throws
  `NotSupportedException`.
- Activation shutdown stops new execution, drains running executions, discards their
  results, and leaves committed work for replay in the next activation.
- The adapter and abstractions await their first NuGet publication, and their persisted
  formats can change while the integration is evaluated.
