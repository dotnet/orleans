---
layout: page
title: Grain Directory
---
# Grain Directory

## What is the Grain Directory?

To locate grain activation in the cluster, Orleans uses a Grain Directory. The directory responsibility is to keep a mapping between a Grain identity and where its Activation lives, if any.

By default, Orleans uses a built-in distributed directory. This directory is eventually consistent and distributed across all silos in the cluster. 

We also provide:

- an Azure Table implementation: Microsoft.Orleans.GrainDirectory.AzureStorage (beta)
- a Redis Store implementation: Microsoft.Orleans.GrainDirectory.Redis (beta)

You can choose which Grain Directory you want to use on a per-grain basis, and you can even inject your own implementation.

## Which Grain Directory should you use?

You should use the default one (built-in distributed directory). While we encourage people to try the Redis implementation, the default one should be the one to use. When you have some experience with Orleans and have a use case for a more “stable” directory, then consider using the Redis implementation for some long-lived grains.

## Configuration

### Default Grain Directory configuration 

You don't have do to anything; the directory will be automatically distributed accross the cluster.

### Non-default Grain Directory configuration

You need to specify a directory name on your grain implementation and inject the directory you want to use with that name during the silo configuration.

#### Grain configuration

Specifying the Grain Directory name is done with the ``GrainDirectory`` attribute:

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

You can configure multiple directories with different names for different grains:

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