# Microsoft Orleans Clustering for Redis

## Introduction
Microsoft Orleans Clustering for Redis provides cluster membership functionality for Microsoft Orleans using Redis. This allows Orleans silos to coordinate and form a cluster using Redis as the backing store.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Clustering.Redis
```

## Example - Configuring Redis Membership
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = new HostBuilder()
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            // Configure Redis as the membership provider
            .UseRedisClustering(options =>
            {
                options.ConnectionString = "localhost:6379";
                options.Database = 0;
            });
    });

// Run the host
await builder.RunConsoleAsync();
```

## Example - Client Configuration
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var clientBuilder = new ClientBuilder()
    // Configure Redis as the gateway provider
    .UseRedisGatewayListProvider(options =>
    {
        options.ConnectionString = "localhost:6379";
        options.Database = 0;
    });

var client = clientBuilder.Build();
await client.Connect();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://docs.microsoft.com/dotnet/orleans/)
- [Configuration Guide](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/)
- [Orleans Clustering](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management)
- [Redis Documentation](https://redis.io/documentation)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)