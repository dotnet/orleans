---
title: Perform a rolling Orleans upgrade
description: Evolve a grain interface and state safely, validate mixed versions, roll the cluster, and rehearse rollback.
ms.date: 08/11/2026
ms.topic: tutorial
---

# Perform a rolling Orleans upgrade

This walkthrough takes an application from version 1 to version 2 without stopping the cluster. It combines interface versioning, serializer compatibility, state evolution, deployment order, observation, and rollback.

Use a staging environment with the same clustering and storage providers as production. A localhost cluster can't validate membership propagation, readiness, draining, or provider behavior.

## Establish the version 1 baseline

Deploy at least two version 1 silos and a version 1 client. Select a grain identity and:

1. Call every operation which the upgrade changes.
1. Persist representative state, including optional and boundary values.
1. Record successful-call, failed-call, activation, serialization, and state-read metrics.
1. Save the exact version 1 artifacts and configuration needed for rollback.

Do not proceed until the baseline is repeatable.

## Make the contract additive

Create version 2 by retaining every version 1 method and adding new behavior through a new method. Apply `[Version(2)]` to the new interface. The attribute changes routing; it doesn't make an incompatible contract safe.

For serialized request, response, and state types:

- keep every existing `[Id]` value unchanged;
- assign new IDs to new fields;
- give new fields defaults which version 1 data can tolerate;
- keep old result and exception types available; and
- don't change the meaning of an existing field.

If version 2 needs a new state representation, first deploy code which can read both representations while continuing to write the old one. Delay irreversible writes until rollback is no longer required.

See the [grain contract compatibility guidelines](../grains/grain-versioning/backward-compatibility-guidelines.md) for examples.

## Test mixed versions

Build a matrix instead of testing each version alone:

| Caller | Silo implementation | Expected result |
| --- | --- | --- |
| Version 1 | Version 1 | Existing operations and state remain unchanged. |
| Version 1 | Version 2 | Existing operations retain version 1 semantics. |
| Version 2 | Version 2 | New and existing operations succeed. |
| Version 2 | Version 1 | New calls are routed away from incompatible activations. |

Run the matrix against state first written by version 1 and state subsequently touched by version 2. Restart silos between cases to prove that compatibility doesn't depend on an existing activation.

## Configure compatible routing

The default rolling-upgrade policy uses `BackwardCompatible` compatibility with `AllCompatibleVersions` selection. Confirm that your application hasn't overridden these strategies. Use <xref:Orleans.Versions.Selector.LatestVersion> only when new activations should prefer the newest compatible implementation; it doesn't proactively replace existing compatible activations.

## Roll forward

1. Start one version 2 silo while all version 1 silos and callers remain active.
1. Wait until membership converges and readiness checks pass.
1. Send version 1 traffic through existing and new grain identities. Confirm that version 2 preserves old behavior.
1. Add enough version 2 capacity for the expected traffic.
1. Deploy version 2 callers and exercise the new operation.
1. Watch incompatible-request deactivations, placement failures, serialization failures, state-read failures, latency, and error rate.
1. Drain version 1 callers.
1. Gracefully stop one version 1 silo at a time, waiting for membership convergence and stable traffic after each removal.

Keep the version 1 deployment artifacts and backward-compatible state contract until the rollback window closes.

## Rehearse rollback

While both versions are present:

1. Stop version 2 callers so that no new-only requests enter the system.
1. Route traffic through version 1 callers.
1. Stop version 2 silos one at a time.
1. Confirm that version 1 silos can activate grains whose state was touched by version 2.
1. Compare behavior and telemetry with the version 1 baseline.

If version 1 can't read version 2 writes, stopping version 2 doesn't restore the service. Restore from a tested data backup or complete a forward fix according to the incident plan; don't repeatedly reactivate incompatible grains.

## Complete the migration

After the rollback window:

1. Remove version 1 callers and silos from deployment definitions.
1. Confirm through telemetry that the old method is unused.
1. Enable new state writes if they were delayed.
1. Keep old-state readers for the retention period needed to activate dormant grains.
1. Remove compatibility code only in a later, separately reversible deployment.

For strategy details, see [deploy new grain interface versions](../grains/grain-versioning/deploying-new-versions-of-grains.md) and [graceful shutdown and upgrades](../deployment/upgrades.md).
