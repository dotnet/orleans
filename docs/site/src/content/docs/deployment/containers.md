---
title: Run Orleans in containers across multiple hosts
description: Configure routable Orleans endpoints for multi-host container deployments.
ms.date: 08/15/2026
ms.topic: how-to
ms.custom: devops
---

# Run Orleans in containers across multiple hosts

An Orleans cluster can span container hosts only when every silo has a unique endpoint that all other silos can reach directly. External Orleans clients need the same reachability to every advertised gateway endpoint. Container discovery, a shared membership table, and published host ports don't create those network paths by themselves.

Use a private routed network, container overlay, or per-workload network interface whenever possible. Don't expose the Orleans silo or gateway ports to the public internet.

## Choose an endpoint model

Each silo has separate listening and advertised endpoints:

| Setting | Meaning | Typical container value |
| --- | --- | --- |
| <xref:Orleans.Configuration.EndpointOptions.SiloListeningEndpoint> | Address and port opened inside the container for silo traffic | `0.0.0.0:11111` |
| <xref:Orleans.Configuration.EndpointOptions.GatewayListeningEndpoint> | Address and port opened inside the container for client traffic | `0.0.0.0:30000` |
| <xref:Orleans.Configuration.EndpointOptions.AdvertisedIPAddress> | Per-silo address that peers and clients dial | A routable private container, task, pod, or host address |
| <xref:Orleans.Configuration.EndpointOptions.SiloPort> | Silo port stored in membership | The directly routed or published silo port |
| <xref:Orleans.Configuration.EndpointOptions.GatewayPort> | Gateway port returned to clients | The directly routed or published gateway port |

Binding to `0.0.0.0` or `::` only opens local interfaces. A wildcard address can't be advertised, and Orleans doesn't infer a host's published port from a container bind.

Prefer one of these models:

- **Direct per-container addressing:** Give every container a private address routable from all silo and client networks. Advertise that address and use the same ports inside and outside the container.
- **Per-silo host-port mapping:** Advertise the host's private address and a unique published silo and gateway port pair. Bind to the corresponding target ports inside the container.

Don't advertise a shared load balancer, ingress virtual IP, or service name which can route a connection to a different replica. Orleans membership identifies a specific silo, not an interchangeable backend pool.

## Configure listening and advertised endpoints

Pass the advertised address and published ports to each replica from the deployment platform. The following example uses fixed container target ports and independently supplied advertised values:

:::code language="csharp" source="../snippets/compiled/Deployment/DeploymentSnippets.cs" id="container_endpoint_usings":::
:::code language="csharp" source="../snippets/compiled/Deployment/DeploymentSnippets.cs" id="configure_container_endpoints":::

For example, a container can listen on `0.0.0.0:11111` and `0.0.0.0:30000` while advertising the private host mappings `10.0.8.24:21111` and `10.0.8.24:23000`. Every other silo must be able to connect to `10.0.8.24:21111`, and every external Orleans client must be able to connect to `10.0.8.24:23000`.

If the address or published ports are dynamically allocated, discover them before starting Orleans and keep them stable for the lifetime of that silo membership entry. A replacement instance can use a different endpoint, but it must join with a new silo generation.

## Provide private bidirectional connectivity

Validate the complete network matrix:

1. From every silo network, connect to every advertised silo endpoint.
1. From every client network, connect to every advertised gateway endpoint.
1. When using host-port translation, test the published endpoint from the owning host or container as well as from remote hosts. Some network address translation implementations don't support the required hairpin path.
1. Repeat the tests after scale-out, scale-in, host replacement, and rolling deployment.

Firewall rules, security groups, network policies, and service-mesh policy must allow long-lived TCP connections in both directions between silos. Clients only need gateway access, not silo-port access. Restrict both ports to trusted workloads and use [Orleans Transport Layer Security](../host/transport-layer-security.md) when the network isn't a trusted boundary.

## Understand discovery versus connectivity

A clustering provider such as DynamoDB, Azure Table Storage, ADO.NET, Redis, Consul, or ZooKeeper coordinates membership and gateway discovery. It records the endpoints that participants advertise; it doesn't proxy Orleans traffic, create routes, or verify that those endpoints are reachable.

Therefore, all silos can successfully read and write the same membership table while the cluster still fails to communicate. Likewise, a client can discover active gateways and then time out because its network can't reach one or more advertised gateway endpoints.

HTTP ingress and application service discovery are separate. A frontend load balancer can route HTTP requests to an application, but Orleans silo and gateway connections must still reach the specific member selected from cluster membership.

## Account for container platform behavior

- A host-local bridge address is normally meaningful only on that host. Don't advertise it across hosts unless the platform explicitly routes that address range between hosts.
- A published port is usable only if the host address is routable, the mapping is unique to one silo, and the mapping works from every required source network.
- Multiple silos on one host need distinct advertised silo and gateway ports when they share the host address.
- A dynamic or shared load-balancer mapping which can select any replica doesn't preserve silo identity and isn't suitable as an advertised endpoint.

For Docker Engine, a user-defined [overlay network](https://docs.docker.com/engine/network/drivers/overlay/) can provide cross-host container connectivity; standalone containers still require the participating hosts to be in Swarm mode. With a host bridge and published ports, explicitly advertise the host's private address and each container's unique published ports.

On Amazon ECS, [`awsvpc` task networking](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-networking.html) assigns each task its own network interface and private address, which matches the direct-address model. Other network modes require explicit validation of host-port uniqueness, routing, and security-group rules. A DynamoDB membership table can discover silos on AWS, but it doesn't compensate for unreachable task or host endpoints.

For orchestrated and managed platforms, also review [Platform requirements](platform-guides.md), [Host Orleans on Kubernetes](kubernetes.md), and [Host Orleans on Azure Container Apps](deploy-to-azure-container-apps.md).

## Diagnose forwarding loops and timeouts

Start with the exact endpoints, not only the membership-provider health:

1. Record each process's effective advertised silo endpoint, advertised gateway endpoint, and listening endpoints.
1. Inspect the membership records and confirm that every active row contains a unique, expected, routable silo endpoint.
1. From each silo container, test TCP connectivity to every active advertised silo endpoint. From the client network, test every advertised gateway endpoint.
1. Compare container target ports, host or platform mappings, firewall rules, and the advertised ports.
1. Correlate failures with instance replacement and remove stale membership only after confirming that the old silo can't still be running in a partition.

Use the platform's TCP test tool. For example:

```bash
nc -vz 10.0.8.24 21111
```

```powershell
Test-NetConnection 10.0.8.24 -Port 21111
```

Common indicators include:

| Symptom | Likely endpoint problem |
| --- | --- |
| Membership is healthy, but calls expire at the request timeout | Discovery succeeded, but the advertised transport endpoint is blocked or unroutable. |
| Logs or membership show `127.0.0.1`, a host-local bridge address, or the container target port | The silo advertised its bind-side address or port instead of the peer-reachable mapping. |
| Multiple active silos advertise the same address and port | A shared load balancer or non-unique host mapping can't select the intended silo. |
| Repeated forwarding, connection attempts, or timeout messages target an endpoint which should represent the local or destination silo | The advertised identity doesn't match a reachable endpoint for that silo. Messages can be retried or forwarded without reaching the intended process. |
| A TCP connection succeeds but traffic reaches another replica | A proxy, ingress, or load balancer is distributing Orleans transport connections across replicas. |

Correct the endpoint mapping first. Increasing response timeouts or changing the clustering provider won't make an unreachable advertised endpoint routable. For broader incident triage, see [Troubleshoot deployments](troubleshooting-deployments.md).
