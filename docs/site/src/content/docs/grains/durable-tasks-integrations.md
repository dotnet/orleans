---
title: Integrate durable tasks with other systems
description: Call payment providers, databases, and HTTP services from durable tasks with idempotency keys, receive webhooks as signals, and choose where to retry transient failures.
ms.date: 10/15/2026
ms.topic: how-to
---

# Integrate durable tasks with other systems

These recipes call systems outside Orleans from durable tasks: a payment provider, a
database, an HTTP service with long-running operations, and webhooks which report back.
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
| A brief external fault, seconds long | Inside the step | A loop or a resilience pipeline, such as Polly's, around the call, with the same idempotency key on every attempt. |
| An external outage, minutes to hours long | Across steps | A loop of steps with `DurableTask.Delay` backoff, which survives deactivation, bounded by a deadline in logical time. |
| A rejection, such as a declined payment, a conflict, or a validation error | Nowhere | Return it from the step as a value, or catch its exception, and compensate or report it. |

Messaging treats only explicit rejections as permanent: a start whose task ID already
names another request fails with `DurableTaskConflictException`, and a target which
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

- The step receives a cancellation token which durable cancellation of the workflow
  signals. Pass it to the client and to the delay between attempts.
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
  [ORLEANS0101](../diagnostics/orleans0101.md).
- Keep in-step retries short. The step holds an execution running while it retries, and a
  deactivation ends the attempt, which the next run starts again from the first attempt.

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

## Call an HTTP service and wait for a long-running operation

**Task:** submit a label to a service which accepts the job with `202 Accepted` and a
status URI, then poll until the label is ready.

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_http" language="csharp":::

- The `submit` step sends an `Idempotency-Key` header, bounds each request with a
  timeout which it links to the step's cancellation token, and records the job's status
  URI.
- Each poll is a step, and the wait between polls is a durable delay, which honors the
  service's `Retry-After` header. A poll which waits for minutes doesn't hold the grain
  active, and a replay doesn't poll again for recorded results.
- The deadline is computed from `DurableTask.Current.UtcNow`, so every replay computes the
  same deadline.

Configure `HttpClient` through `IHttpClientFactory`, with its `BaseAddress` and any
resilience handlers, and inject it into the grain. Resilience handlers retry within one
step's run, like the loop in the payment recipe.

**Verify:** the grain lists `submit`, then alternating `poll` and `poll-wait` operations,
and the last `poll` completes with the label's URL.

## Receive webhooks and callbacks as signals

**Task:** wait for a payment provider's webhook which confirms a charge.

:::code source="../snippets/compiled/Grains/DurableTaskIntegrationSnippets.cs" id="durable_tasks_webhook" language="csharp":::

The web endpoint verifies the webhook's signature and calls an ordinary grain method,
which delivers the confirmation as a signal with `TrySignalAsync`. The task records the
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

- `DurableTaskConflictException` reports that the ID already names another request. Every
  retry under the same ID reaches the same conflict, so the workflow resolves it, for
  example by reading the existing task, or by choosing another ID.
- `DurableTaskRejectedException` reports a target which rejected the request with its own
  code, in `Code`.
- `DurableTaskDeliveryException` reports a start which Messaging couldn't deliver within
  `OutboxRetryTimeout`. The workflow treats it like an outage which outlasted its
  deadline.
- Application outcomes, such as a declined card or an out-of-stock line, are values which
  steps and grain methods return, so the workflow branches on them instead of retrying.

**Verify:** a second start under the same ID with different arguments throws
`DurableTaskConflictException`, and the target's dead letters record the
`durable-rpc/conflict` code; see [Dead letters](durable-tasks-operations.md#dead-letters).
