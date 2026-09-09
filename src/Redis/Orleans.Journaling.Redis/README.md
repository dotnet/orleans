# Microsoft Orleans Journaling for Redis

## Introduction

Microsoft Orleans Journaling for Redis provides a Redis-backed implementation of the Orleans Journaling storage and catalog abstractions. Orleans Durable Jobs can use Orleans Journaling as a backing store, so this provider can persist durable job state through the journaling layer.

The provider stores each journal as Redis string data plus Redis hash metadata. Per-journal reads and mutations use atomic Lua scripts. Journal discovery scans metadata keys on each connected primary Redis server and reads the canonical journal id from each hash. Configure Redis persistence, such as AOF with an appropriate `appendfsync` setting, according to the durability guarantees required by your application.

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

## Journal discovery

Use `IJournalStorageCatalog.ListAsync` with optional `ListOptions` to filter by a journal id prefix:

```csharp
await foreach (var journalId in catalog.ListAsync(
    new ListOptions { Prefix = JournalId.Create("jobs") },
    cancellationToken))
{
    // Process the discovered journal.
}
```

The prefix is read when enumeration begins. Discovery scans primary servers incrementally and reads canonical journal ids in batches of at most 128 metadata keys, yielding matching ids in traversal order without sorting or buffering the whole catalog. Duplicate ids are suppressed across the traversal using O(N) seen-id memory for N distinct matching ids; metadata keys are not retained for the whole traversal, so repeated scan results can cause repeated metadata reads.

The Redis `SCAN COUNT` value of 250 is a hint, not a strict response-size or server-work bound. One enumerator advancement can traverse many empty or nonmatching scans, and Redis/client-side scan buffering is not bounded by the metadata batch size. Discovery observes live storage rather than a snapshot.

Storage errors and cancellation propagate without provider-level retries, restarts, or success fallbacks. A disconnected primary or the absence of any primary is an error; failures on later servers can occur after earlier ids have been yielded. Dispose the enumerator when stopping early. Cancellation is checked between scan and metadata operations, but does not abort an in-flight Redis metadata request.

## Documentation

- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Grain Persistence](https://dotnet.github.io/orleans/docs/grains/grain-persistence/)
- [Redis Documentation](https://redis.io/docs/latest/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
