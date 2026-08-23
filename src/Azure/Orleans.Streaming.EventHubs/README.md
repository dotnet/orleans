# Microsoft Orleans Stream Provider for Azure Event Hubs

## Introduction
Microsoft Orleans Stream Provider for Azure Event Hubs enables Orleans applications to leverage Azure Event Hubs for reliable, scalable event processing. This provider allows you to use Event Hubs as a streaming backbone for your Orleans application to both produce and consume streams of events.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Streaming.EventHubs
```

## Example - Configuring Event Hubs Stream Provider

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
namespace ExampleGrains;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Azure Event Hubs as a stream provider
            .AddEventHubStreams(
                "EventHubStreamProvider",
                configurator => 
                {
                    configurator.ConfigureEventHub(builder => builder.Configure(options => 
                    {
                        options.ConnectionString = "YOUR_EVENT_HUB_CONNECTION_STRING";
                        options.ConsumerGroup = "YOUR_CONSUMER_GROUP"; // Default is "$Default"
                        options.Path = "YOUR_EVENT_HUB_NAME";
                    }));
                    
                    configurator.UseAzureTableCheckpointer(builder => builder.Configure(options => 
                    {
                        options.ConnectionString = "YOUR_STORAGE_CONNECTION_STRING";
                        options.TableName = "EventHubCheckpoints"; // Optional
                    }));
                });
    });

// Run the host
await builder.RunAsync();
```

### Using Orleans grain storage for checkpoints

Azure Table Storage remains the default checkpoint store for compatibility with existing deployments. As an alternative, checkpoints can be stored using Orleans grains and the configured `PubSubStore` grain storage provider:

```csharp
siloBuilder
    .AddMemoryGrainStorage("PubSubStore")
    .AddEventHubStreams("EventHubStreamProvider", configurator =>
    {
        configurator.ConfigureEventHub(builder => builder.Configure(options =>
        {
            options.ConnectionString = "YOUR_EVENT_HUB_CONNECTION_STRING";
            options.ConsumerGroup = "YOUR_CONSUMER_GROUP";
            options.Path = "YOUR_EVENT_HUB_NAME";
        }));
        configurator.UseGrainCheckpointer(builder => builder.Configure(options =>
        {
            options.PersistInterval = TimeSpan.FromSeconds(30);
        }));
    });
```

The grain checkpointer applies numeric ordering to Event Hubs offsets so that an older offset cannot overwrite a newer checkpoint. Configure a durable `PubSubStore` provider in production; in-memory grain storage does not preserve checkpoints across cluster restarts. Switching checkpoint stores does not migrate existing offsets and can cause events to be replayed.
Set `GrainStreamQueueCheckpointerOptions.StorageProviderName` to use another registered grain storage provider.

Both `UseGrainCheckpointer` and `UseAzureTableCheckpointer` extend `ISiloPersistentStreamConfigurator`, so they can also be used with other persistent stream providers. Event Hubs configures numeric checkpoint ordering by default; other providers can set the corresponding `CheckpointComparer` option for their checkpoint format.

### Recovering from an expired checkpoint

When Event Hubs rejects a persisted partition offset as invalid, the provider clears that partition's checkpoint, closes and recreates its receiver, and resumes on the next pull using the configured no-checkpoint position. `EventHubReceiverOptions.StartFromNow` selects that position. Its default value, `true`, starts at the partition tail and reads newly enqueued events, skipping the retained backlog. Set it to `false` to replay from the earliest retained event, which can duplicate delivery. Choose the policy according to the application's recovery requirements and keep consumers idempotent.

The built-in Azure Table and grain-backed checkpointers support this reset contract. A custom `IStreamQueueCheckpointer<TCheckpoint>` participates in recovery by implementing `Reset(CancellationToken)`; the default implementation surfaces unsupported recovery with `NotSupportedException`.

Existing Orleans subscription cursors rebind to the replacement partition cache, so subscriptions continue without application re-registration. Checkpoint reset, receiver shutdown, and receiver initialization observe the pulling agent's cancellation token.

## Example - Using Event Hub Streams in a Grain

```csharp
// Grain interface
public interface IStreamProcessingGrain : IGrainWithGuidKey
{
    Task StartProcessing();
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
        _streamProvider = GetStreamProvider("EventHubStreamProvider");
        
        // Get a reference to a specific stream
        _stream = _streamProvider.GetStream<MyEvent>(this.GetPrimaryKey(), "MyStreamNamespace");
        
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

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Orleans Streams](https://dotnet.github.io/orleans/docs/streaming/)
- [Event Hubs integration](https://dotnet.github.io/orleans/docs/implementation/streams-implementation/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)