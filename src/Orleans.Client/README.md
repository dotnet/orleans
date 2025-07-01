# Microsoft Orleans Client

## Introduction
Microsoft Orleans Client is a metapackage that includes all the necessary components to connect to an Orleans cluster from a client application. It provides a simplified way to set up an Orleans client by providing a single package reference rather than requiring you to reference multiple packages individually.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Client
```

## Example - Creating an Orleans Client

```csharp
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
namespace ExampleGrains;

// Define a grain interface
public interface IMyGrain : IGrainWithStringKey
{
    Task<string> DoSomething();
}


// Create a client
var builder = Host.CreateApplicationBuilder(args)
    .UseOrleansClient(client =>
    {
        client.UseLocalhostClustering();
    });

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
- [Orleans client configuration](https://learn.microsoft.com/en-us/dotnet/orleans/host/client)
- [Grain references](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-references)
- [Orleans request context](https://learn.microsoft.com/en-us/dotnet/orleans/grains/request-context)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)