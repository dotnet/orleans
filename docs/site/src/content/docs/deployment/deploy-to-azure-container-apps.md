---
title: Host Orleans on Azure Container Apps
description: Deploy and qualify a multi-replica Orleans cluster on Azure Container Apps.
ms.date: 08/06/2026
ms.topic: how-to
ms.custom: devops
---

# Host Orleans on Azure Container Apps

The maintained [Orleans cluster on Azure Container Apps sample](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps) demonstrates multiple Orleans silos, Orleans clients, external membership, autoscaling, and centralized telemetry in one Container Apps environment.

Use the sample as a reference implementation, not as a production template. Its Orleans topology is viable only while replicas can connect directly to the individual silo and gateway endpoints advertised through Orleans membership. Azure Container Apps ingress is a separate, load-balanced path and doesn't provide that per-replica connectivity.

## Understand the sample

The solution separates the application into these projects:

| Project | Purpose |
| --- | --- |
| `Abstractions` and `Grains` | Grain contracts and implementations |
| `Silo` | A dedicated Orleans silo |
| `Dashboard` | A second silo that hosts the Orleans dashboard |
| `Clients.MinimalApi` | A public HTTP API implemented as an Orleans client |
| `Clients.WorkerService` | A background Orleans client that simulates devices |
| `Scaler` | A custom KEDA external scaler implemented as an Orleans client |
| `Infrastructure` | Shared Application Insights configuration |
| `Azure` | Bicep modules for the registry, storage, telemetry, environment, and container apps |

The sample's workflow provisions the Azure resources, builds five container images, tags them with the Git commit, pushes them to Azure Container Registry, and updates the container apps. The Bicep configuration keeps at least one `silo` replica and allows the custom scaler to request more replicas. The dashboard, API, worker, and scaler are separate container apps in the same environment.

> [!WARNING]
> The sample workflow and Bicep files use a client-secret credential, registry administrator credentials, and a storage account key. They also omit production health probes, a termination grace setting, and durable grain storage. Replace these parts before using the design for production.

## Qualify direct replica networking

Orleans uses three different network paths:

1. Silo replicas connect directly to every advertised silo endpoint.
1. Orleans clients connect directly to the advertised gateway endpoints they discover.
1. Web clients connect to the Minimal API through HTTP ingress.

The third path doesn't replace either of the first two. Container Apps app names and fully qualified domain names route through the platform proxy and load balance across revisions and replicas. Internal or external TCP ingress also addresses a container app, not one specific replica. Don't advertise an app FQDN, revision FQDN, or ingress virtual IP as an Orleans silo endpoint.

The sample doesn't enable ingress on its `silo` app. `Silo/Program.cs` calls `ConfigureEndpoints` with silo port `11111` and gateway port `30000`; Orleans resolves a local IPv4 address and records it in Azure Table membership. The dashboard silo uses ports `11112` and `30001`. This design relies on those dynamic replica addresses being reachable directly by the other replicas. The platform-provided `CONTAINER_APP_REPLICA_NAME` identifies a replica, but it isn't a routable per-replica endpoint.

A **listening endpoint** is the local address and port on which the silo accepts a connection. An **advertised endpoint** is the address and port that Orleans publishes so another process can connect. Binding to all local interfaces can be useful in a container, but it doesn't choose a routable advertised address:

```csharp
siloBuilder.ConfigureEndpoints(
    advertisedIP,
    siloPort: 11_111,
    gatewayPort: 30_000,
    listenOnAnyHostAddress: true);
```

Determine `advertisedIP` from a network interface address that is assigned to the replica and reachable inside the Container Apps environment. Don't infer it from the HTTP ingress hostname. Log the selected address at startup and compare it with the address in the membership table.

Before accepting production traffic:

1. Start at least three silo replicas in the target environment and network configuration.
1. Record every advertised silo and gateway endpoint from Orleans membership.
1. From every silo, open a TCP connection to every other advertised silo endpoint.
1. From every Orleans client, open a TCP connection to every advertised gateway endpoint.
1. Repeat the matrix during scale-out, scale-in, revision replacement, and environment maintenance.
1. Confirm that replacement replicas advertise new addresses and that former replicas are marked dead and excluded from gateway discovery.

The sample shows that this topology can run multiple replicas, but Azure Container Apps doesn't expose a stable per-replica endpoint through ingress. Treat direct replica reachability as a deployment qualification that must be retested after platform, environment type, virtual network, or ingress changes. If the required path isn't reachable and supported for the selected environment, use a platform with direct per-instance networking.

See [Topology, networking, and clustering](networking.md), [Container Apps networking](/azure/container-apps/networking), and [communication between container apps](/azure/container-apps/connect-apps).

## Configure clustering and durable state

All silos and clients need a supported external clustering provider. The sample uses Azure Table Storage through `UseAzureStorageClustering`, with a common `ServiceId` and `ClusterId`. That membership table coordinates the cluster and lets clients discover gateways.

Membership storage isn't grain storage. The sample's sensor grain logs received values and doesn't persist them, and the silo doesn't register a durable grain storage provider. For application state that must survive collection or loss of every replica:

1. Configure a supported [grain persistence provider](../grains/grain-persistence/index.md).
1. Persist state explicitly using the provider's Orleans integration or an application-owned database.
1. Back up and restore-test the state store according to its durability requirements.
1. Keep production, test, and blue-green cluster identifiers and provider namespaces isolated.

Use private connectivity and provider-side firewall rules where required. Capacity-test the membership and state providers independently; cluster membership traffic and grain state have different availability, retention, and recovery requirements.

## Use managed identity

Assign a managed identity to each container app that accesses Azure resources. Configure Azure Storage providers with a service URI and a `TokenCredential` rather than a connection string containing an account key.

Grant only the data-plane roles each identity requires:

- **Storage Table Data Contributor** for Azure Table membership or table-backed grain state.
- The corresponding data role for another state provider, such as **Storage Blob Data Contributor** for blob-backed state.
- **AcrPull** for the identity used by Container Apps to pull private images.

Scope assignments to the specific registry, storage account, or narrower resource where possible. Disable shared-key authorization after every workload uses Microsoft Entra authentication. Don't place storage keys, registry passwords, or complete connection strings in Bicep outputs, environment variables, images, or logs.

For a non-Azure dependency that still requires a secret, store it in Azure Key Vault and use a [Container Apps Key Vault reference](/azure/container-apps/manage-secrets#reference-secret-from-key-vault). Pin a secret version when deployment-controlled rotation is required; otherwise, test the platform's latest-version refresh and restart behavior. Restrict secret access to the workload identity that consumes it.

See [managed identities in Container Apps](/azure/container-apps/managed-identity) and [managed identity image pulls](/azure/container-apps/managed-identity-image-pull).

## Secure continuous deployment

Replace the sample's `AzureSPN` JSON secret with GitHub Actions workload identity federation:

1. Create a dedicated Microsoft Entra application or user-assigned managed identity for deployment.
1. Add a federated identity credential constrained to the intended repository and protected GitHub environment.
1. Require environment approval and restrict which branches or tags can deploy.
1. Give the workflow only `contents: read` and `id-token: write`.
1. Pass the client, tenant, and subscription identifiers to `azure/login`; don't create a client secret.
1. Pin third-party actions to reviewed commit hashes.

Separate privileged infrastructure bootstrap from routine image deployment. Bootstrap can create identities and narrowly scoped role assignments. The routine identity generally needs image push permission on its registry and only the resource actions required to publish a new Container Apps revision. It shouldn't have subscription-wide Owner permission or permission to create arbitrary role assignments.

Use immutable image tags such as the Git commit and deploy by digest when practical. Scan images and infrastructure changes before deployment, and retain the source commit, image digest, revision name, and approver as deployment metadata.

See [OpenID Connect authentication from GitHub Actions](/azure/developer/github/connect-from-azure-openid-connect) and [publishing Container Apps revisions with GitHub Actions](/azure/container-apps/github-actions).

## Configure health and lifecycle

Define explicit startup, readiness, and liveness probes. Container Apps supports HTTP(S) or TCP probes, but not `exec` or gRPC probes. The sample Bicep declares no probes; platform-added TCP defaults for an ingress-enabled app only prove that a port accepts connections.

| Probe | Orleans behavior to represent |
| --- | --- |
| Startup | Configuration and credentials are valid, listeners are bound, required providers are initialized, and the silo has joined the intended cluster |
| Readiness | The process can safely accept new application work; report unready before and during shutdown |
| Liveness | The process can make local progress; don't call another silo, the clustering provider, or grain storage |

Tune initial delays and failure thresholds from measured cold-start and provider-recovery times. Keep detailed health output private, and don't expose provider names, addresses, or exceptions to unauthenticated callers. See [Container Apps health probes](/azure/container-apps/health-probes) and [Orleans health and observability](health-and-observability.md).

Set `minReplicas` above zero for every active revision that supplies silos. Size the floor to preserve tested availability and capacity after at least one replica is lost; the sample's floor of one demonstrates scaling but isn't a high-availability recommendation. Don't allow an Orleans cluster to scale to zero. Durable state might survive, but the cluster and its gateways won't be available.

During scale-in, revision deactivation, and deletion, Container Apps sends `SIGTERM`. Let normal .NET host shutdown stop Orleans and wait for the silo to leave membership. Set the container app template's `terminationGracePeriodSeconds` longer than the measured host shutdown time and include margin for provider latency. The platform default is 30 seconds; after the grace deadline, termination can be forced. The sample doesn't override the default.

Graceful shutdown reduces failed calls but isn't a correctness guarantee. Test abrupt termination as well as graceful termination, and don't acknowledge work before its required durable effects complete. See [Container Apps application lifecycle](/azure/container-apps/application-lifecycle-management) and [Graceful shutdown and upgrades](upgrades.md).

## Plan revisions and upgrades

A Container Apps revision is an immutable application snapshot, not an Orleans deployment boundary.

- **Single revision mode** can overlap old and new replicas while startup and readiness probes pass. If both use the same `ClusterId`, they join one Orleans cluster, so their grain interfaces, serializers, state, and provider schemas must be compatible.
- **Multiple revision mode** can keep two revisions active and split ingress traffic. Traffic weights affect HTTP or TCP ingress; they don't control Orleans membership, placement, or gateway discovery. A revision with zero percent HTTP traffic can still join the cluster when it has running replicas.
- **Rolling updates** must preserve the minimum ready silo count and enough spare capacity for activation movement. Update every silo-hosting component, including the sample dashboard silo, using mutually compatible versions.
- **Blue-green updates** for incompatible versions require distinct `ClusterId` values and isolated membership. Share mutable grain state only when both versions have an explicit, safe migration and ownership protocol. Otherwise, use separate state and cut over at the application HTTP ingress after validation.

Don't assume that moving an ingress label drains direct Orleans calls. Observe membership and gateway connections, stop new application work, and wait for the old silos to leave before deactivating their revision. Test rollback after the new version has written state.

See [Container Apps revisions](/azure/container-apps/revisions) and [traffic splitting](/azure/container-apps/traffic-splitting).

## Bound untrusted grain identities

A public route that maps arbitrary input to a grain key can create an effectively unbounded number of activations. Validate and authorize the identity before obtaining or invoking a grain reference.

> [!WARNING]
> The sample's default branch still accepts arbitrary string hello-grain keys. Apply and validate the open [bounded hello-grain update](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps/pull/18) before exposing that endpoint. Until the change is merged and deployed, the public route can create an unbounded number of grain activations.

The proposed update applies this rule:

- `IHelloGrain` uses integer grain keys.
- `/hello/{grain:int}` matches integer route values and rejects values outside the inclusive `0` through `255` range with HTTP 400 before invoking Orleans.
- Non-integer values don't match that endpoint.
- `/providers` returns the hello-grain keys as sorted numeric values.
- `HelloGrain` sets a two-minute `CollectionAgeLimit` so inactive hello activations can be collected.

The bounded identity space and collection age together prevent this public path from retaining or continuously creating an unbounded set of hello-grain activations, addressing [CWE-770: Allocation of Resources Without Limits](https://cwe.mitre.org/data/definitions/770.html).

Apply the same principle to every untrusted identity source. Enforce a finite domain or strict length and format limit, tenant authorization, request rate and concurrency limits, and bounded downstream work. A short collection age alone doesn't make an unlimited key space safe.

## Configure observability

Route Container Apps console and system logs to a central destination and enable HTTP ingress logs when needed. Monitor the platform's CPU, memory, network, replica, and restart metrics by revision and replica.

Export Orleans logs, metrics, and traces in addition to the platform signals. Correlate:

- Service ID, cluster ID, silo name, replica name, revision, image digest, and source commit.
- Membership joins, departures, suspicions, and ready silo count.
- Gateway connections, request failures, timeouts, rejections, and dropped messages.
- Activation count and collection, scheduler delay, process resources, and provider latency or throttling.

Don't use unbounded grain keys, secrets, or tenant data as metric dimensions. Alert when ready silo count falls below the tested floor, membership churn is sustained, provider failures increase, or a rollout changes latency or error rate. Secure or disable the sample dashboard before production; don't expose administrative cluster details through public ingress.

See [Container Apps logging](/azure/container-apps/logging), [Container Apps metrics](/azure/container-apps/metrics), and [Orleans observability](../host/monitoring/index.md).

## Validate the deployment

Complete these checks in a production-like environment:

- [ ] At least three silos form one cluster and every direct silo and gateway TCP path succeeds.
- [ ] Silo and client membership rows contain replica-specific addresses, not ingress addresses.
- [ ] Durable grain state survives collection, loss of every replica, and cluster restart.
- [ ] Workloads use managed identity; storage shared-key access and registry administrator access are disabled.
- [ ] CI uses OIDC, protected environments, immutable action pins, and narrowly scoped Azure roles.
- [ ] Startup, readiness, and liveness failures cause the intended and distinct platform behavior.
- [ ] Silo minimum replicas remain above zero and scaling never crosses the tested redundancy floor.
- [ ] Graceful scale-in completes within the configured termination grace period; forced termination also recovers safely.
- [ ] Rolling revisions preserve compatibility, capacity, and gateway reachability.
- [ ] Blue and green clusters remain isolated unless coexistence and shared state are explicitly designed.
- [ ] After applying the bounded hello-grain update, `/hello/0` and `/hello/255` succeed, `/hello/-1` and `/hello/256` return HTTP 400, and non-integers don't match.
- [ ] After applying the update, `/providers` returns numeric keys and inactive hello activations are eligible for collection after two minutes.
- [ ] Logs and metrics identify the replica and revision without exposing secrets or unbounded identifiers.

The sample's custom activation-count scaler and its value of 300 grains per silo are demonstrations, not capacity guidance. A [reported scaler regression](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps/issues/17) also means that this component must be revalidated or replaced. Base production scaling on measured CPU, memory, scheduler delay, request latency, provider capacity, and loss-of-replica tests, with conservative scale-in.
