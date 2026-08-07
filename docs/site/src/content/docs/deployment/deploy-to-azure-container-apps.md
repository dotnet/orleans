---
title: Host Orleans on Azure Container Apps
description: Design, secure, deploy, and validate an Orleans cluster on Azure Container Apps.
ms.date: 08/06/2026
ms.topic: concept-article
---

# Host Orleans on Azure Container Apps

Azure Container Apps can host Orleans, but the topology must preserve Orleans endpoint semantics. Orleans silos connect to the unique silo and gateway endpoint that each member advertises. Container Apps service discovery and TCP or HTTP ingress instead address a **container app or revision** and load-balance across its replicas. They don't provide a supported replica DNS name or replica IP address.

To build a cluster from documented Container Apps endpoints, use a virtual-network-integrated internal environment and its private static IP. Deploy each silo as a separate Container App with exactly one replica and assign each app a unique pair of TCP ingress ports on that private IP. Repeat that resource to add silos. You must still complete the production acceptance tests on this page. A single Container App scaled to multiple silo replicas can work in a particular environment, but direct replica addresses aren't part of the documented Container Apps networking contract.

Use the [Deploy and scale an Orleans app on Azure](../quickstarts/deploy-scale-orleans-on-azure.md) quickstart to learn the Azure Developer CLI deployment flow. The maintained [Orleans cluster on Azure Container Apps sample](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureContainerApps) is also useful for studying a multi-component topology and the external-scaler contract. Review the [sample limitations](#understand-the-maintained-sample) before adapting either resource for production.

If the application uses Aspire, the [Aspire deployment flow for Azure Container Apps](https://aspire.dev/deployment/azure/container-apps/) can provision and publish platform resources. It doesn't remove the Orleans endpoint requirements on this page: review the generated topology, keep one replica per advertised silo endpoint, configure external clustering and durable state, and add the required health, identity, upgrade, and capacity controls before production.

## Choose a topology

| Topology | Endpoint behavior | Guidance |
| --- | --- | --- |
| One silo replica per Container App | A unique silo and gateway port pair on the environment's private static IP routes to one app and one silo. | Use this topology when Container Apps is required. Set both minimum and maximum replicas to one, and deploy multiple silo apps. |
| Multiple silo replicas in one Container App | The app and revision host names route through the platform proxy to any eligible replica. Container Apps doesn't publish a supported address for selecting one replica. | Use only after environment-specific qualification. Don't treat observed container IP reachability as a platform guarantee. |
| Silos on Kubernetes or another direct-address platform | The orchestrator publishes a routable address for each pod or process. | Prefer this topology when automatic replica scaling and conventional rolling replacement are requirements. |

The one-replica-per-app topology is operationally heavier because infrastructure as code must create a bounded set of silo apps and allocate environment-unique ports. It also requires a deliberate replacement procedure during upgrades. In exchange, it doesn't depend on undocumented pod networking.

Multiple one-replica apps provide process and app-resource redundancy, but Container Apps doesn't document anti-affinity or cross-zone placement across separate apps. Its zone-redundancy guidance distributes multiple replicas of the same app and requires at least two replicas, which conflicts with this endpoint model. Don't claim availability-zone redundancy for the one-replica-per-app topology. If independent failure-domain placement is required, use a platform that provides both direct per-process addresses and placement controls.

Keep public HTTP APIs in a separate Container App that hosts an Orleans client, or cohost an HTTP API with each silo only when that topology is intentional. Public HTTP ingress routes application requests; it isn't an Orleans transport.

## Configure networking and Orleans endpoints

Create the Container Apps environment in an existing virtual network with its accessibility level set to **Internal**. The environment then has a private static IP on an Azure internal load balancer and no public endpoint.

Configure two TCP ingress ports for every silo app:

- The **silo port** carries silo-to-silo traffic.
- The **gateway port** carries traffic from Orleans clients.

Set the app ingress to `external: true` so that these ports are available at the environment boundary. Because the environment itself is internal, the inbound IP remains private to the virtual network. Assign a unique exposed silo and gateway port to every silo app. Externally exposed TCP ports must be unique across the environment; port `36985` is reserved. Each app can use the same container target ports, such as `11111` and `30000`.

Place clients in the same environment or in a connected private network that can reach the environment's private IP. Restrict network access to trusted workloads and use [Orleans Transport Layer Security](../host/transport-layer-security.md) when the network isn't a trusted boundary.

Pass the environment's `properties.staticIp` and the app's unique exposed ports to every silo. Bind the listening endpoints to the container target ports:

```csharp
using System.Net;
using Orleans.Configuration;

var advertisedAddress = IPAddress.Parse(
    builder.Configuration["ORLEANS_ADVERTISED_IP"]
        ?? throw new InvalidOperationException("ORLEANS_ADVERTISED_IP isn't configured."));
var advertisedSiloPort = int.Parse(
    builder.Configuration["ORLEANS_ADVERTISED_SILO_PORT"]
        ?? throw new InvalidOperationException("ORLEANS_ADVERTISED_SILO_PORT isn't configured."));
var advertisedGatewayPort = int.Parse(
    builder.Configuration["ORLEANS_ADVERTISED_GATEWAY_PORT"]
        ?? throw new InvalidOperationException("ORLEANS_ADVERTISED_GATEWAY_PORT isn't configured."));

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<EndpointOptions>(options =>
    {
        options.AdvertisedIPAddress = advertisedAddress;
        options.SiloPort = advertisedSiloPort;
        options.GatewayPort = advertisedGatewayPort;
        options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 11_111);
        options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 30_000);
    });
});
```

A **listening endpoint** is where the process binds inside its container. An **advertised endpoint** is what Orleans writes to membership for peers and clients. Binding to `0.0.0.0` doesn't discover an address to advertise. If ingress maps different exposed and target ports, configure <xref:Orleans.Configuration.EndpointOptions> directly so that:

- <xref:Orleans.Configuration.EndpointOptions.SiloListeningEndpoint> and <xref:Orleans.Configuration.EndpointOptions.GatewayListeningEndpoint> contain the target ports.
- <xref:Orleans.Configuration.EndpointOptions.AdvertisedIPAddress>, <xref:Orleans.Configuration.EndpointOptions.SiloPort>, and <xref:Orleans.Configuration.EndpointOptions.GatewayPort> contain the routable ingress address and exposed ports.

Container Apps provides `CONTAINER_APP_REPLICA_NAME` to identify a replica and `CONTAINER_APP_HOSTNAME` to identify a revision host. Neither value is a supported per-replica network address. Don't advertise an app or revision host name from multiple silo replicas: a peer can be routed to a different replica than the membership entry identifies.

See [Networking in Azure Container Apps](https://learn.microsoft.com/azure/container-apps/networking), [Configure ingress](https://learn.microsoft.com/azure/container-apps/ingress-how-to), and [Topology, networking, and clustering](networking.md) for the underlying network requirements.

## Configure clustering and durable state

Use an external clustering provider so that silos and clients discover the same membership records. Azure Table Storage is a common choice. A clustering provider stores membership; it doesn't persist grain state. Register a grain storage provider separately for every state name that must survive activation or cluster loss.

The following configuration uses Microsoft Entra ID instead of a storage account key:

```csharp
using Azure.Data.Tables;
using Azure.Identity;
using Orleans.Configuration;

var tableEndpoint = new Uri(
    builder.Configuration["AZURE_TABLE_STORAGE_ENDPOINT"]
        ?? throw new InvalidOperationException("AZURE_TABLE_STORAGE_ENDPOINT isn't configured."));
var tableServiceClient = new TableServiceClient(
    tableEndpoint,
    new DefaultAzureCredential());

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "orders";
            options.ClusterId = builder.Configuration["ORLEANS_CLUSTER_ID"]
                ?? throw new InvalidOperationException("ORLEANS_CLUSTER_ID isn't configured.");
        })
        .UseAzureStorageClustering(
            options => options.TableServiceClient = tableServiceClient)
        .AddAzureTableGrainStorage(
            name: "default",
            options => options.TableServiceClient = tableServiceClient);
});
```

Install `Microsoft.Orleans.Clustering.AzureStorage`, `Microsoft.Orleans.Persistence.AzureStorage`, and `Azure.Identity` for this configuration. Configure the same `ServiceId`, `ClusterId`, clustering table, and credentials on Orleans clients. Keep `ServiceId` stable for the application and use a distinct `ClusterId` for each environment or isolated blue-green cluster.

Assign the silo and client identities **Storage Table Data Contributor** only on the tables or storage account they use. The Orleans Azure Table provider calls <xref:Azure.Data.Tables.TableClient.CreateIfNotExistsAsync*?displayProperty=nameWithType>, so **Storage Table Data Reader** alone isn't sufficient even for a client that subsequently reads gateway records. If grain state uses blobs or another service, grant the corresponding data-plane role at the narrowest practical scope. Disable shared-key access when every consumer supports Microsoft Entra ID.

## Provision production infrastructure

Define the environment and every Container App using a stable Azure Resource Manager API version. A production template should include:

- A virtual-network-integrated internal environment. Pass its private `properties.staticIp` to every silo.
- At least two, and typically three, one-replica silo apps for process and app-resource redundancy. Each silo app has `minReplicas: 1` and `maxReplicas: 1`; this doesn't provide a documented cross-zone placement guarantee.
- A distinct exposed silo and gateway port pair for each app, mapped to the container's listening ports. Enable app-level external TCP ingress only within the internal environment.
- Explicit startup, readiness, and liveness probes.
- A `terminationGracePeriodSeconds` value longer than the measured .NET host and Orleans shutdown time. Container Apps defaults the process grace period to 30 seconds and sends `SIGKILL` when it expires.
- A user-assigned or system-assigned managed identity for each runtime app.
- [Managed-identity image pulls](https://learn.microsoft.com/azure/container-apps/managed-identity-image-pull) from Azure Container Registry. Grant only `AcrPull`, or the equivalent repository-reader role for a registry using ABAC.
- Azure Table Storage or another supported clustering provider, durable grain storage where required, and private service access where the security model requires it.
- Log Analytics or Azure Monitor diagnostic routing, plus application metrics and tracing.
- Immutable image digests or unique image tags. Don't deploy a mutable `latest` tag.

Never scale the Orleans cluster to zero. Keep enough silo apps running to meet the tested availability and capacity floor after losing one instance. Scale out by adding a new one-replica silo app and waiting for it to become active in membership. Scale in one app at a time after removing it from application traffic and allowing graceful shutdown. See [Set scaling rules in Azure Container Apps](https://learn.microsoft.com/azure/container-apps/scale-app).

For true secrets, prefer a Container Apps [Key Vault secret reference](https://learn.microsoft.com/azure/container-apps/manage-secrets#reference-secret-from-key-vault) using a managed identity with **Key Vault Secrets User**. Don't put storage keys, registry passwords, certificates, or deployment credentials directly in source, images, Bicep parameters, or ordinary environment variables.

## Secure continuous deployment

Use [GitHub OpenID Connect (OIDC) and workload identity federation](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect) instead of a long-lived service-principal secret:

```yaml
permissions:
  contents: read
  id-token: write

steps:
  - uses: azure/login@f5d393ae46f8fde4be8b75f32e3fc50e654ad0ca # v3.0.1
    with:
      client-id: ${{ vars.AZURE_CLIENT_ID }}
      tenant-id: ${{ vars.AZURE_TENANT_ID }}
      subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}
```

Restrict the federated credential to the repository and protected branch or GitHub environment that deploys. Pin third-party actions to reviewed commit SHAs.

Separate identities and permissions by responsibility:

- The routine deployment identity can update the intended Container Apps and related resources in one resource group.
- The image-publishing identity has `AcrPush`, or a repository-scoped writer role, only on the target repository.
- Runtime Container Apps use a different identity with image-pull and application data-plane permissions only.
- A separately governed bootstrap identity creates role assignments. Don't grant the routine workflow subscription-wide **Contributor** or permission to assign arbitrary roles.

Validate untrusted input before using it as a grain key. An attacker who can choose arbitrary keys can force an unbounded number of grain activations. Bound the accepted key space, authorize access, rate-limit requests, and avoid recording raw grain keys as metric dimensions.

## Configure health and shutdown

Define HTTP startup, readiness, and liveness probes explicitly in infrastructure as code. Container Apps supports one HTTP(S) or TCP probe of each type per container; it doesn't support `exec` or gRPC probes.

- **Startup** succeeds after configuration is valid, listeners are bound, and the silo has joined the intended cluster.
- **Readiness** succeeds only while the app can safely accept new application traffic. Make it fail when shutdown starts.
- **Liveness** checks local process progress. Don't make it depend on Azure Storage, another silo, or another remote service, because a shared outage could restart the entire cluster.

Readiness controls Container Apps ingress traffic. For a one-replica silo app, failing readiness prevents new connections through its advertised silo and gateway ingress ports, but existing TCP connections can remain open. Readiness doesn't update Orleans membership or complete a drain by itself. Coordinate the readiness transition with stopping new application work, leaving membership, closing existing connections, and normal .NET host termination. Set `terminationGracePeriodSeconds` above the measured shutdown time with margin, and keep the .NET host shutdown timeout shorter than that platform deadline.

See [Health probes in Azure Container Apps](https://learn.microsoft.com/azure/container-apps/health-probes), [Application lifecycle in Azure Container Apps](https://learn.microsoft.com/azure/container-apps/application-lifecycle-management), and [Health and observability](health-and-observability.md).

## Plan revisions and upgrades

Container Apps uses single-revision mode by default. It can temporarily run old and new replicas while it waits for startup and readiness. That overlap is unsafe for a one-replica silo app if both revisions share one advertised app endpoint.

Replace silo apps deliberately:

1. Deploy a replacement app with a new pair of exposed ports and the same cluster identity when the versions are compatible.
1. Wait until the replacement is ready and active in Orleans membership.
1. Stop application work on the old silo, allow graceful shutdown, and confirm it leaves membership.
1. Remove the old app only after cluster capacity and latency stabilize.

This process is a rolling replacement across distinct Container App resources, not a traffic-weighted revision rollout.

Old and new silos can share a `ClusterId` only when their grain interfaces, serializers, persisted state, provider schemas, and side effects are compatible. For an incompatible blue-green deployment, use a distinct `ClusterId` and endpoint set. Route application HTTP traffic between matching client or API deployments. Container Apps revision weights and labels affect ingress traffic only; they don't split direct Orleans TCP traffic.

See [Revisions in Azure Container Apps](https://learn.microsoft.com/azure/container-apps/revisions), [Traffic splitting](https://learn.microsoft.com/azure/container-apps/traffic-splitting), and [Graceful shutdown and upgrades](upgrades.md).

## Configure observability

Export application logs, Orleans and .NET metrics, and distributed traces centrally. Add these dimensions to logs and traces:

- `CONTAINER_APP_NAME`, `CONTAINER_APP_REVISION`, and `CONTAINER_APP_REPLICA_NAME`.
- Orleans silo name, advertised endpoint, `ServiceId`, and `ClusterId`.
- Image digest or deployment version.

Monitor ready and active silo count, membership changes, gateway connections, grain call latency and failures, activation count, CPU, memory, socket use, restarts, and provider latency or throttling. Container Apps console and system logs include app, revision, and replica metadata. Enable HTTP logs for public application ingress, and don't emit secrets, tenant data, or unbounded grain keys.

## Validate the deployment

Record deterministic evidence for the exact Container Apps environment and network configuration. First enumerate active revisions and replicas:

```azurecli
az containerapp revision list \
  --resource-group "$RESOURCE_GROUP" \
  --name "$CONTAINER_APP" \
  --query "[].{name:name,active:properties.active,replicas:properties.replicas,health:properties.healthState,running:properties.runningState}" \
  --output table

az containerapp replica list \
  --resource-group "$RESOURCE_GROUP" \
  --name "$CONTAINER_APP" \
  --revision "$REVISION" \
  --output json
```

The replica API doesn't return a supported application-network IP address. Use `az containerapp exec` to inspect each replica's environment and listening sockets, but don't infer a support guarantee from an observed interface address.

Query Azure Table membership using Microsoft Entra authentication:

```azurecli
az storage entity query \
  --account-name "$STORAGE_ACCOUNT" \
  --auth-mode login \
  --table-name OrleansSiloInstances \
  --filter "PartitionKey eq '$CLUSTER_ID'" \
  --output table
```

Complete these acceptance tests before production and after platform or network changes:

1. Confirm that each intended silo has one active membership row with a unique advertised silo and gateway endpoint.
1. From every silo app, open a TCP connection to every other advertised silo endpoint.
1. From every client network, open a TCP connection to every advertised gateway endpoint.
1. Send calls through a bounded set of test grain keys and verify that calls reach activations across the cluster.
1. Add a replacement silo, remove one old silo under load, and confirm graceful membership changes, stable state, and acceptable latency.
1. Restart every silo in turn and verify that durable grain state survives.
1. Exercise the compatible rolling and isolated blue-green procedures, including rollback.
1. Verify that silo and gateway ports aren't publicly reachable and that runtime and deployment identities have only the intended roles.

For the multiple-replica-in-one-app topology, add a blocking test that matches every active Orleans membership endpoint to one Container Apps replica and tests every advertised address from every peer. A successful test establishes evidence for that environment, but it doesn't turn the observed replica IP into a documented Container Apps contract.

## Understand the maintained sample

The [in-repo sample](https://github.com/dotnet/orleans/tree/main/samples/Deployment/AzureContainerApps), imported from the [original Azure sample](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps), demonstrates:

- Two dedicated silo Container Apps, a dashboard silo, a Minimal API client, a worker client, and an external-scaler service in one internal environment.
- One replica per Orleans server app. The two silos and dashboard advertise the environment's private static IP with unique exposed port pairs: `11111`/`30000`, `11112`/`30001`, and `11113`/`30002`.
- Stable Container Apps resource APIs, virtual-network integration with private DNS, explicit startup/readiness/liveness probes, a 60-second termination grace period, and nonzero replica floors.
- A user-assigned runtime identity, managed-identity ACR pulls, disabled registry admin credentials, and disabled storage shared-key access.
- Azure Table Storage clustering through <xref:Azure.Identity.DefaultAzureCredential>. The runtime identity has **Storage Table Data Contributor** on the precreated membership table. The sample doesn't configure durable grain storage.
- A separately run bootstrap template for role assignments and a routine deployment workflow that uses GitHub OIDC, SHA-pinned actions, Git-SHA image tags, and digest-pinned Container App revisions.
- Clients that discover individual gateways through Orleans membership rather than using HTTP ingress as an Orleans transport.
- The external-scaler gRPC service as a study component. It isn't attached to a silo scaling rule because scaling one app to multiple silo replicas would reintroduce the unsupported endpoint assumption.

The maintained sample incorporates the bounded-input design from open [Azure-Samples pull request 18](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps/pull/18). Its hello grain uses an integer key, the public route accepts only keys from 0 through 255 before calling Orleans, the provider endpoint returns numeric keys, and inactive hello grains have a two-minute collection age. Preserve that bounded-input pattern when adapting the API. A collection age alone doesn't make an unbounded key space safe.

The sample is still an architecture demonstration rather than a production deployment manifest. The registry and storage data endpoints remain public, although they require Microsoft Entra authentication. All runtime apps share one identity. The simple readiness endpoints don't implement application-specific draining, and the workflow updates existing apps in place, which can temporarily overlap revisions that advertise the same endpoint. For production upgrades, use the replacement-app procedure on this page. The one-replica apps don't prove cross-zone or independent failure-domain placement, and the external scaler doesn't make a capacity recommendation.

Use the sample to understand component relationships, Orleans membership-based gateway discovery, managed identity, workload generation, and explicit endpoint mapping. Complete the networking, failure, upgrade, security, state-recovery, and readiness acceptance tests on this page before using the topology in production.
