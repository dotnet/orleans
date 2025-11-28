# Microsoft Orleans Durable Jobs for Redis

## Introduction
Microsoft Orleans Durable Jobs for Redis provides persistent storage for Orleans Durable Jobs using Redis Streams. This allows your Orleans applications to schedule jobs that survive silo restarts, grain deactivation, and cluster reconfigurations. Jobs are stored in Redis Streams, providing efficient storage and retrieval for time-based job scheduling.

## Getting Started

### Installation
To use this package, install it via NuGet along with the core package:

```shell
dotnet add package Microsoft.Orleans.DurableJobs
dotnet add package Microsoft.Orleans.DurableJobs.Redis
```

### Configuration
Configure the Redis durable jobs provider in your silo configuration:

```csharp
siloBuilder.UseRedisDurableJobs(options =>
{
    options.CreateMultiplexer = async _ => await ConnectionMultiplexer.ConnectAsync("localhost:6379");
    options.ShardPrefix = "my-app"; // Optional: prefix for shard keys
});
```

### Configuration Options

| Option | Description | Default |
|--------|-------------|---------|
| `CreateMultiplexer` | Delegate to create the Redis connection multiplexer | Required |
| `ShardPrefix` | Prefix for shard identifiers in Redis | `"shard"` |
| `MaxShardCreationRetries` | Maximum retries when creating a shard | `5` |
| `MaxBatchSize` | Maximum operations per batch write | `128` |
| `MinBatchSize` | Minimum operations before flush | `1` |
| `BatchFlushInterval` | Time to wait for more operations | `100ms` |

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Durable Jobs Core Package](../../../Orleans.DurableJobs/README.md)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
