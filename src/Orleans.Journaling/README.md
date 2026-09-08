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

## Catalog enumeration

`IJournalStorageCatalog.ListAsync` returns an `IAsyncEnumerable<JournalId>` in provider traversal order. Pass `ListOptions` with `Prefix` to select an exact journal id and its descendants, or omit the options to enumerate all ids. Options are read when enumeration begins.

When updating callers of the former prefix overload, pass `new ListOptions { Prefix = prefix }`. Applications which require ordinal ordering can materialize the sequence and sort `JournalId.Value` using `StringComparer.Ordinal`.

Storage providers fetch pages internally and yield matching identities as they discover them. `await foreach` advances the traversal and disposes the enumerator when the loop ends. Consumers which process identities in batches can retain one enumerator across batches, advance it serially, and dispose it after the last pending `MoveNextAsync` completes. Use a cancellation token whose lifetime covers that enumeration.

| Provider | Internal traversal | Client memory |
| --- | --- | --- |
| Volatile | Enumerates the existing concurrent storage dictionary, checks journal existence, and applies the prefix | Constant additional traversal state; the storage dictionary holds the journals. |
| Azure Blob | Requests up to 5000 blobs per service page, then applies WAL and hierarchical prefix filtering | Proportional to the current service page. |
| Azure Table | Requests up to 1000 journal headers per service page, then decodes canonical or reversible legacy ids and applies the prefix | Proportional to the current service page. |
| S3 | Requests up to 1000 bucket objects per `ListObjectsV2` page, then applies canonical WAL and prefix filtering | Proportional to the current service page; includes unordered S3 Express listings. |
| Redis | Scans primary-server metadata keys and reads canonical ids in bounded batches, suppressing repeated ids | Read-batch state plus a seen-id set which grows with the catalog. `SCAN` count remains a service work hint. |

One `MoveNextAsync` can traverse multiple empty or filtered storage pages before yielding an identity. A consumer's identity or metadata-read budget therefore bounds returned candidates, while storage services determine internal scan work, request latency, and retries. Table header queries can inspect additional rows internally, and S3 custom mappings require a bucket traversal.

Enumeration observes live storage. Concurrent changes follow each provider's listing semantics; callers should tolerate repeated identities during changes and use subsequent enumerations to discover later updates. Journal existence can change between discovery and a storage operation. Cancellation and storage errors propagate through enumeration. Dispose a failed enumerator and begin a new enumeration when retrying a listing operation.

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
