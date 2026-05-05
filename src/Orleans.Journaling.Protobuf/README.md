# Microsoft Orleans Journaling for Protocol Buffers

## Introduction
Microsoft Orleans Journaling for Protocol Buffers provides a Protocol Buffers-based storage format for Orleans.Journaling durable state machines. Use this package to serialize durable dictionary, list, queue, set, value, state, and task completion source log entries directly as protobuf records.

This package provides the physical log format and durable entry format used by Orleans.Journaling. Pair it with a Journaling storage provider such as Microsoft.Orleans.Journaling.AzureStorage. The storage provider remains independent of the serialization format: this package supplies the protobuf log format and protobuf durable-entry codec providers which durable state machines use to encode and recover their own operations.

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
using Orleans.Journaling;
using Orleans.Journaling.Protobuf;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddAzureAppendBlobLogStorage(options =>
            {
                options.LogFormatKey = ProtobufJournalingExtensions.LogFormatKey;
            })
            .UseProtobufJournalingFormat(options =>
            {
                options.AddMessageParser(StringValue.Parser);
            });
    });

await builder.Build().RunAsync();
```

If you use a different Journaling storage provider, configure it to use the `ProtobufJournalingExtensions.LogFormatKey` format key and call `UseProtobufJournalingFormat(...)` after registering that provider.

## Value payloads and Native AOT

Common scalar payload types are encoded natively without reflection: `string`, `byte[]`, `bool`, `int`, `uint`, `long`, `ulong`, `float`, and `double`.

Generated protobuf message types are encoded natively only when their generated parser is registered:

```csharp
siloBuilder
    .AddAzureAppendBlobLogStorage(options =>
    {
        options.LogFormatKey = ProtobufJournalingExtensions.LogFormatKey;
    })
    .UseProtobufJournalingFormat(options =>
    {
        options.AddMessageParser(MyJournaledMessage.Parser);
    });
```

Other value types can fall back to a configured `ILogValueCodec<T>` compatibility codec. The native protobuf path remains the recommended low-allocation path. If a protobuf message parser or fallback codec is missing, journaling fails with a configuration error instead of discovering parsers through reflection. This keeps the protobuf codec trimming and Native AOT friendly.

## Storage format

The protobuf journaling format stores physical log data as repeated length-delimited `LogEntry` messages with no surrounding extent envelope:

```protobuf
message LogEntry {
  uint64 stream_id = 1;
  bytes payload = 2;
}
```

Durable entry payloads use protobuf wire-format tags with fixed command ids and field numbers. Storage write batches append one or more complete length-delimited `LogEntry` messages, and recovery reads entries until the input is exhausted.

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
