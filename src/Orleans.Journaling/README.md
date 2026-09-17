# Microsoft Orleans Journaling

## Introduction
Microsoft Orleans Journaling persists durable state changes as ordered journal data which can be replayed to recover in-memory durable collections and values.

The package includes a JSON Lines-based storage format powered by System.Text.Json and uses it by default. Pair it with a Journaling storage provider such as Microsoft.Orleans.Journaling.AzureStorage. The storage provider remains independent of the serialization format: Microsoft.Orleans.Journaling supplies the journal format and keyed durable-entry codecs which durable states use to encode and recover their own operations.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling
```

## Example - Configuring JSON journaling
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;

[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ulong))]
internal partial class JournalJsonContext : JsonSerializerContext;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddAzureBlobJournalStorage()
            .UseJsonJournalFormat(JournalJsonContext.Default);
    });

await builder.Build().RunAsync();
```

If you need to customize `JsonSerializerOptions`, configure `JsonJournalOptions` through the options pipeline:

```csharp
siloBuilder
    .AddAzureBlobJournalStorage()
    .Configure<JsonJournalOptions>(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.AddTypeInfoResolver(JournalJsonContext.Default);
    });
```

JSON Lines is the default `JournaledStateManagerOptions.JournalFormatKey`. Storage providers can persist the journal format key as metadata alongside journal bytes. During recovery, Orleans uses that stored key to select the matching journal format and durable operation codecs. If a non-empty journal has no stored format metadata, Orleans treats it as legacy OrleansBinary data for compatibility.

If you already have data written with the OrleansBinary format, you can keep using it while you plan a migration:

```csharp
siloBuilder
    .AddAzureBlobJournalStorage()
    .ConfigureServices(services =>
        services.Configure<JournaledStateManagerOptions>(options =>
            options.JournalFormatKey = "orleans-binary"));
```

To migrate to JSON, configure `JournaledStateManagerOptions.JournalFormatKey` to `JsonJournalExtensions.JournalFormatKey` and call `UseJsonJournalFormat(...)`. When a grain recovers data written with a different format than the configured write format, the next write is forced to a full snapshot so the journal is rewritten using JSON and the storage format metadata is updated.

## Example - Using durable states
```csharp
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Journaling;

public interface IShoppingCartGrain : IGrainWithStringKey
{
    ValueTask AddItem(string itemId, int quantity);
    ValueTask<Dictionary<string, int>> GetItems();
}

public sealed class ShoppingCartGrain(
    IJournaledStateManager stateManager,
    [FromKeyedServices("cart")] IDurableDictionary<string, int> cart)
    : Grain, IShoppingCartGrain
{
    public async ValueTask AddItem(string itemId, int quantity)
    {
        cart[itemId] = quantity;
        await stateManager.WriteStateAsync(CancellationToken.None);
    }

    public ValueTask<Dictionary<string, int>> GetItems() => new(cart.ToDictionary());
}
```

The standard grain-scoped factory registered by `AddJournalStorage` enrolls the state manager in the grain lifecycle before returning it. Constructor-injected durable states register with that manager, and recovery completes before `OnActivateAsync` and grain requests. This works with `Grain` or an application-owned grain base class. Registering journal storage makes the services available; only activations which resolve the manager perform per-grain journal I/O.

`DurableGrain` remains an optional convenience base exposing `StateManager`, `GetOrCreateState`, and `WriteStateAsync`. It also enrolls explicitly supplied managers implementing `ILifecycleParticipant<IGrainLifecycle>`, including a standard manager supplied by a custom registration, while preserving the standard hosting factory's completed enrollment. A custom manager used with an ordinary grain assigns enrollment to its service factory or an explicit shared activation setup action.

Managers created with an explicit `JournalId` through `IJournaledStateManagerFactory`, or constructed manually, retain caller-owned initialization and disposal even when created inside a grain call. Register their states and await `InitializeAsync(CancellationToken.None)` before use, or deliberately supply them through a registration which assigns lifecycle ownership. Creation through the explicit-journal factory keeps failure handling independent of the ambient grain context, including when the caller subsequently enrolls the manager in a lifecycle. The asynchronous `IJournaledStateManager` methods require a cancellation-token argument.

All durable state types use the configured JSON codec automatically. Configure `JsonJournalOptions` to control the `JsonSerializerOptions` instance used for entry payloads. Journaling command names and record shape are fixed by the storage format, so serializer naming policies only affect user payload values.

For trimming and Native AOT, use `Configure<JsonJournalOptions>(...)` to configure `SerializerOptions.TypeInfoResolver`, `SerializerOptions.TypeInfoResolverChain`, or `JsonJournalOptions.AddTypeInfoResolver(...)` with source-generated metadata for every journaled key, value, and state type. The `UseJsonJournalFormat(JournalJsonContext.Default)` overload is the recommended low-friction path when you also want to enable the JSON format explicitly. If metadata is unavailable, the JSON durable entry codecs fail with a configuration error instead of falling back to reflection-based serialization.

## Staging and failure boundaries

The pending journal is shared by every caller using a state manager. Prepare fallible work and external
acknowledgements in operation-local data. Once an outcome is safe to commit, apply its mutations to the
durable states and await `WriteStateAsync`. Applications are responsible for sequencing that transition
with other interleaved operations and for making uncertain-outcome retries idempotent.

A failed journal operation permanently fences the manager, faults queued operations, and requests
deactivation of the associated grain. In-flight calls retain their existing in-memory state while subsequent
state-manager operations fail explicitly. A new activation recovers the actual durable outcome.
For a manager created through `IJournaledStateManagerFactory`, dispose the failed instance and create
another manager for the same `JournalId`, registering new state instances before initialization.

Cancelling a caller's wait leaves an already queued write running. Observe durability through write
acknowledgement or a fresh activation before deciding whether to retry an application command.

## Storage format

The JSON journaling format stores journal entries as true JSON Lines: UTF-8 text, no byte order mark, and one JSON array per journal entry line. Each line is terminated by `\n`. Recovery accepts both LF and CRLF line endings. Storage providers which use format metadata should store `JsonJournalExtensions.JournalFormatKey` as the format key and may use `application/jsonl` as the MIME type.

Each record contains the state id as element 0 and the durable operation payload array as element 1:

```json
[8,["set","alpha",1]]
```

Inside the operation payload array, element 0 is the command name, followed by command-specific operands such as keys, values, item arrays, or versions. Storage write batches append one or more complete JSON Lines records without adding a separate extent envelope or final container-close step.

Existing data is read using its stored format metadata, or as legacy OrleansBinary data when metadata is absent, and migrated to the configured write format by the next snapshot write.

## Catalog enumeration

`IJournalStorageCatalog.ListAsync` returns an `IAsyncEnumerable<JournalCatalogEntry>` in provider traversal order.
Each entry contains its `Id` and optional `Metadata`. Deduplicate by `Id` when unique identities
are required, since repeated entries can carry different metadata versions.
`ListOptions.Prefix` matches the raw beginning of `JournalId.Value`, including partial path
segments. For example, `jobs/shards/20260909` selects timestamped names for that UTC day.
Use a trailing slash, such as `jobs/shards/`, to select a namespace's descendants.
`MinId` and `MaxId` supply inclusive lower and upper bounds. All three constraints use
`StringComparison.Ordinal` and are snapshotted when enumeration begins. Default values
leave the corresponding constraint open. Disjoint constraints produce an empty result.

```csharp
var options = new ListOptions
{
    Prefix = new JournalId("jobs/shards/20260909"),
    MinId = new JournalId("jobs/shards/20260909T1000000000000Z-"),
    MaxId = new JournalId("jobs/shards/20260909T1200000000000Z~"),
    IncludeMetadata = true
};
```

Providers can narrow the native prefix further using the common prefix of the lower and upper
bounds. Applications requiring a uniform result order sort the selected ids using
`StringComparer.Ordinal`.

Storage providers fetch pages internally and yield matching identities as they discover them. `await foreach` advances the traversal and disposes the enumerator when the loop ends. Consumers which process identities in batches can retain one enumerator across batches, advance it serially, and dispose it after the last pending `MoveNextAsync` completes. Use a cancellation token whose lifetime covers that enumeration.

`IncludeMetadata` defaults to false and is snapshotted with the range options. When true, providers
include complete metadata available from the listing: journal format, storage ETag, and caller-owned
properties observed together, using the same semantics as `GetMetadataAsync`. Azure Blob projects
WAL metadata, Azure Table projects header properties, and Volatile snapshots metadata under its store
lock. S3 and Redis return null metadata because their listings expose identities rather than the
complete journal metadata. Projection adds no separate per-journal metadata requests.

Consumers requiring metadata use the supplied snapshot or call `GetMetadataAsync` when it is absent.
An empty caller-property dictionary is a valid complete snapshot. Treat the ETag as the version of that
snapshot and pass it to conditional metadata updates; concurrent changes can cause the update to fail.

| Provider | How narrowly discovery scans | Remaining work |
| --- | --- | --- |
| Volatile | An ordered key index selects a view covering the requested prefix and bounds. | Snapshots selected keys and checks current journal existence. |
| Azure Table, default mapping | Printable ASCII ids use two uppercase hex digits per byte, allowing direct indexed prefix and lower/upper key filters. | Queries include the journal header row condition; the service controls work inside the selected key range. |
| Azure Table, custom mapping | Canonical journal-id filters limit returned headers. | Arbitrary mappings can require a table scan because the journal-id property is not indexed. |
| Azure Blob | The `wal/` namespace and raw id prefix select WAL blobs; conservative ASCII bounds narrow traversal safely in both flat and hierarchical namespaces. | Widened bounds and the final page can include additional candidates, filtered against the original ordinal range. Checkpoints occupy a separate namespace. |
| S3 general-purpose, ordered listing enabled | The `wal/` namespace excludes checkpoints. Identity-mapped keys use native prefixes and an initial `StartAfter` marker preceding the inclusive lower bound, then stop at the upper WAL key. | The final page can overrun the range. Custom key mappings use their configured native prefix and identity filtering. |
| S3 Express directory buckets | A native slash-terminated prefix within `wal/` limits the namespace and excludes checkpoints. | Partial-name and time bounds are filtered during unordered traversal. |
| Redis | Readable key names enable native `SCAN MATCH` prefix filtering and local key-range checks before identity metadata reads for the default mapping. | `SCAN MATCH` still traverses the server keyspace. Custom key mappings read canonical ids from matching metadata hashes. |

Blob native bounds preserve the shared listing prefix and widen suffixes where punctuation or
directory separators affect storage ordering. Ordered S3 native lower/upper optimizations apply
where storage ordering agrees with ordinal identity ordering, including fixed-width ASCII
timestamp names. All original bounds remain enforced while traversing storage. S3 ordered listing is an explicit
`UseOrderedListing` capability setting for general-purpose buckets.
The selected identity count, transferred keys, and backend scan work are separate costs:
server-side filtering can reduce transferred data while the service still examines a wider keyspace.

Enumeration observes live storage. Concurrent changes follow each provider's listing semantics; callers should tolerate repeated identities during changes and use subsequent enumerations to discover later updates. Journal existence can change between discovery and a storage operation. Cancellation and storage errors propagate through enumeration. Dispose a failed enumerator and begin a new enumeration when retrying a listing operation.

## Provider metrics

The `Microsoft.Orleans` meter records catalog traversal and explicit provider retries:

- `orleans-journaling-provider-catalog-pages` counts pages received during S3 and Azure catalog
  traversal, including empty pages.
- `orleans-journaling-provider-catalog-items` counts native page candidates and consumed Redis
  scan keys before local filtering.
- `orleans-journaling-provider-catalog-entries` counts entries delivered by S3, Azure Blob, Azure
  Table, Redis, and Volatile providers.
- `orleans-journaling-provider-retries` counts explicit provider-loop retries.

Pages, items, and entries use a `provider` tag supplied by the provider; retries also use `reason`.
Each provider library owns its short name, such as `volatile` for the in-memory provider.
Compare candidates with delivered entries to assess filtering and duplicate suppression.

Existing state-manager and provider-specific metrics retain storage latency, outcomes, and byte
measurements. The application supplies [client SDK telemetry](../../docs/site/src/content/docs/grains/journaling/operations.md#use-host-provided-dependency-telemetry)
through hosting integrations such as Aspire, instrumentation libraries, or SDK diagnostic
configuration. Use those signals for dependency timing and outcomes, and service-side transaction
metrics for billing reconciliation.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Grain Persistence](https://dotnet.github.io/orleans/docs/grains/grain-persistence/)
- [System.Text.Json Documentation](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/overview)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
