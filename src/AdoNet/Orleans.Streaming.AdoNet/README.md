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

The provider stores each queue as an immutable, ordered stream partition. Its stream partition pipeline appends records, reads partition history, and advances an ownership-fenced checkpoint. The default provider configuration creates one queue partition; calling `ConfigurePartitioning()` without an argument selects the method's public default of eight.

| Option | Default | Effect |
|---|---:|---|
| `StartFromNow` | `false` | A new partition starts before its earliest retained record. `true` starts a previously unseen partition at the current tail. |
| `FaultOnDeliveryFailure` | `false` | Optionally faults one failing subscription while preserving the shared partition record. |
| `MaxMessagesPerRead` | `1000` | Bounds one ordered live storage read. |
| `CheckpointPersistInterval` | `5 seconds` | Throttles durable checkpoint writes. |
| `RetentionPeriod` | `1 day` | Retains checkpointed records for at least this age. Fractional seconds round upward. |
| `MaximumRetentionPeriod` | unset | Optional hard age ceiling which can delete unread or replay-protected records and create a diagnosed retention gap. |
| `CleanupInterval` | `1 minute` | Controls the minimum interval between cleanup attempts. |
| `CleanupBatchSize` | `1000` | Bounds the contiguous eligible prefix deleted by one cleanup operation. |
| `ReplayLeaseDuration` | `1 minute` | Protects an active historical reader's safe retained watermark across silos. |
| `ReplayLeaseRenewalInterval` | `20 seconds` | Renews the lease before its TTL; it must remain shorter than `ReplayLeaseDuration`. |
| `InitializationTimeout` | `30 seconds` | Bounds database query-catalog initialization and the provider's shared initialization lock. |

`ConfigureCache` defaults the live receiver cache to 4,096 records. `ConfigureReplay` defaults each queue to four active historical readers, 32 normally pending cursors, 4,096 raw records per replay fragment, 256 records per read, and a 200 ms temporary-tail retry delay. Plan memory from these record counts, encoded record sizes, and pooled-buffer overhead. Reader disposal can temporarily admit one bounded replacement waiter per disposing reader so admission can progress during capacity turnover.

The partitioned stream provider resumes strictly after the durable, ownership-fenced queue checkpoint and can redeliver records after a crash without skipping uncheckpointed data. The checkpoint advances through the earliest contiguous position which is safe for every live subscription, including unrelated partition records which quiet-stream cursors have scanned.

Explicit subscriptions can start or resume from retained ADO.NET tokens after their records leave the live cache. Admission locks the partition, validates the retained lower bound, and creates a provider-visible replay lease in one database transaction. Cleanup respects the minimum active replay watermark across silos. Consumer-safe partition progress alone advances that watermark. A receiver shutdown leaves the lease active until TTL expiry so a new queue owner can reconstruct the reader with continuous cleanup protection.

The receiver delivers historical records in partition order, pins the live-cache boundary, and attaches the subscription to live delivery before releasing replay state. Caller-supplied start tokens are inclusive and internally acknowledged delivery tokens resume after their record. Delivery remains at least once. `MaximumRetentionPeriod` remains a hard capacity ceiling and terminates an affected live reader or replay with a diagnosed `DataNotAvailableException`.

A full replay admission queue throws `InvalidOperationException`. Temporary database failures during replay admission, reading, and lease renewal surface as `TransientStreamReplayException`; the pulling agent retries from its last safe partition token. Normal cursor disposal releases its lease. Receiver shutdown leaves the lease active until TTL expiry so a replacement queue owner can reconstruct protection before cleanup proceeds.

Partition acquisition is cancellation-aware. A receiver whose acquisition command is still completing retains its queue reservation, so a late database result settles before a replacement receiver acquires a newer ownership epoch.

Message creation and checkpoint-eligibility timestamps are sampled after the partition lock is acquired. Lock contention therefore does not consume the configured retention window.

## Alpha schema upgrade

The current streaming scripts use schema version 3 and include fenced replay leases. The provider fails during initialization when it detects old or mixed streaming query keys.

Alpha schema upgrades replace the streaming schema as one unit. Stop producers and consumers, drop `OrleansStreamMessage`, `OrleansStreamReplayLease`, `OrleansStreamPartition`, former dead-letter/control/sequence objects, the streaming routines, and their `OrleansQuery` rows. Then apply the current SQL Server, PostgreSQL, or MySQL streaming script. Export required payloads before replacing the tables.

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
- [ADO.NET partitioned streams](https://dotnet.github.io/orleans/docs/streaming/adonet-streaming/)
- [Retained-history replay](https://dotnet.github.io/orleans/docs/streaming/retained-history-replay/)
- [ADO.NET Database Setup](https://dotnet.github.io/orleans/docs/host/configuration-guide/adonet-configuration/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
