---
title: Host Orleans on Azure Container Apps
description: Assess Azure Container Apps networking and lifecycle requirements for an Orleans 10 cluster.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Host Orleans on Azure Container Apps

The previous shopping-cart deployment tutorial has been withdrawn. Its sample, Azure AD B2C configuration, secret-based GitHub Actions workflow, Bicep templates, and runtime versions aren't a validated Orleans 10 production baseline.

Azure Container Apps revisions and HTTP ingress don't automatically provide the per-replica addressability Orleans requires. Before selecting this platform, validate direct TCP reachability to individual silo replicas and a stable way for each silo to advertise the address and ports that peers and Orleans clients can reach.

## Validate before production use

Prove all of these requirements in the target Container Apps environment:

- Every silo replica can establish long-lived bidirectional TCP connections to every other replica.
- Each replica can discover and advertise its own routable address and silo port.
- Orleans clients can discover and reach individual advertised gateway endpoints.
- Revision rollout, traffic splitting, scale-to-zero, scale-in, replica replacement, and environment maintenance preserve Orleans endpoint semantics.
- Minimum replicas remain greater than zero and maintain the tested availability and capacity floor.
- Termination notification and grace time allow a graceful .NET host and silo shutdown.
- A supported external clustering provider, durable state providers, workload identity, health endpoints, and telemetry are configured.

HTTP ingress can route application requests, but it doesn't replace Orleans silo or gateway connectivity. See [Topology, networking, and clustering](networking.md), [Platform requirements](platform-guides.md), and [Production-readiness checklist](production-readiness.md).

> [!WARNING]
> Don't copy the retired tutorial's GitHub Actions credentials, Bicep, ingress assumptions, or shopping-cart configuration into a production deployment.

## Sample modernization required

A future end-to-end Container Apps tutorial requires a maintained Orleans 10 sample that:

1. Uses current .NET, Orleans, and Azure Container Apps capabilities.
1. Demonstrates and tests per-replica advertised endpoint discovery.
1. Uses a supported clustering provider and durable grain storage.
1. Uses workload identity and OpenID Connect federation instead of long-lived deployment secrets.
1. Includes startup, readiness, liveness, graceful shutdown, and a nonzero replica floor.
1. Tests rolling revisions, blue-green isolation, traffic cutover, scale-in, and replica replacement.
1. Supplies current infrastructure as code and a pinned, least-privilege CI workflow.

Until those requirements are demonstrated by a maintained sample, this page is platform qualification guidance, not a deploy-by-copying tutorial.
