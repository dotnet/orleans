# Microsoft Orleans Journaling for Azure Storage

## Introduction
Microsoft Orleans Journaling for Azure Storage provides an Azure Storage implementation of the Orleans Journaling provider. This allows logging and tracking of grain operations using Azure Storage as a backing store.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling.AzureStorage
```

## Example - Configuring Azure Storage Journaling
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Configuration;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MyGrainNamespace;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Azure Storage as a journaling provider
            .AddAzureAppendBlobStateMachineStorage(optionsBuilder =>
            {
                optionsBuilder.Configure((options, serviceProvider) => options.BlobServiceClient = serviceProvider.GetRequiredService<BlobServiceClient>());
            });
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