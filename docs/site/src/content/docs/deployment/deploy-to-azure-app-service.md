---
title: Host Orleans on Azure App Service
description: Assess Azure App Service networking and lifecycle requirements for an Orleans 10 cluster.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Host Orleans on Azure App Service

The previous shopping-cart deployment tutorial has been withdrawn. Its sample, secret-based GitHub Actions workflow, Bicep templates, and runtime versions aren't a validated Orleans 10 production baseline.

Azure App Service can host Orleans only when the selected plan and networking configuration provide routable, per-instance private addresses and ports for direct silo-to-silo TCP connections. Standard HTTP scale-out behavior isn't sufficient by itself.

## Validate before production use

Prove all of these requirements in the target App Service configuration:

- Each instance receives a private address and at least one mapped silo port, plus a gateway port when external Orleans clients connect.
- The application listens on local interfaces and advertises the platform-provided routable address and mapped ports. Listening and advertised endpoints might differ.
- Every instance can connect directly to every other instance's advertised silo endpoint.
- Orleans clients can discover and reach each advertised gateway endpoint.
- Scale-out, scale-in, host replacement, deployment slots, and platform maintenance don't reuse endpoint mappings in a way that routes to the wrong instance.
- Shutdown notification and grace time allow the .NET host and Orleans silo to stop.
- A supported external clustering provider, durable state providers, workload identity, health endpoints, and telemetry are configured.

See [Topology, networking, and clustering](networking.md), [Platform requirements](platform-guides.md), and [Production-readiness checklist](production-readiness.md).

> [!WARNING]
> Don't copy the retired tutorial's GitHub Actions credentials, Bicep, private-port assumptions, or shopping-cart configuration into a production deployment.

## Sample modernization required

A future end-to-end App Service tutorial requires a maintained Orleans 10 sample that:

1. Uses current .NET and Orleans packages.
1. Discovers App Service's current per-instance private address and port mappings and configures listening and advertised endpoints separately.
1. Uses a supported clustering provider and durable grain storage.
1. Uses OpenID Connect federation and managed identity instead of a long-lived service-principal secret.
1. Includes startup, readiness, and liveness behavior plus graceful shutdown.
1. Validates multi-instance rollout, slot swap, scale-in, endpoint reuse, and host replacement.
1. Supplies current infrastructure as code and a pinned, least-privilege CI workflow.

Until that sample is available and tested, this page is platform qualification guidance, not a deploy-by-copying tutorial.
