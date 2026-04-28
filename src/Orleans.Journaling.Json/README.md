# Microsoft Orleans Journaling for System.Text.Json

## Introduction
Microsoft Orleans Journaling for System.Text.Json provides a JSON Lines-based storage format for Orleans.Journaling durable state machines. Use this package to serialize durable dictionary, list, queue, set, value, state, and task completion source log entries directly as JSON records.

This package configures the physical log extent format and durable entry format used by Orleans.Journaling. Pair it with a Journaling storage provider such as Microsoft.Orleans.Journaling.AzureStorage. The storage provider remains independent of the serialization format: this package supplies the JSON Lines extent codec and the JSON durable-entry codec providers which durable state machines use to encode and recover their own operations.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling.Json
```

## Example - Configuring JSON journaling
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

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
            .AddAzureAppendBlobStateMachineStorage()
            .UseJsonCodec(JournalJsonContext.Default);
    });

await builder.Build().RunAsync();
```

If you need to customize `JsonSerializerOptions`, add the generated context through `JsonJournalingOptions`:

```csharp
siloBuilder
    .AddAzureAppendBlobStateMachineStorage()
    .UseJsonCodec(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.AddTypeInfoResolver(JournalJsonContext.Default);
    });
```

If you use a different Journaling storage provider, call `UseJsonCodec(...)` after registering that provider.

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

For trimming and Native AOT, configure `SerializerOptions.TypeInfoResolver`, `SerializerOptions.TypeInfoResolverChain`, or `JsonJournalingOptions.AddTypeInfoResolver(...)` with source-generated metadata for every journaled key, value, and state type. The `UseJsonCodec(JournalJsonContext.Default)` overload is the recommended low-friction path. If metadata is unavailable, the JSON durable entry codecs fail with a configuration error instead of falling back to reflection-based serialization.

## Storage format

`UseJsonCodec()` stores log extents as JSON Lines (`.jsonl`): UTF-8 text, no byte order mark, and one JSON array per line. Each line is one physical log extent and is terminated by `\n`. Recovery accepts both LF and CRLF line endings.

Each extent line is an array of records. Each record contains the state machine id and the durable entry payload:

```json
[{"streamId":8,"entry":{"cmd":"set","key":"alpha","value":1}}]
```

The `entry` object is the durable state machine command. It is nested so storage metadata such as `streamId` cannot collide with durable command fields such as `cmd`, `key`, `value`, `items`, or `version`. Batching records into extent lines preserves the extent boundaries used by storage writes.

The configured journaling codec must match the data already stored for a grain. This package does not automatically migrate existing binary, protobuf, or older binary-framed JSON journaling data to JSON Lines.

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
