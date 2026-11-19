---
title: Run a durable order workflow
description: Submit an order with a stable business identity, observe its result, recover pending work, and handle compensation with the DurableWorkflows sample.
ms.date: 11/19/2026
ms.topic: tutorial
---

# Run a durable order workflow

Start with an order: reserve inventory, charge payment, and ship. Orleans records the
work so a caller can reconnect to the same operation and another activation can
continue unfinished steps. This guide uses the
[DurableWorkflows sample](https://github.com/dotnet/orleans/tree/main/samples/DurableWorkflows)
for the runnable application, its host configuration, and its failure scenarios.

## Run your first order

Use the .NET SDK selected by the repository's `global.json` and a
running Docker-compatible container runtime. The sample targets `net10.0`. Its AppHost
provides two service replicas, Redis for cluster membership, and Azurite Blob Storage
for journals and durable jobs. Keep Redis and Azurite running throughout the exercise
while stopping individual service replicas. These local resources provide the
environment; running the sample requires no Azure account.

Use a trusted HTTPS development certificate for browser and PowerShell requests.
If needed, run `dotnet dev-certs https --trust` before launching the AppHost.

The Durable packages are pre-release alpha packages. Until they are published, build
from the repository root using the existing local-package pipeline:

```powershell
.\samples\Build-Samples.ps1 -Configuration Release -DisableSharedCompilation
```

The script packs the repository's Orleans sources and builds the samples against that
exact generated version. Keep those build outputs for this run:

```powershell
dotnet run --project .\samples\DurableWorkflows\DurableWorkflows.AppHost\DurableWorkflows.AppHost.csproj `
    --configuration Release --no-build --no-restore
```

This launches the Aspire AppHost using the Release outputs and the locally packed
packages from the preceding build.

Open the `service` endpoint in the Aspire dashboard. In another PowerShell window, set
`$service` to that endpoint and submit an order:

```powershell
$service = 'https://localhost:REPLACE_WITH_SERVICE_PORT'
$orderId = 'order-1001'
Invoke-RestMethod -Method Post "$service/workflows/orders/$orderId`?failShipping=false"
```

The response contains the order ID, `status: 0` (`Completed`), an empty `compensations`
array, and a null `failure`. The default JSON serializer represents the sample's
business-result enum as a number. The HTTP call waits for completion. `WorkflowGrain`
uses the order ID as its grain key, settles inventory and payment concurrently, then
ships. A successful result means all three steps completed.

### Connect the contract, implementation, and host

The order entry point returns a <xref:Orleans.DurableTasks.DurableTask`1>. Request and
result types have Orleans serializers because they cross grain calls and enter the
journal. These compiled declarations mirror the sample's order contracts:

:::code source="journaling/snippets/journaling/DurableOnboardingSnippets.cs" id="durable_onboarding_contract" language="csharp":::

In the sample, `WorkflowGrain` derives from <xref:Orleans.Journaling.DurableGrain> and
implements this method. `InventoryGrain` and `PaymentGrain` keep effects in durable
dictionaries by operation ID. Read their implementation in the sample alongside
`Contracts.cs`, `WorkflowGrain.cs`, and `ActivityGrains.cs`.

The service's `Program.cs` registers the durable runtime as follows:

:::code source="journaling/snippets/journaling/DurableOnboardingSnippets.cs" id="durable_onboarding_hosting" language="csharp":::

`UseAzureBlobDurableJobs` also registers the default Azure Blob journal provider.
The host supplies the `BlobServiceClient` through Aspire's
`AddAzureBlobServiceClient("durable-state")`; all replicas use the same Blob resources
and container. `AddKeyedRedisClient("clustering")` and the AppHost's Orleans reference
supply membership configuration. The AppHost waits for both dependencies and starts
two replicas. Run the provided AppHost to supply this configuration; the registration
fragment above fits inside that host's `UseOrleans` callback.

For production credentials, capacity, and storage options, follow
[Azure Journaling providers](journaling/azure-storage.md) and
[durable task operations](durable-tasks-operations.md) when replacing the emulators.
Keep the sample's shared storage topology when testing recovery.

## Start safely with a business identity

Choose the order ID in your application before the first send and save it wherever
your application tracks the business request. The sample maps it to the grain key
`order-1001` and root task ID `order-order-1001`. The client schedules that named root,
then waits:

:::code source="journaling/snippets/journaling/DurableOnboardingSnippets.cs" id="durable_onboarding_submit" language="csharp":::

<xref:Orleans.DurableTasks.DurableTaskExtensions.ScheduleAsync*> returns after the
target has durably accepted the root invocation. Completion is a later boundary:
`WaitAsync` returns its recorded result. The sample's order HTTP endpoint combines
both waits into one response.

If the HTTP response is lost, send the same order ID again with the same input:

```powershell
Invoke-RestMethod -Method Post "$service/workflows/orders/$orderId`?failShipping=false"
```

While the root is retained, the runtime reaches the same method invocation and
returns its recorded result. The sample retains completed client roots for 24 hours.
An exception or lost connection before the caller receives acceptance leaves the
caller uncertain whether the start committed; retry with the saved identity.
Canceling the HTTP request or the client's wait ends that wait. Durable cancellation
is a separate persisted command.

**Bind input at the application boundary.** Runtime deduplication identifies a task
by grain, root ID, caller, and method. Repeating the same method with different
arguments reaches the retained invocation and its original arguments. For example,
changing `failShipping` on an existing order leaves its first run unchanged. An
application which promises `409 Conflict` for changed order contents must persist
and compare its canonical request under the business identity before accepting it.
Use a new business identity for genuinely new work.

Authorize submission, observation, signals, and cancellation for the business owner,
and scope business identities to the appropriate tenant.

After task-result retention expires, scheduling that root creates a new run. Keep
business deduplication records and external provider keys for the lifetime of the
business obligation. The sample participants retain their effect IDs independently
of root-task retention; choose a fresh order ID for each exercise.

### Vocabulary for this operation

| Term | In this order |
| --- | --- |
| Root operation | The business order, addressed by the application-owned order ID and root task ID. |
| Task | A durable execution with a recorded invocation and outcome, such as the order or a call to inventory. |
| Step | A named child, such as `prepare` or `ship`, whose recorded result is reused on replay. |
| Attempt | One run of unfinished work; retry or recovery can run another attempt of the same logical step. |
| Result | The recorded application decision, such as `Completed` or `Compensated`, returned to callers. |

[Task identity](durable-tasks-identity.md) explains child identities, caller scopes,
and root retention in detail.

## Observe work which outlives the request

A connected caller can await its scheduled handle. A reconnecting caller reconstructs
a reference using the same grain and root ID, without scheduling new work:

:::code source="journaling/snippets/journaling/DurableOnboardingSnippets.cs" id="durable_onboarding_observe" language="csharp":::

A missing or expired root raises
<xref:Orleans.DurableTasks.DurableTaskNotFoundException>. Preserve the business identity
and consult the application's retained state before choosing to start again.

For an HTTP acceptance-and-status example in the same running application, start the
approval workflow:

```powershell
$reviewId = 'review-1001'
Invoke-WebRequest -Method Post "$service/workflows/approval/$reviewId`?subject=order-review"
Invoke-RestMethod "$service/workflows/approval/$reviewId/status"
```

`202 Accepted` follows durable scheduling and supplies a `Location` status URL.
`GET` returns the retained task projection `{taskId, status, result, error}` without
scheduling it. `pending` means it is still waiting, `succeeded` includes the business
result, and `canceled` or `failed` includes a safe summary. Missing or expired roots
return `404`. A declined review is a successful task with `result.approved: false`.

## Recover after losing an owner

Leave `review-1001` at its named `decision` signal wait. In the Aspire dashboard,
stop one `service` replica and submit the review through a running service endpoint:

```powershell
$decision = @{ subject = 'order-review'; approved = $true; reason = 'reviewed' } | ConvertTo-Json
Invoke-RestMethod -Method Put "$service/workflows/approval/$reviewId" `
    -ContentType 'application/json' -Body $decision
Invoke-RestMethod "$service/workflows/approval/$reviewId/status"
```

The completed result contains `siloAddress` and `activationId` of the completing
activation. Stopping a replica only forces recovery when that replica owns the
workflow; the sample's automated owner-loss scenario identifies the owning silo
before terminating it and observes completion through the original handle. Keep the
same review ID throughout this exercise: a second ID would demonstrate a new submission.

After the local-package build above, run that deterministic recovery check without
rebuilding or restoring against published packages:

```powershell
dotnet test --project .\samples\DurableWorkflows\DurableWorkflows.Tests\DurableWorkflows.Tests.csproj `
    --framework net10.0 --configuration Release --no-build --no-restore `
    --filter-method "*InProgressWorkflowRecoversOnAnotherSiloAfterReplicaTermination*" `
    --minimum-expected-tests 1
```

The check schedules one root, kills its identified owning silo while its approval is
pending, and signals the original root. It verifies the same workflow ID, original
request, and review result, with a different silo address and activation ID. Its
passing result therefore establishes recovery rather than a second successful start.
The test uses an in-process two-silo cluster and volatile test journals; the AppHost
exercise uses the sample's Azure Blob provider.

Recovery loads journaled state and the pending invocation, then replays the method.
Recorded child outcomes supply their previous values; unfinished work resumes.
Durable Jobs provides wake-ups for pending executions and due delays. Messaging
transfers calls, replies, signals, and cancellation between grains using durable
inboxes and outboxes. Shared journals and jobs let another activation continue the
same operation. Persisted state and the recovered result establish progress;
process-local log lines alone establish only that an attempt ran.

## Handle business outcomes and cancellation

Use a new order to exercise compensation:

```powershell
Invoke-RestMethod -Method Post "$service/workflows/orders/order-1002?failShipping=true"
```

The result has `status: 1` (`Compensated`), `refund-payment` and
`release-inventory` in `compensations`, and a shipping failure description. The order
settles both preparation steps with `WhenAllCompleted` and compensates those which
succeeded. A compensated business result is a successfully completed durable task.

The sample's cancellation workflow exposes a persisted cancellation command:

```powershell
$holdId = 'hold-1001'
Invoke-RestMethod -Method Post "$service/workflows/cancellation/$holdId"
Invoke-RestMethod "$service/workflows/cancellation/$holdId/status"
Invoke-RestMethod -Method Delete "$service/workflows/cancellation/$holdId`?reason=operator-request"
Invoke-RestMethod "$service/workflows/cancellation/$holdId/status"
```

The command records the first cancellation reason. Once the workflow owns a successful
reservation, it catches cancellation and releases that reservation in a shielded
child. Its retained business result has `canceled: true` and the reason, under task
status `succeeded`. A cancellation received before the body starts can instead produce
task status `canceled` and no reservation. Poll until the terminal outcome to distinguish
request acceptance from completed compensation.

| Boundary or outcome | What the application knows | Next action |
| --- | --- | --- |
| Durable acceptance | The root invocation committed; work can outlive the connection. | Save its identity and observe it. |
| Success | The task recorded its result; for an order, read `Completed` versus `Compensated`. | Return that business result. |
| Business rejection | A recorded decision, such as `approved: false`, can be a successful task. | Explain the decision; choose new work explicitly. |
| Durable cancellation | The reason is persisted; the workflow may still be compensating. | Observe its final result and retained effects. |
| Uncertainty | A response was lost or an external outcome remains unresolved. | Reattach by identity and reconcile with the authority for that effect. |

## Cross an external payment boundary

The sample order's `PaymentGrain` records a payment effect inside Orleans Journaling.
For a real payment API, the provider owns the charge, while Orleans records the
application's decision about it. A remote success followed by a lost response leaves
two distinct facts: the provider may have charged, and the step may still be
unrecorded locally.

Use the same provider idempotency key for every attempt of one logical charge,
including a retry after owner loss. Keep that key stable across any loop of durable
retry steps, whose individual task IDs can differ. The provider's deduplication
retention and lookup contract determine when retry is safe and when reconciliation
is required. Return payment decline as a business outcome and compensate successful
preparation work according to the order's policy.

[External integration recipes](durable-tasks-integrations.md) show compiled provider
adapters, bounded in-step retry, durable backoff, and reconciliation boundaries.
[Idempotency](durable-tasks-idempotency.md) explains the authority and retention
requirements for external effects.

## Diagnose waits, retries, and stalls

| Observation | Check | Response |
| --- | --- | --- |
| Approval remains `pending` | The `decision` signal or its durable deadline. | Submit the decision for the same identity; an intentional signal wait is healthy. |
| A canceled workflow remains pending | The successful reservation and its shielded release. | Observe compensation; keep its participants and storage available. |
| A grain call retries | Membership, target availability, Messaging outbox, and dead letters. | Restore delivery; use the original identity to observe the outcome. |
| A delay does not resume | Durable Jobs and the shared journal provider on every replica. | Restore storage and wake-up processing. |
| Execution is stalled | Replay-divergence diagnostics and the deployed workflow version. | Correct the incompatible code or follow the documented recovery procedure. |
| External charge outcome is unresolved | Provider records under the original idempotency key. | Reconcile before authorizing a new effect. |
| Status is missing after completion | Root-result retention and the application's business record. | Read retained business state before resubmitting. |

Use [operations and troubleshooting](durable-tasks-operations.md) for diagnostic
events, retention, storage configuration, replay stalls, and operator actions.

## How the layers support the order

| Layer | Guarantee used by the operation | Registration |
| --- | --- | --- |
| Journaling | Commits each grain's state and its task, inbox, and outbox changes together. | `AddJournaling()` and a shared journal provider. |
| Durable Jobs | Wakes owners for pending executions and durable delays. | `UseAzureBlobDurableJobs(...)` in this sample. |
| Messaging | Transfers and deduplicates durable work and results between grains. | Included by `AddDurableTasks()`. |
| Durable Tasks | Records recoverable task progress, outcomes, signals, and cancellation. | `AddDurableTasks(...)`. |

### Package reference

The sample pins package versions in its `Directory.Packages.props`. Copy the sample
as a unit and keep its package family aligned. For package-based applications after
the alpha packages are published, use `--prerelease` when adding the Durable packages
and pin the selected versions in your central package file:

```shell
dotnet add package Microsoft.Orleans.Durable --prerelease
dotnet add package Microsoft.Orleans.Durable.AzureStorage --prerelease
```

| Project | References or configuration |
| --- | --- |
| Grain contracts declaring `DurableTask` methods | `Microsoft.Orleans.Durable`, including its invokable mappings and transitive abstractions. |
| Silo implementation | `Microsoft.Orleans.Server`, `Microsoft.Orleans.Durable`, and the selected durable provider. |
| Separate client | `Microsoft.Orleans.Client`, `Microsoft.Orleans.Durable`, and client `AddDurableTasks()`. |
| Sample AppHost | Aspire Orleans, Redis, and Azure Storage hosting packages, with references and startup dependencies. |

A separate Orleans client registers the durable client services after its clustering
configuration:

:::code source="journaling/snippets/journaling/DurableOnboardingSnippets.cs" id="durable_onboarding_client" language="csharp":::

Continue with [signals and compensation](durable-tasks-recipes.md),
[parallel composition](durable-tasks-structured-concurrency.md),
[workflow versioning](durable-tasks-versioning.md), and the
[programming model and guarantees](durable-tasks.md).
