# Microsoft Orleans Journaling for Azure Storage

## Introduction
Microsoft Orleans Journaling for Azure Storage provides an Azure Storage implementation of the Orleans Journaling provider. This allows journaling and tracking of grain operations using Azure Storage as a backing store.

Blob names are derived from the configured journal storage identity and do not use journal format file extensions. Azure append blobs store the journal format key in blob metadata and, when the selected journal format provides a MIME type, are created with that content type. The WAL blob name and checkpoint blob name can be customized using `AzureBlobJournalStorageOptions.GetWalBlobName` and `GetCheckpointBlobName`.

## Using an alternative blob layout

By default, WAL blobs are named `wal/<journalId>` and checkpoint blobs are named `checkpoints/<journalId>/<snapshotId>`. The separate prefixes let catalog discovery select WAL blobs directly, keeping retained checkpoints out of listing pages. Configure the blob name delegates to use an alternative layout, such as a shared prefix, file extensions, tenant-specific paths, or names which match an existing storage convention. Each delegate returns a container-relative blob name, and checkpoint names should include the supplied snapshot id to avoid collisions. Catalog discovery uses the default WAL layout in the configured container; custom delegates participating in discovery produce `wal/<journalId>` for each journal.

```csharp
siloBuilder.AddAzureBlobJournalStorage(options =>
{
    options.GetWalBlobName = static journalId => $"journals/{journalId.Value}.wal";
    options.GetCheckpointBlobName = static (journalId, snapshotId) => $"journals/{journalId.Value}.{snapshotId}.chk";
});
```

## Azure Table journal storage

The package also provides an Azure Table implementation, `AddAzureTableJournalStorage`. Each journal is stored as one table partition: a header row carries the journal manifest and is the optimistic concurrency fence, and journal bytes are stored in data rows whose keys order the current generation by sequence. Every append commits its rows together with the header update in a single entity group transaction, and recovery replays the whole journal with a single partition range query, avoiding the per-block pacing which append blob replay is subject to. Because an append is limited by entity group transaction limits, a single journal batch must not exceed 2 MiB; replace operations may be any size.

```csharp
siloBuilder.AddAzureTableJournalStorage(options =>
{
    options.TableName = "journal";
    options.TableServiceClient = tableServiceClient;
});
```

`AzureTableJournalStorageOptions.GetPartitionKey` can be used to apply a custom partition layout. The returned key must be unique per journal, satisfy Azure Table partition-key restrictions, and contain at most 1,024 characters. The canonical journal id is stored in the header so catalog listing remains accurate with custom mappings.

## Catalog enumeration

Both Azure providers implement `IJournalStorageCatalog.ListAsync`, returning `JournalCatalogEntry` values incrementally in service traversal order. Each entry carries its journal identity in `Id`. `ListOptions.Prefix` is a raw ordinal string prefix and may end within a segment; use a trailing `/` when selecting only entries inside a namespace. `MinId` and `MaxId` are inclusive ordinal bounds, each unlimited by default. All constraints apply and are snapshotted when enumeration starts. Consumers requiring due order must sort selected ids using `StringComparer.Ordinal`. The provider handles service continuations internally and yields entries from the current page before fetching the next page.

Set `ListOptions.IncludeMetadata` to include each entry's format, ETag, and caller-owned properties in `Metadata`. The snapshot is observed together in the listing response and has the same semantics as `GetMetadataAsync`; its ETag can be supplied to `UpdateMetadataAsync` for a conditional update. Blob listing requests metadata traits, while Table listing selects the header's format and serialized caller metadata alongside the identity and timestamp. Both providers construct snapshots directly from listing pages. By default, enumeration projects identities with `Metadata` set to `null`. `IncludeMetadata` is snapshotted with the range options when enumeration starts.

The Blob catalog scans the configured `ContainerName` and interprets append blobs named `wal/<journalId>` as journal identities. This traversal applies equally when a custom naming delegate or container factory produces the same entries. Requests use `wal/` followed by the raw journal-id prefix, narrowed by the common prefix of `MinId` and `MaxId` when possible, and up to 5000 blobs per page. Checkpoints occupy their own namespace and are excluded before pagination, including for unbounded catalog queries. For an ASCII lower bound, `GetBlobsOptions.StartFrom` seeks to `wal/<MinId>`, preserving the minimum journal's WAL. Azure's lexical listing order permits stopping after `wal/<MaxId>` for an ASCII upper bound. Other bounds are enforced locally. Upper-bound termination happens while consuming results: the provider reads the page crossing the upper bound and then completes the traversal. Empty intersections issue no request. Recovery and checkpoint cleanup use the full checkpoint name published in WAL metadata.

Table enumeration requests up to 1000 header rows per service page and reads identities from the canonical `JournalId` header property. The default partition mapping accepts printable ASCII journal ids (`0x20` through `0x7E`) and encodes each byte as two uppercase hexadecimal digits, preserving ordinal ordering and raw prefixes, including partial segments. The 1,024-character partition-key limit therefore permits at most 512 characters per journal id. Validation occurs when creating storage. This restriction belongs to the default Table mapping; custom mappings and other providers retain their journal-id contracts. Default-mapping queries combine the header row key with indexed partition-key constraints for the raw prefix, inclusive `MinId`, and inclusive `MaxId`. Query bounds retain their original ordinal meaning, including bounds outside the stored ASCII alphabet. An empty intersection issues no query. Custom mappings apply well-formed Unicode bounds to the canonical `JournalId` property; bounds containing unpaired surrogates are enforced locally. This preserves ordinal range semantics and can require a full table scan. Unbounded enumeration also scans headers across the table. Constraints are checked again before yielding, and one enumerator advance can cross multiple empty or filtered pages.

Use `await foreach` or dispose a retained enumerator when stopping early. Pass a cancellation token covering the traversal lifetime; cancellation and service errors propagate to the caller.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling.AzureStorage
```

## Example - Configuring Azure Storage Journaling

The journaling provider resolves a registered `BlobServiceClient` from DI. How you obtain that client depends on your hosting model.

### Authentication options

For production workloads, prefer Microsoft Entra (Azure AD) credentials with `DefaultAzureCredential` rather than long-lived connection strings:

```csharp
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddSingleton(_ =>
    new BlobServiceClient(
        new Uri("https://<your-account>.blob.core.windows.net"),
        new DefaultAzureCredential()));
```

If you are integrating with .NET Aspire (as the bundled `JournalingAzureBlobJson` sample does), the AppHost emits a connection string that the consuming project resolves via `AddAzureBlobServiceClient`. Aspire wires up local emulator credentials in development and Entra-backed credentials in production.

For ad-hoc local development you may register a `BlobServiceClient` from a connection string (such as the Azurite UseDevelopmentStorage shortcut). Do not embed production connection strings in source.

### Wiring it into the silo

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Configuration;
using Orleans.Journaling.Json;
using System.Text.Json.Serialization;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MyGrainNamespace;

[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ulong))]
internal partial class JournalJsonContext : JsonSerializerContext;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Azure Storage as a journaling provider
            .AddAzureBlobJournalStorage(optionsBuilder =>
            {
                optionsBuilder.Configure((options, serviceProvider) => options.BlobServiceClient = serviceProvider.GetRequiredService<BlobServiceClient>());
            })
            // JSON Lines is the default journaling format. Register metadata for all journaled payload types.
            .UseJsonJournalFormat(JournalJsonContext.Default);
    });

var host = await builder.StartAsync();

// Get a reference to the grain
var shoppingCart = host.Services.GetRequiredService<IGrainFactory>()
    .GetGrain<IShoppingCartGrain>("user1-cart");

// Use the grain
await shoppingCart.UpdateItem("apple", 5, 0);
await shoppingCart.UpdateItem("banana", 3, 1);

// Get and print the cart contents
var (contents, version) = await shoppingCart.GetCart();
Console.WriteLine($"Shopping cart (version {version}):");
foreach (var item in contents)
{
    Console.WriteLine($"- {item.Key}: {item.Value}");
}

// Wait for the application to terminate
await host.WaitForShutdownAsync();
```

## Example - Using Journaling in a Grain
```csharp
using Orleans.Runtime;

namespace MyGrainNamespace;

public interface IShoppingCartGrain : IGrain
{
    ValueTask<(bool success, long version)> UpdateItem(string itemId, int quantity, long version);
    ValueTask<(Dictionary<string, int> Contents, long Version)> GetCart();
    ValueTask<long> GetVersion();
    ValueTask<(bool success, long version)> Clear(long version);
}

public class ShoppingCartGrain(
    [FromKeyedServices("shopping-cart")] IDurableDictionary cart,
    [FromKeyedServices("version")] IDurableValue<long> version) : DurableGrain, IShoppingCartGrain
{
    private readonly IDurableValue<long> _version = version;

    public async ValueTask<(bool success, long version)> UpdateItem(string itemId, int quantity, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (quantity == 0)
        {
            cart.Remove(itemId);
        }
        else
        {
            cart[itemId] = quantity;
        }

        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }

    public ValueTask<(Dictionary<string, int> Contents, long Version)> GetCart() => new((cart.ToDictionary(), _version.Value));
    public ValueTask<long> GetVersion() => new(_version.Value);

    public async ValueTask<(bool success, long version)> Clear(long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        cart.Clear();
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Grain Persistence](https://dotnet.github.io/orleans/docs/grains/grain-persistence/)
- [Azure Blob Storage Documentation](https://learn.microsoft.com/azure/storage/blobs/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
