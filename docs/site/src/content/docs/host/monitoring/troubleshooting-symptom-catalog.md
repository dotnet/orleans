---
title: Orleans symptom and signal catalog
description: Find Orleans production remedies by exception, log event, metric, or observed behavior.
ms.date: 08/11/2026
ms.topic: troubleshooting
---

# Orleans symptom and signal catalog

Use this catalog when you have a specific exception, event ID, metric, or observed behavior. Search this page for the literal signal, then use the linked runbook for the full investigation sequence.

An exception or event identifies where Orleans noticed a problem, not necessarily its root cause. Preserve the exception chain, structured <xref:Microsoft.Extensions.Logging.EventId>, category, trace ID, silo identity, cluster membership view, and the first occurrence before restarting processes.

## Quick index

| Signal | Start with |
|---|---|
| <xref:Orleans.Runtime.Messaging.ConnectionFailedException>, no connected gateway | [Connection and gateway failures](#connection-and-gateway-failures) |
| <xref:Orleans.Runtime.SiloUnavailableException>, <xref:Orleans.Runtime.OrleansMessageRejectionException> | [Silo unavailable or message rejected](#silo-unavailable-or-message-rejected) |
| <xref:Orleans.Runtime.GatewayTooBusyException>, rejected or load-shed messages | [Gateway or cluster overload](#gateway-or-cluster-overload) |
| <xref:System.TimeoutException>, `orleans-app-requests-timedout` | [Request timeouts](#request-timeouts) |
| `orleans-scheduler-long-running-turns`, calls to one grain stop progressing | [Long-running or deadlocked grain turns](#long-running-or-deadlocked-grain-turns) |
| <xref:Orleans.Storage.InconsistentStateException>, ETag or version conflict | [Storage consistency failures](#storage-consistency-failures) |
| Storage errors or latency rise with backend throttling | [Storage throttling](#storage-throttling) |
| Serializer or codec exception after deployment | [Serialization failures after a version change](#serialization-failures-after-a-version-change) |
| Repeated membership changes, missed pings, duplicate activations | [Membership and directory churn](#membership-and-directory-churn) |
| One grain is slow while cluster capacity remains | [Hot grains and skewed placement](#hot-grains-and-skewed-placement) |
| Reminder or timer runs late, twice, or not at all | [Reminder and timer timing](#reminder-and-timer-timing) |
| Available memory falls, GC pauses rise, or a container is OOM-killed | [Memory pressure](#memory-pressure) |
| Shutdown exceeds the termination grace period | [Shutdown does not complete](#shutdown-does-not-complete) |

## Connection and gateway failures

**Signals:** <xref:Orleans.Runtime.Messaging.ConnectionFailedException>, repeated connection attempts, no connected gateways, or client readiness remains false.

**Likely causes:** client and silo cluster identities differ; the gateway provider returns no active silo; advertised gateway addresses aren't reachable from the client; DNS, firewall, NAT, or network policy blocks traffic; or TLS names, trust, or client-certificate requirements don't match.

**Confirm:** query the gateway source from the client network, compare cluster and service IDs, and test DNS and TCP connectivity to every advertised gateway. For TLS, preserve the inner handshake exception and inspect certificate SAN, trust chain, EKU, and `TargetHost`.

**Remedy:** correct identity/provider configuration or publish addresses which the client can route to. Replace an invalid certificate or trust configuration. Don't hide a deterministic configuration failure behind an unbounded startup retry.

**Prevent:** validate exact advertised endpoints from each deployment network and keep readiness false until the client has a usable gateway. Follow [Client can't connect](troubleshooting.md#client-cant-connect).

## Silo unavailable or message rejected

**Signals:** <xref:Orleans.Runtime.SiloUnavailableException>, <xref:Orleans.Runtime.OrleansMessageRejectionException>, connection closure, or failures clustered around a membership change.

**Likely causes:** a silo stopped or became unreachable; membership hasn't converged; a stale activation address was used during churn; the destination is shutting down; or overload caused a rejection.

**Confirm:** correlate the exception timestamp and target silo with membership, deployment, socket, ping, and process events. Determine whether the call reached application code by using traces or an application operation ID.

**Remedy:** restore cluster/network health or complete the rollout. The grain reference remains valid for later calls. Retry only bounded, idempotent or deduplicated operations because the failed call can have an unknown outcome.

**Prevent:** preserve failure-domain capacity during rollout, make retryable operations idempotent, and test membership loss under load. See [Calls fail during membership changes](../../deployment/troubleshooting-deployments.md#calls-fail-during-membership-changes).

## Gateway or cluster overload

**Signals:** <xref:Orleans.Runtime.GatewayTooBusyException>, rejected messages, gateway load shedding, rising queue latency, or throughput falls while demand rises.

**Likely causes:** gateway admission limits, saturated grain schedulers, a hot grain, thread-pool or CPU starvation, or a storage/stream dependency which can't keep up.

**Confirm:** compare ingress, completion, rejection, and timeout rates with CPU, thread-pool queues, long-running turns, activation count, and dependency latency. Compare silos: one outlier suggests skew or a host problem; a cluster-wide shift suggests shared capacity or dependency pressure.

**Remedy:** reduce admission and optional work first. Use bounded exponential backoff with jitter at a safe retry boundary. Scale only when work can partition and shared dependencies have headroom.

**Prevent:** load test the real key and fan-out distribution, alert before rejection begins, and maintain a capacity envelope for gateways and dependencies. Follow [The cluster is overloaded](troubleshooting.md#the-cluster-is-overloaded).

## Request timeouts

**Signals:** <xref:System.TimeoutException>, increasing `orleans-app-requests-timedout`, or high `orleans-app-requests-latency`.

**Likely causes:** queueing before execution, a long-running grain turn, cyclic calls, blocking work, slow storage or another dependency, membership recovery, or network loss.

**Confirm:** follow a sampled trace to determine whether the call reached a silo and where it waited. Correlate timeouts with long-running turns, rejected/dropped messages, CPU, GC, thread pool, membership, and dependency latency.

**Remedy:** remove the bottleneck or reduce load. Increase a timeout only when measured valid latency exceeds the budget. Treat the timed-out operation as having an unknown outcome unless the application can reconcile it.

**Prevent:** set end-to-end latency budgets, bound dependency calls and queues, propagate cancellation where safe, and use idempotency or deduplication for retries. Follow [Grain calls time out](troubleshooting.md#grain-calls-time-out).

## Long-running or deadlocked grain turns

**Signals:** `orleans-scheduler-long-running-turns` increases; calls to one activation stop progressing; traces show long application execution; or stacks show `.Result`, `.Wait()`, monitor contention, or a cyclic grain call.

**Likely causes:** synchronous blocking, CPU-heavy work, an unbounded loop, external I/O without a timeout, or an inter-grain call cycle. Reentrancy can allow cycles to progress, but broad reentrancy can also expose state to interleaving and doesn't repair synchronous blocking.

**Confirm:** capture a trace or dump while the issue is active. Identify the grain type and method, inspect managed stacks and scheduler queues, and draw the call graph. Check whether a non-reentrant activation is awaiting a call which eventually calls back into it.

**Remedy:** remove blocking waits; use bounded asynchronous operations; break call cycles; move CPU-heavy work out of grain turns; or redesign the protocol. Apply <xref:Orleans.Concurrency.ReentrantAttribute> or <xref:Orleans.Concurrency.AlwaysInterleaveAttribute> only after proving that the interleaved state transitions are safe.

**Prevent:** keep turns bounded, review inter-grain call graphs, test callback paths, and document grain invariants under interleaving. Follow [Grain turns appear stuck](troubleshooting.md#grain-turns-appear-stuck).

## Storage consistency failures

**Signals:** <xref:Orleans.Storage.InconsistentStateException>, an ETag/version/precondition conflict, or repeated failure to write grain state.

**Likely causes:** concurrent writers, duplicate activations during a membership or custom-directory failure, application code reusing stale state, or an out-of-band writer modifying the same record.

**Confirm:** preserve the stored and expected version values, grain type/key, provider operation, and membership timeline. Determine whether more than one activation or an external process wrote the record. Don't log grain state or secrets.

**Remedy:** stop unsafe writers and reconcile the record according to application semantics. Don't retry a deterministic concurrency conflict indefinitely or overwrite newer data blindly.

**Prevent:** keep one authoritative writer per state record, use provider concurrency tokens, avoid out-of-band updates, and test recovery from ambiguous writes and activation churn.

## Storage throttling

**Signals:** storage error instruments rise, provider exceptions report throttling or rate limits, storage spans lengthen, and request timeouts follow.

**Likely causes:** provisioned throughput is too low; partition keys are skewed; connection pools are exhausted; retries amplify load; or activation/recovery waves create a burst.

**Confirm:** correlate provider request units, partition throttles, latency, and retry volume with Orleans storage operations and activation count. Compare affected grain keys by an approved low-cardinality partition classification rather than adding grain keys to metrics.

**Remedy:** reduce admission and retry amplification, increase or redistribute backend capacity, and relieve hot partitions. Use only the provider's documented transient-failure policy with a bounded retry budget.

**Prevent:** capacity-test storage with realistic state sizes and key distributions, budget retries, alert on latency before throttling propagates into call timeouts, and stagger recovery work.

## Serialization failures after a version change

**Signals:** an Orleans serialization/codec exception, "codec not found", "serializer not found", unsupported wire type, unknown field/type, or activation/storage reads begin failing after deployment.

**Likely causes:** mixed versions don't share a compatible contract; a serializable type or member identity changed; member IDs were reused or renumbered; a type was renamed without a stable alias; required serializer registration differs; or persisted/queued data contains an old representation.

**Confirm:** capture the full exception and type names, identify whether the payload came from a call, storage, stream, reminder, or queue, and compare old/new generated serialization metadata and package versions. Reproduce by reading representative old data with the new version before changing production data.

**Remedy:** roll back to a compatible version or deploy a reader which understands both representations, then migrate data deliberately. Don't delete unreadable state or queues until recovery and retention requirements are understood.

**Prevent:** keep stable `[Id(n)]` values and type aliases, test rolling upgrades with both version directions, and include persisted and queued payloads in compatibility tests. See [Deployment and rollback](../../migration/deployment-and-rollback.md).

## Membership and directory churn

**Signals:** silos repeatedly leave `Active`, missed pings, membership warnings, transient duplicate activations, repeated address invalidation, or calls bounce between old and new activation addresses.

**Likely causes:** process or network pauses, CPU/GC starvation, membership-store latency or throttling, unstable advertised addresses, clock problems, an aggressive rollout, a network partition, or custom grain-directory consistency/cleanup behavior.

**Confirm:** compare membership views and timestamps from all silos with platform events, provider latency, pings, and advertised endpoints. For suspected split brain, determine whether isolated silos remained alive and could reach the membership store; don't infer a partition from one stale log line.

**Remedy:** stop rollout/scale-in, preserve the larger healthy partition, restore membership-store and network health, and allow convergence. Isolate a stale partition before changing membership records. For custom directories, follow their documented repair procedure.

**Prevent:** use a shared, durable membership provider; keep endpoints stable and mutually reachable; maintain clock synchronization and failure-domain capacity; and test partitions and rolling replacement. Follow [Membership is unstable](troubleshooting.md#membership-is-unstable) and [Disaster recovery](../../deployment/disaster-recovery.md).

## Hot grains and skewed placement

**Signals:** one grain type/key has high queue or latency while other silos have spare capacity; a small set of silos has disproportionate CPU or activations; or adding silos doesn't improve throughput.

**Likely causes:** one consistency boundary receives most traffic, fan-in targets one aggregator, keys distribute unevenly, resource-constrained placement has too few eligible silos, or placement optimizes locality but concentrates work.

**Confirm:** compare latency and throughput by bounded grain type/operation dimensions, inspect representative traces and dashboard method profiling, and analyze the application key distribution securely. Distinguish a hot activation from a generally expensive grain type.

**Remedy:** partition the domain workload across keys, use hierarchical aggregation or batching, use stateless workers for suitable stateless work, or relax an unnecessarily restrictive placement rule. Moving one ordinary stateful activation doesn't add concurrency to that key.

**Prevent:** load test skewed and celebrity-key traffic, define per-key limits, and monitor placement/activation distribution without exporting raw grain keys. See [How do I avoid hot grains?](../../resources/frequently-asked-questions.md#how-do-i-avoid-hot-grains).

## Reminder and timer timing

**Signals:** a callback runs late, callbacks bunch after a pause, a reminder appears to run more than once, or callbacks stop after deactivation/restart.

**Likely causes:** timer callbacks were expected to survive activation loss; a reminder provider or membership dependency is unavailable; scheduler/CPU pressure delays execution; callback duration exceeds the period; or application code assumes exactly-once delivery.

**Confirm:** first determine whether the application uses a grain timer or a reminder. Correlate callback timestamps with activation lifetime, silo restarts, scheduler delay, membership, reminder-provider health, and callback duration. Use a durable application operation ID to detect repeated work.

**Remedy:** use reminders for work which must survive activation loss, restore provider health, reduce scheduler pressure, and make callbacks idempotent. Reconcile missed business work from durable application state rather than relying only on callback count.

**Prevent:** choose timers only for activation-scoped scheduling, make reminder work idempotent, monitor provider health and scheduling delay, and don't use either mechanism as a precision real-time clock.

## Memory pressure

**Signals:** low `orleans-runtime-available-memory`, growing working set or GC heap, long GC pauses, activation churn, or container OOM kills.

**Likely causes:** unbounded grain state or caches, stream buffers, large request payloads, telemetry cardinality, activation growth, allocation-heavy code, or a container limit below the assumed host capacity.

**Confirm:** correlate process/container limits, working set, GC heap and allocation rate, activation working set, and stream cache metrics. Capture a dump or GC trace before restart when safe and inspect retention paths.

**Remedy:** reduce load, bound the responsible data structure or payload, and scale out when work partitions. Raising a limit without identifying retained growth postpones another failure.

**Prevent:** set explicit memory budgets, bound caches/buffers/state, load test steady state and recovery, and alert against the actual container limit. Follow [Memory pressure rises](troubleshooting.md#memory-pressure-rises).

## Shutdown does not complete

**Signals:** the orchestrator kills the process after its grace period, lifecycle logs stop during shutdown, or membership remains `ShuttingDown`/`Stopping`.

**Likely causes:** the platform budget is shorter than host shutdown; new work continues arriving; a lifecycle participant ignores cancellation; grain/dependency calls block; or stream/provider cleanup exceeds the deadline.

**Confirm:** compare application and platform deadlines, inspect final lifecycle logs and traces, and capture stacks before forced termination. Identify the lifecycle stage or hosted service which didn't finish.

**Remedy:** stop admission before silo shutdown, make the blocking participant bounded and cancellation-aware, and leave platform time after the host deadline for process cleanup.

**Prevent:** test shutdown under load and dependency failure, preserve rollout surge capacity, and monitor forced terminations. Follow [Shutdown doesn't complete](troubleshooting.md#shutdown-doesnt-complete).
