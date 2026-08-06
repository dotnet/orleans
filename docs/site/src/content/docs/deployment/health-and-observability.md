---
title: Health and observability
description: Design Orleans startup, readiness, liveness, dependency health, telemetry, and alerts.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Health and observability

Health endpoints are application-owned. Orleans cluster membership detects failed silos, but membership isn't a substitute for orchestrator probes or dependency health checks.

## Separate the health signals

| Signal | Question | Failure action |
| --- | --- | --- |
| Startup | Has initialization completed within its allowed window? | Delay liveness and readiness evaluation; restart only after the startup deadline |
| Readiness | Should this instance receive new application traffic? | Remove it from traffic without restarting it |
| Liveness | Can this process still make local forward progress? | Restart the process |
| Dependency health | Which required or optional dependency is degraded? | Degrade, reject, or alert according to application policy |

Don't make liveness depend on the clustering provider, grain storage, a remote silo, or another network service. A shared dependency outage would cause every instance to restart simultaneously, adding load and erasing useful diagnostics.

## Startup

Startup should remain incomplete until:

- Configuration and credentials are valid.
- Required listeners are bound.
- The silo has joined the intended cluster.
- Dependencies required to safely accept traffic have passed initialization.

Set the platform startup deadline longer than the worst measured initialization time, including membership cleanup and provider recovery. A startup failure should log the stage, dependency, and exception before the platform restarts the process.

## Readiness

Readiness should become false when:

- The silo hasn't completed startup.
- Graceful shutdown or scale-in has started.
- The application can't safely accept new work.
- A required dependency is unavailable and the application has no safe degraded mode.
- Local saturation exceeds a deliberately chosen load-shedding threshold.

Keep optional dependencies out of aggregate readiness when the application can serve a documented degraded response. Publish their state separately and alert on sustained degradation.

Removing an HTTP endpoint from a load balancer doesn't stop direct Orleans traffic. During shutdown, combine readiness removal with graceful host shutdown and enough time for the silo to leave membership.

## Liveness

Use a cheap, local check. It can verify that the health endpoint is responsive and that a local progress signal has advanced within a conservative interval. Avoid grain calls and remote I/O.

Allow transient pauses caused by garbage collection, CPU contention, or diagnostic collection. Aggressive liveness thresholds turn temporary overload into a restart loop.

## Dependency degradation

Classify dependencies before deployment:

- **Required for correctness**: Reject new work when unavailable.
- **Required for durability**: Don't acknowledge operations that can't meet durability requirements.
- **Optional**: Continue with a visible degraded mode.
- **Asynchronous**: Buffer only within a bounded, monitored capacity.

Apply timeouts and concurrency limits at every remote boundary. Use circuit breakers to stop repeated calls to an unhealthy dependency. Retry only when the operation is safe to repeat and the caller still has time available.

## Telemetry

Orleans uses `Microsoft.Extensions.Logging`, `System.Diagnostics.Metrics`, and distributed tracing. Configure a central exporter and see [Orleans observability](../host/monitoring/index.md) for the runtime instruments.

At minimum, correlate these dimensions:

- Service ID, cluster ID, deployment version, silo name, and host instance.
- Grain type and operation name where cardinality remains bounded.
- Dependency name and outcome, without grain keys, secrets, or tenant data as metric dimensions.

Monitor:

- Ready and active silo count, joins, departures, and suspected silos.
- Grain call rate, latency, timeouts, rejections, dropped messages, and failures.
- Activation count, activation failures, scheduler delay, and long-running turns.
- Gateway connections and load shedding.
- Process CPU, allocation rate, garbage collection, working set, thread pool, and socket counts.
- Provider latency, throttling, errors, capacity, credential expiry, and circuit state.

Alert on sustained user impact, reduced redundancy, saturation, and exhausted error budgets. A single silo departure during a controlled rollout is an event to correlate, not necessarily an incident.
