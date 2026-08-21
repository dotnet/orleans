---
title: Choose a deployment target
description: Compare supported Orleans hosting topologies, networking models, scaling controls, and operational responsibilities.
ms.date: 08/21/2026
ms.topic: concept-article
ms.custom: devops
---

# Choose a deployment target

Orleans runs on platforms that give each silo a unique, directly reachable TCP endpoint and coordinate process lifecycle with the .NET host. Choose the platform whose supported networking model, scaling controls, failure-domain placement, and operational ownership match the application.

Use the [platform requirements](platform-guides.md) as an acceptance gate against the application's availability objective. Then use these matrices to select a maintained deployment guide.

## Target matrix

| Target | Supported Orleans topology | Endpoint identity and client reachability | Scaling and failure domains | Operational ownership | Common provider choices |
| --- | --- | --- | --- | --- | --- |
| [Azure Kubernetes Service (AKS)](azure-kubernetes-service.md) | One silo per pod in a `Deployment`; separate in-cluster clients or cohosted application endpoints | Silos advertise pod IPs. In-cluster clients reach pod gateway endpoints directly. Azure CNI Pod Subnet extends direct pod routing to connected networks when external Orleans clients require it. | Horizontal pod scaling, node-pool autoscaling, availability zones, topology spread, disruption budgets, and controlled rolling replacement | Azure operates the control plane; the application team owns workload manifests, capacity, providers, upgrades, and recovery | Azure Table Storage or ADO.NET for clustering; application-selected durable storage, reminders, and streams |
| [Kubernetes](kubernetes.md) | One silo per pod with explicit pod name and pod IP configuration | Silos advertise pod IPs. Clients run on a network that can route to every advertised gateway pod IP. | Deployment replicas, cluster-specific node scaling, topology spread, disruption budgets, and rolling updates | Shared between the Kubernetes operator and application team | A highly available provider already operated near the cluster |
| [Azure Container Apps](deploy-to-azure-container-apps.md) | One silo replica per Container App, with multiple silo apps in one internal environment | Every silo app receives a unique TCP silo and gateway port pair on the environment's private static IP | Scale by adding or removing one-replica silo apps. Replacement apps provide controlled rollout. This topology provides app-resource and process redundancy without a documented cross-zone placement guarantee. Use AKS when zonal placement is part of the availability objective. | Azure operates hosts and the environment; the application team owns the bounded app topology, port allocation, and replacement workflow | Azure Table Storage for clustering and optional grain state; other Azure data services where supported by the application |
| [Azure App Service](azure-app-service.md) | One cohosted silo per App Service worker | Each worker advertises `WEBSITE_PRIVATE_IP` and its allocated private port. Trusted application clients use validated private gateway mappings when enabled. | App Service distributes multiple workers across fault domains. Supported Premium plans can enable zone redundancy. Deployment slots provide compatible release rollout. | Azure operates workers and the front end; the application team validates private endpoint behavior, slot compatibility, zone configuration, and capacity | Azure Table Storage for clustering and grain state in the maintained sample |
| [Service Fabric](service-fabric.md) | One silo per unpartitioned stateless Reliable Service instance | Each instance advertises its node address and runtime-allocated silo and gateway ports | Stateless instance count, fault domains, update domains, monitored rolling upgrades, and application health policies | Service Fabric manages placement and lifecycle; the application owns the Reliable Service integration and Orleans providers | Azure Table Storage in the compiled example; another supported external provider when appropriate |

## Compare lifecycle and release mechanisms

| Target | Health and termination mechanism | Upgrade and rollback mechanism |
| --- | --- | --- |
| AKS | Application-owned startup, readiness, and liveness probes; Kubernetes pod termination grace period; PDB-governed voluntary eviction | Deployment rolling update plus node-pool surge and drain controls; isolated AKS cluster and Orleans cluster ID for incompatible changes |
| Kubernetes | Application-owned probes, orchestrator termination grace period, and disruption policy | Deployment or controller rollout with bounded unavailable and surge capacity; separate cluster identity for incompatible changes |
| Azure Container Apps | Explicit startup, readiness, and liveness probes plus container termination grace period | Replacement one-replica silo apps with new port pairs; isolated app set and cluster ID for incompatible changes |
| Azure App Service | App Service Health check, startup and slot warm-up, application readiness, and bounded host shutdown | Staging-slot warm-up and swap for compatible changes; separate app and cluster ID for incompatible changes |
| Service Fabric | Communication-listener lifecycle plus application-authored Service Fabric health reports and close timeout | Monitored rolling upgrade by update domain; separately named application and cluster ID for incompatible changes |

## Qualify another platform

The maintained target guides above provide complete platform-specific operations guidance. Use these supporting guides to qualify another hosting system:

| Platform shape | Networking guidance | Qualification path |
| --- | --- | --- |
| Container orchestrator or service | [Run containers across multiple hosts](containers.md) | Prove direct per-container addressing or unique host-port mapping, then complete every [platform requirement](platform-guides.md) for the selected orchestrator. |
| Virtual machines or bare-metal hosts | Stable private host addresses and unique silo and gateway ports | Complete the [platform requirements](platform-guides.md) with the infrastructure team's process supervision, failure-domain placement, patching, scaling, and replacement automation. |

## Choose the networking model first

Orleans membership identifies a specific silo endpoint. The selected platform therefore needs one of these models:

- **Direct workload addressing** gives each pod, task, container, or process a private address which every silo can route to. Kubernetes with routable pod networking and Amazon ECS with `awsvpc` task networking use this model.
- **Explicit endpoint mapping** gives each silo a private host or platform address plus unique published silo and gateway ports. Azure Container Apps, Azure App Service, and host-port container deployments use this model.
- **Stable host addressing** gives each virtual machine or physical host a private address. Each silo on that host advertises a unique port pair.

HTTP ingress, reverse proxies, and shared service virtual IPs route application traffic. Orleans transport connections continue to use the individual endpoints stored in membership. See [Topology, networking, and clustering](networking.md) for the runtime boundary.

## Match client placement to the target

An Orleans client discovers gateway endpoints from the clustering provider and connects to those endpoints directly.

- In-cluster clients fit pod-overlay networks because they can route to every gateway pod IP.
- Clients in connected virtual networks require a target whose advertised gateway addresses are routed to those networks.
- Public callers use an authenticated application API. Keep silo and gateway ports on trusted private networks.
- Cohosted clients use the local silo while application HTTP or gRPC ingress remains a separate platform path.

Document the client networks and test every advertised gateway from each one before launch.

## Match scaling to Orleans capacity

The platform changes process or node count. Orleans membership incorporates new silos, placement uses the available silos, and activations recover after a silo leaves.

Set scaling policy from measured application signals:

- Keep a tested minimum silo count and enough capacity to lose one failure domain.
- Scale out early enough for new silos to start, join membership, and absorb activations.
- Scale in one bounded set of silos at a time after readiness is removed and graceful host shutdown begins.
- Coordinate pod or process autoscaling with node, plan, or host capacity.
- Load-test scale-out, scale-in, restart, provider degradation, and rolling replacement.

See [Capacity planning and scaling](capacity-planning.md) and [Graceful shutdown and upgrades](upgrades.md).

## Production coverage for every target

Complete these outcomes in the selected target guide and record environment-specific values in the deployment runbook.

| Concern | Required outcome |
| --- | --- |
| Topology and networking | Every silo has a unique advertised endpoint, every silo can reach every silo endpoint, and every client can reach every advertised gateway endpoint. |
| Dependencies and data | Production clustering, grain storage, reminders, streams, and external dependencies have explicit availability, durability, quota, backup, and recovery decisions. |
| Identity and secrets | Runtime and deployment identities have separate least-privilege permissions. Credentials and certificates use managed delivery, rotation, and expiry monitoring. |
| Health and lifecycle | Startup, readiness, liveness, dependency health, graceful shutdown, and the platform termination deadline have distinct measured behavior. |
| Scaling and resilience | Minimum capacity, autoscaling boundaries, failure-domain distribution, disruption policy, and recovery after one failure domain are load-tested. |
| Upgrades and rollback | Compatible releases use bounded rolling replacement. Incompatible releases use isolated cluster IDs and an explicit state and traffic migration plan. |
| Observability and incidents | Central logs, metrics, traces, deployment identity, membership evidence, alerts, dashboards, and incident commands support diagnosis under load. |
| Infrastructure delivery | Infrastructure and workload configuration are versioned, reviewed, reproducible, and validated before production traffic. |

Use the [production-readiness checklist](production-readiness.md) for the full review and [Troubleshoot deployments](troubleshooting-deployments.md) for incident evidence.
