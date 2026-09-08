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
using Orleans.Journaling;

public interface IShoppingCartGrain : IGrainWithStringKey
{
    ValueTask AddItem(string itemId, int quantity);
    ValueTask<Dictionary<string, int>> GetItems();
}

public sealed class ShoppingCartGrain(
    [FromKeyedServices("cart")] IDurableDictionary<string, int> cart)
    : DurableGrain, IShoppingCartGrain
{
    public async ValueTask AddItem(string itemId, int quantity)
    {
        cart[itemId] = quantity;
        await WriteStateAsync();
    }

    public ValueTask<Dictionary<string, int>> GetItems() => new(cart.ToDictionary());
}
```

All durable state types use the configured JSON codec automatically. Configure `JsonJournalOptions` to control the `JsonSerializerOptions` instance used for entry payloads. Journaling command names and record shape are fixed by the storage format, so serializer naming policies only affect user payload values.

For trimming and Native AOT, use `Configure<JsonJournalOptions>(...)` to configure `SerializerOptions.TypeInfoResolver`, `SerializerOptions.TypeInfoResolverChain`, or `JsonJournalOptions.AddTypeInfoResolver(...)` with source-generated metadata for every journaled key, value, and state type. The `UseJsonJournalFormat(JournalJsonContext.Default)` overload is the recommended low-friction path when you also want to enable the JSON format explicitly. If metadata is unavailable, the JSON durable entry codecs fail with a configuration error instead of falling back to reflection-based serialization.

## Storage format

The JSON journaling format stores journal entries as true JSON Lines: UTF-8 text, no byte order mark, and one JSON array per journal entry line. Each line is terminated by `\n`. Recovery accepts both LF and CRLF line endings. Storage providers which use format metadata should store `JsonJournalExtensions.JournalFormatKey` as the format key and may use `application/jsonl` as the MIME type.

Each record contains the state id as element 0 and the durable operation payload array as element 1:

```json
[8,["set","alpha",1]]
```

Inside the operation payload array, element 0 is the command name, followed by command-specific operands such as keys, values, item arrays, or versions. Storage write batches append one or more complete JSON Lines records without adding a separate extent envelope or final container-close step.

Existing data is read using its stored format metadata, or as legacy OrleansBinary data when metadata is absent, and migrated to the configured write format by the next snapshot write.

## Catalog paging

`IJournalStorageCatalog.ListAsync` enumerates matching journal ids in ordinal `JournalId.Value` order. Catalogs which also implement `IPagedJournalStorageCatalog` support resumable pages through the same catalog instance. `ReadPageAsync` returns at most `pageSize` matching ids in provider traversal order.

Start with a null continuation token, then pass each returned token with the same prefix to the next call. A null returned token marks completion. An empty page with a non-null token advances the traversal and gives callers a point to yield before requesting more work. Page size can change between calls. Tokens belong to one prefix and one initialized provider instance; start a new traversal after replacing or restarting the provider. Malformed or mismatched tokens raise `ArgumentException`; storage-service token expiry and request errors propagate to the caller.

| Provider | Work represented by one page | Traversal and memory |
| --- | --- | --- |
| Volatile | A seek into the journal prefix range followed by at most `pageSize` indexed identities, then hierarchical prefix filtering | Ordinal journal id order. The maintained existence index occupies O(catalog size) memory; page allocation is O(page size + log(catalog size)). |
| Azure Blob | One service page of at most `min(pageSize, 5000)` blobs, then WAL and prefix filtering | Blob service traversal order in the default container and WAL naming layout. Page allocation is proportional to the service page. |
| Azure Table | One service page of at most `min(pageSize, 1000)` journal headers, then prefix filtering | Table partition/row order, with canonical ids from headers and reversible legacy partition keys. Page allocation is proportional to the service page. |
| S3 | One `ListObjectsV2` page of at most `min(pageSize, 1000)` bucket objects, then canonical WAL and prefix filtering | S3 traversal order, including unordered S3 Express directory-bucket results. Page allocation is proportional to the service page. |

Storage services determine scanning work, request latency, and retries. In particular, a Table header query can examine additional rows internally, and a filtered traversal can require many pages to find matching journals. The page bounds describe returned storage records and client-side processing.

Pages observe the live catalog. With an unchanged catalog, following continuations visits the matching identities. Concurrent changes follow each provider's listing semantics: callers should tolerate repeated identities and use subsequent traversals to discover new journals. Volatile traversal advances past the last visited id, so newly created earlier ids become visible on the next traversal. Journal existence can change between discovery and a storage operation.

Redis provides the existing sorted `ListAsync` catalog operation. It scans metadata keys on the primary servers and deduplicates identities before yielding them. Its `SCAN` count is a work hint and its cursor traversal can repeat keys; a paged Redis capability requires a separate design for bounded responses and lossless continuation.

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
