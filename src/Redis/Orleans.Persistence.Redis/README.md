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