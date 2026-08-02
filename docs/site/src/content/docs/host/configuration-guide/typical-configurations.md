---
title: Typical Orleans configurations
description: Choose a local, Aspire, or production Orleans 10 configuration.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Typical Orleans configurations

Choose the smallest hosting model that matches the deployment.

| Scenario | Hosting model | Clustering | State and reminders |
|---|---|---|---|
| One-process development | Co-hosted silo and client | `UseLocalhostClustering` | Memory providers |
| Local distributed development | Aspire with multiple silo replicas | Local container or emulator | Local container or emulator |
| Production service with HTTP/API entry points | Co-host ASP.NET Core and Orleans in each silo when resource isolation isn't required | Platform-appropriate durable provider | Durable providers |
| Isolated frontend and worker tier | External Orleans client in frontend; silo-only worker tier | Same durable provider and cluster identity in both tiers | Durable providers on silos |

## Single-process development

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="local_silo_and_client":::

This is intentionally disposable. See [Local development configuration](local-development-configuration.md) before adding more local silos.

## Aspire development and deployment modeling

The compiled AppHost examples define Orleans resources and their dependencies:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="orleans_with_storage_reminders":::

The silo registers the Aspire service clients and lets Orleans consume injected configuration:

:::code language="csharp" source="../snippets/aspire/Silo/SiloProgram.cs" id="silo_basic_config":::

Use `.WithReplicas(...)` to model multiple silos. Local Redis containers and Azurite are useful development dependencies, but production deployments must bind those resources to managed or otherwise durable services. Don't call `.RunAsEmulator()` in a production AppHost configuration.

## Production configuration

A production configuration should make these choices explicit:

1. Pick a durable clustering provider supported by the platform.
2. Set stable `ServiceId` and environment/deployment-specific `ClusterId` values.
3. Configure advertised addresses that every silo and client can route to.
4. Add durable storage, reminders, streams, and grain directories only for features the application uses.
5. Supply credentials through the deployment environment, preferably using workload identity.
6. Configure health/readiness, telemetry, graceful termination, CPU, memory, and server GC.

For example, an Azure deployment can use Azure Table Storage for clustering and reminders and Azure Blob or Table Storage for grain state. An AWS deployment can use DynamoDB. A database-centered deployment can use ADO.NET. Redis, Cosmos DB, Consul, and ZooKeeper providers are also available for the capabilities their packages implement. Kubernetes deployments can use the separate Kubernetes hosting integration with one of these clustering providers.

The provider used for clustering doesn't need to match the grain storage or reminder provider. Select each based on durability, latency, operational ownership, and cost.

## Separate external client

Use an external client when the frontend and silo tier need separate scaling, security boundaries, deployments, or resource isolation. Configure `UseOrleansClient` with exactly the same service identity, cluster identity, and clustering backend as the silo tier. The client reaches gateway endpoints, so expose and secure those routes separately from silo-to-silo endpoints.

## Avoid development configuration in production

Don't use any of the following in production:

- `UseLocalhostClustering`, development clustering, or static gateway lists.
- Memory grain storage or memory reminders when data must survive.
- Azurite or other emulator endpoints.
- Loopback or wildcard addresses as advertised endpoints.
- Unbounded custom client connection retries.

See [Server configuration](server-configuration.md), [Client configuration](client-configuration.md), and [Orleans and Aspire integration](../aspire-integration.md) for implementation details.
