---
title: Troubleshoot Orleans incidents
description: Operational runbooks for common Orleans connection, timeout, membership, storage, overload, memory, and shutdown incidents.
ms.date: 08/02/2026
ms.topic: troubleshooting
---

# Troubleshoot Orleans incidents

Use these runbooks as investigation sequences, not as automatic proof of one root cause. Capture the incident window, deployment changes, cluster topology, and traffic level before changing configuration. Prefer a rollback or traffic reduction when user impact is growing.

## Client can't connect

**Evidence:** no connected gateways, repeated connection failures, <xref:Orleans.Runtime.Messaging.ConnectionFailedException>, or a dashboard/client reporting lost cluster connectivity.

1. Confirm the client and silos use the same cluster and service IDs and the same clustering provider.
2. Query the membership/gateway source from the client's network boundary. Confirm it returns active silos with reachable advertised gateway addresses.
3. Test DNS and TCP reachability to those addresses. Check firewalls, network policy, NAT, and load balancers.
4. If TLS is enabled, inspect handshake errors, certificate validity, SAN/`TargetHost`, trust chain, EKU, and whether the silo unexpectedly requires a client certificate.
5. Compare all silo logs. One reachable gateway can mask a partial routing problem.

Don't add retries around startup indefinitely. Bound startup/reconnect behavior and expose readiness as false while the client has no usable gateway.

## Grain calls time out

**Evidence:** increased `orleans-app-requests-timedout`, high request latency, or <xref:System.TimeoutException> at callers.

1. Follow a sampled trace from the caller. Determine whether the call reached a silo and whether it waited before grain execution, inside the grain, or in a dependency.
2. Compare timeout growth with rejected/dropped messages, gateway connectivity, long-running turns, CPU, thread-pool starvation, GC pauses, and storage latency.
3. Check whether the timeout started with a deployment, traffic increase, membership change, or dependency degradation.
4. Inspect the target grain type and operation. Look for blocking calls, lock contention, synchronous I/O, fan-out, or calls which don't honor cancellation.
5. Increase a timeout only when the operation's valid latency exceeds the existing budget. A larger timeout doesn't fix overload or a blocked turn and can increase queued work.

## Membership is unstable

**Evidence:** silos repeatedly transition out of `Active`, ping replies are missed, or membership warnings occur across the cluster.

1. Compare each silo's membership view and timestamps. Account for an intentional rollout before treating changes as failures.
2. Check host pauses, CPU starvation, GC pauses, packet loss, DNS changes, and clock synchronization.
3. Verify membership-store latency, availability, credentials, and throttling from every silo.
4. Confirm advertised silo addresses are stable and mutually reachable; don't advertise loopback or an ephemeral address across hosts.
5. Avoid loosening liveness settings until infrastructure stalls and store latency are understood. Longer detection can reduce false positives but also delays failure recovery.

## Grain turns appear stuck

**Evidence:** `orleans-scheduler-long-running-turns` increases, one grain's queue grows, or traces show long application execution.

1. Identify the grain type and method from traces, dashboard profiling, and logs.
2. Capture a process trace or dump while the issue is active. Inspect managed stacks, CPU consumers, thread-pool queues, and monitor contention.
3. Look for synchronous blocking (`.Result`, `.Wait()`), unbounded loops, CPU-heavy work, external calls without timeouts, and non-reentrant grains waiting through cyclic calls.
4. Reduce incoming load or isolate the affected operation before restarting. A restart clears evidence and may only move the blockage.
5. Move blocking/CPU-heavy work out of grain turns or split it into bounded asynchronous steps.

## Storage operations fail

**Evidence:** Orleans storage error instruments increase, storage spans fail, activation fails, or provider exceptions appear.

1. Separate reads, writes, and clears and identify the storage provider and grain type.
2. Check backend health, throttling, authentication, DNS/TLS, connection pools, and latency from the affected silo.
3. Determine whether failures are transient, concurrency/ETag conflicts, serialization failures, or deterministic data/schema problems.
4. Compare affected silos and partitions. A single host suggests local connectivity or credentials; a shared partition suggests backend hot-spotting.
5. Don't retry deterministic serialization or concurrency failures indefinitely. Use bounded retry for provider-documented transient failures and preserve idempotency.

## The cluster is overloaded

**Evidence:** rejected messages, gateway load shedding, rising queue/latency, high CPU, or falling throughput while demand rises.

1. Verify whether pressure is at gateways, grain execution, storage, streams, or another dependency.
2. Compare request rate, completion rate, latency, rejections, activation count, CPU, thread pool, and GC across silos.
3. Reduce admission or shed optional work. Retrying rejected work immediately amplifies overload; use bounded exponential backoff with jitter at an appropriate boundary.
4. Scale only if work partitions across additional silos and the bottleneck isn't a shared dependency or hot grain.
5. After recovery, set capacity alerts below the rejection point and add load tests for the triggering traffic shape.

## Memory pressure rises

**Evidence:** low `orleans-runtime-available-memory`, growing working set/GC heap, long GC pauses, container OOM kills, or activation collection churn.

1. Compare process working set, GC heap size, allocation rate, collection count/pause time, Orleans activation working set, and stream cache metrics.
2. Check the real container or host memory limit. Physical-host availability can mislead a containerized process.
3. Capture a dump or GC trace before restart if safe. Group retained objects by type and retention path.
4. Check unbounded grain state, caches, stream buffers, request payloads, telemetry cardinality, and activation growth.
5. Lower load or scale out to stabilize. Don't simply raise limits without identifying whether memory is intentionally bounded.

## Shutdown doesn't complete

**Evidence:** the orchestrator kills silos after the grace period, membership remains `ShuttingDown`/`Stopping`, or logs report that graceful shutdown was aborted.

1. Compare the host shutdown timeout with the orchestrator termination grace period. Leave time for Orleans and other hosted services to stop before forced termination.
2. Stop accepting new external work before stopping the silo.
3. Inspect the final lifecycle logs and traces for long-running grain calls, deactivation, membership-store updates, stream shutdown, or blocked hosted services.
4. Ensure <xref:Orleans.ILifecycleObserver.OnStop*> honors cancellation and custom lifecycle participants complete promptly.
5. Treat repeated forced termination as an availability risk. Fix the blocking component rather than relying on process kill.

## Preserve diagnostic evidence

For intermittent or production-only failures, retain:

- UTC timestamps and deployment/version identifiers.
- Logs from all involved clients and silos.
- Metrics covering before, during, and after the event.
- Representative trace IDs and unsampled request counts.
- Membership snapshots and provider health.
- A process trace or dump when scheduler, CPU, or memory behavior is involved.

Redact secrets and grain state before sharing artifacts. See [.NET diagnostics](https://learn.microsoft.com/dotnet/core/diagnostics/) for `dotnet-counters`, `dotnet-trace`, and dump collection guidance.
