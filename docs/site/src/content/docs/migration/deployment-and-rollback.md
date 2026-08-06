---
title: Upgrade deployment and rollback
description: Plan a safe Orleans major-version deployment with explicit rolling-upgrade and rollback prerequisites.
ms.date: 08/02/2026
ms.topic: how-to
---

# Upgrade deployment and rollback

## Compatibility boundary

Orleans 7 and later use a version-tolerant serializer, and Orleans grain versioning supports compatible application contract versions. However, the documented runtime guarantee for mixed Orleans versions is limited to one major-version family, such as Orleans 9.x patch and minor versions.

For a major-version upgrade, use a parallel or blue-green cluster by default. Treat a mixed-major rolling upgrade as unsupported until the exact runtime versions, providers, application contracts, and traffic patterns have passed your own qualification suite.

## Rolling-upgrade prerequisites

Before any rolling deployment, including an internally qualified cross-major deployment:

- All silos must use the same clustering provider, cluster identity, provider configuration, and network protocol configuration.
- Old and new binaries must share compatible grain interface method signatures. Add methods instead of changing or removing existing methods during the rollout.
- Serialized types must retain member IDs, aliases, and shapes that both versions can read.
- Provider schemas and stored payloads must be readable by both versions for the entire rollback window.
- Stream providers, queue partition counts, reminder providers, grain directories, and placement policies must support the mixed topology.
- Stateless workers and streaming agents must be considered separately because grain version selection doesn't isolate them.
- The deployment must have automated health gates, traffic draining, state backups, and a tested abort procedure.

Use the compatibility and selector strategies described in [Deploy new versions of grains](../grains/grain-versioning/deploying-new-versions-of-grains.md) for application contract changes. Those strategies don't establish runtime-major compatibility.

## Recommended parallel-cluster sequence

1. Build the target version from the same application contract revision used to validate old-state reads.
1. Create a new cluster with separate membership and gateway discovery from the production cluster.
1. Point it at cloned provider data or a staging data set and validate clustering, storage, reminders, streams, timers, filters, cancellation, and placement.
1. Start production with no write traffic and complete smoke tests.
1. Shift a small, observable portion of traffic.
1. Increase traffic only after latency, failure, activation, storage, reminder, stream, and cancellation metrics are stable.
1. Stop writes to the old cluster before any shared-state cutover that could allow concurrent activation or conflicting writes.
1. Keep the old deployment and recovery point until the rollback window expires.

Don't run two independent clusters against the same grain storage or reminder tables unless the provider and application have been designed for that topology.

## Rollback prerequisites

A rollback is safe only when all of the following are true:

- The previous runtime can read every payload and provider row written by the target runtime.
- Database changes are backward compatible or a tested database restore is available.
- No new grain interface method, serialized type, stream payload, or reminder state is required by queued work.
- The previous package set, configuration, secrets, and deployment image remain available.
- Traffic can be drained without allowing both clusters to own the same logical activations.

If any condition isn't met, rollback means restoring the full pre-upgrade recovery point, not only redeploying old binaries.

## Rollback sequence

1. Stop new traffic and writes to the target cluster.
1. Drain or account for in-flight calls and queued messages.
1. Restore provider data if the previous version can't read target-version writes.
1. Start the previous cluster in isolation.
1. Validate membership, reminders, streams, representative state, and client calls.
1. Restore traffic gradually and retain target-cluster diagnostics for investigation.

## Deployment checklist

- [ ] Identify whether the change stays within one Orleans major family.
- [ ] Verify grain interface and serializer compatibility in both directions.
- [ ] Verify provider schema and payload compatibility in both directions.
- [ ] Back up clustering, reminders, streams, and grain state where applicable.
- [ ] Test old-state reads and rollback reads.
- [ ] Rehearse traffic draining, cutover, and rollback.
- [ ] Prevent two independent clusters from concurrently owning the same logical work.
- [ ] Define objective health gates and a rollback deadline.
