---
title: Durable task identity and references
description: Reference for how durable task IDs are constructed, which IDs callers, handles, and diagnostics expose, how they stay stable across replays and deployments, and how to store and pass references to tasks.
ms.date: 10/15/2026
ms.topic: reference
---

# Durable task identity and references

Every durable task is identified by the grain which hosts it and a hierarchical
<xref:Orleans.DurableTasks.TaskId>. Identity is how a replay finds each recorded
operation, how a retried start reaches the same task, how a callee distinguishes its
callers, and how other parties observe, cancel, and signal a task. This page lists every
kind of ID which the runtime constructs, where each one is visible, which changes
preserve it, and how to address a task from elsewhere.

## The shape of a task ID

A task ID is a path of segments separated by `/`: a root segment, followed by one segment
per level of nesting. `DurableTask.Current.Id` reports the ID of the running execution,
and `TaskId.Child(segment)`, `Parent()`, and `IsAncestorOf(other)` navigate the path.

- `TaskId.ToString()` returns the escaped path, which `TaskId.Parse` reads back. A `/` or
  `\` inside a segment is escaped with `\`, so a segment which contains a grain ID, such
  as `report/q3`, appears as `report\/q3`.
- `TaskId.CreateRoot(segment)` and the `string rootId` overloads, such as
  `GetDurableTask<TResult>(rootId)` and `IDurableTaskManager.CancelAsync(rootId, reason)`,
  take one unescaped root segment.
- Segments which the runtime generates begin with, or contain, `$`. Names which
  `WithId` and `DurableTask.RunInline` receive inside a durable task don't contain `$`, so a
  name never equals a generated segment; see [ORLEANS0107](../diagnostics/orleans0107.md).

The following workflow creates most kinds of ID. Its comments name the ID which each
operation records:

:::code source="../snippets/compiled/Grains/DurableTaskIdentitySnippets.cs" id="durable_tasks_identity_tree" language="csharp":::

## Kinds of IDs

### Root IDs

A root task has no parent on its grain. The party which starts it chooses its root ID:

| Started by | Root ID | Example |
| --- | --- | --- |
| A client or an ordinary grain method, with `WithId(name)` | `name`, verbatim | `publish-q3` |
| A client or an ordinary grain method, without `WithId` | A new GUID, formatted with `"N"` | `3f2a9c01b7e44d6b9a0c5e8f1d2b3a47` |
| A durable task, with `.Detached().WithId(name)` | `name`, verbatim | `archive-q3` |
| A durable task, with `.Detached()` and no name | `{grainId}/{taskId}:{segment}`: the starting grain, the starting execution's ID, and the generated segment of the execution's next unnamed operation | `report/q3/publish-q3:$child-2`, which `ToString()` formats as `report\/q3\/publish-q3:$child-2` |

The root ID `$caller` is reserved for the caller scopes described below. Outside a
durable task, `.Detached()` changes nothing, because every task which a client starts is
already a root. A derived detached root ID includes the starting grain, so workflows
with the same task ID on different grains start different tasks; starters which must
share one task name it with `WithId`.

### Operations of an execution

Each operation which a body runs is recorded beneath the execution's ID:

| Operation | Segment | Example |
| --- | --- | --- |
| A child, delay, combinator, or subscription named with `WithId(name)`, or a recorded effect, `DurableTask.RunInline(name, effect)` | `name`; later occurrences of the same name in one run, `name$2`, `name$3`, and so on | `publish-q3/check$2` |
| An unnamed child, delay, `DurableTask.Run` step, subscription, or `DurableTask.RunInline(effect)` | `$child-{n}`, numbered in the order in which the run creates them | `publish-q3/$child-1` |
| An unnamed combinator or timeout | `${kind}-{n}`, where `{kind}` is `when-all`, `when-any`, `race`, `schedule-all`, `when-all-completed`, `when-each`, or `timeout` | `publish-q3/$race-1` |
| A combinator's input | `{combinator}/{index}`, in input order | `publish-q3/render/0` |
| A timeout's work and delay, `WaitAsync` and `WaitUntilAsync` | `{timeout}/0` and `{timeout}/1` | `quote/0`, `quote/1` |
| A selection which a durable completion order can't decide | `{combinator}/$winner` for `WhenAny`, `Race`, and timeouts, or `{combinator}/$next-{n}` for `WhenEach` | `first-quote/$winner` |
| A version decision, `DurableTask.GetVersion(name, ...)` | `$version:name`, once per execution | `publish-q3/$version:carrier-booking` |
| A detached start | `$detach:{targetGrainId}:{rootId}` | `publish-q3/$detach:archive\/archive:archive-q3` |
| A signal send from a durable task | `$send:{targetGrainId}:{taskId}:{name}`; repeated sends, `$2`, `$3` | `place-order-42/$send:order\/42:place-order-42:shipped` |
| A signal's recorded value, beneath the invoked task | `$signal:{name}`; `DurableSignal<T>.For(suffix)` names `{name}/{suffix}` | `publish-q3/$signal:approved`, `publish-q3/$signal:approved\/round-2` |

- Named children, named combinators, and named effects share one identifier per
  occurrence of a name: `DurableTask.RunInline("x", f)` and `await DurableTask.Run(g).WithId("x")`
  both record under `x`. A replay which finds the other kind recorded under an
  identifier fails as a divergence which names both kinds, instead of running the work
  again.
- Unnamed children, unnamed effects, unnamed subscriptions, and the segment of an
  unnamed detached root share the `$child-{n}` count. Unnamed combinators and timeouts
  share the `${kind}-{n}` count.
- Inside the parts of `$detach:` and `$send:` segments, `:`, `\`, and `$` are escaped
  with `\`.
- A selection is recorded only when completion order can't decide it, such as when an
  input completed without a completion sequence. Its ID appears in the
  `SelectionWaitStarted` diagnostic event whenever a selection waits.
- Each local step has its own execution, so its operations nest beneath its ID, such as
  `publish-q3/check/$child-1`.

### Calls to other grains

A call to another grain is recorded in the caller's tree under the call's segment, such
as `publish-q3/render/0`. The callee stores the task under the caller's scope:

```text
$caller/{callerGrainId}/{callerTaskId}
$caller/report\/q3/publish-q3/render/0
```

The scope keeps callers apart: two grains whose workflows have the same task ID call one
callee under different IDs, so neither reattaches to the other's task. The mapping is a
pure function of the caller and its child ID, so replays and redeliveries of the call
reach the same task. In the callee, `DurableTask.Current.Id` reports the scoped ID. A
detached start sends a root ID instead, which the callee stores verbatim.

### Signals and subscriptions

A signal addresses an *invoked task*: a root task, or a task which another grain called.
Its value is recorded beneath that task as `$signal:{name}`, where waits anywhere in the
task's tree find it. An unnamed wait runs as part of the waiting execution and records no
operation; a wait which `WithId` names runs as a child under that name. Shielding a wait
leaves its identity unchanged.

Awaiting a `DurableTaskReference<TResult>` inside a durable task, scheduling it, or
passing it to a combinator records a subscription child under the ID which that operation
assigns, such as `$child-3` or a `WithId` name. The child completes with the referenced
task's outcome.

### Where each ID is visible

| Source | Reports |
| --- | --- |
| `DurableTask.Current.Id` | The running execution: a root, a step, or a callee's scoped task. |
| `ScheduledTask.Id` | The operation in the execution which started or observes the task: a child ID, a `$detach:` record, or a subscription child. Outside a durable task, the root ID. |
| `ScheduledTask.TargetTaskId` | The task on the grain which stores it, which other parties address: a detached task's root ID, a callee's `$caller/...` ID, or the task which a subscription observes. Outside a durable task, the root ID. |
| `IDurableTaskReference.Id` | The referenced task, with `Target`, its grain. |
| `DurableTaskAddress.Id` | A task which other parties address, with `Target`, its grain. |
| `IDurableTaskGrainExtension.GetTasksAsync` | Every task which the grain retains, with its execution tree's records. |
| `DurableTaskEvents` | The ID of each task, operation, signal wait, and selection which a transition concerns. |

A client observes the IDs of its own start through the handle:

:::code source="../snippets/compiled/Grains/DurableTaskIdentitySnippets.cs" id="durable_tasks_identity_client" language="csharp":::

### Internal names

The runtime also uses names which aren't IDs and aren't part of the programming model:
the journaled state `orleans.durable-tasks.runtime` and the tables inside it, such as
`$tasks`, `$task-requests`, and `$task-results`, and request fingerprints which begin with
`$`, such as `$effect`, `$version`, `$subscribe:`, `$signal-send:`, and `$completion-decision:`.
Diagnostics show some of them; their format can change between releases.

## Stability across replays and deployments

A replay finds each recorded operation under the ID which the new run computes, so an ID
is stable when every run computes it from the same inputs:

| ID | Stable when |
| --- | --- |
| A root ID which `WithId` names | Always: callers derive it. |
| A named operation, `name` or `name$n` | The name is unchanged, and earlier occurrences of the same name are unchanged. |
| `$child-{n}` and `${kind}-{n}` | Every run creates the unnamed operations before it in the same order. |
| A combinator's input, `{combinator}/{index}` | The combinator's ID and its input order are unchanged. |
| `$detach:`, `$send:`, and `$signal:` records | Always: they derive from the target, the task, and the name. |
| `$version:` records | Always: they derive from the change's name, and every check of the name in an execution shares one record. |
| A derived detached root ID | The starting execution's ID and its `$child-{n}` count before the start are unchanged. |
| A caller-scoped ID | The caller's child ID is unchanged. |

Changes between deployments affect pending executions as follows:

- **Renaming a `WithId` name** gives the operation a new ID. A replay finds no record
  under it, so it runs the operation again, and the record under the old name stays in
  the tree until the tree is deleted.
- **Adding an unnamed operation** before existing unnamed ones shifts their generated
  IDs. A replay then finds another operation's record under a shifted ID: a call to
  another grain with another request fails as a divergence, and a local step returns the
  other step's result.
- **Adding a named operation** with a new name leaves every other ID unchanged.
- **Adding or removing an occurrence of a repeated name** before existing ones renumbers
  the later occurrences; see [ORLEANS0108](../diagnostics/orleans0108.md).

Name every operation which a later change might move, give new operations new names, and
follow [Version workflows safely](durable-tasks-operations.md#version-workflows-safely).
To change operations which pending executions already passed, branch on
`DurableTask.GetVersion`; see [Version durable tasks](durable-tasks-versioning.md).

## Address a task from elsewhere

A task is addressed by its grain and its task ID. Every party which knows both can
observe, cancel, abort, or signal the task, while the grain retains it.

### Durable references belong to root tasks

Root tasks, which clients, ordinary grain methods, and detached starts create, have their
own lifetimes: their grain retains them for `ResultRetentionPeriod` after completion, and
until each subscriber's completion is delivered. They make durable references.

An attached child, including a callee's `$caller/...` task, belongs to its owner's tree:
the callee deletes it once the calling grain accepts its result, and the owner's grain
deletes the owner's tree once the root is terminal. Use its handle within the execution
which started it, and start work which other parties track as a root.

### Derive references from business identity

When a task's grain and root ID derive from business values, every party computes the
reference instead of storing it. The archive started above is addressed from the
report's ID alone:

:::code source="../snippets/compiled/Grains/DurableTaskIdentitySnippets.cs" id="durable_tasks_identity_lookup" language="csharp":::

Derived references need no storage, and a start under the same name from a retried
request reaches the same task. Choose names which identify the business operation, such
as `archive-{reportId}`, and keep them stable across deployments.

### Store and send addresses

A <xref:Orleans.DurableTasks.DurableTaskAddress> is the value which names a task elsewhere:
its `Target` grain and its task `Id`. Orleans serializes it in grain calls and journaled
state, and System.Text.Json writes it as `{"Target":"report/q3","Id":"publish-q3"}` in
JSON journals and HTTP payloads. A `TaskId` on its own serializes as its escaped path.

| To | Use |
| --- | --- |
| Read a handle's address | `handle.TryGetAddress(out var address)`, which returns `true` for a root task, a detached task, or a task which `GetDurableTask` observes, and whose `Id` is the handle's `TargetTaskId`. An attached child's handle has no address. |
| Read a reference's address | `DurableTaskReference<TResult>.Address`, or `Target` and `Id` through `IDurableTaskReference`. |
| Resolve an address | `GrainFactory.GetDurableTask<TResult>(address)` in a grain, or `client.GetDurableTask<TResult>(address)` in a client. |
| Signal the task | `GrainFactory.GetGrain(address.Target)`, then `SignalDurableTaskAsync` or `TrySignalDurableTaskAsync` with `address.Id`. |

The reference which an address resolves to behaves as any other: a durable task awaits
it to subscribe to the task's result, and other code awaits, polls, cancels, or aborts the
task through it. A grain which hands off a detached task stores its address, and reaches
the task again after any number of reactivations:

:::code source="../snippets/compiled/Grains/DurableTaskIdentitySnippets.cs" id="durable_tasks_identity_store" language="csharp":::

The workflow stores the address in its synchronous tail, so the address commits with the
task's result. `AwaitArchiveAsync` reads the stored address in a recorded effect, so a
replay subscribes to the same task.

A client returns an address to an HTTP caller, and a later request carries it back to
signal, await, or cancel the task:

:::code source="../snippets/compiled/Grains/DurableTaskIdentitySnippets.cs" id="durable_tasks_identity_send" language="csharp":::

An address grants control of the task to whoever holds it: anyone who can call the grain
can cancel, abort, or signal the task. Authorize requests which carry an address before
acting on it, as for any grain call which an external request triggers.

### Retention and not-found results

A reference resolves while the grain retains the task:

- A task without a calling grain, such as a client-started or detached root, is retained
  for `ResultRetentionPeriod` after completion, and until the completion to each
  subscribing durable task is delivered.
- A task which another grain called is deleted once the calling grain has durably
  accepted its result.

After deletion, polls and subscriptions throw `DurableTaskNotFoundException`, signals
return `TaskNotFound`, and a start under the same ID runs the request again. A
`DurableTaskNotFoundException` also reports a task which never started, or whose start
hasn't reached its grain yet; `ScheduleAsync` returns once the grain holds the task, so
an ID which it returned is found until retention ends. See
[Retention and deletion](durable-tasks-operations.md#retention-and-deletion).
