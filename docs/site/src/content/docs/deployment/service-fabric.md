---
title: Host Orleans on Service Fabric
description: Host and operate an Orleans cluster as an application-authored Service Fabric stateless Reliable Service.
ms.date: 08/03/2026
ms.topic: how-to
ms.custom: devops
---

# Host Orleans on Service Fabric

Orleans can run on [Azure Service Fabric](https://learn.microsoft.com/azure/service-fabric) as an unpartitioned stateless Reliable Service. Each Service Fabric service instance hosts one Orleans silo in a normal .NET generic host.

There is no Orleans Service Fabric hosting or clustering package. The integration is application-authored using:

- [`Microsoft.ServiceFabric.Services`](https://www.nuget.org/packages/Microsoft.ServiceFabric.Services) for the Reliable Services runtime.
- [`Microsoft.Orleans.Server`](https://www.nuget.org/packages/Microsoft.Orleans.Server) for the silo.
- One supported [Orleans clustering provider](networking.md#choose-a-clustering-provider) for membership and gateway discovery.

Use multiple stateless service instances across nodes, fault domains, and update domains. Don't use a stateful Reliable Service to host Orleans merely to obtain Service Fabric state replication: Orleans grain state uses an Orleans storage provider, independently of the Service Fabric service type.

## Separate platform and Orleans responsibilities

Service Fabric and Orleans have complementary roles:

| Concern | Owner |
| --- | --- |
| Process placement, restart, service instance lifecycle, application upgrade | Service Fabric |
| Per-instance port allocation and node address | Service Fabric service manifest and runtime context |
| Service Fabric service endpoint publication | <xref:Microsoft.ServiceFabric.Services.Communication.Runtime.ICommunicationListener.OpenAsync*?displayProperty=nameWithType> and the Service Fabric Naming Service |
| Silo membership, failure detection, and Orleans gateway discovery | Orleans and the selected clustering provider |
| Grain activation and placement | Orleans |
| Durable grain state, reminders, and streams | Configured Orleans providers |

Service Fabric Naming Service isn't an Orleans clustering provider. Orleans silos and clients must use the same external clustering provider, <xref:Orleans.Configuration.ClusterOptions.ServiceId>, and <xref:Orleans.Configuration.ClusterOptions.ClusterId>. Don't use <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering*?displayProperty=nameWithType> in a deployed service.

## Implement the generic-host integration

The sample under `snippets/service-fabric` uses Azure Table Storage as one concrete clustering provider so that the code compiles end to end. Replace it with another supported provider when appropriate for the environment.

The compiled example targets Windows Service Fabric nodes and publishes self-contained for `win-x64`, so the nodes don't need a separately installed .NET runtime. For a Linux cluster, select the corresponding Linux runtime identifier, publish a Linux executable, and update the service manifest entry point. Keep the service lifecycle and Orleans configuration pattern unchanged.

The service manifest declares two dynamically allocated TCP endpoints:

:::code language="xml" source="snippets/service-fabric/ServiceFabricSilo/PackageRoot/ServiceManifest.xml":::

The stateless service creates an <xref:Microsoft.ServiceFabric.Services.Communication.Runtime.ICommunicationListener>:

:::code language="csharp" source="snippets/service-fabric/ServiceFabricSilo/OrleansStatelessService.cs":::

The listener owns the Orleans generic host. <xref:Microsoft.ServiceFabric.Services.Communication.Runtime.ICommunicationListener.OpenAsync*?displayProperty=nameWithType> starts it, <xref:Microsoft.ServiceFabric.Services.Communication.Runtime.ICommunicationListener.CloseAsync*?displayProperty=nameWithType> requests graceful shutdown, and <xref:Microsoft.ServiceFabric.Services.Communication.Runtime.ICommunicationListener.Abort*?displayProperty=nameWithType> disposes it without assuming graceful work can complete:

:::code language="csharp" source="snippets/service-fabric/ServiceFabricSilo/OrleansCommunicationListener.cs":::

Finally, the process registers the Service Fabric service type and constructs the Orleans host:

:::code language="csharp" source="snippets/service-fabric/ServiceFabricSilo/Program.cs":::

The example uses:

- The node address from <xref:System.Fabric.NodeContext.IPAddressOrFQDN?displayProperty=nameWithType> as the advertised address.
- Ports allocated from the service manifest for the silo and gateway endpoints.
- `listenOnAnyHostAddress: true` because the advertised node address might not be an address the process can bind directly.
- <xref:Azure.Identity.DefaultAzureCredential> with an Azure Table service URI, avoiding a storage account key in configuration.
- A 120-second .NET host shutdown timeout.

The application manifest should create a singleton-partition stateless service with multiple instances and `ServicePackageActivationMode="ExclusiveProcess"`. This example also wires the Orleans settings, a dedicated local RunAs user, and the service identity binding used by `DefaultAzureCredential`:

:::code language="xml" source="snippets/service-fabric/ApplicationPackageRoot/ApplicationManifest.xml":::

Exclusive process activation avoids sharing one host process and endpoint resources among service instances. Size the instance count for capacity and redundancy; three is only an example.

## Configure topology and endpoints

Each service instance needs:

- A unique silo identity. Orleans generates one if the application doesn't set <xref:Orleans.Configuration.SiloOptions.SiloName>.
- A silo endpoint reachable from every other silo.
- A gateway endpoint reachable from every Orleans client when gateways are enabled.
- Connectivity to the clustering provider and every configured state, reminder, and stream provider.

When an endpoint omits `Port` in _ServiceManifest.xml_, Service Fabric allocates a port from the application port range. The application reads that allocation from <xref:System.Fabric.CodePackageActivationContext.GetEndpoint*>. Don't hard-code the Orleans default ports unless the cluster reserves them and prevents conflicts.

`ICommunicationListener.OpenAsync` returns a string that Service Fabric publishes through its Naming Service. The sample publishes the allocated Orleans endpoints for diagnostics. Orleans clients don't consume that value; they discover gateways through the Orleans clustering provider.

Apply network controls so only trusted silos can reach the silo ports and only trusted clients can reach gateway ports. Don't expose either port directly to the public internet. Configure [Orleans TLS](../host/transport-layer-security.md) when the network boundary alone doesn't provide the required authentication and encryption.

## Health and readiness

Service Fabric opens the communication listener before calling <xref:Microsoft.ServiceFabric.Services.Runtime.StatelessService.RunAsync*?displayProperty=nameWithType>. In this integration, `OpenAsync` completes only after the Orleans host starts, so Service Fabric doesn't publish the listener address while silo startup is still in progress.

That lifecycle boundary is necessary but isn't a complete application health model. Add application-authored Service Fabric [health reports](https://learn.microsoft.com/azure/service-fabric/service-fabric-health-introduction) for sustained conditions that operators or monitored upgrades must evaluate, such as:

- Failure to join the intended Orleans cluster.
- Loss of a required storage or clustering dependency.
- Local saturation or inability to make forward progress.
- A prolonged degraded mode.

Keep transient dependency failures from causing synchronized restarts. If the process also exposes HTTP ingress, implement separate startup, readiness, liveness, and dependency checks as described in [Health and observability](health-and-observability.md).

Set Service Fabric upgrade health policies from application signals. Built-in platform health can detect process and deployment failures, but it can't infer Orleans request correctness or capacity.

## Shutdown and scale-in

During graceful stateless service shutdown, Service Fabric calls `ICommunicationListener.CloseAsync`. The listener calls <xref:Microsoft.Extensions.Hosting.IHost.StopAsync*>, allowing Orleans to leave membership and stop within the supplied deadline.

Graceful shutdown isn't guaranteed:

- `Abort` can occur after a process, node, or lifecycle failure.
- Service Fabric can terminate a code package after configured timeouts.
- The host can crash or lose network access before leaving membership.

Therefore, correctness must tolerate abrupt silo loss and [unknown call outcomes](handling-failures.md). Configure Service Fabric close and upgrade timeouts to exceed the measured Orleans shutdown duration, and align them with <xref:Microsoft.Extensions.Hosting.HostOptions.ShutdownTimeout>.

Scale in one instance or update domain at a time where possible. Wait for Orleans membership and application latency to stabilize before removing more capacity.

## Rolling upgrades

Use Service Fabric monitored rolling upgrades so each update domain must satisfy the application health policy before the next proceeds. Maintain enough instances outside one update domain to serve the workload and absorb reactivated grains.

Old and new silos coexist during a rolling upgrade. They must be compatible at every boundary:

- Grain interfaces and serialized payloads.
- Persisted grain state.
- Clustering, reminder, stream, and storage schemas.
- External side effects and deduplication records.

Increment the application type version and service manifest version for a release. Also increment the version of every changed code, configuration, or data package; changing binaries without changing the `CodePackage` version doesn't identify a new code package to Service Fabric. Test automatic rollback with mixed versions and with state written by the new version. See [Graceful shutdown and upgrades](upgrades.md) and [Service Fabric application upgrades](https://learn.microsoft.com/azure/service-fabric/service-fabric-application-upgrade).

For an incompatible release, use a separately named Service Fabric application and a distinct Orleans `ClusterId`, then follow the [blue-green guidance](upgrades.md#blue-green-upgrades). Don't let incompatible clusters concurrently own the same mutable grain state.

## Identity, secrets, and configuration

Two identities have different purposes:

- A **RunAs identity** is the local operating-system account for the code package. The sample creates a dedicated local user and applies a `RunAsPolicy` instead of running under Service Fabric's default account.
- A **managed identity** authenticates the application to Azure resources. The sample maps `OrleansSiloApplicationIdentity` in the application manifest to `OrleansSiloServiceIdentity` in the service manifest.

The manifest mapping alone doesn't create or assign an Azure identity. The sample is configured for a **user-assigned identity**. Deploy the application as an Azure resource, assign the user-assigned identity in the Azure Resource Manager deployment, and map its friendly name to `OrleansSiloApplicationIdentity`. Applications not deployed as Azure resources can't use Service Fabric application managed identities. See [Deploy a Service Fabric application with a user-assigned managed identity](https://learn.microsoft.com/azure/service-fabric/how-to-deploy-service-fabric-application-user-assigned-managed-identity).

A system-assigned identity uses the reserved application identity name `SystemAssigned`; update the application principal and identity-binding policy accordingly if you choose that model.

Grant the managed identity only the provider permissions it needs. For the sample's Azure Table clustering provider, assign a role containing table data actions, such as **Storage Table Data Contributor**, at the narrowest practical scope. A management-plane Contributor role doesn't grant table data access.

Don't put credentials in source, manifests, application parameters, or command lines. When workload identity isn't available, use an external secret store or [Service Fabric encrypted secrets](https://learn.microsoft.com/azure/service-fabric/service-fabric-application-secret-management), and plan rotation and expiry alerts.

Treat `ServiceId` as the stable application identity and `ClusterId` as the environment or deployment identity. The sample declares nonsecret defaults in the service manifest and parameterized environment overrides in the application manifest. Replace the example table URI and set environment-specific application parameters at deployment. Preserve the effective parameter map during application upgrades because Service Fabric doesn't automatically carry application parameters forward. Validate effective configuration before starting the host and fail startup explicitly when required values are absent.

## Diagnostics

Correlate:

- Service Fabric application, service, partition, instance, node, code package, and version.
- Orleans service ID, cluster ID, silo name, and advertised endpoints.
- Deployment update domain and application upgrade operation.

Use Service Fabric Explorer and health events to inspect placement, restarts, package activation, endpoint allocation, and upgrade decisions. Export Orleans logs, metrics, and traces as described in [Orleans observability](../host/monitoring/index.md). Preserve telemetry outside the Service Fabric cluster so it remains available during cluster incidents.

During an incident, compare the endpoints published by the listener with Orleans membership and test the exact advertised silo and gateway addresses. See [Troubleshoot deployments](troubleshooting-deployments.md).
