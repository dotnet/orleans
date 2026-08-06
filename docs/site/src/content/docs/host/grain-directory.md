---
title: Grain directory
description: Learn about the grain directory in .NET Orleans.
ms.date: 01/22/2026
ms.topic: concept-article
ms.custom: sfi-ropc-nochange
zone_pivot_groups: orleans-version
---

# Orleans grain directory

Grains have stable logical identities. They can activate (instantiate) and deactivate many times over the application's life, but at most one activation of a grain exists at any point in time. Each time a grain activates, it might be placed on a different silo in the cluster. When a grain activates in the cluster, it registers itself in the _grain directory_. This ensures subsequent invocations of that grain are delivered to that activation and prevents the creation of other activations (instances) of that grain. The grain directory is responsible for maintaining a mapping between a grain identity and the location (which silo) of its current activation.

:::zone target="docs" pivot="orleans-10-0,orleans-9-0"

Orleans provides several grain directory implementations:

| Directory | Package | Description |
|-----------|---------|-------------|
| **Distributed In-Cluster** (default) | Built-in | Eventually consistent, partitioned across silos using a distributed hash table. Allows occasional duplicate activations during cluster instability. |
| **Strongly-Consistent In-Cluster** | Built-in | Strongly consistent distributed hash table with versioned range locks. Prevents duplicate activations but requires more coordination. |
| **ADO.NET** | `Microsoft.Orleans.GrainDirectory.AdoNet` | Database-backed directory supporting SQL Server, PostgreSQL, MySQL, and Oracle. |
| **Azure Table Storage** | `Microsoft.Orleans.GrainDirectory.AzureStorage` | Azure Table-backed directory for persistent grain locations. |
| **Redis** | `Microsoft.Orleans.GrainDirectory.Redis` | Redis-backed directory for high-performance persistent lookups. |

:::zone-end

:::zone target="docs" pivot="orleans-8-0,orleans-7-0,orleans-3-x"

By default, Orleans uses a built-in distributed in-cluster directory. This directory is eventually consistent and partitioned across all silos in the cluster in the form of a distributed hash table.

Starting with version 3.2.0, Orleans also supports pluggable grain directory implementations.

Two such plugins are included in the 3.2.0 release:

- An Azure Table implementation: [Microsoft.Orleans.GrainDirectory.AzureStorage](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.AzureStorage)
- A Redis store implementation: [Microsoft.Orleans.GrainDirectory.Redis](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.Redis)

:::zone-end

You can configure which grain directory implementation to use on a per-grain type basis, and you can even inject your implementation.

## Which grain directory should you use?

We recommend always starting with the default directory (the built-in distributed in-cluster directory). Although it's eventually consistent and allows occasional duplicate activations when the cluster is unstable, the built-in directory is self-sufficient, has no external dependencies, requires no configuration, and has been used successfully in production since the beginning.

When you have some experience with Orleans and have a use case requiring a stronger single-activation guarantee, or if you want to minimize the number of grains deactivated when a silo shuts down, consider using a storage-based grain directory implementation, such as the Redis implementation. Try using it for one or a few grain types first, starting with those that are long-lived, have significant state, or have an expensive initialization process.

:::zone target="docs" pivot="orleans-10-0,orleans-9-0"

## Strongly-consistent in-cluster directory

[!INCLUDE [orleans-10-preview](../includes/orleans-10-preview.md)]

Orleans also provides a strongly-consistent grain directory using a distributed hash table with virtual nodes (similar to Amazon Dynamo and Apache Cassandra). Unlike the default eventually-consistent directory, this implementation prevents duplicate grain activations even during cluster instability.

### Key features

- **Strong consistency**: Uses versioned range locks during view changes to ensure consistency
- **Virtual nodes**: Each silo manages 30 virtual nodes (partitions) on the hash ring for better distribution
- **Automatic recovery**: Recovers automatically when silos crash without completing handoff
- **Two-phase operation**: Operates in normal phase and view-change phase for safe membership transitions

### Configuration

To use the strongly-consistent directory, explicitly add it using `AddDistributedGrainDirectory`:

```csharp
// Use as the default grain directory
builder.AddDistributedGrainDirectory();

// Or add as a named directory
builder.AddDistributedGrainDirectory("MyDistributedDirectory");
```

### When to use

Use the strongly-consistent directory when you need to prevent duplicate grain activations during cluster membership changes. The default eventually-consistent directory is suitable for most scenarios where occasional duplicate activations are acceptable.

Consider external storage-backed directories (Redis, Azure Table, ADO.NET) when:

- You need grain registrations to persist across full cluster restarts
- You have very large clusters where memory usage is a concern

## ADO.NET grain directory

The ADO.NET-based grain directory stores grain locations in a relational database. This provides persistent grain location storage that survives cluster restarts.

### Supported databases

- SQL Server
- PostgreSQL
- MySQL / MariaDB
- Oracle

### Installation

Install the NuGet package:

```dotnetcli
dotnet add package Microsoft.Orleans.GrainDirectory.AdoNet
```

### Configuration

Configure the ADO.NET grain directory as the default or as a named directory:

```csharp
// Use as the default grain directory
builder.UseAdoNetGrainDirectoryAsDefault(options =>
{
    options.Invariant = "System.Data.SqlClient"; // or "Npgsql", "MySql.Data.MySqlClient"
    options.ConnectionString = "Server=localhost;Database=Orleans;...";
});

// Or add as a named directory
builder.AddAdoNetGrainDirectory("MyAdoNetDirectory", options =>
{
    options.Invariant = "Npgsql";
    options.ConnectionString = "Host=localhost;Database=Orleans;...";
});
```

### AdoNetGrainDirectoryOptions

| Option | Type | Description |
|--------|------|-------------|
| `Invariant` | `string` | **Required.** The ADO.NET provider invariant name (e.g., `System.Data.SqlClient`, `Npgsql`, `MySql.Data.MySqlClient`). |
| `ConnectionString` | `string` | **Required.** The database connection string. This value is redacted in logs. |

### Database setup

Before using the ADO.NET grain directory, you must create the required database tables. Run the appropriate SQL script for your database:

- SQL Server: [CreateOrleansTables_SqlServer.sql](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Shared)
- PostgreSQL: [CreateOrleansTables_PostgreSql.sql](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Shared)
- MySQL: [CreateOrleansTables_MySql.sql](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Shared)

:::zone-end

## Configuration

By default, you don't need to do anything; Orleans automatically uses the in-cluster grain directory and partitions it across the cluster. If you want to use a non-default grain directory configuration, you need to specify the name of the directory plugin to use. You can do this via an attribute on the grain class and by configuring the directory plugin with that name using dependency injection during silo configuration.

### Grain configuration

Specify the grain directory plugin name using the <xref:Orleans.GrainDirectory.GrainDirectoryAttribute>:

```csharp
[GrainDirectory(GrainDirectoryName = "my-grain-directory")]
public class MyGrain : Grain, IMyGrain
{
    // ...
}
```

#### Silo configuration

Here's how you configure the Redis grain directory implementation:

```csharp
siloBuilder.AddRedisGrainDirectory(
    "my-grain-directory",
    options => options.ConfigurationOptions = redisConfiguration);
```

Configure the Azure grain directory like this:

```csharp
siloBuilder.AddAzureTableGrainDirectory(
    "my-grain-directory",
    options => options.ConnectionString = azureConnectionString);
```

You can configure multiple directories with different names for use with different grain classes:

```csharp
siloBuilder
    .AddRedisGrainDirectory(
        "redis-directory-1",
        options => options.ConfigurationOptions = redisConfiguration1)
    .AddRedisGrainDirectory(
        "redis-directory-2",
        options => options.ConfigurationOptions = redisConfiguration2)
    .AddAzureTableGrainDirectory(
        "azure-directory",
        options => options.ConnectionString = azureConnectionString);
```
