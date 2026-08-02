---
title: Troubleshoot deployments
description: Triage Orleans 10 deployment and production incidents.
ms.date: 08/02/2026
ms.topic: troubleshooting
ms.custom: devops
---

# Troubleshoot deployments

Start with a timeline. Record the first user-visible symptom, deployment or configuration changes, membership events, dependency incidents, and platform actions. Preserve logs and traces before restarting every instance.

## Stabilize the service

1. Stop an active rollout or automated scale-in.
1. Preserve enough healthy capacity and failure-domain redundancy.
1. Shed optional load and disable unbounded retries.
1. Isolate a suspected bad version or dependency without deleting membership or state.
1. Capture service ID, cluster ID, version, silo names, advertised endpoints, and provider health.

Don't repeatedly restart all silos. Simultaneous restarts remove evidence, increase provider load, and can turn partial degradation into an outage.

## No silo can start

Check:

- Configuration parsing, credentials, certificates, and clock synchronization.
- Connectivity and authorization to the clustering provider.
- Whether service ID, cluster ID, and provider namespace match the intended environment.
- Stale membership records after a disaster or forced termination.
- Startup probe deadlines and platform kill events.
- Provider throttling or an unavailable quorum.

Use a fresh cluster ID or membership namespace for disaster recovery. Don't delete membership records until you've proved that no partitioned silo from that cluster remains alive.

## Silos start but don't form one cluster

Compare every silo's:

- Service ID and cluster ID.
- Clustering provider type, endpoint, database or table, and namespace.
- Advertised IP address and silo port.
- DNS results, network policy, firewall, and service-mesh behavior.
- Orleans package and application versions.

From each silo network, test TCP connectivity to every advertised silo endpoint. A process can listen successfully while advertising an address no peer can reach.

## Clients can't connect

Clients must use the same service ID, cluster ID, and clustering provider as the silos. Confirm that the provider returns active gateways and that the client network can reach every advertised gateway endpoint.

A web load balancer or Kubernetes service doesn't make individual gateway addresses reachable. Test the exact addresses stored in membership.

## Calls fail during membership changes

A <xref:Orleans.Runtime.SiloUnavailableException>, timeout, or connection failure can occur while a silo leaves or becomes unreachable. The grain reference remains usable, and Orleans can route later calls to a new activation.

The failed call has an **unknown outcome**: it might have run before the response was lost. Retry only when the operation is idempotent or deduplicated and the retry policy is bounded. See [Failure handling](handling-failures.md).

Frequent membership churn points to host restarts, liveness thresholds, resource exhaustion, network loss, provider latency, or incompatible rollout behavior. Correlate membership events with platform events and process telemetry.

## High latency or rejection rate

Inspect:

- CPU throttling, scheduler delay, garbage collection, memory pressure, and thread-pool starvation.
- Hot grain keys, long-running turns, blocking calls, and fan-out.
- Gateway load shedding, dropped or expired messages, and socket counts.
- Dependency latency, throttling, connection pools, circuit breakers, and retry volume.
- Activation growth and reactivation after a silo loss.

Add capacity only after verifying that the provider and downstream services can absorb it. Use [Capacity planning and scaling](capacity-planning.md) to avoid scaling a retry storm.

## Dependency degradation

Keep liveness healthy when a remote dependency is down. Use readiness only if the application can't safely serve any new work. Otherwise expose the dependency as degraded and preserve the documented reduced capability.

Confirm that timeouts and circuit breakers are working and that retry traffic is bounded. Check credential expiry, provider quotas, DNS, TLS validation, and regional incidents.

## Shutdown and rollout failures

If calls fail during planned replacement:

- Confirm readiness becomes false when shutdown starts.
- Compare the .NET host shutdown timeout with the platform termination grace period.
- Check whether the platform sends a graceful termination signal before killing the process.
- Reduce rollout concurrency and preserve surge capacity.
- Verify old and new contracts, serializers, storage schemas, and configuration are compatible.

See [Graceful shutdown and upgrades](upgrades.md).

## Missing or unsafe telemetry

Orleans logs through `Microsoft.Extensions.Logging` and publishes metrics and traces using .NET diagnostics APIs. See [Orleans observability](../host/monitoring/index.md).

Ensure telemetry includes cluster and deployment identity but doesn't record secrets or high-cardinality grain keys as metric dimensions. If telemetry disappears with the service, send it to an external collector and give shutdown flushing a bounded deadline.

## Kubernetes checks

Use:

```bash
kubectl get pods --namespace <namespace> --show-labels
kubectl describe pod --namespace <namespace> <pod-name>
kubectl logs --namespace <namespace> <pod-name> --previous
kubectl auth can-i list pods --namespace <namespace> --as system:serviceaccount:<namespace>:<service-account>
```

Check pod IP, identity labels, downward-API environment variables, service account, role binding, probe failures, exit reason, and resource throttling. See [Host Orleans on Kubernetes](kubernetes.md).

## Escalation evidence

Collect a bounded time window containing:

- Application, Orleans, platform, and provider logs.
- Metrics and traces around the first symptom.
- Membership snapshots and advertised endpoints.
- Deployment manifests and effective configuration with secrets removed.
- Runtime and application versions.
- A minimal sequence that reproduces the failure, if available.

State what is known, unknown, and inferred. In particular, don't describe a timed-out business operation as definitely failed unless the application has reconciled its outcome.
