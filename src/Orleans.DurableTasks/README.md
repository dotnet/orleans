# Orleans durable tasks

`Microsoft.Orleans.DurableTasks` hosts distributed durable tasks on Orleans
grains and uses Orleans durable messaging for remote calls, completion, and
cancellation.

## Guarantees

- A request is persisted before execution starts.
- Equivalent scheduling with the same `TaskId` observes one execution.
- Conflicting reuse of a `TaskId` is rejected.
- Remote child scheduling, completion, and cancellation use persisted,
  idempotent message intents.
- Initial execution uses the inbound request context. A bounded scheduling-time
  snapshot is restored when persisted execution resumes after recovery.
- Deactivation stops admission and drains or hands off activation-owned work
  before recovered execution starts on a replacement activation.
- Polling timeout and caller cancellation stop observation without canceling the
  durable task.

Durable application code assigns operation identifiers to external effects so a
caller retry or uncertain network result can be reconciled safely.

## Status

The package is an alpha feature and can change before a stable release.
