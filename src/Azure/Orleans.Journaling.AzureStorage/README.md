# Microsoft Orleans Journaling for Azure Storage

## Introduction
Microsoft Orleans Journaling for Azure Storage provides an Azure Storage implementation of the Orleans Journaling provider. This allows journaling and tracking of grain operations using Azure Storage as a backing store.

Blob names are derived from the configured grain storage identity and do not use journal format file extensions. Azure append blobs store the journal format key in blob metadata and, when the selected journal format provides a MIME type, are created with that content type.

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
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Journaling](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/event-sourcing)
- [Event Sourcing Grains](https://learn.microsoft.com/en-us/dotnet/orleans/grains/event-sourcing)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
