---
layout: page
title: Grain Directory
---
# Grain Directory

## What is the Grain Directory?

Grains have stable logical identities and may get activated (instantiated) and deactivated many times over the life of the application, but at most one activation of grain exist at any point in time.
Each time a grain gets activated, it may be placed on a different silo in the cluster.
When a grain gets activated in the cluster, it gets registered in the global registry, Grain Directory.
This ensures that subsequent invocations of that grain will be delivered to that activation of the grain, and that no other activations (instances) of that grain will be created.
Grain Directory is responsible for keeping a mapping between a grain identity and where (which silo) its current activation is at.

By default, Orleans uses a built-in distributed in-memory directory. 
This directory is eventually consistent and partitioned across all silos in the cluster in a form of a Distributed Hash Table.

Starting with 3.2.0, Orleans also supports pluggable implementations of Grain Directory.

Two such plugins are included in the 3.2.0 release:

- an Azure Table implementation: Microsoft.Orleans.GrainDirectory.AzureStorage (beta)
- a Redis Store implementation: Microsoft.Orleans.GrainDirectory.Redis (beta)

You can configure which Grain Directory implementation to use on a per-grain type basis, and you can even inject your own implementation.

## Which Grain Directory should you use?

We recommend to always start with the default one (built-in in-memory distributed directory).
Even though it is eventually consistent and allows for occasional duplicate activation when cluster is unstable, the built-in directory is self-sufficient with no external dependencies, does not requires any configuration, and has been used in production the whole time.
When you have some experience with Orleans and have a use case for Grain Directory a with stronger single-activation guarantee and/or want to minimize the number of grain that get deactivated when a silo in the cluster shuts down, consider using a storage-based implementation of Grain Directory, such as the Redis implementation.
Try using it for one or a few grain types first, starting with those that are long-lived and have a significant amount of state or an expensive initialization process.

## Configuration

### Default Grain Directory configuration 

You don't have do to anything; the in-memory grain directory will be automatically used and partitioned across the cluster.

### Non-default Grain Directory configuration

You need to specify name of the directory plugin to use via an attribute on the grain class and inject the directory plugin with that name during the silo configuration.

#### Grain configuration

Specifying the Grain Directory plugin name with the ``GrainDirectory`` attribute:

```csharp
[GrainDirectory(GrainDirectoryName = "my-grain-directory")]
public class MyGrain : Grain, IMyGrain
{
    [...]
}
```

#### Silo Configuration

Here we configure the Redis Grain Directory implementation:

```csharp
siloBuilder.AddRedisGrainDirectory(
    "my-grain-directory",
    options => options.ConfigurationOptions = redisConfiguration);
```

The Azure Grain Directory is configured like this:

```csharp
siloBuilder.AddAzureTableGrainDirectory(
    "my-grain-directory",
    options => options.ConnectionString =  = azureConnectionString);
```

You can configure multiple directories with different names to use for different grain classes:

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
        options => options.ConnectionString =  = azureConnectionString);
```