---
title: Platform requirements
description: Evaluate whether a hosting platform can run an Orleans production cluster safely.
ms.date: 08/15/2026
ms.topic: concept-article
---

# Platform requirements

Orleans can run on Kubernetes, managed container platforms, virtual machines, and bare-metal hosts. The platform must support Orleans's process and network model; a platform name alone doesn't make a deployment production-ready.

## Required capabilities

A production platform must provide:

- Stable per-instance addresses or explicit advertised endpoint mapping.
- Direct bidirectional TCP connectivity from every silo to every silo endpoint.
- Reachability from Orleans clients to advertised gateway endpoints.
- Multiple concurrently running instances across failure domains.
- Graceful termination notification and a configurable termination deadline.
- Startup, readiness, and liveness checks with separate semantics.
- Secure access to a supported clustering provider and all state providers.
- Workload identity or secure secret and certificate delivery.
- Central logs, metrics, traces, and deployment metadata.
- Resource requests, limits, scaling controls, and disruption policies.

An HTTP-only platform isn't sufficient unless Orleans silos can also establish direct TCP connections to individual instances. A load-balanced HTTP endpoint doesn't replace silo-to-silo connectivity.

## Platform guidance

- [Multi-host container deployments](containers.md) require either direct per-container private addresses or unique, peer-reachable host-port mappings for every silo.
- [Kubernetes](kubernetes.md) provides direct pod networking. Explicit endpoint configuration is recommended; the Orleans hosting package is optional and limited to simple one-`Deployment`-per-cluster topologies.
- [Service Fabric](service-fabric.md) uses an application-authored stateless Reliable Service integration with runtime-allocated endpoints and an external Orleans clustering provider.
- Azure App Service requires validation of private per-instance address and port mapping on [Windows](deploy-to-azure-app-service.md) and [Linux](deploy-to-azure-app-service-linux.md).
- [Azure Container Apps](deploy-to-azure-container-apps.md) can map unique TCP port pairs on an internal environment's private IP to one replica per silo app; scaling multiple silos as replicas of one app relies on per-replica networking that the platform doesn't publish as a supported contract.

For another platform, prove the required capabilities with at least three silos under rolling replacement, scale-in, host restart, and network interruption. Record the exact endpoint mapping and shutdown behavior in the deployment runbook.

## Shared responsibility

Orleans provides membership, grain activation, placement, and call routing. The platform and application remain responsible for:

- Durable state and provider availability.
- Correct health endpoints and degraded-mode policy.
- Network security and transport encryption.
- Compatible releases and state migrations.
- Capacity, overload control, backup, restore, and incident response.
