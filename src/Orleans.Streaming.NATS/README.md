# Microsoft Orleans Stream Provider for NATS

## Introduction
Microsoft Orleans Stream Provider for NATS enables Orleans applications to leverage NATS JetStream for reliable, scalable event processing. This provider allows you to use NATS JetStream as a streaming backbone for your Orleans application to both produce and consume streams of events.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Streaming.NATS
```

## Example - Configuring NATS Stream Provider

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Streaming.NATS.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure NATS JetStream as a stream provider
            .AddNatsStreams(
                "NatsStreamProvider",
                options =>
                {
                    options.StreamName = "orleans-stream";
                    // Optional: Configure NATS client options
                    // options.NatsClientOptions = new NatsOpts { Url = "nats://localhost:4222" };
                    // Optional: Configure batch size (default: 100)
                    // options.BatchSize = 100;
                    // Optional: Configure partition count (default: 8)
                    // options.PartitionCount = 8;
                });
    });

// Run the host
await builder.RunAsync();
```

## Example - Configuring NATS Streams on Client

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Streaming.NATS.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleansClient(clientBuilder =>
    {
        clientBuilder
            .UseLocalhostClustering()
            .AddNatsStreams(
                "NatsStreamProvider",
                options =>
                {
                    options.StreamName = "orleans-stream";
                });
    });

await builder.RunAsync();
```

## Example - Using NATS Streams in a Grain

```csharp
using Orleans;
using Orleans.Streams;

// Grain interface
public interface IStreamProcessingGrain : IGrainWithGuidKey
{
    Task StartProcessing();
    Task SendEvent(MyEvent evt);
}

// Grain implementation
public class StreamProcessingGrain : Grain, IStreamProcessingGrain
{
    private IStreamProvider _streamProvider;
    private IAsyncStream<MyEvent> _stream;
    private StreamSubscriptionHandle<MyEvent> _subscription;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Get the stream provider
        _streamProvider = this.GetStreamProvider("NatsStreamProvider");
        
        // Get a reference to a specific stream
        _stream = _streamProvider.GetStream<MyEvent>(
            StreamId.Create("MyStreamNamespace", this.GetPrimaryKey()));
        
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task StartProcessing()
    {
        // Subscribe to the stream to process events
        _subscription = await _stream.SubscribeAsync(OnNextAsync);
    }

    private Task OnNextAsync(MyEvent evt, StreamSequenceToken token)
    {
        Console.WriteLine($"Received event: {evt.Data}");
        return Task.CompletedTask;
    }

    // Produce an event to the stream
    public Task SendEvent(MyEvent evt)
    {
        return _stream.OnNextAsync(evt);
    }
}

// Event class
public class MyEvent
{
    public string Data { get; set; }
}
```

## Stream identities and upgrades

The provider routes each `StreamId` as two NATS subject tokens, one for the namespace and one for the key. Dots, wildcards, whitespace, control characters, and arbitrary binary bytes are encoded as `~` followed by uppercase hexadecimal bytes. Tokens beginning with `~` and the literal namespace `null` also use this encoding, keeping encoded values distinct from literal values and the empty namespace. Other UTF-8 tokens retain their existing subjects and partition assignments. JetStream applies the existing subject transform to select a partition, and the message payload preserves the original namespace and key bytes.

The consumer filters, durable consumer names, and JetStream stream configuration remain the same, so existing partitioned messages continue to be consumed. The provider reads the legacy `namespace/key` JSON identity and writes that format for identities which round-trip through it. Empty namespaces, namespaces containing `/`, and non-UTF-8 identities use a two-element array of base64 namespace and key bytes.

For an upgrade from the raw-subject format, pause producers and drain deliverable messages, upgrade all silos and clients using the provider, then resume publishing. This coordinates the new payload format and the changed partition assignments for escaped identities across publishers and consumers. Events retained on pre-upgrade unpartitioned subjects, such as those created by dotted namespaces, require replay through the upgraded provider to enter a consumer partition.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Orleans Streams](https://dotnet.github.io/orleans/docs/streaming/)
- [NATS JetStream Documentation](https://docs.nats.io/nats-concepts/jetstream)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
