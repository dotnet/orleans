---
title: Orleans NuGet packages
description: Choose Orleans packages for hosts, providers, serialization, observability, and testing.
ms.date: 08/21/2026
ms.topic: reference
---

# Orleans NuGet packages

All official packages use the `Microsoft.Orleans` prefix and are published on [NuGet.org](https://www.nuget.org/profiles/Orleans). Keep Orleans package versions aligned within an application.

## Start with a metapackage

| Package | Use it for |
| --- | --- |
| [Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Server) | Applications which host an Orleans silo. It includes the runtime, SDK, client APIs, and memory persistence provider. |
| [Microsoft.Orleans.Client](https://www.nuget.org/packages/Microsoft.Orleans.Client) | Standalone processes which connect to a cluster without hosting a silo. It includes the SDK. |
| [Microsoft.Orleans.Sdk](https://www.nuget.org/packages/Microsoft.Orleans.Sdk) | Grain contract or implementation libraries which need Orleans analyzers, source generation, serialization, and core APIs. |

Most applications should begin with one of these packages and then add only the provider and feature packages they use.

For installation guidance, see [`dotnet package add`](https://learn.microsoft.com/dotnet/core/tools/dotnet-package-add) and [NuGet package installation workflows](https://learn.microsoft.com/nuget/consume-packages/overview-and-workflow).

## Hosting and observability

| Package | Purpose |
| --- | --- |
| [Microsoft.Orleans.Hosting.Kubernetes](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.Kubernetes) | Kubernetes hosting integration. |
| [Microsoft.Orleans.Dashboard](https://www.nuget.org/packages/Microsoft.Orleans.Dashboard) | Built-in Orleans Dashboard server and UI. |
| [Microsoft.Orleans.Dashboard.Abstractions](https://www.nuget.org/packages/Microsoft.Orleans.Dashboard.Abstractions) | Dashboard contracts for components which don't host the UI. |
| [Microsoft.Orleans.Connections.Security](https://www.nuget.org/packages/Microsoft.Orleans.Connections.Security) | TLS support for Orleans connections. |

`Microsoft.Orleans.Runtime`, `Microsoft.Orleans.Core`, and the abstractions packages are lower-level dependencies of the metapackages. Reference them directly only when building a library with a narrower dependency requirement.

## Clustering

Every multi-silo production cluster needs a shared membership provider.

| Package | Backend |
| --- | --- |
| [Microsoft.Orleans.Clustering.AdoNet](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.AdoNet) | ADO.NET-compatible relational databases |
| [Microsoft.Orleans.Clustering.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.AzureStorage) | Azure Table Storage |
| [Microsoft.Orleans.Clustering.Cassandra](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Cassandra) | Apache Cassandra |
| [Microsoft.Orleans.Clustering.Consul](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Consul) | HashiCorp Consul |
| [Microsoft.Orleans.Clustering.Cosmos](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Cosmos) | Azure Cosmos DB |
| [Microsoft.Orleans.Clustering.DynamoDB](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.DynamoDB) | Amazon DynamoDB |
| [Microsoft.Orleans.Clustering.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.EntityFrameworkCore) | Shared Entity Framework Core clustering infrastructure |
| [Microsoft.Orleans.Clustering.EntityFrameworkCore.MySql](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.EntityFrameworkCore.MySql) | MySQL and MariaDB through Entity Framework Core |
| [Microsoft.Orleans.Clustering.EntityFrameworkCore.PostgreSQL](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.EntityFrameworkCore.PostgreSQL) | PostgreSQL through Entity Framework Core |
| [Microsoft.Orleans.Clustering.EntityFrameworkCore.SqlServer](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.EntityFrameworkCore.SqlServer) | Microsoft SQL Server through Entity Framework Core |
| [Microsoft.Orleans.Clustering.Firestore](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Firestore) | Google Cloud Firestore |
| [Microsoft.Orleans.Clustering.Redis](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Redis) | Redis |
| [Microsoft.Orleans.Clustering.ZooKeeper](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.ZooKeeper) | Apache ZooKeeper |

## Grain persistence

| Package | Backend |
| --- | --- |
| [Microsoft.Orleans.Persistence.AdoNet](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AdoNet) | ADO.NET-compatible relational databases |
| [Microsoft.Orleans.Persistence.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AzureStorage) | Azure Table and Blob Storage |
| [Microsoft.Orleans.Persistence.Cosmos](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Cosmos) | Azure Cosmos DB |
| [Microsoft.Orleans.Persistence.DynamoDB](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.DynamoDB) | Amazon DynamoDB |
| [Microsoft.Orleans.Persistence.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.EntityFrameworkCore) | Shared Entity Framework Core grain persistence infrastructure |
| [Microsoft.Orleans.Persistence.EntityFrameworkCore.MySql](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.EntityFrameworkCore.MySql) | MySQL and MariaDB through Entity Framework Core |
| [Microsoft.Orleans.Persistence.EntityFrameworkCore.PostgreSQL](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.EntityFrameworkCore.PostgreSQL) | PostgreSQL through Entity Framework Core |
| [Microsoft.Orleans.Persistence.EntityFrameworkCore.SqlServer](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.EntityFrameworkCore.SqlServer) | Microsoft SQL Server through Entity Framework Core |
| [Microsoft.Orleans.Persistence.Firestore](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Firestore) | Google Cloud Firestore |
| [Microsoft.Orleans.Persistence.Redis](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Redis) | Redis |
| [Microsoft.Orleans.Persistence.Memory](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Memory) | Process memory for development and testing |

File storage for local, single-silo development and testing is included in [Microsoft.Orleans.Runtime](https://www.nuget.org/packages/Microsoft.Orleans.Runtime).

Memory persistence distributes records across cluster storage grains but isn't durable or replicated. Use it only when losing records with a hosting process is acceptable.

## Reminders and durable jobs

| Package | Purpose |
| --- | --- |
| [Microsoft.Orleans.Reminders](https://www.nuget.org/packages/Microsoft.Orleans.Reminders) | Core reminder support. |
| [Microsoft.Orleans.Reminders.AdoNet](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.AdoNet) | ADO.NET reminder storage. |
| [Microsoft.Orleans.Reminders.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.AzureStorage) | Azure Table Storage reminders. |
| [Microsoft.Orleans.Reminders.Cosmos](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.Cosmos) | Azure Cosmos DB reminders. |
| [Microsoft.Orleans.Reminders.DynamoDB](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.DynamoDB) | Amazon DynamoDB reminders. |
| [Microsoft.Orleans.Reminders.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.EntityFrameworkCore) | Shared Entity Framework Core reminder storage infrastructure. |
| [Microsoft.Orleans.Reminders.EntityFrameworkCore.MySql](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.EntityFrameworkCore.MySql) | MySQL and MariaDB reminder storage through Entity Framework Core. |
| [Microsoft.Orleans.Reminders.EntityFrameworkCore.PostgreSQL](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.EntityFrameworkCore.PostgreSQL) | PostgreSQL reminder storage through Entity Framework Core. |
| [Microsoft.Orleans.Reminders.EntityFrameworkCore.SqlServer](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.EntityFrameworkCore.SqlServer) | Microsoft SQL Server reminder storage through Entity Framework Core. |
| [Microsoft.Orleans.Reminders.Firestore](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.Firestore) | Google Cloud Firestore reminders. |
| [Microsoft.Orleans.Reminders.Redis](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.Redis) | Redis reminders. |
| [Microsoft.Orleans.DurableJobs](https://www.nuget.org/packages/Microsoft.Orleans.DurableJobs) | Distributed scheduling for durable one-time jobs. |
| [Microsoft.Orleans.DurableJobs.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.DurableJobs.AzureStorage) | Azure Blob Storage for durable jobs. |

Use reminders for recurring durable callbacks and durable jobs for scheduled one-time work. Grain timers are activation-scoped and use the core runtime rather than a provider package.

## Streams and broadcast channels

| Package | Purpose |
| --- | --- |
| [Microsoft.Orleans.Streaming](https://www.nuget.org/packages/Microsoft.Orleans.Streaming) | Core Orleans Streams APIs and runtime. |
| [Microsoft.Orleans.Streaming.AdoNet](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.AdoNet) | ADO.NET-backed streams. |
| [Microsoft.Orleans.Streaming.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.AzureStorage) | Azure Queue Storage streams. |
| [Microsoft.Orleans.Streaming.EventHubs](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.EventHubs) | Azure Event Hubs streams. |
| [Microsoft.Orleans.Streaming.Kinesis](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.Kinesis) | Amazon Kinesis Data Streams. |
| [Microsoft.Orleans.Streaming.NATS](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.NATS) | NATS JetStream streams. |
| [Microsoft.Orleans.Streaming.Redis](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.Redis) | Redis Streams integration. |
| [Microsoft.Orleans.Streaming.SQS](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.SQS) | Amazon SQS streams. |
| [Microsoft.Orleans.BroadcastChannel](https://www.nuget.org/packages/Microsoft.Orleans.BroadcastChannel) | Lightweight broadcast channels. |

## Grain directories

Custom grain directory providers store grain registration information for selected grain types.

| Package | Backend |
| --- | --- |
| [Microsoft.Orleans.GrainDirectory.AdoNet](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.AdoNet) | ADO.NET-compatible relational databases |
| [Microsoft.Orleans.GrainDirectory.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.AzureStorage) | Azure Table Storage |
| [Microsoft.Orleans.GrainDirectory.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.EntityFrameworkCore) | Shared Entity Framework Core grain directory infrastructure |
| [Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.MySql](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.MySql) | MySQL and MariaDB through Entity Framework Core |
| [Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL) | PostgreSQL through Entity Framework Core |
| [Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.SqlServer](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.SqlServer) | Microsoft SQL Server through Entity Framework Core |
| [Microsoft.Orleans.GrainDirectory.Firestore](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.Firestore) | Google Cloud Firestore |
| [Microsoft.Orleans.GrainDirectory.Redis](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.Redis) | Redis |

These packages don't replace the cluster membership provider.

## State models and transactions

| Package | Purpose |
| --- | --- |
| [Microsoft.Orleans.EventSourcing](https://www.nuget.org/packages/Microsoft.Orleans.EventSourcing) | Event-sourced grain base types and log-consistency abstractions. |
| [Microsoft.Orleans.Journaling](https://www.nuget.org/packages/Microsoft.Orleans.Journaling) | Pre-release alpha durable journaled collections and values with experimental diagnostic `ORLEANSEXP005`; see the [Journaling overview](../grains/journaling/index.md). |
| [Microsoft.Orleans.Journaling.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Journaling.AzureStorage) | Pre-release alpha Azure Blob and Azure Table providers for Orleans Journaling. |
| [Microsoft.Orleans.Journaling.Redis](https://www.nuget.org/packages/Microsoft.Orleans.Journaling.Redis) | Pre-release alpha Redis provider for Orleans Journaling. |
| [Microsoft.Orleans.Journaling.S3](https://www.nuget.org/packages/Microsoft.Orleans.Journaling.S3) | Pre-release alpha Amazon S3 Express One Zone provider for Orleans Journaling. |
| [Microsoft.Orleans.Transactions](https://www.nuget.org/packages/Microsoft.Orleans.Transactions) | Distributed transaction runtime. |
| [Microsoft.Orleans.Transactions.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.Transactions.AzureStorage) | Azure Storage transaction state. |
| [Microsoft.Orleans.Transactions.DynamoDB](https://www.nuget.org/packages/Microsoft.Orleans.Transactions.DynamoDB) | Amazon DynamoDB transaction state. |

## Serialization

Orleans source-generates serializers for annotated application types. Add an integration package when using another format or ecosystem.

| Package | Purpose |
| --- | --- |
| [Microsoft.Orleans.Serialization.SystemTextJson](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.SystemTextJson) | System.Text.Json integration. |
| [Microsoft.Orleans.Serialization.MessagePack](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.MessagePack) | MessagePack integration. |
| [Microsoft.Orleans.Serialization.Protobuf](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.Protobuf) | Protocol Buffers integration. |
| [Microsoft.Orleans.Serialization.NewtonsoftJson](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.NewtonsoftJson) | Newtonsoft.Json integration. |
| [Microsoft.Orleans.Serialization.FSharp](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.FSharp) | F# core type support. |

`Microsoft.Orleans.Serialization` and `Microsoft.Orleans.Serialization.Abstractions` are lower-level packages used by the SDK and integrations.

## Testing

| Package | Purpose |
| --- | --- |
| [Microsoft.Orleans.TestingHost](https://www.nuget.org/packages/Microsoft.Orleans.TestingHost) | Host configurable in-process test clusters. |
| [Microsoft.Orleans.Serialization.TestKit](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.TestKit) | Verify custom serializer behavior. |
| [Microsoft.Orleans.Transactions.TestKit.Base](https://www.nuget.org/packages/Microsoft.Orleans.Transactions.TestKit.Base) | Shared transaction test kit. |
| [Microsoft.Orleans.Transactions.TestKit.xUnit](https://www.nuget.org/packages/Microsoft.Orleans.Transactions.TestKit.xUnit) | xUnit integration for the transaction test kit. |

For package installation commands in a multi-project application, see [Build your first Orleans app](../quickstarts/build-your-first-orleans-app.md).
