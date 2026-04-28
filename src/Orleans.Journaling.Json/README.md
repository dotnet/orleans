# Microsoft Orleans Journaling for System.Text.Json

## Introduction
Microsoft Orleans Journaling for System.Text.Json provides a `System.Text.Json`-based codec for Orleans.Journaling durable state machines. Use this package to serialize durable dictionary, list, queue, set, value, state, and task completion source log entries directly as JSON.

This package configures the log entry format used by Orleans.Journaling. Pair it with a Journaling storage provider such as Microsoft.Orleans.Journaling.AzureStorage. The storage provider remains independent of the serialization format: this package supplies the JSON codec providers which durable state machines use to encode and recover their own operations.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling.Json
```

## Example - Configuring JSON journaling
```csharp
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddAzureAppendBlobStateMachineStorage()
            .UseJsonCodec(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });
    });

await builder.Build().RunAsync();
```

If you use a different Journaling storage provider, call `UseJsonCodec()` after registering that provider.

## Example - Using durable state machines
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

All durable state machine types use the configured JSON codec automatically. `JsonJournalingOptions` exposes the `JsonSerializerOptions` instance used for entry payloads. Journaling command and property names are fixed by the storage format, so serializer naming policies only affect user payload values.

The configured journaling codec must match the data already stored for a grain. This package does not automatically migrate existing binary or protobuf journaling data to JSON.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Journaling](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/event-sourcing)
- [Event Sourcing Grains](https://learn.microsoft.com/en-us/dotnet/orleans/grains/event-sourcing)
- [System.Text.Json Documentation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/overview)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
