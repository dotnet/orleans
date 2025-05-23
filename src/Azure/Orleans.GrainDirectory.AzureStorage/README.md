# Microsoft Orleans Grain Directory for Azure Storage

## Introduction
Microsoft Orleans Grain Directory for Azure Storage provides a grain directory implementation using Azure Storage. The grain directory is used to locate active grain instances across the cluster, and this package allows Orleans to store that information in Azure Storage.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.GrainDirectory.AzureStorage
```

## Example - Configuring Azure Storage Grain Directory
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Azure Storage as grain directory
            .UseAzureStorageGrainDirectoryAsDefault(options =>
            {
                options.ConnectionString = "YOUR_AZURE_STORAGE_CONNECTION_STRING";
                options.TableName = "GrainDirectory";
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

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)