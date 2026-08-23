---
title: Orleans and Aspire integration
description: Model and run Orleans applications with Aspire.
ms.date: 08/10/2026
ms.topic: how-to
---

# Orleans and Aspire integration

## Overview

<a id="prerequisites"></a>

The `Aspire.Hosting.Orleans` package models an Orleans cluster and its backing services in an Aspire AppHost. [Aspire](https://aspire.dev/get-started/what-is-aspire/) supplies cluster identity, endpoints, provider configuration, service discovery, dependency ordering, and observability context to silo and client projects.

Use Aspire when you want a repeatable local distributed environment or already use an AppHost to describe deployment resources. Aspire orchestrates the application resources and supplies configuration to the Orleans providers that implement clustering, storage, reminders, streaming, directories, and journaling. See [Install the Aspire CLI](https://aspire.dev/get-started/install-cli/) for the supported toolchain.

## Configure the AppHost

<a id="required-packages"></a>
<a id="apphost-project"></a>

Reference `Aspire.Hosting.Orleans` and the Aspire integrations for the resources you use:

:::code language="xml" source="snippets/aspire/AppHost/AppHost.csproj" id="apphost_packages":::

Define a clustering resource and an Orleans resource, then reference Orleans from the silo project:

<a id="basic-orleans-cluster-with-redis-clustering"></a>

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="basic_orleans_cluster":::

`.WithReplicas(3)` starts three local silo replicas. `.WaitFor(redis)` prevents the silo project from starting before Redis is ready.

Add only the capabilities the application needs:

<a id="orleans-with-grain-storage-and-reminders"></a>

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="orleans_with_storage_reminders":::

The named grain storage resources correspond to named Orleans providers such as `Default` and `PubSubStore`.

## Configure the Orleans silo project

<a id="orleans-silo-project"></a>
<a id="service-defaults-pattern"></a>

Register the keyed Aspire client for every backing resource consumed by Orleans, then call parameterless <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*>:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_basic_config":::

The AppHost injects the `Orleans` configuration hierarchy. Orleans binds cluster identity, endpoints, clustering, reminders, streaming, grain storage, and grain directory configuration from it.

> [!IMPORTANT]
> Resource references inject configuration, but the application project must register the matching keyed service client. For example, use `AddKeyedRedisClient`, `AddKeyedAzureTableServiceClient`, or the matching Aspire integration method for the resource type and name.

Use the <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> delegate only for configuration that the AppHost doesn't model, such as application-specific options or custom services.

## Configure the Orleans client project

<a id="orleans-client-project-if-separate-from-silo"></a>

<a id="separate-silo-and-client-projects"></a>

Create a client-only view of the Orleans resource with `.AsClient()`:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="separate_silo_and_client":::

In the client project, register the keyed resource client and call parameterless <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*>:

:::code language="csharp" source="snippets/aspire/Client/ClientProgram.cs" id="client_basic_config":::

The client receives the same cluster identity and clustering provider settings as the silos, but doesn't receive silo hosting capabilities.

## Azure Storage with Aspire

<a id="development-vs-production-configuration"></a>
<a id="local-development-using-emulators"></a>
<a id="production-using-managed-services"></a>

This compiled example uses Azurite for local Azure Storage development:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="azure_storage_aspire":::

Register the matching Azure Tables client in the silo:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_azure_config":::

`.RunAsEmulator()` is a local-development choice. For production, bind the Azure Storage resource to a real account and configure identity and access in the deployment environment. Don't copy emulator configuration into a production AppHost.

The same principle applies to Redis and databases: the AppHost resource can launch a local container during development and bind to a managed service in deployment.

## Provider wiring reference

For automatically configured external resources, the AppHost needs the corresponding Aspire hosting integration. The silo or client needs the Orleans provider package and registers any keyed service client named by the generated `ServiceKey`. ADO.NET providers consume injected connection strings directly. AWS resources expose structured outputs, so their examples use `IProviderConfiguration` to map those outputs into Orleans configuration.

| Backend | Aspire-to-Orleans mapping | Orleans capabilities | Provider guidance |
|---|---|---|---|
| Azure Tables | Resource inference and keyed `TableServiceClient` | Clustering, grain storage, reminders, grain directory; explicit grain journaling | [Azure Storage](../grains/grain-persistence/azure-storage.md) |
| Azure Blobs | Resource inference and keyed `BlobServiceClient` | Grain storage; explicit grain journaling | [Azure Storage](../grains/grain-persistence/azure-storage.md) |
| Azure Queues | Resource inference and keyed `QueueServiceClient` | Streaming | [Azure Queue streams](../implementation/streams-implementation/azure-queue-streams.md) |
| Azure Cosmos DB | Resource inference and keyed `CosmosClient` | Clustering, grain storage, reminders | [Azure Cosmos DB storage](../grains/grain-persistence/azure-cosmos-db.md) |
| Redis and Azure Managed Redis | Resource inference and keyed `IConnectionMultiplexer` | Clustering, grain storage, reminders, grain directory, streaming; explicit grain journaling | [Redis storage](../grains/grain-persistence/redis-storage.md) |
| SQL Server and Azure SQL | Database-resource inference and injected connection string | Clustering, grain storage, reminders, grain directory, streaming | [ADO.NET configuration](configuration-guide/configuring-ado-dot-net-providers.md) |
| PostgreSQL and Azure Database for PostgreSQL | Database-resource inference and injected connection string | Clustering, grain storage, reminders, grain directory, streaming | [ADO.NET configuration](configuration-guide/configuring-ado-dot-net-providers.md) |
| MySQL and MariaDB | Database-resource inference and injected connection string | Clustering, grain storage, reminders, grain directory, streaming | [ADO.NET configuration](configuration-guide/configuring-ado-dot-net-providers.md) |
| Oracle Database | Database-resource inference and injected connection string | Clustering, grain storage, reminders | [ADO.NET configuration](configuration-guide/configuring-ado-dot-net-providers.md) |
| Azure Event Hubs | Resource inference, injected Event Hubs connection metadata, and a keyed Azure Tables checkpoint client | Streaming | [Stream providers](../streaming/stream-providers.md) |
| NATS JetStream | Resource inference and keyed `INatsConnection` | Streaming | [Stream providers](../streaming/stream-providers.md) |
| Amazon DynamoDB | Explicit `IProviderConfiguration` mapping for AWS endpoints and structured outputs | Clustering, grain storage, reminders | [DynamoDB with Aspire](dynamodb-aspire.md) |
| Amazon SQS | Explicit partition-topology configuration from the AppHost | Streaming | [SQS streaming](../streaming/sqs-streaming.md) and prerequisite [#10797](https://github.com/dotnet/orleans/pull/10797) |
| Amazon Kinesis | Explicit `IProviderConfiguration` mapping for stream and checkpoint outputs | Streaming with grain or DynamoDB checkpoints | [Kinesis streaming](../streaming/kinesis-streaming.md) |
| In-memory | Orleans AppHost operations | Development clustering, grain storage, reminders, and streaming | This page |

### ADO.NET providers

Orleans registers aliases for the Aspire SQL Server, Azure SQL, PostgreSQL, Azure PostgreSQL, MySQL, MariaDB, and Oracle database resource types. The provider builder selects the matching ADO.NET invariant and resolves the injected connection string through `ServiceKey`:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_apphost":::

The silo calls parameterless <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*>. Register an Aspire database client separately when application code also consumes that client.

Generic connection-string resources can use an explicit `IProviderConfiguration` to select `AdoNet`, set the database invariant, and reference the connection:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_explicit_provider":::

The `WithOrleansProviderType(...)` resource annotation is implemented upstream in [microsoft/aspire#13630](https://github.com/microsoft/aspire/pull/13630) and can replace the provider-name portion of this explicit mapping after it ships in the Aspire package used by the application.

### Grain journaling

Aspire provisions and starts the backing resource, then the silo configures the Orleans journal provider from the injected resource reference. The [Journaling with Azure Blob JSON sample](../grains/journaling/samples.md) demonstrates this explicit path with Azurite and verifies state recovery after grain reactivation.

Configuration-driven journaling providers use the `Orleans:GrainJournaling` section:

```json
{
  "Orleans": {
    "GrainJournaling": {
      "ProviderType": "AzureBlobStorage",
      "ConnectionName": "blobs"
    }
  }
}
```

Register the keyed Aspire client or inject the named connection used by the selected provider before calling <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*>. Orleans activates the provider during silo startup and validates its connection and provider-specific options.

Aspire resource-model support for emitting this section is tracked by [microsoft/aspire#19609](https://github.com/microsoft/aspire/issues/19609).

### Grain directories

Redis and Azure Tables can back named grain directories. This example configures a Redis grain directory in the AppHost:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="grain_directory_apphost":::

Register the Redis client with the same resource name in the silo:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="grain_directory_silo":::

## AppHost extension methods reference

`AddOrleans` produces standard Orleans configuration. The application projects still call <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> or <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*>, and Orleans validates the resulting provider configuration at startup. You can inspect injected environment variables in the Aspire dashboard when diagnosing a missing provider, keyed resource, or endpoint.

Common AppHost operations include:

<a id="core-methods"></a>
<a id="clustering"></a>
<a id="grain-storage"></a>
<a id="reminders"></a>
<a id="streaming"></a>
<a id="grain-directory"></a>

| Operation | Purpose |
|---|---|
| `AddOrleans(name)` | Define an Orleans cluster resource. |
| `WithClustering(resource)` | Select the membership and gateway provider. |
| `WithGrainStorage(name, resource)` | Add named grain storage. |
| `WithReminders(resource)` | Add a durable reminder provider. |
| `WithStreaming(name, resource)` | Add a named stream provider. |
| `WithGrainDirectory(name, resource)` | Add a named grain directory. |
| `AsClient()` | Reference the cluster from a client-only project. |
| `WithReference(orleans)` | Inject Orleans configuration into a project. |

Consult the [Aspire Orleans integration reference](https://aspire.dev/integrations/frameworks/orleans/) for resource types and overloads supported by your Aspire version.

## Best practices

<a id="opentelemetry-configuration"></a>
<a id="health-checks"></a>
<a id="see-also"></a>

Set stable service and cluster identifiers for environments that must interoperate across restarts and rolling deployments:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="explicit_cluster_ids":::

- Treat the AppHost as a resource model, not as a substitute for durable services.
- Use managed identities or workload identities instead of embedding secrets.
- Keep <xref:Orleans.Configuration.ClusterOptions.ServiceId> stable and isolate environments with <xref:Orleans.Configuration.ClusterOptions.ClusterId>.
- Run multiple silo replicas across failure domains.
- Configure readiness, telemetry export, and graceful termination in each application project.
- Match keyed service names exactly between the AppHost and application projects.
