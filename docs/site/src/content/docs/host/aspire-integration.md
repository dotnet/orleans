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
- Configure clustering, grain storage, reminder, streaming, and grain directory providers
- Model silo and client relationships
- Inject provider configuration into your projects via environment variables

The AppHost configures *what* backing resource to use and *where* to find it. The silo and client projects register the corresponding Aspire component (e.g., `AddKeyedRedisClient`) so that Orleans can resolve it from the dependency injection container.

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

> [!NOTE]
> When using `.AsClient()`, only clustering configuration is injected into the client project. Grain storage, reminders, and grain directory settings are silo-only.

## Configure the Orleans silo project

In your Orleans silo project, configure Orleans to use the Aspire-provided resources:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_basic_config":::

> [!TIP]
> When using Aspire, the parameterless <xref:Microsoft.Extensions.Hosting.GenericHostExtensions.UseOrleans*> is typically all you need. Aspire injects Orleans configuration (cluster ID, service ID, endpoints, and provider settings) via environment variables that Orleans reads automatically. You only need the delegate overload `UseOrleans(siloBuilder => {...})` when you require additional manual configuration beyond what Aspire provides.

> [!IMPORTANT]
> You must call the appropriate `AddKeyed*` method (such as `AddKeyedRedisClient`, `AddKeyedAzureTableServiceClient`, or `AddKeyedAzureBlobServiceClient`) to register the backing resource in the dependency injection container. Orleans providers look up resources by their keyed service name — if you skip this step, Orleans throws a dependency resolution error at runtime. The key must match the Aspire resource name exactly.

## Adapter wiring reference

This section describes which packages and `AddKeyed*` calls are required on the silo or client side for each backing resource type.

### Redis

**AppHost packages:** `Aspire.Hosting.Redis`

| Feature | Orleans package |
|---------|-----------------|
| Clustering | `Microsoft.Orleans.Clustering.Redis` |
| Grain storage | `Microsoft.Orleans.Persistence.Redis` |
| Reminders | `Microsoft.Orleans.Reminders.Redis` |
| Grain directory | `Microsoft.Orleans.GrainDirectory.Redis` |

**Silo/client package:** `Aspire.StackExchange.Redis`

**Silo/client registration:**

```csharp
builder.AddKeyedRedisClient("orleans-redis"); // key = Aspire resource name
```

**AppHost example:**

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="basic_orleans_cluster":::

**Silo example:**

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_basic_config":::

---

### Azure Table Storage

**AppHost packages:** `Aspire.Hosting.Azure.Storage`

| Feature | Orleans package |
|---------|-----------------|
| Clustering | `Microsoft.Orleans.Clustering.AzureStorage` |
| Grain storage | `Microsoft.Orleans.Persistence.AzureStorage` |
| Reminders | `Microsoft.Orleans.Reminders.AzureStorage` |
| Grain directory | `Microsoft.Orleans.GrainDirectory.AzureStorage` |

**Silo package:** `Aspire.Azure.Data.Tables`

**Silo registration:**

```csharp
builder.AddKeyedAzureTableServiceClient("clustering"); // key = .AddTables("...") resource name
```

**AppHost example:**

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="azure_storage_aspire":::

**Silo example:**

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="reminders_azure_table_silo":::

---

### Azure Blob Storage

Azure Blob Storage is supported for **grain storage only** (not clustering, reminders, or grain directory).

**AppHost packages:** `Aspire.Hosting.Azure.Storage`

| Feature | Orleans package |
|---------|-----------------|
| Grain storage | `Microsoft.Orleans.Persistence.AzureStorage` |

**Silo package:** `Aspire.Azure.Storage.Blobs`

**Silo registration:**

```csharp
builder.AddKeyedAzureBlobServiceClient("grainstate"); // key = .AddBlobs("...") resource name
```

---

### ADO.NET (SQL Server, PostgreSQL, MySQL)

ADO.NET providers support clustering, grain storage, and reminders. Use the appropriate Aspire hosting package for your database:

- SQL Server: `Aspire.Hosting.SqlServer`
- PostgreSQL: `Aspire.Hosting.PostgreSQL`
- MySQL: `Aspire.Hosting.MySql`

| Feature | Orleans package |
|---------|-----------------|
| Clustering | `Microsoft.Orleans.Clustering.AdoNet` |
| Grain storage | `Microsoft.Orleans.Persistence.AdoNet` |
| Reminders | `Microsoft.Orleans.Reminders.AdoNet` |

**Silo package:** `Aspire.Microsoft.Data.SqlClient` (SQL Server), `Aspire.Npgsql` (PostgreSQL), or `Aspire.MySqlConnector` (MySQL)

**Silo registration:**

```csharp
builder.AddKeyedSqlServerClient("orleans-db"); // key = Aspire database resource name
```

> [!IMPORTANT]
> ADO.NET resources require manual Orleans configuration in the silo. Aspire infers the Orleans provider name from the resource's .NET class name (for example, `SqlServerDatabaseResource` → `SqlServerDatabase`), which does not match the `AdoNet` provider name that Orleans expects. There is no public API to override this inference in `Aspire.Hosting.Orleans`. Instead, pass the database resource directly to the silo project with `.WithReference(db)` so Aspire injects the connection string, then configure Orleans providers manually using `UseOrleans(siloBuilder => {...})`.

**AppHost example:**

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_apphost":::

**Silo example:**

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="adonet_silo":::

---

### In-memory providers (development only)

For local development, you can use in-memory providers without any external dependencies. In-memory state is lost when the silo restarts.

| Method | Equivalent resource |
|--------|---------------------|
| `.WithDevelopmentClustering()` | No external resource — single-host, in-process |
| `.WithMemoryGrainStorage(name)` | No external resource |
| `.WithMemoryReminders()` | No external resource |
| `.WithMemoryStreaming(name)` | No external resource |

> [!WARNING]
> Development clustering (`.WithDevelopmentClustering()`) only supports a single silo. It does not work with `.WithReplicas()` or in multi-silo deployments.

## Provider support matrix

The following table shows which backing resources are supported for each Orleans feature:

| Feature | Redis | Azure Tables | Azure Blobs | ADO.NET | In-Memory |
|---------|-------|-------------|-------------|---------|-----------|
| **Clustering** | ✅ | ✅ | ❌ | ✅ | ✅ (dev only) |
| **Grain storage** | ✅ | ✅ | ✅ | ✅ | ✅ (dev only) |
| **Reminders** | ✅ | ✅ | ❌ | ✅ | ✅ (dev only) |
| **Grain directory** | ✅ | ✅ | ❌ | ❌ | ❌ |
| **Streaming** | ❌ | ❌ | ❌ | ❌ | ✅ (dev only) |
| **Broadcast channel** | N/A | N/A | N/A | N/A | ✅ (built-in) |

> [!NOTE]
> Streaming providers aren't supported via Aspire environment variable injection as of Orleans 8.x. The `WithStreaming` AppHost API exists, but Orleans streaming providers don't yet consume Aspire-injected configuration. Use `WithMemoryStreaming` for local development and configure streaming providers manually for production.

## Reminders

### Redis reminders

**AppHost:**

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="reminders_redis_apphost":::

**Silo:**

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="reminders_redis_silo":::

### Azure Table Storage reminders

**AppHost:**

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="reminders_azure_table_apphost":::

**Silo:**

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="reminders_azure_table_silo":::

### In-memory reminders (development only)

**AppHost:**

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="reminders_inmemory_apphost":::

**Silo:**

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="reminders_inmemory_silo":::

## Grain directory

Custom grain directories allow you to use a distributed backing store for grain activation lookup instead of the in-process distributed directory. Only Redis and Azure Table Storage are supported.

**AppHost:**

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="grain_directory_apphost":::

**Silo:**

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="grain_directory_silo":::

## How Aspire configures Orleans

When you call `.WithReference(orleans)` on a project, Aspire injects environment variables that Orleans reads at startup. Understanding these variables helps with debugging and manual configuration.

### Always-injected variables

```
Orleans__ClusterId          = <explicit or auto-generated value>
Orleans__ServiceId          = <explicit or auto-generated value>
Orleans__EnableDistributedTracing = true
```

### Silo-only endpoint variables

```
Orleans__Endpoints__SiloPort    = <TCP port>
Orleans__Endpoints__GatewayPort = <TCP port>
```

### Provider variables (per resource)

Each call to `WithClustering`, `WithGrainStorage`, etc. injects a `ProviderType` and, for external resources, a `ServiceKey`:

| AppHost call | Environment variable | Value |
|---|---|---|
| `.WithClustering(redis)` | `Orleans__Clustering__ProviderType` | `Redis` |
| `.WithClustering(redis)` | `Orleans__Clustering__ServiceKey` | `orleans-redis` |
| `.WithClustering(tables)` | `Orleans__Clustering__ProviderType` | `AzureTableStorage` |
| `.WithDevelopmentClustering()` | `Orleans__Clustering__ProviderType` | `Development` |
| `.WithGrainStorage("Default", blobs)` | `Orleans__GrainStorage__Default__ProviderType` | `AzureBlobStorage` |
| `.WithGrainStorage("Default", blobs)` | `Orleans__GrainStorage__Default__ServiceKey` | `grainstate` |
| `.WithMemoryGrainStorage("Default")` | `Orleans__GrainStorage__Default__ProviderType` | `Memory` |
| `.WithReminders(tables)` | `Orleans__Reminders__ProviderType` | `AzureTableStorage` |
| `.WithMemoryReminders()` | `Orleans__Reminders__ProviderType` | `Memory` |
| `.WithGrainDirectory("dir", redis)` | `Orleans__GrainDirectory__dir__ProviderType` | `Redis` |
| `.WithBroadcastChannel("chan")` | `Orleans__BroadcastChannel__chan__ProviderType` | `Default` |

The `ServiceKey` value always equals the Aspire resource name, which must also be passed to `AddKeyed*` on the silo side. The connection string itself is injected under `ConnectionStrings__<resourceName>`.

### Provider type inference

Aspire infers the provider type name from the .NET class name of the resource by stripping the `"Resource"` suffix:

- `RedisResource` → `Redis`
- `AzureBlobStorageResource` → `AzureBlobStorage`
- `AzureTableStorageResource` → `AzureTableStorage`
- `SqlServerDatabaseResource` → `SqlServerDatabase` (incorrect for Orleans ADO.NET — no public override API; configure ADO.NET providers manually in the silo instead)

Because there is no public API to override inferred provider type names in `Aspire.Hosting.Orleans`, configure ADO.NET providers manually in the silo using `UseOrleans(siloBuilder => {...})` and read the database connection string from configuration.

## Configure the Orleans client project

For separate client projects, configure the Orleans client similarly:

:::code language="csharp" source="snippets/aspire/Client/ClientProgram.cs" id="client_basic_config":::

> [!NOTE]
> The client only needs a reference to the clustering resource. Grain storage, reminders, and grain directory resources are silo-only and should not be registered in client projects.

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
| `.WithClustering(resource)` | Configures Orleans clustering to use the specified resource (Redis, Azure Tables, ADO.NET, etc.). |
| `.WithDevelopmentClustering()` | Configures in-memory, single-host clustering for local development only. Not suitable for production or multi-replica deployments. |

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
| `.WithStreaming(name, resource)` | Configures a named stream provider using the specified resource. See [streaming limitation](#provider-support-matrix). |
| `.WithMemoryStreaming(name)` | Configures in-memory streaming for development. |
| `.WithBroadcastChannel(name)` | Configures a broadcast channel provider with the specified name. |

### Grain directory

| Method | Description |
|--------|-------------|
| `.WithGrainDirectory(name, resource)` | Configures a named grain directory using the specified resource (Redis or Azure Tables). |


## Azure Storage with Aspire

You can use Azure Storage resources for Orleans clustering, grain storage, and reminders:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="azure_storage_aspire":::

## Development vs. production configuration

### Local development (using emulators)

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="local_development":::

### Production (using managed services)

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="production_config":::

### Stable cluster IDs for production

By default, `ClusterId` and `ServiceId` are auto-generated per run. This is fine for local development, but in production you should set stable, explicit values. Without stable IDs, silos started in different deployments won't recognize each other, and clients may fail to connect during rolling updates.

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="explicit_cluster_ids":::

## Service defaults pattern

Aspire uses a ServiceDefaults project pattern to share common configuration across all projects. For Orleans, this typically includes:

### OpenTelemetry configuration

:::code language="csharp" source="snippets/aspire/ServiceDefaults/Extensions.cs" id="service_defaults":::

## Health checks

Aspire automatically configures health check endpoints. You can add Orleans-specific health checks:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="health_checks":::

## Configure with explicit connection string

If you need explicit control over the connection string, you can bypass Aspire's automatic configuration:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_explicit_connection":::

## Best practices

1. **Use ServiceDefaults**: Share common configuration (OpenTelemetry, health checks) across all projects using a ServiceDefaults project.

2. **Wait for dependencies**: Always use `.WaitFor()` to ensure backing resources (Redis, databases) are ready before Orleans silos start.

3. **Configure replicas**: Use `.WithReplicas()` to run multiple silo instances for fault tolerance and scalability.

4. **Separate client projects**: For web frontends, use `.AsClient()` to configure Orleans client-only mode.

5. **Use emulators for development**: Aspire can run Redis, Azure Storage (Azurite), and other dependencies locally using containers.

6. **Enable distributed tracing**: Configure OpenTelemetry with Orleans source names to trace grain calls across the cluster.

7. **Set stable cluster IDs for production**: Always call `.WithClusterId()` and `.WithServiceId()` with fixed, meaningful values in production to ensure silos and clients recognize each other across restarts and deployments.

8. **Configure ADO.NET providers manually**: Because Aspire infers the provider type name from the .NET class name and there is no public override API, ADO.NET clustering/storage/reminders must be configured manually in the silo using `UseOrleans(siloBuilder => {...})`.

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

