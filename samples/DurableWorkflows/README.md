# Durable Orleans workflows

This sample runs workflows as Orleans durable tasks: grain methods which return `DurableTask<T>`, which the
`Microsoft.Orleans.DurableTasks` runtime records in each grain's journal and resumes after deactivation or silo failure.
It demonstrates:

- durable grain calls with stable root and child task IDs;
- fan-out/fan-in with a named `DurableTask.WhenAll`, whose branches are `branches/0`, `branches/1`, and so on;
- human approval with a `DurableSignal<T>` and a deadline from `TryWaitAsync`;
- cooperative cancellation with a persisted reason and inline compensation in a shielded child (`.Shielded()`);
- an order saga which settles concurrent steps with `WhenAllCompleted` and compensates exactly the steps which succeeded;
- idempotent effects keyed by operation ID;
- recovery of an in-progress approval workflow on another service replica;
- observation of retained results by root ID.

The Aspire AppHost starts two service replicas, Redis for Orleans membership, and persistent Azurite Blob Storage for
Journaling and Durable Jobs. The service exposes health checks and HTTP endpoints without fixed ports.

## Run

```shell
aspire run --project DurableWorkflows.AppHost
```

Open the service endpoint shown in the Aspire dashboard. Example requests, where `$service` is that endpoint:

```shell
curl -X POST "$service/workflows/basic/basic-1?input=hello"
curl -X POST "$service/workflows/fan-out/fan-1" -H "Content-Type: application/json" -d '["one","two","three"]'
curl -i -X POST "$service/workflows/approval/approval-1?subject=production"
curl "$service/workflows/approval/approval-1/status"
curl -X PUT "$service/workflows/approval/approval-1" -H "Content-Type: application/json" -d '{"subject":"production","approved":true,"reason":"reviewed"}'
curl "$service/workflows/approval/approval-1/status"
curl -i -X POST "$service/workflows/cancellation/hold-1"
curl "$service/workflows/cancellation/hold-1/status"
curl -X DELETE "$service/workflows/cancellation/hold-1?reason=operator-request"
curl "$service/workflows/cancellation/hold-1/status"
curl -X POST "$service/workflows/orders/order-1?failShipping=true"
```

## Workflows

`WorkflowGrain` hosts every workflow. The service schedules each one under a root ID derived from the workflow ID,
such as `approval-approval-1`, and the workflow awaits each child with `WithId`, so its children have IDs such as
`approval-approval-1/decision`. Scheduling an existing root ID with the same request reattaches to that task, and a
different request under that ID fails with a conflict.

- **Basic and fan-out.** The workflow calls `ActivityGrain` once per step. The fan-out workflow returns each branch's
  result in item order, including the ID of the task which recorded it, such as `fanout-fan-1/branches/2`.
- **Approval.** The workflow waits for the `approval-decided` signal until three days after it started, measured on its
  durable clock. `PUT` starts the workflow when it isn't running and signals the decision to its root task with
  `TrySignalDurableTaskAsync`. The task records the first decision: repeating it succeeds, and a different decision, a
  decision after the deadline, or a subject which differs from the running workflow's returns `409 Conflict`.
- **Cancellation.** The workflow reserves stock and holds it for one day. `DELETE` starts the workflow when it isn't
  running and requests durable cancellation with a reason. The runtime records the first request and its reason with
  the task, so a replay observes them. The workflow catches the cancellation, releases the reservation in a
  shielded child, `.Shielded()`, and completes with `canceled: true` and the reason.
- **Order saga.** The workflow reserves inventory and charges payment concurrently with `WhenAllCompleted`, then ships.
  When shipping fails, it refunds and releases the steps which succeeded with a named `WhenAll`. Inventory, payment, and
  activity grains record each effect by operation ID and return the recorded effect for a repeated operation, so a replay
  or a repeated saga applies each effect once.

Approval and cancellation commands return `202 Accepted` with a `Location` header which names the GET status resource.
A status response includes the root task ID and one of `pending`, `succeeded`, `canceled`, or `failed`. Successful
responses include the workflow result; canceled and failed responses include a safe summary. Clients poll the status
resource after a command instead of repeating the command.

```json
{
  "taskId": "approval-approval-1",
  "status": "pending",
  "result": null,
  "error": null
}
```

GET polls the retained task without scheduling it and returns `404 Not Found` for a workflow which was never started or
whose retained result has expired. A workflow which failed remains a `200 OK` status response with `status: "failed"`.

For manual failover, start an approval workflow, stop its active `service` replica in the Aspire dashboard, and then
submit the decision. The test suite terminates the owning silo while the workflow waits and observes its completion on
the other silo through the original task handle.

## Guarantees and operational boundaries

- Journaling commits each durable task's state, its Orleans Messaging inbox and outbox changes, and the grain's durable
  collections together. Every replica uses the same Journaling and Durable Jobs storage.
- The runtime replays an unfinished workflow from the start, and completed children return their recorded results.
  Code between children computes the same values on every replay; it reads time from
  `DurableTask.Current.UtcNow`.
- A task which a client schedules, like every workflow in this sample, is retained for
  `DurableTaskOptions.ResultRetentionPeriod` after it completes (24 hours here). Within that period, scheduling its root
  ID returns the recorded result; afterwards, polling it returns `404`, and scheduling the same root ID runs the
  workflow again. A task which another grain invokes is deleted with its execution tree once that grain accepts the
  result.
- The application effects in this sample are idempotent by operation ID, independently of task retention. Effects
  outside Orleans Journaling and Messaging need their own idempotency.
- Monitor durable task diagnostics and Messaging dead letters, and treat dead letters and exhausted retries as
  operator-visible failures.

## Package availability

`Microsoft.Orleans.DurableTasks` is an incubating adapter which isn't published to NuGet, and the Journaling,
Messaging, and Durable Jobs packages are alpha packages. `samples/Build-Samples.ps1` packs the current sources into a
local feed, opting into the adapter's package with `-p:PackDurableTaskAdapter=true`, and builds this sample against
that exact version. Build the sample through that script until the packages are published.

## Test

```shell
dotnet run --project DurableWorkflows.Tests/DurableWorkflows.Tests.csproj
```

The test project is a Microsoft Testing Platform application, so `dotnet run` runs its tests. The tests run in-process
Orleans clusters with volatile journal storage, so they need no external services. They cover
each workflow, signal latching and deadlines, cancellation recovery and compensation, saga compensation and
idempotency, cross-silo recovery, the HTTP status resources, and retained-result expiry.
