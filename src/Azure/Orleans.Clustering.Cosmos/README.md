# Microsoft Orleans Clustering for Azure Cosmos DB

## Introduction
Microsoft Orleans Clustering for Azure Cosmos DB provides cluster membership functionality for Microsoft Orleans using Azure Cosmos DB. This allows Orleans silos to coordinate and form a cluster using Azure Cosmos DB as the backing store.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Clustering.Cosmos
```

## Example - Configuring Azure Cosmos DB Membership
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseCosmosClustering(options =>
            {
                options.AccountEndpoint = "https://YOUR_COSMOS_ENDPOINT";
                options.AccountKey = "YOUR_COSMOS_KEY";
                options.DB = "YOUR_DATABASE_NAME";
                options.CanCreateResources = true;
            });
    });

// Run the host
await builder.RunAsync();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Configuration Guide](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/)
- [Orleans Clustering](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)