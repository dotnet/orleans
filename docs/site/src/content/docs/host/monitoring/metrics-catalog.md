---
title: Orleans metrics catalog
description: Complete reference for metrics emitted by the Microsoft.Orleans meter.
ms.date: 09/04/2026
ms.topic: reference
---

# Orleans metrics catalog

This catalog lists every instrument created for the `Microsoft.Orleans` meter in the current source tree. Core runtime instruments are available when their component runs. Streaming, transactions, durable jobs, journaling, and provider-specific instruments appear only when those features are configured. Discover the instruments emitted by the deployed Orleans version because releases and configured packages can differ.

The **Unit** column gives the natural unit recorded by Orleans. "Implicit" means the instrument doesn't set unit metadata. `TimeSpan` ticks are 100 nanoseconds. The **Attributes** column lists attributes added by Orleans; resource attributes such as `service.instance.id` also identify each time series.

Instrument types use these abbreviations:

- **C**: <xref:System.Diagnostics.Metrics.Counter`1>
- **OC**: <xref:System.Diagnostics.Metrics.ObservableCounter`1>
- **UDC**: <xref:System.Diagnostics.Metrics.UpDownCounter`1>
- **OG**: <xref:System.Diagnostics.Metrics.ObservableGauge`1>
- **H**: <xref:System.Diagnostics.Metrics.Histogram`1>

Some instruments retain historical types or unit strings. The descriptions below state the runtime behavior so queries can preserve those semantics.

## Application requests and clients

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-app-requests-canceled` | C | Requests, implicit | `grain_type` | Outbound grain requests canceled by their caller. |
| `orleans-app-requests-latency-bucket` | OC | Milliseconds, implicit | `duration` | Completed outbound request callbacks in one latency band. Bands are mutually exclusive, range from `1ms` through `15000ms`, and include an overflow band. |
| `orleans-app-requests-latency-count` | OC | Requests, implicit | - | Total completed outbound request callbacks represented by the latency bands. |
| `orleans-app-requests-latency-sum` | OC | Milliseconds, implicit | - | Sum of caller-observed elapsed time for completed outbound request callbacks. |
| `orleans-app-requests-timedout` | C | Requests, implicit | `grain_type` | Outbound grain requests which exceeded the configured response timeout. |
| `orleans-client-connected-gateways` | OG | Gateways, implicit | - | Gateway connections currently held by an Orleans client. |

Request latency covers the interval until the caller's callback completes, including terminal timeout, cancellation, target-silo failure, and host-shutdown paths.

## Networking, gateways, and messaging

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-gateway-connected-clients` | UDC | Clients, implicit | - | Client identities currently tracked by a silo gateway, including disconnected clients retained during the configured drop timeout. |
| `orleans-gateway-load-shedding` | C | Messages, implicit | - | Client requests rejected because the gateway overload detector reported saturation. |
| `orleans-gateway-received` | C | Messages, implicit | - | Messages received by a silo through a client gateway connection. |
| `orleans-gateway-sent` | C | Messages, implicit | - | Gateway message-send events, including messages written to client connections. |
| `orleans-messaging-expired` | C | Messages, implicit | `Phase` | Messages which expired during `Send`, `Receive`, `Dispatch`, `Invoke`, or `Respond`. |
| `orleans-messaging-pings-received` | C | Pings, implicit | `Destination` | Membership probe messages received from the identified silo address. |
| `orleans-messaging-pings-reply-missed` | C | Replies, implicit | `Destination` | Direct or indirect membership probes which didn't receive a successful reply from the identified silo address. |
| `orleans-messaging-pings-reply-received` | C | Replies, implicit | `Destination` | Successful direct or indirect membership probe replies from the identified silo address. |
| `orleans-messaging-pings-sent` | C | Pings, implicit | `Destination` | Membership probe messages sent to the identified silo address. |
| `orleans-messaging-processing-activation-data` | OG | Requests, implicit | - | Requests currently queued or executing across all grain activations on the silo. |
| `orleans-messaging-processing-dispatcher-forwarded` | OC | Messages, implicit | - | Messages forwarded by the dispatcher. |
| `orleans-messaging-processing-dispatcher-processed` | OC | Messages, implicit | `Direction`, `Status` | Messages processed by the dispatcher, split by message direction and `Ok` or `Error` result. |
| `orleans-messaging-processing-dispatcher-received` | OC | Messages, implicit | `Context`, `Direction` | Messages received by the dispatcher, split by message direction and whether a grain runtime context was present. |
| `orleans-messaging-processing-ima-enqueued` | OC | Messages, implicit | `Context` | Messages enqueued to an incoming-message-agent target: `ToNull`, `ToSystemTarget`, or `ToGrain`. |
| `orleans-messaging-processing-ima-received` | OC | Messages, implicit | - | Messages received by the incoming message agent. |
| `orleans-messaging-received-header-size` | OC | `bytes` | - | Cumulative serialized header bytes received by the process. |
| `orleans-messaging-received-messages-size` | H | `bytes` | `ConnectionDirection`, `MessageDirection`, optional `silo` | Distribution of total received message sizes. |
| `orleans-messaging-rejected` | C | Messages, implicit | `Direction` | Messages rejected by the runtime. |
| `orleans-messaging-rerouted` | C | Messages, implicit | `Direction` | Messages rerouted after their original route couldn't be used. |
| `orleans-messaging-sent-dropped` | C | Messages, implicit | `Direction` | Outbound messages dropped before successful transmission. |
| `orleans-messaging-sent-failed` | C | Messages, implicit | `Direction` | Outbound messages whose send attempt failed. |
| `orleans-messaging-sent-header-size` | OC | `bytes` | - | Cumulative serialized header bytes sent by the process. |
| `orleans-messaging-sent-local` | OC | Messages, implicit | - | Messages delivered locally without a network send. |
| `orleans-messaging-sent-messages-size` | H | `bytes` | `ConnectionDirection`, `MessageDirection`, optional `silo` | Distribution of total sent message sizes. |
| `orleans-networking-sockets-closed` | C | Sockets, implicit | `Direction` | Network connections closed, split by connection direction. |
| `orleans-networking-sockets-opened` | C | Sockets, implicit | `Direction` | Network connections opened, split by connection direction. |

`Destination` and `silo` contain silo addresses and incarnations. They are useful during incident diagnosis and can create new time series during restarts.

## Runtime dissemination

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-dissemination-anti-entropy-digests` | C | `digests` | `direction` | Digest entries sent in successful outbound exchanges or received in inbound exchanges. |
| `orleans-dissemination-anti-entropy-exchanges` | C | `operations` | `direction`, `truncated` | Successful anti-entropy requests and responses. `truncated=true` indicates that response budgets left candidates for a later exchange. |
| `orleans-dissemination-anti-entropy-failures` | C | `operations` | `reason` | Outbound anti-entropy peer operations which ended in `timeout` or `error`. |
| `orleans-dissemination-anti-entropy-values` | C | `values` | `direction` | Repair values returned by successful anti-entropy exchanges. |
| `orleans-dissemination-broadcast-received` | C | `messages` | `namespace`, `kind` | Broadcast batches received, counted once for each namespace represented in the batch. |
| `orleans-dissemination-broadcast-scheduled` | C | `schedules` | `reason` | Per-peer pump schedules split into `immediate`, `coalesce`, `retry`, and `priority`. |
| `orleans-dissemination-broadcast-send-failures` | C | `attempts` | `reason` | Broadcast send attempts which ended in `timeout` or `error`. |
| `orleans-dissemination-broadcast-sent` | C | `messages` | `namespace`, `kind` | Successfully completed broadcast sends, counted once for each namespace represented in the batch. |
| `orleans-dissemination-bytes-sent` | C | `bytes` | `namespace`, `kind` | Serialized dissemination payload bytes in successful broadcasts. |
| `orleans-dissemination-payload-dropped` | C | `values` | `namespace`, `reason` | Values rejected by a payload guard. |
| `orleans-dissemination-pump-failures` | C | `failures` | `status` | Unexpected peer-pump failures split into `recovered` and `permanent`. |
| `orleans-dissemination-publications` | C | `operations` | `namespace`, `result`, `reason` | Publication attempts classified as `accepted` or `rejected`. Rejection reasons identify disabled namespaces, unavailable membership or values, invalid versions or repairs, and over-budget repairs. |
| `orleans-dissemination-queue-admission-rejected` | C | `keys` | `namespace`, `reason` | New distinct keys rejected after a namespace reached its per-peer pending-key limit. |
| `orleans-dissemination-values-applied` | C | `values` | `namespace`, `result` | Received values classified by the namespace apply result. |
| `orleans-dissemination-values-received` | C | `values` | `namespace`, `kind` | Values included in received broadcast batches before individual application. |
| `orleans-dissemination-values-sent` | C | `values` | `namespace`, `kind` | Values included in successfully completed broadcast sends. |

All dissemination attributes have bounded runtime-defined values. Keys and peer addresses are available through opt-in diagnostic events instead of metric attributes. See [Monitor runtime dissemination](runtime-dissemination.md).

## Scheduling, activations, and grains

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-catalog-activation-collections` | C | Scans, implicit | - | Activation-collection scans performed by the silo. |
| `orleans-catalog-activation-concurrent-registration-attempts` | C | Attempts, implicit | - | Activation registrations which encountered an activation already registered elsewhere. |
| `orleans-catalog-activation-created` | C | Activations, implicit | - | Activation objects created on the silo. |
| `orleans-catalog-activation-destroyed` | C | Activations, implicit | - | Activations removed from the silo catalog. |
| `orleans-catalog-activation-failed-to-activate` | C | Activations, implicit | - | Activations whose lifecycle initialization failed or was canceled. |
| `orleans-catalog-activation-latency` | H | `ms` | `status`, `directory` | Activation duration, split by outcome and whether the grain uses the directory. Status values include `success`, `canceled`, `directory_error`, `duplicate`, and `error`. |
| `orleans-catalog-activation-non-existent` | C | Messages, implicit | - | Messages which targeted an activation that wasn't present in the local catalog. |
| `orleans-catalog-activation-shutdown` | C | Activations, implicit | `via` | Activation shutdowns split by `collection`, `deactivateOnIdle`, `deactivateStuckActivation`, or `migration`. |
| `orleans-catalog-activation-working-set` | OG | Activations, implicit | - | Activations currently in the silo's active working set. |
| `orleans-catalog-activations` | OG | Activations, implicit | - | Activations currently registered in the silo catalog. |
| `orleans-catalog-deactivation-latency` | H | `ms` | `via` | Deactivation duration, split by the shutdown path. |
| `orleans-grains` | UDC | Grains, implicit | `type` | Current grain instances by grain type. |
| `orleans-scheduler-long-running-turns` | C | Turns, implicit | - | Grain micro-turns whose synchronous execution exceeded <xref:Orleans.Configuration.SchedulingOptions.TurnWarningLengthThreshold>. |
| `orleans-system-targets` | UDC | System targets, implicit | `type` | Current Orleans system-target instances by type. |

## Grain directory and consistent rings

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-consistent-ring-range-percentage-average` | OG | Percent, implicit | - | Equal-share percentage of the consistent-hash ring for one silo (`100 / ring size`). |
| `orleans-consistent-ring-range-percentage-local` | OG | Percent, implicit | - | Percentage of the consistent-hash ring currently owned by this silo. |
| `orleans-consistent-ring-size` | OG | Silos, implicit | - | Members in the consistent-hash ring. |
| `orleans-directory-cache-size` | OG | Entries, implicit | - | Total entries across registered local grain-directory caches. |
| `orleans-directory-lookups-cache-issued` | C | Lookups, implicit | - | Grain-location lookups attempted against the local directory cache. |
| `orleans-directory-lookups-cache-successes` | C | Lookups, implicit | - | Local directory-cache lookups which returned a usable entry. |
| `orleans-directory-lookups-full-issued` | C | Lookups, implicit | - | Full grain-directory lookups initiated by this silo. |
| `orleans-directory-lookups-local-directory-issued` | C | Lookups, implicit | - | Lookups issued directly against the local directory partition. |
| `orleans-directory-lookups-local-directory-successes` | C | Lookups, implicit | - | Direct local-partition lookups which returned a grain address. |
| `orleans-directory-lookups-local-issued` | C | Lookups, implicit | - | Grain-location lookups issued through the local grain locator. |
| `orleans-directory-lookups-local-successes` | C | Lookups, implicit | - | Local grain-locator lookups which returned a grain address. |
| `orleans-directory-lookups-remote-received` | C | Lookups, implicit | - | Forwarded directory lookup requests received from another silo. |
| `orleans-directory-lookups-remote-sent` | C | Lookups, implicit | - | Directory lookup requests forwarded to another silo. |
| `orleans-directory-partition-size` | OG | Entries, implicit | - | Grain registrations in this silo's local directory partition. |
| `orleans-directory-range-lock-held-duration` | H | Milliseconds, implicit | - | Distribution of time directory range locks were held. |
| `orleans-directory-recovery-count` | C | Recoveries, implicit | - | Directory range recoveries completed after membership changes. |
| `orleans-directory-recovery-duration` | H | Milliseconds, implicit | - | Distribution of completed directory range-recovery durations. |
| `orleans-directory-registration-duration` | H | `ms` | `locator`, `status` | Grain registration duration by locator implementation and `success`, `canceled`, or `error` outcome. |
| `orleans-directory-registrations` | C | Registrations, implicit | `locator`, `status` | Completed grain registration attempts by locator implementation and outcome. |
| `orleans-directory-registrations-single-act-issued` | C | Registrations, implicit | - | Single-activation registration requests initiated on this silo. |
| `orleans-directory-registrations-single-act-local` | C | Registrations, implicit | - | Single-activation registrations handled by the local directory owner. |
| `orleans-directory-registrations-single-act-remote-received` | C | Registrations, implicit | - | Forwarded single-activation registration requests received from another silo. |
| `orleans-directory-registrations-single-act-remote-sent` | C | Registrations, implicit | - | Single-activation registration requests forwarded to another silo. |
| `orleans-directory-ring-local-portion-average-percentage` | OG | Percent, implicit | - | Equal-share percentage of the local grain-directory ring (`100 / ring size`). |
| `orleans-directory-ring-local-portion-distance` | OG | Hash-ring distance, implicit | - | Raw hash-ring distance from this silo to its successor. |
| `orleans-directory-ring-local-portion-percentage` | OG | Percent, implicit | - | Percentage of the local grain-directory ring assigned to this silo. |
| `orleans-directory-ring-size` | OG | Silos, implicit | - | Members in the local grain-directory ring. |
| `orleans-directory-snapshot-transfer-count` | C | Transfers, implicit | - | Directory range snapshots successfully received from the previous partition owner. |
| `orleans-directory-snapshot-transfer-duration` | H | Milliseconds, implicit | - | Distribution of successful directory snapshot-transfer durations. |
| `orleans-directory-unregistrations-issued` | C | Unregistrations, implicit | - | Single grain-unregistration requests initiated on this silo. |
| `orleans-directory-unregistrations-local` | C | Unregistrations, implicit | - | Single grain-unregistration requests handled by the local directory owner. |
| `orleans-directory-unregistrations-many-issued` | C | Requests, implicit | - | Batch grain-unregistration requests initiated on this silo. |
| `orleans-directory-unregistrations-many-remote-received` | C | Requests, implicit | - | Forwarded batch unregistration requests received from another silo. |
| `orleans-directory-unregistrations-many-remote-sent` | C | Requests, implicit | - | Batch unregistration requests forwarded to another silo. |
| `orleans-directory-unregistrations-remote-received` | C | Unregistrations, implicit | - | Forwarded single unregistration requests received from another silo. |
| `orleans-directory-unregistrations-remote-sent` | C | Unregistrations, implicit | - | Single unregistration requests forwarded to another silo. |
| `orleans-directory-validations-cache-received` | C | Requests, implicit | - | Batch directory-cache validation requests received from another silo. |

## Reminders, storage, runtime resources, and watchdog

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-reminders-active` | OG | Reminders, implicit | - | Upper bound on reminders tracked locally, including temporary tombstones. |
| `orleans-reminders-tardiness` | H | `seconds` | - | Delay between a reminder's scheduled tick time and the time delivery begins. |
| `orleans-reminders-ticks-delivered` | C | Ticks, implicit | - | Reminder ticks successfully delivered to `IRemindable.ReceiveReminder`. |
| `orleans-runtime-available-memory` | OG | `MB` | - | GC-reported available memory budget, divided by 1,024 squared. |
| `orleans-runtime-total-physical-memory` | OG | `MB` | - | <xref:System.GCMemoryInfo.TotalAvailableMemoryBytes>, divided by 1,024 squared. The historical name can represent a process or container memory limit instead of host physical RAM. |
| `orleans-storage-clear-errors` | C | Errors, implicit | `provider_type_name`, `state_name`, `state_type` | Grain-state clear operations which threw an exception. |
| `orleans-storage-clear-latency` | H | `ms` | `provider_type_name`, `state_name`, `state_type` | Latency of successfully completed grain-state clear operations. |
| `orleans-storage-read-errors` | C | Errors, implicit | `provider_type_name`, `state_name`, `state_type` | Grain-state read operations which threw an exception. |
| `orleans-storage-read-latency` | H | `ms` | `provider_type_name`, `state_name`, `state_type` | Latency of successfully completed grain-state reads. |
| `orleans-storage-write-errors` | C | Errors, implicit | `provider_type_name`, `state_name`, `state_type` | Grain-state write operations which threw an exception. |
| `orleans-storage-write-latency` | H | `ms` | `provider_type_name`, `state_name`, `state_type` | Latency of successfully completed grain-state writes. |
| `orleans-watchdog-health-checks` | C | Checks, implicit | - | Runtime watchdog health checks performed. |
| `orleans-watchdog-health-checks-failed` | C | Checks, implicit | - | Runtime watchdog health checks which reported failure. |

## Streaming

Pub/sub instruments use `provider` and `grain_type`. Persistent-stream read instruments use `provider` and `queue`; sent instruments use `provider` and `grain_type`. Default receiver and cache monitors use `QueueId`, and default block-pool monitors use `BlockPoolId`. Custom monitor implementations can choose different dimensions.

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-streams-block-pool-allocated-memory` | OC | `bytes` | Default: `BlockPoolId` | Cumulative bytes allocated from the stream buffer pool. |
| `orleans-streams-block-pool-available-memory` | OC | `bytes` | Default: `BlockPoolId` | Current available bytes reported by the stream buffer pool. This point-in-time value is implemented as an observable counter. |
| `orleans-streams-block-pool-claimed-memory` | OC | `bytes` | Default: `BlockPoolId` | Current claimed bytes reported by the stream buffer pool. This point-in-time value is implemented as an observable counter. |
| `orleans-streams-block-pool-released-memory` | OC | `bytes` | Default: `BlockPoolId` | Cumulative bytes released to the stream buffer pool. |
| `orleans-streams-block-pool-total-memory` | OC | `bytes` | Default: `BlockPoolId` | Current total bytes reported by the stream buffer pool. This point-in-time value is implemented as an observable counter. |
| `orleans-streams-persistent-stream-messages-read` | C | Batch containers, implicit | `provider`, `queue` | Batch containers read from persistent-stream receiver queues. One container can hold multiple stream events. |
| `orleans-streams-persistent-stream-messages-sent` | C | Batch containers, implicit | `provider`, `grain_type` | Batch containers selected for consumer delivery. The counter increments before delivery, so it isn't a successful-delivery count. |
| `orleans-streams-persistent-stream-pubsub-cache-size` | OG | Entries, implicit | `name` | Pub/sub cache entries held by a persistent-stream pulling agent. |
| `orleans-streams-persistent-stream-pulling-agents` | OG | Agents, implicit | `name` | Pulling agents currently owned by a persistent-stream provider on this silo. |
| `orleans-streams-pubsub-consumers` | C, signed deltas | Consumers, implicit | `provider`, `grain_type` | Signed cumulative consumer-registration deltas recorded by this process, using positive additions and negative removals. The series isn't initialized from persisted pub/sub state and isn't a point-in-time count. |
| `orleans-streams-pubsub-consumers-added` | C | Attempts, implicit | `provider`, `grain_type` | Pub/sub consumer registration attempts. |
| `orleans-streams-pubsub-consumers-removed` | C | Attempts, implicit | `provider`, `grain_type` | Pub/sub consumer unregistration attempts. |
| `orleans-streams-pubsub-producers` | C, signed deltas | Producers, implicit | `provider`, `grain_type` | Signed cumulative producer-registration deltas recorded by this process, using positive additions and negative removals. The series isn't initialized from persisted pub/sub state and isn't a point-in-time count. |
| `orleans-streams-pubsub-producers-added` | C | Attempts, implicit | `provider`, `grain_type` | Pub/sub producer registration attempts. |
| `orleans-streams-pubsub-producers-removed` | C | Attempts, implicit | `provider`, `grain_type` | Pub/sub producer unregistration attempts. |
| `orleans-streams-queue-cache-length` | OC | `messages` | Default: `QueueId` | Current number of messages in a queue cache. This point-in-time value is implemented as an observable counter. |
| `orleans-streams-queue-cache-memory-allocated` | OC | Bytes, implicit | Default: `QueueId` | Cumulative bytes allocated by a queue cache. |
| `orleans-streams-queue-cache-memory-released` | OC | Bytes, implicit | Default: `QueueId` | Cumulative bytes released by a queue cache. |
| `orleans-streams-queue-cache-messages-added` | OC | Messages, implicit | Default: `QueueId` | Cumulative messages added to a queue cache. |
| `orleans-streams-queue-cache-messages-purged` | OC | Messages, implicit | Default: `QueueId` | Cumulative messages purged from a queue cache. |
| `orleans-streams-queue-cache-oldest-age` | OG | `TimeSpan` ticks, implicit | Default: `QueueId` | Age of the oldest dequeued message still represented by the cache monitor. |
| `orleans-streams-queue-cache-oldest-to-newest-duration` | OG | `TimeSpan` ticks, implicit | Default: `QueueId` | Enqueue-time span from the oldest to newest messages represented by the cache monitor. |
| `orleans-streams-queue-cache-pressure` | OG | Ratio, implicit | Default: `QueueId`, `PressureMonitorType` | Current pressure value reported by each cache pressure monitor. |
| `orleans-streams-queue-cache-pressure-contribution-count` | OG | Contributions, implicit | Default: `QueueId`, `PressureMonitorType` | Pressure-contribution count reported by each cache pressure monitor. |
| `orleans-streams-queue-cache-size` | OC | `bytes` | Default: `QueueId` | Current queue-cache size. This point-in-time value is implemented as an observable counter. |
| `orleans-streams-queue-cache-under-pressure` | OG | Boolean (`0` or `1`), implicit | Default: `QueueId`, `PressureMonitorType` | Whether each cache pressure monitor currently reports pressure. |
| `orleans-streams-queue-initialization-duration` | C | `TimeSpan` ticks, implicit | Default: `QueueId` | Cumulative duration of queue receiver initialization calls. |
| `orleans-streams-queue-initialization-exceptions` | C | Exceptions, implicit | Default: `QueueId` | Queue receiver initialization calls which supplied an exception. |
| `orleans-streams-queue-initialization-failures` | C | Failures, implicit | Default: `QueueId` | Queue receiver initialization calls reported as unsuccessful. |
| `orleans-streams-queue-messages-received` | OC | Messages, implicit | Default: `QueueId` | Cumulative messages received from a stream queue. |
| `orleans-streams-queue-newest-message-enqueue-age` | OG | `TimeSpan` ticks, implicit | Default: `QueueId` | Age of the newest message returned by the latest queue read. |
| `orleans-streams-queue-oldest-message-enqueue-age` | OG | `TimeSpan` ticks, implicit | Default: `QueueId` | Age of the oldest message returned by the latest queue read. |
| `orleans-streams-queue-read-duration` | C | `TimeSpan` ticks, implicit | Default: `QueueId` | Cumulative duration of queue receiver read calls. |
| `orleans-streams-queue-read-exceptions` | C | Exceptions, implicit | Default: `QueueId` | Queue receiver read calls which supplied an exception. |
| `orleans-streams-queue-read-failures` | C | Failures, implicit | Default: `QueueId` | Queue receiver read calls reported as unsuccessful. |
| `orleans-streams-queue-shutdown-duration` | C | `TimeSpan` ticks, implicit | Default: `QueueId` | Cumulative duration of queue receiver shutdown calls. |
| `orleans-streams-queue-shutdown-exceptions` | C | Exceptions, implicit | Default: `QueueId` | Queue receiver shutdown calls which supplied an exception. |
| `orleans-streams-queue-shutdown-failures` | C | Failures, implicit | Default: `QueueId` | Queue receiver shutdown calls reported as unsuccessful. |

Failure and exception counters can both increment for one operation. Duration counters aggregate total elapsed ticks; divide their increase by the matching operation count when calculating a windowed mean.

## Transactions

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-transactions-failed` | OC | Transactions, implicit | - | Transactions which completed unsuccessfully. |
| `orleans-transactions-started` | OC | Transactions, implicit | - | Transactions started by the local transaction agent. |
| `orleans-transactions-successful` | OC | Transactions, implicit | - | Transactions which completed successfully. |
| `orleans-transactions-throttled` | OC | Transactions, implicit | - | Transaction attempts rejected by transaction-agent throttling. |

## Durable jobs

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-durablejobs-cancel-job-call-duration` | H | `ms` | `status` | Duration of `CancelAsync` calls, split by `cancellation_requested`, `not_found`, `operation_canceled`, or `error`. |
| `orleans-durablejobs-cancel-job-calls` | C | Calls, implicit | `status` | Completed `CancelAsync` calls by request outcome. |
| `orleans-durablejobs-handler-execution-duration` | H | `ms` | `status` | Duration of completed handler executions, split by `completed`, `attempt_canceled`, or `failed`. |
| `orleans-durablejobs-handler-executions` | C | Executions, implicit | `status` | Handler executions which reached a terminal outcome. |
| `orleans-durablejobs-handler-executions-started` | C | Executions, implicit | - | Durable-job handler executions started. |
| `orleans-durablejobs-job-attempt-duration` | H | `ms` | `status` | Duration of job attempts ending as `completed`, `failed`, `retried`, or `rescheduled`. |
| `orleans-durablejobs-job-attempts-started` | C | Attempts, implicit | - | Durable-job attempts started. |
| `orleans-durablejobs-job-dispatch-lag` | H | `ms` | - | Delay between a job's due time and the start of its attempt. |
| `orleans-durablejobs-job-schedule-duration` | H | `ms` | - | Time required to persist and schedule a job. |
| `orleans-durablejobs-job-cancellation-requests` | C | Requests, implicit | - | Durable job cancellation requests which were durably recorded. |
| `orleans-durablejobs-jobs-completed` | C | Jobs, implicit | - | Durable jobs completed successfully. |
| `orleans-durablejobs-jobs-failed` | C | Jobs, implicit | - | Durable-job attempts recorded as failed. |
| `orleans-durablejobs-jobs-retried` | C | Jobs, implicit | - | Durable-job attempts scheduled for retry. |
| `orleans-durablejobs-jobs-scheduled` | C | Jobs, implicit | - | Durable jobs successfully scheduled. |
| `orleans-durablejobs-ownership-check-duration` | H | `ms` | - | Duration of shard-ownership checks. |
| `orleans-durablejobs-schedule-job-call-duration` | H | `ms` | `status` | Duration of schedule-job API calls, split by `ok`, `operation_canceled`, or `error`. |
| `orleans-durablejobs-schedule-job-calls` | C | Calls, implicit | `status` | Completed schedule-job API calls by outcome. |
| `orleans-durablejobs-shard-batch-bytes` | H | `bytes` | - | Serialized byte size of durable-job shard mutation batches. |
| `orleans-durablejobs-shard-batch-mutations` | H | Mutations, implicit | - | Mutation count in durable-job shard batches. |
| `orleans-durablejobs-shard-pending-depth` | H | Mutations, implicit | - | Pending mutation operations collected into each shard batch immediately before processing. |
| `orleans-durablejobs-shard-processing-duration` | H | `ms` | `status` | Shard processing duration, split by `completed`, `attempt_canceled`, or `error`. |
| `orleans-durablejobs-shards-processed` | C | Shards, implicit | `status` | Shard processing passes completed by outcome. |
| `orleans-durablejobs-storage-batch-size` | H | Mutations, implicit | `status` | Applied shard mutations included in each state-write attempt, split by outcome. |
| `orleans-durablejobs-storage-batches` | C | Batches, implicit | `status` | Durable-job storage batches split by `ok`, `operation_canceled`, or `error`. |
| `orleans-durablejobs-stripe-distribution` | C | Assignments, implicit | `stripe` | Job assignments by durable-job stripe number. |

## Journaling

Core journaling instruments use `operation` and `status` where applicable. Operation values include `append`, `snapshot`, `replace`, `read`, `delete`, and `recovery`; status is `ok` or `error`.

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-journaling-compaction-triggers` | C | Triggers, implicit | `reason` | Journal compactions requested because of `user_snapshot`, `storage_requested`, or `migration`. |
| `orleans-journaling-gather-duration` | H | `ms` | `operation` | Time spent gathering journal state for a storage operation. |
| `orleans-journaling-recoveries` | C | Recoveries, implicit | `operation`, `status` | Journal recovery attempts by outcome. |
| `orleans-journaling-recovery-duration` | H | `ms` | `operation`, `status` | Journal recovery duration by outcome. |
| `orleans-journaling-state-delete-duration` | H | `ms` | `operation`, `status` | Duration of journaled-state delete requests. |
| `orleans-journaling-state-delete-requests` | C | Requests, implicit | `operation`, `status` | Journaled-state delete requests by outcome. |
| `orleans-journaling-state-scan-count` | H | States, implicit | `operation` | Journal states scanned while gathering work for an operation. |
| `orleans-journaling-state-write-duration` | H | `ms` | `operation`, `status` | Duration of journaled-state write requests. |
| `orleans-journaling-state-write-requests` | C | Requests, implicit | `operation`, `status` | Journaled-state write requests by operation and outcome. |
| `orleans-journaling-storage-bytes` | C | `bytes` | `operation` | Cumulative bytes successfully processed by journal storage operations. |
| `orleans-journaling-storage-operation-bytes` | H | `bytes` | `operation`, `status` | Distribution of successful journal storage operation sizes. |
| `orleans-journaling-storage-operation-duration` | H | `ms` | `operation`, `status` | Journal storage operation duration by operation and outcome. |
| `orleans-journaling-storage-operation-queue-duration` | H | `ms` | `operation`, `status` | Time journal storage operations spent queued before execution. |
| `orleans-journaling-storage-operations` | C | Operations, implicit | `operation`, `status` | Journal storage operations by operation and outcome. |
| `orleans-journaling-write-coalesced-callers` | H | Callers, implicit | `operation` | Number of callers combined into each coalesced journal write. |

Azure journaling storage instruments use operations `create`, `get_metadata`, `update_metadata`, `append`, `delete`, `read`, and `replace`.

| Instrument | Type | Unit | Attributes | Description |
|---|---|---|---|---|
| `orleans-journaling-azure-blob-operation-bytes` | C | `bytes` | `operation` | Cumulative bytes successfully processed by Azure Blob journal storage operations. |
| `orleans-journaling-azure-blob-operation-duration` | H | `ms` | `operation`, `status` | Azure Blob journal storage operation duration by operation and outcome. |
| `orleans-journaling-azure-blob-operations` | C | Operations, implicit | `operation`, `status` | Azure Blob journal storage operations by operation and `ok` or `error` status. |
| `orleans-journaling-azure-table-operation-bytes` | C | `bytes` | `operation` | Cumulative bytes successfully processed by Azure Table journal storage operations. |
| `orleans-journaling-azure-table-operation-duration` | H | `ms` | `operation`, `status` | Azure Table journal storage operation duration by operation and outcome. |
| `orleans-journaling-azure-table-operations` | C | Operations, implicit | `operation`, `status` | Azure Table journal storage operations by operation and `ok` or `error` status. |

## Use the catalog

Use [Monitor Orleans metrics](metrics.md) to select health indicators, aggregate each instrument according to its type, control cardinality, and build alerts. Use [Troubleshoot Orleans incidents](troubleshooting.md) to correlate metrics with logs, traces, deployment changes, and dependency health.
