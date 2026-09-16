# Durable Orleans workflows

This sample demonstrates the experimental durable RPC programming model using package references:

- basic durable grain RPC with stable task identity;
- durable `WhenAll` fan-out/fan-in;
- human approval or rejection using a stable correlation id;
- monotonic, persisted cancellation;
- an order saga which compensates in reverse order (refund payment, then release inventory);
- recovery of an in-progress approval workflow on another service replica.

The Aspire AppHost starts two service replicas, Redis for Orleans membership, and persistent Azurite Blob Storage for Journaling and Durable Jobs. The service exposes health checks and HTTP endpoints without fixed ports.

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
curl -i -X POST "$service/workflows/cancellation/cancel-1"
curl "$service/workflows/cancellation/cancel-1/status"
curl -X DELETE "$service/workflows/cancellation/cancel-1?reason=operator-request"
curl "$service/workflows/cancellation/cancel-1/status"
curl -X POST "$service/workflows/orders/order-1?failShipping=true"
```

Approval and cancellation commands return `202 Accepted`. Their `Location` header names the corresponding GET status resource. A status response always includes the stable durable task ID and one of `pending`, `running`, `succeeded`, `canceled`, or `failed`. Successful responses include the workflow result. Canceled and failed responses include a safe summary.
After submitting a command, clients should poll that status resource instead of repeating
the mutation solely to discover completion.

GET polls the retained task state and returns `404 Not Found` for a missing or expired root task. A retained workflow execution failure remains a `200 OK` status response with `status: "failed"` and a safe error summary.

```json
{
  "taskId": "approval-approval-1",
  "status": "pending",
  "result": null,
  "error": null
}
```

Repeating an in-progress or retained command returns the same status resource and task ID. The subject in an approval PUT must match the subject used to start that correlation id; request registration and durable request fingerprinting reject conflicting reuse before recording a decision. Repeating the same decision is idempotent, while a different decision returns `409 Conflict` and preserves the original workflow.

For manual process failover, start an approval workflow, stop its active `service` replica in the Aspire dashboard, and then submit the decision. The automated test suite terminates the owning silo while the workflow is in progress and waits on the original scheduled task for completion on the other silo.

## Guarantees and operational boundaries

- Durable Messaging retries deliveries and deduplicates messages within its configured retention windows. Durable RPC retains `(target grain, task id)` request fingerprints as journaled tombstones after result expiry. Use a new root ID for a new logical workflow. Deleting a grain's journal removes its retained identity state. Application effects in this sample are independently idempotent by operation id.
- Journaling commits durable task state, inbox/outbox changes, and the sample's durable collections together. Production deployments need shared, durable Journaling and Durable Jobs storage; every replica must use the same stores.
- `DurableTask.Run` replays work using its retained durable execution state. Use durable RPC or an idempotent transactional outbox for external network, file, database, or grain effects.
- The cancellation endpoint commits an idempotent business cancellation signal in journaled state, and the workflow observes that signal across activation recovery. Caller wait cancellation ends that caller's observation. The durable-task `CancelAsync` API requests monotonic task cancellation.
- Successful and failed result payloads remain pollable for `ResultRetentionPeriod`. Durable grain callers acknowledge completion, while external clients observe retained responses by polling. Result expiry preserves the root's retained identity.
- Monitor durable task diagnostics plus Durable Messaging inbox/outbox dead letters. Treat dead letters and exhausted retries as operator-visible failures.

## Publication gate

Repository sample validation builds an isolated local package feed from the current sources. `Microsoft.Orleans.DurableTasks.Abstractions` and `Microsoft.Orleans.DurableMessaging` are alpha foundations included by `dotnet pack Orleans.slnx`. Validation opts into packaging the incubating `Microsoft.Orleans.DurableTasks` RPC adapter using `PackDurableTaskAdapter=true` and selects the exact local package version throughout the Orleans package family.

Standalone restore requires public publication of every referenced package and its dependencies. The alpha foundations still await their first publication, and the RPC adapter requires an explicit publication decision. Until those packages are available, use the repository's `samples/Build-Samples.ps1` package-boundary build.

## Test

```shell
dotnet run --project DurableWorkflows.Tests/DurableWorkflows.Tests.csproj --framework net10.0
```

Tests cover successful workflows, approval and rejection, cancellation recovery, failed saga compensation, replay/idempotency, and cross-silo recovery.
Repository CI runs these tests against the packages produced by the sample build.
