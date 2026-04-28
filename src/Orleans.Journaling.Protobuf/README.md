# Microsoft Orleans Journaling for Protocol Buffers

## Introduction
Microsoft Orleans Journaling for Protocol Buffers provides a Google Protocol Buffers-based storage format for Orleans.Journaling durable state machines. Use this package to serialize durable dictionary, list, queue, set, value, state, and task completion source log entries directly using protobuf wire-format tags.

This package configures the physical log extent format and durable entry format used by Orleans.Journaling. Pair it with a Journaling storage provider such as Microsoft.Orleans.Journaling.AzureStorage. The storage provider remains independent of the serialization format: this package supplies the protobuf extent codec and durable-entry codec providers which durable state machines use to encode and recover their own operations.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling.Protobuf
```

## Example - Configuring Protocol Buffers journaling
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddAzureAppendBlobStateMachineStorage()
            .UseProtobufCodec();
    });

await builder.Build().RunAsync();
```

If you use a different Journaling storage provider, call `UseProtobufCodec()` after registering that provider.

## Example - Using protobuf payloads in durable state machines
```csharp
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

public interface IReminderGrain : IGrainWithStringKey
{
    ValueTask SetLastUpdated(Timestamp value);
    ValueTask<Timestamp?> GetLastUpdated();
}

public sealed class ReminderGrain(
    [FromKeyedServices("last-updated")] IDurableValue<Timestamp> lastUpdated)
    : DurableGrain, IReminderGrain
{
    public async ValueTask SetLastUpdated(Timestamp value)
    {
        lastUpdated.Value = value;
        await WriteStateAsync();
    }

    public ValueTask<Timestamp?> GetLastUpdated() => new(lastUpdated.Value);
}
```

`string` and `Google.Protobuf.IMessage` payloads use native protobuf payload encoding. Other payload types fall back to the configured `ILogDataCodec<T>` implementation.

## Storage format

`UseProtobufCodec()` stores physical log extents as a stream of length-delimited protobuf `LogExtent` messages. Each stored extent is prefixed by its protobuf varint length, so appended extents can be concatenated and recovered sequentially:

```text
[varint length][LogExtent bytes][varint length][LogExtent bytes]...
```

The logical schema is:

```proto
message LogExtent {
  repeated LogRecord records = 1;
}

message LogRecord {
  uint64 stream_id = 1;
  bytes entry = 2;
}
```

The `entry` bytes are the durable state machine command encoded by the durable-type protobuf codec.

The configured journaling codec must match the data already stored for a grain. This package does not automatically migrate existing binary, JSON, or older binary-framed protobuf journaling data to this protobuf extent format.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Journaling](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/event-sourcing)
- [Event Sourcing Grains](https://learn.microsoft.com/en-us/dotnet/orleans/grains/event-sourcing)
- [Create Protobuf messages for .NET apps](https://learn.microsoft.com/en-us/aspnet/core/grpc/protobuf)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
