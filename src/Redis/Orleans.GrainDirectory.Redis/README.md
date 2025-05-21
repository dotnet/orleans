# Microsoft Orleans Grain Directory for Redis

## Introduction
Microsoft Orleans Grain Directory for Redis provides a grain directory implementation using Redis. The grain directory is used to locate active grain instances across the cluster, and this package allows Orleans to store that information in Redis.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.GrainDirectory.Redis
```

## Example - Configuring Redis Grain Directory
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Redis as the grain directory
            .UseRedisGrainDirectoryAsDefault(options =>
            {
                options.ConnectionString = "localhost:6379";
                options.Database = 0;
            });
    });

// Run the host
await builder.RunAsync();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Configuration Guide](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/)
- [Implementation Details](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/index)
- [Redis Documentation](https://redis.io/documentation)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)