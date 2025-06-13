# Microsoft Orleans Core Library

## Introduction
Microsoft Orleans Core is the primary library used by both client and server applications. It provides the runtime components necessary for Orleans applications, including serialization, communication, and the core hosting infrastructure.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Core
```

This package is automatically included when you reference the Orleans SDK or the Orleans client/server metapackages.

## Example - Configuring a Client

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using System;
using System.Threading.Tasks;

// Define a grain interface
namespace MyGrainNamespace;

public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string greeting);
}

// Implement the grain interface
public class HelloGrain : Grain, IHelloGrain
{
    public Task<string> SayHello(string greeting)
    {
        return Task.FromResult($"Hello! I got: {greeting}");
    }
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
var grain = host.Services.GetRequiredService<IClusterClient>().GetGrain<IHelloGrain>("grain-id");
var response = await grain.SayHello("Hello from client!");

// Print the result
Console.WriteLine($"Response: {response}");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Client Configuration](https://learn.microsoft.com/en-us/dotnet/orleans/host/client)
- [Dependency Injection](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/dependency-injection)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)