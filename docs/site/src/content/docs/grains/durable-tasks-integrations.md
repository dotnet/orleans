---
title: Integrate durable tasks with other systems
description: Call payment providers, databases, and HTTP services from durable tasks with idempotency keys, serve durable tasks as HTTP operations, receive webhooks as signals, and choose where to retry transient failures.
ms.date: 10/17/2026
ms.topic: how-to
---

# Integrate durable tasks with other systems

These recipes connect durable tasks with systems outside Orleans: a payment provider, a
database, HTTP services with long-running operations, HTTP clients which start durable
tasks, and webhooks which report back.
Each external call runs in a step, which records its result, and sends an idempotency
key derived from the task, because a step whose result didn't commit runs again. The
external clients are interfaces, so the examples compile without a provider's SDK.
[Idempotency in durable tasks](durable-tasks-idempotency.md) explains the guarantees
which these recipes rely on.

## Choose where to retry

A failure is either *transient*, such as a timeout, a rate limit, or an outage, which a
later attempt can overcome, or *permanent*, such as a declined card, a validation error,
or a conflict, which every attempt reproduces. Retry transient failures at the level
which matches how long they last, and handle permanent failures as outcomes:

| Failure | Where it's retried | How |
| --- | --- | --- |
| A grain call or a durable task start can't be delivered or processed | Messaging | Automatically, until `OutboxRetryTimeout` (6 hours by default), then `DurableTaskDeliveryException`. The workflow doesn't retry. |
| A brief external fault, seconds long | Inside the step | A loop or a resilience pipeline, such as Polly's or an `IDurableHttpClient`'s resilience handler, around the call, with the same idempotency key on every attempt. |
| An external outage, minutes to hours long | Across steps | A loop of steps with `DurableTask.Delay` backoff, which survives deactivation, bounded by a deadline in logical time. |
| A rejection, such as a declined payment, a conflict, or a validation error | Nowhere | Return it from the step as a value, or catch its exception, and compensate or report it. |

Messaging treats only explicit rejections as permanent: a start whose task ID already
names another method fails with `DurableTaskConflictException`, and a target which
rejects a request with its own code fails it with `DurableTaskRejectedException`.

## Call a payment API with an idempotency key

**Task:** charge a customer through a provider which deduplicates requests by an
`Idempotency-Key`, such as Stripe.

The provider's client reports declines and transient unavailability as exceptions:

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_payment_provider" language="csharp":::

The step converts the outcomes which the workflow handles into a value, so the step's
recorded result tells a replay which outcome occurred:

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_charge_outcome" language="csharp":::

The key combines the grain's identity, the task's ID, and the operation's name, which
are the same in every replay and every attempt:

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_idempotency_key" language="csharp":::

The workflow charges in a `DurableTask.Run` step, which retries a brief unavailability
twice inside the step:

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_charge_in_step" language="csharp":::

- The step receives `DurableTask.Current.CancellationToken`, which the workflow's task
  cancellation and the cancellation of the step's attempt both cancel. Pass it to the
  client and to the delay between attempts.
- Every attempt sends the same key, so the provider charges once. When the silo fails
  after the provider charged and before the step's result committed, the next run sends
  the same key again, and the provider returns the original charge.
- A declined card returns a value. The step completes, and the workflow continues with the
  decline, in every replay.
- A resilience pipeline, such as a Polly `ResiliencePipeline`, replaces the loop inside
  the delegate in the same way: it retries within the step's run, with the same key and
  the step's cancellation token.

Pitfalls:

- Compute the key outside the step's retry loop, from values which are stable across
  replays. A key from `Guid.NewGuid()` in the body changes in every replay; see
  [ORLEANS0201](../diagnostics/orleans0201.md).
- Keep in-step retries short. The step holds an execution running while it retries, and a
  deactivation cancels the step's attempt, which the next run starts again from the first
  retry.

**Verify:** the grain lists `charge` as `Completed` with the charge's ID, and the
provider's dashboard shows one charge for the key, with any repeated requests marked as
idempotent replays.

## Retry across steps with durable backoff

**Task:** keep trying to charge through a provider outage which lasts for hours, without
holding the grain active.

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_charge_with_backoff" language="csharp":::

Each attempt is a step: the loop names them `charge`, `charge$2`, and so on, and the
delays `charge-backoff`, `charge-backoff$2`. A step which reports unavailability completes
with that outcome, the workflow waits for the provider's `Retry-After` with a durable
delay, and the grain can deactivate while it waits. A replay returns each recorded
attempt and resumes the delay which was pending. The deadline is computed from logical
time, so every replay computes the same one.

Pitfalls:

- Bound the retries with a deadline or an attempt count, and decide what the workflow
  does when it passes, such as compensating or reporting the failure.
- Each attempt and delay adds a record to the task's tree until the tree is deleted. A
  workflow which retries for days at short intervals uses longer intervals or a detached
  successor task.

**Verify:** while the provider is unavailable, the grain lists the attempts as `Completed`
with the unavailable outcome and the latest `charge-backoff` as `Delayed` until its due
time.

## Write to a database

**Task:** record a shipment in a relational database, once, and decide from the
database's current contents.

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_database" language="csharp":::

- **Reads run in steps.** The workflow decides from the row which the `find` step
  recorded, so a replay takes the same branch even when the table changed since.
- **Writes are keyed by an operation ID.** An upsert with a unique constraint on the ID,
  such as `INSERT ... ON CONFLICT (operation_id) DO NOTHING`, applies a repeated write
  once. A store without upserts records the operation ID in the same transaction as the
  change, and checks it before applying the change.
- **One step per transaction.** A transaction which commits several changes runs in one
  step, so its changes and their operation ID commit together, and the step's record
  tells the workflow that they committed.

### Deliver database changes to grains

A service which commits a change to its database and must start a workflow for it writes
a row to an outbox table in the same transaction. A relay reads the table and starts a
durable task for each row, named after the row:

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_outbox_relay" language="csharp":::

The row's ID makes the start idempotent: a relay which crashes before it marks a row as
sent starts the row's task again, which reaches the existing task. The receiving grain's
task state is the inbox. Size `ResultRetentionPeriod` to exceed the longest delay between
a row's first and last relay.

**Verify:** the database holds one row per operation ID, and relaying the same outbox row
twice leaves the grain with one task named `outbox-{id}`.

## Call HTTP APIs from durable tasks

**Task:** submit a label to a service which runs it as a long-running operation, wait
for the label, and cancel the job when the workflow is canceled.

The `Microsoft.Orleans.DurableTasks.Http` package's `IDurableHttpClient` runs HTTP
requests and operations as steps of the calling task. Register a named client in the
silo, configure its `HttpClient` through the builder which `AddDurableHttpClient`
returns, and inject the client as a keyed service:

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_http" language="csharp":::

- **Idempotency keys come from step identities.** Each step's unsafe requests carry an
  `Idempotency-Key` derived from the service, the grain, the step's task ID, and the
  task's run, `DurableTaskContext.Incarnation`, so a step which runs again after a
  failure or a deactivation reaches the job which its first attempt started, and a later
  run of the same task ID sends new keys. The key handler precedes the handlers which the builder adds,
  such as a resilience handler, so their retries carry the same key.
- **Responses are recorded.** A replay reads each step's recorded response without
  sending its request again.
- **Operations follow long-running operation conventions.** A `202 Accepted` response
  locates the operation through `Operation-Location`, `Azure-AsyncOperation`, or
  `Location`. `DurableHttpWaitMode.Events` reads the status monitor's server-sent events
  within one step, and `DurableHttpWaitMode.Poll` polls after the interval which
  `Retry-After` advertises, within the task's poll intervals, with durable delays
  between polls, so a long wait doesn't hold the grain active. `WaitAsync` bounds the
  wait with a deadline in logical time. A failed or canceled operation throws
  `DurableHttpOperationException`.
- **Cancellation reaches the remote operation.** `CancelOperationAsync` sends
  `POST {status monitor}:cancel` with the task's cancellation reason, or the deadline's,
  shielded so it runs after the workflow's cancellation. `SignalOperationAsync` delivers
  a signal the same way.

A step which sends its own requests with `DurableTask.Run` gets the same keys from an
`HttpClient` which `AddDurableIdempotencyKey` configures. For every method and option,
see the [package README](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Http/README.md).

**Verify:** the label service receives one job per step identity, however often the
step's request is retried, and the grain lists the start and wait steps, each
`Completed` with its recorded response.

## Serve durable tasks as HTTP operations

**Task:** let HTTP clients start a return as a long-running operation, retry the
request safely, follow its progress, cancel it, and deliver an inspection result.

`WithDurableOperations` makes each endpoint of a route group whose handler returns a
durable grain call start an operation which performs it:

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_http_operations" language="csharp":::

- **The `Idempotency-Key` names the operation.** Each retry of a request reaches the
  operation which its first attempt started, and a start without a key receives
  `400 Bad Request`. The operation's key combines the group's scope, its route values,
  the caller's authorization scope, and the identifier, so a caller reaches only its
  own operations, and the call runs as a root task named by that key. A key which
  another request used to start other work receives `422 Unprocessable Content`.
- **The status monitor reports progress.** A start answers `200 OK` with the result when
  the operation completes, including within the wait which `Prefer: wait=N` requests,
  and otherwise `202 Accepted` with an `Operation-Location` header, the URL of
  `GET {group}/operations/{id}`. That status monitor reports `Running`, `Succeeded`,
  `Failed`, or `Canceled`, with the result or an error, and also honors `Prefer: wait=N`.
- **Events stream the status.** `GET {group}/operations/{id}/events` streams the status
  monitor as server-sent events until the operation is terminal, so a browser's
  `EventSource` follows it without polling.
- **Clients cancel and signal the task.** `POST {group}/operations/{id}:cancel` requests
  durable cancellation with a reason, and `POST {group}/operations/{id}/signals/{name}`
  delivers a signal which `MapDurableSignal` maps. The operation's host records the task
  which performs its work in its journal, so cancellations and signals reach that task
  whichever activation serves them.
- **Authorization is the application's.** Policies and filters on the group cover its
  starts and the routes which serve its operations.

The operation's host retains a completed operation, and so deduplicates its key, for
`ResultRetentionPeriod`. The [package README](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Http/README.md) describes every response, the
Nexus protocol, and callbacks which deliver an operation's outcome to a webhook URL.

**Verify:** a repeated `POST` with the same `Idempotency-Key` returns the same
`Operation-Location`, the status monitor reports the operation's outcome, and its
events stream ends with the terminal status.

## Receive webhooks and callbacks as signals

**Task:** wait for a payment provider's webhook which confirms a charge.

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_webhook" language="csharp":::

The web endpoint verifies the webhook's signature and calls an ordinary grain method,
which delivers the confirmation as a signal with `TrySignalAsync`. For a task which runs
as an HTTP operation, `MapDurableSignal` serves the same delivery as
`POST {group}/operations/{id}/signals/{name}`. The task records the
first value for the signal, so:

- A webhook which arrives before the workflow waits is recorded, and the wait returns it
  at once.
- A provider which delivers the same event again receives `Duplicate`, which the endpoint
  acknowledges with a success status, so the provider stops retrying.
- A different value for the same signal returns `Conflict`, which the endpoint reports
  and logs.
- `TryWaitAsync` bounds the wait with a durable deadline.

Webhooks which report several events for one task use one signal name per event, derived
with `DurableSignal<T>.For`, such as one per charge ID.

**Verify:** the first delivery returns `Accepted`, a repeated delivery returns
`Duplicate`, and the workflow completes with the confirmation's outcome.

## Handle rejections without retrying

**Task:** start a follow-up survey under a business ID, and handle a start which can't
succeed.

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_rejections" language="csharp":::

- `DurableTaskConflictException` reports that the ID already names another method. Every
  retry under the same ID reaches the same conflict, so the workflow resolves it, for
  example by reading the existing task, or by choosing another ID.
- `DurableTaskRejectedException` reports a target which rejected the request with its own
  code, in `Code`.
- `DurableTaskDeliveryException` reports a start which Messaging couldn't deliver within
  `OutboxRetryTimeout`. The workflow treats it like an outage which outlasted its
  deadline.
- Application outcomes, such as a declined card or an out-of-stock line, are values which
  steps and grain methods return, so the workflow branches on them instead of retrying.

**Verify:** a second start under the same ID with the same method and different
arguments reaches the first task, and a start under that ID with another method throws
`DurableTaskConflictException`, and the target's dead letters record the
`durable-rpc/conflict` code; see [Dead letters](durable-tasks-operations.md#dead-letters).
