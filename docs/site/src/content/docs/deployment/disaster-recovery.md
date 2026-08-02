---
title: Backup, restore, and disaster recovery
description: Protect Orleans 10 application state and recover a cluster after regional or provider failure.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Backup, restore, and disaster recovery

An Orleans cluster contains several kinds of data with different recovery requirements. Inventory each provider and assign recovery point and recovery time objectives.

## Membership isn't grain state

The clustering provider stores ephemeral coordination records describing silos and gateways. It doesn't contain live grain activations and usually isn't the source of truth for application state.

| Data | Typical durability | Recovery concern |
| --- | --- | --- |
| Cluster membership | Ephemeral | A new cluster can recreate membership; stale live rows can delay or block startup |
| Grain state | Application-defined durable data | Back up, restore, and validate according to business requirements |
| Reminders | Durable scheduling metadata | Preserve when scheduled work must survive cluster loss |
| Stream provider state | Provider-specific | Protect checkpoints, subscriptions, and queued events as required |
| External databases and side effects | Application-defined | Coordinate consistency and deduplication with grain state |
| Telemetry and audit data | Operational or compliance data | Retain independently from the Orleans cluster |

Don't treat deleting a membership table as a grain-state restore. Don't restore membership rows from backup as a substitute for starting a new cluster.

## Backup design

- Use provider-native, consistent backup or replication features.
- Encrypt backups and restrict restore permissions.
- Record the service ID, provider configuration, schema version, application version, and backup timestamp.
- Coordinate backups across stores when correctness spans grain state and external systems.
- Retain deduplication or operation records for at least the maximum retry and replay window.
- Test that reminders, streams, and secondary indexes recover with grain state where required.

## Restore procedure

Document and rehearse these steps:

1. Stop or fence writers to the affected state.
1. Choose a restore point and identify expected data loss.
1. Restore each durable provider into an isolated recovery environment.
1. Use a new cluster ID so recovery silos don't join a surviving cluster accidentally.
1. Start a small canary cluster and validate provider access, state deserialization, reminders, streams, and application invariants.
1. Reconcile incomplete external side effects using operation IDs or business records.
1. Add capacity, switch traffic deliberately, and monitor errors and dependency load.
1. Preserve the failed environment until investigation and rollback decisions are complete.

If a restored clustering provider includes stale active members, follow the provider's Orleans membership cleanup procedure or use a fresh membership namespace. Never let recovery automation delete records from a cluster that might still be alive across a network partition.

## Regional recovery

Active-passive is simpler than active-active for stateful grains. A standby region should use an isolated cluster ID and must not process the same mutable grain keys unless the storage and application protocols explicitly support multi-writer operation.

Validate:

- Provider replication lag and consistency.
- DNS or ingress cutover time.
- Credential, certificate, and configuration availability.
- Capacity in the recovery region.
- The behavior of outstanding requests whose outcome is unknown.
- Failback after the primary region returns.

Run disaster recovery exercises. A backup that hasn't been restored and validated doesn't establish a recovery capability.
