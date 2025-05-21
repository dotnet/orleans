# Microsoft Orleans Hosting for Azure Cloud Services

## Introduction
Microsoft Orleans Hosting for Azure Cloud Services provides support for hosting Orleans silos in Azure Cloud Services. This package integrates Orleans with the Azure Cloud Services lifecycle, allowing your silos to properly start, stop, and take advantage of Azure Cloud Services features.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Hosting.AzureCloudServices
```

## Example - Configuring Orleans with Azure Cloud Services
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

// In your CloudService WorkerRole entry point
public class WorkerRole : RoleEntryPoint
{
    private ISiloHost _silo;

    public override bool OnStart()
    {
        // Create the silo host
        _silo = Host.CreateApplicationBuilder(args)
            .UseOrleans(builder =>
            {
                // Configure Orleans for Azure Cloud Services
                builder.UseAzureStorageClustering(options =>
                {
                    options.ConnectionString = "YOUR_AZURE_STORAGE_CONNECTION_STRING";
                });

                // Add other Orleans configurations as needed
            })
            .Build();

        // Start the silo
        _silo.StartAsync().GetAwaiter().GetResult();
        
        return base.OnStart();
    }

    public override void OnStop()
    {
        // Properly shutdown the silo
        _silo.StopAsync().GetAwaiter().GetResult();
        
        base.OnStop();
    }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Hosting in Azure](https://learn.microsoft.com/en-us/dotnet/orleans/host/azure-cloud-services)
- [Silo Configuration](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/typical-configurations)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)