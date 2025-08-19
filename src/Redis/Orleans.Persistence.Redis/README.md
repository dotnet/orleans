# Microsoft Orleans Persistence for Redis

## Introduction
Microsoft Orleans Persistence for Redis provides grain persistence for Microsoft Orleans using Redis. This allows your grains to persist their state in Redis and reload it when they are reactivated, leveraging Redis's in-memory data store for fast access.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Persistence.Redis
```

## Example - Configuring Redis Persistence
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Redis as grain storage
            .AddRedisGrainStorage(
                name: "redisStore",
                configureOptions: options =>
                {
                    options.ConnectionString = "localhost:6379";
                    options.Database = 0;
                    options.UseJson = true; // Serializes grain state as JSON
                    options.KeyPrefix = "grain-"; // Optional prefix for Redis keys
                });
    });

// Run the host
await builder.RunAsync();
```

## Example - Using Grain Storage in a Grain
```csharp
// Define grain state class

public class MyGrainState
{
    public string Data { get; set; }
    public int Version { get; set; }
}

// Grain implementation that uses Redis storage
public class MyGrain : Grain, IMyGrain, IGrainWithStringKey
{
    private readonly IPersistentState<MyGrainState> _state;

    public MyGrain([PersistentState("state", "redisStore")] IPersistentState<MyGrainState> state)
    {
        _state = state;
    }

    public async Task SetData(string data)
    {
        _state.State.Data = data;
        _state.State.Version++;
        await _state.WriteStateAsync();
    }

    public Task<string> GetData()
    {
        return Task.FromResult(_state.State.Data);
    }
}
```

## Configuration via Microsoft.Extensions.Configuration

You can configure Orleans Redis persistence using `Microsoft.Extensions.Configuration` (such as `appsettings.json`) instead of configuring it in code. When using this approach, Orleans will automatically read the configuration from the `Orleans` section.

> **Note**: You can use either `"ProviderType": "Redis"` or `"ProviderType": "AzureRedisCache"` - both are supported and functionally equivalent.

### Example - appsettings.json
```json
{
  "ConnectionStrings": {
    "redis": "localhost:6379"
  },
  "Orleans": {
    "ClusterId": "my-cluster",
    "ServiceId": "MyOrleansService",
    "GrainStorage": {
      "redisStore": {
        "ProviderType": "Redis",
        "ServiceKey": "redis"
      }
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
var grain = client.GetGrain<IMyGrain>("user123");
await grain.SetData("Hello from Aspire Redis!");
var data = await grain.GetData();

Console.WriteLine($"Grain data: {data}");
await host.WaitForShutdownAsync();
```

This example assumes your AppHost project has configured Redis like this:
```csharp
// In your AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");

var orleans = builder.AddOrleans("orleans")
    .WithGrainStorage("redisStore", redis);

builder.AddProject<Projects.MyOrleansApp>("orleans-app")
    .WithReference(orleans);

builder.Build().Run();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Grain Persistence](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence)
- [Redis Documentation](https://redis.io/documentation)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)