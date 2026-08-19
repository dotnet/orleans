---
title: Operate a production cluster
description: Manage configuration, routine maintenance, releases, and incidents for a production Orleans cluster.
ms.date: 08/19/2026
ms.topic: how-to
ms.custom: devops
---

# Operate a production cluster

Production operations keep an Orleans cluster within its tested capacity, compatibility, and recovery boundaries. Use this guide to establish ownership, control changes, perform routine maintenance, and create runbooks for each environment.

## Understand the operating boundaries

Each layer provides a distinct guarantee:

- The hosting platform starts, supervises, probes, scales, and terminates silo and client processes.
- The clustering provider records cluster membership and gateway information for processes that share a service ID and cluster ID.
- Orleans detects membership changes, places activations on available silos, and routes subsequent calls after a failed or departing silo.
- Durable providers preserve application state, reminders, stream state, and other application data according to their own consistency and availability guarantees.
- The application defines request idempotency, authorization, dependency-degradation behavior, data compatibility, and recovery objectives.

Record an owner and escalation path for the hosting platform, networking, clustering provider, grain storage, reminders, streams, secrets, telemetry, and application data. Provider service-level objectives and quotas form part of the Orleans service's operating envelope.

## Maintain an environment record

Keep a version-controlled record for each environment containing:

- Service ID, cluster ID, region, failure domains, and provider namespaces.
- Deployment artifact digest, Orleans package version, application version, and configuration revision.
- Silo and client resource requests, limits, replica floors, surge capacity, and scaling thresholds.
- Advertised silo and gateway endpoints, listening endpoints, DNS, firewall, and network-policy requirements.
- Clustering, storage, reminder, stream, and grain-directory providers with their availability tier and quotas.
- Credential and certificate sources, owners, rotation procedures, and expiry alerts.
- Dashboards, alert rules, recovery objectives, backup policy, and links to tested runbooks.

Export the effective configuration with secrets removed during deployment. Attach it to the deployment record so operators can compare intended values with the values used by each process. Include the configuration revision, application version, cluster ID, service ID, and silo name in logs and deployment metadata.

## Control configuration changes

Treat configuration as a versioned deployment input. Promote the same reviewed artifact through test and production environments while supplying environment-specific identity, endpoints, credentials, and capacity values through the hosting platform.

The .NET host assembles configuration before it starts Orleans. Orleans runtime services and providers consume their options during construction or startup, so replacing an instance is the consistent boundary for applying an Orleans configuration revision. Runtime reload is an option- or provider-specific contract; use it only when that component explicitly documents the resulting behavior. The deployment system coordinates the revision across the fleet, while each Orleans process validates and applies its local configuration as it starts. See the [Orleans configuration guide](../host/configuration-guide/index.md) for binding, precedence, and provider configuration.

Classify each change before rollout:

| Change | Compatibility requirement | Rollout approach |
| --- | --- | --- |
| Telemetry, alert, or resource threshold | Every instance can operate while old and new values coexist | Rolling change with saturation and error monitoring |
| Endpoint, clustering provider, service ID, or cluster ID | All participants in one cluster use a coherent identity and reachable advertised endpoints | Coordinated migration or a new cluster |
| Grain contract or serializer | Old and new callers and implementations exchange compatible payloads | Additive change followed by a rolling upgrade |
| Persisted-state or provider schema | Every running version can read data written during the rollout and rollback window | Expand, deploy, migrate, then contract |
| Credential or certificate | Providers and peers accept both old and new credentials during the overlap | Add, roll, verify, then revoke |
| Capacity or placement policy | The remaining cluster preserves redundancy and absorbs activation movement | Incremental change with a measured stabilization period |

## Roll out a configuration revision

1. Publish an immutable configuration revision with the application image, package versions, provider schema revision, and secret references that it requires.
1. Validate configuration and provider connectivity in a production-shaped environment. Exercise both the new revision and the previous revision against data written by each version when rollback remains available.
1. Confirm the ready-silo floor, surge capacity, shutdown budget, health gates, and rollback threshold.
1. Remove a bounded set of instances from application traffic and use [graceful shutdown](upgrades.md#graceful-shutdown-and-scale-in).
1. Start replacements with the new revision. Wait for startup and readiness to complete, active membership to stabilize, and the [service-level and dependency signals](health-and-observability.md#telemetry) to remain within the rollout gates.
1. Compare the effective redacted configuration with the intended revision, then continue through the remaining failure domains.
1. Keep the previous image and configuration revision available until every write, schema, credential, and queued-work compatibility condition for rollback has expired.

Return to the previous image and configuration revision when a rollout crosses its rollback threshold and the previous version can still read every payload and provider record written during the rollout. A change to cluster identity, advertised endpoints, provider namespace, or an incompatible data schema uses a coordinated migration or isolated blue-green cluster instead. Use [Upgrade deployment and rollback](../migration/deployment-and-rollback.md) for the compatibility boundary and recovery sequence.

## Perform routine maintenance

Use a maintenance window sized from measured shutdown, activation recovery, provider migration, and rollback times.

1. Confirm current cluster health, ready-silo count, dependency health, backup status, and available surge capacity.
1. Pause unrelated deployments and automated scale-in.
1. Capture the deployment and effective-configuration revisions.
1. Remove one silo or failure domain from application traffic and begin graceful host shutdown.
1. Wait for membership to stabilize and confirm that remaining silos absorb reactivations without exceeding latency, saturation, or error thresholds.
1. Apply the host, runtime, provider, credential, or infrastructure change.
1. Return the instance to service after startup and readiness complete.
1. Repeat within the tested disruption budget.
1. Record the result, observed recovery time, and any runbook corrections.

For provider maintenance, preserve the provider's documented quorum, availability, and backup guarantees. Schedule storage maintenance with enough Orleans capacity to absorb higher activation and state-load latency. Schedule clustering-provider maintenance with enough provider capacity for membership heartbeats, gateway discovery, and rollout churn.

## Run regular operational exercises

Exercise the behaviors that production recovery depends on:

- Replace one silo while representative traffic continues.
- Lose one tested failure domain and verify capacity headroom.
- Restore durable application data into an isolated recovery environment.
- Rotate credentials and certificates through their overlap period.
- Roll a compatible version forward and back while mixed versions run.
- Degrade each required dependency and verify admission, timeout, circuit-breaker, and alert behavior.
- Reconcile an operation whose caller observed a timeout and whose outcome is unknown.

Record recovery time, user impact, provider load, activation rate, and operator decisions. Feed observed limits back into [capacity planning](capacity-planning.md), [health and alerting](health-and-observability.md), and the [production-readiness checklist](production-readiness.md).

## Prepare runbooks

Each alert that requires operator action should link to a runbook containing:

1. The user-visible symptom and the signals that trigger the runbook.
1. The affected cluster, provider, deployment, and failure boundaries.
1. Safe stabilization actions and the conditions for stopping a rollout.
1. Commands or platform procedures with required permissions and expected results.
1. Evidence to preserve before replacement or restart.
1. Escalation criteria and the owner for each dependency.
1. Recovery verification, including membership stability, dependency health, latency, errors, and data reconciliation.

Start with runbooks for failed rollout, partial network partition, clustering-provider outage, storage degradation, overload, expired credentials, certificate rotation, backup restoration, and regional recovery. Use [Troubleshoot deployments](troubleshooting-deployments.md) for the incident workflow and evidence checklist.
