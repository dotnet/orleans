---
title: Deploy and operate Orleans
description: Plan, deploy, and operate an Orleans 10 application in production.
ms.date: 08/02/2026
ms.topic: overview
---

# Deploy and operate Orleans

An Orleans production deployment is a cluster of silo processes, optionally with separate Orleans clients. Silos communicate directly with each other over TCP. Clients discover gateways through the configured clustering provider and connect to those gateways.

Orleans manages grain activation and cluster membership, but the hosting platform remains responsible for process supervision, networking, health probes, secrets, resource allocation, and controlled rollout. A production design also needs durable grain state where the application requires it.

## Operations track

Use these articles together:

1. [Production-readiness checklist](production-readiness.md) - Review the decisions required before launch.
1. [Topology and networking](networking.md) - Configure listening and advertised endpoints, firewalls, and clustering.
1. [Health and observability](health-and-observability.md) - Design startup, readiness, liveness, dependency health, telemetry, and alerts.
1. [Graceful shutdown and upgrades](upgrades.md) - Drain instances, scale in, and perform rolling or blue-green releases.
1. [Capacity planning and scaling](capacity-planning.md) - Size silos, set resource policies, and validate scaling behavior.
1. [Backup, restore, and disaster recovery](disaster-recovery.md) - Protect application state and recover clusters safely.
1. [Failure handling](handling-failures.md) - Design grain calls for unknown outcomes, idempotency, and bounded retries.
1. [Troubleshoot deployments](troubleshooting-deployments.md) - Triage incidents using membership, networking, dependencies, and telemetry.

## Choose a platform

- [Kubernetes](kubernetes.md)
- [Service Fabric](service-fabric.md)
- [Azure App Service](deploy-to-azure-app-service.md)
- [Azure Container Apps](deploy-to-azure-container-apps.md)
- Other orchestrators, virtual machines, or bare-metal hosts that satisfy the [platform requirements](platform-guides.md)

For application configuration, see [Server configuration](../host/configuration-guide/server-configuration.md), [Client configuration](../host/configuration-guide/client-configuration.md), and [Typical configurations](../host/configuration-guide/typical-configurations.md).

> [!IMPORTANT]
> Localhost clustering and in-memory grain storage are development defaults. They don't provide a multi-host production cluster or durable application state.
