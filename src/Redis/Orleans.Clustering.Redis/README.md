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

## Configuration via Microsoft.Extensions.Configuration

You can configure Orleans Redis clustering using `Microsoft.Extensions.Configuration` (such as `appsettings.json`) instead of configuring it in code. When using this approach, Orleans will automatically read the configuration from the `Orleans` section.

> **Note**: You can use either `"ProviderType": "Redis"` or `"ProviderType": "AzureRedisCache"` - both are supported and functionally equivalent.

### Example - appsettings.json (Silo)
```json
{
  "ConnectionStrings": {
    "redis": "localhost:6379"
  },
  "Orleans": {
    "ClusterId": "my-cluster",
    "ServiceId": "MyOrleansService",
    "Clustering": {
      "ProviderType": "Redis",
      "ServiceKey": "redis"
    }
  }
}
```

### Example - appsettings.json (Client)
```json
{
  "ConnectionStrings": {
    "redis": "localhost:6379"
  },
  "Orleans": {
    "ClusterId": "my-cluster",
    "ServiceId": "MyOrleansService", 
    "Clustering": {
      "ProviderType": "Redis",
      "ServiceKey": "redis"
    }
  }
}
```

### .NET Aspire Integration

For applications using .NET Aspire, consider using the [.NET Aspire Redis integration](https://learn.microsoft.com/en-us/dotnet/aspire/caching/stackexchange-redis-integration) which provides simplified Redis configuration, automatic service discovery, health checks, and telemetry. The Aspire integration automatically configures connection strings that Orleans can consume via the configuration system.

#### Example - Program.cs with Aspire Redis Integration
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (Aspire configurations)
builder.AddServiceDefaults();

// Add Redis via Aspire client integration
builder.AddKeyedRedisClient("redis");

// Add Orleans
builder.UseOrleans();

var host = builder.Build();
await host.StartAsync();

// Get a reference to a grain and call it
var client = host.Services.GetRequiredService<IClusterClient>();
var grain = client.GetGrain<IHelloGrain>("user123");
var response = await grain.SayHello("Aspire Redis");

Console.WriteLine($"Grain response: {response}");
await host.WaitForShutdownAsync();
```

This example assumes your AppHost project has configured Redis like this:
```csharp
// In your AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");

var orleans = builder.AddOrleans("orleans")
    .WithClustering(redis);

builder.AddProject<Projects.MyOrleansApp>("orleans-app")
    .WithReference(orleans);

builder.Build().Run();
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