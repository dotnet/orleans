# Microsoft Orleans Clustering Provider for Azure Storage

## Introduction
Microsoft Orleans Clustering Provider for Azure Storage allows Orleans silos to organize themselves as a cluster using Azure Table Storage. This provider enables silos to discover each other, maintain cluster membership, and detect and handle failures.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Clustering.AzureStorage
```

## Example - Configuring Azure Storage Clustering

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = new HostBuilder()
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            // Configure Azure Table Storage for clustering
            .UseAzureStorageClustering(options =>
            {
                options.ConnectionString = "YOUR_AZURE_STORAGE_CONNECTION_STRING";
                options.TableName = "OrleansClustering"; // Optional: defaults to "OrleansClustering"
            })
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "my-cluster";
                options.ServiceId = "MyOrleansService";
            });
    });

// Run the host
await builder.RunConsoleAsync();
```

## Example - Configuring Client to Connect to Cluster

```csharp
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;

var clientBuilder = new HostBuilder()
    .UseOrleansClient(clientBuilder =>
    {
        clientBuilder
            // Configure the client to use Azure Storage for clustering
            .UseAzureStorageClustering(options =>
            {
                options.ConnectionString = "YOUR_AZURE_STORAGE_CONNECTION_STRING";
                options.TableName = "OrleansClustering"; // Optional: defaults to "OrleansClustering"
            })
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "my-cluster";
                options.ServiceId = "MyOrleansService";
            });
    });

var host = await clientBuilder.StartAsync();
var client = host.Services.GetRequiredService<IClusterClient>();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://docs.microsoft.com/dotnet/orleans/)
- [Clustering providers](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management)
- [Azure Storage provider](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/azure-storage-providers)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)