# Microsoft Orleans Streaming for Redis Streams

## Introduction

Microsoft Orleans Streaming for Redis Streams provides a stream provider implementation for Orleans using Redis Streams as the underlying messaging infrastructure. It enables publishing and subscribing to streams of events leveraging Redis Streams semantics (consumer groups, acks, and message reclaim).

## Getting Started

To use this package, install it via NuGet:

```sh
dotnet add package Microsoft.Orleans.Streaming.Redis

```

## Example - Configuring Redis Streams

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Redis Streams as a stream provider
            .AddRedisStreams(
                name: "RedisStreamProvider",
                configure: options =>
                {
                    options.ConfigureRedis(cfg =>
                    {
                        cfg.ConfigurationOptions = ConfigurationOptions.Parse("localhost:6379");
                    });
                });
    });

// Run the host
await builder.RunAsync();

```

## Example - Using Redis Streams in a Grain

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;

// Producer grain
public class ProducerGrain : Grain, IProducerGrain
{
    private IAsyncStream<string> _stream;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = GetStreamProvider("RedisStreamProvider");
        _stream = streamProvider.GetStream<string>(Guid.NewGuid(), "MyStreamNamespace");
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task SendMessage(string message)
    {
        await _stream.OnNextAsync(message);
    }
}

// Consumer grain
public class ConsumerGrain : Grain, IConsumerGrain, IAsyncObserver<string>
{
    private StreamSubscriptionHandle<string> _subscription;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = GetStreamProvider("RedisStreamProvider");
        var stream = streamProvider.GetStream<string>(this.GetPrimaryKey(), "MyStreamNamespace");
        _subscription = await stream.SubscribeAsync(this);
        await base.OnActivateAsync(cancellationToken);
    }

    public Task OnNextAsync(string item, StreamSequenceToken token = null)
    {
        Console.WriteLine($"Received message: {item}");
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex)
    {
        Console.WriteLine($"Stream error: {ex.Message}");
        return Task.CompletedTask;
    }
}

```

## Relevant Options

- `RedisStreamOptions.ConfigurationOptions`: StackExchange.Redis `ConfigurationOptions` used to connect to Redis.
- `RedisStreamReceiverOptions.ConsumerGroupName`: consumer group name (default: `orleans`).
- `RedisStreamReceiverOptions.ConsumerName`: consumer name (default: `pullingagent`).
- `RedisStreamReceiverOptions.DeliveredMessageIdleTimeout`: idle timeout used for AutoClaim behavior.

## Documentation

For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Streams](https://learn.microsoft.com/en-us/dotnet/orleans/streaming/index)
- [Stream Providers](https://learn.microsoft.com/en-us/dotnet/orleans/streaming/stream-providers)
- [Redis Streams Introduction](https://redis.io/docs/latest/develop/data-types/streams/)
- [StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/)

## Feedback & Contributing

- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
