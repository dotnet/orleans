# Microsoft Orleans SDK

## Introduction
Microsoft Orleans SDK is a metapackage that includes all the necessary components to build Orleans applications. It provides both client and server functionality, making it easy to get started with Orleans without having to reference individual packages.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Sdk
```

## Example - Creating a Grain Interface and Implementation

```csharp
// Define a grain interface
public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string greeting);
}

// Implement the grain interface
public class HelloGrain : Grain, IHelloGrain
{
    public Task<string> SayHello(string greeting)
    {
        return Task.FromResult($"Hello, {greeting}!");
    }
}
```

## Example - Configuring an Orleans Application

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
namespace ExampleGrains;

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
var grain = client.GetGrain<IHelloGrain>("user123");
var response = await grain.SayHello("World");

// Print the result
Console.WriteLine($"Grain response: {response}");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Getting started with Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/tutorials-and-samples/tutorial-1)
- [Grains](https://learn.microsoft.com/en-us/dotnet/orleans/grains/)
- [Hosting Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/host/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)