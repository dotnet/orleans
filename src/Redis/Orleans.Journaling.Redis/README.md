# Microsoft Orleans Journaling for Redis

## Introduction

Microsoft Orleans Journaling for Redis provides a Redis-backed implementation of the Orleans Journaling storage and catalog abstractions. Orleans Durable Jobs can use Orleans Journaling as a backing store, so this provider can persist durable job state through the journaling layer.

The provider stores each journal as Redis string data plus Redis hash metadata, and maintains a Redis set catalog of journal ids. Configure Redis persistence, such as AOF with an appropriate `appendfsync` setting, according to the durability guarantees required by your application.

## Getting Started

Install the package:

```shell
dotnet add package Microsoft.Orleans.Journaling.Redis
```

Configure the silo:

```csharp
using Orleans.Journaling;
using StackExchange.Redis;

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddRedisJournalStorage(options =>
        {
            options.ConfigurationOptions = ConfigurationOptions.Parse("localhost:6379");
        });
});
```

If the Redis connection is already registered in dependency injection, configure it using a keyed service and the `GrainJournaling` provider configuration `ServiceKey`.

## Documentation

- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Journaling](https://learn.microsoft.com/dotnet/orleans/)
