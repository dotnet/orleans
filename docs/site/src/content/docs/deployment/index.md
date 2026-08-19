---
title: Deploy and operate Orleans
description: Plan, deploy, and operate an Orleans application in production.
ms.date: 08/19/2026
ms.topic: overview
---

# Deploy and operate Orleans

An Orleans production deployment is a cluster of silo processes, optionally with separate Orleans clients. Silos communicate directly with each other over TCP. Clients discover gateways through the configured clustering provider and connect to those gateways.

Orleans manages grain activation and cluster membership, but the hosting platform remains responsible for process supervision, networking, health probes, secrets, resource allocation, and controlled rollout. A production design also needs durable grain state where the application requires it.

Review the [Orleans security model](../security/index.md) before choosing the client, network, provider, and administrative boundaries for a production deployment.

<a id="configure-and-start-a-silo"></a>
<a id="configure-and-connect-to-a-client"></a>
<a id="configure-and-connect-a-client"></a>

## Start the application

Configure a silo on the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host) with <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*>, then build and run the host with <xref:Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync*>. For a separate client process, use <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> and run that host the same way. Starting the host starts the silo or connects the client; stopping it coordinates a [graceful Orleans shutdown](../host/configuration-guide/shutting-down-orleans.md).

See [Server configuration](../host/configuration-guide/server-configuration.md) and [Client configuration](../host/configuration-guide/client-configuration.md) for compiled examples.

## Follow the deployment walkthrough

[Deploy an Orleans application to Azure Container Apps](../tutorials-and-samples/production-application.md) takes you from an empty directory to a running multi-process cluster, then through code-based production configuration, deployment, observability, and verification. The sample's application code, infrastructure, and deployment workflow are validated together.

For an existing application, use the walkthrough as a sequence:

1. Host silos and external clients on the .NET Generic Host.
1. Configure cluster identity, providers, credentials, and endpoints in code from deployment-supplied configuration.
1. Choose a platform which gives every silo a unique, directly reachable endpoint.
1. Deploy multiple silos with health probes, telemetry, resource policies, and a graceful termination deadline.
1. Verify membership, TCP reachability, grain calls, provider state, failure behavior, and rolling replacement before admitting production traffic.

<a id="production-configurations"></a>

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

- [Azure Container Apps deployment walkthrough and sample](../tutorials-and-samples/production-application.md)
- [Containers across multiple hosts](containers.md)
- [Kubernetes](kubernetes.md)
- [Service Fabric](service-fabric.md)
- [Azure App Service on Windows](deploy-to-azure-app-service.md)
- [Azure App Service on Linux](deploy-to-azure-app-service-linux.md)
- [Azure Container Apps](deploy-to-azure-container-apps.md)
- Other orchestrators, virtual machines, or bare-metal hosts that satisfy the [platform requirements](platform-guides.md)

For application configuration, see [Server configuration](../host/configuration-guide/server-configuration.md), [Client configuration](../host/configuration-guide/client-configuration.md), and [Typical configurations](../host/configuration-guide/typical-configurations.md).

> [!IMPORTANT]
> Localhost clustering and in-memory grain storage are development defaults. They don't provide a multi-host production cluster or durable application state.
