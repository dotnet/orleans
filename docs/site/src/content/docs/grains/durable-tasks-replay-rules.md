---
title: Replay-safety rules for durable tasks
description: Reference for the rules which keep durable task bodies correct under replay, the failures each rule prevents, and the analyzer diagnostics ORLEANS0200 to ORLEANS0223 which enforce them.
ms.date: 10/16/2026
ms.topic: reference
---

# Replay-safety rules for durable tasks

A durable task resumes after a deactivation or a failure by running its body again from
the start, while each recorded operation returns its recorded outcome. The rules on this
page keep that replay equivalent to the run which recorded the operations. The
`Microsoft.Orleans.DurableTasks.Abstractions` package carries analyzers which check them
at build time and report diagnostics ORLEANS0200 to ORLEANS0223; each diagnostic's page
shows the code which triggers it and common fixes.

For the model behind the rules, see
[Replay and determinism](durable-tasks.md#replay-and-determinism) and
[Idempotency in durable tasks](durable-tasks-idempotency.md).

## Replayed code, recorded operations, and the tail

The analyzers divide each durable task body into regions:

- **Recorded operations** run once per execution, and a replay returns their outcomes:
  awaits of durable calls, local steps, `DurableTask.Run` delegates, delays, signal
  waits, combinators, and handles; `DurableTask.RunInline` effects; `Post`; and reads and
  writes of journaled state, such as `IDurableValue<T>`, the durable collections, and
  sends through the Messaging outbox. An await of a `Task`-returning helper in the same
  project which performs durable operations counts as one.
- **Replayed code** is code from which a recorded operation is still reachable. A
  replay runs it again, after later operations committed their results and state
  changes.
- **The synchronous tail** is code from which no recorded operation is reachable. It runs
  only in the run which completes the body, and its state changes commit with the task's
  result.
- **Recorded bodies**, the delegates which `DurableTask.RunInline` and `DurableTask.Run`
  receive and the bodies of local steps, run once per recorded result. A step's body is
  itself a durable task body, with its own regions.

A `DurableTask.RunInline` effect runs immediately, in the current work item, before it
returns; a `DurableTask.Run` step runs when it's awaited, scheduled, or posted, possibly
in a later turn, as a child execution. Consecutive effects in one synchronous block
therefore commit in the same journal write with the state which they change, and no
other turn of the grain runs between them, while each step has its own
`DurableTask.Current`, cancellation scope, and journal write. An effect and a step with
the same name share an identifier, so changing one into the other diverges for
executions which recorded it; branch the change on `DurableTask.GetVersion`.

A replay which diverges from its record, by scheduling another operation under a
recorded ID or by finding a recorded outcome which no longer decodes, doesn't fail into
the body: the runtime stalls the execution with `DurableTaskAttemptCancellationReason.Diverged`
and keeps the task pending until code which matches the record replays it. See
[Diagnose replay divergence](durable-tasks-operations.md#diagnose-replay-divergence) and
[Stalled executions](durable-tasks-versioning.md#stalled-executions).

The following grain shows each region:

:::code source="../snippets/compiled/Grains/DurableTaskSnippets.cs" id="durable_tasks_replay_regions" language="csharp":::

A durable task body is an `async` method, local function, or lambda which returns
`DurableTask` or `DurableTask<TResult>`, or a delegate which `DurableTask.Run` receives.
Ordinary grain methods aren't durable task bodies, and the rules don't apply to them. The
analysis is intraprocedural: it doesn't inspect helpers which a body calls without
awaiting, or helpers in other assemblies.

## Rules

| Rule | Failure which it prevents | Diagnostic |
| --- | --- | --- |
| Read and change persistent state in recorded effects, steps, or the tail. | A replay applies a change again, or decides from state which later operations wrote. | [ORLEANS0200](../diagnostics/orleans0200.md) |
| Produce clock, random, and environment values in recorded effects, steps, or the tail, and read time from `DurableTask.Current.UtcNow`. Produce a value which the tail sends to an external service, such as an idempotency key, in a recorded effect or a step, because a tail whose result didn't commit runs again. | A replay computes other arguments, IDs, keys, or deadlines, and diverges or repeats an external effect. | [ORLEANS0201](../diagnostics/orleans0201.md) |
| Await external operations in steps or the tail. | A replay repeats the operation, observes another result, or fails while the service is unavailable. | [ORLEANS0202](../diagnostics/orleans0202.md) |
| Change persistent state after the last external await of a step or the tail. | The change commits without the result, and the next run applies it again. | [ORLEANS0203](../diagnostics/orleans0203.md) |
| Return the outputs of a `DurableTask.RunInline` effect. | A replay skips the effect, and the body's variables keep their initial values. | [ORLEANS0204](../diagnostics/orleans0204.md) |
| Await, schedule, or post every durable task definition. | The task never runs. | [ORLEANS0205](../diagnostics/orleans0205.md) |
| Run each `async DurableTask` definition once. | The second run throws `InvalidOperationException`. | [ORLEANS0206](../diagnostics/orleans0206.md) |
| Name operations without `$`. | The runtime rejects the name with `ArgumentException`. | [ORLEANS0207](../diagnostics/orleans0207.md) |
| Give each call site its own name. | Removing or reordering a call site shifts the recorded IDs of the others. | [ORLEANS0208](../diagnostics/orleans0208.md) |
| Keep bodies on the activation's scheduler. | Work races with the grain, and `RunInline` and `Post` throw off the scheduler. | [ORLEANS0209](../diagnostics/orleans0209.md) |
| Run concurrent durable work through durable combinators. | Children receive IDs in thread order, which a replay can change, and losing inputs keep running. | [ORLEANS0210](../diagnostics/orleans0210.md) |
| Wait with `DurableTask.Delay` and `DelayUntil`. | A deactivation loses the wait, and a replay waits the whole duration again. | [ORLEANS0211](../diagnostics/orleans0211.md) |
| Filter cancellation handlers by `DurableTask.Current.IsTaskCancellationRequested`. | The cancellation of an attempt, such as a deactivation, runs compensation for a task which nobody canceled. | [ORLEANS0212](../diagnostics/orleans0212.md) |
| Return a task from asynchronous code in bodies. | Work continues after the body completed, and its failures escape. | [ORLEANS0213](../diagnostics/orleans0213.md) |
| Derive a signal name for each repeated wait. | Every wait returns the first value which the signal received. | [ORLEANS0214](../diagnostics/orleans0214.md) |
| Log replayed code through `logger.ReplaySafe()`. | Each replay logs the events of the completed operations again. | [ORLEANS0215](../diagnostics/orleans0215.md) |
| Post from grains and durable task bodies, and schedule from clients. | `Post` throws, because a client has no durable outbox. | [ORLEANS0216](../diagnostics/orleans0216.md) |
| Pass `AttemptCancellationToken` to the calls which task-cancellation cleanup makes. | The cleanup's calls end at once, because `CancellationToken` is already canceled. | [ORLEANS0217](../diagnostics/orleans0217.md) |
| Pass constant names and ranges to version checks. | One change records several decisions, or a deployment records versions which no branch handles. | [ORLEANS0218](../diagnostics/orleans0218.md) |
| Await, combine, or return the handle of each scheduled child. | The child's failure goes unobserved, and deleting the execution's tree cancels it. | [ORLEANS0219](../diagnostics/orleans0219.md) |
| Name operations in loops and conditions, and after other unnamed operations. | A loop, a condition, or a deployment shifts generated identifiers, and a replay diverges. | [ORLEANS0220](../diagnostics/orleans0220.md) |
| Run synchronous work with `DurableTask.RunInline`. | A step adds a child execution and a journal write without running asynchronous work. | [ORLEANS0221](../diagnostics/orleans0221.md) |
| Pass delegates which return a task to `DurableTask.Run` and `DurableTask.RunInline`. | The task records an `async void` delegate as complete at its first `await`, before its work runs. | [ORLEANS0222](../diagnostics/orleans0222.md) |
| Assign new immutable values to grain state. | A change in place bypasses the journal, so the recovered state differs from the state in memory. | [ORLEANS0223](../diagnostics/orleans0223.md) |

The analyzers also suppress CS1998 for `async DurableTask` methods without an `await`,
which are ordinary steps; see [ORLEANS0224](../diagnostics/orleans0224.md).

### State, values, and external work

The first four rules keep each side effect in exactly one run. Journaled state records
each access of a body, and a replay returns the recorded outcomes, so a body reads and
changes it anywhere; see
[Journaled state in durable task bodies](durable-tasks.md#journaled-state-in-durable-task-bodies).
Persistent state, `IPersistentState<TState>`, holds every committed change and changes in
place, so replayed code reads newer state and applies its writes again. A
`DurableTask.RunInline` effect, a step's final code, and the tail each commit their changes
with a record, so no replay repeats them. External calls run in steps, whose results the
replay returns, and they send an idempotency key derived from the task, because a step
whose result didn't commit runs again; see
[Integrate durable tasks with other systems](durable-tasks-integrations.md).

### Definitions and names

A durable task definition is cold and, for `async DurableTask` methods, single-use. Each
recorded operation is identified by its name, or by its position among unnamed
operations, so names stay unique per call site, without `$`, and stable across
deployments. [Task identity and references](durable-tasks-identity.md) lists every
identifier which the runtime generates and which changes affect it.

### Scheduling, time, and concurrency

The activation's scheduler orders a body's operations, so the body stays on it and runs
concurrent durable work through `DurableTask.WhenAll`, `WhenAny`, `Race`, `ScheduleAll`,
and `WhenEach`, which identify inputs by index. Durable delays and timeouts record their
due times in logical time. [Structured concurrency](durable-tasks-structured-concurrency.md)
describes how combinators settle their inputs.

### Cancellation, signals, logging, and starts

Durable cancellation is recorded, and a host stop isn't, so handlers tell them apart.
Signals latch one value per name. Logs repeat in replayed code unless the logger skips
replays. `Post` stages a start in the calling grain's outbox, which clients don't have.

## Rules which the analyzers don't check

These rules depend on data or deployment history which the analyzers can't observe:

- **Schedule from ordered inputs.** Build combinator inputs and loops which schedule
  durable work from arrays, lists, or sorted sequences. A `HashSet<T>` or a dictionary
  after removals can enumerate in another order in another process, which reassigns
  input indexes and occurrence numbers.
- **Decide from replay-stable configuration.** Configuration which a replay reads, such
  as an option which sets a loop's bound, stays the same while executions are pending.
  A recorded delay keeps its recorded due time when its configured duration changes.
- **Don't branch on `IsReplaying`.** `DurableTask.Current.IsReplaying` serves logging and
  diagnostics. A run which ended before its work committed is replayed, so code which
  runs only while `IsReplaying` is `false` can run twice or never.
- **Version by addition or by branch.** Keep existing operations, names, and arguments
  while executions are pending: a replayed call matches its record by method and target,
  not arguments, and returns the outcome recorded for its original arguments. Add new
  operations with new names after existing ones,
  branch changes to operations which pending executions passed on
  `DurableTask.GetVersion`, and add new workflow shapes as new methods. See
  [Version durable tasks](durable-tasks-versioning.md).
- **Access journaled state in the same order.** Each replay makes the accesses which the
  access log recorded, in the recorded order, and changes a value by writing it back
  rather than in place, because a replay returns recorded values as copies. A replay
  which makes another access at a recorded position stalls until code which makes the
  recorded accesses replays it.
- **Bound the history.** Every recorded operation stays in the task's tree until the
  tree is deleted, and the access log keeps a record for each access in re-executed code
  until the execution completes; consecutive reads of unchanged values, such as strings
  and numbers, share one record. A workflow which loops indefinitely ends each generation
  and starts its successor as a detached task, such as `subscription-{id}-{generation}`.

## Configure the analyzers

The rules apply to every project which references the abstractions package. Set a
rule's severity in `.editorconfig`, for example to fail the build on replayed logs:

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0215.severity = error
```

Each diagnostic's page describes when suppressing it is appropriate.
