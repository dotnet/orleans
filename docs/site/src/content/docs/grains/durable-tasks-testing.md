---
title: Test workflow changes
description: Capture the recorded histories of durable tasks, and replay them against changed workflow code in tests with Microsoft.Orleans.DurableTasks.Testing, to find changes which pending executions can't replay.
ms.date: 10/17/2026
ms.topic: how-to
---

# Test workflow changes

A deployment which changes a durable task's code replays every pending execution against
the new code. The `Microsoft.Orleans.DurableTasks.Testing` package checks a change before
it's deployed: it captures the recorded histories of executions which earlier code
started, and replays them against the changed code in a test. Each replay reports how
far every execution went, which recorded operations it reached and skipped, where its
recorded history ended, and the versions which it follows.

This page builds on [Replay and determinism](durable-tasks.md#replay-and-determinism) and
[Version durable tasks](durable-tasks-versioning.md).

## Capture histories

A `DurableTaskHistory` holds a grain's committed journal: its tasks, their recorded
operations and outcomes, and the grain's journaled state, in the journal format which
recorded them. Capture one from the journal storage of a test cluster, or of a
deployment, at a point where the workflow waits, and keep it with the tests:

:::code source="../snippets/compiled/Grains/DurableTaskTestingSnippets.cs" id="durable_tasks_testing_capture" language="csharp":::

- The capture reads what the journal has committed. Operations whose results the
  workflow has observed are committed; a `DurableTask.RunInline` effect commits with
  the execution's next durable write.
- A history file is a JSON document which holds the journal's bytes in base64, with the
  grain's ID, the journal format, and the time of the capture.
- Requests, results, and state are stored as Orleans-serialized payloads, so a replay also
  checks that the changed code decodes them.

## Replay histories

A `DurableTaskReplayer` hosts a dedicated in-process silo, which runs the grain classes
of the test process with the services which `StartAsync`'s callback registers.
`ReplayAsync` activates the history's grain, whose recovery replays its pending tasks,
and returns once no execution can run:

:::code source="../snippets/compiled/Grains/DurableTaskTestingSnippets.cs" id="durable_tasks_testing_replay" language="csharp":::

The replay reads the recorded history only:

- An execution which reaches an operation that the history doesn't record, such as a
  child, an effect, a delay, a detached start, or a signal's send, stops before starting
  it.
- The silo holds every outgoing message, so no other grain or service observes the
  replay and no reply arrives, and its Durable Jobs clock stands still, so no scheduled
  message, such as a delay's resume, becomes due.
- The clock stands at the time the replayer started. The replay evaluates recorded due
  times, such as outbox expiries, inbox retry deadlines, and `DeliverAt`, against that
  time.
- Version decisions and completion selections, which the replay derives from recorded
  outcomes, are made as in a live run and reported.
- A body's result is reported without being recorded. Each replay starts from its history,
  and the replayer deactivates the grain afterwards.

Replays run one at a time, so one replayer replays any number of histories, including
several histories of the same grain. A replay which doesn't settle within a minute, for
example because grain code blocks, throws `TimeoutException` with the progress which it
made.

## Read the results

`DurableTaskReplayResult.Executions` lists the replayed executions in the order in which
they started: the grain's pending invoked tasks, and the local steps which they ran. Each
`DurableTaskExecutionReplay` reports its `Outcome`:

| Outcome | Meaning | Consistent replay |
| --- | --- | --- |
| `Completed` | The body completed from recorded outcomes. `Response` holds its result. | Yes, when it reached every recorded operation |
| `ReachedNewOperation` | The body reached an operation which the history doesn't record, the end of its history. `NewOperation` and `NewOperationDescription` name it. | Yes, when it reached every recorded operation |
| `Waiting` | The body waits for recorded operations which the history doesn't complete, which `WaitingOn` lists. | Yes |
| `Stalled` | The runtime stalled the execution. `Stall.Reason` tells why: `Diverged` when the code requests another operation under a recorded identifier, or `UnsupportedVersion` when the code doesn't support a recorded version. `Stall.InnerException` describes it. | No |
| `Faulted` | The body failed from recorded outcomes. `Response` holds the failure. | No |

`Reached` lists the recorded operations which the replay reached, `Skipped` those which
it didn't, and `Versions` the version of each change which the execution checked. An
execution which completed or reached the end of its history while recorded operations
remain skipped no longer runs the operations which the earlier code ran, for example
after an operation was renamed or removed.

`Succeeded` reports whether every execution replays consistently, and `EnsureSucceeded`
throws `DurableTaskReplayException` with a report of each execution otherwise. Assert on
the details of a change, such as the version which an earlier execution follows:

:::code source="../snippets/compiled/Grains/DurableTaskTestingSnippets.cs" id="durable_tasks_testing_assert" language="csharp":::

## Check changes in CI

Keep histories of the workflows' long-lived states, such as an order waiting for
approval, in the test project, and replay all of them in a test:

:::code source="../snippets/compiled/Grains/DurableTaskTestingSnippets.cs" id="durable_tasks_testing_all" language="csharp":::

- A change which fails a replay changes operations which pending executions recorded.
  Branch it on `DurableTask.GetVersion`, and the replay reports version `0` for the
  executions which earlier code started; see
  [Version durable tasks](durable-tasks-versioning.md).
- Raising a change's `minSupported` stalls the replays of histories which follow a
  retired version, as it stalls those executions in a deployment. Keep histories of each
  supported version, and retire a version once no pending execution follows it.
- Capture new histories as workflows change, so the histories cover the versions which
  pending executions follow.
