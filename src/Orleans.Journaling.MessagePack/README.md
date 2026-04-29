# Microsoft Orleans Journaling for MessagePack

## Introduction

Microsoft Orleans Journaling for MessagePack provides a MessagePack-based storage format for Orleans.Journaling durable state machines. Use this package to serialize durable dictionary, list, queue, set, value, state, and task completion source log entries as MessagePack records.

This package configures the physical log format and durable entry format used by Orleans.Journaling. Pair it with a Journaling storage provider such as Microsoft.Orleans.Journaling.AzureStorage. The storage provider remains independent of the serialization format: this package supplies the MessagePack log format and MessagePack durable-entry codec providers which durable state machines use to encode and recover their own operations.

## Getting Started

To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling.MessagePack
```

## Example - Configuring MessagePack journaling

```csharp
using MessagePack;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddAzureAppendBlobStateMachineStorage()
            .UseMessagePackCodec(options =>
            {
                options.SerializerOptions = MessagePackSerializerOptions.Standard;
            });
    });

await builder.Build().RunAsync();
```

If you use a different Journaling storage provider, call `UseMessagePackCodec(...)` after registering that provider.

## Value payloads and Native AOT

MessagePack durable entry codecs use the configured `MessagePackSerializerOptions` for user values. For trimming and Native AOT, configure options with resolvers that know every journaled key, value, and state type. Missing or unsupported value metadata surfaces as a configuration or serialization error instead of silently switching storage formats.

## Storage format

`UseMessagePackCodec()` stores physical log data as repeated standalone MessagePack entry arrays with no surrounding extent envelope:

```text
entry := [streamId, payload]
```

`streamId` is the durable state machine id. `payload` is a MessagePack binary value containing the durable operation payload for that state machine. Storage write batches append one or more complete entry arrays; recovery reads arrays until the input is exhausted.

The configured journaling codec must match the data already stored for a grain. This package does not automatically migrate existing binary, JSON Lines, protobuf, or older MessagePack journaling data to the current MessagePack format.

## Documentation

For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Journaling](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/event-sourcing)
- [Event Sourcing Grains](https://learn.microsoft.com/en-us/dotnet/orleans/grains/event-sourcing)
- [MessagePack for C#](https://github.com/MessagePack-CSharp/MessagePack-CSharp)

## Feedback & Contributing

- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)