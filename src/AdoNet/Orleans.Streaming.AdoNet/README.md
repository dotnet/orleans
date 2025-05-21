# Microsoft Orleans Streaming for ADO.NET

## Introduction
Microsoft Orleans Streaming for ADO.NET provides a stream provider implementation for Orleans using ADO.NET-compatible databases (SQL Server, MySQL, PostgreSQL, etc.). This allows for publishing and subscribing to streams of events with relational databases as the underlying infrastructure.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Streaming.AdoNet
```

You will also need to install the appropriate ADO.NET provider for your database:

```shell
# For SQL Server
dotnet add package System.Data.SqlClient

# For MySQL
dotnet add package MySql.Data

# For PostgreSQL
dotnet add package Npgsql
```

## Example - Configuring ADO.NET Streaming
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Streams;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure ADO.NET as a stream provider
            .AddAdoNetStreams(
                name: "AdoNetStreamProvider",
                configureOptions: options =>
                {
                    options.Invariant = "System.Data.SqlClient";  // For SQL Server
                    options.ConnectionString = "Server=localhost;Database=OrleansStreaming;User ID=orleans;******;";
                });
    });

// Run the host
await builder.RunAsync();
```

## Example - Using ADO.NET Streams in a Grain
```csharp
// Producer grain
public class ProducerGrain : Grain, IProducerGrain
{
    private IAsyncStream<string> _stream;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Get a reference to a stream
        var streamProvider = GetStreamProvider("AdoNetStreamProvider");
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
        var streamProvider = GetStreamProvider("AdoNetStreamProvider");
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
- [ADO.NET Database Setup](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/adonet-configuration)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)