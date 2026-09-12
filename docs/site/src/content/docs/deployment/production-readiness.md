---
title: Production-readiness checklist
description: Review an Orleans deployment before it receives production traffic.
ms.date: 08/07/2026
ms.topic: checklist
---

# Production-readiness checklist

Complete this checklist for each production environment. Record owners, expected values, and links to runbooks instead of relying on implicit platform defaults.

## Identity and compatibility

- [ ] Every silo and client uses the intended Orleans package versions.
- [ ] <xref:Orleans.Configuration.ClusterOptions.ServiceId> is stable for the lifetime of the application and isn't reused by an unrelated application.
- [ ] <xref:Orleans.Configuration.ClusterOptions.ClusterId> identifies this deployment environment. Production, staging, and blue/green clusters use distinct values unless they are intentionally joining the same cluster.
- [ ] Grain interface, serializer, and persisted-state changes are compatible with the selected [upgrade strategy](upgrades.md).

## Networking and discovery

- [ ] Each silo advertises an address and silo port that every other silo can reach.
- [ ] Every external Orleans client can reach the advertised gateway addresses and ports.
- [ ] Listening endpoints bind to interfaces available inside the process; advertised endpoints describe how peers reach the process.
- [ ] Network policies, firewalls, service meshes, and network address translation preserve long-lived bidirectional TCP connectivity.
- [ ] Silos and clients use the same production [clustering provider and cluster identity](networking.md#choose-a-clustering-provider).
- [ ] The clustering provider is highly available, capacity-tested, and isolated appropriately between environments.

## State and dependencies

- [ ] Every grain storage, reminder, stream, and clustering provider is explicitly configured for production.
- [ ] Data that must survive activation or cluster loss uses durable grain storage. In-memory storage is used only for disposable data.
- [ ] Dependencies use workload identity or another short-lived credential mechanism where available. Secrets aren't embedded in images, source, or deployment manifests.
- [ ] Timeouts, concurrency limits, circuit breakers, and [bounded retry policies](handling-failures.md) prevent dependency failures from becoming retry storms.
- [ ] The behavior for a degraded or unavailable dependency is documented: fail closed, reject work, serve stale data, or buffer a bounded amount of work.

## Lifecycle and health

- [ ] Startup remains unready until the silo joins the cluster and required dependencies are usable.
- [ ] Readiness is removed before scale-in or shutdown begins.
- [ ] Liveness checks only detect a process that can't make local progress; they don't restart a process merely because a remote dependency is unavailable.
- [ ] The platform's termination grace period exceeds the host shutdown timeout and the observed time required for graceful silo shutdown.
- [ ] Rolling deployment settings preserve enough ready silos to maintain capacity.

## Security and access

- [ ] The [Orleans trust boundaries](../security/index.md) and application-owned security controls are documented.
- [ ] Only trusted workloads can reach silo and gateway ports.
- [ ] TLS protects silo-to-silo and client-to-gateway traffic, with platform chain, DNS-name, EKU, and revocation validation. See [Secure Orleans connections with TLS](../host/transport-layer-security.md).
- [ ] Workload authentication uses cluster-specific audiences, separate silo and client roles, explicit caller allowlists, and fail-closed enforcement. See [Authenticate Orleans connections](../host/authenticated-silo-connections.md).
- [ ] Every silo and external Orleans client admitted by these policies is trusted to access the cluster; untrusted users are authenticated and authorized at application ingress.
- [ ] Grain calls enforce [application authentication and authorization](../security/authentication-authorization.md), and validated credentials establish the identity carried through request context.
- [ ] Serializer type-name resolution follows the [least-privilege type policy](../security/serialization.md).
- [ ] Membership, storage, reminder, and stream providers independently use encrypted transport, workload identity, and least-privilege permissions.
- [ ] Configured providers and persisted data are treated as trusted cluster infrastructure, with administrative access restricted accordingly.
- [ ] Administrative endpoints, health details, metrics, and logs don't expose secrets or tenant data.
- [ ] Provider identities have least privilege for membership, state, reminders, and streams.
- [ ] Certificates and credentials have rotation and expiry alerts.
- [ ] Negative connection tests prove that wrong certificates, tenants, audiences, roles, caller IDs, and baseline-only peers are rejected.

## Observability and operations

- [ ] Logs, Orleans metrics, .NET runtime metrics, and traces are exported centrally. See [Orleans observability](../host/monitoring/index.md).
- [ ] Dashboards show ready silo count, membership changes, request latency and failures, rejected or dropped messages, activation count, CPU, memory, and dependency health.
- [ ] Alerts are based on user impact and sustained symptoms, not individual transient membership events.
- [ ] Operators can correlate a deployment version, silo name, cluster ID, service ID, and host instance across logs and telemetry.
- [ ] Named owners and runbooks follow the [production operations guide](production-operations.md) for failed rollout, partial network partition, provider outage, overload, data restore, and credential expiry.

## Capacity and recovery

- [ ] Load tests include expected traffic, bursts, hot grain keys, silo loss, and dependency slowdown.
- [ ] Scaling policies use measured saturation and include headroom for losing at least one failure domain.
- [ ] Durable application data is backed up and restore-tested.
- [ ] Recovery objectives distinguish membership data from grain state, reminder data, stream state, and application databases.
- [ ] A disaster recovery exercise has demonstrated the documented [restore procedure](disaster-recovery.md).

Before launch, run a controlled restart and a one-silo-at-a-time scale-in while production-like load continues. No correctness property should depend on graceful shutdown succeeding, because processes can always fail abruptly.
