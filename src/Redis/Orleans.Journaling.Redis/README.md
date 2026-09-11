# Microsoft Orleans Journaling for Redis

## Introduction

Microsoft Orleans Journaling for Redis provides a Redis-backed implementation of the Orleans Journaling storage and catalog abstractions. Orleans Durable Jobs can use Orleans Journaling as a backing store, so this provider can persist durable job state through the journaling layer.

The provider stores each journal as Redis string data plus Redis hash metadata. Per-journal reads and mutations use atomic Lua scripts. Journal discovery scans metadata keys on each connected primary Redis server. The default key mapping preserves journal ids in the keys, so discovery does not read the metadata hashes. Configure Redis persistence, such as AOF with an appropriate `appendfsync` setting, according to the durability guarantees required by your application.

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

`ListOptions.Prefix` matches the raw journal id using ordinal `StartsWith`, including partial segments. For example, `new JournalId("jobs/2026/0")` matches both `jobs/2026/01` and `jobs/2026/09`. `ListOptions.MinId` and `MaxId` add inclusive ordinal lower and upper bounds; their default values are unlimited. All filters apply together and are snapshotted when enumeration begins. Empty intersections perform no scans or metadata reads.

With the default `GetKeyName` mapping, discovery supplies an escaped `SCAN MATCH` pattern for metadata keys beginning with the encoded raw prefix. A common prefix of `MinId` and `MaxId` can narrow this pattern further. Returned keys contain the journal id, which is decoded and filtered against both bounds locally without `HGET` requests. Malformed matching keys are errors, not silently ignored entries.

Custom `GetKeyName` mappings cannot safely translate logical journal prefixes or bounds into Redis key names. Discovery therefore scans all metadata keys for the configured key prefix and reads canonical journal ids from their `$journal-id` fields in concurrent batches of at most 128, then applies the filters locally. Missing metadata is checked atomically to distinguish deletion from corruption.

Discovery yields ids incrementally in traversal order, without sorting or buffering the whole catalog. Redis `SCAN` is unordered, so encountering a future id does not end traversal. Consumers requiring due order must sort the selected ids using `StringComparer.Ordinal`. Duplicate ids are suppressed across the traversal using O(N) seen-id memory for N distinct matching ids. Metadata keys are not retained for the whole traversal, so custom mappings can cause repeated metadata reads for repeated scan results.

**`SCAN MATCH` still traverses the Redis keyspace on the server.** Prefix narrowing reduces returned keys and client work, not the server-side keyspace scan. Arbitrary ordinal ranges are filtered client-side, not sought through an index. The Redis `SCAN COUNT` value of 250 is a hint, not a strict response-size or server-work bound. One enumerator advancement can traverse many empty or nonmatching scans, and Redis/client-side scan buffering is not bounded by the metadata batch size. Discovery observes live storage rather than a snapshot. With the default mapping, a concurrently deleted metadata key can still yield its journal id; consumers must handle the journal no longer existing when they read it.

Storage errors and cancellation propagate without provider-level retries, restarts, or success fallbacks. A disconnected primary or the absence of any primary is an error; failures on later servers can occur after earlier ids have been yielded. Dispose the enumerator when stopping early. Cancellation is checked between scan and metadata operations, but does not abort an in-flight Redis metadata request.

## Redis key layout

Keys have the form `<keyPrefix>:journal:{<SHA256(keyName)>}:<Uri.EscapeDataString(keyName)>:metadata` or the same base with a `:data` suffix. The reversible key name is outside the existing SHA-256 hash tag, preserving Redis Cluster colocation of each journal's data and metadata for atomic Lua operations. URI encoding preserves literal percent signs, separators, Unicode, and Redis glob characters in journal ids; the scan pattern also escapes glob characters in the configured key prefix.

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
