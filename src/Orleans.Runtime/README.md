# Microsoft Orleans Runtime

## Introduction
Microsoft Orleans Runtime is the core server-side component of Orleans. It hosts and executes grains, manages grain lifecycles, and provides all the runtime services necessary for a functioning Orleans server (silo).

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Runtime
```

This package is automatically included when you reference the Orleans Server metapackage.

## Example - Configuring a Silo

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = new HostBuilder()
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "my-cluster";
                options.ServiceId = "MyOrleansService";
            })
            .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(MyGrain).Assembly).WithCodeGeneration());
    });

var host = builder.Build();
await host.StartAsync();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://docs.microsoft.com/dotnet/orleans/)
- [Server configuration](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/server-configuration)
- [Silo lifecycle](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/silo-lifecycle)
- [Clustering](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)