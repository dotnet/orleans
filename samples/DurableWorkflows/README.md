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

Approval and cancellation commands return `202 Accepted`. Their `Location` header names the corresponding GET status resource. A status response always includes the stable durable task ID and one of `pending`, `running`, `succeeded`, `canceled`, or `failed`. Successful responses include the workflow result. Canceled and failed responses include only a safe summary, not internal exception details.

```json
{
  "taskId": "approval-approval-1",
  "status": "pending",
  "result": null,
  "error": null
}
```

Repeating the same command returns the same status resource and task ID. The subject in an approval PUT must match the subject used to start that correlation id; request registration and durable request fingerprinting reject conflicting reuse before recording a decision. Repeating the same decision is idempotent, while a different decision returns `409 Conflict` without changing the original workflow.

For manual process failover, start an approval workflow, stop its active `service` replica in the Aspire dashboard, and then submit the decision. The automated test suite deterministically terminates the owning silo while the workflow is in progress and verifies completion on the other silo.

## Guarantees and operational boundaries

- Durable Messaging is at-least-once. Stable `(target grain, task id)` identity and retained request/completion records deduplicate retries only within their configured retention windows. Application effects in this sample are independently idempotent by operation id.
- Journaling commits durable task state, inbox/outbox changes, and the sample's durable collections together. Production deployments need shared, durable Journaling and Durable Jobs storage; every replica must use the same stores.
- `DurableTask.Run` does not make arbitrary network, file, database, or non-durable grain effects replay-safe. Use durable RPC or an idempotent transactional outbox for external effects.
- The cancellation endpoint records an idempotent business cancellation signal in journaled state before the workflow observes it, so recovery cannot lose the request. Separately, canceling a caller's wait is different from the durable-task `CancelAsync` API, which requests monotonic task cancellation.
- Successful and failed results remain pollable for `ResultRetentionPeriod`. Durable grain callers acknowledge completion; external clients poll and cannot provide a durable completion ACK.
- Monitor durable task diagnostics plus Durable Messaging inbox/outbox dead letters. Treat dead letters and exhausted retries as operator-visible failures.

## Publication gate

This sample is stacked on the experimental packages introduced by the dependent pull requests. `Microsoft.Orleans.DurableTasks` currently depends on the incubating, non-published `System.Distributed.DurableTasks` package, so a copied folder cannot restore from NuGet until that package is published. The sample intentionally does not bypass the package boundary with source project references. In-tree sample validation uses the repository's local package feed once every dependency is packable; keep this PR draft and dependent until then.

## Test

```shell
dotnet test DurableWorkflows.Tests
```

Tests cover successful workflows, approval and rejection, cancellation recovery, failed saga compensation, replay/idempotency, and cross-silo recovery.
