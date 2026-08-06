---
title: Orleans and Aspire integration
description: Learn how to integrate Orleans with Aspire for cloud-native development.
ms.date: 01/21/2026
ms.topic: concept-article
zone_pivot_groups: orleans-version
---

# Orleans and Aspire integration

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

[Aspire](https://aspire.dev/get-started/what-is-aspire/) provides a streamlined approach to building cloud-native applications with built-in support for Orleans. Starting with Orleans 8.0, you can use Aspire to orchestrate your Orleans cluster, manage backing resources (like Redis or Azure Storage), and automatically configure service discovery, observability, and health checks.

## Overview

Orleans integration with Aspire uses the `Aspire.Hosting.Orleans` package in your AppHost project. This package provides extension methods to:

- Define Orleans as a distributed resource
- Configure clustering providers (Redis, Azure Storage, ADO.NET)
- Configure grain storage providers
- Configure reminder providers
- Configure grain directory providers
- Model silo and client relationships

## Prerequisites

Before using Orleans with Aspire, ensure you have:

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- [Aspire CLI](https://aspire.dev/get-started/install-cli/)
- An IDE with Aspire support (Visual Studio 2022 17.9+, VS Code with C# Dev Kit, or JetBrains Rider)

## Required packages

Your solution needs the following package references:

### AppHost project

:::code language="xml" source="snippets/aspire/AppHost/AppHost.csproj" id="apphost_packages":::

### Orleans silo project

:::code language="xml" source="snippets/aspire/Silo/Silo.csproj" id="silo_packages":::

### Orleans client project (if separate from silo)

:::code language="xml" source="snippets/aspire/Client/Client.csproj" id="client_packages":::

## Configure the AppHost

The AppHost project orchestrates your Orleans cluster and its dependencies.

### Basic Orleans cluster with Redis clustering

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="basic_orleans_cluster":::

### Orleans with grain storage and reminders

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="orleans_with_storage_reminders":::

### Separate silo and client projects

When your Orleans client runs in a separate process (such as a web frontend), use the `.AsClient()` method:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="separate_silo_and_client":::

## Configure the Orleans silo project

In your Orleans silo project, configure Orleans to use the Aspire-provided resources:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_basic_config":::

> [!TIP]
> When using Aspire, the parameterless <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> is typically all you need. Aspire injects Orleans configuration (cluster ID, service ID, endpoints, and provider settings) via environment variables that Orleans reads automatically. You only need the delegate overload `UseOrleans(siloBuilder => {...})` when you require additional manual configuration beyond what Aspire provides.

> [!IMPORTANT]
> You must call the appropriate `AddKeyed*` method (such as `AddKeyedRedisClient`, `AddKeyedAzureTableClient`, or `AddKeyedAzureBlobClient`) to register the backing resource in the dependency injection container. Orleans providers look up resources by their keyed service name—if you skip this step, Orleans won't be able to resolve the resource and will throw a dependency resolution error at runtime. This applies to all Aspire-managed resources used with Orleans.

### Configure with explicit connection string

If you need explicit control over the connection string, you can read it from configuration:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_explicit_connection":::

## Configure the Orleans client project

For separate client projects, configure the Orleans client similarly:

:::code language="csharp" source="snippets/aspire/Client/ClientProgram.cs" id="client_basic_config":::

## AppHost extension methods reference

The `Aspire.Hosting.Orleans` package provides these extension methods:

### Core methods

| Method | Description |
|--------|-------------|
| `builder.AddOrleans(name)` | Adds an Orleans resource to the distributed application with the specified name. |
| `.WithClusterId(id)` | Sets the Orleans ClusterId. Accepts a string or `ParameterResource`. If not specified, a unique ID is generated automatically. |
| `.WithServiceId(id)` | Sets the Orleans ServiceId. Accepts a string or `ParameterResource`. If not specified, a unique ID is generated automatically. |
| `.AsClient()` | Returns a client-only reference to the Orleans resource (doesn't include silo capabilities). |
| `project.WithReference(orleans)` | Adds the Orleans resource reference to a project, enabling configuration injection. |

> [!NOTE]
> When you configure a backing resource using `.WithClustering(resource)`, `.WithGrainStorage(name, resource)`, or similar methods, the Orleans resource automatically includes a reference to that backing resource. You don't need to call `.WithReference()` separately for each backing resource—only `.WithReference(orleans)` is required. However, you should use `.WaitFor()` on the backing resource to ensure it's ready before the silo starts.

### Clustering

| Method | Description |
|--------|-------------|
| `.WithClustering(resource)` | Configures Orleans clustering to use the specified resource (Redis, Azure Storage, Cosmos DB, etc.). |
| `.WithDevelopmentClustering()` | Configures in-memory, single-host clustering for local development only. Not suitable for production. |

### Grain storage

| Method | Description |
|--------|-------------|
| `.WithGrainStorage(name, resource)` | Configures a named grain storage provider using the specified resource. |
| `.WithMemoryGrainStorage(name)` | Configures in-memory grain storage for the specified name. Data is lost on silo restart. |

### Reminders

| Method | Description |
|--------|-------------|
| `.WithReminders(resource)` | Configures the Orleans reminder service using the specified resource. |
| `.WithMemoryReminders()` | Configures in-memory reminders for development. Reminders are lost on silo restart. |

### Streaming

| Method | Description |
|--------|-------------|
| `.WithStreaming(name, resource)` | Configures a named stream provider using the specified resource (e.g., Azure Queue Storage). |
| `.WithMemoryStreaming(name)` | Configures in-memory streaming for development. |
| `.WithBroadcastChannel(name)` | Configures a broadcast channel provider with the specified name. |

### Grain directory

| Method | Description |
|--------|-------------|
| `.WithGrainDirectory(name, resource)` | Configures a named grain directory using the specified resource. |

## Service defaults pattern

Aspire uses a ServiceDefaults project pattern to share common configuration across all projects. For Orleans, this typically includes:

### OpenTelemetry configuration

:::code language="csharp" source="snippets/aspire/ServiceDefaults/Extensions.cs" id="service_defaults":::

## Azure Storage with Aspire

You can use Azure Storage resources for Orleans clustering and persistence:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="azure_storage_aspire":::

## Development vs. production configuration

Aspire makes it easy to switch between development and production configurations:

### Local development (using emulators)

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="local_development":::

### Production (using managed services)

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="production_config":::

## Health checks

Aspire automatically configures health check endpoints. You can add Orleans-specific health checks:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="health_checks":::

## Best practices

1. **Use ServiceDefaults**: Share common configuration (OpenTelemetry, health checks) across all projects using a ServiceDefaults project.

2. **Wait for dependencies**: Always use `.WaitFor()` to ensure backing resources (Redis, databases) are ready before Orleans silos start.

3. **Configure replicas**: Use `.WithReplicas()` to run multiple silo instances for fault tolerance and scalability.

4. **Separate client projects**: For web frontends, use `.AsClient()` to configure Orleans client-only mode.

5. **Use emulators for development**: Aspire can run Redis, Azure Storage (Azurite), and other dependencies locally using containers.

6. **Enable distributed tracing**: Configure OpenTelemetry with Orleans source names to trace grain calls across the cluster.

## See also

- [Aspire overview](https://aspire.dev/get-started/what-is-aspire/)
- [Aspire setup and tooling](https://aspire.dev/get-started/install-cli/)
- [Orleans configuration guide](configuration-guide/index.md)
- [Orleans Redis providers](../grains/grain-persistence/index.md#redis-grain-persistence)
- [Orleans Azure Storage providers](../grains/grain-persistence/azure-storage.md)

:::zone-end

:::zone target="docs" pivot="orleans-7-0"

Aspire integration was introduced in Orleans 8.0. For Orleans 7.0, you can still deploy to Aspire-orchestrated environments, but the dedicated `Aspire.Hosting.Orleans` package and its extension methods are not available.

Consider upgrading to Orleans 8.0 or later to take advantage of the Aspire integration features.

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Aspire integration is available in Orleans 8.0 and later. Orleans 3.x does not support Aspire.

:::zone-end
