# Microsoft Orleans Journaling for Protocol Buffers

## Introduction
Microsoft Orleans Journaling for Protocol Buffers provides a Protocol Buffers-based storage format for Orleans.Journaling durable state machines. Use this package to serialize durable dictionary, list, queue, set, value, state, and task completion source log entries directly as protobuf records.

This package configures the physical log extent format and durable entry format used by Orleans.Journaling. Pair it with a Journaling storage provider such as Microsoft.Orleans.Journaling.AzureStorage. The storage provider remains independent of the serialization format: this package supplies the protobuf extent codec and protobuf durable-entry codec providers which durable state machines use to encode and recover their own operations.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling.Protobuf
```

## Example - Configuring protobuf journaling
```csharp
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddAzureAppendBlobStateMachineStorage()
            .UseProtobufCodec(options =>
            {
                options.AddMessageParser(StringValue.Parser);
            });
    });

await builder.Build().RunAsync();
```

If you use a different Journaling storage provider, call `UseProtobufCodec(...)` after registering that provider.

## Value payloads and Native AOT

Common scalar payload types are encoded natively without reflection: `string`, `byte[]`, `bool`, `int`, `uint`, `long`, `ulong`, `float`, and `double`.

Generated protobuf message types are encoded natively only when their generated parser is registered:

```csharp
siloBuilder
    .AddAzureAppendBlobStateMachineStorage()
    .UseProtobufCodec(options =>
    {
        options.AddMessageParser(MyJournaledMessage.Parser);
    });
```

Other value types fall back to the configured `ILogDataCodec<T>`. If a protobuf message parser or fallback codec is missing, journaling fails with a configuration error instead of discovering parsers through reflection. This keeps the protobuf codec trimming and Native AOT friendly.

## Storage format

`UseProtobufCodec()` stores physical log extents as a stream of length-delimited `LogExtent` messages. Each extent contains repeated records with the state machine id and the durable entry payload. Durable entry payloads use protobuf wire-format tags with fixed command ids and field numbers.

The configured journaling codec must match the data already stored for a grain. This package does not automatically migrate existing binary, JSON Lines, or older protobuf journaling data to the current protobuf format.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Journaling](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/event-sourcing)
- [Event Sourcing Grains](https://learn.microsoft.com/en-us/dotnet/orleans/grains/event-sourcing)
- [Protocol Buffers Documentation](https://protobuf.dev/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
