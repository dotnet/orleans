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
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

var builder = Host.CreateApplicationBuilder(args)
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

var host = builder.Build();
await host.StartAsync();

// Get a reference to a grain and call it
var client = host.Services.GetRequiredService<IClusterClient>();
var grain = client.GetGrain<IHelloGrain>("user123");
var response = await grain.SayHello("Redis");

// Print the result
Console.WriteLine($"Grain response: {response}");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

## Example - Client Configuration
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

var clientBuilder = Host.CreateApplicationBuilder(args)
    .UseOrleansClient(builder =>
    {
        builder
            // Configure Redis as the gateway provider
            .UseRedisGatewayListProvider(options =>
            {
                options.ConnectionString = "localhost:6379";
                options.Database = 0;
            });
    });

var host = clientBuilder.Build();
await host.StartAsync();
var client = host.Services.GetRequiredService<IClusterClient>();

// Get a reference to a grain and call it
var grain = client.GetGrain<IHelloGrain>("user123");
var response = await grain.SayHello("Redis Client");

// Print the result
Console.WriteLine($"Grain response: {response}");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Configuration Guide](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/)
- [Orleans Clustering](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management)
- [Redis Documentation](https://redis.io/documentation)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)