# Microsoft Orleans Streaming for Azure Storage Queues

## Introduction
Microsoft Orleans Streaming for Azure Storage provides a stream provider implementation for Orleans using Azure Storage Queues. This allows for publishing and subscribing to streams of events with Azure Storage Queues as the underlying messaging infrastructure.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Streaming.AzureStorage
```

## Example - Configuring Azure Storage Queues Streaming
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Streams;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Azure Storage Queues as a stream provider
            .AddAzureQueueStreams(
                name: "AzureQueueStreamProvider", 
                b => b.ConfigureAzureQueue(ob => ob.Configure((options, dep) =>
                {
                    options.ConfigureTestDefaults();
                    options.QueueNames = Enumerable.Range(0, 8).Select(num => $"{dep.Value.ClusterId}-{num}").ToList();
                })));
    });

// Run the host
await builder.RunAsync();
```

## Example - Using Azure Storage Queue Streams in a Grain
```csharp
// Producer grain
public class ProducerGrain : Grain, IProducerGrain
{
    private IAsyncStream<string> _stream;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Get a reference to a stream
        var streamProvider = GetStreamProvider("AzureQueueStreamProvider");
        _stream = streamProvider.GetStream<string>(Guid.NewGuid(), "MyStreamNamespace");
        
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task SendMessage(string message)
    {
        // Send a message to the stream
        await _stream.OnNextAsync(message);
    }
}

// Consumer grain
public class ConsumerGrain : Grain, IConsumerGrain, IAsyncObserver<string>
{
    private StreamSubscriptionHandle<string> _subscription;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Get a reference to a stream
        var streamProvider = GetStreamProvider("AzureQueueStreamProvider");
        var stream = streamProvider.GetStream<string>(this.GetPrimaryKey(), "MyStreamNamespace");
        
        // Subscribe to the stream
        _subscription = await stream.SubscribeAsync(this);
        
        await base.OnActivateAsync(cancellationToken);
    }

    public Task OnNextAsync(string item, StreamSequenceToken token = null)
    {
        Console.WriteLine($"Received message: {item}");
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync()
    {
        Console.WriteLine("Stream completed");
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        Console.WriteLine($"Stream error: {ex.Message}");
        return Task.CompletedTask;
    }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Streams](https://learn.microsoft.com/en-us/dotnet/orleans/streaming/index)
- [Stream Providers](https://learn.microsoft.com/en-us/dotnet/orleans/streaming/stream-providers)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)