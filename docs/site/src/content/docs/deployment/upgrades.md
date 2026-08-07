---
title: Graceful shutdown and upgrades
description: Scale in and deploy Orleans applications using graceful, rolling, or blue-green strategies.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Graceful shutdown and upgrades

Orleans tolerates abrupt process loss, but a graceful shutdown reduces failed calls and avoids unnecessary recovery work. Correctness must never depend on graceful shutdown because a process, host, or network can fail without warning.

## Graceful shutdown and scale-in

For each instance being removed:

1. Stop admitting new application traffic and report not ready.
1. Stop application-specific background work from accepting new items.
1. Ask the .NET host to stop and await <xref:Microsoft.Extensions.Hosting.IHost.StopAsync*?displayProperty=nameWithType> or normal host termination.
1. Allow Orleans to leave cluster membership and deactivate or transfer runtime responsibilities.
1. Terminate the process only after the shutdown deadline expires.

The orchestrator termination grace period must exceed the .NET host shutdown timeout. Measure the actual duration under load and leave margin for provider latency.

Scale in gradually. Remove one failure domain at a time and wait until membership stabilizes, remaining silos absorb activations, and latency returns to normal. Don't reduce the cluster below its tested redundancy or capacity floor.

See [Shut down Orleans](../host/configuration-guide/shutting-down-orleans.md) for host APIs.

## Rolling upgrades

Use a rolling upgrade when old and new versions can safely share:

- Grain interfaces and serialized payloads.
- Persisted grain state.
- Reminder, stream, and provider schemas.
- External side effects and deduplication records.
- Cluster configuration and transport settings.

Before rollout:

1. Upgrade clients and silos in an order compatible with both versions.
1. Use additive contract changes first; don't reuse or renumber serializer field IDs.
1. Make storage migrations backward-compatible and independently reversible.
1. Verify the minimum ready-silo count and surge capacity.
1. Test rollback while both versions and both state formats exist.

Replace a bounded number of silos at a time. Pause when error rate, latency, membership churn, dependency load, or activation failures exceed the rollout threshold.

Orleans grain versioning can route calls among compatible grain implementations, but it doesn't make arbitrary application or storage changes compatible. See [Deploy new versions of grains](../grains/grain-versioning/deploying-new-versions-of-grains.md) and [Backward compatibility guidelines](../grains/grain-versioning/backward-compatibility-guidelines.md).

## Blue-green upgrades

Use blue-green deployment when versions can't safely coexist in one Orleans cluster.

- Give blue and green distinct `ClusterId` values.
- Decide whether they can share grain storage. Sharing is safe only when both versions can read and write the same records without concurrent ownership or incompatible side effects.
- Route external traffic at the application ingress, not by placing a load balancer in front of silo endpoints.
- Keep the previous environment available until state compatibility and rollback constraints permit removal.

For stateful cutovers, use one of these explicit strategies:

- Quiesce writes, migrate state, validate, then switch traffic.
- Dual-write through an application-owned migration protocol with reconciliation.
- Use separate state stores and perform an offline transfer.

Never point two incompatible active clusters at the same mutable grain state and assume Orleans membership will coordinate them. Membership is scoped by cluster ID; it doesn't provide cross-cluster ownership.

## Rollback

A rollback plan must cover code, configuration, schema, credentials, and persisted state. If the new version has written data the old version can't read, reverting only the image isn't a rollback.

Stop a rollout when safety thresholds are exceeded. Preserve logs and traces from failed instances, restore capacity with a known-compatible version, and avoid repeated automated retries that churn membership.
