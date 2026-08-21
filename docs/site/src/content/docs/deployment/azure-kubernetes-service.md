---
title: Host Orleans on Azure Kubernetes Service
description: Deploy and operate an Orleans cluster on AKS with direct pod networking, workload identity, zone-aware placement, autoscaling, and controlled upgrades.
ms.date: 08/21/2026
ms.topic: how-to
ms.custom: devops
---

# Host Orleans on Azure Kubernetes Service

Azure Kubernetes Service (AKS) provides managed Kubernetes control-plane operations while Orleans runs as an application workload. Use one silo per pod, advertise the pod IP supplied by the Kubernetes downward API, and use an external production clustering provider.

Start with the platform-neutral [Kubernetes guide](kubernetes.md) for the Orleans `Deployment`, endpoint configuration, probes, resources, disruption budget, and graceful shutdown baseline. This guide applies Azure networking, identity, availability, scaling, upgrades, and observability to that workload.

## Choose the AKS operating model

[AKS Automatic](https://learn.microsoft.com/azure/aks/intro-aks-automatic) provides managed production defaults for networking, node pools, security, and upgrades. [AKS Standard](https://learn.microsoft.com/azure/aks/what-is-aks) provides direct control over network address management, node pools, upgrade policy, and cluster features.

Choose AKS Standard when external Orleans clients require direct routes to gateway pod IPs or when the workload needs custom node-pool, network, or upgrade controls. Record the selected cluster mode and the Orleans-specific settings that remain application-owned:

- Production clustering, grain storage, reminder, stream, and grain-directory providers.
- Pod endpoint configuration, cluster identity, health endpoints, and shutdown behavior.
- Resource requests, scaling thresholds, disruption policy, and minimum silo count.
- Application compatibility, deployment sequencing, rollback, and disaster recovery.

## Choose an AKS network model

Orleans silos require direct pod-to-pod TCP connectivity. [Azure CNI Powered by Cilium](https://learn.microsoft.com/azure/aks/azure-cni-powered-by-cilium) supports this path and enforces Kubernetes network policies.

Select the IP address management model from the Orleans client topology:

| AKS network model | Silo-to-silo path | Orleans client placement |
| --- | --- | --- |
| [Azure CNI Overlay](https://learn.microsoft.com/azure/aks/concepts-network-azure-cni-overlay) | Pods communicate directly across the cluster overlay using pod IPs. | Run Orleans clients inside the AKS cluster. Endpoints outside the cluster reach application ingress or Kubernetes services rather than overlay pod IPs. |
| [Azure CNI Pod Subnet](https://learn.microsoft.com/azure/aks/concepts-network-azure-cni-pod-subnet) | Pods receive virtual-network addresses from a delegated pod subnet. | Use when trusted clients in peered virtual networks or connected on-premises networks require direct routes to every advertised gateway pod IP. |

Configure the silo from `POD_NAME` and `POD_IP` exactly as shown in [Host Orleans on Kubernetes](kubernetes.md#configure-the-silo). Advertise the pod IP, listen on all pod interfaces, and keep the silo and gateway ports stable across pods.

A Kubernetes `Service`, ingress controller, or [Application Gateway for Containers](https://learn.microsoft.com/azure/application-gateway/for-containers/overview) can expose HTTP or gRPC application traffic. Orleans clients continue to discover and dial the individual gateway pod IPs stored in membership.

## Create the private network boundary

A [private AKS cluster](https://learn.microsoft.com/azure/aks/private-clusters) gives the Kubernetes API server a private endpoint. Combine the private control plane with:

- Private node and pod subnets sized for ordinary scale, upgrade surge, and failure recovery.
- Nonoverlapping pod, service, node, peered-network, VPN, and ExpressRoute address spaces.
- Private endpoints or approved egress paths for clustering, state, reminder, stream, registry, telemetry, and secret dependencies.
- Network policies which allow silo-to-silo TCP, client-to-gateway TCP, DNS, health probes, and the exact provider egress destinations.
- Private DNS resolution for the API server and every private Azure dependency.

Restrict the silo port to the Orleans workload and the gateway port to trusted Orleans clients. Use [Orleans Transport Layer Security](../host/transport-layer-security.md) when the network boundary requires workload-level authentication and encryption.

Validate the effective policy from each workload identity and network. A network policy can allow pod traffic while a subnet security rule, firewall, private endpoint policy, or route table blocks the same path.

## Configure workload identity and dependencies

[Microsoft Entra Workload ID](https://learn.microsoft.com/azure/aks/workload-identity-overview) federates a Kubernetes service account with a Microsoft Entra identity. AKS Automatic preconfigures the cluster OIDC issuer and workload-identity capability. Enable both features on AKS Standard, then assign a dedicated service account to each workload role.

Use separate runtime identities for silos, clients, and operational jobs when their permissions differ. Grant each identity the narrowest data-plane roles for:

- The clustering membership store.
- Named grain storage providers.
- Reminder and stream providers.
- Key Vault secrets and certificates.
- Telemetry export when the application authenticates directly to the destination.

Azure Table Storage is a common clustering provider. ADO.NET or Redis can also fit an existing managed dependency strategy. Select providers using [Topology, networking, and clustering](networking.md#choose-a-clustering-provider), and configure durable state, reminders, and streams independently. A healthy membership table establishes discovery; each application data dependency retains its own durability and recovery requirements.

Use Key Vault references, the Secrets Store CSI Driver, or direct workload-identity access for secrets and certificates. Set rotation and expiry alerts and verify that a rotated value reaches new and existing pods according to the application lifecycle.

AKS pulls container images before the pod workload identity is available. For Azure Container Registry, grant the AKS kubelet identity `AcrPull`, or `Container Registry Repository Reader` for a registry using repository-scoped ABAC permissions. See [Integrate Azure Container Registry with AKS](https://learn.microsoft.com/azure/aks/cluster-container-registry-integration).

## Distribute silos across failure domains

Create a dedicated user node pool for the Orleans workload when isolation, VM sizing, scaling, or maintenance policy differs from system components. AKS keeps a Linux system node pool for critical cluster services; application user pools can use separate labels, taints, and autoscaling bounds. See [Create node pools in AKS](https://learn.microsoft.com/azure/aks/create-node-pools).

In a region with [AKS availability-zone support](https://learn.microsoft.com/azure/aks/reliability-availability-zones-configure):

1. Create zone-spanning or zone-aligned user node pools with VM sizes available in every selected zone.
1. Run at least three silo replicas when the tested availability target requires one ready silo per zone.
1. Add `topologySpreadConstraints` using `topology.kubernetes.io/zone` and `kubernetes.io/hostname`.
1. Add pod anti-affinity so the scheduler distributes silos across nodes.
1. Keep enough quota, subnet addresses, and surge capacity to replace nodes while one zone or node pool is unavailable.

Kubernetes scheduling constraints establish the requested distribution when eligible capacity exists. Verify the actual pod-to-zone placement after deployment and during node-pool upgrades.

Zone-spanning providers complete the design. Configure the clustering, state, reminder, stream, registry, Key Vault, and telemetry services for the same availability objective, and test the application's behavior when one dependency zone is unavailable.

## Configure health, shutdown, and disruption policy

Use the startup, readiness, and liveness behavior from [Host Orleans on Kubernetes](kubernetes.md#health-probes):

- Startup succeeds after configuration is valid, listeners are bound, required initialization completes, and the silo joins the intended cluster.
- Readiness becomes false before shutdown and whenever the application cannot safely accept new work.
- Liveness reports local process progress and remains independent from shared remote dependencies.

Set `terminationGracePeriodSeconds` longer than the measured .NET host shutdown timeout. AKS node drains and workload replacement then give the host time to report unready, stop application work, and let Orleans leave membership.

A `PodDisruptionBudget` limits simultaneous voluntary eviction. It preserves a ready-silo floor during node drain and upgrade when enough schedulable capacity exists. Node failure and zone loss remain abrupt failure paths, so size the cluster to serve load after those events.

Use a rolling update with `maxUnavailable: 0`, bounded surge, and `minReadySeconds` for compatible versions. Make probe timing, progress deadlines, PDBs, node drain timeouts, and host shutdown timeouts mutually consistent so each rollout either progresses with capacity or pauses visibly.

## Scale pods and nodes

Orleans placement uses the silos currently active in membership. An application or platform autoscaler changes the number of silo pods, and the [AKS cluster autoscaler](https://learn.microsoft.com/azure/aks/cluster-autoscaler) adds or removes nodes when pods become unschedulable or nodes can be drained safely.

Configure the two layers together:

- Keep the minimum silo replica count at the tested capacity and redundancy floor.
- Scale silo pods from application signals such as sustained CPU, queue depth, request latency, rejection rate, or a bounded custom metric.
- Keep resource requests representative so pending pods accurately signal node demand.
- Give the node-pool autoscaler enough maximum capacity, quota, and subnet address space for recovery and rollout surge.
- Use stabilization windows and gradual scale-in so membership, activation movement, and provider load settle between removals.
- Keep system and Orleans user node-pool policies independent.

The node autoscaler manages AKS virtual machine scale-set capacity. Preserve its ownership of scale settings rather than applying independent virtual machine scale-set autoscaling.

Load-test a burst which adds pods and nodes, loss of one silo and one node, a controlled scale-in, and provider slowdown. Confirm that the application remains within latency and rejection objectives throughout the sequence.

## Plan application and AKS upgrades

Application rolling upgrades temporarily mix old and new silos in one cluster. Maintain compatibility for grain interfaces, serializers, persisted state, reminders, streams, provider schemas, and external side effects. Use a separate `ClusterId` and traffic migration for an incompatible release.

AKS also upgrades the Kubernetes control plane and node pools. Use [AKS upgrade options](https://learn.microsoft.com/azure/aks/upgrade-options) and planned maintenance to:

1. Validate Kubernetes API compatibility and the Orleans workload in a representative preproduction cluster.
1. Preserve quota, subnet addresses, and node-pool surge capacity.
1. Drain a bounded number of nodes while the PDB and rollout policy retain ready silos.
1. Monitor membership, reactivation load, provider latency, request outcomes, and pod placement.
1. Pause when capacity or application thresholds are exceeded.

Test rollback while mixed application versions and state written by the new version exist. For cluster-level changes which cannot roll back in place, provision a second AKS cluster with a distinct Orleans `ClusterId`, validate it, and migrate application traffic using the [blue-green guidance](upgrades.md#blue-green-upgrades).

## Configure observability and incident access

Export Orleans logs, .NET metrics, distributed traces, Kubernetes events, container logs, and AKS control-plane diagnostics to central systems. [Azure Monitor managed service for Prometheus](https://learn.microsoft.com/azure/azure-monitor/metrics/prometheus-metrics-overview) and [Azure Managed Grafana](https://learn.microsoft.com/azure/managed-grafana/overview) can provide the Azure-managed metrics path.

Correlate:

- AKS cluster, namespace, node pool, node, zone, pod, deployment, and replica set.
- Orleans service ID, cluster ID, silo name, advertised endpoints, and application version.
- Container image digest, deployment operation, provider request, and workload identity.

Alert on sustained user impact, reduced ready-silo count, failed or blocked rollouts, pending pods, node pressure, restart loops, membership churn, provider throttling, credential expiry, and exhausted capacity.

Keep incident access available through the private cluster boundary. Operators need a tested path to run `kubectl`, inspect membership, test every advertised endpoint, query providers, and retrieve previous container logs during a partial outage. See [Troubleshoot deployments](troubleshooting-deployments.md#kubernetes-checks).

## Validate the production deployment

Complete these checks before production traffic and after material network, identity, node-pool, or upgrade-policy changes:

1. Confirm that every active silo membership row contains the expected pod IP and unique silo and gateway ports.
1. Test TCP connectivity from every silo pod to every advertised silo endpoint.
1. Test every advertised gateway endpoint from each Orleans client network.
1. Verify network-policy enforcement and provider access using the workload identities assigned to silos and clients.
1. Confirm actual pod distribution across nodes and zones.
1. Replace one silo pod under load and verify graceful membership departure and activation recovery.
1. Drain one node and verify the disruption budget, termination deadline, replacement scheduling, and capacity floor.
1. Exercise pod scale-out, node scale-out, gradual scale-in, and one-zone or one-node-pool capacity loss.
1. Perform a compatible rolling deployment and rollback.
1. Restore durable application data in an isolated cluster and validate the recovery runbook.

Record the effective manifests, cluster network model, service and pod CIDRs, node-pool configuration, identities, role assignments, provider namespaces, probe thresholds, shutdown deadlines, scaling bounds, and operational commands. Finish the shared [production-readiness checklist](production-readiness.md).
