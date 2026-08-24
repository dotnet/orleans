# Microsoft Orleans Streaming for ADO.NET

## Introduction
Microsoft Orleans Streaming for ADO.NET provides a partitioned stream provider for Orleans using ADO.NET-compatible databases (SQL Server, MySQL, PostgreSQL, etc.). This allows for publishing and subscribing to streams of events with relational databases as the underlying infrastructure.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Streaming.AdoNet
```

You will also need to install the appropriate ADO.NET provider for your database:

```shell
# For SQL Server
dotnet add package Microsoft.Data.SqlClient

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
                    options.Invariant = "Microsoft.Data.SqlClient";  // For SQL Server
                    options.ConnectionString = "Server=localhost;Database=OrleansStreaming;User ID=orleans;******;";
                });
    });

// Run the host
await builder.RunAsync();
```

The provider stores each queue as an immutable, ordered stream partition. Its stream partition pipeline appends records, reads partition history, and advances an ownership-fenced checkpoint. Configure it with:

- `StartFromNow`: initialize a new checkpoint at the current partition history tail instead of before the earliest retained record.
- `FaultOnDeliveryFailure`: optionally fault a failing subscription while preserving the shared partition records.
- `MaxMessagesPerRead`: bound each ordered storage read.
- `CheckpointPersistInterval`: throttle durable checkpoint updates.
- `RetentionPeriod`: retain checkpointed records for at least this period (one day by default). Fractional seconds round upward.
- `MaximumRetentionPeriod`: optionally delete older records even when they are not checkpointed. This is a hard capacity ceiling and can create a diagnosed retention gap. Fractional seconds round upward.
- `CleanupInterval` and `CleanupBatchSize`: bound cleanup frequency and work. Fractional cleanup intervals round upward.

The partitioned stream provider is rewindable while requested records remain retained. Inclusive subscription start positions remain pending until the corresponding record is delivered or intentionally filtered. The queue checkpoint advances through the earliest contiguous position which is safe for every subscription, including unrelated partition records which quiet-stream cursors have scanned. It resumes strictly after that durable, ownership-fenced checkpoint and can redeliver records after a crash without skipping uncheckpointed data.

Partition acquisition is cancellation-aware. A receiver whose acquisition command is still completing retains its queue reservation, so a late database result settles before a replacement receiver acquires a newer ownership epoch.

## Alpha schema upgrade

The current streaming scripts use schema version 2 and are intentionally incompatible with the former queue, visibility-timeout, confirmation, and dead-letter schema. The provider fails during initialization when it detects old or mixed streaming query keys.

There is no in-place migration for this alpha package. Stop producers and consumers, drop `OrleansStreamMessage`, `OrleansStreamDeadLetter`, `OrleansStreamControl`, `OrleansStreamMessageSequence`, the old streaming routines, and their `OrleansQuery` rows. Drop `OrleansStreamPartition` too after a partial version 2 installation. Then apply the current SQL Server, PostgreSQL, or MySQL streaming script. Existing alpha rows are not read or silently converted, so export payloads first if they must be retained.

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
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Orleans Streams](https://dotnet.github.io/orleans/docs/streaming/)
- [Stream Providers](https://dotnet.github.io/orleans/docs/streaming/stream-providers/)
- [ADO.NET Database Setup](https://dotnet.github.io/orleans/docs/host/configuration-guide/adonet-configuration/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
