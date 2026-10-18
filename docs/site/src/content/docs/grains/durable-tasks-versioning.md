---
title: Version durable tasks
description: Change durable task code while executions are pending, with DurableTask.GetVersion branches which each execution follows from its recorded version, and retire old versions safely.
ms.date: 10/16/2026
ms.topic: how-to
---

# Version durable tasks

A durable task which is pending during a deployment resumes on the new code: the next
activation runs the body again from the start, and each recorded operation returns its
recorded outcome. `DurableTask.GetVersion` lets one body hold the code for every version
of a change, and gives each execution the version which it follows: executions which
started on earlier code keep their operations, and new executions take the new ones.

This page builds on [Replay and determinism](durable-tasks.md#replay-and-determinism) and
[Task identity and references](durable-tasks-identity.md). Changes which only add
operations after existing ones, or which add a new method, need no version; see
[Version workflows safely](durable-tasks-operations.md#version-workflows-safely).

## Branch on a change's version

The workflow below shipped orders through a warehouse. A later deployment books a carrier
instead, and a deployment after that also notifies the customer. Each deployment keeps
the earlier branches, and the check states the versions which the code supports:

:::code source="../snippets/compiled/Grains/DurableTaskVersioningSnippets.cs" id="durable_tasks_versioning_branch" language="csharp":::

The first check of a change in an execution decides the version, and records it beneath
the execution as `$version:{name}`. Every later check of the change in the execution,
and every replay, returns the recorded version:

| Execution | Version |
| --- | --- |
| Reaches the check for the first time: a new execution, or one whose earlier runs stopped before this point | `maxSupported` |
| Ran past this point on code without the check | `0`, the code before the change |
| Recorded a version | The recorded version |
| A step whose enclosing execution on the same grain recorded a version | The enclosing execution's version |

A replay identifies an execution which ran past the check from its history: the replay
reaches the check while operations which the earlier run recorded remain ahead of it.
Place the check before the first operation which the change adds, removes, or alters, so
that every execution reaches it with the operations which its version expects.

- A check inside a loop, or in several helpers, returns the first decision every time.
- The record commits with the execution's next durable write, as a `DurableTask.RunInline`
  effect's does, and a replay which ends before that write decides again from the same
  history.
- Version decisions are scoped to an invoked task's execution tree: a call to another
  grain decides its own versions.
- The check runs on the activation's scheduler, as `DurableTask.RunInline` does. Outside a
  durable task, it returns `maxSupported`.
- Names follow the rules for operation names: they're non-empty and don't contain `$`.
  [ORLEANS0118](../diagnostics/orleans0118.md) reports names and ranges which aren't
  constants.

### Evolve a change

Each deployment of a change raises `maxSupported` and adds a branch:

1. The first deployment checks `GetVersion("carrier-booking", 0, 1)`: version 1 books a
   carrier, and version 0 keeps the warehouse.
2. The next deployment checks `GetVersion("carrier-booking", 0, 2)`: version 2 adds the
   notification, and executions which recorded version 1 keep booking without it.

An unrelated change elsewhere in the body gets its own name. Give each version's new
operations new names, so their records never meet another version's records.

### Declare a change once

A `DurableVersion` holds a change's name and supported range in one declaration, as a
`DurableSignal<T>` holds a signal's name. Every check of the change then states the same
range, and a deployment which adds or retires a version edits the declaration:

:::code source="../snippets/compiled/Grains/DurableTaskVersioningSnippets.cs" id="durable_tasks_versioning_descriptor" language="csharp":::

`Get()` returns the execution's version, and `IsAtLeast(version)` reports whether the
execution follows `version` or a newer one.

## Retire a version

Retire a version once no pending execution follows it:

1. Find the executions which follow it. Each execution holds its decision as the
   `$version:{name}` record, whose response is the version, in
   `IDurableTaskGrainExtension.GetTasksAsync`, and the `VersionRecorded` event reports
   each decision as it's recorded; see [Monitor versions](#monitor-versions).
2. Raise `minSupported` above the version, and remove its branch:

   :::code source="../snippets/compiled/Grains/DurableTaskVersioningSnippets.cs" id="durable_tasks_versioning_retired" language="csharp":::

3. Once `minSupported` equals `maxSupported` and every pending execution follows that
   version, remove the check. The records which executions hold stay in their trees
   until the trees are deleted, and replays skip them.

Give a later change a new name rather than reusing a retired one: an execution which ran
past the retired check before it was added holds no record, and a new check under the
old name would decide version `0` for it.

## Stalled executions

An execution whose recorded version lies outside the range which the running code
supports stalls. The runtime cancels the attempt with the reason
`UnsupportedVersion`, as it cancels the attempts of a stopping activation; see
[Task cancellation and attempt cancellation](durable-tasks-structured-concurrency.md#task-cancellation-and-attempt-cancellation).
The runtime:

- Throws `DurableTaskAttemptCanceledException` from the check, with a
  `DurableTaskVersionException` as its inner exception, which names the change, the
  recorded version, and the supported range. The attempt's later durable operations and
  journaled state changes throw the same cancellation, and `AttemptCancellationToken`
  is canceled, so the attempt unwinds through its `finally` blocks.
- Discards the attempt's result, whatever the body does with the exception.
- Keeps the task pending. Callers which await it keep waiting.
- Logs a warning and raises the `ExecutionStalled` event, which carries the
  `DurableTaskVersionException`.
- Keeps a wake-up for the grain, so a later activation replays the task, for example on
  the silos of the next deployment.

A stall happens when a deployment raises `minSupported` while executions follow the
retired version, or when code which supports fewer versions replays an execution, such
as during a rollback. Deploy code which supports the recorded version, or abort the task;
see [Abort stuck tasks](durable-tasks-operations.md#abort-stuck-tasks).

### Rolling deployments

During a rolling deployment, a grain can activate on a silo which runs the previous
code. That code stalls executions which recorded a version newer than its
`maxSupported`. Code from before a change's first check has no check, so it continues
every execution on the code before the change. Let a deployment which adds a change's
first check complete on every silo before work depends on the new version.

## Monitor versions

The `DurableTaskEvents` listener reports each recorded decision and each stall. A
monitor which counts decisions shows which versions executions still record, and logs
stalls:

:::code source="../snippets/compiled/Grains/DurableTaskVersioningSnippets.cs" id="durable_tasks_versioning_monitor" language="csharp":::

`VersionRecorded` reports the execution, the change's name, and the version: `0` for an
execution which earlier code ran past the check. `ExecutionStalled` reports the stalled
execution and its `DurableTaskVersionException`.

## Choose versions or new methods

- **Version in place** for a change within a body, such as a replaced step, a changed
  argument, or an added notification, while executions are pending.
- **Add a method** for a new workflow shape. Schedule new work with the new method, and
  keep the old method until its pending tasks complete.
- **Start a generation** for a workflow which runs indefinitely: each generation ends and
  starts its successor as a detached task, such as `subscription-{id}-{generation}`, and a
  new generation starts on the code which is current when it starts.
