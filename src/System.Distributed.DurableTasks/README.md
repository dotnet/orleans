# Distributed durable tasks

`System.Distributed.DurableTasks` provides task-like types for expressing
recoverable distributed work. The package is an alpha programming model used by
the Orleans durable task runtime.

## Execution model

A `DurableTask` method returns a task definition. The host schedules that
definition with a `TaskId`, persists the request, and executes it in a
`DurableExecutionContext`. Awaited child tasks receive child identifiers derived
from the parent task identifier.

The host can replay a persisted request after activation or process restart.
Durable code therefore treats every external effect as an idempotent operation
or routes it through a host-provided durable messaging boundary.

## Task identifiers

`TaskId` values are non-empty hierarchical identifiers. Segment separators and
escape characters are validated so every identifier round-trips through
`ToString`, parsing, and serialization.

Scheduling the same equivalent request with the same identifier observes the
existing execution. Reusing an identifier for a different method or different
arguments is rejected.

Configured tasks retain one generated identifier across scheduling, polling,
waiting, and cancellation.

## Cancellation

Cancellation is a durable request. A runtime persists cancellation intent and
retries delivery to remote child tasks until the target observes it
idempotently. A cancellation acknowledgement means that the target recorded the
request; application code still cooperates with cancellation while completing
or compensating external work.

Caller polling timeouts only stop waiting for a result. They do not cancel the
durable execution.

## Request context

The runtime captures a bounded snapshot of the Orleans request context when an
invocation is scheduled. Initial execution uses the inbound context, and
persisted execution restores the snapshot after recovery. Request context is
caller-provided metadata. Applications validate identity, tenant, and
authorization values at a trusted boundary.

## Activation lifecycle

An activation stops admitting new durable work when deactivation begins. It
drains or hands off admitted executions before its state is disposed. A
replacement activation resumes persisted work after recovery completes.

## Status

The API is experimental and can change before a stable release.
