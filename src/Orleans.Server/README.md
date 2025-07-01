# Microsoft Orleans Server

## Introduction
Microsoft Orleans Server is a metapackage that includes all the necessary components to run an Orleans silo (server). It simplifies the process of setting up an Orleans server by providing a single package reference rather than requiring you to reference multiple packages individually.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Server
```

## Example - Creating an Orleans Silo Host

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

// Define a grain interface
namespace MyGrainNamespace;

public interface IMyGrain : IGrainWithStringKey
{
    Task<string> DoSomething();
}

// Implement the grain interface
public class MyGrain : Grain, IMyGrain
{
    public Task<string> DoSomething()
    {
        return Task.FromResult("Done something!");
    }
}


// Create the host
var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering();
    });

// Start the host
var host = builder.Build();
await host.StartAsync();

// Get a reference to a grain and call it
var client = host.Services.GetRequiredService<IClusterClient>();
var grain = client.GetGrain<IMyGrain>("my-grain-id");
var result = await grain.DoSomething();

// Print the result
Console.WriteLine($"Result: {result}");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans server (silo) configuration](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/server-configuration)
- [Hosting Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/host/generic-host)
- [Grain persistence](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)